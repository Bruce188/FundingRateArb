# Prompts: Trading Pipeline Hardening

> **Plan**: `docs/plan-pipeline-hardening.md`
> **Branch**: `fix/trading-pipeline-hardening`

---

## Stream A — PnL & Funding Pipeline

> Model: Sonnet | Effort: high | Agent: worktree | /clear before starting

### Files to modify:
- `src/FundingRateArb.Domain/Entities/ArbitragePosition.cs`
- `src/FundingRateArb.Application/Services/ExecutionEngine.cs`
- `src/FundingRateArb.Application/Services/YieldCalculator.cs`
- `src/FundingRateArb.Infrastructure/BackgroundServices/FundingRateFetcher.cs`
- `src/FundingRateArb.Application/DTOs/OrderResultDto.cs`

### Instructions:

You are fixing the #1 systemic issue in this funding rate arbitrage bot: **PnL tracking is completely broken**. RealizedPnl omits trading fees AND accumulated funding (the primary profit source). AccumulatedFunding is never written by any code path.

**Read ALL files listed above before making changes.**

#### Task A1: Add fee tracking fields to ArbitragePosition
In `ArbitragePosition.cs`, add after `AccumulatedFunding` (line 20):
```csharp
public decimal EntryFeesUsdc { get; set; }
public decimal ExitFeesUsdc { get; set; }
public DateTime? ClosingStartedAt { get; set; }
```
The `ClosingStartedAt` field is for Stream C but adding it here avoids a second migration.

#### Task A2: Add IsEstimatedFill to OrderResultDto
In `OrderResultDto.cs`, add:
```csharp
public bool IsEstimatedFill { get; set; }
```

#### Task A3: Add fee helper + record entry fees in ExecutionEngine
In `ExecutionEngine.cs`:

1. Add a private static helper method:
```csharp
private static decimal GetTakerFeeRate(string exchangeName) => exchangeName switch
{
    "Hyperliquid" => 0.00045m,
    "Lighter" => 0m,
    "Aster" => 0.0004m,
    _ => 0.0005m,
};
```

2. In `OpenPositionAsync`, after the successful-open block where `LongOrderId` and `ShortOrderId` are set (around line 131-132), add:
```csharp
var longNotional = longResult!.FilledPrice * longResult.FilledQuantity;
var shortNotional = shortResult!.FilledPrice * shortResult.FilledQuantity;
position.EntryFeesUsdc = (longNotional * GetTakerFeeRate(opp.LongExchangeName))
                       + (shortNotional * GetTakerFeeRate(opp.ShortExchangeName));
```

3. If `IsEstimatedFill` is true on either result, log a warning:
```csharp
if (longResult.IsEstimatedFill || shortResult.IsEstimatedFill)
    _logger.LogWarning("Position #{Id} has estimated fill prices (Lighter) — PnL may be approximate", position.Id);
```

#### Task A4: Fix RealizedPnl calculation in ClosePositionAsync
In the successful-close block (around line 342-350), replace the PnL calculation:
```csharp
var longPnl  = (longClose.FilledPrice  - position.LongEntryPrice)  * longClose.FilledQuantity;
var shortPnl = (position.ShortEntryPrice - shortClose.FilledPrice) * shortClose.FilledQuantity;

// Record exit fees
var longCloseNotional = longClose.FilledPrice * longClose.FilledQuantity;
var shortCloseNotional = shortClose.FilledPrice * shortClose.FilledQuantity;
var longExName = position.LongExchange?.Name
    ?? (await _uow.Exchanges.GetByIdAsync(position.LongExchangeId))!.Name;
var shortExName = position.ShortExchange?.Name
    ?? (await _uow.Exchanges.GetByIdAsync(position.ShortExchangeId))!.Name;
position.ExitFeesUsdc = (longCloseNotional * GetTakerFeeRate(longExName))
                      + (shortCloseNotional * GetTakerFeeRate(shortExName));

// RealizedPnl = price PnL + funding collected - all fees
var pricePnl = longPnl + shortPnl;
position.RealizedPnl = pricePnl + position.AccumulatedFunding
                     - position.EntryFeesUsdc - position.ExitFeesUsdc;
```

