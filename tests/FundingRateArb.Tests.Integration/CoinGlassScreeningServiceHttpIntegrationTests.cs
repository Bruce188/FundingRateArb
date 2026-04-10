using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;

namespace FundingRateArb.Tests.Integration;

public class CoinGlassScreeningServiceHttpIntegrationTests
{
    [Fact]
    public async Task GetHotSymbols_ValidKey_ReturnsFilteredSymbols()
    {
        var json = """{"code":"0","data":[{"symbol":"BTC/USDT","apr":15.2},{"symbol":"ETH","apr":5.0}]}""";
        var handler = new FakeHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });

        await using var provider = BuildProvider(handler, includeApiKey: true);
        var service = provider.GetRequiredService<ICoinGlassScreeningProvider>();

        var result = await service.GetHotSymbolsAsync(CancellationToken.None);

        result.Should().Contain("BTC", "BTC apr 15.2 >= threshold 10.0");
        result.Should().NotContain("ETH", "ETH apr 5.0 < threshold 10.0");
    }

    [Fact]
    public async Task GetHotSymbols_MissingKey_ReturnsEmptySet()
    {
        var handler = new FakeHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        await using var provider = BuildProvider(handler, includeApiKey: false);
        var service = provider.GetRequiredService<ICoinGlassScreeningProvider>();

        var result = await service.GetHotSymbolsAsync(CancellationToken.None);

        result.Should().BeEmpty("no API key means feature is disabled");
    }

    [Fact]
    public async Task GetHotSymbols_Non2xxResponse_ReturnsEmptySetGracefully()
    {
        var handler = new FakeHandler(new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate limited", System.Text.Encoding.UTF8, "text/plain")
        });

        await using var provider = BuildProvider(handler, includeApiKey: true);
        var service = provider.GetRequiredService<ICoinGlassScreeningProvider>();

        var act = () => service.GetHotSymbolsAsync(CancellationToken.None);
        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().BeEmpty("non-2xx should return empty set gracefully");
    }

    private static ServiceProvider BuildProvider(FakeHandler handler, bool includeApiKey)
    {
        var services = new ServiceCollection();
        services.AddLogging(lb => lb.AddDebug());

        var configDict = new Dictionary<string, string?>
        {
            ["ExchangeConnectors:CoinGlass:ScreeningInvestmentUsd"] = "10000",
            ["ExchangeConnectors:CoinGlass:ScreeningMinAprPct"] = "10.0"
        };

        if (includeApiKey)
        {
            configDict["ExchangeConnectors:CoinGlass:ApiKey"] = "FAKE_API_KEY_FOR_TESTS";
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict)
            .Build();
        services.AddSingleton<IConfiguration>(config);

        services.AddResiliencePipeline("CoinGlass-v4", pipelineBuilder =>
        {
            pipelineBuilder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(60),
                MinimumThroughput = 5,
                BreakDuration = TimeSpan.FromMinutes(5),
            });
        });

        services.AddHttpClient<ICoinGlassScreeningProvider, CoinGlassScreeningService>(client =>
        {
            client.BaseAddress = new Uri("https://open-api-v4.coinglass.com/");
            client.MaxResponseContentBufferSize = 2 * 1024 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider();
    }

    private sealed class FakeHandler : DelegatingHandler
    {
        private readonly HttpResponseMessage _response;

        public FakeHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _response.Dispose();
            base.Dispose(disposing);
        }
    }
}
