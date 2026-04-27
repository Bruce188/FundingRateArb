using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Domain.Entities;

public class BotConfiguration : IValidatableObject
{
    public int Id { get; set; }
    public bool IsEnabled { get; set; }
    public BotOperatingState OperatingState { get; set; } = BotOperatingState.Stopped;
    [Range(0.0, 0.01)]
    public decimal OpenThreshold { get; set; } = 0.0005m;

    [Range(0.0, 0.01)]
    public decimal AlertThreshold { get; set; } = 0.0001m;

    /// <remarks>
    /// Close fires when spread/h drops below this magnitude (negative number).
    /// A value of -0.00005 means the close triggers when the hourly spread falls below -0.005%.
    /// </remarks>
    [Range(-0.01, 0.0)]
    public decimal CloseThreshold { get; set; } = -0.0002m;

    [Range(0.01, 1.0)]
    public decimal StopLossPct { get; set; } = 0.10m;

    [Range(1, 168)]
    public int MaxHoldTimeHours { get; set; } = 48;

    /// <summary>Minimum hours to hold before allowing SpreadCollapsed close. StopLoss always applies.
    /// Capped at 48h regardless of MaxHoldTimeHours — validated by ConfigValidator.</summary>
    [Range(0, 48)]
    public int MinHoldTimeHours { get; set; } = 4;

    [Range(0.0001, 0.1)]
    public decimal VolumeFraction { get; set; } = 0.001m;

    [Range(0.01, 1.0)]
    public decimal MaxCapitalPerPosition { get; set; } = 0.90m;

    [Range(1, 168)]
    public int BreakevenHoursMax { get; set; } = 8;

    /// <remarks>
    /// Deprecated. Sizing logic now reads live equity from
    /// <c>IBalanceAggregator.GetBalanceSnapshotAsync</c> instead of this static field.
    /// Field is retained for EF schema and seed compatibility only — do not consume in new code.
    /// </remarks>
    [Obsolete("Sizing now reads live equity from IBalanceAggregator. Field retained for schema/seed compatibility only — do not consume in new code.", error: false)]
    [Range(0.01, (double)decimal.MaxValue)]
    public decimal TotalCapitalUsdc { get; set; } = 39m;

    [Range(1, 125)]
    public int DefaultLeverage { get; set; } = 5;

    [Range(1, 100)]
    public int MaxConcurrentPositions { get; set; } = 1;

    public AllocationStrategy AllocationStrategy { get; set; } = AllocationStrategy.Concentrated;

    [Range(1, 20)]
    public int AllocationTopN { get; set; } = 3;

    // Risk management (decoupled from MaxHoldTimeHours)
    [Range(1, 168)]
    public int FeeAmortizationHours { get; set; } = 12;

    [Range(1, (double)decimal.MaxValue)]
    public decimal MinPositionSizeUsdc { get; set; } = 10m;

    [Range(0, (double)decimal.MaxValue)]
    public decimal MinVolume24hUsdc { get; set; } = 50_000m;

    [Range(1, 120)]
    public int RateStalenessMinutes { get; set; } = 15;

    [Range(0.01, 1.0)]
    public decimal DailyDrawdownPausePct { get; set; } = 0.08m;

    [Range(1, 20)]
    public int ConsecutiveLossPause { get; set; } = 3;

    /// <summary>
    /// Minutes before next funding settlement to boost opportunity scoring.
    /// Opportunities within this window receive a 20% effective yield boost for allocation.
    /// </summary>
    [Range(1, 60)]
    public int FundingWindowMinutes { get; set; } = 10;

    /// <summary>Max fraction of live aggregated capital (from IBalanceAggregator) in any single asset (e.g., 0.5 = 50%).</summary>
    [Range(0.01, 1.0)]
    public decimal MaxExposurePerAsset { get; set; } = 0.5m;

