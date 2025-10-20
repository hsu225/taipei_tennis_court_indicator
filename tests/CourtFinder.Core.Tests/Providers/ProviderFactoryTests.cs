using CourtFinder.Core.Providers;

namespace CourtFinder.Core.Tests.Providers;

public class ProviderFactoryTests
{
    [Fact]
    public void CreateDefault_WithEnvMock_Returns_MockProvider()
    {
        var prev = Environment.GetEnvironmentVariable("COURTFINDER_PROVIDER");
        try
        {
            Environment.SetEnvironmentVariable("COURTFINDER_PROVIDER", "mock");
            var p = ProviderFactory.CreateDefault();
            Assert.IsType<MockProvider>(p);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COURTFINDER_PROVIDER", prev);
        }
    }

    [Fact]
    public void CreateDefault_Default_Returns_TaipeiOpenDataProvider()
    {
        var prev = Environment.GetEnvironmentVariable("COURTFINDER_PROVIDER");
        try
        {
            Environment.SetEnvironmentVariable("COURTFINDER_PROVIDER", null);
            using var http = new HttpClient(new StubHandler("[]"));
            var p = ProviderFactory.CreateDefault(http);
            Assert.IsType<TaipeiOpenDataProvider>(p);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COURTFINDER_PROVIDER", prev);
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;
        public StubHandler(string json) => _json = json;
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

