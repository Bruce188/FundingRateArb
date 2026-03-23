# Trading Engine

The trading engine is the core of FundingRateArb. It detects funding rate arbitrage opportunities across exchanges and executes hedged positions to capture the spread.

## Concept

Perpetual futures exchanges charge or pay **funding rates** to keep contract prices aligned with spot. When one exchange pays a positive rate (longs pay shorts) and another pays a negative rate (shorts pay longs), a **spread** exists. By going long on one exchange and short on the other, the bot collects the funding differential with minimal directional risk.

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

Executes dual-leg trades with safety checks:

**Opening a position:**
1. Pre-flight: verify balance, check leverage limits, confirm margin adequacy
2. Place long leg and short leg concurrently
3. If either leg fails, close the successful leg (emergency rollback)
4. Record entry prices, fees, and order IDs
5. Create position entity with status `Open`

**Closing a position:**
1. Close both legs concurrently
2. Calculate realized P&L including accumulated funding and fees
3. Update position status to `Closed` with close reason

The `OrderExecution` resilience pipeline has **no retry** to prevent double fills on market orders. The `OrderClose` pipeline uses only a timeout since close operations must not be blocked.

### 4. Position Health Monitor (`IPositionHealthMonitor`)

Monitors all open positions every cycle and triggers close when:

| Condition | Close Reason | Description |
|-----------|-------------|-------------|
| Spread inverted | `SpreadCollapsed` | Current spread is negative (paying to hold) |
| Hold time exceeded | `MaxHoldTimeReached` | Position open longer than `MaxHoldTimeHours` |
| Loss limit | `StopLoss` | Accumulated P&L below `-StopLossPct * MarginUsdc` |
| Target reached | `PnlTargetReached` | Funding collected >= `TargetPnlMultiplier * EntryFees` (when `AdaptiveHoldEnabled`) |
| Mark price stale | `EmergencyLegFailed` | Price feed unavailable for either leg |

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
| `OpenThreshold` | 0.0003/hr | Minimum spread to open a position |
| `AlertThreshold` | 0.0001/hr | Spread level that triggers an alert |
| `CloseThreshold` | -0.00005/hr | Spread level that triggers close |

### Capital Management

| Parameter | Default | Description |
|-----------|---------|-------------|
| `TotalCapitalUsdc` | 107 | Total trading capital in USDC |
| `MaxCapitalPerPosition` | 80% | Max fraction per position |
| `DefaultLeverage` | 5x | Default leverage for new positions |
| `MaxConcurrentPositions` | 1 | Parallel position limit |
| `MinPositionSizeUsdc` | 10 | Minimum position size |
| `AllocationStrategy` | Concentrated | How capital is split across opportunities |

### Risk Management

| Parameter | Default | Description |
|-----------|---------|-------------|
| `StopLossPct` | 15% | Max loss as fraction of margin |
| `MaxHoldTimeHours` | 72 | Auto-close after this many hours |
| `BreakevenHoursMax` | 6 | Skip opportunities that take too long to break even on fees |
| `MinVolume24hUsdc` | 50,000 | Minimum 24h volume to consider |
| `DailyDrawdownPausePct` | 5% | Pause bot after this daily drawdown |
| `ConsecutiveLossPause` | 3 | Pause after N consecutive losses |
| `RateStalenessMinutes` | 15 | Ignore rates older than this |

### Advanced

| Parameter | Default | Description |
|-----------|---------|-------------|
| `AdaptiveHoldEnabled` | false | Close when P&L target is hit |
| `TargetPnlMultiplier` | 2.0x | P&L target as multiple of fees |
| `RebalanceEnabled` | false | Auto-rebalance portfolio |
| `RebalanceMinImprovement` | 0.0002/hr | Min spread improvement to justify rebalance |
| `MaxRebalancesPerCycle` | 2 | Rebalance limit per cycle |
| `FundingWindowMinutes` | 10 | Settlement proximity boost window |
| `MaxExposurePerAsset` | 50% | Concentration limit per asset |
| `MaxExposurePerExchange` | 70% | Concentration limit per exchange |

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

## Circuit Breaker

The BotOrchestrator implements its own circuit breaker for failed opportunities:

- On execution failure: asset+exchange pair enters cooldown (exponential backoff)
- On consecutive user losses: user-level pause after `ConsecutiveLossPause` failures
- On daily drawdown: global pause when realized losses exceed `DailyDrawdownPausePct`