    /// <summary>Max fraction of live aggregated capital (from IBalanceAggregator) on any single exchange (e.g., 0.7 = 70%).</summary>
    [Range(0.01, 1.0)]
    public decimal MaxExposurePerExchange { get; set; } = 0.7m;

    /// <summary>Close position when AccumulatedFunding >= TargetPnlMultiplier * estimated_entry_fees.</summary>
    [Range(0.5, 100.0)]
    public decimal TargetPnlMultiplier { get; set; } = 2.0m;

    /// <summary>Enable PnL-target exit (when false, only MaxHoldTimeHours applies).</summary>
    public bool AdaptiveHoldEnabled { get; set; } = true;

    /// <summary>Enable automatic portfolio rebalancing.</summary>
    public bool RebalanceEnabled { get; set; }

    /// <summary>Minimum spread improvement (per hour) to justify closing an existing position.</summary>
    [Range(0.0, 0.01)]
    public decimal RebalanceMinImprovement { get; set; } = 0.0002m;

    /// <summary>Maximum number of rebalance closes per bot cycle to prevent runaway liquidation.</summary>
    [Range(1, 20)]
    public int MaxRebalancesPerCycle { get; set; } = 2;

    /// <summary>Number of consecutive failures before a per-exchange circuit breaker opens.</summary>
    [Range(1, 20)]
    public int ExchangeCircuitBreakerThreshold { get; set; } = 3;

    /// <summary>Minutes an exchange is excluded after the circuit breaker opens.</summary>
    [Range(1, 120)]
    public int ExchangeCircuitBreakerMinutes { get; set; } = 15;

    /// <summary>Minimum minutes before PnlTargetReached can fire. Prevents premature close before funding accrues.</summary>
    [Range(0, 1440)]
    public int MinHoldBeforePnlTargetMinutes { get; set; } = 60;

    /// <summary>Minutes a pair is excluded from re-entry after closing at PnL target. Prevents immediate re-open.</summary>
    [Range(5, 1440)]
    public int PnlTargetCooldownMinutes { get; set; } = 30;

    /// <summary>
    /// Unified-PnL tolerance for PnlTargetReached gate. The position will not auto-close at PnL target
    /// when unifiedPnl &lt; -PnlTargetUnifiedTolerance, even if totalPnl &gt; 0. Default 0m = strict
    /// (unified PnL must be non-negative). Raise to permit closing when unified PnL is slightly negative.
    /// </summary>
    [Range(0, (double)decimal.MaxValue)]
    public decimal PnlTargetUnifiedTolerance { get; set; } = 0m;

    /// <summary>Spread/hr below which SpreadCollapsed bypasses MinHoldTimeHours. Deeply negative = emergency.</summary>
    [Range(-1.0, 0)]
    public decimal EmergencyCloseSpreadThreshold { get; set; } = -0.001m;

    /// <summary>Consecutive price feed failures before force-closing position. Prevents unprotected positions.</summary>
    [Range(5, 100)]
    public int PriceFeedFailureCloseThreshold { get; set; } = 10;

    /// <summary>Basis points subtracted from net yield to account for slippage/market impact.</summary>
    [Range(0, 50)]
    public int SlippageBufferBps { get; set; } = 5;

    /// <summary>
    /// Lighter DEX order slippage floor (default 0.75%). Replaces hardcoded 0.5%.
    /// Must not exceed <see cref="LighterSlippageMaxPct"/>; cross-property constraint is enforced
    /// at runtime by <c>ISlippageConfigurable.ConfigureSlippage</c> which throws <see cref="ArgumentException"/>
    /// when floor &gt; max.
    /// </summary>
    [Range(0.001, 0.05)]
    public decimal LighterSlippageFloorPct { get; set; } = 0.0075m;

    /// <summary>
    /// Lighter DEX maximum adaptive slippage cap (default 3%). Replaces hardcoded 2%.
    /// Must be at least as large as <see cref="LighterSlippageFloorPct"/>.
    /// </summary>
    [Range(0.005, 0.10)]
    public decimal LighterSlippageMaxPct { get; set; } = 0.03m;

