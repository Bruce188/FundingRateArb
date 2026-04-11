using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using Microsoft.ApplicationInsights;

namespace FundingRateArb.Infrastructure.Services;

/// <summary>
/// <see cref="ISignalEngineMetrics"/> implementation that emits aggregate cycle
/// metrics to Application Insights <c>customMetrics</c> via
/// <see cref="TelemetryClient.TrackMetric(string, double)"/>. Metrics are buffered
/// in-process and flushed by the SDK; <c>RecordCycle</c> itself is effectively
/// allocation-free and sub-millisecond.
/// </summary>
public sealed class AppInsightsSignalEngineMetrics : ISignalEngineMetrics
{
    private readonly TelemetryClient _telemetryClient;

    public AppInsightsSignalEngineMetrics(TelemetryClient telemetryClient)
    {
        ArgumentNullException.ThrowIfNull(telemetryClient);
        _telemetryClient = telemetryClient;
    }

    public void RecordCycle(PipelineDiagnosticsDto diagnostics, TimeSpan cycleDuration)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        _telemetryClient.TrackMetric("signal_engine.pairs_evaluated", diagnostics.TotalPairsEvaluated);
        _telemetryClient.TrackMetric("signal_engine.pairs_passing", diagnostics.PairsPassing);
        _telemetryClient.TrackMetric("signal_engine.pairs_filtered_by_volume", diagnostics.PairsFilteredByVolume);
        _telemetryClient.TrackMetric("signal_engine.pairs_filtered_by_threshold", diagnostics.PairsFilteredByThreshold);
        _telemetryClient.TrackMetric("signal_engine.net_positive_below_threshold", diagnostics.NetPositiveBelowThreshold);
        _telemetryClient.TrackMetric("signal_engine.net_positive_below_edge_guardrail", diagnostics.NetPositiveBelowEdgeGuardrail);
        _telemetryClient.TrackMetric("signal_engine.pairs_filtered_by_break_even_size", diagnostics.PairsFilteredByBreakEvenSize);
        _telemetryClient.TrackMetric("signal_engine.best_raw_spread_per_hour", (double)diagnostics.BestRawSpread);
        _telemetryClient.TrackMetric("signal_engine.total_rates_loaded", diagnostics.TotalRatesLoaded);
        _telemetryClient.TrackMetric("signal_engine.rates_after_staleness_filter", diagnostics.RatesAfterStalenessFilter);
        _telemetryClient.TrackMetric("signal_engine.cycle_duration_ms", cycleDuration.TotalMilliseconds);
    }
}
