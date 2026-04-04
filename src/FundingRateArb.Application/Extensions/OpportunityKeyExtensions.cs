using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Extensions;

public static class OpportunityKeyExtensions
{
    public static string OpportunityKey(this ArbitrageOpportunityDto o)
        => $"{o.AssetId}_{o.LongExchangeId}_{o.ShortExchangeId}";

    public static string PositionKey(this ArbitragePosition p)
        => $"{p.AssetId}_{p.LongExchangeId}_{p.ShortExchangeId}";
}