    /// <summary>
    /// Threshold above which entry slippage triggers an AlertType.HighSlippageWarning alert.
    /// Default 0.001 = 0.1%. First-cut: single global threshold; future work could add
    /// per-exchange-pair overrides. AVOID list explicitly forbids using this as an entry-side
    /// REJECTION gate without prior backtest validation — this is informational alerting only.
    /// </summary>
    [Range(0.0001, 0.01)]
    public decimal MaxAcceptableSlippagePct { get; set; } = 0.001m;

    /// <summary>Fraction of distance-to-liquidation at which to close. 0.5 = close when 50% of the safe range remains (i.e. half consumed).</summary>
    [Range(0.1, 0.9)]
    public decimal LiquidationWarningPct { get; set; } = 0.50m;

    /// <summary>
    /// Fraction of distance-to-liquidation at which to *alert* (not close). Default 0.75 =
    /// warn when 75% of the safe range remains (i.e. 25% consumed). Must be greater than
    /// <see cref="LiquidationWarningPct"/>, otherwise the warning fires after the close.
    /// </summary>
    [Range(0.1, 0.99)]
    public decimal LiquidationEarlyWarningPct { get; set; } = 0.75m;

    /// <summary>Number of bot cycles between exchange position reconciliation checks.</summary>
    [Range(1, 100)]
    public int ReconciliationIntervalCycles { get; set; } = 10;

