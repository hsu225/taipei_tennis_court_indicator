using System.Text;
using CourtFinder.Core.Models;
using CourtFinder.Core.Providers;

// Ensure proper UTF-8 encoding for Chinese characters in Windows console
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

var provider = ProviderFactory.CreateDefault();
var providerName = provider is TaipeiOpenDataProvider ? "taipei-open" : (provider is MockProvider ? "mock" : provider.GetType().Name.ToLowerInvariant());
var showProvider = args.Skip(1).Any(a => string.Equals(a, "--show-provider", StringComparison.OrdinalIgnoreCase)) || providerName == "mock";

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintHelp();
    return 0;
}

var cmd = args[0].ToLowerInvariant();
var ct = CancellationToken.None;

// Optional connectivity probe: use with --probe flag, or via 'health' command
if (HasFlag("--probe") && cmd is not "health")
{
    await ProbeConnectivityAsync();
}

switch (cmd)
{
    case "list":
        await ListCourts();
        break;
    case "search":
        await SearchCourts();
        break;
    case "status":
        await ShowStatus();
        break;
    case "health":
        await ProbeConnectivityAsync();
        return 0;
    default:
        Console.WriteLine($"Unknown command: {cmd}\n");
        PrintHelp();
        return 2;
}

return 0;

async Task ListCourts()
{
    if (showProvider)
    {
        Console.WriteLine($"Provider: {providerName}{(providerName == "mock" ? " (sample data)" : string.Empty)}");
    }
    var district = GetOptionValue("--district");
    var strictName = HasFlag("--strict-name");
    var excludeRaw = GetOptionValue("--exclude");
    var excludeTerms = string.IsNullOrWhiteSpace(excludeRaw)
        ? Array.Empty<string>()
        : excludeRaw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    var courts = await provider.GetCourtsAsync(ct);
    var filtered = string.IsNullOrWhiteSpace(district)
        ? courts
        : courts.Where(c => c.District.Contains(district, StringComparison.OrdinalIgnoreCase)).ToList();

    if (strictName)
    {
        filtered = filtered.Where(c =>
            (!string.IsNullOrWhiteSpace(c.Name) &&
             (c.Name.Contains("網球", StringComparison.OrdinalIgnoreCase) ||
              c.Name.Contains("tennis", StringComparison.OrdinalIgnoreCase)))
        ).ToList();
    }

    if (excludeTerms.Length > 0)
    {
        filtered = filtered.Where(c =>
            excludeTerms.All(term => !c.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
        ).ToList();
    }

    if (filtered.Count == 0)
    {
        Console.WriteLine("No courts returned. If using live data, check network or try a broader filter.");
        return;
    }

    foreach (var c in filtered.OrderBy(c => c.District).ThenBy(c => c.Name))
    {
        Console.WriteLine($"[{c.Id}] {c.Name} — {c.District} | {c.Address}");
    }
}

async Task SearchCourts()
{
    if (showProvider)
    {
        Console.WriteLine($"Provider: {providerName}{(providerName == "mock" ? " (sample data)" : string.Empty)}");
    }
    var name = GetOptionValue("--name");
    if (string.IsNullOrWhiteSpace(name))
    {
        Console.WriteLine("search requires --name <keyword>");
        return;
    }
    var courts = await provider.GetCourtsAsync(ct);
    var filtered = courts.Where(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
    if (filtered.Count == 0)
    {
        var loose = FilterCourts(courts, name);
        if (loose.Count == 0)
        {
            Console.WriteLine($"No courts matched '{name}'. Try a different keyword or use 'list'.");
            return;
        }
        filtered = loose;
    }
    foreach (var c in filtered.OrderBy(c => c.Name))
    {
        Console.WriteLine($"[{c.Id}] {c.Name} — {c.District} | {c.Address}");
    }
}

async Task ShowStatus()
{
    if (showProvider)
    {
        Console.WriteLine($"Provider: {providerName}{(providerName == "mock" ? " (sample data)" : string.Empty)}");
    }
    var venueK = GetOptionValue("--k");
    var courtName = GetOptionValue("--court");
    var dateStr = GetOptionValue("--date");
    var date = string.IsNullOrWhiteSpace(dateStr) ? DateOnly.FromDateTime(DateTime.Today) : DateOnly.Parse(dateStr);
    var timeRange = GetOptionValue("--time");
    var availableOnly = HasFlag("--available");

    if (!string.IsNullOrWhiteSpace(venueK))
    {
        // Use Playwright-based provider for better reCAPTCHA handling
        await using var web = new CourtFinder.Core.Providers.TaipeiPlaywrightProvider();
        var availWeb = await web.GetAvailabilityAsync(venueK, date, ct) ?? new Availability { CourtId = venueK, Date = date };
        var info = await web.GetVenueInfoAsync(venueK, ct);

        // Fallback: if Playwright failed to extract any slots, try lightweight HTTP parser
        if (availWeb.Slots.Count == 0)
        {
            Console.WriteLine("No data via browser path; trying HTTP fallback...");
            var http = new HttpClient();
            var httpProv = new CourtFinder.Core.Providers.TaipeiWebProvider(http);
            var availHttp = await httpProv.GetAvailabilityAsync(venueK, date, ct);
            if (availHttp != null && availHttp.Slots.Count > 0)
            {
                availWeb = availHttp;
                // Also try to populate venue info via HTTP if missing
                info ??= await httpProv.GetVenueInfoAsync(venueK, ct);
            }
        }
        var displayName = info?.Name ?? $"K={venueK}";
        var displayDistrict = info?.District ?? string.Empty;
        var displayAddress = info?.Address ?? string.Empty;
        Console.WriteLine($"{displayName}{(string.IsNullOrEmpty(displayDistrict)?"":"  "+displayDistrict)} | {displayAddress}");
        Console.WriteLine($"Date: {availWeb.Date:yyyy-MM-dd}");
        var slotsK = availWeb.Slots;
        if (!string.IsNullOrWhiteSpace(timeRange) && TryParseTimeRange(timeRange, out var kstart, out var kend))
        {
            slotsK = slotsK.Where(s => !(s.End <= kstart || s.Start >= kend)).ToList();
        }
        if (availableOnly)
        {
            slotsK = slotsK.Where(s => s.IsAvailable).ToList();
        }

        // Optional: filter a specific court within the venue when using --k
        if (!string.IsNullOrWhiteSpace(courtName))
        {
            var q = courtName.Trim();
            bool Matches(string key)
            {
                if (string.IsNullOrWhiteSpace(key)) return false;
                if (key.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
                // If user provides a pure number, also try common patterns
                if (int.TryParse(q, out var num))
                {
                    var n = num.ToString();
                    if (key.Contains($"Court {n}", StringComparison.OrdinalIgnoreCase)) return true;
                    if (key.Contains($"場地{n}", StringComparison.OrdinalIgnoreCase)) return true;
                    if (key.Contains($"{n}號", StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }

            var before = slotsK.Count;
            slotsK = slotsK.Where(s => Matches(s.SourceNote ?? string.Empty)).ToList();
            if (slotsK.Count == 0)
            {
                Console.WriteLine($"No slot data matched court '{courtName}'. Available courts:");
                var availableCourts = availWeb.Slots
                    .Select(s => s.SourceNote ?? "Court")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x);
                foreach (var c in availableCourts) Console.WriteLine($"  - {c}");
                return;
            }
        }
        if (slotsK.Count == 0)
        {
            Console.WriteLine("No slot data available for the given filters.");
            return;
        }

        // Group by court name (SourceNote contains court name for multi-court venues)
        var groups = slotsK
            .GroupBy(s => string.IsNullOrWhiteSpace(s.SourceNote) ? "Court" : s.SourceNote)
            .OrderBy(g => g.Key)
            .ToList();

        if (groups.Count > 1 && groups.Any(g => !string.Equals(g.Key, "Court", StringComparison.OrdinalIgnoreCase)))
        {
            // 若已取得分面資料，移除聚合的未標記群組
            groups = groups.Where(g => !string.Equals(g.Key, "Court", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var groupedSlots = groups;

        foreach (var courtGroup in groupedSlots)
        {
            if (groupedSlots.Count() > 1)
            {
                Console.WriteLine($"\n{courtGroup.Key}:");
            }
            foreach (var s in courtGroup.OrderBy(s => s.Start))
            {
                Console.WriteLine($"  {s.Start:HH\\:mm}-{s.End:HH\\:mm} {(s.IsAvailable ? "✓ Available" : "✗ Booked")}");
            }
        }
        return;
    }

    var courts = await provider.GetCourtsAsync(ct);
    var court = FindBestCourtMatch(courts, courtName);

    if (court == null)
    {
        Console.WriteLine("Court not found. Try 'list' or 'search'.");
        var suggestions = FilterCourts(courts, courtName ?? string.Empty).Take(5).ToList();
        if (suggestions.Count > 0)
        {
            Console.WriteLine("Did you mean:");
            foreach (var s in suggestions)
            {
                Console.WriteLine($"  - {s.Name} [{s.Id}]");
            }
        }
        return;
    }

    var avail = await provider.GetAvailabilityAsync(court.Id, date, ct) ?? new Availability { CourtId = court.Id, Date = date };
    Console.WriteLine($"{court.Name} — {court.District} | {court.Address}");
    Console.WriteLine($"Date: {avail.Date:yyyy-MM-dd}");
    var slots = avail.Slots;

    // Optional time filtering
    if (!string.IsNullOrWhiteSpace(timeRange) && TryParseTimeRange(timeRange, out var start, out var end))
    {
        slots = slots.Where(s => !(s.End <= start || s.Start >= end)).ToList();
    }

    if (availableOnly)
    {
        slots = slots.Where(s => s.IsAvailable).ToList();
    }

    if (slots.Count == 0)
    {
        Console.WriteLine("No slot data available for the given filters.");
        return;
    }
    foreach (var s in slots)
    {
        Console.WriteLine($"{s.Start:HH\\:mm}-{s.End:HH\\:mm} {(s.IsAvailable ? "Available" : "Booked")}");
    }
}

void PrintHelp()
{
    Console.WriteLine("CourtFinder CLI");
    Console.WriteLine("Commands:");
    Console.WriteLine("  list [--district <name>] [--strict-name] [--exclude term1,term2]");
    Console.WriteLine("  search --name <keyword>");
    Console.WriteLine("  status --court <name> [--date yyyy-MM-dd] [--time HH:mm-HH:mm] [--available]");
    Console.WriteLine("  status --k <venueK> [--date yyyy-MM-dd] [--time HH:mm-HH:mm] [--available] [--court <nameOrNo>]");
    Console.WriteLine("  health  (probe connectivity and data sources)");
    Console.WriteLine();
    Console.WriteLine("Flags:");
    Console.WriteLine("  --probe          (probe data sources before command)");
    Console.WriteLine("  --show-provider  (print current provider: taipei-open or mock)");
    Console.WriteLine("  --strict-name    (list only items whose name contains 網球/tennis)");
    Console.WriteLine("  --exclude a,b    (exclude items whose name contains any of the terms)");
    Console.WriteLine();
    Console.WriteLine("Env:");
    Console.WriteLine("  COURTFINDER_PROVIDER=mock|taipei-open|taipei-web (default taipei-open)");
    Console.WriteLine("  COURTFINDER_TAIPEI_VBS=<vbs json url>");
    Console.WriteLine("  COURTFINDER_TAIPEI_DATASET=<data.taipei dataset url>");
}

string? GetOptionValue(string name)
{
    for (int i = 1; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

bool HasFlag(string name)
{
    for (int i = 1; i < args.Length; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
    }
    return false;
}

bool TryParseTimeRange(string input, out TimeOnly start, out TimeOnly end)
{
    start = default;
    end = default;
    var parts = input.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length != 2) return false;
    if (!TimeOnly.TryParse(parts[0], out start)) return false;
    if (!TimeOnly.TryParse(parts[1], out end)) return false;
    return end > start;
}

// Looser court name matching: exact (case-insensitive), contains, then subsequence match
Court? FindBestCourtMatch(IReadOnlyList<Court> courts, string? query)
{
    if (string.IsNullOrWhiteSpace(query) || courts.Count == 0) return null;

    // 1) Exact (case-insensitive)
    var exact = courts.FirstOrDefault(c => string.Equals(c.Name, query, StringComparison.OrdinalIgnoreCase));
    if (exact != null) return exact;

    // 2) Contains (case-insensitive)
    var contains = courts.FirstOrDefault(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    if (contains != null) return contains;

    // 3) Normalized contains and subsequence (handles extra words like 「河濱」)
    var qn = Normalize(query);
    Court? best = null;
    double bestScore = 0.0;
    foreach (var c in courts)
    {
        var tn = Normalize(c.Name);
        var score = MatchScore(tn, qn);
        if (score > bestScore)
        {
            bestScore = score;
            best = c;
        }
    }

    // A reasonable threshold so short accidental inputs don't match wildly
    return bestScore >= 0.6 ? best : null;
}

static string Normalize(string s)
{
    // Remove spaces and common punctuation, convert to lower-invariant
    var span = s.Trim().ToLowerInvariant().AsSpan();
    Span<char> buffer = stackalloc char[span.Length];
    var idx = 0;
    foreach (var ch in span)
    {
        if (char.IsWhiteSpace(ch)) continue;
        if (ch is '-' or '_' or '（' or '）' or '(' or ')' or '【' or '】' or '『' or '』' or '《' or '》' or '［' or '］' or '、' or '，' or '。' or '．' or '・' or '／' or '/' or '|' or '︱' or '：' or ':' )
            continue;
        buffer[idx++] = ch;
    }
    return new string(buffer[..idx]);
}

static double MatchScore(string targetNorm, string queryNorm)
{
    if (targetNorm.Length == 0 || queryNorm.Length == 0) return 0.0;
    if (targetNorm == queryNorm) return 1.0;
    if (targetNorm.Contains(queryNorm)) return Math.Min(0.95, (double)queryNorm.Length / targetNorm.Length + 0.3);
    if (IsSubsequence(targetNorm, queryNorm)) return Math.Min(0.85, (double)queryNorm.Length / targetNorm.Length + 0.2);
    return 0.0;
}

static bool IsSubsequence(string target, string query)
{
    // Returns true if all chars in query appear in order within target
    int i = 0, j = 0;
    while (i < target.Length && j < query.Length)
    {
        if (target[i] == query[j]) j++;
        i++;
    }
    return j == query.Length;
}

List<Court> FilterCourts(IReadOnlyList<Court> courts, string query)
{
    var results = new List<(Court court, double score)>();
    var qn = Normalize(query ?? string.Empty);
    foreach (var c in courts)
    {
        var tn = Normalize(c.Name);
        var score = MatchScore(tn, qn);
        if (score >= 0.5)
        {
            results.Add((c, score));
        }
    }
    return results
        .OrderByDescending(r => r.score)
        .ThenBy(r => r.court.Name)
        .Select(r => r.court)
        .ToList();
}

async Task ProbeConnectivityAsync()
{
    if (provider is TaipeiOpenDataProvider)
    {
        var vbs = Environment.GetEnvironmentVariable("COURTFINDER_TAIPEI_VBS")?.Trim()
                  ?? "https://vbs.sports.taipei/opendata/sports_tms2.json";
        var tp = Environment.GetEnvironmentVariable("COURTFINDER_TAIPEI_DATASET")?.Trim()
                 ?? "https://data.taipei/api/v1/dataset/260d743c-0a0e-4147-b152-a753f6d10ed1?scope=resourceAquire";

        Console.WriteLine("Probing network connectivity (Taipei endpoints)...");
        await ProbeUrl(vbs, 3);
        await ProbeUrl(tp, 3);
    }
    else if (provider is MockProvider)
    {
        Console.WriteLine("Using mock provider; verifying local sample_data...");
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "sample_data");
        var okCourts = File.Exists(Path.Combine(root, "courts.json"));
        Console.WriteLine(okCourts ? "sample_data/courts.json: OK" : "sample_data/courts.json: MISSING");
    }
}

static async Task ProbeUrl(string url, int timeoutSeconds)
{
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var http = new HttpClient() { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        using var req = new HttpRequestMessage(HttpMethod.Head, url);
        HttpResponseMessage? res;
        try
        {
            res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (HttpRequestException)
        {
            // Some endpoints may not support HEAD; fall back to GET headers only
            res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        Console.WriteLine($"  {url} -> {(int)res.StatusCode} {res.StatusCode}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  {url} -> FAIL ({ex.GetType().Name}: {ex.Message})");
    }
}
