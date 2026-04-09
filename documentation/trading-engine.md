# Trading Engine

The trading engine is the core of FundingRateArb. It detects funding rate arbitrage opportunities across exchanges and executes hedged positions to capture the spread.

## Concept

Perpetual futures exchanges charge or pay **funding rates** to keep contract prices aligned with spot. When one exchange pays a positive rate (longs pay shorts) and another pays a negative rate (shorts pay longs), a **spread** exists. By going long on one exchange and short on the other, the bot collects the funding differential with minimal directional risk.

## Bot Operating State

`BotConfiguration.OperatingState` drives a four-state lifecycle that `BotOrchestrator` consults before each cycle:

| State | Value | Opens positions | Monitors open positions | Typical trigger |
|-------|-------|-----------------|-------------------------|-----------------|
| `Stopped` | 0 | No | No | Manual shutdown or startup default |
| `Armed` | 1 | No | Yes | Operator prepping the bot for trading |
| `Trading` | 2 | Yes | Yes | Operator confirms active trading |
| `Paused` | 3 | No | Yes | Daily drawdown limit or consecutive loss pause |

Transitions from `Trading` → `Paused` are automatic (drawdown, consecutive losses). Transitions back to `Trading` require explicit operator action. Health monitoring and close paths remain active in `Armed` and `Paused` so existing positions are never abandoned.

## Dry Run Mode

`BotConfiguration.DryRunEnabled` (global) and `UserConfiguration.DryRunEnabled` (per-user, can only enable for a user — never disable when global is on) route order placement through `DryRunConnectorWrapper`. Dry-run positions use real mark prices for simulated fills, write `IsDryRun = true` on the `ArbitragePosition` row, and are excluded from balance aggregation and daily drawdown calculations. The dashboard marks dry-run positions with a distinct badge.

## Pipeline

### 1. Signal Engine (`ISignalEngine`)

Scores every possible exchange pair for each asset:

```
For each asset:
  For each exchange pair (A, B):
    spread = rateA - rateB  (long A, short B)
    netYield = spread - estimatedFees
    score = netYield * volumeWeight * settlementBoost
```

**Scoring factors:**
- **Net spread per hour** - Funding rate differential minus estimated round-trip fees
- **Volume weighting** - Lower scores for low-liquidity pairs
- **Settlement window boost** - 20% yield boost when within `FundingWindowMinutes` of settlement (periodic exchanges)
- **Rate prediction** - Optional ML-based prediction of future spread direction via `IRatePredictionService`

Returns a sorted list of `ArbitrageOpportunityDto` above the configured `OpenThreshold`.

### 2. Position Sizer (`IPositionSizer`)

Allocates capital across opportunities based on `AllocationStrategy`:

| Strategy | Behavior |
|----------|----------|
| **Concentrated** | 100% to the single best opportunity |
| **WeightedSpread** | Proportional to net yield (higher spread = more capital) |
| **EqualSpread** | Equal allocation across top N opportunities |
| **RiskAdjusted** | Weighted by yield/volume ratio (penalizes thin markets) |

**Constraints applied:**
- `MaxCapitalPerPosition` - Max fraction of total capital per position
- `MaxExposurePerAsset` - Max fraction of capital in any single asset
- `MaxExposurePerExchange` - Max fraction of capital on any single exchange
- `MinPositionSizeUsdc` - Minimum viable position size
- `VolumeFraction` - Position size capped at fraction of 24h volume

### 3. Execution Engine (`IExecutionEngine`)

Executes dual-leg trades with safety checks. `ConnectorLifecycleManager` provides user-scoped connectors (including the `DryRunConnectorWrapper` when dry-run is enabled) and keeps leverage tier caches warm. `PositionCloser` and `EmergencyCloseHandler` own the close paths.

**Opening a position:**
1. Pre-flight: verify balance, resolve `effectiveLeverage = min(userLeverage, globalCap, userCap, tierMax)` via `ILeverageTierProvider`, confirm margin adequacy
2. Place long leg and short leg concurrently
3. If either leg fails, `EmergencyCloseHandler` closes the successful leg and records the close reason
4. Record entry prices, fees, order IDs, and a baseline snapshot for later reconciliation
5. Create position entity with status `Open`

**Closing a position:**
1. `PositionCloser` closes both legs concurrently
2. `PnlReconciliationService` reconciles per-exchange realized PnL against the bot's tracked PnL and flags divergence
3. Calculate realized P&L using actual fill prices (directional component) plus accumulated funding minus fees
4. Update position status to `Closed` with close reason

The `OrderExecution` resilience pipeline has **no retry** to prevent double fills on market orders. The `OrderClose` pipeline uses only a timeout since close operations must not be blocked.

