namespace FundingRateArb.Domain.Entities;

public class ExchangeAssetConfig
{
    public int Id { get; set; }
    public int ExchangeId { get; set; }
    public int AssetId { get; set; }
    public int SizeDecimals { get; set; }
    public decimal MinOrderSize { get; set; }
    public decimal StepSize { get; set; }
    public int PriceDecimals { get; set; }

    public Exchange Exchange { get; set; } = null!;
    public Asset Asset { get; set; } = null!;
}
