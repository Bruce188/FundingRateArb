namespace FundingRateArb.Application.DTOs;

public class ArbitrageOpportunityDto
{
    public string AssetSymbol { get; set; } = null!;
    public int AssetId { get; set; }
    public string LongExchangeName { get; set; } = null!;
    public int LongExchangeId { get; set; }
    public string ShortExchangeName { get; set; } = null!;
    public int ShortExchangeId { get; set; }
    public decimal LongRatePerHour { get; set; }
    public decimal ShortRatePerHour { get; set; }
    public decimal SpreadPerHour { get; set; }
    public decimal NetYieldPerHour { get; set; }
    public decimal AnnualizedYield { get; set; }
    public decimal LongVolume24h { get; set; }
    public decimal ShortVolume24h { get; set; }
    public decimal LongMarkPrice { get; set; }
    public decimal ShortMarkPrice { get; set; }
}