#### Leverage tiers and caps

`LeverageTierRefresher` pre-fetches per-exchange leverage brackets hourly. At order placement, the execution engine applies three caps in order: the global `BotConfiguration.MaxLeverageCap`, the per-user `UserConfiguration.MaxLeverageCap` (which can only tighten the global), and the exchange-specific tier max at the position's notional size. The user's requested leverage is then clamped to the minimum of those. This matches the safety guardrail recommended by both academic research and industry (e.g. Gate.io hard-capping at 3x).

### 4. Position Health Monitor (`IPositionHealthMonitor`)

Monitors all open positions every cycle and triggers close when:

| Condition | Close Reason | Description |
|-----------|-------------|-------------|
| Spread inverted | `SpreadCollapsed` | Current spread is negative (paying to hold) |
| Funding differential flipped | `FundingFlipped` | Rate differential inverted for `FundingFlipExitCycles` consecutive cycles |
| Hold time exceeded | `MaxHoldTimeReached` | Position open longer than `MaxHoldTimeHours` |
| Loss limit | `StopLoss` | Accumulated P&L below `-StopLossPct * MarginUsdc` |
| Target reached | `PnlTargetReached` | Funding collected >= `TargetPnlMultiplier * EntryFees` (when `AdaptiveHoldEnabled`) |
| Rebalanced | `Rebalanced` / `Rotation` | Closed to free capital for a materially better opportunity |
| Price feed lost | `PriceFeedLost` | Mark price unavailable beyond `PriceFeedFailureCloseThreshold` cycles |
| Leg failure | `EmergencyLegFailed` | One leg went missing or was liquidated |
| Exchange drift | `ExchangeDrift` | Bot-tracked position diverged from exchange-reported state during reconciliation |
| Liquidation risk | `LiquidationRisk` | `MaxSafeMove` below `LiquidationWarningPct` of current price |
| Stablecoin depeg | `StablecoinDepeg` | USDT/USDC spread crossed `StablecoinCriticalThresholdPct` |
| Mark divergence | `DivergenceCritical` | Mark price divergence between legs exceeded `DivergenceAlertMultiplier × entry spread` |
| Manual | `Manual` | Closed by operator via UI |

#### Unified PnL (three-view model)

Every open position reports three PnL figures:

1. **Per-exchange PnL** — the raw `unrealizedPnl` as each exchange reports it. Used for margin-health and liquidation monitoring; matches what the exchange UI shows.
2. **Unified-reference-price PnL** — strategy PnL computed against a single reference price across both legs. `ReferencePriceProvider` prioritises the Binance index price when Binance is one leg; otherwise averages the oracle prices from the DEX legs. This is the true strategy performance indicator for open positions and hides the mark-price noise that makes each exchange leg look mispriced in isolation.
3. **Final realized PnL** — computed from actual fill prices after the position closes, decomposed into directional, funding, and fees via `PnlDecompositionDto`.

`PnlReconciliationService` periodically cross-checks the bot's tracked PnL against the exchanges' realized PnL to detect drift. Divergence beyond `DivergenceAlertMultiplier` raises an alert.

### 5. Yield Calculator (`IYieldCalculator`)

Computes financial metrics:

- **Annualized yield** - Converts hourly rate to APY: `ratePerHour * 8760 * 100`
- **Projected P&L** - `sizeUsdc * netRate * hours`
- **Unrealized P&L** - Current position funding minus fees
- **Break-even hours** - `totalFees / (sizeUsdc * netRatePerHour)` - hours needed to recover entry/exit fees

## Bot Configuration

All parameters are stored in the `BotConfigurations` table and editable via the admin dashboard:

### Thresholds

| Parameter | Default | Description |
|-----------|---------|-------------|
| `OpenThreshold` | 0.0002/hr | Minimum spread to open a position |
| `AlertThreshold` | 0.0001/hr | Spread level that triggers an alert |
| `CloseThreshold` | -0.00005/hr | Spread level that triggers close |

### Capital Management

| Parameter | Default | Description |
|-----------|---------|-------------|
| `TotalCapitalUsdc` | 39 | Total trading capital in USDC |
| `MaxCapitalPerPosition` | 90% | Max fraction per position |
| `DefaultLeverage` | 5x | Default leverage for new positions |
| `MaxLeverageCap` | 3x | Global hard cap on leverage regardless of exchange tier max |
| `MaxConcurrentPositions` | 1 | Parallel position limit |
| `MinPositionSizeUsdc` | 5 | Minimum position size |
| `AllocationStrategy` | Concentrated | How capital is split across opportunities |
| `MinEdgeMultiplier` | 3.0 | Opportunity net edge must exceed `MinEdgeMultiplier × totalEntryCost` to open |

