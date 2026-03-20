using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Application.Services;

public class SignalEngine : ISignalEngine
{
    private readonly IUnitOfWork _uow;

    /// <summary>
    /// Fallback round-trip fee constants used when Exchange.TakerFeeRate is not set in the DB.
    /// </summary>
    private static readonly Dictionary<string, decimal> FallbackRoundTripFees = new()
    {
        { "Hyperliquid", 0.00090m },
        { "Lighter",     0.00000m },
        { "Aster",       0.00080m }
    };

    public SignalEngine(IUnitOfWork uow) => _uow = uow;

    public async Task<List<ArbitrageOpportunityDto>> GetOpportunitiesAsync(CancellationToken ct = default)
    {
        var config = await _uow.BotConfig.GetActiveAsync();
        var latestRates = await _uow.FundingRates.GetLatestPerExchangePerAssetAsync();
        var rates = latestRates.Where(r => r.Asset is not null && r.Exchange is not null).ToList();

        // H3: Filter out stale rates (guard against zero — would filter everything)
        var stalenessMinutes = Math.Max(config.RateStalenessMinutes, 1);
        var cutoff = DateTime.UtcNow.AddMinutes(-stalenessMinutes);
        rates = rates.Where(r => r.RecordedAt >= cutoff).ToList();

        var opportunities = new List<ArbitrageOpportunityDto>();

        foreach (var group in rates.Where(r => r.Asset?.Symbol is not null).GroupBy(r => r.Asset!.Symbol))
        {
            var symbol    = group.Key;
            var assetRates = group.ToList();

            for (int i = 0; i < assetRates.Count; i++)
            for (int j = i + 1; j < assetRates.Count; j++)
            {
                var a = assetRates[i];
                var b = assetRates[j];

                var (longR, shortR) = a.RatePerHour <= b.RatePerHour ? (a, b) : (b, a);

                // M2: Skip opportunities where either leg has insufficient volume
                if (longR.Volume24hUsd < config.MinVolume24hUsdc || shortR.Volume24hUsd < config.MinVolume24hUsdc) continue;

                var diff = shortR.RatePerHour - longR.RatePerHour;

                // Use DB-stored TakerFeeRate when available; fall back to built-in constants.
                var longFee  = longR.Exchange.TakerFeeRate * 2
                               ?? FallbackRoundTripFees.GetValueOrDefault(longR.Exchange.Name, 0.001m);
                var shortFee = shortR.Exchange.TakerFeeRate * 2
                               ?? FallbackRoundTripFees.GetValueOrDefault(shortR.Exchange.Name, 0.001m);
                var amortHours = Math.Max(config.FeeAmortizationHours, 1);
                var feePerHour = (longFee + shortFee) / amortHours;
                var net = diff - feePerHour;

                if (net >= config.OpenThreshold)
                {
                    opportunities.Add(new ArbitrageOpportunityDto
                    {
                        AssetSymbol = symbol,
                        AssetId = longR.AssetId,
                        LongExchangeName = longR.Exchange.Name,
                        LongExchangeId = longR.ExchangeId,
                        ShortExchangeName = shortR.Exchange.Name,
                        ShortExchangeId = shortR.ExchangeId,
                        LongRatePerHour = longR.RatePerHour,
                        ShortRatePerHour = shortR.RatePerHour,
                        SpreadPerHour = diff,
                        NetYieldPerHour = net,
                        AnnualizedYield = net * 24m * 365m,
                        LongVolume24h = longR.Volume24hUsd,
                        ShortVolume24h = shortR.Volume24hUsd,
                        LongMarkPrice = longR.MarkPrice,
                        ShortMarkPrice = shortR.MarkPrice,
                    });
                }
            }
        }

        return [.. opportunities.OrderByDescending(o => o.NetYieldPerHour).Take(26)];
    }
}