#### Task A5: Build funding accumulation in FundingRateFetcher
In `FundingRateFetcher.cs`:

1. Add a new private method `UpdateAccumulatedFundingAsync`:
```csharp
private async Task UpdateAccumulatedFundingAsync(IUnitOfWork uow, CancellationToken ct)
{
    var openPositions = await uow.Positions.GetOpenTrackedAsync();
    if (openPositions.Count == 0) return;

    var latestRates = await uow.FundingRates.GetLatestPerExchangePerAssetAsync();

    foreach (var pos in openPositions)
    {
        var longRate = latestRates
            .FirstOrDefault(r => r.ExchangeId == pos.LongExchangeId && r.AssetId == pos.AssetId);
        var shortRate = latestRates
            .FirstOrDefault(r => r.ExchangeId == pos.ShortExchangeId && r.AssetId == pos.AssetId);

        if (longRate is null || shortRate is null) continue;

        var netRatePerHour = shortRate.RatePerHour - longRate.RatePerHour;
        var notional = pos.SizeUsdc * pos.Leverage;
        // Each cycle is ~60 seconds = 1/60 of an hour
        var fundingDelta = notional * netRatePerHour / 60m;
        pos.AccumulatedFunding += fundingDelta;
    }

    await uow.SaveAsync(ct);
}
```

2. Call it at the end of the `FetchAllAsync` method (or the main fetch loop), after rates have been saved:
```csharp
await UpdateAccumulatedFundingAsync(uow, ct);
```

#### Task A6: Fix YieldCalculator.UnrealizedPnl
In `YieldCalculator.cs`, in the `UnrealizedPnl` method, fix the fallback estimate to use notional:
```csharp
// Current (wrong — uses margin):
// return pos.SizeUsdc * pos.EntrySpreadPerHour * hoursOpen;
// Fixed (uses notional):
var notional = pos.SizeUsdc * pos.Leverage;
return notional * pos.EntrySpreadPerHour * hoursOpen;
```

#### Task A7: Create EF migration
Run:
```bash
cd src/FundingRateArb.Web
dotnet ef migrations add AddFeeTrackingAndClosingStartedAt -p ../FundingRateArb.Infrastructure
```

**Verify**: `dotnet build` succeeds with 0 errors, 0 warnings.

Before reporting done: stage all your changes and commit with message `wip: stream A — PnL & funding pipeline fixes`. If a pre-commit hook fails, fix the issue and retry the commit. Do NOT report done without a successful commit — uncommitted worktree changes are lost on cleanup.

---

## Stream B — Connector Safety

> Model: Sonnet | Effort: high | Agent: worktree | /clear before starting

### Files to modify:
- `src/FundingRateArb.Infrastructure/ExchangeConnectors/HyperliquidConnector.cs`
- `src/FundingRateArb.Infrastructure/ExchangeConnectors/AsterConnector.cs`
- `src/FundingRateArb.Infrastructure/ExchangeConnectors/LighterConnector.cs`
- `src/FundingRateArb.Infrastructure/ExchangeConnectors/MarkPriceCacheHelper.cs`
- `src/FundingRateArb.Application/Services/ExecutionEngine.cs`
- `src/FundingRateArb.Web/Program.cs`

### Instructions:

You are hardening the exchange connector layer to prevent financial loss from edge cases. **Read ALL files listed above before making changes.**

#### Task B1: Guard mark price = 0 in Hyperliquid + Aster
In `HyperliquidConnector.PlaceMarketOrderAsync`, after `var markPrice = await GetMarkPriceAsync(asset, ct);` (around line 60), add:
```csharp
if (markPrice <= 0)
    return new OrderResultDto { Success = false, Error = $"Mark price is zero or negative for {asset}" };
```
Same in `AsterConnector.PlaceMarketOrderAsync` after `var markPrice = await GetMarkPriceAsync(asset, ct);` (around line 92).

#### Task B2: Abort Aster order if SetLeverageAsync fails
In `AsterConnector.PlaceMarketOrderAsync`, change the leverage failure handling (around line 97-103) from a warning to an abort:
```csharp
var leverageResult = await _restClient.FuturesApi.Account.SetLeverageAsync(symbol, leverage, null, ct);
if (!leverageResult.Success)
{
    return new OrderResultDto
    {
        Success = false,
        Error = $"Failed to set leverage to {leverage}x on {symbol}: {leverageResult.Error?.Message ?? "unknown"}. Aborting order."
    };
}
```

