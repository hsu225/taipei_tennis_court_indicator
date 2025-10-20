using System.Collections.Concurrent;
using System.Text.Json;
using CourtFinder.Core.Models;
using Microsoft.Playwright;

namespace CourtFinder.Core.Providers;

public class TaipeiPlaywrightProvider : ITennisCourtProvider, IAsyncDisposable
{
    private readonly TimeSpan _cacheTtl;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private bool _disposed;

    private static readonly ConcurrentDictionary<string, (DateTimeOffset expires, Availability avail)> AvailCache = new();
    private static readonly ConcurrentDictionary<string, (DateTimeOffset expires, VenueInfo info)> InfoCache = new();

    public TaipeiPlaywrightProvider()
    {
        var ttlEnv = Environment.GetEnvironmentVariable("COURTFINDER_CACHE_SECONDS");
        if (!int.TryParse(ttlEnv, out var ttlSecs) || ttlSecs <= 0) ttlSecs = 300;
        _cacheTtl = TimeSpan.FromSeconds(ttlSecs);
    }

    private static bool IsHeadless()
    {
        var v = Environment.GetEnvironmentVariable("COURTFINDER_HEADLESS")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(v)) return true; // default: headless
        return !(v == "false" || v == "0" || v == "no");
    }

    private static int GetEnvInt(string name, int def)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return int.TryParse(v, out var i) ? i : def;
    }

    public Task<IReadOnlyList<Court>> GetCourtsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Court>>(Array.Empty<Court>());

    public async Task<Availability?> GetAvailabilityAsync(string courtId, DateOnly date, CancellationToken ct = default)
    {
        var cacheKeyAvail = $"avail:{courtId}:{date:yyyy-MM-dd}";
        if (TryGetAvailFromCache(cacheKeyAvail, out var cached)) return cached;

        Console.WriteLine($"Fetching availability for K={courtId}, date={date}");

        try
        {
            // Create a fresh browser instance for each request to avoid reCAPTCHA tracking
            Console.WriteLine("Creating Playwright instance...");
            var playwright = await Playwright.CreateAsync();
            Console.WriteLine("Launching browser...");
            Console.WriteLine($"Headless: {IsHeadless()}  SlowMo(ms): {GetEnvInt("COURTFINDER_SLOWMO_MS", 0)}");

            // Try to launch browser, if it fails, try to install
            IBrowser? browser = null;
            try
            {
                browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = IsHeadless(),
                    SlowMo = GetEnvInt("COURTFINDER_SLOWMO_MS", 0),
                    Args = new[] { "--disable-blink-features=AutomationControlled" }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Browser launch failed: {ex.Message}");
                Console.WriteLine("Attempting to install Chromium... (this may take a while)");

                // Install browsers
                var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
                if (exitCode != 0)
                {
                    throw new Exception($"Failed to install Playwright browsers (exit code: {exitCode})");
                }

                Console.WriteLine("Chromium installed, retrying...");
                browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = IsHeadless(),
                    SlowMo = GetEnvInt("COURTFINDER_SLOWMO_MS", 0),
                    Args = new[] { "--disable-blink-features=AutomationControlled" }
                });
            }

            await using var _ = browser;

            Console.WriteLine("Creating browser context...");
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
            });

            Console.WriteLine("Creating new page...");
            var page = await context.NewPageAsync();

            // Enable console logging
            page.Console += (_, e) =>
            {
                if (e.Text.Contains("reCAPTCHA") || e.Text.Contains("mmDataPickup"))
                    Console.WriteLine($"[PAGE] {e.Text}");
            };

            var url = $"https://vbs.sports.taipei/venues/?K={Uri.EscapeDataString(courtId)}#Schedule";
            Console.WriteLine($"Loading {url}...");

            // Navigate and wait for initial load
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
            Console.WriteLine("Initial page loaded...");

            // Wait for either mmDataPickup OR reCAPTCHA redirect (whichever comes first)
            var startTime = DateTime.Now;
            var maxWaitTime = GetEnvInt("COURTFINDER_WAIT_MS", 90000); // total wait budget
            var checkInterval = 500; // Check every 500ms
            var elapsed = 0;
            var redirectDetected = false;

            while (elapsed < maxWaitTime)
            {
                // Check current URL
                var currentUrl = page.Url;

                // Check if we have mmDataPickup (success)
                var hasData = await page.EvaluateAsync<bool>("typeof mmDataPickup !== 'undefined'");
                if (hasData)
                {
                    Console.WriteLine($"mmDataPickup loaded after {(DateTime.Now - startTime).TotalSeconds:F1}s");
                    break;
                }

                // Check if this is a reCAPTCHA redirect page
                var isRedirectPage = await page.EvaluateAsync<bool>(@"
                    () => {
                        return document.title.includes('reCAPTCHA') ||
                               document.body.textContent.includes('正在檢查瀏覽器') ||
                               window.location.href.includes('recaptcha');
                    }
                ");

                if (isRedirectPage && !redirectDetected)
                {
                    Console.WriteLine("reCAPTCHA redirect detected, waiting for automatic redirect back...");
                    redirectDetected = true;

                    // Wait for navigation back to original page
                    try
                    {
                        await page.WaitForURLAsync(url => url.Contains("venues") && !url.Contains("recaptcha"), new() { Timeout = 45000 });
                        Console.WriteLine("Redirected back to venue page");
                        // Wait longer for JavaScript to fully load and execute
                        await page.WaitForTimeoutAsync(8000);
                        Console.WriteLine("Waited 8s for JS initialization");
                    }
                    catch
                    {
                        Console.WriteLine("WARNING: Redirect timeout or failed");
                    }
                }

                await page.WaitForTimeoutAsync(checkInterval);
                elapsed += checkInterval;
            }

            if (elapsed >= maxWaitTime)
            {
                Console.WriteLine($"WARNING: Timed out after {maxWaitTime/1000}s");
                var pageContent = await page.ContentAsync();
                if (pageContent.Contains("reCAPTCHA"))
                {
                    Console.WriteLine("WARNING: Still on reCAPTCHA page");
                    Console.WriteLine("TIP: 設定環境變數 COURTFINDER_HEADLESS=false 後重試，並在開啟的瀏覽器中手動通過驗證。");
                }
            }

            // Extract schedule data
            var scheduleDataJson = await page.EvaluateAsync<string>(@"
                () => {
                    const result = {};
                    if (typeof mmDataPickup !== 'undefined') {
                        result.mmDataPickup = mmDataPickup;
                        console.log('mmDataPickup keys:', Object.keys(mmDataPickup));
                        if (mmDataPickup.Data) {
                            console.log('mmDataPickup.Data keys:', Object.keys(mmDataPickup.Data));
                        }
                    }
                    if (typeof VenuesCalendarJson !== 'undefined') {
                        result.VenuesCalendarJson = VenuesCalendarJson;
                        console.log('VenuesCalendarJson keys:', Object.keys(VenuesCalendarJson));
                    }
                    return Object.keys(result).length > 0 ? JSON.stringify(result) : null;
                }
            ");

            if (!string.IsNullOrEmpty(scheduleDataJson))
            {
                Console.WriteLine($"DEBUG: Extracted JSON length: {scheduleDataJson.Length} chars");
            }

            // Extract venue info
            var venueInfoJson = await page.EvaluateAsync<string>(@"
                () => {
                    if (typeof VenuesInfoJson !== 'undefined') {
                        return JSON.stringify(VenuesInfoJson);
                    }
                    return null;
                }
            ");

            // Cache venue info
            if (!string.IsNullOrEmpty(venueInfoJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(venueInfoJson);
                    if (doc.RootElement.TryGetProperty("Info", out var infoEl))
                    {
                        var venueInfo = new VenueInfo
                        {
                            Name = TryGetString(infoEl, "Name") ?? string.Empty,
                            Address = TryGetString(infoEl, "Address") ?? string.Empty,
                            District = TryGetString(infoEl, "Area") ?? string.Empty
                        };
                        SetInfoCache(courtId, venueInfo);
                    }
                }
                catch { }
            }

            var availability = new Availability { CourtId = courtId, Date = date, Slots = new List<TimeSlot>() };

            if (string.IsNullOrEmpty(scheduleDataJson))
            {
                return availability;
            }

            int? openSh = null, openEh = null;
            try
            {
                using var doc = JsonDocument.Parse(scheduleDataJson);

                if (doc.RootElement.TryGetProperty("VenuesCalendarJson", out var calendarEl))
                {
                    Console.WriteLine("DEBUG: Using VenuesCalendarJson");
                    ParseVenuesCalendar(calendarEl, date, availability);
                }
                else if (doc.RootElement.TryGetProperty("mmDataPickup", out var dataPickupRoot))
                {
                    // Read opening hours if present: _C.SH (start hour) and _C.EH (end hour)
                    int? sh = null, eh = null;
                    try
                    {
                        if (dataPickupRoot.TryGetProperty("_C", out var cEl))
                        {
                            sh = TryGetInt(cEl, "SH");
                            eh = TryGetInt(cEl, "EH");
                        }
                    }
                    catch { }

                    if (dataPickupRoot.TryGetProperty("Data", out var dataEl))
                    {
                        Console.WriteLine("DEBUG: Using mmDataPickup.Data");
                        ParseDataPickup(dataEl, date, availability, sh, eh);
                    }
                    openSh = sh; openEh = eh;
                }
            }
            catch { }

            // If we still have no per-court labels (SourceNote empty for all), try clicking court tabs to collect data per court
            if (availability.Slots.Count == 0 || availability.Slots.All(s => string.IsNullOrWhiteSpace(s.SourceNote)))
            {
                try
                {
                    var labels = await page.EvaluateAsync<string[]>(@"
                        () => {
                            const results = new Set();
                            const nodes = Array.from(document.querySelectorAll('a,button,li,span,div'));
                            const patterns = [
                                /^第\s*\d+\s*面\s*(NO\.?\s*\d+)?$/u,
                                /^NO\.?\s*\d+$/iu,
                                /^場地\s*NO\.?\s*\d+$/u,
                                /^場地\s*\d+$/u
                            ];
                            for (const el of nodes) {
                                const t = (el.textContent||'').trim().replace(/\s+/g,' ');
                                if (!t) continue;
                                if (patterns.some(p=>p.test(t))) results.add(t);
                            }
                            return Array.from(results).slice(0, 30);
                        }
                    ");

                    foreach (var label in labels)
                    {
                        try
                        {
                            await page.GetByText(label, new() { Exact = false }).First.ClickAsync();
                            await page.WaitForTimeoutAsync(800);
                            var json = await page.EvaluateAsync<string>(@"
                                () => {
                                    if (typeof mmDataPickup !== 'undefined' && mmDataPickup.Data) {
                                        return JSON.stringify({ _C: mmDataPickup._C, Data: mmDataPickup.Data });
                                    }
                                    return null;
                                }
                            ");
                            if (string.IsNullOrEmpty(json)) continue;
                            using var pdoc = JsonDocument.Parse(json);
                            JsonElement dataEl;
                            int? sh2=null, eh2=null;
                            if (pdoc.RootElement.TryGetProperty("_C", out var cEl))
                            {
                                sh2 = TryGetInt(cEl, "SH");
                                eh2 = TryGetInt(cEl, "EH");
                            }
                            if (pdoc.RootElement.TryGetProperty("Data", out dataEl))
                            {
                                var before = availability.Slots.Count;
                                ParseDataPickupPerCourt(dataEl, date, availability, label, sh2 ?? openSh, eh2 ?? openEh);
                                var added = availability.Slots.Count - before;
                                Console.WriteLine($"DEBUG: Collected {added} slots for {label}");
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            availability.Slots = availability.Slots.OrderBy(s => s.Start).ToList();
            SetAvailCache(cacheKeyAvail, availability);
            return availability;
        }
        catch (Exception)
        {
            return new Availability { CourtId = courtId, Date = date, Slots = new() };
        }
    }

    public async Task<VenueInfo?> GetVenueInfoAsync(string courtId, CancellationToken ct = default)
    {
        if (TryGetInfoFromCache(courtId, out var cached)) return cached;

        try
        {
            var browser = await GetBrowserAsync(ct);
            await using var context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();

            var url = $"https://vbs.sports.taipei/venues/?K={Uri.EscapeDataString(courtId)}#Schedule";
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30000 });
            await page.WaitForTimeoutAsync(5000);

            var venueInfoJson = await page.EvaluateAsync<string>(@"
                () => {
                    if (typeof VenuesInfoJson !== 'undefined') {
                        return JSON.stringify(VenuesInfoJson);
                    }
                    return null;
                }
            ");

            if (string.IsNullOrEmpty(venueInfoJson)) return null;

            try
            {
                using var doc = JsonDocument.Parse(venueInfoJson);
                if (doc.RootElement.TryGetProperty("Info", out var infoEl))
                {
                    var venueInfo = new VenueInfo
                    {
                        Name = TryGetString(infoEl, "Name") ?? string.Empty,
                        Address = TryGetString(infoEl, "Address") ?? string.Empty,
                        District = TryGetString(infoEl, "Area") ?? string.Empty
                    };
                    SetInfoCache(courtId, venueInfo);
                    return venueInfo;
                }
            }
            catch { }
        }
        catch { }

        return null;
    }

    private async Task<IBrowser> GetBrowserAsync(CancellationToken ct)
    {
        if (_browser != null && _browser.IsConnected) return _browser;

        await _browserLock.WaitAsync(ct);
        try
        {
            if (_browser != null && _browser.IsConnected) return _browser;

            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = IsHeadless(),
                SlowMo = GetEnvInt("COURTFINDER_SLOWMO_MS", 0),
                Args = new[] { "--disable-blink-features=AutomationControlled" }
            });

            return _browser;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    private static void ParseVenuesCalendar(JsonElement calendarEl, DateOnly date, Availability availability)
    {
        if (!calendarEl.TryGetProperty("Schedule", out var scheduleEl)) return;

        var dateKey = date.ToString("yyyy-MM-dd");
        if (!scheduleEl.TryGetProperty(dateKey, out var dateEl)) return;

        if (dateEl.ValueKind != JsonValueKind.Array) return;

        foreach (var slotEl in dateEl.EnumerateArray())
        {
            var courtName = TryGetString(slotEl, "CourtName") ?? TryGetString(slotEl, "Court") ?? string.Empty;
            var sStr = TryGetString(slotEl, "Start") ?? TryGetString(slotEl, "S");
            var eStr = TryGetString(slotEl, "End") ?? TryGetString(slotEl, "E");
            var status = TryGetString(slotEl, "Status") ?? TryGetString(slotEl, "IR");

            if (sStr is null || eStr is null) continue;
            if (!TimeOnly.TryParse(sStr, out var start)) continue;
            if (!TimeOnly.TryParse(eStr, out var end)) continue;

            var isAvail = string.Equals(status, "1", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(status, "available", StringComparison.OrdinalIgnoreCase);

            availability.Slots.Add(new TimeSlot
            {
                Start = start,
                End = end,
                IsAvailable = isAvail,
                SourceNote = courtName
            });
        }
    }

    private static void ParseDataPickup(JsonElement dataEl, DateOnly date, Availability availability, int? startHour, int? endHour)
    {
        var monthKey = $"{date.Year}{date.Month:D2}";
        if (!dataEl.TryGetProperty(monthKey, out var monthEl)) return;

        // Structure might be: monthEl[day][court][timeSlot] OR monthEl[court][timeSlot]
        var dayKey = date.Day.ToString();

        // Try to get data for specific day
        if (monthEl.TryGetProperty(dayKey, out var dayEl))
        {
            Console.WriteLine($"DEBUG: Found date key '{dayKey}' in mmDataPickup.Data[{monthKey}], ValueKind={dayEl.ValueKind}");

            // dayEl should contain courts
            var courtNames = new List<string>();
            if (dayEl.ValueKind == JsonValueKind.Object)
            {
                // Heuristic: check if properties are time slots directly (e.g., "1000": {S,E,...})
                bool IsTimeObj(JsonElement el)
                    => el.ValueKind == JsonValueKind.Object &&
                       el.TryGetProperty("S", out var _s) && _s.ValueKind == JsonValueKind.String &&
                       el.TryGetProperty("E", out var _e) && _e.ValueKind == JsonValueKind.String;

                using var objEnum = dayEl.EnumerateObject().GetEnumerator();
                if (objEnum.MoveNext() && IsTimeObj(objEnum.Current.Value))
                {
                    Console.WriteLine("DEBUG: Day-level is time map (no per-court keys)");
                    // Parse each time entry directly
                    foreach (var timeProp in dayEl.EnumerateObject())
                    {
                        var obj = timeProp.Value;
                        var sStr = obj.TryGetProperty("S", out var sEl) && sEl.ValueKind == JsonValueKind.String ? sEl.GetString() : null;
                        var eStr = obj.TryGetProperty("E", out var eEl) && eEl.ValueKind == JsonValueKind.String ? eEl.GetString() : null;
                        var d = obj.TryGetProperty("D", out var dEl) && dEl.ValueKind == JsonValueKind.String ? dEl.GetString() : "0";
                        if (sStr is null || eStr is null) continue;
                        if (!TimeOnly.TryParse(sStr, out var start)) continue;
                        if (!TimeOnly.TryParse(eStr, out var end)) continue;
                        if (!WithinOpeningHours(start, end, startHour, endHour)) continue;
                        var isAvail = string.Equals(d, "0", StringComparison.OrdinalIgnoreCase);
                        availability.Slots.Add(new TimeSlot
                        {
                            Start = start,
                            End = end,
                            IsAvailable = isAvail,
                            SourceNote = string.Empty
                        });
                    }
                }
                else
                {
                    foreach (var courtProp in dayEl.EnumerateObject())
                    {
                        courtNames.Add(courtProp.Name);
                        Console.WriteLine($"DEBUG: Court key='{courtProp.Name}', ValueKind={courtProp.Value.ValueKind}");
                        if (courtProp.Value.ValueKind == JsonValueKind.Object)
                        {
                            var courtName = $"Court {courtProp.Name}";
                            ParseCourtSlots(courtProp.Value, courtName, availability, startHour, endHour);
                        }
                    }
                    Console.WriteLine($"DEBUG: Found {courtNames.Count} courts for day {dayKey}: {string.Join(", ", courtNames.OrderBy(x => x))}");
                }
            }
            else
            {
                Console.WriteLine($"DEBUG: dayEl is not an object, it's {dayEl.ValueKind}");
            }
        }
        else
        {
            Console.WriteLine($"DEBUG: Day key '{dayKey}' not found. Available keys: {string.Join(", ", monthEl.EnumerateObject().Select(p => p.Name).OrderBy(x => x))}");
        }
    }

    private static bool WithinOpeningHours(TimeOnly start, TimeOnly end, int? sh, int? eh)
    {
        if (sh is null && eh is null) return true;
        var min = sh is int s ? new TimeOnly(Math.Clamp(s, 0, 23), 0) : new TimeOnly(0, 0);
        // EH 表示營業截止小時（結束時間不得超過 EH:00）
        TimeOnly maxEnd = eh is int e ? new TimeOnly(Math.Clamp(e, 0, 23), 0) : new TimeOnly(23, 59);
        return start >= min && end <= maxEnd;
    }

    private static void ParseCourtSlots(JsonElement slotsEl, string courtName, Availability availability, int? startHour, int? endHour)
    {
        foreach (var timeProp in slotsEl.EnumerateObject())
        {
            var obj = timeProp.Value;
            var sStr = obj.TryGetProperty("S", out var sEl) && sEl.ValueKind == JsonValueKind.String ? sEl.GetString() : null;
            var eStr = obj.TryGetProperty("E", out var eEl) && eEl.ValueKind == JsonValueKind.String ? eEl.GetString() : null;
            var d = obj.TryGetProperty("D", out var dEl) && dEl.ValueKind == JsonValueKind.String ? dEl.GetString() : "0";

            if (sStr is null || eStr is null) continue;
            if (!TimeOnly.TryParse(sStr, out var start)) continue;
            if (!TimeOnly.TryParse(eStr, out var end)) continue;
            if (!WithinOpeningHours(start, end, startHour, endHour)) continue;

            var isAvail = string.Equals(d, "0", StringComparison.OrdinalIgnoreCase);
            availability.Slots.Add(new TimeSlot
            {
                Start = start,
                End = end,
                IsAvailable = isAvail,
                SourceNote = courtName
            });
        }
    }

    // Same as ParseDataPickup but attach a fixed court label
    private static void ParseDataPickupPerCourt(JsonElement dataEl, DateOnly date, Availability availability, string courtLabel, int? startHour, int? endHour)
    {
        var monthKey = $"{date.Year}{date.Month:D2}";
        if (!dataEl.TryGetProperty(monthKey, out var monthEl)) return;
        var dayKey = date.Day.ToString();
        if (!monthEl.TryGetProperty(dayKey, out var dayEl)) return;
        if (dayEl.ValueKind != JsonValueKind.Object) return;

        foreach (var timeProp in dayEl.EnumerateObject())
        {
            var obj = timeProp.Value;
            var sStr = obj.TryGetProperty("S", out var sEl) && sEl.ValueKind == JsonValueKind.String ? sEl.GetString() : null;
            var eStr = obj.TryGetProperty("E", out var eEl) && eEl.ValueKind == JsonValueKind.String ? eEl.GetString() : null;
            var d = obj.TryGetProperty("D", out var dEl) && dEl.ValueKind == JsonValueKind.String ? dEl.GetString() : "0";
            if (sStr is null || eStr is null) continue;
            if (!TimeOnly.TryParse(sStr, out var start)) continue;
            if (!TimeOnly.TryParse(eStr, out var end)) continue;
            if (!WithinOpeningHours(start, end, startHour, endHour)) continue;
            var isAvail = string.Equals(d, "0", StringComparison.OrdinalIgnoreCase);
            availability.Slots.Add(new TimeSlot
            {
                Start = start,
                End = end,
                IsAvailable = isAvail,
                SourceNote = courtLabel
            });
        }
    }

    private bool TryGetAvailFromCache(string key, out Availability? avail)
    {
        avail = null;
        if (AvailCache.TryGetValue(key, out var entry))
        {
            if (entry.expires > DateTimeOffset.UtcNow)
            {
                avail = entry.avail;
                return true;
            }
            AvailCache.TryRemove(key, out _);
        }
        return false;
    }

    private void SetAvailCache(string key, Availability avail)
    {
        AvailCache[key] = (DateTimeOffset.UtcNow.Add(_cacheTtl), avail);
    }

    private bool TryGetInfoFromCache(string k, out VenueInfo? info)
    {
        info = null;
        if (InfoCache.TryGetValue(k, out var entry))
        {
            if (entry.expires > DateTimeOffset.UtcNow)
            {
                info = entry.info;
                return true;
            }
            InfoCache.TryRemove(k, out _);
        }
        return false;
    }

    private void SetInfoCache(string k, VenueInfo info)
    {
        InfoCache[k] = (DateTimeOffset.UtcNow.Add(_cacheTtl), info);
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    private static int? TryGetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        switch (v.ValueKind)
        {
            case JsonValueKind.String:
                if (int.TryParse(v.GetString(), out var si)) return si;
                break;
            case JsonValueKind.Number:
                if (v.TryGetInt32(out var ni)) return ni;
                break;
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_browser != null)
        {
            await _browser.CloseAsync();
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
        _browserLock.Dispose();
    }
}
