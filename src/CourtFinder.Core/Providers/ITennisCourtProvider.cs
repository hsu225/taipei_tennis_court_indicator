using CourtFinder.Core.Models;

namespace CourtFinder.Core.Providers;

public interface ITennisCourtProvider
{
    Task<IReadOnlyList<Court>> GetCourtsAsync(CancellationToken ct = default);
    Task<Availability?> GetAvailabilityAsync(string courtId, DateOnly date, CancellationToken ct = default);
}