#### Task B3: Zero-quantity guard in Hyperliquid + Aster
In `HyperliquidConnector.PlaceMarketOrderAsync`, after computing `quantity` (around line 71), add:
```csharp
if (quantity <= 0)
    return new OrderResultDto { Success = false, Error = $"Calculated quantity is zero for {asset} (size={sizeUsdc}, leverage={leverage}, mark={markPrice})" };
```
Same in `AsterConnector.PlaceMarketOrderAsync` after computing `quantity` (around line 94).

#### Task B4: Cap Hyperliquid close fallback quantity
In `HyperliquidConnector.ClosePositionAsync`, change the fallback (around line 123):
```csharp
// Old: quantity = decimal.MaxValue / 1_000_000m;
quantity = 1_000_000m; // Safe upper bound; reduceOnly=true caps to actual position
```

#### Task B5: Slippage protection on Hyperliquid
In `HyperliquidConnector.cs`, add a constant:
```csharp
private const decimal SlippagePct = 0.005m; // 0.5% max slippage
```
In `PlaceMarketOrderAsync`, before placing the order, compute a slippage-adjusted price:
```csharp
var limitPrice = side == Side.Long
    ? markPrice * (1 + SlippagePct)
    : markPrice * (1 - SlippagePct);
```
Use `limitPrice` instead of `markPrice` in the `PlaceOrderAsync` call's `price:` parameter.

Also in `ClosePositionAsync`, compute the slippage price for the close:
```csharp
var closingSide = side == Side.Long ? OrderSide.Sell : OrderSide.Buy;
var limitPrice = closingSide == OrderSide.Buy
    ? markPrice * (1 + SlippagePct)
    : markPrice * (1 - SlippagePct);
```

#### Task B6: Absolute order size cap in ExecutionEngine
In `ExecutionEngine.cs`, add a constant at class level:
```csharp
private const decimal MaxSingleOrderUsdc = 10_000m;
```
At the top of `OpenPositionAsync`, after `var config = await _uow.BotConfig.GetActiveAsync();`, add:
```csharp
if (sizeUsdc > MaxSingleOrderUsdc)
{
    _logger.LogCritical("Order size {Size:F2} exceeds safety cap {Max} for {Asset}",
        sizeUsdc, MaxSingleOrderUsdc, opp.AssetSymbol);
    return (false, $"Order size {sizeUsdc:F2} exceeds safety cap of {MaxSingleOrderUsdc} USDC");
}
```

#### Task B7: Validate fill quantities after open
In `ExecutionEngine.OpenPositionAsync`, after both legs succeed and entry prices are set (around line 132), add:
```csharp
var longQ = longResult!.FilledQuantity;
var shortQ = shortResult!.FilledQuantity;
if (longQ > 0 && shortQ > 0)
{
    var mismatchPct = Math.Abs(longQ - shortQ) / Math.Max(longQ, shortQ);
    if (mismatchPct > 0.05m)
    {
        _logger.LogWarning("Fill quantity mismatch: long={LongQ}, short={ShortQ} ({Pct:P1}) for {Asset}",
            longQ, shortQ, mismatchPct, opp.AssetSymbol);
        position.Notes = $"Quantity mismatch: long={longQ:F6}, short={shortQ:F6} ({mismatchPct:P1})";
    }
}
```

#### Task B8: Separate circuit breaker for close operations
In `Program.cs`, find where `"OrderExecution"` pipeline is registered. Add a new pipeline after it:
```csharp
options.AddPipeline("OrderClose", builder => builder
    .AddTimeout(TimeSpan.FromSeconds(30)));
```

In all three connectors' `ClosePositionAsync` methods:
- `HyperliquidConnector.ClosePositionAsync`: Change `_pipelineProvider.GetPipeline("OrderExecution")` to `_pipelineProvider.GetPipeline("OrderClose")`
- `AsterConnector.ClosePositionAsync`: Same change
- `LighterConnector.ClosePositionAsync`: Same change

