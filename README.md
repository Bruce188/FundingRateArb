# FundingRateArb

[![Deploy](https://github.com/Bruce188/FundingRateArb/actions/workflows/deploy.yml/badge.svg)](https://github.com/Bruce188/FundingRateArb/actions/workflows/deploy.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

![.NET 8](https://img.shields.io/badge/.NET_8-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-239120?style=flat-square&logo=csharp&logoColor=white)
![ASP.NET Core](https://img.shields.io/badge/ASP.NET_Core_MVC-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![SignalR](https://img.shields.io/badge/SignalR-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![SQL Server](https://img.shields.io/badge/SQL_Server_2022-CC2927?style=flat-square&logo=microsoftsqlserver&logoColor=white)
![EF Core](https://img.shields.io/badge/EF_Core_8-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![Docker](https://img.shields.io/badge/Docker-2496ED?style=flat-square&logo=docker&logoColor=white)
![Azure](https://img.shields.io/badge/Azure-0078D4?style=flat-square&logo=microsoftazure&logoColor=white)
![GitHub Actions](https://img.shields.io/badge/GitHub_Actions-2088FF?style=flat-square&logo=githubactions&logoColor=white)
![Serilog](https://img.shields.io/badge/Serilog-2B2D42?style=flat-square&logo=dotnet&logoColor=white)
![Polly](https://img.shields.io/badge/Polly-FF6F00?style=flat-square&logo=dotnet&logoColor=white)
![xUnit](https://img.shields.io/badge/xUnit-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![Playwright](https://img.shields.io/badge/Playwright-2EAD33?style=flat-square&logo=playwright&logoColor=white)

Automated funding rate arbitrage bot for perpetual futures. Monitors funding rate differentials across DEXs (Hyperliquid, Lighter, dYdX v4, Aster) and a CEX (Binance), opens hedged long/short positions when spreads exceed a configurable threshold, and collects the funding rate differential as yield. Supplementary screening data comes from CoinGlass.

## How It Works

Perpetual futures exchanges charge or pay **funding rates** to keep contract prices aligned with spot. When Exchange A pays longs and Exchange B pays shorts at different rates, a **spread** exists. The bot goes long on one and short on the other, capturing the differential as market-neutral yield.

```
Exchange A:  funding rate  +0.01%/hr  (longs pay shorts)
Exchange B:  funding rate  -0.03%/hr  (shorts pay longs)
                                       ─────────────────
Spread:      0.04%/hr  →  bot opens long A + short B  →  collects 0.04%/hr
```

Every 60 seconds the bot scores all exchange pairs, sizes positions based on available capital, executes dual-leg trades with pre-flight margin checks, and monitors open positions for health degradation.

## Architecture

The project follows **Clean Architecture** with four layers:

```
FundingRateArb.sln
├── src/
│   ├── FundingRateArb.Domain          # Entities, enums — zero dependencies
│   ├── FundingRateArb.Application     # Services, DTOs, interfaces, business logic
│   ├── FundingRateArb.Infrastructure  # EF Core, exchange connectors, SignalR, background services
│   └── FundingRateArb.Web             # ASP.NET Core MVC, controllers, Razor views
└── tests/
    ├── FundingRateArb.Tests.Unit         # xUnit + Moq + FluentAssertions
    ├── FundingRateArb.Tests.Integration  # Repositories, hub, dashboard, admin pages
    ├── FundingRateArb.Tests.E2E          # xUnit + Microsoft.Playwright (.NET)
    └── playwright/                       # Legacy end-to-end browser tests (Python)
```

| Layer | Responsibility |
|-------|---------------|
| **Domain** | Entities (`ArbitragePosition`, `BotConfiguration`, `UserConfiguration`, `Alert`, `Asset`), enums (`BotOperatingState`, `PositionStatus`, `AllocationStrategy`), no external dependencies |
| **Application** | Service interfaces, DTOs, business logic (`SignalEngine`, `PositionSizer`, `ExecutionEngine`, `ConnectorLifecycleManager`, `PositionCloser`, `EmergencyCloseHandler`, `PositionHealthMonitor`, `PnlReconciliationService`, `ExchangeAnalyticsService`, `YieldCalculator`), `DryRunConnectorWrapper` |
| **Infrastructure** | EF Core (`AppDbContext`, migrations), exchange connectors (Hyperliquid, Lighter, dYdX v4, Aster, Binance, CoinGlass), `ReferencePriceProvider`, SignalR hub, six hosted background services, Data Protection vault |
| **Web** | Controllers, Razor views, Admin area, `Program.cs` DI/middleware setup, static assets |

## Key Components

| Component | Purpose |
|-----------|---------|
| **SignalEngine** | Scores funding rate spreads across all exchange pairs with adaptive threshold fallback and optional ML-based rate prediction |
| **PositionSizer** | Allocates capital using configurable strategies (`Concentrated`, `WeightedSpread`, `EqualSpread`, `RiskAdjusted`) with per-asset/exchange exposure limits and `MinEdgeMultiplier` guardrail |
| **ExecutionEngine** | Concurrent dual-leg order placement with pre-flight margin checks, leverage tier clamping, and atomic rollback on partial fills; routes through `DryRunConnectorWrapper` when dry-run is enabled |
| **ConnectorLifecycleManager** | User-scoped connector creation, leverage tier cache warming, and dry-run wrapper application |
| **PositionCloser** | Owns the concurrent dual-leg close path for normal and operator-initiated closes |
| **EmergencyCloseHandler** | Closes the surviving leg when one leg fails during open, or when liquidation/drift conditions require an immediate exit |
| **BotOrchestrator** | Background service running 60-second trading cycles with operating-state gating, circuit breaker, consecutive loss tracking, daily drawdown pause, and alert deduplication |
| **PositionHealthMonitor** | Monitors open positions for spread collapse, funding flip, max hold time, stop loss, P&L targets, stale price feeds, liquidation risk, stablecoin depeg, and mark-price divergence |
| **PnlReconciliationService** | Periodically reconciles bot-tracked PnL against exchange-reported realized PnL and raises alerts on divergence |
| **ReferencePriceProvider** | Produces a single reference mark for unified PnL: Binance index when Binance is a leg, otherwise averaged DEX oracle prices |
| **ExchangeAnalyticsService** | CoinGlass-driven exchange overviews, spread opportunities, rate comparisons, and new-coin / new-exchange discovery feed |
| **YieldCalculator** | Computes annualized yield, projected/unrealized P&L, break-even hours, and fee-decomposed realized P&L |
| **MarketDataCache** | In-memory cache of latest rates from both REST polling and WebSocket streams |
| **DashboardHub** | SignalR hub pushing real-time rate updates, opportunities, position changes, balance snapshots, status explanations, and alerts to connected clients. The dashboard supports anonymous access with cached opportunities (30s TTL); authenticated users also see positions, alerts, and P&L |
| **ApiKeyVault** | Encrypted exchange credential storage using ASP.NET Core Data Protection API |

## Background Services

Six hosted services run continuously alongside the web server:

1. **MarketDataStreamManager** — Starts WebSocket connections to all exchanges, monitors health every 30 seconds, auto-reconnects on failure
2. **FundingRateFetcher** — Polls funding rates via REST every 60 seconds, stores snapshots, updates the in-memory cache, signals readiness on first fetch
3. **FundingRateReadinessSignal** — Waits for `FundingRateFetcher` to complete the first rate fetch, then signals readiness so `BotOrchestrator` can begin its trading cycle
4. **BotOrchestrator** — Runs the trading cycle every 60 seconds: score opportunities, size positions, execute trades, monitor health
5. **LeverageTierRefresher** — Pre-fetches and caches per-exchange leverage brackets hourly so `ExecutionEngine` can clamp leverage without blocking on API calls during order placement
6. **DailySummaryService** — Sends daily P&L summary emails to opted-in users

## Exchange Integrations

| Exchange | Type | Funding Interval | Connection | Auth |
|----------|------|------------------|------------|------|
| **Hyperliquid** | DEX | 1h | SDK (HyperLiquid.Net) + WebSocket | Wallet-based (with optional sub-account vault) |
| **Lighter** | DEX | 1h | Custom REST + WebSocket | Custom zkLighter signer |
| **dYdX v4** | DEX | 1h | Cosmos indexer + user signer | Per-user credentials (Settings page) |
| **Aster** | DEX | 8h (±15s window) | SDK (Aster.Net) + WebSocket | API key + secret |
| **Binance** | CEX | 8h (shifts to 4h/1h in volatility) | REST + WebSocket | API key + secret |
| **CoinGlass** | Data | N/A | REST | API key |

CoinGlass is a data-only source (`IsDataOnly = true`) providing supplementary volume data and the arbitrage screening feed that powers `/Admin/ExchangeAnalytics`. It implements `IExchangeConnector` but is excluded from trading.

Each tradable exchange implements `IExchangeConnector` (REST: funding rates, orders, balance, margin state, leverage tiers, position reconciliation) and `IMarketDataStream` (WebSocket). The `ExchangeConnectorFactory` manages infrastructure-level connector lifecycle with key rotation and rate-limit cooldown tracking. `ConnectorLifecycleManager` wraps user-scoped connector creation, leverage tier caching, and `DryRunConnectorWrapper` application for paper-trading mode.

## Bot Operating States

`BotConfiguration.OperatingState` drives a four-state lifecycle that `BotOrchestrator` consults before each cycle:

| State | Value | Opens positions | Monitors open positions | Typical trigger |
|-------|-------|-----------------|-------------------------|-----------------|
| `Stopped` | 0 | No | No | Manual shutdown or startup default |
| `Armed` | 1 | No | Yes | Operator prepping the bot for trading |
| `Trading` | 2 | Yes | Yes | Operator confirms active trading |
| `Paused` | 3 | No | Yes | Daily drawdown limit or consecutive loss pause |

Transitions from `Trading` → `Paused` are automatic (drawdown, consecutive losses). Returning to `Trading` requires explicit operator action. Health monitoring and close paths remain active in `Armed` and `Paused` so existing positions are never abandoned.

## Dry Run Mode

`BotConfiguration.DryRunEnabled` (global) and `UserConfiguration.DryRunEnabled` (per-user — can only enable for a user, never disable when the global flag is on) route order placement through `DryRunConnectorWrapper`. Dry-run positions use real mark prices for simulated fills, write `IsDryRun = true` on the `ArbitragePosition` row, and are excluded from balance aggregation and daily drawdown calculations. The dashboard marks dry-run positions with a distinct badge.

## Unified PnL (Three-View Model)

Every open position reports three PnL figures:

1. **Per-exchange PnL** — raw `unrealizedPnl` as each exchange reports it. Used for margin-health and liquidation monitoring; matches what each exchange UI shows.
2. **Unified-reference-price PnL** — strategy PnL computed against a single reference price across both legs via `ReferencePriceProvider` (Binance index when Binance is one leg, otherwise averaged DEX oracle prices). This hides the mark-price noise that makes each leg look mispriced in isolation.
3. **Final realized PnL** — computed from actual fill prices after the position closes, decomposed into `Directional`, `Funding`, and `Fees` via `PnlDecompositionDto`.

`PnlReconciliationService` cross-checks bot-tracked PnL against exchange-reported realized PnL each `ReconciliationIntervalCycles`. Divergence beyond `DivergenceAlertMultiplier` raises a critical alert.

## Leverage Tier Capping

`LeverageTierRefresher` pre-fetches per-exchange leverage brackets hourly. At order placement, the execution engine applies three caps in order:

1. Global `BotConfiguration.MaxLeverageCap` (default **3x**)
2. Per-user `UserConfiguration.MaxLeverageCap` (can only tighten the global cap)
3. Exchange-specific tier max at the position's notional size

The user's requested leverage is clamped to the minimum of those. This matches the safety guardrail recommended by academic research and industry practice (e.g. Gate.io hard-capping at 3x).

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- SQL Server 2022+ (or Docker)
- Exchange API credentials for whichever exchanges you intend to trade (Hyperliquid, Lighter, Aster, Binance). dYdX v4 credentials are entered per-user via `/Settings`. CoinGlass is optional but recommended for opportunity screening.

## Quick Start

### 1. Clone and restore

```bash
git clone https://github.com/Bruce188/FundingRateArb.git
cd FundingRateArb
dotnet restore
```

### 2. Start SQL Server (Docker)

```bash
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=<REPLACE_WITH_STRONG_PASSWORD>" \
  -p 1433:1433 -d mcr.microsoft.com/mssql/server:2022-latest
```

### 3. Configure secrets

```bash
cd src/FundingRateArb.Web

# Database connection
dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
  "Server=localhost,1433;Database=FundingRateArbDb;User Id=SA;Password=<YOUR_SA_PASSWORD>;TrustServerCertificate=True"

# Admin seed password
dotnet user-secrets set "Seed:AdminPassword" "<YOUR_ADMIN_PASSWORD>"

# Lighter (DEX)
dotnet user-secrets set "Exchanges:Lighter:SignerPrivateKey" "<your-private-key>"
dotnet user-secrets set "Exchanges:Lighter:ApiKey" "<api-key-index-2-to-254>"
dotnet user-secrets set "Exchanges:Lighter:AccountIndex" "<your-numeric-account-index>"

# Aster (DEX)
dotnet user-secrets set "Exchanges:Aster:ApiKey" "<your-key>"
dotnet user-secrets set "Exchanges:Aster:ApiSecret" "<your-secret>"

# Hyperliquid (DEX)
# SubAccountAddress is optional — leave empty to trade on the main account
dotnet user-secrets set "Exchanges:Hyperliquid:WalletAddress" "<0x...>"
dotnet user-secrets set "Exchanges:Hyperliquid:PrivateKey" "<your-key>"
dotnet user-secrets set "Exchanges:Hyperliquid:SubAccountAddress" "<0x... optional vault address>"

# Binance (CEX)
dotnet user-secrets set "Exchanges:Binance:ApiKey" "<your-key>"
dotnet user-secrets set "Exchanges:Binance:ApiSecret" "<your-secret>"

# CoinGlass (data-only — powers the analytics dashboard and opportunity screening)
dotnet user-secrets set "Exchanges:CoinGlass:ApiKey" "<your-key>"

# dYdX v4 — NOT configured via user-secrets.
# The infrastructure connector is read-only (indexer polling). User trading
# requires per-user credentials supplied via the /Settings page.
```

### 4. Run

```bash
dotnet run --project src/FundingRateArb.Web
```

The app starts at `http://localhost:5273`. EF Core auto-migrates and seeds an admin account on first run.

## Docker Deployment

```bash
# Create .env file
cat > .env <<EOF
SA_PASSWORD=<REPLACE_WITH_STRONG_PASSWORD>
ADMIN_PASSWORD=<REPLACE_WITH_STRONG_PASSWORD>
LIGHTER_SIGNER_KEY=<your-key>
LIGHTER_API_KEY=2
LIGHTER_ACCOUNT_INDEX=<your-account-index>
ASTER_API_KEY=<your-key>
ASTER_API_SECRET=<your-secret>
HYPERLIQUID_WALLET=<0x...>
HYPERLIQUID_KEY=<your-key>
HYPERLIQUID_SUBACCOUNT=<0x... optional vault address>
BINANCE_API_KEY=<your-key>
BINANCE_API_SECRET=<your-secret>
COINGLASS_API_KEY=<your-key>
EOF

# Build and run
docker compose up -d
```

**Important:** Replace all placeholder values with strong, unique passwords. Never commit `.env` to version control.

The multi-stage Dockerfile builds with the .NET SDK and runs on the lightweight ASP.NET runtime image as a non-root user. Docker Compose orchestrates the app alongside SQL Server 2022 with health checks and volume persistence.

## Testing

```bash
# All tests (unit + integration + .NET E2E)
dotnet test

# Unit tests only
dotnet test tests/FundingRateArb.Tests.Unit

# Integration tests — most use EF Core InMemory, but CI runs them against
# a SQL Server 2022 service container via ConnectionStrings__DefaultConnection
dotnet test tests/FundingRateArb.Tests.Integration

# .NET E2E suite (Playwright-driven, requires the app running)
dotnet test tests/FundingRateArb.Tests.E2E
```

### Python End-to-End Tests (legacy)

```bash
cd tests/playwright
pip install playwright pytest
playwright install chromium
pytest
```

Requires the app running at `http://localhost:5273` with a seeded database.

### Test Coverage

| Suite | Framework | Scope |
|-------|-----------|-------|
| Unit (~1670 tests) | xUnit, Moq, FluentAssertions | Signal engine, position sizer, execution engine, closers, PnL reconciliation, yield calculator, config validator, API key vault, every exchange connector, and all background services |
| Integration (~45 tests) | xUnit, EF Core (InMemory locally, SQL Server in CI) | Repositories, unit-of-work, hub reconnection, dashboard sections, admin pages, health endpoint, startup time |
| .NET E2E (~5 tests) | xUnit + Microsoft.Playwright | Connectivity test page, credential balance flow |
| Python E2E | Playwright (Python), pytest | Authentication, dashboard, admin panel, settings, mobile responsiveness |

## CI/CD

A single GitHub Actions workflow (`.github/workflows/deploy.yml`) runs on every push to `main` and handles both CI and CD in one pipeline. There is no separate `ci.yml` — pull requests are verified locally before merge, and the canonical CI signal is the deploy workflow running on `main`. Documentation-only changes (`documentation/**`) are skipped.

**`build-and-test` job** — restores packages, runs `dotnet format --verify-no-changes`, builds in Release, runs unit tests with XPlat code coverage (Cobertura), enforces a **14% line-coverage threshold**, runs integration tests against a SQL Server 2022 service container, scans for vulnerable NuGet packages, and regex-scans the PR diff for exposed secrets. Test results and coverage reports are uploaded as artifacts.

**`deploy` job** — depends on `build-and-test`. Downloads the build artifact, logs in to Azure via OIDC federation (no stored credentials), runs the EF Core migration bundle against the production database, and deploys to Azure App Service via `azure/webapps-deploy@v3`.

## Resilience

The application uses Polly resilience pipelines to handle exchange API failures gracefully:

| Pipeline | Retry | Circuit Breaker | Timeout | Use Case |
|----------|-------|----------------|---------|----------|
| `ExchangeSdk` | 3x exponential | 50% failure / 30s | 15s | General exchange API calls |
| `OrderExecution` | None | 50% failure / 60s | 30s | Order placement — no retry to prevent double fills |
| `OrderClose` | None | None | 30s | Position close — critical path must not be blocked |

In addition, `BotOrchestrator` runs its own per-opportunity circuit breaker: failed asset+exchange pairs enter exponential-backoff cooldown, the user is paused after `ConsecutiveLossPause` failures, and the whole bot is paused when realized losses cross `DailyDrawdownPausePct`.

Rate limiting is enforced at the web layer:
- **Auth endpoints** (`auth`): 10 requests/min
- **SignalR** (`signalr`): 20 requests/10s with queue depth of 5
- **General** (`general`): 200 requests/min

## Configuration

Bot behavior is configured via the database (`BotConfigurations` table), editable through the admin dashboard at `/Admin/BotConfig` and validated by `IConfigValidator` before saving. The full parameter reference lives in [`documentation/trading-engine.md`](documentation/trading-engine.md); the most-tuned settings are:

### Lifecycle & Capital

| Setting | Description | Default |
|---------|-------------|---------|
| `OperatingState` | Four-state lifecycle: `Stopped` / `Armed` / `Trading` / `Paused` | `Stopped` |
| `IsEnabled` | Legacy kill switch for automated trading | `false` |
| `DryRunEnabled` | Route all order placement through `DryRunConnectorWrapper` | `false` |
| `TotalCapitalUsdc` | Total capital budget in USDC | `39` |
| `MaxCapitalPerPosition` | Max fraction per position (0-1) | `0.90` |
| `MaxConcurrentPositions` | Parallel position limit | `1` |
| `AllocationStrategy` | `Concentrated` / `WeightedSpread` / `EqualSpread` / `RiskAdjusted` | `Concentrated` |
| `DefaultLeverage` | Requested leverage for new positions | `5` |
| `MaxLeverageCap` | Global hard cap regardless of exchange tier max | `3` |
| `MinEdgeMultiplier` | Net edge must exceed `MinEdgeMultiplier × totalEntryCost` to open | `3.0` |

### Thresholds

| Setting | Description | Default |
|---------|-------------|---------|
| `OpenThreshold` | Min spread/hr to open a position | `0.0002` |
| `AlertThreshold` | Spread/hr that triggers an alert | `0.0001` |
| `CloseThreshold` | Spread/hr that triggers close | `-0.00005` |

### Risk Management

| Setting | Description | Default |
|---------|-------------|---------|
| `StopLossPct` | Max loss as fraction of margin | `0.10` |
| `MaxHoldTimeHours` | Auto-close after this many hours | `48` |
| `MinHoldTimeHours` | Hours before `SpreadCollapsed` close can fire | `2` |
| `BreakevenHoursMax` | Max hours to break even on fees | `8` |
| `MinVolume24hUsdc` | Minimum 24h volume to consider | `50,000` |
| `DailyDrawdownPausePct` | Pause bot after this daily drawdown | `0.08` |
| `ConsecutiveLossPause` | Pause after N consecutive losses | `3` |
| `FundingFlipExitCycles` | Close if differential stays inverted for this many cycles | `2` |
| `DivergenceAlertMultiplier` | Mark-divergence multiplier over entry spread that forces close | `2.0` |
| `StablecoinCriticalThresholdPct` | USDT/USDC spread that forces emergency close | `0.01` |
| `LiquidationWarningPct` | `MaxSafeMove` threshold that triggers a liquidation-risk close | — |
| `MarginUtilizationAlertPct` | Per-exchange margin-used threshold raising an alert | — |

### Per-User Overrides (`UserConfiguration`)

`MaxLeverageCap` and `DryRunEnabled` on the user config can only **tighten** the global settings — a user cannot raise their own leverage cap above the bot-wide limit, and a user cannot opt out of global dry-run mode.

Exchange credentials are managed via .NET User Secrets (development) or environment variables / Azure Key Vault (production). Never commit credentials to the repository.

## Security

- ASP.NET Core Identity with strict password policies (12+ chars) and role-based access control
- Exchange API keys encrypted at rest via Data Protection API
- Content Security Policy with pinned CDN URLs and SRI hashes
- X-Frame-Options, X-Content-Type-Options, Referrer-Policy, Permissions-Policy headers
- HttpOnly, SameSite=Strict cookies with 8-hour sliding expiration
- Non-root Docker container user
- GitHub Actions OIDC federation for passwordless Azure deployments
- Rate limiting on all endpoints
- Serilog with sensitive data masking enricher

## Documentation

Detailed documentation is available in [`documentation/`](documentation/):

- [Architecture Overview](documentation/architecture.md) — layer diagram, runtime architecture, data flow, background services
- [Trading Engine](documentation/trading-engine.md) — signal engine, position sizer, execution engine, health monitor, full configuration reference
- [API Reference](documentation/api-reference.md) — all routes, SignalR hub, core interfaces, DTOs
- [Configuration Guide](documentation/configuration.md) — secrets management, identity, logging, rate limiting
- [Deployment Guide](documentation/deployment.md) — local dev, Docker, Azure, CI/CD, migrations

## License

[MIT](LICENSE)
