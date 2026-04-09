using Microsoft.Extensions.Time.Testing;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;

namespace FundingRateArb.Tests.Unit.Common;

/// <summary>
/// Test helpers for building <see cref="ResiliencePipelineProvider{TKey}"/> instances that
/// return configurable pipelines — used by CoinGlass connector / screening service tests.
/// </summary>
internal static class TestResiliencePipelineProvider
{
    /// <summary>
    /// Returns a provider where the v3 and v4 CoinGlass pipeline names both resolve to a
    /// no-op passthrough pipeline. Use in tests that don't exercise circuit-breaker
    /// behavior but need to satisfy the constructor signature.
    /// </summary>
    public static ResiliencePipelineProvider<string> NoOp()
    {
        var registry = new ResiliencePipelineRegistry<string>();
        registry.TryAddBuilder("CoinGlass-v3", (builder, _) => { /* passthrough — no strategies */ });
        registry.TryAddBuilder("CoinGlass-v4", (builder, _) => { /* passthrough — no strategies */ });
        return registry;
    }

    /// <summary>
    /// Returns a provider whose <c>"CoinGlass-v3"</c> and <c>"CoinGlass-v4"</c> pipelines are
    /// independent circuit-breaker-only pipelines configured with the production parameters
    /// (5 minute break, 50% failure ratio, minimum throughput 5) and a test-supplied
    /// <see cref="TimeProvider"/> so tests can advance simulated time past the break window.
    /// The two pipelines track state independently (review-v133 NB5) — failing one should
    /// not trip the other.
    /// </summary>
    public static ResiliencePipelineProvider<string> WithCircuitBreaker(FakeTimeProvider timeProvider)
    {
        var registry = new ResiliencePipelineRegistry<string>();
        registry.TryAddBuilder("CoinGlass-v3", (builder, _) => ConfigureBreaker(builder, timeProvider));
        registry.TryAddBuilder("CoinGlass-v4", (builder, _) => ConfigureBreaker(builder, timeProvider));
        return registry;
    }

    private static void ConfigureBreaker(ResiliencePipelineBuilder builder, FakeTimeProvider timeProvider)
    {
        builder.TimeProvider = timeProvider;
        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(60),
            MinimumThroughput = 5,
            BreakDuration = TimeSpan.FromMinutes(5),
            // Production uses HttpRequestException/TaskCanceledException, plus the
            // connectors throw a sentinel exception on non-2xx responses. Tests need
            // to mirror that — handle any non-cancellation exception so the same
            // production code paths trip the circuit breaker.
            ShouldHandle = new PredicateBuilder().Handle<Exception>(ex =>
                ex is not OperationCanceledException || ex is TaskCanceledException),
        });
    }
}