#### Task B9: Lighter connector — mark fill prices as estimated
In `LighterConnector.PlaceMarketOrderAsync`, in the return statement, add:
```csharp
IsEstimatedFill = true,
```
Same in `LighterConnector.ClosePositionAsync`.

#### Task B10: Reduce mark price cache TTL
In `MarkPriceCacheHelper.cs`, reduce the cache expiry from 30 seconds to 10 seconds.

#### Task B11: Wrap Hyperliquid PlaceMarketOrderAsync in try-catch
Currently Hyperliquid can throw exceptions from SDK calls. Wrap the order placement in try-catch to return `OrderResultDto { Success = false }` consistently with other connectors:
```csharp
try
{
    // existing order placement code
}
catch (Exception ex)
{
    return new OrderResultDto { Success = false, Error = ex.Message };
}
```

**Verify**: `dotnet build` succeeds with 0 errors, 0 warnings.

Before reporting done: stage all your changes and commit with message `wip: stream B — connector safety hardening`. If a pre-commit hook fails, fix the issue and retry the commit. Do NOT report done without a successful commit — uncommitted worktree changes are lost on cleanup.

---

## Stream C — Orchestration & Monitoring Safety

> Model: Sonnet | Effort: high | Agent: worktree | /clear before starting

### Files to modify:
- `src/FundingRateArb.Application/Services/PositionHealthMonitor.cs`
- `src/FundingRateArb.Infrastructure/BackgroundServices/BotOrchestrator.cs`
- `src/FundingRateArb.Application/Services/PositionSizer.cs`
- `src/FundingRateArb.Application/Services/SignalEngine.cs`
- `src/FundingRateArb.Application/Services/ConfigValidator.cs`
- `src/FundingRateArb.Application/Services/ExecutionEngine.cs`
- `src/FundingRateArb.Web/Program.cs`

### Instructions:

You are fixing orchestration, monitoring, and validation safety issues. **Read ALL files listed above before making changes.**

#### Task C1: Fix Closing position reaper to use ClosingStartedAt
**Note**: Stream A adds `ClosingStartedAt` to `ArbitragePosition`. You can reference it — it will be there after merge.

In `ExecutionEngine.ClosePositionAsync`, where `position.Status = PositionStatus.Closing` is set (around line 207), add:
```csharp
position.ClosingStartedAt = DateTime.UtcNow;
```

In `PositionHealthMonitor.ReapStalePositionsAsync`, change the stale-time check. Currently it uses `p.OpenedAt < cutoff` for all statuses. Fix it:
```csharp
foreach (var pos in positions)
{
    // For Closing status, use ClosingStartedAt; for Opening, use OpenedAt
    var referenceTime = status == PositionStatus.Closing
        ? pos.ClosingStartedAt ?? pos.OpenedAt
        : pos.OpenedAt;

    if (referenceTime >= cutoff) continue;

    // ... existing reap logic
}
```

#### Task C2: Handle partial fill on close
In `ExecutionEngine.ClosePositionAsync`, in the successful-close block (after PnL computation, before setting Status = Closed), add a partial fill check:
```csharp
// Check for partial fills
var avgEntryPrice = (position.LongEntryPrice + position.ShortEntryPrice) / 2m;
if (avgEntryPrice > 0)
{
    var expectedQty = position.SizeUsdc * position.Leverage / avgEntryPrice;
    var longFillRatio = expectedQty > 0 ? longClose.FilledQuantity / expectedQty : 1m;
    var shortFillRatio = expectedQty > 0 ? shortClose.FilledQuantity / expectedQty : 1m;

    if (longFillRatio < 0.95m || shortFillRatio < 0.95m)
    {
        position.Status = PositionStatus.Closing;
        position.Notes = $"Partial close: long={longFillRatio:P0}, short={shortFillRatio:P0}. Retry next cycle.";
        _uow.Alerts.Add(new Alert
        {
            UserId = position.UserId,
            ArbitragePositionId = position.Id,
            Type = AlertType.SpreadWarning,
            Severity = AlertSeverity.High,
            Message = $"Partial close on {position.Asset?.Symbol ?? "unknown"}: long filled {longFillRatio:P0}, short filled {shortFillRatio:P0}. Position remains in Closing status.",
        });
        await _uow.SaveAsync(ct);
        return;
    }
}
```
Insert this BEFORE the line that sets `position.Status = PositionStatus.Closed`.

