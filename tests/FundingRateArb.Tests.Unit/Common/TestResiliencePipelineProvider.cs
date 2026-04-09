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
    /// Returns a provider where every pipeline name resolves to a no-op passthrough pipeline.
    /// Use in tests that don't exercise circuit-breaker behavior but need to satisfy the
    /// constructor signature.
    /// </summary>
    public static ResiliencePipelineProvider<string> NoOp()
    {
        var registry = new ResiliencePipelineRegistry<string>();
        registry.TryAddBuilder("CoinGlass", (builder, _) => { /* passthrough — no strategies */ });
        return registry;
    }

    /// <summary>
    /// Returns a provider whose <c>"CoinGlass"</c> pipeline is a circuit-breaker-only pipeline
    /// configured with the production parameters (5 minute break, 50% failure ratio, minimum
    /// throughput 5) and a test-supplied <see cref="TimeProvider"/> so tests can advance
    /// simulated time past the break window.
    /// </summary>
    public static ResiliencePipelineProvider<string> WithCircuitBreaker(FakeTimeProvider timeProvider)
    {
        var registry = new ResiliencePipelineRegistry<string>();
        registry.TryAddBuilder("CoinGlass", (builder, _) =>
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
        });
        return registry;
    }
}
