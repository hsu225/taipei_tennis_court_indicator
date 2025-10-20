using CourtFinder.Core.Providers;

namespace CourtFinder.Core.Tests.Providers;

public class MockProviderTests
{
    [Fact]
    public async Task GetCourtsAsync_Reads_From_SampleJson()
    {
        var root = TestUtilities.SampleDataPath();
        var provider = new MockProvider(root);

        var courts = await provider.GetCourtsAsync();

        Assert.NotNull(courts);
        Assert.NotEmpty(courts);
        Assert.Contains(courts, c => c.Id == "DAAN_FOREST");
    }

    [Fact]
    public async Task GetAvailabilityAsync_ExistingFile_Parses_Slots()
    {
        var root = TestUtilities.SampleDataPath();
        var provider = new MockProvider(root);

        var date = new DateOnly(2025, 10, 01);
        var availability = await provider.GetAvailabilityAsync("DAAN_FOREST", date);

        Assert.NotNull(availability);
        Assert.Equal("DAAN_FOREST", availability!.CourtId);
        Assert.Equal(date, availability.Date);
        Assert.NotEmpty(availability.Slots);
    }
}

