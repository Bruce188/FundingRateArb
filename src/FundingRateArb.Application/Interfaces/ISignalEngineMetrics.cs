using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;

namespace FundingRateArb.Application.Interfaces;

/// <summary>
/// Fire-and-forget signal-engine cycle telemetry. Implementations must not throw;
/// any internal failure should be caught and logged by the implementation. Called
/// at the end of each successful <see cref="ISignalEngine.GetOpportunitiesWithDiagnosticsAsync"/>
/// cycle so historical trends of pairs evaluated, pairs passing, best raw spread,
/// cycle duration, and filter counts are queryable in the telemetry backend.
/// </summary>
public interface ISignalEngineMetrics
{
    /// <summary>
    /// Record a completed signal-engine cycle.
    /// </summary>
    /// <param name="diagnostics">Aggregated diagnostics from the just-completed cycle.</param>
    /// <param name="cycleDuration">Wall-clock duration of the cycle (DB load through opportunity sort).</param>
    void RecordCycle(PipelineDiagnosticsDto diagnostics, TimeSpan cycleDuration);
}
