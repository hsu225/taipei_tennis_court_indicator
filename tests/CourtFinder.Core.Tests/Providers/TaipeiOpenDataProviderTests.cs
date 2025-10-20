using CourtFinder.Core.Models;
using CourtFinder.Core.Providers;

namespace CourtFinder.Core.Tests.Providers;

public class TaipeiOpenDataProviderTests
{
    [Fact]
    public async Task GetCourtsAsync_Parses_Tennis_From_VbsPayload()
    {
        var vbsPayload = "[ { \"sport\": \"tennis\", \"PlaceId\": \"ID1\", \"PlaceName\": \"Test Court\", \"District\": \"Daan\" }, { \"sport\": \"basketball\", \"PlaceId\": \"B1\" } ]";
        using var http = new HttpClient(new StaticJsonHandler(vbsPayload));
        var provider = new TaipeiOpenDataProvider(http);

        var courts = await provider.GetCourtsAsync();

        Assert.NotNull(courts);
        Assert.Contains(courts, c => c.Id == "ID1" && c.Name == "Test Court");
    }

    private sealed class StaticJsonHandler : HttpMessageHandler
    {
        private readonly string _json;
        public StaticJsonHandler(string json) => _json = json;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var res = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(res);
        }
    }
}