#### Task C3: Include Opening status in duplicate check
In `BotOrchestrator.RunCycleAsync`, find where `openPositions` is used to build the `openKeys` HashSet. Change it to also include Opening-status positions:
```csharp
var openingPositions = await uow.Positions.GetByStatusAsync(PositionStatus.Opening);
var allActiveKeys = openPositions
    .Concat(openingPositions)
    .Select(p => $"{p.AssetId}_{p.LongExchangeId}_{p.ShortExchangeId}")
    .ToHashSet();
```
Use `allActiveKeys` instead of `openKeys` in the duplicate check below.
Also update `slotsAvailable` to account for Opening positions:
```csharp
var slotsAvailable = config.MaxConcurrentPositions - openPositions.Count - openingPositions.Count;
```

#### Task C4: Subtract open positions from available capital
In `PositionSizer.CalculateBatchSizesAsync`, change the capital calculation (around line 25-26):
```csharp
var config = await _uow.BotConfig.GetActiveAsync();
var openPositions = await _uow.Positions.GetOpenAsync();
var allocatedCapital = openPositions.Sum(p => p.SizeUsdc);
var availableCapital = Math.Max(0, config.TotalCapitalUsdc - allocatedCapital);
var totalCapital = availableCapital * config.MaxCapitalPerPosition;
```
This ensures the sizer never over-allocates across multiple cycles.

#### Task C5: Expand ConfigValidator
In `ConfigValidator.cs`, add these validation rules:
```csharp
if (config.ConsecutiveLossPauseCount < 1)
    errors.Add("ConsecutiveLossPauseCount must be at least 1.");

if (config.CloseThreshold >= config.AlertThreshold)
    errors.Add("CloseThreshold must be less than AlertThreshold.");

if (config.DefaultLeverage < 1)
    errors.Add("DefaultLeverage must be at least 1.");

if (config.MaxHoldTimeHours < 1)
    errors.Add("MaxHoldTimeHours must be at least 1 hour.");

if (config.AllocationMode != AllocationStrategy.Concentrated
    && config.MaxCapitalPerPosition * config.MaxConcurrentPositions > 1.5m)
    errors.Add("MaxCapitalPerPosition × MaxConcurrentPositions exceeds 150% — risk of capital over-allocation.");
```

#### Task C6: Guard null Asset.Symbol in SignalEngine
In `SignalEngine.cs`, change the GroupBy line (around line 35) to filter nulls:
```csharp
foreach (var group in rates.Where(r => r.Asset?.Symbol is not null).GroupBy(r => r.Asset!.Symbol))
```

#### Task C7: Fix RecordCloseResult — only on successful close
In `BotOrchestrator.RunCycleAsync`, change the close loop (around line 146-149):
```csharp
foreach (var (pos, reason) in positionsToClose)
{
    await executionEngine.ClosePositionAsync(pos, reason, ct);
    // Only record PnL for circuit breaker if close actually completed
    if (pos.Status == PositionStatus.Closed && pos.RealizedPnl.HasValue)
        RecordCloseResult(pos.RealizedPnl.Value);
}
```

#### Task C8: Guard zero entry prices in PositionHealthMonitor
In `PositionHealthMonitor.CheckAndActAsync`, in the position loop, after fetching mark prices but before the PnL calculation (around line 77), add:
```csharp
if (pos.LongEntryPrice <= 0 || pos.ShortEntryPrice <= 0)
{
    _logger.LogCritical("Position #{Id} has zero entry prices — stop-loss check disabled, skipping", pos.Id);
    continue;
}
```

#### Task C9: Fix _lastAlertPushUtc initialization
In `BotOrchestrator.cs`, change line 38:
```csharp
private DateTime _lastAlertPushUtc = DateTime.UtcNow.AddMinutes(-5);
```

