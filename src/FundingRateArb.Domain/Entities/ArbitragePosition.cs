using System.ComponentModel.DataAnnotations;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Domain.Entities;

public class ArbitragePosition
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public int AssetId { get; set; }
    public int LongExchangeId { get; set; }
    public int ShortExchangeId { get; set; }
    public decimal SizeUsdc { get; set; }
    public decimal MarginUsdc { get; set; }
    public int Leverage { get; set; }
    public decimal LongEntryPrice { get; set; }
    public decimal ShortEntryPrice { get; set; }
    public decimal? LongLiquidationPrice { get; set; }
    public decimal? ShortLiquidationPrice { get; set; }
    public decimal EntrySpreadPerHour { get; set; }
    public decimal CurrentSpreadPerHour { get; set; }
    public decimal AccumulatedFunding { get; set; } = 0m;
    public decimal EntryFeesUsdc { get; set; }
    public decimal ExitFeesUsdc { get; set; }
    public DateTime? ClosingStartedAt { get; set; }
    public decimal? RealizedPnl { get; set; }
    public PositionStatus Status { get; set; } = PositionStatus.Opening;
    public CloseReason? CloseReason { get; set; }

    /// <summary>PnL reported by the exchange (informational — never overwrites local RealizedPnl).</summary>
    public decimal? ExchangeReportedPnl { get; set; }

    /// <summary>Percentage divergence between local and exchange PnL: (local - exchange) / |exchange| * 100.</summary>
    public decimal? PnlDivergence { get; set; }

    /// <summary>Current cross-exchange price divergence percentage, updated each health check.</summary>
    public decimal? CurrentDivergencePct { get; set; }

    /// <summary>Previous cycle's cross-exchange price divergence percentage. Null on first cycle.</summary>
    public decimal? PrevDivergencePct { get; set; }

    /// <summary>Total funding payments reported by the exchange for this position.</summary>
    public decimal? ExchangeReportedFunding { get; set; }

    /// <summary>When reconciliation was last performed against exchange data.</summary>
    public DateTime? ReconciledAt { get; set; }

    public bool LongLegClosed { get; set; }
    public bool ShortLegClosed { get; set; }

    /// <summary>True if this position was opened in dry-run (paper trading) mode.</summary>
    public bool IsDryRun { get; set; }

    /// <summary>True if entry/exit fees were backfilled via the phantom-fee correction path.</summary>
    public bool IsPhantomFeeBackfill { get; set; }

    [MaxLength(200)]
    public string? LongOrderId { get; set; }

    [MaxLength(200)]
    public string? ShortOrderId { get; set; }

    /// <summary>Actual filled quantity on the long exchange (for audit trail).</summary>
    public decimal? LongFilledQuantity { get; set; }

    /// <summary>Actual filled quantity on the short exchange (for audit trail).</summary>
    public decimal? ShortFilledQuantity { get; set; }

    /// <summary>
    /// Price at which the long leg closed. Populated by PositionCloser on the retry round
    /// that actually closes the long leg (may be a different round than the short leg).
    /// Consumed by the PnL computation path so multi-round closes reconstruct the full
    /// price-PnL component even when FinalizeClosedPositionAsync runs in a later cycle.
    /// Null for positions closed before this field was added or positions still open.
    /// </summary>
    public decimal? LongExitPrice { get; set; }

    /// <summary>Quantity actually filled when the long leg closed. See <see cref="LongExitPrice"/>.</summary>
    public decimal? LongExitQty { get; set; }

    /// <summary>
    /// Price at which the short leg closed. Populated by PositionCloser on the retry round
    /// that actually closes the short leg. See <see cref="LongExitPrice"/> for semantics.
    /// </summary>
    public decimal? ShortExitPrice { get; set; }

    /// <summary>Quantity actually filled when the short leg closed. See <see cref="ShortExitPrice"/>.</summary>
    public decimal? ShortExitQty { get; set; }

    /// <summary>Long-leg intended mid price at order-submit time (from GetMarkPriceAsync pre-flight).
    /// Null for positions opened before this field was added or when the mid was unavailable.</summary>
    public decimal? LongIntendedMidAtSubmit { get; set; }

    /// <summary>Short-leg intended mid price at order-submit time. See <see cref="LongIntendedMidAtSubmit"/>.</summary>
    public decimal? ShortIntendedMidAtSubmit { get; set; }

    /// <summary>Long-leg entry slippage as a fraction: (LongEntryPrice - LongIntendedMidAtSubmit) / LongIntendedMidAtSubmit.
    /// Null when either the intended mid or the actual fill price was not captured.</summary>
    public decimal? LongEntrySlippagePct { get; set; }

    /// <summary>Short-leg entry slippage. See <see cref="LongEntrySlippagePct"/>.</summary>
    public decimal? ShortEntrySlippagePct { get; set; }

    /// <summary>Long-leg exit slippage. Null for the stub leg of a single-leg close round (FilledPrice = 0).</summary>
    public decimal? LongExitSlippagePct { get; set; }

    /// <summary>Short-leg exit slippage. See <see cref="LongExitSlippagePct"/>.</summary>
    public decimal? ShortExitSlippagePct { get; set; }

    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// UTC timestamp when both exchange legs confirmed execution.
    /// Null for historical positions (pre-migration) or while still in Opening status.
    /// Display as: ConfirmedAtUtc ?? OpenedAt.
    /// </summary>
    public DateTime? ConfirmedAtUtc { get; set; }

    /// <summary>
    /// UTC timestamp when the both-leg confirmation window completed successfully.
    /// Null for rows still in pending-confirm (Opening) state or for rows that failed
    /// the confirmation window and were marked Failed with ReconciliationDrift.
    /// </summary>
    public DateTime? OpenConfirmedAt { get; set; }

    public DateTime? ClosedAt { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public ApplicationUser User { get; set; } = null!;
    public Asset Asset { get; set; } = null!;
    public Exchange LongExchange { get; set; } = null!;
    public Exchange ShortExchange { get; set; } = null!;
    public ICollection<Alert> Alerts { get; set; } = [];

    /// <summary>
    /// Directional (price-based) PnL component for normally-closed positions, derived from the
    /// RealizedPnl accounting identity: pricePnl = RealizedPnl - AccumulatedFunding + total fees.
    /// This separates the directional component from funding for the three-view closed PnL
    /// model defined in Analysis Section 4.3.3: directional + funding - fees = realized.
    /// Returns null while the position is still open, if RealizedPnl has not been set, or
    /// for emergency-closed positions — EmergencyCloseHandler writes RealizedPnl as just
    /// -(entryFee + exitFee) without incorporating accumulated funding, so the identity
    /// backsolve would produce a fabricated directional component (-AccumulatedFunding)
    /// that misrepresents the price legs which were never marketed-fill.
    /// </summary>
    public decimal? RealizedDirectionalPnl
    {
        get
        {
            if (RealizedPnl is null || Status == PositionStatus.EmergencyClosed)
            {
                return null;
            }
            return RealizedPnl.Value - AccumulatedFunding + EntryFeesUsdc + ExitFeesUsdc;
        }
    }

    /// <summary>
    /// Total fees paid across entry and exit for both legs (sum of EntryFeesUsdc and
    /// ExitFeesUsdc). Convenience field for the three-view closed PnL display.
    /// </summary>
    public decimal TotalFeesUsdc => EntryFeesUsdc + ExitFeesUsdc;
}
