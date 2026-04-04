using System.ComponentModel.DataAnnotations;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Domain.Entities;

public class Exchange
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = null!;

    [Required, MaxLength(255)]
    public string ApiBaseUrl { get; set; } = null!;

    [Required, MaxLength(255)]
    public string WsBaseUrl { get; set; } = null!;

    public FundingInterval FundingInterval { get; set; }
    public int FundingIntervalHours { get; set; } = 1;
    public FundingSettlementType FundingSettlementType { get; set; } = FundingSettlementType.Continuous;
    public bool SupportsSubAccounts { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// True for data-only aggregators (e.g. CoinGlass) that provide funding rate data
    /// but cannot execute trades. These exchanges are auto-included in dashboard views
    /// but excluded from trading preferences and position opening.
    /// </summary>
    public bool IsDataOnly { get; set; }

    /// <summary>
    /// True for exchanges that are planned for future direct connector integration
    /// but not yet implemented (e.g. Binance, dYdX).
    /// </summary>
    public bool IsPlanned { get; set; }

    /// <summary>
    /// Taker fee rate as a fraction (e.g. 0.00045 = 0.045%). Null means use the built-in fallback.
    /// </summary>
    public decimal? TakerFeeRate { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    public ICollection<FundingRateSnapshot> FundingRateSnapshots { get; set; } = [];
    public ICollection<ArbitragePosition> LongPositions { get; set; } = [];
    public ICollection<ArbitragePosition> ShortPositions { get; set; } = [];
}
