namespace FundingRateArb.Application.DTOs;

/// <summary>
/// Per-pair slippage average over a rolling time window. Aggregated server-side via SQL GroupBy.
/// Avg fields are null when no rows in the group had non-null slippage data.
/// </summary>
public class PairSlippageRollupDto
{
    public string LongExchangeName { get; set; } = "?";
    public string ShortExchangeName { get; set; } = "?";
    public int PositionCount { get; set; }
    public decimal? AvgLongEntrySlippagePct { get; set; }
    public decimal? AvgShortEntrySlippagePct { get; set; }
    public decimal? AvgLongExitSlippagePct { get; set; }
    public decimal? AvgShortExitSlippagePct { get; set; }
}

/// <summary>
/// Per-asset slippage average over a rolling time window. Same shape as the pair rollup
/// but grouped by asset symbol.
/// </summary>
public class AssetSlippageRollupDto
{
    public string AssetSymbol { get; set; } = "?";
    public int PositionCount { get; set; }
    public decimal? AvgLongEntrySlippagePct { get; set; }
    public decimal? AvgShortEntrySlippagePct { get; set; }
    public decimal? AvgLongExitSlippagePct { get; set; }
    public decimal? AvgShortExitSlippagePct { get; set; }
}

/// <summary>
/// Combined slippage rollup returned by IPositionRepository.GetSlippageRollupAsync.
/// </summary>
public class SlippageRollupDto
{
    public List<PairSlippageRollupDto> ByPair { get; set; } = [];
    public List<AssetSlippageRollupDto> ByAsset { get; set; } = [];
}
