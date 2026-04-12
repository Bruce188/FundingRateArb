# Feature: Dashboard Diagnostics Accuracy and PnL Transparency (2026-04-11)

## feat/dashboard-pnl-fixes

**Description:** Dashboard display-layer fixes: alert-template parity between Razor and JS, Best-Spread/hr tile accuracy, new-position yellow+PnL instant rendering, persistent exchange-balances tile with 60s refresh, divergence badge invariant tests, BotConfig admin form gap (19 missing fields) with fractional-percent UX rework, UserConfiguration MaxLeverageCap exposure, Positions/Details page revamp, OpenedAt local-time conversion, CloseReason surfacing with click-through navigation, PositionAnalysis decomposition view, and Exchange Analytics CoinGlass data path.

**Constraints:** No behavioural changes to SignalEngine or PositionHealthMonitor PnL math — display-layer, computation-timing, and form-exposure changes only. Preserve all existing unit tests (run dotnet test tests/FundingRateArb.Tests.Unit as a regression guard). Follow the conventions in .claude/CLAUDE.md. Dashboard JS edits retain the CSP-friendly DOM-construction style (no innerHTML). Close-positions investigation is tracked separately in docs/feature-close-positions-investigation-2026-04-12.md and is NOT in scope here.

**Objective**
Fix four dashboard display inconsistencies between the server-rendered Razor view and the client-side SignalR update path, make exchange balances permanently visible on the dashboard, and investigate a reported strategy-vs-exchange PnL discrepancy to (a) validate the divergence badge calculation and (b) surface concrete profitability-improvement levers.

### Run Log
**Run 2026-04-11T22:40:27Z:** Started pipeline (orchestrate.sh)
**Failed 2026-04-11T22:40:27Z:** analyze | Status: FAILED
**Evidence Source**
- User report 2026-04-11: dashboard diagnostics alert flips between a long 5-line Razor render and a short 3-line JS render on each SignalR push
- User report 2026-04-11: newly-opened positions take ~several seconds to display the yellow (`table-warning`) warning state and to populate Strategy PnL / Exchange PnL columns
- User report 2026-04-11: the Exchange Balances tile is hidden on initial load and only appears once the bot pushes a balance update from `BotOrchestrator`
- User report 2026-04-11: the "Best Spread/Hr" tile value does not match the widest spread in the Arbitrage Opportunities table
- User report 2026-04-11: observed position PnL display `strategy -$0.0271 / exchange -$0.3262 / divergence 0.37%` — user asks whether the badge is correctly calculated and how to be more profitable
- Live DB sample (2026-04-11): position #32 LIT Lighter/Hyperliquid with `size=27.08, leverage=3, notional=$81.24, divergence=0.2361%` — confirms typical position notional in the $40-$80 range
- Code references inline per issue below

---

### Run Log
**Run 2026-04-11T22:40:27Z:** Started pipeline (orchestrate.sh)
**Failed 2026-04-11T22:40:27Z:** analyze | Status: FAILED
### Issue 1: Diagnostics alert flickers between Razor and JS templates

#### Description
When the Arbitrage Opportunities table is empty and the `SignalEngine` returns diagnostics, two different templates render the same alert and produce different output. On initial page load the user sees the Razor (5-line) version; ~1s later the first SignalR `ReceiveDashboardUpdate` arrives and the JS rewrites the cell with a truncated (3-line) version. Every subsequent cycle rewrites the same cell, so the alert "flickers" and several informative lines disappear.

The specific lines that disappear on JS update:
1. Leading "N pairs rejected by profitability filters." summary count
2. "N above threshold but below the 3× edge guardrail — loosen MinEdgeMultiplier to surface." (visible when `NetPositiveBelowEdgeGuardrail > 0`)
3. "N filtered by break-even-size floor" (visible when `PairsFilteredByBreakEvenSize > 0`)

Additionally, the "N profitable (adaptive eligible)" label is misleading in both templates: it uses `NetPositiveBelowThreshold`, which counts pairs with net yield > 0 **but below `OpenThreshold`**. Those pairs are NOT passing opportunities — they're below-gate candidates that an adaptive-threshold engine might take. The current label sounds like "N opportunities are profitable right now" which is wrong when the main table is empty.

#### Root Cause (Code)
- `src/FundingRateArb.Web/Views/Dashboard/Index.cshtml:330-356` — Razor branch when `PairsPassing == 0` renders all 5 lines. Line 341 has the misleading label.
- `src/FundingRateArb.Web/wwwroot/js/dashboard.js:491-504` — JS branch only renders 3 lines; missing the leading rejection count, the edge-guardrail line, and the break-even-size line. Line 497 reproduces the same misleading label.

#### Required Fixes
1. Rewrite the JS branch at `dashboard.js:491-504` to mirror the Razor template structure, using DOM construction (no `innerHTML`). Render all 5 lines in the same order with the same conditional logic, referencing the same diagnostics fields:
   - `pairsFilteredByThreshold`, `netPositiveBelowThreshold`, `netPositiveBelowEdgeGuardrail`, `pairsFilteredByBreakEvenSize`
2. Update the "profitable (adaptive eligible)" label in BOTH templates to clearer wording: **`N net-positive below gate (adaptive eligible)`**. This makes the distinction between actionable opportunities (main table) and below-gate candidates (diagnostic hint) explicit.
   - `Index.cshtml:341`
   - `dashboard.js:497`

#### Required Tests
- A new DOM-level JS test (or integration test that renders the dashboard via Playwright) asserting that after a SignalR update, all 5 lines are present when the corresponding counters are non-zero.
- Regression guard: visually compare Razor HTML fragment to JS-generated HTML fragment for three diagnostics shapes: (a) only threshold, (b) threshold + edge guardrail, (c) all buckets populated.

#### Constraints
- Keep the DOM-construction style used elsewhere in `dashboard.js` (no `innerHTML` to preserve the CSP-friendliness the file already follows).
- Do NOT change the existing diagnostics DTO field set — this is a pure display-layer fix.

---
**Run 2026-04-11T22:40:27Z:** Started pipeline (orchestrate.sh)
**Failed 2026-04-11T22:40:27Z:** analyze | Status: FAILED
### Issue 2: Newly-opened positions take several seconds to show yellow state and PnL

#### Description
When a new arbitrage position opens, the frontend does receive a `ReceivePositionUpdate` SignalR event immediately and correctly creates a new row in the positions table via `createPositionRow` (`dashboard.js:298`). However the first push carries only skeletal data: `exchangePnl`, `unifiedPnl`, `divergencePct`, and `warningLevel` are all either zero/null because those fields are computed by `PositionHealthMonitor` on its own cycle, which has not yet run for the newly-created position. Result: the row first appears without the yellow `table-warning` class and with "$0.0000" for both PnL columns. A few seconds later, after the next `PositionHealthMonitor` cycle, a second SignalR push updates the existing row with real PnL and warning level — that's the delayed "yellow + filled-in" transition the user wants to be instant.

