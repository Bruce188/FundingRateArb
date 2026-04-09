# Architecture Overview

FundingRateArb follows **Clean Architecture** with strict dependency inversion. Each layer depends only on the layer directly inside it; the Domain layer has zero external dependencies.

## Layer Diagram

```
                          +-----------------------+
                          |    FundingRateArb.Web  |
                          |  (ASP.NET Core MVC)    |
                          +----------+------------+
                                     |
                          +----------v------------+
                          | FundingRateArb.        |
                          | Infrastructure         |
                          |  (EF Core, Connectors, |
                          |   Background Services) |
                          +----------+------------+
                                     |
                          +----------v------------+
                          | FundingRateArb.        |
                          | Application            |
                          |  (Services, DTOs,      |
                          |   Interfaces)          |
                          +----------+------------+
                                     |
                          +----------v------------+
                          | FundingRateArb.Domain  |
                          |  (Entities, Enums)     |
                          +-----------------------+
```

## Project Responsibilities

| Project | Purpose | Dependencies |
|---------|---------|-------------|
| **Domain** | Entities, enums, value objects | None |
| **Application** | Service interfaces, DTOs, business logic | Domain |
| **Infrastructure** | EF Core, exchange connectors, SignalR, background services, encryption | Application, Domain |
| **Web** | Controllers, Razor views, DI setup, middleware | All layers |

## Runtime Architecture

```
                    Browser (MVC + SignalR)
                           |
                    +------v-------+
                    |  ASP.NET     |
                    |  Core MVC    |
                    |  + SignalR   |
                    +------+-------+
                           |
          +----------------+----------------+
          |                |                |
   +------v------+  +-----v------+  +------v------+
   | Controllers |  | DashboardHub|  | Rate Limiter|
   +------+------+  +-----+------+  +-------------+
          |                |
   +------v----------------v------+
   |        Service Layer          |
   |  SignalEngine, ExecutionEngine|
   |  ConnectorLifecycleManager,   |
   |  PositionCloser,              |
   |  EmergencyCloseHandler,       |
   |  PositionHealthMonitor,       |
   |  PnlReconciliationService,    |
   |  ExchangeAnalyticsService,    |
   |  ReferencePriceProvider,      |
   |  PositionSizer, YieldCalc     |
   +------+-----------+-----------+
          |           |
   +------v------+  +-v-----------+
   | UnitOfWork  |  | Exchange    |
   | (EF Core)   |  | Connectors  |
   +------+------+  +------+------+
          |                |
   +------v------+  +------v------+
   | SQL Server  |  | Exchange    |
   | 2022        |  | APIs        |
   +-------------+  +-------------+
```

## Background Services

Six hosted services run continuously alongside the web server:

1. **MarketDataStreamManager** - Starts WebSocket connections to all exchanges, monitors health every 30 seconds, auto-reconnects on failure
2. **FundingRateFetcher** - Polls funding rates via REST every 60 seconds, stores snapshots, updates the in-memory cache, signals readiness on first fetch
3. **FundingRateReadinessSignal** - Waits for FundingRateFetcher to complete the first rate fetch, then signals readiness so BotOrchestrator can begin its trading cycle
4. **BotOrchestrator** - Waits for FundingRateFetcher readiness, then runs the trading cycle every 60 seconds: score opportunities, size positions, execute trades, monitor health
5. **LeverageTierRefresher** - Pre-fetches and caches per-exchange leverage brackets hourly so `ExecutionEngine` can clamp leverage without blocking on API calls during order placement
6. **DailySummaryService** - Sends daily P&L summary emails to opted-in users

## Data Flow: Trading Cycle

```
1. FundingRateFetcher polls exchange APIs
                |
                v
2. Rates stored in DB + MarketDataCache
                |
                v
3. BotOrchestrator triggers cycle
                |
                v
4. SignalEngine scores all exchange-pair spreads
                |
                v
5. PositionSizer allocates capital per AllocationStrategy
                |
                v
6. ExecutionEngine places dual-leg orders (long + short)
                |
                v
7. PositionHealthMonitor checks open positions
                |
                v
8. Alerts pushed via SignalR to connected dashboards
```

