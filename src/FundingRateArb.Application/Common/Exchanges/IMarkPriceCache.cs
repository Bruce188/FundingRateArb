namespace FundingRateArb.Application.Common.Exchanges;

/// <summary>
/// Singleton mark price cache shared across DI scopes.
/// Each exchange gets an isolated cache partition with its own TTL and thundering-herd prevention.
/// </summary>
public interface IMarkPriceCache
{
    Task<decimal> GetOrRefreshAsync(
        string exchangeName,
        string asset,
        Func<CancellationToken, Task<Dictionary<string, decimal>>> fetchFactory,
        CancellationToken ct = default);
}