#### Root Cause (Code)
- `src/FundingRateArb.Infrastructure/Services/SignalRNotifier.cs:95-110` — position DTO is mapped via `ArbitragePositionMappingExtensions.ToDto` then optionally patched with PnL fields from a `pnl` parameter. When the caller doesn't supply `pnl`, the dto's PnL fields stay at their default mapping values (zero for non-closed positions: see `ArbitragePositionMappingExtensions.cs:26-28` which returns 0 for open positions).
- Callers of `PushPositionUpdateAsync` that fire on position CREATION do not pass computed PnL, because the computation lives in `PositionHealthMonitor.MonitorAllAsync()` which runs on its own ~5-10s cycle independent of position creation.
- `createPositionRow` at `dashboard.js:104-174` renders whatever it gets; warning level 0 → no `table-warning` class; PnL 0 → displays as `$0.0000`.

#### Required Fixes
Option B (preferred): **synchronously compute first-snapshot PnL and warning level inside the open-position push path.**

1. Extract the PnL computation in `PositionHealthMonitor` (lines ~204-305: `unrealizedPnl`, `unifiedUnrealizedPnl`, `CurrentDivergencePct`, warning level synthesis) into a reusable `ComputePositionSnapshotAsync(ArbitragePosition pos)` method on `IPositionHealthMonitor` that returns a `ComputedPositionPnl` + warning level.
2. In the position-open call path (likely `BotOrchestrator` or `ExecutionEngine` — verify before coding), after the DB insert and before `PushPositionUpdateAsync`, call `ComputePositionSnapshotAsync(newPos)` and pass the result into the notifier so the first push carries the full data.
3. If `ComputePositionSnapshotAsync` requires a live mark price that can't be fetched synchronously in the hot path without adding meaningful latency, fall back to option A: defer the first push until after the next `PositionHealthMonitor` cycle populates the row (visible lag stays, but UI never shows a half-populated row — same effective visible state as today but with a cleaner transition).