    /// <summary>
    /// Seconds to wait for both-leg confirmation after the sequential open flow completes.
    /// If either leg has not confirmed within this window, the confirmed leg is rolled back
    /// via ClosePositionAsync and the position is marked Failed with ReconciliationDrift.
    /// Values &lt;= 0 are rejected; the effective range is [5, 300] as enforced by <see cref="RangeAttribute"/>.
    /// </summary>
    [Range(5, 300)]
    public int OpenConfirmTimeoutSeconds
    {
        get => _openConfirmTimeoutSeconds;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(OpenConfirmTimeoutSeconds),
                    value, "OpenConfirmTimeoutSeconds must be greater than zero.");
            }

            _openConfirmTimeoutSeconds = value;
        }
    }

    private int _openConfirmTimeoutSeconds = 30;

    /// <summary>Alert when price divergence exceeds this multiple of entry spread cost. Default 2.0.</summary>
    [Range(0.5, 10.0)]
    public decimal DivergenceAlertMultiplier { get; set; } = 2.0m;

    /// <summary>
    /// Number of consecutive cycles the divergence must exceed the alert threshold before an alert fires.
    /// Default 1 preserves existing behavior (immediate alert). Set to 3+ in production for debounce.
    /// </summary>
    [Range(1, 20)]
    public int DivergenceAlertConfirmationCycles { get; set; } = 1;

    /// <summary>
    /// Horizon in hours over which a new rotation opportunity must outperform the divergence exit cost.
    /// Suppresses rotation when divergence exit cost exceeds net yield advantage over this window.
    /// </summary>
    [Range(0.25, 24.0)]
    public decimal RotationDivergenceHorizonHours { get; set; } = 2.0m;

    /// <summary>
    /// When true, SpreadCollapsed close bypasses MinHoldTimeHours when divergence is narrowing.
    /// Enables a soft-close preference that exits positions recovering from price divergence.
    /// </summary>
    public bool PreferCloseOnDivergenceNarrowing { get; set; } = true;

    /// <summary>
    /// When true (default), DivergenceCritical close only fires when the position is past
    /// its MinHoldTimeHours AND the liquidation distance has fallen below
    /// LiquidationEarlyWarningPct. Set to false to revert to the pre-fix behavior where
    /// any divergence breach fires an immediate close regardless of hold time or liquidation.
    /// </summary>
    public bool UseRiskBasedDivergenceClose { get; set; } = true;

    /// <summary>
    /// When true, SignalEngine applies a stricter break-even-size filter: opportunities
    /// are rejected when net yield × MinHoldTimeHours cannot cover MinEdgeMultiplier × fees.
    /// Distinct from the existing edge-guardrail check which uses FeeAmortizationHours as
    /// its denominator. Default is false — operators must opt in via admin UI because the
    /// filter is strictly more aggressive and can reject opportunities the bot would have
    /// previously admitted.
    /// <para>
    /// Magnitude example: at the bot defaults (MinHoldTimeHours=2, FeeAmortizationHours=12,
    /// MinEdgeMultiplier=3) this floor is ~6× stricter than the legacy edge-guardrail
    /// (1.5× totalEntryCost/hr vs 0.25× totalEntryCost/hr). The majority of marginal
    /// historical opportunities will be rejected after flipping this flag. Recommended:
    /// raise MinHoldTimeHours toward FeeAmortizationHours before enabling, or lower
    /// MinEdgeMultiplier, and validate against historical backtest results first.
    /// </para>
    /// <para>
    /// When MinHoldTimeHours=0 with this flag enabled, the filter fails-closed (rejects
    /// everything) because zero worst-case hold means fees can never be amortized.
    /// </para>
    /// </summary>
    public bool UseBreakEvenSizeFilter { get; set; }

    /// <summary>Consecutive snapshots with positive funding spread required before entry.</summary>
    [Range(1, 20)]
    public int MinConsecutiveFavorableCycles { get; set; } = 3;

    /// <summary>Consecutive negative-spread cycles before triggering FundingFlipped close.</summary>
    [Range(1, 20)]
    public int FundingFlipExitCycles { get; set; } = 2;

    /// <summary>USDT/USDC spread percentage at which to issue a warning alert.</summary>
    [Range(0.0, 5.0)]
    public decimal StablecoinAlertThresholdPct { get; set; } = 0.3m;

    /// <summary>USDT/USDC spread percentage at which to close cross-stablecoin positions.</summary>
    [Range(0.0, 10.0)]
    public decimal StablecoinCriticalThresholdPct { get; set; } = 1.0m;

    /// <summary>Enable dry-run (paper trading) mode globally.</summary>
    public bool DryRunEnabled { get; set; }

    /// <summary>
    /// When true, skip sequential execution path and use Task.WhenAll even for estimated-fill exchanges.
    /// Risk: if on-chain tx fails, one-sided position requires emergency close.
    /// Default false — sequential remains safe default until exchange reliability is validated.
    /// </summary>
    public bool ForceConcurrentExecution { get; set; }

    /// <summary>Hard ceiling on effective leverage across all exchanges. Overrides exchange-reported max.</summary>
    [Range(1, 50)]
    public int MaxLeverageCap { get; set; } = 3;

    /// <summary>Fraction of margin utilization that triggers an alert (e.g. 0.70 = 70%).</summary>
    [Range(0.1, 0.95)]
    public decimal MarginUtilizationAlertPct { get; set; } = 0.70m;

    /// <summary>
    /// Multiplier applied to amortized entry cost to compute the minimum net yield a
    /// candidate opportunity must exceed before it's considered actionable. Per
    /// Appendix B of the analysis, the industry best-practice guardrail is 3× entry cost.
    /// Set to &lt;= 1 to disable the guardrail (e.g., for backtests).
    /// </summary>
    [Range(0.0, 20.0)]
    public decimal MinEdgeMultiplier { get; set; } = 3m;

    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    public string UpdatedByUserId { get; set; } = null!;

    /// <inheritdoc/>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (LighterSlippageFloorPct > LighterSlippageMaxPct)
        {
            yield return new ValidationResult(
                $"LighterSlippageFloorPct ({LighterSlippageFloorPct}) must not exceed LighterSlippageMaxPct ({LighterSlippageMaxPct}).",
                [nameof(LighterSlippageFloorPct), nameof(LighterSlippageMaxPct)]);
        }
    }
}
