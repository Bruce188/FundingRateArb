# FundingRateArb

[![CI](https://github.com/Bruce188/FundingRateArb/actions/workflows/ci.yml/badge.svg)](https://github.com/Bruce188/FundingRateArb/actions/workflows/ci.yml)
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

Automated funding rate arbitrage bot for perpetual futures. Monitors funding rate differentials across DEXs (Lighter, Aster, HyperLiquid), opens hedged long/short positions when spreads exceed a configurable threshold, and collects the funding rate differential as yield.

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
    ├── FundingRateArb.Tests.Integration  # EF Core in-memory database tests
    └── playwright/                       # End-to-end browser tests (Python)
```

| Layer | Responsibility |
|-------|---------------|
| **Domain** | Entities (`ArbitragePosition`, `BotConfiguration`, `Alert`, `Asset`), enums, no external dependencies |
| **Application** | Service interfaces, DTOs, business logic (`SignalEngine`, `PositionSizer`, `ExecutionEngine`, `YieldCalculator`) |
| **Infrastructure** | EF Core (`AppDbContext`, migrations), exchange connectors (Lighter, Aster, HyperLiquid), SignalR hub, background services, Data Protection vault |
| **Web** | Controllers, Razor views, `Program.cs` DI/middleware setup, static assets |

## Key Components

| Component | Purpose |
|-----------|---------|
| **SignalEngine** | Scores funding rate spreads across all exchange pairs with adaptive threshold fallback and optional ML-based rate prediction |
| **PositionSizer** | Allocates capital using configurable strategies (Concentrated, WeightedSpread, EqualSpread, RiskAdjusted) with per-asset/exchange exposure limits |
| **ExecutionEngine** | Concurrent dual-leg order placement with pre-flight margin checks and atomic rollback on partial fills |
| **BotOrchestrator** | Background service running 60-second trading cycles with circuit breaker, consecutive loss tracking, and alert deduplication |
| **PositionHealthMonitor** | Monitors open positions for spread collapse, max hold time, stop loss, P&L targets, and stale price feeds |
| **YieldCalculator** | Computes annualized yield, projected/unrealized P&L, and break-even hours |
| **MarketDataCache** | In-memory cache of latest rates from both REST polling and WebSocket streams |
| **DashboardHub** | SignalR hub pushing real-time rate updates, opportunities, position changes, and alerts to connected clients; Dashboard supports anonymous access with cached opportunities |
| **ApiKeyVault** | Encrypted exchange credential storage using ASP.NET Core Data Protection API |

## Exchange Integrations

| Exchange | Type | Settlement | Connection | Auth |
|----------|------|-----------|------------|------|
| **HyperLiquid** | DEX | Continuous | SDK (HyperLiquid.Net) + WebSocket | Wallet-based |
| **Lighter** | DEX | Continuous | Custom REST + WebSocket | Custom signer (zkLighter) |
| **Aster** | DEX | Periodic (8h) | SDK (Aster.Net) + WebSocket | API key |
| **CoinGlass** | Data | N/A | REST | API key |

CoinGlass provides supplementary volume data and is flagged as `IsDataOnly` (not used for trading).

Each exchange implements `IExchangeConnector` (REST) and `IMarketDataStream` (WebSocket). The `ExchangeConnectorFactory` manages connector lifecycle with key rotation and rate-limit cooldown.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- SQL Server 2022+ (or Docker)
- Exchange API credentials (Lighter, Aster, optionally HyperLiquid)

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
dotnet user-secrets set "Exchanges:Hyperliquid:WalletAddress" "<0x...>"
dotnet user-secrets set "Exchanges:Hyperliquid:PrivateKey" "<your-key>"
dotnet user-secrets set "Exchanges:Hyperliquid:SubAccountAddress" "<0x... optional vault address>"
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
EOF

# Build and run
docker compose up -d
```

**Important:** Replace all placeholder values with strong, unique passwords. Never commit `.env` to version control.

The multi-stage Dockerfile builds with the .NET SDK and runs on the lightweight ASP.NET runtime image as a non-root user. Docker Compose orchestrates the app alongside SQL Server 2022 with health checks and volume persistence.

## Testing

```bash
# All tests
dotnet test

# Unit tests only
dotnet test tests/FundingRateArb.Tests.Unit

# Integration tests (uses EF Core in-memory database)
dotnet test tests/FundingRateArb.Tests.Integration
```

### End-to-End Tests (Playwright)

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
| Unit (747 tests) | xUnit, Moq, FluentAssertions | SignalEngine, PositionSizer, ExecutionEngine, YieldCalculator, ConfigValidator, ApiKeyVault, Connectors |
| Integration (28 tests) | xUnit, EF Core InMemory | Repositories, UnitOfWork, database persistence |
| E2E | Playwright (Python), pytest | Authentication, dashboard, admin panel, settings, mobile responsiveness |

## CI/CD

Two GitHub Actions workflows automate the build and deployment pipeline:

**Pull Requests** (`ci.yml`) — builds the solution, checks code formatting, runs unit tests with coverage enforcement (15% threshold) and integration tests against SQL Server, scans for vulnerable packages and exposed secrets, checks license compliance, detects code duplication, verifies all NuGet packages exist on nuget.org, and verifies the Docker image builds.

**Deploy** (`deploy.yml`) — triggers on merge to `main`. Runs the full test suite, generates an EF Core migration bundle, and deploys to Azure App Service using OIDC federation (no stored credentials).

## Resilience

The application uses Polly resilience pipelines to handle exchange API failures gracefully:

| Pipeline | Strategy | Use Case |
|----------|----------|----------|
| `ExchangeSdk` | Retry (3x exponential) + circuit breaker + 15s timeout | General exchange API calls |
| `OrderExecution` | Circuit breaker + 30s timeout (no retry) | Order placement — prevents double fills |
| `OrderClose` | 30s timeout only | Position close — critical path |

Rate limiting is enforced at the web layer:
- **Auth endpoints**: 10 requests/min
- **SignalR**: 20 requests/10s with queue depth of 5
- **General**: 200 requests/min

## Configuration

Bot behavior is configured via the database (`BotConfigurations` table), editable through the admin dashboard:

| Setting | Description | Default |
|---------|-------------|---------|
| `IsEnabled` | Kill switch for automated trading | `false` |
| `TotalCapitalUsdc` | Total capital budget in USDC | `39` |
| `MaxCapitalPerPosition` | Max fraction per position (0-1) | `0.90` |
| `DefaultLeverage` | Leverage for new positions | `5` |
| `OpenThreshold` | Min spread/hr to open a position | `0.0002` |
| `CloseThreshold` | Spread/hr that triggers close | `-0.00005` |
| `StopLossPct` | Max loss as fraction of margin | `0.10` |
| `MaxHoldTimeHours` | Auto-close after this many hours | `48` |
| `BreakevenHoursMax` | Max hours to break even on fees | `8` |
| `MaxConcurrentPositions` | Parallel position limit | `1` |
| `AllocationStrategy` | Capital split strategy | `Concentrated` |

Exchange credentials are managed via .NET User Secrets (development) or environment variables (production). Never commit credentials to the repository.

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
