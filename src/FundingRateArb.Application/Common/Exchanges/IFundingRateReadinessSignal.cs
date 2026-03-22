namespace FundingRateArb.Application.Common.Exchanges;

/// <summary>
/// Signals when the first funding rate fetch has completed successfully.
/// BotOrchestrator waits on this before starting its trading cycle.
/// </summary>
public interface IFundingRateReadinessSignal
{
    /// <summary>
    /// Blocks until the first successful funding rate fetch completes,
    /// or the cancellation token is triggered, or the 120-second timeout elapses.
    /// </summary>
    Task WaitForReadyAsync(CancellationToken ct);

    /// <summary>
    /// Called by FundingRateFetcher after the first successful fetch.
    /// Idempotent — subsequent calls are no-ops.
    /// </summary>
    void SignalReady();
}
