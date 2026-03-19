# FundingRateArb

Automated funding rate arbitrage bot for perpetual futures. Monitors funding rate differentials across DEXs (Lighter, Aster) and CEXs (HyperLiquid), opens hedged long/short positions when spreads exceed a configurable threshold, and collects the funding rate differential as yield.

## Architecture

```
FundingRateArb.sln
├── src/
│   ├── FundingRateArb.Domain          # Entities, enums, value objects
│   ├── FundingRateArb.Application     # Services, DTOs, interfaces
│   ├── FundingRateArb.Infrastructure  # EF Core, exchange connectors, SignalR
│   └── FundingRateArb.Web            # ASP.NET Core MVC, controllers, views
└── tests/
    ├── FundingRateArb.Tests.Unit
    └── FundingRateArb.Tests.Integration
```

**Key components:**
- **SignalEngine** — scores funding rate spreads across exchange pairs
- **PositionSizer** — calculates position size from capital, leverage, and liquidity limits
- **ExecutionEngine** — concurrent dual-leg order placement with pre-flight margin checks and emergency close
- **BotOrchestrator** — background service running 60-second cycles (fetch rates, monitor positions, open/close trades)
- **Dashboard** — real-time MVC + SignalR UI showing rates, opportunities, positions, and alerts

## Tech Stack

- .NET 8, ASP.NET Core MVC, SignalR
- SQL Server, Entity Framework Core
- Serilog (console, file, SQL Server audit sink)
- ASP.NET Core Identity (authentication, RBAC)
- Polly (resilience pipelines for exchange SDK calls)
- CryptoExchange.Net (Aster.Net, HyperLiquid.Net SDKs)
- Lighter DEX REST API + native signer

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- SQL Server 2019+ (or Docker)
- Exchange API credentials (Lighter, Aster, optionally HyperLiquid)

## Quick Start

### 1. Clone and restore

```bash
git clone <repo-url>
cd eindproject
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

The app is available at `http://localhost:5273`.

## Testing

```bash
# All tests
dotnet test

# Unit tests only
dotnet test tests/FundingRateArb.Tests.Unit

# Integration tests (requires SQL Server)
dotnet test tests/FundingRateArb.Tests.Integration
```

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

## Project Structure

| Layer | Responsibility |
|-------|---------------|
| **Domain** | Entities (`ArbitragePosition`, `BotConfiguration`, `Alert`), enums, no dependencies |
| **Application** | Service interfaces, DTOs, business logic (`SignalEngine`, `PositionSizer`, `ExecutionEngine`) |
| **Infrastructure** | EF Core (`AppDbContext`), exchange connectors, SignalR hub, background services |
| **Web** | Controllers, Razor views, `Program.cs` DI setup, static assets |

## License

MIT
