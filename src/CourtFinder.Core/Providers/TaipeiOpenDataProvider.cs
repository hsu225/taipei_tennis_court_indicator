using System.Net.Http.Json;
using System.Text.Json;
using CourtFinder.Core.Models;

namespace CourtFinder.Core.Providers;

public class TaipeiOpenDataProvider : ITennisCourtProvider
{
    private readonly HttpClient _http;
    private readonly string _vbsUrl;
    private readonly string _tpUrl;

    public TaipeiOpenDataProvider(HttpClient http)
    {
        _http = http;
        _vbsUrl = Environment.GetEnvironmentVariable("COURTFINDER_TAIPEI_VBS")?.Trim()
                  ?? "https://vbs.sports.taipei/opendata/sports_tms2.json";
        _tpUrl = Environment.GetEnvironmentVariable("COURTFINDER_TAIPEI_DATASET")?.Trim()
                 ?? "https://data.taipei/api/v1/dataset/260d743c-0a0e-4147-b152-a753f6d10ed1?scope=resourceAquire";
    }

    public async Task<IReadOnlyList<Court>> GetCourtsAsync(CancellationToken ct = default)
    {
        var courts = new List<Court>();
        try
        {
            using var res = await _http.GetAsync(_vbsUrl, ct);
            res.EnsureSuccessStatusCode();
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (!IsTennis(el)) continue;
                    var c = MapCourt(el);
                    if (!string.IsNullOrWhiteSpace(c.Id)) courts.Add(c);
                }
            }
        }
        catch
        {
            // swallow network/parse errors; caller will see empty list
        }

        // Deduplicate by Id
        var dedup = courts
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .ToList();

        return dedup;
    }

    public async Task<Availability?> GetAvailabilityAsync(string courtId, DateOnly date, CancellationToken ct = default)
    {
        // Availability mapping TBD; probe reachability but return empty slots for now
        try
        {
            using var res = await _http.GetAsync(_tpUrl, ct);
            res.EnsureSuccessStatusCode();
        }
        catch
        {
            // ignore network errors in restricted environments
        }

        return new Availability
        {
            CourtId = courtId,
            Date = date,
            Slots = new List<TimeSlot>()
        };
    }

    private static bool IsTennis(JsonElement el)
    {
        // Robust detection: scan all string fields for keywords.
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var s = prop.Value.GetString()?.ToLowerInvariant() ?? string.Empty;
                    if (s.Contains("網球") || s.Contains("tennis")) return true;
                }
            }
        }
        return false;
    }

    private static Court MapCourt(JsonElement el)
    {
        string GetString(params string[] names)
        {
            foreach (var n in names)
            {
                if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString() ?? string.Empty;
            }
            // Fallback: try case-insensitive name match
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    if (names.Any(n => string.Equals(n, prop.Name, StringComparison.OrdinalIgnoreCase))
                        && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        return prop.Value.GetString() ?? string.Empty;
                    }
                }
            }
            return string.Empty;
        }

        double? GetDouble(params string[] names)
        {
            foreach (var n in names)
            {
                if (el.TryGetProperty(n, out var v))
                {
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
                    if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var ds)) return ds;
                }
            }
            return null;
        }

        bool GetBool(params string[] names)
        {
            foreach (var n in names)
            {
                if (el.TryGetProperty(n, out var v))
                {
                    if (v.ValueKind == JsonValueKind.True) return true;
                    if (v.ValueKind == JsonValueKind.False) return false;
                    if (v.ValueKind == JsonValueKind.String)
                    {
                        var s = v.GetString()?.ToLowerInvariant();
                        if (s == "yes" || s == "true" || s == "1" || s == "是" || s == "有") return true;
                        if (s == "no" || s == "false" || s == "0" || s == "否" || s == "無") return false;
                    }
                }
            }
            return false;
        }

        var id = GetString("PlaceId", "Id", "id", "場地代碼", "場地編號");
        var name = GetString("PlaceName", "Name", "name", "場地名稱");
        var district = GetString("District", "area", "AreaName", "行政區");
        var address = GetString("Address", "addr", "地址");
        var surface = GetString("Surface", "surface", "場地材質", "地面材質");
        var hasLights = GetBool("HasLights", "lights", "夜間照明", "夜間照明設備");
        var lat = GetDouble("Lat", "latitude", "Y", "Ypos", "緯度");
        var lng = GetDouble("Lng", "longitude", "X", "Xpos", "經度");

        if (string.IsNullOrWhiteSpace(id)) id = name; // fallback

        return new Court
        {
            Id = id ?? string.Empty,
            Name = name,
            District = district,
            Address = address,
            Surface = surface,
            HasLights = hasLights,
            Latitude = lat,
            Longitude = lng
        };
    }
}