### Risk Management

| Parameter | Default | Description |
|-----------|---------|-------------|
| `StopLossPct` | 10% | Max loss as fraction of margin |
| `MaxHoldTimeHours` | 48 | Auto-close after this many hours |
| `MinHoldBeforePnlTargetMinutes` | — | Block PnL-target close before this many minutes elapsed |
| `BreakevenHoursMax` | 8 | Skip opportunities that take too long to break even on fees |
| `MinVolume24hUsdc` | 50,000 | Minimum 24h volume to consider |
| `DailyDrawdownPausePct` | 8% | Pause bot after this daily drawdown |
| `ConsecutiveLossPause` | 3 | Pause after N consecutive losses |
| `RateStalenessMinutes` | 15 | Ignore rates older than this |
| `LiquidationWarningPct` | — | Fraction of current price at which `MaxSafeMove` triggers a liquidation-risk close |
| `MarginUtilizationAlertPct` | — | Per-exchange margin-used threshold raising an alert |
| `DivergenceAlertMultiplier` | 2.0 | Mark-price divergence multiplier over entry spread that triggers a critical close |
| `StablecoinAlertThresholdPct` | 0.3% | USDT/USDC spread that raises a warning |
| `StablecoinCriticalThresholdPct` | 1.0% | USDT/USDC spread that forces an emergency close |
| `MinConsecutiveFavorableCycles` | — | Funding trend stability cycles required before opening a position |
| `FundingFlipExitCycles` | 2 | Close if differential stays inverted for this many cycles |
| `EmergencyCloseSpreadThreshold` | — | Extreme spread move that triggers emergency close |
| `PriceFeedFailureCloseThreshold` | — | Cycles of stale mark price that trigger close |
| `SlippageBufferBps` | — | Slippage buffer applied to pre-flight size/price checks |
| `ReconciliationIntervalCycles` | — | How often `PnlReconciliationService` runs relative to the trading cycle |
| `ExchangeCircuitBreakerThreshold` | — | Failure count per exchange that trips the breaker |
| `ExchangeCircuitBreakerMinutes` | — | Cooldown duration once the breaker trips |

### Advanced

| Parameter | Default | Description |
|-----------|---------|-------------|
| `AdaptiveHoldEnabled` | true | Close when P&L target is hit |
| `TargetPnlMultiplier` | 2.0x | P&L target as multiple of fees |
| `RebalanceEnabled` | false | Auto-rebalance portfolio |
| `RebalanceMinImprovement` | 0.0002/hr | Min spread improvement to justify rebalance |
| `MaxRebalancesPerCycle` | 2 | Rebalance limit per cycle |
| `FundingWindowMinutes` | 10 | Settlement proximity boost window |
| `MaxExposurePerAsset` | 50% | Concentration limit per asset |
| `MaxExposurePerExchange` | 70% | Concentration limit per exchange |
| `DryRunEnabled` | false | Route all order placement through `DryRunConnectorWrapper` |
| `ForceConcurrentExecution` | false | Override the sequential-execution safety fallback |

### Per-User Overrides (`UserConfiguration`)

A subset of these parameters has a per-user override. `MaxLeverageCap` and `DryRunEnabled` on the user config can only tighten the global settings — a user cannot raise their own leverage cap above the bot-wide limit, and a user cannot opt out of global dry-run mode.

## Position Lifecycle

```
  [Opportunity detected]
          |
          v
      Opening ──(both legs filled)──> Open
          |                             |
     (leg failed)               +------+------+------+------+
          |                     |      |      |      |      |
          v                     v      v      v      v      v
    EmergencyClosed        Spread  MaxHold  Stop  PnL   Rebal
                           Collapse  Time   Loss Target  ance
                                     |
                                     v
                                  Closing ──> Closed
                                     |
                                (leg failed)
                                     |
                                     v
                              EmergencyClosed
```

### Position Statuses

| Status | Description |
|--------|-------------|
| `Opening` | Legs being placed |
| `Open` | Both legs filled, actively collecting funding |
| `Closing` | Close orders being placed |
| `Closed` | Both legs closed, P&L realized |
| `EmergencyClosed` | One leg failed during open/close |
| `Liquidated` | Exchange-level liquidation detected |
| `Failed` | Open attempt failed verification (e.g. no confirmed fill within the grace window) |

## Circuit Breaker

The BotOrchestrator implements its own circuit breaker for failed opportunities:

- On execution failure: asset+exchange pair enters cooldown (exponential backoff)
- On consecutive user losses: user-level pause after `ConsecutiveLossPause` failures
- On daily drawdown: global pause when realized losses exceed `DailyDrawdownPausePct`
