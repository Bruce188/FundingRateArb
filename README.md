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

Automated funding rate arbitrage bot for perpetual futures. Monitors funding rate differentials across DEXs (Lighter, Aster) and CEXs (HyperLiquid), opens hedged long/short positions when spreads exceed a configurable threshold, and collects the funding rate differential as yield.

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

- **SignalEngine** — scores funding rate spreads across exchange pairs, adaptive threshold fallback
- **PositionSizer** — calculates position size from capital, leverage, and liquidity limits
- **ExecutionEngine** — concurrent dual-leg order placement with pre-flight margin checks and emergency close
- **BotOrchestrator** — background service running 60-second cycles (fetch rates, monitor positions, open/close trades)
- **PositionHealthMonitor** — monitors open positions for health degradation and triggers alerts
- **YieldCalculator** — computes realized and projected yield from funding rate differentials
- **BalanceAggregator** — aggregates balances across all connected exchange accounts
- **Dashboard** — real-time MVC + SignalR UI showing rates, opportunities, positions, alerts, and KPIs
- **ApiKeyVault** — encrypted exchange credential storage using ASP.NET Core Data Protection API

## Exchange Integrations

| Exchange | Type | Connection | Features |
|----------|------|------------|----------|
| **Lighter** | DEX | REST API + WebSocket | Custom signer, market data streaming, order placement |
| **Aster** | DEX | SDK (Aster.Net) + WebSocket | Funding rates, order execution, market data |
| **HyperLiquid** | CEX | SDK (HyperLiquid.Net) + WebSocket | Wallet-based auth, funding rates, positions |

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
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=YourPassword123!" \
  -p 1433:1433 -d mcr.microsoft.com/mssql/server:2022-latest
```

### 3. Configure secrets

```bash
cd src/FundingRateArb.Web

# Database connection
dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
  "Server=localhost,1433;Database=FundingRateArbDb;User Id=SA;Password=YourPassword123!;TrustServerCertificate=True"

# Admin seed password
dotnet user-secrets set "Seed:AdminPassword" "YourAdminPassword!"

# Exchange credentials
dotnet user-secrets set "Exchanges:Lighter:SignerPrivateKey" "<your-key>"
dotnet user-secrets set "Exchanges:Lighter:ApiKey" "2"
dotnet user-secrets set "Exchanges:Aster:ApiKey" "<your-key>"
dotnet user-secrets set "Exchanges:Aster:ApiSecret" "<your-secret>"

# Optional: HyperLiquid
dotnet user-secrets set "Exchanges:Hyperliquid:WalletAddress" "<0x...>"
dotnet user-secrets set "Exchanges:Hyperliquid:PrivateKey" "<your-key>"
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
SA_PASSWORD=YourStrongPassword123!
ADMIN_PASSWORD=YourAdminPassword!
LIGHTER_SIGNER_KEY=<your-key>
LIGHTER_API_KEY=2
ASTER_API_KEY=<your-key>
ASTER_API_SECRET=<your-secret>
EOF

# Build and run
docker compose up -d
```

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
| Unit | xUnit, Moq, FluentAssertions | SignalEngine, PositionSizer, ExecutionEngine, YieldCalculator, ConfigValidator, ApiKeyVault |
| Integration | xUnit, EF Core InMemory | Repositories, UnitOfWork, database persistence |
| E2E | Playwright (Python), pytest | Authentication, dashboard, admin panel, settings, mobile responsiveness |

## CI/CD

Two GitHub Actions workflows automate the build and deployment pipeline:

**Pull Requests** (`ci.yml`) — builds the solution, runs unit and integration tests against SQL Server, and verifies the Docker image builds and starts successfully.

**Deploy** (`deploy.yml`) — triggers on merge to `main`. Runs the full test suite, generates an EF Core migration bundle, and deploys to Azure App Service using OIDC federation (no stored credentials).

## Resilience

The application uses Polly resilience pipelines to handle exchange API failures gracefully:

| Pipeline | Strategy | Use Case |
|----------|----------|----------|
| `ExchangeSdk` | Retry + circuit breaker + 15s timeout | General exchange API calls |
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
| `TotalCapitalUsdc` | Total capital budget in USDC | — |
| `MaxCapitalPerPosition` | Max fraction per position (0-1) | `0.90` |
| `DefaultLeverage` | Leverage for new positions | `20` |
| `OpenThreshold` | Min spread/hr to open a position | `0.00003` |
| `BreakevenHoursMax` | Max hours to break even on fees | `6` |
| `MaxConcurrentPositions` | Parallel position limit | `1` |

Exchange credentials are managed via .NET User Secrets (development) or environment variables (production). Never commit credentials to the repository.

## Security

- ASP.NET Core Identity with strict password policies and role-based access control
- Exchange API keys encrypted at rest via Data Protection API
- Content Security Policy, X-Frame-Options, and strict cookie settings
- Non-root Docker container user
- GitHub Actions OIDC federation for passwordless Azure deployments
- Rate limiting on all endpoints

## License

[MIT](LICENSE)
