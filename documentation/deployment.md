# Deployment Guide

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- SQL Server 2022+ (or Docker)
- Exchange API credentials

## Local Development

### 1. Clone and Restore

```bash
git clone https://github.com/Bruce188/FundingRateArb.git
cd FundingRateArb
dotnet restore
```

### 2. Start SQL Server

```bash
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=<REPLACE_WITH_STRONG_PASSWORD>" \
  -p 1433:1433 -d mcr.microsoft.com/mssql/server:2022-latest
```

### 3. Configure Secrets

See [Configuration Guide](configuration.md) for full secrets setup.

### 4. Run

```bash
dotnet run --project src/FundingRateArb.Web
```

The app starts at `http://localhost:5273`. EF Core auto-migrates and seeds an admin account on first run.

## Docker Deployment

### Docker Compose

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

The multi-stage Dockerfile:
1. Builds with the .NET 8 SDK image
2. Runs on the lightweight ASP.NET runtime image
3. Executes as a non-root user
4. Docker Compose orchestrates app + SQL Server with health checks and volume persistence

## Azure Deployment

### CI/CD Pipeline

Two GitHub Actions workflows:

**`ci.yml`** (Pull Requests):
- Restores dependencies and verifies all NuGet packages exist on nuget.org
- Checks code formatting (`dotnet format`)
- Builds the solution (Release)
- Runs unit tests with code coverage enforcement (15% threshold)
- Runs integration tests against SQL Server
- Scans for vulnerable NuGet packages
- Scans for exposed secrets in the diff
- Checks package license compliance (advisory)
- Detects code duplication with jscpd (advisory)
- Verifies Docker image builds and starts

**`deploy.yml`** (Merge to main):
- Runs full test suite
- Generates EF Core migration bundle (self-contained)
- Deploys to Azure App Service via OIDC federation (no stored credentials)

### Azure Resources

| Resource | Purpose |
|----------|---------|
| App Service (Linux) | Hosts the application |
| Azure SQL | Production database |
| Azure Key Vault | Secrets management |
| Azure Blob Storage | Data Protection key persistence |
| Application Insights | Telemetry and monitoring |
| GitHub Actions CI/CD | Build, test, deploy automation |

### Required Configuration

Set in Azure App Service configuration or Key Vault:

```
ConnectionStrings__DefaultConnection=Server=tcp:<server>.database.windows.net;...
KeyVaultName=<your-keyvault>
ApplicationInsights__ConnectionString=InstrumentationKey=...
DataProtection__BlobStorageConnection=DefaultEndpointsProtocol=https;...
Exchanges__Lighter__SignerPrivateKey=...
Exchanges__Lighter__AccountIndex=<numeric>
Exchanges__Aster__ApiKey=...
Exchanges__Aster__ApiSecret=...
Exchanges__Hyperliquid__WalletAddress=0x...
Exchanges__Hyperliquid__PrivateKey=...
Exchanges__Hyperliquid__SubAccountAddress=0x... (optional)
SendGrid__ApiKey=...
```

## Database Migrations

### Apply Migrations

Migrations apply automatically on startup via `db.Database.MigrateAsync()`.

### Create New Migration

```bash
dotnet ef migrations add <Name> \
  --project src/FundingRateArb.Infrastructure \
  --startup-project src/FundingRateArb.Web
```

### Generate Migration Bundle (CI/CD)

```bash
dotnet ef migrations bundle \
  --project src/FundingRateArb.Infrastructure \
  --startup-project src/FundingRateArb.Web \
  --output efbundle
./efbundle --connection "Server=..."
```

## Testing

```bash
# All tests
dotnet test

# Unit tests only
dotnet test tests/FundingRateArb.Tests.Unit

# Integration tests (EF Core in-memory)
dotnet test tests/FundingRateArb.Tests.Integration

# E2E (requires running app)
cd tests/playwright
pip install playwright pytest
playwright install chromium
pytest
```

## Health Monitoring

- **Health endpoint:** `GET /health` (database connectivity check)
- **Application Insights:** Auto-collects request metrics, exceptions, dependencies
- **Serilog audit logs:** Warning+ events logged to `AuditLogs` SQL table in production
- **SignalR:** Real-time bot status pushed to connected dashboards