#### Required Tests
- Unit test: `PositionHealthMonitorTests.ComputePositionSnapshotAsync_PopulatesAllFields_ForFreshPosition`
- Integration test: opening a position and immediately checking the first SignalR push contains non-null `exchangePnl`, `unifiedPnl`, `divergencePct`, `warningLevel`
- Regression guard: the existing `PositionHealthMonitor` cycle must still populate the same fields via its normal path (don't double-compute)

#### Constraints
- Computing PnL requires current mark prices. Prefer the `IMarketDataCache` path (WebSocket cached, sub-second) over REST connector calls. If cache is cold, fall back gracefully rather than blocking the position-open path.
- Yellow state maps to `warningLevel === 2` → `table-warning` CSS class, confirmed by user. Do not change the mapping; only make sure the initial push carries the real level.

---
**Run 2026-04-11T22:40:28Z:** Started pipeline (orchestrate.sh)
**Failed 2026-04-11T22:40:28Z:** analyze | Status: FAILED
### Issue 3: Exchange Balances tile not always visible

#### Description
The `exchange-balances-row` element exists in the dashboard DOM but is hidden by default via `style="display:none;"` and is only revealed when a `ReceiveBalanceUpdate` SignalR event arrives with a non-empty `balances` array. The only publisher of this event is `BotOrchestrator.cs:770-771`, which calls `IBalanceAggregator.GetBalanceSnapshotAsync(userId)` and pushes via `_notifier.PushBalanceUpdateAsync(...)` — this only fires during a bot trading cycle. When the bot is stopped, or between cycles, the tile is invisible. User needs balances visible at all times as a persistent dashboard element.

#### Required Fixes
Two changes:

**A. Server-side initial load**
1. Add `BalanceSnapshotDto? InitialBalances { get; set; }` to `DashboardViewModel`.
2. In `DashboardController.Index`, after the existing parallel `Task.WhenAll` block (around line 141-174), add an additional `Task.Run` that scopes an `IBalanceAggregator` from `_scopeFactory` and calls `GetBalanceSnapshotAsync(userId, ct)`, writing the result into a captured `initialBalances` local. Assign into `DashboardViewModel.InitialBalances` when constructing the view model.
3. In `Views/Dashboard/Index.cshtml:207-219`:
   - Remove `style="display:none;"` from `#exchange-balances-row`.
   - Render the balance spans server-side from `Model.InitialBalances.Balances` inside `#exchange-balances`, and `Model.InitialBalances.TotalAvailableUsdc` into `#balance-total`.
   - Preserve the existing `id` attributes so the JS handler (`dashboard.js:679-706`) can still update them in place on subsequent SignalR pushes.
4. If `Model.InitialBalances` is null or empty (anonymous users, or a `BalanceAggregator` error), keep the tile visible but show a subdued placeholder like `"Balances: loading..."` instead of hiding it.

**B. Background refresh every 60s regardless of bot state**
5. Add a new `HostedService` (e.g. `BalanceRefreshService`) that periodically iterates all authenticated user IDs (or: the admin user for single-tenant deployments; verify multi-tenancy before choosing strategy), calls `IBalanceAggregator.GetBalanceSnapshotAsync`, and pushes via `ISignalRNotifier.PushBalanceUpdateAsync`. Interval: 60s, configurable. Register in `Program.cs` alongside other hosted services.
6. Ensure the service uses `IServiceScopeFactory` for per-tick scoping to avoid DbContext lifetime issues.

#### Required Tests
- Unit test: `DashboardController.Index` returns a view model with `InitialBalances` populated when the aggregator returns a snapshot.
- Unit test: `BalanceRefreshService` executes one cycle and pushes via the notifier.
- Integration test: initial page load renders non-empty `#exchange-balances` content without requiring a SignalR connection.

#### Constraints
- Do NOT make `BalanceAggregator.GetBalanceSnapshotAsync` hit exchanges synchronously on the hot dashboard path if it's slow. The existing implementation at `BalanceAggregator.cs` uses an internal cache (see line 35: `_cache.TryGetValue<BalanceSnapshotDto>(cacheKey, out var cached)`) — rely on the cache and only tolerate a cold-start miss gracefully.
- Per-user scoping: balance data is PII-adjacent. The `BalanceRefreshService` MUST push to the right SignalR group (`user-{userId}`) and MUST NOT leak other users' balances in a multi-tenant deployment.

---
**Run 2026-04-11T22:40:28Z:** Started pipeline (orchestrate.sh)
**Failed 2026-04-11T22:40:28Z:** analyze | Status: FAILED
### Issue 4: "Best Spread/Hr" tile doesn't match Arbitrage Opportunities table

#### Description
The "Best Spread/Hr" KPI tile at the top of the dashboard shows a value that does not correspond to the widest spread in the Arbitrage Opportunities table below it. Root cause: the server-side Razor and client-side JS compute the tile value from different sources.

- **Server render (accurate)**: `DashboardController.cs:78-80` and line 278 compute `BestSpread = opportunities.Max(o => o.SpreadPerHour) ?? diagnostics.BestRawSpread`. This is the widest spread among **passing opportunities** — matches the table.
- **JS update (inaccurate)**: `dashboard.js:465-467` always uses `diagnostics.bestRawSpread`, which is the widest raw spread across ALL pairs evaluated by `SignalEngine`, including pairs filtered by volume, break-even-hours, Aster notional caps, etc. This produces a number that is typically HIGHER than any row in the opportunities table and never appears there.

On page load the tile is correct (server render). On the first SignalR refresh the JS overwrites it with the unfiltered `bestRawSpread`, and it stays wrong until the next full page reload.

#### Required Fixes
Rewrite `dashboard.js:465-468` to mirror the server logic:
```js
var bestSpread = document.getElementById("best-spread");
if (bestSpread) {
    var bestFromOpps = opportunitiesArr.length > 0
        ? Math.max.apply(null, opportunitiesArr.map(function (o) { return o.spreadPerHour ?? 0; }))
        : (diagnostics && diagnostics.bestRawSpread) || 0;
    bestSpread.textContent = bestFromOpps > 0
        ? (bestFromOpps * 100).toFixed(4) + "%"
        : "N/A";
}
```

Keep the fallback to `diagnostics.bestRawSpread` only when `opportunitiesArr` is empty — this preserves the useful signal "no opportunities, but the widest thing we saw was X%" on empty-state days.

#### Required Tests
- Unit test (JS, via Jest or Playwright): given a SignalR payload with 3 opportunities and a higher `bestRawSpread` in diagnostics, the tile displays `max(opportunities.spreadPerHour)`, not `bestRawSpread`.
- Regression guard: if opportunities is empty, tile falls back to `bestRawSpread`.

#### Constraints
- Do NOT change the server-side `BestSpread` computation — it's already correct.
- Preserve the "N/A" fallback when both are zero/null.

---
**Run 2026-04-11T22:40:28Z:** Started pipeline (orchestrate.sh)
**Failed 2026-04-11T22:40:28Z:** analyze | Status: FAILED
### Issue 5: Investigate strategy-vs-exchange PnL gap; validate divergence badge; identify profitability levers

#### Description
User observed a position row showing `Strategy PnL -$0.0271 / Exchange PnL -$0.3262 / divergence badge 0.37%`. The Exchange PnL is ~12× worse than the Strategy PnL. User asks: (a) is the 0.37% divergence calculation actually correct, and (b) how can the bot be more profitable in the face of this kind of gap?

#### Preliminary Analysis (to be validated by the feature work)

**The Strategy-vs-Exchange gap is expected by construction, not a bug.** The two values are computed differently by design:

- **Exchange PnL** (`PositionHealthMonitor.cs:209-211`) values each leg against its **own exchange's mark price**:
  ```
  longPnl  = (longMarkLive  - longEntryPrice)  × qty
  shortPnl = (shortEntryPrice - shortMarkLive) × qty
  exchangePnl = longPnl + shortPnl
  ```
  This matches what each exchange's UI shows for your two separate positions.

- **Strategy/Unified PnL** (`PositionHealthMonitor.cs:213-222`) values BOTH legs against a single **unified reference price**:
  ```
  unifiedLongPnl  = (unifiedPrice - longEntryPrice)  × qty
  unifiedShortPnl = (shortEntryPrice - unifiedPrice) × qty
  unifiedPnl = unifiedLongPnl + unifiedShortPnl
  ```
  This is the true arb-strategy PnL: what you'd realize if both legs were marked at the same reference.

**The gap between them equals `(longMark - shortMark) × qty`** — pure algebra, no error possible. This is the "phantom PnL from cross-exchange price divergence". It collapses to zero when both exchanges agree on price, and widens when they don't.

**The divergence badge formula** (`PositionHealthMonitor.cs:268`):
```
divergencePct = |longMark - shortMark| / unifiedPrice × 100
```
Check against the user's observed numbers:
- Gap: `exchangePnl - unifiedPnl = -0.3262 - (-0.0271) = -0.2991`
- Typical position notional at current config: ~$80 (user's position #32 LIT was `size=27.08 × leverage=3 = $81.24`)
- Implied divergence: `0.2991 / 80 = 0.374%` ≈ observed `0.37%` ✓

**The badge IS mathematically correct.** At ~$80 notional, the 0.37% badge precisely explains the $0.30 gap between Exchange PnL and Strategy PnL.

#### Validation Tasks

**Part A: Confirm divergence badge correctness (likely closes as verified)**
1. Write a unit test in `PositionHealthMonitorTests` asserting that for synthetic inputs `longMark=100.50, shortMark=99.50, unifiedPrice=100.00`, the computed `CurrentDivergencePct` equals `1.00m` within epsilon.
2. Write a second unit test asserting that `(ExchangePnl - UnifiedPnl) / notional == sign × CurrentDivergencePct / 100` for a representative set of entry/mark price scenarios (long-favored, short-favored, symmetric). This is the invariant that makes the badge semantically consistent with the PnL gap.
3. Add a tooltip or label change on the badge in the dashboard view (find the badge render — likely in the positions table template — and clarify: "cross-exchange price divergence (|longMark − shortMark| / unified)"). Current label is ambiguous between "price divergence" and "spread divergence from entry".

**Part B: Audit why strategy PnL is near break-even (profitability investigation)**
Strategy PnL -$0.0271 on a ~$80 notional over a hold period is ~-0.034% — effectively break-even. This tells us the bot is entering positions where funding yield is barely covering fees. Investigate:

4. **Fee-optimal pairing**: query closed positions over the last N days and compute `unified_pnl / holdHours` for each pair. Are the winners concentrated on low-fee exchange pairs (Lighter as one leg, given its 0 bps taker + 15% rebate)? If yes, consider a post-scanner re-ranker that prefers lower-fee pairs among equal-net-yield candidates.

5. **Hold-time analysis**: compute the distribution of `hoursOpen` for closed positions vs. their entry net yield. Are positions being closed too early (before fees amortize) or too late (after funding flips negative)? `MinHoldTimeHours=2` is currently the floor. Is the bot actually reaching that floor, or cutting sooner on other close reasons?

6. **Close-reason breakdown**: group closed positions by `CloseReason` (schema is in `ArbitragePosition`). If most losses come from `StopLoss` or `SpreadCollapsed` rather than `MaxHoldTime`, the bot is being shaken out by volatility before the edge materialises — could indicate `StopLossPct=0.10` is too tight, or the adaptive close logic is too aggressive.

7. **Rebate wiring check**: verify `FundingRebateRate` is being applied correctly at `SignalEngine.cs:212-227`. The current code only applies Lighter's 15% rebate if the sign of the rate is in a specific direction. Confirm by hand that a Lighter-leg position in a typical funding environment is actually receiving the rebate boost in its net-yield calc, and that `breakEvenHours` accounts for it.

8. **Position sizing**: at `size=$27 leverage=3`, a position's fee bill is roughly `(fee_long_bps + fee_short_bps) × 2 × size × leverage`. For Lighter(0) + Hyperliquid(4.5bps) = 9 bps round-trip × $81 notional = $0.073 entry cost. The net hourly yield needs to cover that within `FeeAmortizationHours=24` → $0.003/hr. That's a 0.0037% per-hour net yield floor per dollar of notional — extremely tight. Investigate whether **larger position sizes** would amortize fees faster per notional dollar and thus tolerate lower raw spreads. (Trade-off: larger notional → more exchange-margin risk if divergence widens.)

9. **Divergence-driven premature close**: current `PositionHealthMonitor` logic closes positions when divergence exceeds a threshold multiple of entry spread cost. Check whether this is closing positions that would have recovered. Specifically, for each close with reason `DivergenceClose` (if one exists), compute what the PnL would have been 1h, 4h, 24h later using `FundingRateSnapshots` history — hindsight backtest on the close decision.

#### Required Outputs (as part of the feature work)
- A short `docs/analysis-pnl-and-profitability-2026-04-11.md` report summarising Parts A & B findings with concrete numbers from live data (top close reasons, top losing pairs, rebate correctness verification, hold-time distribution)
- Unit tests from Part A steps 1-2
- One actionable code change per Part B item that had a defensible finding. Items without enough evidence can be deferred; document the deferral explicitly.

#### Required Tests
- Divergence invariant tests per Part A
- Regression guards for any profitability-lever code changes (e.g. if position sizing logic changes, existing sizing tests must still pass)

#### Constraints
- No live-trading config changes (e.g. changing `StopLossPct`, `MinHoldTimeHours`) in this feature — any such change requires a separate plan with explicit risk review. This feature's Part B output is the evidence that would justify those changes.
- Do NOT modify the Exchange vs Strategy PnL computation. Both are correct — the fix is clearer labelling, not changing the math.

---
**Run 2026-04-11T22:40:28Z:** Started pipeline (orchestrate.sh)
**Failed 2026-04-11T22:40:28Z:** analyze | Status: FAILED
### Issue 6: BotConfig admin form is missing 19 entity fields + fractional-percent inputs are error-prone

#### Description
Audit of `BotConfiguration` entity vs the admin form reveals 19 properties that exist on the entity (with `[Range]` validation), are read by services, and have meaningful defaults — but have **no input in the admin form**. The only way to change them today is a direct SQL update against prod (as we did tonight for `MinEdgeMultiplier`). In addition, the form exposes several fractional-percent values as raw decimal inputs with `step="any"`, which forces users to count leading zeros (`0.00008` for `OpenThreshold = 0.008%/hr`) and is the reason you asked me "how do I change it?" for `MinEdgeMultiplier` when you could see the field in the DB.

#### Evidence Source
- Audit performed 2026-04-11 against `BotConfiguration.cs`, `BotConfigViewModel.cs`, `BotConfigController.cs`, and `BotConfig/Index.cshtml`.
- Entity has ~50 settable properties; ViewModel exposes 32; Form renders 32 inputs; gap = 19 fields.
- User report: "since we are working with fractions of percentages, it is very cumbersome to be counting all those 0 to make sure the correct value was set"

#### Root Cause — Missing fields (all on `BotConfiguration.cs` entity, none in viewmodel/form)
Required to add:
- `ExchangeCircuitBreakerThreshold` (line 101, `[Range(1,20)]`) — consecutive exchange failures before circuit opens
- `ExchangeCircuitBreakerMinutes` (line 105, `[Range(1,120)]`) — minutes exchange excluded after circuit opens
- `MinHoldBeforePnlTargetMinutes` (line 109, `[Range(0,1440)]`) — minutes before PnlTargetReached can fire
- `EmergencyCloseSpreadThreshold` (line 117, `[Range(-1.0,0)]`) — spread/hr below which SpreadCollapsed bypasses MinHoldTimeHours
- `PriceFeedFailureCloseThreshold` (line 121, `[Range(5,100)]`) — consecutive feed failures before force-close
- `SlippageBufferBps` (line 125, `[Range(0,50)]`) — bps subtracted from net yield
- `LiquidationWarningPct` (line 129, `[Range(0.1,0.9)]`) — distance-to-liq fraction to trigger close
- `LiquidationEarlyWarningPct` (line 137, `[Range(0.1,0.99)]`) — distance-to-liq fraction to trigger alert (must be > LiquidationWarningPct)
- `ReconciliationIntervalCycles` (line 141, `[Range(1,100)]`) — bot cycles between position reconciliation
- `DivergenceAlertMultiplier` (line 145, `[Range(0.5,10.0)]`) — divergence alert threshold as multiple of entry spread cost
- `UseRiskBasedDivergenceClose` (line 153, bool) — toggle for risk-gated divergence close (CRITICAL — see Issue 13)
- `UseBreakEvenSizeFilter` (line 175, bool) — toggle for stricter break-even-size floor
- `MinConsecutiveFavorableCycles` (line 179, `[Range(1,20)]`) — consecutive positive-spread cycles required before entry
- `FundingFlipExitCycles` (line 183, `[Range(1,20)]`) — consecutive negative cycles before FundingFlipped close
- `StablecoinAlertThresholdPct` (line 187, `[Range(0.0,5.0)]`) — USDT/USDC spread % for warning
- `StablecoinCriticalThresholdPct` (line 191, `[Range(0.0,10.0)]`) — USDT/USDC spread % for close
- `DryRunEnabled` (line 194, bool) — paper trading toggle
- `ForceConcurrentExecution` (line 201, bool) — skip sequential execution path for estimated-fill exchanges
- `MinEdgeMultiplier` (line 218, `[Range(0.0,20.0)]`) — minimum edge as multiple of amortized entry cost (the field that started this thread)

#### Root Cause — Fractional-percent UX
The following inputs use `type="number" step="any"` with raw decimals representing fractions of percents per hour. User must count leading zeros to get the right value:

| Field | Entity range | Typical value | How user has to type it today |
|---|---|---|---|
| `OpenThreshold` | 0.0001–0.1 | 0.008%/hr | `0.00008` |
| `CloseThreshold` | -0.01–0.1 | -0.005%/hr | `-0.00005` |
| `AlertThreshold` | 0.0001–0.1 | 0.006%/hr | `0.00006` |
| `RebalanceMinImprovement` | 0.0–0.01 | 0.02%/hr | `0.0002` |
| `VolumeFraction` | 0.00001–0.1 | 0.1% of vol | `0.001` |
| `EmergencyCloseSpreadThreshold` (new) | -1.0–0 | -0.1%/hr | `-0.001` |

`OpenThreshold`, `CloseThreshold`, `AlertThreshold` already have a partial live-helper at `wireThresholdHelper('OpenThreshold', 'open-threshold-pct')` that shows the computed percentage as the user types. But the *input* is still a raw decimal.

#### Required Fixes

**6A. Add missing fields to `BotConfigViewModel`, `BotConfigController` (both Edit and Save), and `BotConfig/Index.cshtml`.**
- Group by accordion section matching existing layout:
  - **Risk Management**: `MinHoldBeforePnlTargetMinutes`, `LiquidationWarningPct`, `LiquidationEarlyWarningPct`, `ExchangeCircuitBreakerThreshold`, `ExchangeCircuitBreakerMinutes`, `PriceFeedFailureCloseThreshold`
  - **Thresholds**: `EmergencyCloseSpreadThreshold`, `MinEdgeMultiplier`, `DivergenceAlertMultiplier`, `SlippageBufferBps`, `StablecoinAlertThresholdPct`, `StablecoinCriticalThresholdPct`, `MinConsecutiveFavorableCycles`, `FundingFlipExitCycles`
  - **Advanced (booleans)**: `UseRiskBasedDivergenceClose`, `UseBreakEvenSizeFilter`, `DryRunEnabled`, `ForceConcurrentExecution`
  - **Infrastructure**: `ReconciliationIntervalCycles`
- Each field needs `[Display(Name = "...")]` and matching form-text description matching the XML doc comment on the entity.
- Preserve the controller's validation-on-save pattern. Boolean fields use `form-check form-switch` (matching existing `AdaptiveHoldEnabled`).

**6B. Fractional-percent input rework — two-input toggle pattern.**

For each field in the table above, replace the single raw-decimal input with a **two-input group**:
```
[ 0.008  ] %/hr   [raw: 0.00008]
```
The primary input accepts percentage (e.g., `0.008`) and a small greyed-out "raw" readonly display shows the decimal form being submitted. Form submission sends the raw decimal (unchanged wire format), but the user only interacts with the percentage number.

Implementation approach:
- Add a small JS helper `bindPercentInput(percentInputId, rawHiddenInputId, percentLabel)` in `wwwroot/js/dashboard.js` or a new `wwwroot/js/admin-forms.js`.
- The form has two inputs per field: a `type="number" step="0.001"` visible input for the percentage, and a `type="hidden"` input bound to the viewmodel's raw-decimal field (`asp-for="OpenThreshold"`).
- On page load, compute the percentage from the hidden field and set the visible input.
- On visible-input change, multiply by 0.01 and write to the hidden input.
- ALSO show the hidden raw value as a subdued "(raw: 0.00008)" badge next to the input so power users can verify.
- Retain the existing `wireThresholdHelper` for server-computed "how much is this in percent terms" — it still serves as a sanity display on page load.

Alternative if the toggle feels too heavy: add a **basis-points input** (type="number", step="0.1") labeled "bps/hour" that converts on submit. 8 bps = 0.00008 is more intuitive than counting zeros. I'd propose the percent approach for thresholds (matches dashboard display), bps for `SlippageBufferBps` (already integer bps, trivial), and leave integer fields unchanged.

**6C. Form-wide input validation parity.**
Several fields have broader `[Range]` on the viewmodel than the entity (or vice versa). Normalise so form validation matches entity validation. Examples:
- Viewmodel `OpenThreshold` is `[Range(0.0001, 0.1)]` but entity has no `[Range]` attribute; both should align.
- Viewmodel `StopLossPct` is `[Range(0.05, 0.50)]` but entity is `[Range(0.01, 1.0)]`. Widen viewmodel to match entity or narrow entity — don't leave them inconsistent.

#### Required Tests
- Unit test: `BotConfigController.Save` round-trips every new field (persisted to DB, visible on re-load).
- Playwright test: load the admin form, verify every accordion section has the expected field count, save once without changing anything, confirm no field is zeroed.
- JS unit test: `bindPercentInput(...)` correctly translates 0.008 ↔ 0.00008 and handles edge cases (empty input, negative values, very small values like 0.0001%).

#### Constraints
- **Do NOT change the entity validation ranges** as a side effect — if a range needs widening, that's a separate risk decision (could allow dangerous configs).
- Preserve the operating state banner at the top of the form — don't re-layout just to fit new fields.
- Bool toggles that gate behaviour (`UseRiskBasedDivergenceClose`, `UseBreakEvenSizeFilter`, `DryRunEnabled`) must each have an explicit form-text warning matching the XML doc on the entity — these are risk toggles, not cosmetic ones.

---
**Run 2026-04-11T22:40:28Z:** Started pipeline (orchestrate.sh)
**Failed 2026-04-11T22:40:28Z:** analyze | Status: FAILED
### Issue 7: UserConfiguration has a quiet `MaxLeverageCap` gap

#### Description
Most of `UserConfiguration`'s 23 properties are correctly exposed in `SettingsController` / the user settings form. One exception: `MaxLeverageCap` (nullable int, `UserConfiguration.cs:23`) exists on the entity and is enforced at `ExecutionEngine` order placement (per the XML doc on line 18-21), but is NOT in `UserConfigViewModel` and NOT rendered in `Views/Settings/Configuration.cshtml`. A user cannot tighten their personal leverage cap below the global one via the UI.

#### Root Cause (Code)
- Entity property: `UserConfiguration.cs:22-23` — `[Range(1, 50)] public int? MaxLeverageCap { get; set; }`
- Missing from: `UserConfigViewModel` (per audit)
- Missing from: `SettingsController` Edit/Save action (per audit)
- Missing from: `Views/Settings/Configuration.cshtml` form

#### Required Fixes
1. Add `public int? MaxLeverageCap` to `UserConfigViewModel` with `[Range(1, 50)]` and `[Display(Name = "Max Leverage Cap (optional)")]`.
2. Wire it through `SettingsController` (load in GET, bind in POST, copy to entity).
3. Add a number input in the appropriate section of `Views/Settings/Configuration.cshtml` with form-text: "Optional per-user hard ceiling. Null = inherit from Bot Config. Can only be equal to or lower than the global cap."
4. Server-side validation: reject values that exceed the current `BotConfiguration.MaxLeverageCap`, with a clear error message.

#### Required Tests
- Unit test: saving a value > global cap returns validation error.
- Unit test: saving null clears the per-user override.
- Integration test: `ExecutionEngine` applies the lower of `user.MaxLeverageCap` and `bot.MaxLeverageCap` at order placement.

#### Constraints
- Do not allow raising the cap above the global value — the entity comment is explicit: "only downward".

---
**Run 2026-04-11T22:40:28Z:** Started pipeline (orchestrate.sh)
**Failed 2026-04-11T22:40:28Z:** analyze | Status: FAILED
### Issue 8: Positions/Details page duplicates the Index list — make it a real detail view

#### Description
`Views/Positions/Index.cshtml` already displays 12 key columns per row: Asset, Long Exchange, Short Exchange, Status, Size, Spread/hr, Strategy PnL, Exchange PnL, Divergence, Funding, Opened, Actions. Clicking the `Details` button navigates to `Positions/Details.cshtml` which shows substantially the same fields in a two-column card layout (`Details.cshtml:20-155`). The only genuinely new fields on the Details view are: Margin Utilization (lazy-loaded via exchange API, often fails silently), Duration Hours (trivially computed from OpenedAt), and Close Reason (only shown when the position is already closed). For an open position, clicking Details adds zero actionable insight.

#### Required Fixes
Rebuild `Views/Positions/Details.cshtml` as an actual detail view. New content blocks:

1. **Header card** — sticky, always visible: asset, exchange pair, status badge, opened timestamp (with timezone-fixed render per Issue 9), close reason if closed, hold time, a kill-switch "Force Close Now" button for open positions (admin only), and a revert link back to Positions list.
2. **PnL Decomposition card** — break down the position's PnL into (a) Directional component: `(long_mark − long_entry) × qty + (short_entry − short_mark) × qty` per leg, (b) Funding accrued: sum from `FundingRateSnapshots` joined through history, (c) Fees: entry fees from `Leverage × size × taker_rate × 2` plus projected exit, (d) **Net**. Show each with sign and color. This is the "where is my money going" answer the Analytics DTO already provides via `ToPnlDecomposition()` but is never rendered anywhere (see Issue 11).
3. **Cross-exchange divergence card** — current divergence %, divergence history chart (last N minutes via SignalR or an `/api/positions/{id}/divergence-history` endpoint), and the clear explanation "this is a phantom PnL that collapses when prices reconverge" matching what I documented in Issue 5 Part A. Text link to the full PnL methodology doc.
4. **Margin & Liquidation card** — exchange-reported PnL, exchange-reported liquidation price (pulled live with spinner, timeout gracefully), distance to liq expressed in both absolute price and `LiquidationWarningPct` terms, margin utilization per leg.
5. **Spread history sparkline** — entry spread, current spread, spread/hour trajectory for the position's lifetime, pulled from `FundingRateSnapshots`.
6. **Actions card** — (admin only) Force close, change allocation, kill-switch leg.

#### Required Tests
- Integration test: `PositionsController.Details(id)` returns a view with all 6 cards for a seeded open position.
- Rendering test (Playwright): the Details page visible-content count is > Index row count (must add real information, not duplicate).
- Unit test: PnL decomposition totals to the same number as `RealizedPnl` (closed) or `UnifiedPnl` (open) within epsilon.

#### Constraints
- Do not fetch live exchange data synchronously in the GET handler — lazy-load margin/liquidation data via an `/api/positions/{id}/live-margin` endpoint that the view calls after initial render. Never block the Details page load on a slow exchange API.
- Keep backward compat: the old layout's data is still available — this is an expansion, not a replacement of existing fields.
- PnL decomposition math MUST use the same formulas as `PositionHealthMonitor` — if any discrepancy is found, fail CI.

---
**Run 2026-04-11T22:40:28Z:** Started pipeline (orchestrate.sh)
**Failed 2026-04-11T22:40:28Z:** analyze | Status: FAILED
### Issue 9: "Opened At" timestamp bug — hardcoded "UTC" label, no local-time conversion

#### Description
The user reports the Opened At column is still wrong. Investigation found 5 render sites across the codebase, all with the same pattern:

```razor
<time datetime="@((pos.ConfirmedAtUtc ?? pos.OpenedAt).ToString("o"))" class="local-time">
    @((pos.ConfirmedAtUtc ?? pos.OpenedAt).ToString("yyyy-MM-dd HH:mm")) UTC
</time>
```

The `<time datetime="...">` attribute is a correct ISO-8601 UTC string — but the visible inner text is a hardcoded UTC-formatted string and the literal label "UTC" is appended regardless of the user's browser timezone. There is a CSS class `local-time` intended as a hook for JS to rewrite the text into the browser's local time, but no JS is actually doing the rewrite. Additionally, `Positions/Details.cshtml:61` uses a different format string (`"yyyy-MM-dd HH:mm:ss"`) than the other sites (`"yyyy-MM-dd HH:mm"`) for no good reason.

#### Render sites
- `Views/Positions/Index.cshtml:86`
- `Views/Positions/Details.cshtml:61` (inconsistent format)
- `Views/Dashboard/Index.cshtml:543` (mobile card, "MMM dd HH:mm" format)
- `Views/Dashboard/Index.cshtml:626` (desktop table)
- `Views/Analytics/PositionAnalysis.cshtml:32`

#### Required Fixes
1. Add a JS helper `rewriteLocalTimes()` in `wwwroot/js/shared-time.js` (new file) that:
   - Queries `document.querySelectorAll("time.local-time")`.
   - Reads `el.getAttribute("datetime")` (the ISO-8601 UTC value).
   - Computes the browser-local display using `Intl.DateTimeFormat` with the user's locale + a short-form option set.
   - Replaces `el.textContent` with the localised display + timezone abbreviation.
2. Call `rewriteLocalTimes()` on `DOMContentLoaded` AND on every SignalR dashboard update that could insert new `<time>` elements.
3. Normalise all 5 render sites to the same format string (`"yyyy-MM-dd HH:mm"` for tables, `"MMM dd HH:mm"` for mobile cards — no mixed `HH:mm:ss`).
4. Stop hardcoding "UTC" in the server-rendered text — the JS will replace it with the correct timezone label.

#### Required Tests
- Playwright test running in `Europe/Brussels` (user's zone): opens a position, verifies the Positions Index row displays the timestamp in local time not UTC.
- Playwright test in UTC: verifies the displayed time matches the server-rendered time (no drift).
- Unit test: `rewriteLocalTimes` gracefully handles invalid `datetime` attributes.

#### Constraints
- The server-rendered `datetime` attribute MUST remain ISO-8601 UTC — it's the source of truth. Only the visible text changes.
- No mutation of the DB — no `DateTimeKind` changes, no new timezone columns. Everything is display-layer.

---
**Run 2026-04-11T22:40:28Z:** Started pipeline (orchestrate.sh)
**Failed 2026-04-11T22:40:28Z:** analyze | Status: FAILED
### Issue 10: Failed-status clarity + CloseReason exposure + click-through navigation

#### Description
Three related UX problems:

**(a) Failed status is visually identical to EmergencyClosed / Liquidated.** `PositionStatus` enum has 7 values: `Opening, Open, Closing, Closed, EmergencyClosed, Liquidated, Failed`. The badge-class switch at `Views/Positions/Index.cshtml:51-58` only maps `Open → bg-success, Opening → bg-info, Closing → bg-warning`. All other statuses fall through to `bg-secondary` (gray) — so `Closed`, `EmergencyClosed`, `Liquidated`, and `Failed` look identical to the user. The Dashboard shows no status badge on positions at all.

**(b) CloseReason is hidden.** The `ArbitragePosition.CloseReason` field (enum with 14 values including `EmergencyLegFailed`, `DivergenceCritical`, `LiquidationRisk`, `StopLoss`, etc.) is only rendered on `Views/Positions/Details.cshtml:72-76` and only when the position is already closed. The Positions Index list has no CloseReason column. The Dashboard has no CloseReason. The `PositionSummaryDto` used by both views doesn't even carry the field. So a user seeing "Failed" on the Positions list has no way to know *why* it failed without navigating away and drilling into each position one at a time.

**(c) Click-through navigation is missing.** Clicking a position row (on either Dashboard or Positions list) doesn't navigate to that position's Details view. The user has to find the "Details" button in the actions column and click it precisely. Same for the Trade Analytics link — it should take you directly to the per-position `PositionAnalysis` view.

#### Required Fixes

**10A. Differentiate status badge colours for all 7 statuses:**
- `Opening` → `bg-info` (unchanged)
- `Open` → `bg-success` (unchanged)
- `Closing` → `bg-warning text-dark` (unchanged)
- `Closed` → `bg-secondary` (successful close — neutral gray)
- `EmergencyClosed` → `bg-warning text-dark` with bordered pattern
- `Liquidated` → `bg-danger` (red, prominent)
- `Failed` → `bg-danger text-white` with a distinct pattern or icon to differentiate from `Liquidated`

Apply in both `Positions/Index.cshtml` and `Positions/Details.cshtml` and the Dashboard's open-positions table.

**10B. Expose CloseReason everywhere a closed/failed position appears:**
1. Add `CloseReason? CloseReason { get; set; }` and `CloseReason_DisplayName` to `PositionSummaryDto`.
2. Update `ArbitragePositionMappingExtensions.ToSummaryDto()` to copy `pos.CloseReason`.
3. Add a new "Close Reason" column in `Views/Positions/Index.cshtml` (only visible when status is a closed/failed state, otherwise blank).
4. Add a `title`/tooltip on the status badge showing the CloseReason name + a 1-sentence explanation when hovered.
5. Dashboard table: add CloseReason as a secondary text line below the asset cell for any non-Open status.

**10C. Row-click navigation:**
1. Wrap each position row in `Views/Positions/Index.cshtml` with a click handler (or make the whole `<tr>` a link via a JS handler) that navigates to `Positions/Details/{id}`. Accessible via keyboard (`tabindex="0"` + `role="link"` + Enter key handler).
2. Same for the dashboard position rows.
3. A new "Analysis" button (icon link) next to the existing "Details" button that navigates to `Analytics/PositionAnalysis?id={id}`.
4. Add a `data-position-id` attribute on each row (already exists in some views via SignalR hooks — reuse it).

#### Required Tests
- Visual regression test: each of the 7 `PositionStatus` values has a distinct visible rendering.
- Unit test: `PositionSummaryDto.CloseReason` is populated correctly for closed positions.
- Playwright test: clicking a Positions Index row navigates to `/Positions/Details/{id}`.
- Playwright test: clicking "Analysis" from a position row navigates to the matching `/Analytics/PositionAnalysis/{id}`.

#### Constraints
- The whole-row-click must not conflict with the existing per-cell action buttons (Details, Analysis, Close) — clicks on those should still work independently.
- Keyboard accessibility is required (tabindex + Enter handler + visible focus state).

---
**Run 2026-04-11T22:40:28Z:** Started pipeline (orchestrate.sh)
**Failed 2026-04-11T22:40:28Z:** analyze | Status: FAILED
### Issue 11: PositionAnalysis revamp — current view is "what", needs to become "why"

#### Description
`Views/Analytics/PositionAnalysis.cshtml` currently renders 3 cards: a header, an "Actual vs Projected PnL" comparison, and a spread history table. It's a minimum-viable view but is missing most of what's needed for post-trade analysis.

Notably, `TradeAnalyticsService.GetPositionAnalyticsAsync()` already produces a rich DTO (`PositionAnalyticsDto`) that has more fields than the view renders. In particular, the PnL decomposition fields `Directional`, `Funding`, `Fees` (produced by a `ToPnlDecomposition()` extension on the analytics layer) are **computed but never rendered**.

#### Required Fixes

**11A. Expand the view to show:**
1. **PnL decomposition bar** — horizontal stacked bar showing Directional (usually near zero for delta-neutral), Funding (the intended profit), Fees (always negative), and Net as the sum. Colour-coded. This is the single most important chart — it tells you whether the arb "worked" (funding > fees) or not.
2. **Close reason & context** — display `CloseReason` prominently with a 1-paragraph contextual explanation of what that close reason means (e.g., "DivergenceCritical: the bot closed this position because cross-exchange prices diverged beyond the safety threshold. Note: this is a risk management action, not necessarily an indicator of strategy failure — see Issue 13 in the feature file for the ongoing investigation into whether DivergenceCritical closes are firing prematurely.")
3. **Per-leg realized PnL** — breakdown of the long leg's PnL and the short leg's PnL separately, using both the exchange-reported values AND the unified-price values, with the gap highlighted.
4. **Entry vs exit analysis** — entry spread/hr, exit spread/hr, spread evolution over hold, and the projected ideal exit timing (max net yield point in the spread history).
5. **Target vs actual** — what was the entry-time projected return (from `ProjectedPnl`)? How does that compare to actual? What percentage of target was achieved? Red if < 0%, yellow if 0-50%, green if > 50%.
6. **Hold time analysis** — hours held, fee amortization period (from the `BotConfiguration.FeeAmortizationHours`), whether the hold time exceeded the break-even point.
7. **Counterfactual: hypothetical PnL at +1h, +4h, +24h, +MaxHoldTime past close** — using spread history, show what the unified PnL would have been if the position had been held longer. This is critical for Issue 13's divergence-close investigation.

**11B. Entry point linking:** add an "Analyse" button from the Positions list and from the Dashboard position rows (per Issue 10).

#### Required Tests
- Unit test: `ToPnlDecomposition()` is called and its output is rendered in the view.
- Rendering test: for a seeded closed position with known PnL history, each of the 7 cards renders with the expected values.
- Counterfactual calculation unit test: the "hypothetical +24h PnL" computation matches the formula `sum(historical_spread × qty × Δt) − fees` for the period after close.

#### Constraints
- The counterfactual computation requires historical `FundingRateSnapshots` data — make sure the query is bounded by the position's close time and a reasonable window (not unbounded).
- Do not modify the `PositionAnalyticsDto` schema if possible — the missing fields should already be computable from existing fields. If not, add them deliberately with DB migration review.

---
**Run 2026-04-11T22:40:28Z:** Started pipeline (orchestrate.sh)
**Failed 2026-04-11T22:40:28Z:** analyze | Status: FAILED
### Issue 12: Exchange Analytics page is empty — data path not populated

#### Description
`Areas/Admin/Views/ExchangeAnalytics/Index.cshtml` renders 4 sections (Exchange Overview, Top Opportunities, Rate Comparison, Discovery Feed), all sourced from `ICoinGlassAnalyticsRepository`. All 4 sections display "No CoinGlass data available yet" when the backing table `CoinGlassExchangeRates` is empty. Investigation confirms the table IS empty or near-empty because:

1. CoinGlass was missing from the `Exchanges` table until earlier today (the `AnyAsync` seeder bug I fixed). CoinGlass was backfilled via the SQL script run in this session.
2. The background service that populates `CoinGlassExchangeRates` from the CoinGlass API may never have been running for this deployment — it requires the `ExchangeConnectors:CoinGlass:ApiKey` config to be set, and the existing `appsettings.json` has it as empty string.
3. Even with the key set, the fetch service may have been gated by "only run if CoinGlass exchange row exists" logic that never passed because of (1).

#### Required Fixes

**12A. Verify the data flow**
1. Find the background service (likely `CoinGlassFetcher` or similar) that populates `CoinGlassExchangeRates`. Locate its registration in `Program.cs` and its configuration dependency.
2. Check whether the CoinGlass API key is provided via Azure Key Vault (`ExchangeConnectors--CoinGlass--ApiKey`). If not, add it as a required secret and document it.
3. Verify the service's trigger condition — if it checks "CoinGlass exchange exists in DB", the check is now satisfied (post-backfill).

**12B. Add an admin "seed test data" button**
Add a button on `Areas/Admin/Views/ExchangeAnalytics/Index.cshtml` for admin-only: "Trigger manual CoinGlass fetch now". Wires to a new `ExchangeAnalyticsController.TriggerFetch(CancellationToken)` action that invokes the fetcher's public fetch method directly (not waiting for the next periodic cycle).

**12C. Alternative: fall back to using live exchange connectors**
If CoinGlass is unreliable or the API key is unavailable, the Exchange Analytics page could fall back to using live `FundingRateSnapshots` data from the bot's own connectors (Binance, dYdX, etc.). This gives a smaller universe but always has fresh data. Keep CoinGlass as an optional overlay for cross-reference.

#### Required Tests
- Integration test: after seeding 10 `CoinGlassExchangeRates` rows, each of the 4 ExchangeAnalytics sections renders with non-empty data.
- Unit test: `ExchangeAnalyticsController.Index` handles both empty and populated data paths without throwing.
- Manual verification: trigger the fetcher, confirm data lands in `CoinGlassExchangeRates`, confirm the UI updates.

#### Constraints
- Do NOT pay for a new CoinGlass subscription tier if the current one is insufficient — confirm the existing rate limits and auth before scoping.
- If fallback (12C) is chosen instead of fixing CoinGlass directly, clearly document that Exchange Analytics is now sourced from the bot's own connectors, not CoinGlass.

---
**Run 2026-04-11T22:40:28Z:** Started pipeline (orchestrate.sh)
**Failed 2026-04-11T22:40:28Z:** analyze | Status: FAILED
#### Cross-cutting Constraints (all issues)
- No behavioural changes to `SignalEngine` or `PositionHealthMonitor` PnL math — only display, computation-timing, and investigation-output changes.
- Preserve all existing unit tests — run `dotnet test tests/FundingRateArb.Tests.Unit` as a regression guard.
- Follow the conventions in `.claude/CLAUDE.md` (.NET architecture, DI, data access, testing).
- Any dashboard JS edits must retain the existing DOM-construction style (no `innerHTML`) — the file is CSP-friendly today and should stay that way.
- Feature file is committed; plan/prompts/progress/review are NOT per `.git/info/exclude` convention.

#### Run Log
**Run 2026-04-11T22:40:29Z:** Started pipeline (orchestrate.sh)
**Failed 2026-04-11T22:40:29Z:** analyze | Status: FAILED
#### Related Features
- **Close-positions root cause investigation** is tracked separately in `docs/feature-close-positions-investigation-2026-04-12.md`. That feature is investigation-first (no code changes until the analysis report lands) and should be run interactively, not as part of an unattended pipeline pass. Issues 5 Part B in this file overlaps in spirit — the deeper close-reason-and-profitability work lives in the separate investigation feature.

#### Run Log
**Run 2026-04-11T22:40:29Z:** Started pipeline (orchestrate.sh)
**Failed 2026-04-11T22:40:29Z:** analyze | Status: FAILED

### Run Log
<!-- pipeline-managed -->
**Run 2026-04-11T22:46:18Z:** Started pipeline (orchestrate.sh)
**Failed 2026-04-12T00:12:25Z:** implement | Status: FAILED
**Run 2026-04-12T00:41:53Z:** Started pipeline (orchestrate.sh)
**Resume 2026-04-12T00:41:53Z:** skipping analyze for feat/dashboard-pnl-fixes (--restart-from=implement)
**Resume 2026-04-12T00:41:53Z:** skipping plan for feat/dashboard-pnl-fixes (--restart-from=implement)
**Failed 2026-04-12T04:40:39Z:** refix | Status: FAILED