#### Task C10: Fix DataProtection key permissions
In `Program.cs`, find where `dpKeysDir` permissions are set. Change to set permissions unconditionally:
```csharp
var dpKeysDir = new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "dp-keys"));
if (!dpKeysDir.Exists) dpKeysDir.Create();
if (!OperatingSystem.IsWindows())
    dpKeysDir.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
```
Remove the `if (!dpKeysDir.Exists)` condition around the `UnixFileMode` assignment (it should always be set).

**Verify**: `dotnet build` succeeds with 0 errors, 0 warnings.

Before reporting done: stage all your changes and commit with message `wip: stream C — orchestration & monitoring safety`. If a pre-commit hook fails, fix the issue and retry the commit. Do NOT report done without a successful commit — uncommitted worktree changes are lost on cleanup.

---

## Stream D — Tests for Streams A-C

> Model: Sonnet | Effort: high | Agent: worktree | /clear before starting
> **DEPENDS ON**: Streams A, B, C must be merged first

### Files to modify:
- `tests/FundingRateArb.Tests.Unit/Services/ExecutionEngineTests.cs`
- `tests/FundingRateArb.Tests.Unit/Services/PositionHealthMonitorTests.cs`
- `tests/FundingRateArb.Tests.Unit/BackgroundServices/BotOrchestratorTests.cs`
- `tests/FundingRateArb.Tests.Unit/Services/PositionSizerTests.cs`
- `tests/FundingRateArb.Tests.Unit/Services/ConfigValidatorTests.cs`
- `tests/FundingRateArb.Tests.Unit/Services/YieldCalculatorTests.cs`
- `tests/FundingRateArb.Tests.Unit/BackgroundServices/FundingRateFetcherTests.cs` (new if needed)
- `tests/FundingRateArb.Tests.Unit/Connectors/HyperliquidConnectorTests.cs`
- `tests/FundingRateArb.Tests.Unit/Connectors/AsterConnectorTests.cs`

### Instructions:

You are writing tests for all the fixes from Streams A, B, and C. **Read each source file AND its existing test file before adding tests.** Follow the existing test patterns (Moq, xUnit, FluentAssertions or Assert).

#### D1: ExecutionEngine fee tracking tests (3 tests)
```
[Fact] OpenPosition_BothLegsSucceed_RecordsEntryFees
  - Setup: Hyperliquid long (0.00045 fee rate), Aster short (0.0004 fee rate)
  - Mock both legs returning FilledPrice=50000, FilledQuantity=0.1
  - Assert: position.EntryFeesUsdc == (50000*0.1*0.00045) + (50000*0.1*0.0004)
  - Assert: position.EntryFeesUsdc == 2.25 + 2.0 == 4.25

[Fact] ClosePosition_BothLegsSucceed_RealizedPnlIncludesFundingAndFees
  - Setup: position with EntryFeesUsdc=4.0, AccumulatedFunding=10.0
  - Mock close: longClose price 51000 (entry 50000), shortClose price 51000 (entry 50000)
  - longPnl = (51000-50000)*0.1 = 100, shortPnl = (50000-51000)*0.1 = -100, pricePnl = 0
  - exitFees estimated from fill notionals
  - Assert: RealizedPnl == 0 + 10.0 - 4.0 - exitFees (approximately 5.x)

[Fact] ClosePosition_BothLegsSucceed_RecordsExitFees
  - Assert ExitFeesUsdc > 0 after close
```

#### D2: ExecutionEngine order safety tests (3 tests)
```
[Fact] OpenPosition_ExceedsSafetyCap_Rejected
  - sizeUsdc = 15000
  - Assert: returns (false, error containing "safety cap")
  - Assert: no orders placed (mock verified)

[Fact] OpenPosition_FillQuantityMismatch_LogsWarning
  - Mock long: FilledQuantity=0.10, Mock short: FilledQuantity=0.08 (20% mismatch)
  - Assert: position.Notes contains "mismatch"

[Fact] ClosePosition_PartialFill_StaysInClosingStatus
  - Setup position with known entry prices and SizeUsdc * Leverage
  - Mock close: FilledQuantity = 50% of expected
  - Assert: position.Status == PositionStatus.Closing (not Closed)
  - Assert: alert created with "Partial close"
```

