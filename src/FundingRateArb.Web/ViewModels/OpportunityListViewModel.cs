using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Web.ViewModels;

public class OpportunityListViewModel
{
    public List<ArbitrageOpportunityDto> Opportunities { get; set; } = [];

    /// <summary>Notional size per leg in USD (capital × leverage).</summary>
    public decimal NotionalPerLeg { get; set; }

    /// <summary>Fraction of 24h volume used for liquidity sizing.</summary>
    public decimal VolumeFraction { get; set; }

    /// <summary>Hard ceiling on leverage from admin config.</summary>
    public int MaxLeverageCap { get; set; }

    /// <summary>Returns true when the opportunity would be volume-constrained.</summary>
    public bool IsVolumeConstrained(ArbitrageOpportunityDto opp)
    {
        if (VolumeFraction <= 0)
        {
            return false;
        }

        var minVol = Math.Min(opp.LongVolume24h, opp.ShortVolume24h);
        return minVol * VolumeFraction < NotionalPerLeg;
    }
}
