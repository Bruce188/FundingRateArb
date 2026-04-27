using System;
using System.Collections.Generic;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.ViewModels;

public class StatusViewModel
{
    public bool DatabaseAvailable { get; set; } = true;
    public string? DegradedReason { get; set; }

    public BotStateHeader BotState { get; set; } = new();
    public List<PnlAttributionWindowDto> PnlAttribution { get; set; } = new();
    public List<HoldTimeBucketDto> HoldTimeBuckets { get; set; } = new();
    public PhantomFeeIndicator PhantomFee { get; set; } = new();
    public List<PerPairPnlRow> PerPairPnl { get; set; } = new();
    public List<PerAssetFeeDrag> PerAssetFeeDrag { get; set; } = new();
    public List<FailedOpenEventDto> FailedOpenEvents { get; set; } = new();
    public SkipReasonHistogram SkipReasons { get; set; } = new();
    public ReconciliationSnapshot? Reconciliation { get; set; }
}

public class BotStateHeader
{
    public bool IsEnabled { get; set; }
    public DateTime LastUpdatedAt { get; set; }
    public decimal OpenThreshold { get; set; }
    public decimal CloseThreshold { get; set; }
    public decimal AlertThreshold { get; set; }
    public decimal StopLossPct { get; set; }
    public int OpenConfirmTimeoutSeconds { get; set; }
    public int MinHoldTimeHours { get; set; }
    public decimal EmergencyCloseSpreadThreshold { get; set; }
    public int DefaultLeverage { get; set; }
    public int MaxLeverageCap { get; set; }
    public decimal TotalCapitalUsdc { get; set; }
    public decimal MinViableOpenThreshold { get; set; }
    public bool MinViableOpenThresholdViolated { get; set; }
}

public class PhantomFeeIndicator
{
    public int EmergencyClosedZeroFill24h { get; set; }
    public int EmergencyClosedZeroFill7d { get; set; }
    public int FailedNullOrderId24h { get; set; }
}

public class PerPairPnlRow
{
    public string LongExchangeName { get; set; } = string.Empty;
    public string ShortExchangeName { get; set; } = string.Empty;
    public decimal TotalPnl { get; set; }
    public int PositionCount { get; set; }
}

public class PerAssetFeeDrag
{
    public string AssetSymbol { get; set; } = string.Empty;
    public decimal TotalPnl { get; set; }
    public decimal AvgFees { get; set; }
    public decimal AvgFunding { get; set; }
    public decimal FeeDragRatio { get; set; }
    public int CloseCount { get; set; }
}

public class SkipReasonHistogram
{
    public bool Available { get; set; }
    public int TotalPairsEvaluated { get; set; }
    public int PairsFilteredByVolume { get; set; }
    public int PairsFilteredByThreshold { get; set; }
    public int PairsFilteredByExchangeSymbolCap { get; set; }
    public int PairsFilteredByBreakeven { get; set; }
    public int PairsFilteredByTrendUnconfirmed { get; set; }
    public int PairsFilteredByEmptyBook { get; set; }
    public int PairsPassing { get; set; }
}

public class ReconciliationSnapshot
{
    public ReconciliationReport Report { get; set; } = null!;
    public Dictionary<string, decimal>? PerExchangeEquity { get; set; }
    public List<string>? DegradedExchanges { get; set; }
    public bool PerExchangeEquityMalformed { get; set; }
    public bool DegradedExchangesMalformed { get; set; }
}
