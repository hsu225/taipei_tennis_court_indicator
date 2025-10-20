using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using CourtFinder.Core.Models;

namespace CourtFinder.Core.Providers;

public class TaipeiWebProvider : ITennisCourtProvider
{
    private readonly HttpClient _http;
    private readonly HttpClientHandler _handler;
    private readonly TimeSpan _cacheTtl;

    private static readonly ConcurrentDictionary<string, (DateTimeOffset expires, string content)> UrlCache = new();
    private static readonly ConcurrentDictionary<string, (DateTimeOffset expires, Availability avail)> AvailCache = new();
    private static readonly ConcurrentDictionary<string, (DateTimeOffset expires, VenueInfo info)> InfoCache = new();

    public TaipeiWebProvider(HttpClient http)
    {
        _handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            UseCookies = true,
            CookieContainer = new System.Net.CookieContainer()
        };
        _http = new HttpClient(_handler, disposeHandler: true);
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CourtFinder", "1.0"));
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(Windows NT 10.0; Win64; x64)"));
        }
        if (!_http.DefaultRequestHeaders.AcceptLanguage.Any())
        {
            _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-TW,zh;q=0.9,en;q=0.8");
        }
        _http.Timeout = TimeSpan.FromSeconds(15);

        var ttlEnv = Environment.GetEnvironmentVariable("COURTFINDER_CACHE_SECONDS");
        if (!int.TryParse(ttlEnv, out var ttlSecs) || ttlSecs <= 0) ttlSecs = 300;
        _cacheTtl = TimeSpan.FromSeconds(ttlSecs);
    }

    public Task<IReadOnlyList<Court>> GetCourtsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Court>>(Array.Empty<Court>());

    public async Task<Availability?> GetAvailabilityAsync(string courtId, DateOnly date, CancellationToken ct = default)
    {
        // In this provider, courtId is the venue K number
        // Check per-date availability cache early to avoid network if possible
        var cacheKeyAvail = $"avail:{courtId}:{date:yyyy-MM-dd}";
        if (TryGetAvailFromCache(cacheKeyAvail, out var cachedEarly)) return cachedEarly;

        var html = await GetVenueHtml(courtId, ct);
        if (string.IsNullOrEmpty(html)) return new Availability { CourtId = courtId, Date = date, Slots = new() };

        // Find datapickup script src
        var dataJsUrl = ExtractDatapickupUrl(html);
        string dataJs = string.Empty;
        if (!string.IsNullOrEmpty(dataJsUrl))
        {
            var referer = $"https://vbs.sports.taipei/venues/?K={Uri.EscapeDataString(courtId)}#Schedule";
            dataJs = await SafeGetStringAsync(dataJsUrl, referer, ct) ?? string.Empty;
        }

        // Try to read mmDataPickup (for opening hours) and Data
        var mmJson = ExtractJsObject(dataJs, "mmDataPickup");
        // mmDataPickup.Data is defined inside the datapickup js
        var dataJson = ExtractJsObject(dataJs, "mmDataPickup.Data");
        if (string.IsNullOrEmpty(dataJson))
        {
            // Fallback: try within HTML (in case of inline script)
            dataJson = ExtractJsObject(html, "mmDataPickup.Data");
        }

        var availability = new Availability { CourtId = courtId, Date = date, Slots = new List<TimeSlot>() };
        if (string.IsNullOrEmpty(dataJson)) return availability;

        int? sh = null, eh = null;
        try
        {
            // Parse opening hours from mmDataPickup._C if available
            if (!string.IsNullOrEmpty(mmJson))
            {
                using var mmDoc = JsonDocument.Parse(mmJson);
                if (mmDoc.RootElement.TryGetProperty("_C", out var cEl))
                {
                    sh = TryGetInt(cEl, "SH");
                    eh = TryGetInt(cEl, "EH");
                }
            }

            using var doc = JsonDocument.Parse(dataJson);
            var monthKey = $"{date.Year}{date.Month:D2}";
            if (!doc.RootElement.TryGetProperty(monthKey, out var monthEl)) return availability;
            var dayKey = date.Day.ToString();
            if (!monthEl.TryGetProperty(dayKey, out var dayEl)) return availability;

            foreach (var timeProp in dayEl.EnumerateObject())
            {
                var obj = timeProp.Value;
                // Expect fields: S (start "HH:mm"), E (end), IR ("1" available), C (code)
                var sStr = obj.TryGetProperty("S", out var sEl) && sEl.ValueKind == JsonValueKind.String ? sEl.GetString() : null;
                var eStr = obj.TryGetProperty("E", out var eEl) && eEl.ValueKind == JsonValueKind.String ? eEl.GetString() : null;
                var ir = obj.TryGetProperty("IR", out var irEl) && irEl.ValueKind == JsonValueKind.String ? irEl.GetString() : null;
                if (sStr is null || eStr is null) continue;
                if (!TimeOnly.TryParse(sStr, out var start)) continue;
                if (!TimeOnly.TryParse(eStr, out var end)) continue;
                if (!WithinOpeningHours(start, end, sh, eh)) continue;
                var isAvail = string.Equals(ir, "1", StringComparison.OrdinalIgnoreCase);
                availability.Slots.Add(new TimeSlot { Start = start, End = end, IsAvailable = isAvail, SourceNote = timeProp.Name });
            }
        }
        catch
        {
            // ignore parsing errors and return what we have (possibly empty)
        }

        // Sort slots by start time
        availability.Slots = availability.Slots.OrderBy(s => s.Start).ToList();
        SetAvailCache(cacheKeyAvail, availability);
        return availability;
    }

    public async Task<VenueInfo?> GetVenueInfoAsync(string courtId, CancellationToken ct = default)
    {
        var html = await GetVenueHtml(courtId, ct);
        if (string.IsNullOrEmpty(html)) return null;

        // VenuesInfoJson is usually defined in datapickup js; load it if present
        var dataJsUrl = ExtractDatapickupUrl(html);
        string dataJs = string.Empty;
        if (!string.IsNullOrEmpty(dataJsUrl))
        {
            var referer = $"https://vbs.sports.taipei/venues/?K={Uri.EscapeDataString(courtId)}#Schedule";
            dataJs = await SafeGetStringAsync(dataJsUrl, referer, ct) ?? string.Empty;
        }

        var venuesJson = ExtractJsObject(dataJs, "VenuesInfoJson");
        if (string.IsNullOrEmpty(venuesJson))
        {
            venuesJson = ExtractJsObject(html, "VenuesInfoJson");
        }
        if (string.IsNullOrEmpty(venuesJson)) return null;

        if (TryGetInfoFromCache(courtId, out var cached)) return cached;

        try
        {
            using var doc = JsonDocument.Parse(venuesJson);
            if (doc.RootElement.TryGetProperty("Info", out var infoEl))
            {
                string name = TryGetString(infoEl, "Name") ?? string.Empty;
                string address = TryGetString(infoEl, "Address") ?? string.Empty;
                string district = TryGetString(infoEl, "Area") ?? string.Empty;
                var venueInfo = new VenueInfo { Name = name, Address = address, District = district };
                SetInfoCache(courtId, venueInfo);
                return venueInfo;
            }
        }
        catch { }
        return null;
    }

    private async Task<string?> GetVenueHtml(string k, CancellationToken ct)
    {
        var url = $"https://vbs.sports.taipei/venues/?K={Uri.EscapeDataString(k)}#Schedule";
        return await SafeGetStringAsync(url, ct);
    }

    private async Task<string?> SafeGetStringAsync(string url, CancellationToken ct)
    {
        try
        {
            // URL-level cache (HTML/JS text)
            if (TryGetUrlFromCache(url, out var cached)) return cached;

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var res = await _http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
            var text = await res.Content.ReadAsStringAsync(ct);
            SetUrlCache(url, text);
            return text;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> SafeGetStringAsync(string url, string? referer, CancellationToken ct)
    {
        try
        {
            // URL-level cache (HTML/JS text) with referer
            if (TryGetUrlFromCache(url, out var cached)) return cached;

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(referer))
            {
                req.Headers.Referrer = new Uri(referer);
            }
            using var res = await _http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
            var text = await res.Content.ReadAsStringAsync(ct);
            SetUrlCache(url, text);
            return text;
        }
        catch
        {
            return null;
        }
    }

    private bool TryGetUrlFromCache(string url, out string? content)
    {
        content = null;
        if (UrlCache.TryGetValue(url, out var entry))
        {
            if (entry.expires > DateTimeOffset.UtcNow)
            {
                content = entry.content;
                return true;
            }
            UrlCache.TryRemove(url, out _);
        }
        return false;
    }

    private void SetUrlCache(string url, string content)
    {
        UrlCache[url] = (DateTimeOffset.UtcNow.Add(_cacheTtl), content);
    }

    private bool TryGetAvailFromCache(string key, out Availability avail)
    {
        avail = null!;
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

    private static string? ExtractDatapickupUrl(string html)
    {
        // <script src="/_/j/datapickupv5.php?163106"></script>
        var m = Regex.Match(html ?? string.Empty, "<script[^>]+src=\\\"(?<u>[^\\\"]*datapickupv5\\.php[^\\\"]*)\\\"", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var u = m.Groups["u"].Value;
        if (u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return u;
        // make absolute
        return $"https://vbs.sports.taipei{(u.StartsWith('/') ? "" : "/")}{u}";
    }

    private static string? ExtractJsObject(string text, string varOrPath)
    {
        if (string.IsNullOrEmpty(text)) return null;
        // Try pattern: var X = { ... };
        var idx = text.IndexOf(varOrPath, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        // find '=' after the variable
        var eq = text.IndexOf('=', idx);
        if (eq < 0) return null;
        // find first '{' after '='
        var start = text.IndexOf('{', eq);
        if (start < 0) return null;
        var obj = ReadBalancedBraces(text, start);
        return obj;
    }

    private static string? ReadBalancedBraces(string text, int start)
    {
        int depth = 0;
        bool inString = false;
        char stringQuote = '\0';
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (c == stringQuote && text[i - 1] != '\\') inString = false;
                continue;
            }
            if (c == '\'' || c == '"')
            {
                inString = true; stringQuote = c; continue;
            }
            if (c == '{') depth++;
            if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    var json = text.Substring(start, i - start + 1);
                    return json;
                }
            }
        }
        return null;
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
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

    private static bool WithinOpeningHours(TimeOnly start, TimeOnly end, int? sh, int? eh)
    {
        if (sh is null && eh is null) return true;
        var min = sh is int s ? new TimeOnly(Math.Clamp(s, 0, 23), 0) : new TimeOnly(0, 0);
        // EH 表示營業截止小時（結束時間不得超過 EH:00）
        TimeOnly maxEnd = eh is int e ? new TimeOnly(Math.Clamp(e, 0, 23), 0) : new TimeOnly(23, 59);
        return start >= min && end <= maxEnd;
    }
}

public sealed class VenueInfo
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string District { get; set; } = string.Empty;
}
