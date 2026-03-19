using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Application.Services;

public class SignalEngine : ISignalEngine
{
    private readonly IUnitOfWork _uow;

    private static readonly Dictionary<string, decimal> RoundTripFees = new()
    {
        { "Hyperliquid", 0.00090m },
        { "Lighter",     0.00000m },
        { "Aster",       0.00080m }
    };

    public SignalEngine(IUnitOfWork uow) => _uow = uow;

    public async Task<List<ArbitrageOpportunityDto>> GetOpportunitiesAsync()
    {
        var config = await _uow.BotConfig.GetActiveAsync();
        var rates = await _uow.FundingRates.GetLatestPerExchangePerAssetAsync();

        var opportunities = new List<ArbitrageOpportunityDto>();

        foreach (var group in rates.GroupBy(r => r.Asset.Symbol))
        {
            var symbol    = group.Key;
            var assetRates = group.ToList();

            for (int i = 0; i < assetRates.Count; i++)
            for (int j = i + 1; j < assetRates.Count; j++)
            {
                var a = assetRates[i];
                var b = assetRates[j];

                var (longR, shortR) = a.RatePerHour <= b.RatePerHour ? (a, b) : (b, a);
                var diff = shortR.RatePerHour - longR.RatePerHour;
                var feePerHour = (RoundTripFees.GetValueOrDefault(longR.Exchange.Name, 0.001m)
                    + RoundTripFees.GetValueOrDefault(shortR.Exchange.Name, 0.001m)) / 24m;
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

        return [.. opportunities.OrderByDescending(o => o.NetYieldPerHour)];
    }
}
