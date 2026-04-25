namespace FundingRateArb.Application.Services;

/// <summary>
/// Provides the evaluated capital amount (USDC) that the signal engine should use for
/// sizing and filtering. Returns <c>min(liveExchangeBalance, config.TotalCapitalUsdc)</c>
/// with a 30-second cache so the hot O(n²) loop avoids per-pair I/O.
/// </summary>
public interface ICapitalProvider
{
    /// <summary>
    /// Returns the evaluated capital in USDC. Cached for 30 seconds.
    /// Falls back to <c>config.TotalCapitalUsdc</c> when the balance aggregator is
    /// unavailable or returns zero, so the engine keeps running during an outage.
    /// </summary>
    Task<decimal> GetEvaluatedCapitalUsdcAsync(CancellationToken ct = default);
}
