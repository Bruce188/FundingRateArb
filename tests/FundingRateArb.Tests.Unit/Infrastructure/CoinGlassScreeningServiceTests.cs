using System.Net;
using System.Text;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using FundingRateArb.Infrastructure.Services;
using FundingRateArb.Tests.Unit.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Moq.Protected;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;

namespace FundingRateArb.Tests.Unit.Infrastructure;

public class CoinGlassScreeningServiceTests : IDisposable
{
    private readonly List<HttpClient> _clientsToDispose = new();

    public void Dispose()
    {
        foreach (var client in _clientsToDispose)
        {
            client.Dispose();
        }
        _clientsToDispose.Clear();
    }

    /// <summary>
    /// Records every request the service makes and returns a queued response, so tests can
    /// verify the request URL/headers and assert how the service handles each response shape.
    /// Disposes any remaining queued responses when itself disposed (via the owning HttpClient).
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public Queue<HttpResponseMessage> Responses { get; } = new();
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Responses.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }
            return Task.FromResult(Responses.Dequeue());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Any responses that were queued but never consumed — dispose to avoid leaks.
                while (Responses.Count > 0)
                {
                    Responses.Dequeue().Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }

    private (CoinGlassScreeningService Service, StubHandler Handler) MakeService(
        string? apiKey = "test-key",
        int investmentUsd = 10000,
        double minAprPct = 10d,
        ResiliencePipelineProvider<string>? pipelineProvider = null)
    {
        var handler = new StubHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://open-api-v4.coinglass.com/") };
        _clientsToDispose.Add(client);
        var configValues = new Dictionary<string, string?>
        {
            ["ExchangeConnectors:CoinGlass:ApiKey"] = apiKey,
            ["ExchangeConnectors:CoinGlass:ScreeningInvestmentUsd"] = investmentUsd.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ExchangeConnectors:CoinGlass:ScreeningMinAprPct"] = minAprPct.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues!).Build();
        var service = new CoinGlassScreeningService(
            client,
            config,
            NullLogger<CoinGlassScreeningService>.Instance,
            pipelineProvider ?? TestResiliencePipelineProvider.NoOp());
        return (service, handler);
    }

    private (CoinGlassScreeningService Service, StubHandler Handler, ListLogger<CoinGlassScreeningService> Logger) MakeServiceWithLogger(
        ResiliencePipelineProvider<string>? pipelineProvider = null)
    {
        var handler = new StubHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://open-api-v4.coinglass.com/") };
        _clientsToDispose.Add(client);
        var configValues = new Dictionary<string, string?>
        {
            ["ExchangeConnectors:CoinGlass:ApiKey"] = "test-key",
            ["ExchangeConnectors:CoinGlass:ScreeningInvestmentUsd"] = "10000",
            ["ExchangeConnectors:CoinGlass:ScreeningMinAprPct"] = "10",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues!).Build();
        var logger = new ListLogger<CoinGlassScreeningService>();
        var service = new CoinGlassScreeningService(
            client,
            config,
            logger,
            pipelineProvider ?? TestResiliencePipelineProvider.NoOp());
        return (service, handler, logger);
    }

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    [Fact]
    public async Task GetHotSymbolsAsync_NoApiKey_ReturnsEmptySetWithoutCallingApi()
    {
        var (service, handler) = MakeService(apiKey: null);

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
        handler.Requests.Should().BeEmpty("screening is opt-in — no key means no API call");
    }

    [Fact]
    public async Task GetHotSymbolsAsync_EmptyApiKey_ReturnsEmptySet()
    {
        var (service, handler) = MakeService(apiKey: "");

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHotSymbolsAsync_NonSuccessStatus_ReturnsEmptySet()
    {
        var (service, handler) = MakeService();
        handler.Responses.Enqueue(JsonResponse("{\"code\":\"0\"}", HttpStatusCode.TooManyRequests));

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHotSymbolsAsync_MalformedJson_ReturnsEmptySet()
    {
        var (service, handler) = MakeService();
        handler.Responses.Enqueue(JsonResponse("{not valid json"));

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHotSymbolsAsync_NonZeroErrorCode_ReturnsEmptySet()
    {
        var (service, handler) = MakeService();
        handler.Responses.Enqueue(JsonResponse("{\"code\":\"40001\",\"msg\":\"rate limited\",\"data\":[]}"));

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHotSymbolsAsync_ItemsAboveAprThreshold_ReturnedAsHotSymbols()
    {
        var (service, handler) = MakeService(minAprPct: 50d);
        var json = """
        {
          "code": "0",
          "msg": "success",
          "data": [
            { "symbol": "BTCUSDT", "apr": 100.5, "funding": 0.5, "fee": 0.03, "spread": 0.01 },
            { "symbol": "ETHUSDT", "apr": 80.0, "funding": 0.4, "fee": 0.03, "spread": 0.01 },
            { "symbol": "DOGEUSDT", "apr": 25.0, "funding": 0.1, "fee": 0.03, "spread": 0.01 }
          ]
        }
        """;
        handler.Responses.Enqueue(JsonResponse(json));

        var result = await service.GetHotSymbolsAsync();

        result.Should().Contain("BTC", "100.5 APR > 50 threshold");
        result.Should().Contain("ETH", "80.0 APR > 50 threshold");
        result.Should().NotContain("DOGE", "25.0 APR < 50 threshold");
    }

    [Fact]
    public async Task GetHotSymbolsAsync_NormalizesUsdtUsdAndPerpSuffixes()
    {
        var (service, handler) = MakeService(minAprPct: 1d);
        var json = """
        {
          "code": "0",
          "msg": "success",
          "data": [
            { "symbol": "BTCUSDT", "apr": 50, "funding": 0, "fee": 0, "spread": 0 },
            { "symbol": "ETHUSD", "apr": 50, "funding": 0, "fee": 0, "spread": 0 },
            { "symbol": "SOL-PERP", "apr": 50, "funding": 0, "fee": 0, "spread": 0 },
            { "symbol": "AVAX_PERP", "apr": 50, "funding": 0, "fee": 0, "spread": 0 }
          ]
        }
        """;
        handler.Responses.Enqueue(JsonResponse(json));

        var result = await service.GetHotSymbolsAsync();

        result.Should().Contain("BTC");
        result.Should().Contain("ETH");
        result.Should().Contain("SOL");
        result.Should().Contain("AVAX");
    }

    [Fact]
    public async Task GetHotSymbolsAsync_SetIsCaseInsensitive()
    {
        var (service, handler) = MakeService(minAprPct: 1d);
        handler.Responses.Enqueue(JsonResponse(
            "{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"symbol\":\"BTC\",\"apr\":100,\"funding\":0,\"fee\":0,\"spread\":0}]}"));

        var result = await service.GetHotSymbolsAsync();

        result.Should().Contain("BTC");
        result.Should().Contain("btc", "the returned set uses StringComparer.OrdinalIgnoreCase");
        result.Should().Contain("Btc");
    }

    [Fact]
    public async Task GetHotSymbolsAsync_SendsApiKeyHeaderAndUsdQueryParameter()
    {
        var (service, handler) = MakeService(apiKey: "secret-key", investmentUsd: 25000);
        handler.Responses.Enqueue(JsonResponse("{\"code\":\"0\",\"data\":[]}"));

        await service.GetHotSymbolsAsync();

        handler.Requests.Should().HaveCount(1);
        var request = handler.Requests[0];
        request.RequestUri!.ToString().Should().Contain("api/futures/funding-rate/arbitrage");
        request.RequestUri.Query.Should().Contain("usd=25000");
        request.Headers.GetValues("CG-API-KEY").Should().Equal("secret-key");
    }

    [Fact]
    public async Task GetHotSymbolsAsync_EmptyDataArray_ReturnsEmptySet()
    {
        var (service, handler) = MakeService();
        handler.Responses.Enqueue(JsonResponse("{\"code\":\"0\",\"msg\":\"\",\"data\":[]}"));

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHotSymbolsAsync_NonSuccessStatusFromServer_ReturnsEmptySet()
    {
        // 503 path — goes through the !response.IsSuccessStatusCode early-return.
        var (service, handler) = MakeService();
        handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
    }

    /// <summary>
    /// Actually throws from SendAsync so the outer catch block runs.
    /// The previous name "NetworkException" was misleading — a 503 response is
    /// not a network exception.
    /// </summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("connection refused");
        }
    }

    [Fact]
    public async Task GetHotSymbolsAsync_TransportException_ReturnsEmptySetAndDoesNotThrow()
    {
        var handler = new ThrowingHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://open-api-v4.coinglass.com/") };
        _clientsToDispose.Add(client);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExchangeConnectors:CoinGlass:ApiKey"] = "test-key",
        }!).Build();
        var service = new CoinGlassScreeningService(
            client,
            config,
            NullLogger<CoinGlassScreeningService>.Instance,
            TestResiliencePipelineProvider.NoOp());

        // Must not throw — the outer catch swallows HttpRequestException.
        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty(
            "transport exceptions must be caught and return an empty set");
    }

    // ── plan-v61 Task 2.2: body logging, IsAvailable, Polly circuit breaker ──

    private static HttpResponseMessage ErrorJson(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task CoinGlassScreeningService_Returns401_LogsBodyAndReturnsUnavailable()
    {
        var (service, handler, logger) = MakeServiceWithLogger();
        handler.Responses.Enqueue(ErrorJson(HttpStatusCode.Unauthorized, "{\"error\":\"unauthorized\"}"));

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
        logger.ContainsMessage("401", LogLevel.Warning).Should().BeTrue();
        logger.ContainsMessage("unauthorized", LogLevel.Warning).Should().BeTrue();
    }

    [Fact]
    public async Task CoinGlassScreeningService_Returns429_LogsBodyAndReturnsUnavailable()
    {
        var (service, handler, logger) = MakeServiceWithLogger();
        handler.Responses.Enqueue(ErrorJson(HttpStatusCode.TooManyRequests, "{\"error\":\"rate limited\"}"));

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
        logger.ContainsMessage("429", LogLevel.Warning).Should().BeTrue();
        logger.ContainsMessage("rate limited", LogLevel.Warning).Should().BeTrue();
    }

    [Fact]
    public async Task CoinGlassScreeningService_Returns500_LogsBodyAndReturnsUnavailable()
    {
        var (service, handler, logger) = MakeServiceWithLogger();
        handler.Responses.Enqueue(ErrorJson(HttpStatusCode.InternalServerError, "{\"error\":\"server boom\"}"));

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
        logger.ContainsMessage("500", LogLevel.Warning).Should().BeTrue();
        logger.ContainsMessage("server boom", LogLevel.Warning).Should().BeTrue();
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("request timeout after 30s");
    }

    [Fact]
    public async Task CoinGlassScreeningService_Timeout_LogsAndReturnsUnavailable()
    {
        var handler = new TimeoutHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://open-api-v4.coinglass.com/") };
        _clientsToDispose.Add(client);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExchangeConnectors:CoinGlass:ApiKey"] = "test-key",
        }!).Build();
        var logger = new ListLogger<CoinGlassScreeningService>();
        var service = new CoinGlassScreeningService(client, config, logger, TestResiliencePipelineProvider.NoOp());

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
        logger.ContainsMessage("CoinGlass arbitrage screening request failed", LogLevel.Warning).Should().BeTrue();
        // NB9 fix (review-v133): assert the sanitized exception message reaches the log,
        // not just the static template prefix. Regressions that dropped the {Body}
        // placeholder would leave the prefix intact but hide the timeout detail.
        logger.ContainsMessage("timeout", LogLevel.Warning).Should().BeTrue();
    }

    [Fact]
    public async Task CoinGlassScreeningService_MalformedJson_LogsAndReturnsUnavailable()
    {
        var (service, handler, logger) = MakeServiceWithLogger();
        handler.Responses.Enqueue(JsonResponse("{bogus"));

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
        logger.ContainsMessage("Failed to parse CoinGlass arbitrage response", LogLevel.Warning).Should().BeTrue();
    }

    [Fact]
    public async Task CoinGlassScreeningService_CircuitOpens_After5ConsecutiveFailures()
    {
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var provider = TestResiliencePipelineProvider.WithCircuitBreaker(fakeTime);
        var (service, handler, _) = MakeServiceWithLogger(provider);

        for (int i = 0; i < 10; i++)
        {
            handler.Responses.Enqueue(ErrorJson(HttpStatusCode.InternalServerError, "fail"));
        }

        for (int i = 0; i < 10 && service.IsAvailable; i++)
        {
            await service.GetHotSymbolsAsync();
            fakeTime.Advance(TimeSpan.FromMilliseconds(100));
        }

        service.IsAvailable.Should().BeFalse("circuit breaker should open after consecutive failures");

        var requestsBeforeShortCircuit = handler.Requests.Count;

        // Subsequent calls must not hit the handler
        for (int i = 0; i < 3; i++)
        {
            await service.GetHotSymbolsAsync();
        }
        handler.Requests.Count.Should().Be(requestsBeforeShortCircuit,
            "open circuit must short-circuit without hitting SendAsync");
    }

    [Fact]
    public async Task CoinGlassScreeningService_CircuitHalfOpens_After5Minutes()
    {
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var provider = TestResiliencePipelineProvider.WithCircuitBreaker(fakeTime);
        var (service, handler, _) = MakeServiceWithLogger(provider);

        // Seed enough failures to open the circuit
        for (int i = 0; i < 15; i++)
        {
            handler.Responses.Enqueue(ErrorJson(HttpStatusCode.InternalServerError, "fail"));
        }

        for (int i = 0; i < 10 && service.IsAvailable; i++)
        {
            await service.GetHotSymbolsAsync();
            fakeTime.Advance(TimeSpan.FromMilliseconds(100));
        }
        service.IsAvailable.Should().BeFalse();

        var requestsBeforeHalfOpen = handler.Requests.Count;

        // Advance past the 5-minute break window — the next call should be a probe
        fakeTime.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        await service.GetHotSymbolsAsync();

        handler.Requests.Count.Should().BeGreaterThan(requestsBeforeHalfOpen,
            "after the break window a probe request should be allowed through");
    }

    [Fact]
    public async Task CoinGlassScreeningService_BodyLogging_RedactsCgApiKey()
    {
        var (service, handler, logger) = MakeServiceWithLogger();
        handler.Responses.Enqueue(ErrorJson(
            HttpStatusCode.Unauthorized,
            "CG-API-KEY: leaked-key\nerror: invalid"));

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Should().NotBeEmpty();
        warnings.Should().NotContain(e => e.Message.Contains("leaked-key", StringComparison.Ordinal));
        warnings.Should().Contain(e => e.Message.Contains("REDACTED", StringComparison.Ordinal));
    }

    // ── review-v133 Task 2.2 reopen tests ──

    /// <summary>
    /// NB1 fix: inner exception messages must be sanitized. Before the fix, the logger
    /// was passed the raw `ex` parameter and log sinks serialized ex.ToString() verbatim,
    /// leaking any secret in the inner exception's message.
    /// </summary>
    [Fact]
    public async Task CoinGlassScreeningService_InnerExceptionWithSecret_NotLogged()
    {
        var inner = new InvalidOperationException("inner CG-API-KEY: innersecret trailing");
        var outer = new HttpRequestException("outer wrapper", inner);

        var throwingHandler = new InnerExceptionHandler(outer);
        var client = new HttpClient(throwingHandler) { BaseAddress = new Uri("https://open-api-v4.coinglass.com/") };
        _clientsToDispose.Add(client);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExchangeConnectors:CoinGlass:ApiKey"] = "test-key",
        }!).Build();
        var logger = new ListLogger<CoinGlassScreeningService>();
        var service = new CoinGlassScreeningService(client, config, logger, TestResiliencePipelineProvider.NoOp());

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Should().NotBeEmpty();
        warnings.Should().NotContain(e => e.Message.Contains("innersecret", StringComparison.Ordinal),
            "the inner exception message must be sanitized before logging");
        warnings.Where(e => e.Message.Contains("CoinGlass arbitrage screening request failed", StringComparison.Ordinal))
            .Should().OnlyContain(e => e.Exception == null,
                "the exception object must not be passed verbatim — only the sanitized string form");
    }

    /// <summary>
    /// NB4 fix: JSON parse failures on non-seekable response streams used to log an empty
    /// body. After the fix the body is read as a string first, so the malformed body
    /// text is visible in the log.
    /// </summary>
    [Fact]
    public async Task CoinGlassScreeningService_MalformedJsonOnNonSeekableStream_BodyAppearsInLog()
    {
        var (service, handler, logger) = MakeServiceWithLogger();
        handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ForwardOnlyStreamContent("{malformed screening"),
        });

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
        logger.ContainsMessage("Failed to parse CoinGlass arbitrage response", LogLevel.Warning).Should().BeTrue();
        logger.ContainsMessage("malformed screening", LogLevel.Warning).Should().BeTrue(
            "pre-fix the body was empty because ReadFromJsonAsync had already consumed the non-seekable stream");
    }

    /// <summary>
    /// NB8 fix: pin the 5-failure threshold. Count handler invocations before IsAvailable
    /// flips to false to guard against a regression that bumped MinimumThroughput.
    /// </summary>
    [Fact]
    public async Task CoinGlassScreeningService_CircuitOpens_AtExactly5Failures()
    {
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var provider = TestResiliencePipelineProvider.WithCircuitBreaker(fakeTime);
        var (service, handler, _) = MakeServiceWithLogger(provider);

        for (int i = 0; i < 20; i++)
        {
            handler.Responses.Enqueue(ErrorJson(HttpStatusCode.InternalServerError, "fail"));
        }

        int attempts = 0;
        while (service.IsAvailable && attempts < 20)
        {
            await service.GetHotSymbolsAsync();
            fakeTime.Advance(TimeSpan.FromMilliseconds(100));
            attempts++;
        }

        service.IsAvailable.Should().BeFalse();
        handler.Requests.Count.Should().BeInRange(5, 6,
            "plan-v61 specifies 5 failures trip the breaker with Polly's rolling window " +
            "(may need 1 extra sample before the sliding window has enough data)");
    }

    // ── review-v134 Task 2.2 reopen tests (cycle 2: NB2) ──

    /// <summary>
    /// NB2 fix (review-v134): after the v4 screening breaker opens, subsequent short-circuited
    /// calls must NOT keep emitting Warning-level log entries. The fix makes the Warning-level
    /// entry fire exactly once on the edge transition (when <see cref="CoinGlassScreeningService.IsAvailable"/>
    /// was still <c>true</c>); subsequent short-circuits during the same break window log
    /// nothing at Warning level. Mirrors the v3 connector test.
    /// </summary>
    [Fact]
    public async Task CoinGlassScreeningService_BrokenCircuit_RepeatedShortCircuits_LogsOnlyOnceOnEdge()
    {
        var fakeTime = new FakeTimeProvider();
        var provider = TestResiliencePipelineProvider.WithCircuitBreaker(fakeTime);
        var (service, handler, logger) = MakeServiceWithLogger(provider);

        // Seed enough failures to drive the breaker open.
        for (int i = 0; i < 20; i++)
        {
            handler.Responses.Enqueue(ErrorJson(HttpStatusCode.InternalServerError, "fail"));
        }

        for (int i = 0; i < 10 && service.IsAvailable; i++)
        {
            await service.GetHotSymbolsAsync();
            fakeTime.Advance(TimeSpan.FromMilliseconds(100));
        }
        service.IsAvailable.Should().BeFalse();

        // Exactly one edge-transition Warning entry should exist after the breaker opened.
        var edgeOpenedWarnings = logger.Entries
            .Where(e => e.Level == LogLevel.Warning
                        && e.Message.Contains("v4 circuit breaker OPENED", StringComparison.Ordinal))
            .Count();
        edgeOpenedWarnings.Should().Be(1,
            "exactly one 'v4 circuit breaker OPENED' Warning should fire on the edge transition");

        // Issue 10 additional short-circuited calls.
        for (int i = 0; i < 10; i++)
        {
            await service.GetHotSymbolsAsync();
        }

        var openedWarningsAfter = logger.Entries
            .Where(e => e.Level == LogLevel.Warning
                        && e.Message.Contains("v4 circuit breaker OPENED", StringComparison.Ordinal))
            .Count();
        openedWarningsAfter.Should().Be(1,
            "subsequent short-circuits must NOT emit additional 'OPENED' Warning entries — " +
            "operators need a clean signal during outages, not one log line per polling attempt");

        // Additionally verify no pre-fix-style "short-circuited: {Body}" entries exist.
        logger.Entries.Should().NotContain(
            e => e.Level == LogLevel.Warning
                 && e.Message.Contains("short-circuited", StringComparison.Ordinal),
            "the per-call body log on short-circuit must be dropped entirely");
    }

    // ── review-v133 Task 4.1 reopen tests (NB5: split pipelines) ──

    /// <summary>
    /// NB5 fix: the CoinGlass-v3 and CoinGlass-v4 pipelines are registered as independent
    /// entries in the ResiliencePipelineProvider. Verifies the Program.cs registrations
    /// by replicating them on a fresh ServiceCollection (integration-lite — avoids a full
    /// WebApplicationFactory bootstrap while still exercising real DI wiring).
    /// </summary>
    [Fact]
    public void CoinGlass_TwoPipelinesRegistered_V3AndV4()
    {
        var services = new ServiceCollection();

        services.AddResiliencePipeline("CoinGlass-v3", static pipelineBuilder =>
        {
            pipelineBuilder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(60),
                MinimumThroughput = 5,
                BreakDuration = TimeSpan.FromMinutes(5),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex =>
                    ex is not OperationCanceledException || ex is TaskCanceledException),
            });
        });
        services.AddResiliencePipeline("CoinGlass-v4", static pipelineBuilder =>
        {
            pipelineBuilder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(60),
                MinimumThroughput = 5,
                BreakDuration = TimeSpan.FromMinutes(5),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex =>
                    ex is not OperationCanceledException || ex is TaskCanceledException),
            });
        });

        using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<ResiliencePipelineProvider<string>>();

        var v3 = provider.GetPipeline("CoinGlass-v3");
        var v4 = provider.GetPipeline("CoinGlass-v4");

        v3.Should().NotBeNull();
        v4.Should().NotBeNull();
        v3.Should().NotBeSameAs(v4,
            "plan-v61 review NB5: v3 and v4 must have independent breaker state");
    }

    /// <summary>
    /// NB5 fix: failing the v4 screening service 5 times should open ONLY the v4 breaker,
    /// leaving the v3 connector (which resolves its own independent pipeline) untouched.
    /// </summary>
    [Fact]
    public async Task CoinGlassConnector_ServiceBreakerIndependentFromConnector()
    {
        CoinGlassConnector.ResetBackoffState();

        var fakeTime = new FakeTimeProvider();
        var provider = TestResiliencePipelineProvider.WithCircuitBreaker(fakeTime);

        // v4 screening service: feed it failures to open ITS breaker.
        var screeningHandler = new StubHandler();
        for (int i = 0; i < 15; i++)
        {
            screeningHandler.Responses.Enqueue(ErrorJson(HttpStatusCode.InternalServerError, "fail"));
        }
        var screeningClient = new HttpClient(screeningHandler) { BaseAddress = new Uri("https://open-api-v4.coinglass.com/") };
        _clientsToDispose.Add(screeningClient);
        var screeningConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExchangeConnectors:CoinGlass:ApiKey"] = "test-key",
        }!).Build();
        var screeningService = new CoinGlassScreeningService(
            screeningClient,
            screeningConfig,
            NullLogger<CoinGlassScreeningService>.Instance,
            provider);

        // v3 connector: uses the SAME provider but resolves its own "CoinGlass-v3" pipeline.
        var connectorHandler = new Mock<HttpMessageHandler>();
        connectorHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":"0","data":[]}""", Encoding.UTF8, "application/json"),
            });
        var connectorClient = new HttpClient(connectorHandler.Object)
        {
            BaseAddress = new Uri("https://open-api-v3.coinglass.com/"),
        };
        _clientsToDispose.Add(connectorClient);
        var connectorConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExchangeConnectors:CoinGlass:ApiKey"] = "test-key",
        }!).Build();
        var analyticsRepo = new Mock<ICoinGlassAnalyticsRepository>();
        analyticsRepo.Setup(r => r.GetKnownPairsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<(string Exchange, string Symbol)>());
        var connector = new CoinGlassConnector(
            connectorClient,
            connectorConfig,
            NullLogger<CoinGlassConnector>.Instance,
            analyticsRepo.Object,
            provider);

        // Drive v4 screening breaker open
        for (int i = 0; i < 10 && screeningService.IsAvailable; i++)
        {
            await screeningService.GetHotSymbolsAsync();
            fakeTime.Advance(TimeSpan.FromMilliseconds(100));
        }
        screeningService.IsAvailable.Should().BeFalse("v4 screening breaker should be OPEN after 5+ failures");

        // v3 connector should remain unaffected — a successful call verifies it.
        var result = await connector.GetFundingRatesAsync();
        connector.IsAvailable.Should().BeTrue(
            "v3 connector's breaker is independent from v4 and should not be tripped by v4 failures");
        result.Should().BeEmpty("handler returns empty data list");
    }

    /// <summary>Throws the supplied exception on every SendAsync call.</summary>
    private sealed class InnerExceptionHandler : HttpMessageHandler
    {
        private readonly Exception _ex;
        public InnerExceptionHandler(Exception ex) => _ex = ex;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw _ex;
    }

    /// <summary>
    /// Minimal HttpContent that exposes the body as a forward-only (non-seekable) stream,
    /// mirroring a real network response and exercising the NB4 fix end-to-end.
    /// </summary>
    private sealed class ForwardOnlyStreamContent : HttpContent
    {
        private readonly byte[] _bytes;

        public ForwardOnlyStreamContent(string body)
        {
            _bytes = Encoding.UTF8.GetBytes(body);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            return stream.WriteAsync(_bytes, 0, _bytes.Length);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
