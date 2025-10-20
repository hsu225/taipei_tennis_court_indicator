using System.Text.Json;
using CourtFinder.Core.Models;

namespace CourtFinder.Core.Providers;

public class MockProvider : ITennisCourtProvider
{
    private readonly string _root;
    public MockProvider(string? root = null)
    {
        _root = root ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "sample_data");
    }

    public async Task<IReadOnlyList<Court>> GetCourtsAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(_root, "courts.json");
        if (!File.Exists(path)) return Array.Empty<Court>();
        await using var fs = File.OpenRead(path);
        var courts = await JsonSerializer.DeserializeAsync<List<Court>>(fs, cancellationToken: ct);
        return courts ?? new List<Court>();
    }

    public async Task<Availability?> GetAvailabilityAsync(string courtId, DateOnly date, CancellationToken ct = default)
    {
        var dir = Path.Combine(_root, "availability");
        var file = Path.Combine(dir, $"{courtId}_{date:yyyy-MM-dd}.json");
        if (!File.Exists(file)) return new Availability { CourtId = courtId, Date = date, Slots = new() };
        await using var fs = File.OpenRead(file);
        var avail = await JsonSerializer.DeserializeAsync<Availability>(fs, cancellationToken: ct);
        return avail;
    }
}