#### D3: Connector safety tests (6 tests)
```
[Fact] Hyperliquid_PlaceOrder_MarkPriceZero_ReturnsFalse
[Fact] Aster_PlaceOrder_MarkPriceZero_ReturnsFalse
[Fact] Aster_PlaceOrder_LeverageSetFails_ReturnsFalse
[Fact] Hyperliquid_PlaceOrder_ZeroQuantity_ReturnsFalse
[Fact] Aster_PlaceOrder_ZeroQuantity_ReturnsFalse
[Fact] Hyperliquid_ClosePosition_FallbackQuantity_IsCapped
  - Verify the fallback quantity is 1_000_000 (not decimal.MaxValue/1M)
```

#### D4: PositionHealthMonitor tests (6 tests)
```
[Fact] ReapStalePositions_ClosingStatus_UsesClosingStartedAt
  - Position with OpenedAt=2hrs ago, ClosingStartedAt=1min ago, Status=Closing
  - Assert: NOT reaped (ClosingStartedAt is recent)

[Fact] ReapStalePositions_ClosingStatus_ReapsWhenClosingStartedAtIsOld
  - Position with ClosingStartedAt=15min ago, Status=Closing, maxAge=10min
  - Assert: reaped to EmergencyClosed

[Fact] CheckAndAct_StopLoss_ExactBoundary_Triggers
  - unrealizedPnl = -(StopLossPct * MarginUsdc) exactly
  - Assert: position returned with CloseReason.StopLoss

[Fact] CheckAndAct_MaxHoldTime_ExactBoundary_Triggers
  - hoursOpen = MaxHoldTimeHours exactly
  - Assert: returned with CloseReason.MaxHoldTimeReached

[Fact] CheckAndAct_ZeroEntryPrices_SkipsPosition
  - Position with LongEntryPrice=0, ShortEntryPrice=0
  - Assert: position NOT in returned close list
  - Assert: LogCritical called

[Fact] CheckAndAct_PriceFeedFailure_5Consecutive_CreatesAlert
  - Mock GetMarkPriceAsync to throw 5 times
  - Assert: Alert with Type=PriceFeedFailure created after 5th failure
```

#### D5: BotOrchestrator tests (4 tests)
```
[Fact] RunCycle_DuplicateCheck_IncludesOpeningStatus
  - Setup: existing position in Opening status for asset X
  - Signal engine returns opportunity for same asset X
  - Assert: opportunity is skipped (not opened)

[Fact] RunCycle_RecordCloseResult_OnlyOnSuccessfulClose
  - Setup: health monitor returns position to close
  - Mock ClosePositionAsync but position stays in Closing (not Closed)
  - Assert: RecordCloseResult NOT called (or called with behavior that doesn't increment losses)

[Fact] RunCycle_BalanceExhaustion_BreaksLoop
  - Setup: 2 opportunities, first open fails with "Insufficient margin"
  - Assert: second opportunity NOT attempted

[Fact] RunCycle_FirstCycle_CatchesStartupAlerts
  - Create alert 3 minutes before orchestrator start
  - Assert: alert is included in first push (not missed due to _lastAlertPushUtc)
```

#### D6: PositionSizer tests (2 tests)
```
[Fact] CalculateBatchSizes_SubtractsOpenPositionCapital
  - Config: TotalCapitalUsdc=1000, MaxCapitalPerPosition=0.8
  - Open positions consuming 500 USDC
  - Assert: totalCapital = (1000-500)*0.8 = 400

[Fact] CalculateBatchSizes_EmptyOpportunities_ReturnsEmpty
  - Empty list input
  - Assert: returns empty array
```

#### D7: ConfigValidator tests (5 tests)
```
[Fact] Validate_ConsecutiveLossPauseCountZero_Invalid
[Fact] Validate_CloseThresholdAboveAlertThreshold_Invalid
[Fact] Validate_LeverageZero_Invalid
[Fact] Validate_MaxHoldTimeZero_Invalid
[Fact] Validate_CapitalOverAllocation_Invalid
  - MaxCapitalPerPosition=0.8, MaxConcurrentPositions=3, AllocationMode=EqualSpread
  - Assert: error contains "over-allocation"
```

