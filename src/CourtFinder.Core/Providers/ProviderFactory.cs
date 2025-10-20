using CourtFinder.Core.Models;

namespace CourtFinder.Core.Providers;

public static class ProviderFactory
{
    public static ITennisCourtProvider CreateDefault(HttpClient? http = null)
    {
        var provider = Environment.GetEnvironmentVariable("COURTFINDER_PROVIDER")?.Trim().ToLowerInvariant();
        return provider switch
        {
            "mock" => new MockProvider(),
            "taipei-web" => new TaipeiWebProvider(http ?? new HttpClient()),
            _ => new TaipeiOpenDataProvider(http ?? new HttpClient())
        };
    }
}
