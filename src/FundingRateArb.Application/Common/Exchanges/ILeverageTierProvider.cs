
using FundingRateArb.Domain.ValueObjects;

namespace FundingRateArb.Application.Common.Exchanges;

public interface ILeverageTierProvider
{
    Task<LeverageTier[]> GetTiersAsync(string exchangeName, string asset, CancellationToken ct = default);
    int GetEffectiveMaxLeverage(string exchangeName, string asset, decimal notionalUsdc);
    decimal GetMaintenanceMarginRate(string exchangeName, string asset, decimal notionalUsdc);
    void UpdateTiers(string exchangeName, string asset, LeverageTier[] tiers);
    bool IsStale(string exchangeName, string asset);
}