## Exchange Integration Pattern

Each exchange implements two interfaces:

- **`IExchangeConnector`** (REST) - Funding rates, order placement, balance queries, margin state, leverage tiers, position reconciliation
- **`IMarketDataStream`** (WebSocket) - Real-time rate streaming

The `ExchangeConnectorFactory` manages connector lifecycle with key rotation and rate-limit cooldown tracking. `ConnectorLifecycleManager` wraps user-scoped connector creation, leverage tier caching, and `DryRunConnectorWrapper` application for paper-trading mode.

| Exchange | Type | Funding Interval | Auth |
|----------|------|------------------|------|
| Hyperliquid | DEX | 1h | Wallet-based (HyperLiquid.Net SDK) |
| Lighter | DEX | 1h | Custom zkLighter signer |
| dYdX v4 | DEX | 1h | Cosmos indexer + user signer |
| Aster | DEX | 8h (with 15s window deviation) | API key (Aster.Net SDK) |
| Binance | CEX | 8h (shifts to 4h/1h in volatility) | API key + secret |
| CoinGlass | Data | N/A | API key (REST only) |

CoinGlass is a data-only source (`IsDataOnly = true`) providing supplementary volume data and the arbitrage screening feed. It implements `IExchangeConnector` but is excluded from trading.

## Resilience

Three Polly pipelines protect exchange API calls:

| Pipeline | Retry | Circuit Breaker | Timeout | Use Case |
|----------|-------|----------------|---------|----------|
| `ExchangeSdk` | 3x exponential | 50% failure / 30s | 15s | General API calls |
| `OrderExecution` | None | 50% failure / 60s | 30s | Order placement (no retry to prevent double fills) |
| `OrderClose` | None | None | 30s | Position close (critical path) |

## Real-Time Updates

The `DashboardHub` (SignalR) pushes updates to connected clients:

- Funding rate changes
- New opportunities detected
- Position opened/closed events
- Alert notifications
- Bot status changes

## Database

SQL Server 2022 with EF Core 8 (code-first migrations). Key tables:

- `ArbitragePositions` - Trading positions with full lifecycle, including unified/exchange PnL split, liquidation price, margin utilization, and leg-level fee tracking
- `FundingRateSnapshots` - Point-in-time rate data (48h retention)
- `FundingRateHourlyAggregates` - Hourly aggregated rates (30-day retention)
- `BotConfigurations` - Global bot settings including `OperatingState`, `MaxLeverageCap`, `MinEdgeMultiplier`, `DryRunEnabled`
- `UserConfigurations` - Per-user overrides (leverage cap and dry-run flag can only tighten the global settings)
- `UserExchangeCredentials` - Encrypted API keys
- `Alerts` - Notification history
- `Exchanges`, `Assets` - Reference data
- `ExchangeAssetConfigs` - Per-exchange asset configuration (min size, fee overrides)
- `OpportunitySnapshots` - Historical opportunity records
- `CoinGlassExchangeRate` - Cached CoinGlass funding/volume snapshots for the analytics dashboard
- `CoinGlassDiscoveryEvent` - New exchange / new coin discovery log sourced from CoinGlass

## Authentication and Authorization

- ASP.NET Core Identity with role-based access (Admin, User)
- Optional external OAuth (Google, GitHub)
- Admin area (`/Admin/*`) restricted to Admin role
- Exchange credentials encrypted via Data Protection API
- Cookie-based with 8-hour sliding expiration, HttpOnly, SameSite=Strict

## Security Headers

Applied via middleware on every response:

- `X-Frame-Options: DENY`
- `X-Content-Type-Options: nosniff`
- `Referrer-Policy: strict-origin-when-cross-origin`
- `Permissions-Policy: geolocation=(), microphone=(), camera=()`
- `Content-Security-Policy` with pinned CDN URLs and SRI hashes