#### D8: YieldCalculator tests (2 tests)
```
[Fact] UnrealizedPnl_NegativeAccumulatedFunding_ReturnsNegative
  - pos.AccumulatedFunding = -5.0m
  - Assert: returns -5.0m

[Fact] UnrealizedPnl_FallbackEstimate_UsesNotional
  - pos.AccumulatedFunding = 0, SizeUsdc=100, Leverage=5, EntrySpreadPerHour=0.001
  - Assert: result uses 100*5*0.001*hours (not 100*0.001*hours)
```

#### D9: Emergency close retry tests (2 tests)
```
[Fact] EmergencyClose_RetryOn_NoOpenPosition_ThenFails
  - Mock ClosePositionAsync: returns "No open position found" 3 times
  - Assert: alert created with "EMERGENCY CLOSE FAILED after 3 attempts"

[Fact] EmergencyClose_ExceptionDuringRetry_CreatesAlert
  - Mock ClosePositionAsync: throws on first attempt
  - Assert: alert created
```

#### D10: FundingRateFetcher test (2 tests)
```
[Fact] UpdateAccumulatedFunding_AccumulatesDelta
  - Open position with SizeUsdc=100, Leverage=5
  - Latest rates: shortRate=0.001/hr, longRate=0.0002/hr
  - Expected: notional=500, netRate=0.0008, delta=500*0.0008/60 = 0.00667
  - Assert: pos.AccumulatedFunding increased by ~0.00667

[Fact] UpdateAccumulatedFunding_SkipsMissingRates
  - Position with no matching rates
  - Assert: AccumulatedFunding unchanged
```

**Verify**: `dotnet test` — all tests pass.

Before reporting done: stage all your changes and commit with message `wip: stream D — tests for pipeline hardening`. If a pre-commit hook fails, fix the issue and retry the commit. Do NOT report done without a successful commit — uncommitted worktree changes are lost on cleanup.

---

## Stream E — Low-Priority Fixes (parallel with D)

> Model: Sonnet | Effort: medium | Agent: worktree | /clear before starting

### Files to modify:
- `src/FundingRateArb.Infrastructure/ExchangeConnectors/LighterConnector.cs`
- `src/FundingRateArb.Infrastructure/ExchangeConnectors/ExchangeConnectorFactory.cs`
- `src/FundingRateArb.Infrastructure/BackgroundServices/BotOrchestrator.cs`

### Instructions:

You are fixing low-priority issues. **Read each file before making changes.**

#### Task E1: Lighter estimated fill price documentation
In `LighterConnector.cs`, add XML comments to both `PlaceMarketOrderAsync` and `ClosePositionAsync` documenting that fill prices are estimates:
```csharp
/// <remarks>
/// IMPORTANT: Lighter's sendTx API returns only TxHash, not actual fill data.
/// FilledPrice and FilledQuantity are estimates based on mark price at submission time.
/// Actual fills may differ due to slippage. IsEstimatedFill is set to true.
/// </remarks>
```

#### Task E2: Replace HashSet with ConcurrentDictionary
In `BotOrchestrator.cs`, change:
```csharp
// Old:
private readonly HashSet<int> _pushedAlertIds = new();
// New:
private readonly ConcurrentDictionary<int, byte> _pushedAlertIds = new();
```
Update all usages:
- `.Add(id)` → `.TryAdd(id, 0)`
- `.Contains(id)` → `.ContainsKey(id)`
- `.Clear()` → `.Clear()`
- `.Count` → `.Count`
- Remove the `internal HashSet<int> PushedAlertIds` property or change its type

#### Task E3: ExchangeConnectorFactory startup validation
In `ExchangeConnectorFactory.cs`, add a method:
```csharp
public void ValidateRegistrations(IEnumerable<string> exchangeNames)
{
    foreach (var name in exchangeNames)
    {
        try { GetConnector(name); }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"Exchange '{name}' in database has no registered connector. " +
                "Check DI registration in Program.cs.", ex);
        }
    }
}
```
This can be called during app startup (optional — just add the method for now).

**Verify**: `dotnet build` succeeds with 0 errors, 0 warnings.

Before reporting done: stage all your changes and commit with message `wip: stream E — low-priority fixes`. If a pre-commit hook fails, fix the issue and retry the commit. Do NOT report done without a successful commit — uncommitted worktree changes are lost on cleanup.
