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

A single GitHub Actions workflow (`.github/workflows/deploy.yml`) runs on every push to `main` and handles both CI and CD in one job. There is no separate `ci.yml`; pull requests are currently verified locally before merge, and the canonical CI signal is the deploy workflow running on main.

**`deploy.yml`** (push to `main`, `workflow_dispatch`):

CI steps (`build-and-test` job):
1. Checkout
2. Setup .NET 8
3. Restore NuGet packages
4. `dotnet format --verify-no-changes` (format gate)
5. `dotnet build -c Release`
6. Unit tests with XPlat code coverage (Cobertura)
7. Coverage threshold check — fails below **14%** line coverage
8. Integration tests against a SQL Server 2022 service container
9. Upload TRX + coverage artifacts
10. `dotnet list package --vulnerable --include-transitive` (fails on vulnerable packages)
11. Secret scan on the PR diff (regex for AWS keys, API keys, private keys, `Password=...`)

Deploy steps (`deploy` job, depends on `build-and-test`):
1. Download build artifacts
2. Azure login via OIDC federation (no stored credentials)
3. Run EF Core migration bundle against the production database
4. Deploy to Azure App Service via `azure/webapps-deploy@v3`

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
# All tests (unit + integration + .NET E2E)
dotnet test

# Unit tests only
dotnet test tests/FundingRateArb.Tests.Unit

# Integration tests (SQL Server required)
dotnet test tests/FundingRateArb.Tests.Integration

# .NET E2E (Playwright-driven, requires running app)
dotnet test tests/FundingRateArb.Tests.E2E

# Python E2E suite (legacy, requires running app)
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
