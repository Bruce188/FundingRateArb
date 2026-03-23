# Configuration Guide

## Secrets Management

FundingRateArb uses .NET User Secrets for development and environment variables (or Azure Key Vault) for production. **Never commit credentials to the repository.**

### Development Setup

```bash
cd src/FundingRateArb.Web

# Database connection
dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
  "Server=localhost,1433;Database=FundingRateArbDb;User Id=SA;Password=<YOUR_SA_PASSWORD>;TrustServerCertificate=True"

# Admin seed password
dotnet user-secrets set "Seed:AdminPassword" "<YOUR_ADMIN_PASSWORD>"

# Lighter (DEX)
dotnet user-secrets set "Exchanges:Lighter:SignerPrivateKey" "<your-key>"
dotnet user-secrets set "Exchanges:Lighter:ApiKey" "2"

# Aster (DEX)
dotnet user-secrets set "Exchanges:Aster:ApiKey" "<your-key>"
dotnet user-secrets set "Exchanges:Aster:ApiSecret" "<your-secret>"

# HyperLiquid (CEX) — optional
dotnet user-secrets set "Exchanges:Hyperliquid:WalletAddress" "<0x...>"
dotnet user-secrets set "Exchanges:Hyperliquid:PrivateKey" "<your-key>"

# OAuth — optional
dotnet user-secrets set "Authentication:Google:ClientId" "<id>"
dotnet user-secrets set "Authentication:Google:ClientSecret" "<secret>"
dotnet user-secrets set "Authentication:GitHub:ClientId" "<id>"
dotnet user-secrets set "Authentication:GitHub:ClientSecret" "<secret>"

# SendGrid — optional (for email notifications)
dotnet user-secrets set "SendGrid:ApiKey" "<key>"
```

### Production Setup

In production, configure via environment variables or Azure Key Vault:

```bash
# Required
ConnectionStrings__DefaultConnection="Server=...;Database=...;Password=..."
Seed__AdminPassword="..."

# Exchange credentials
Exchanges__Lighter__SignerPrivateKey="..."
Exchanges__Lighter__ApiKey="2"
Exchanges__Aster__ApiKey="..."
Exchanges__Aster__ApiSecret="..."

# Azure Key Vault (auto-loads all secrets when set)
KeyVaultName="your-keyvault-name"

# Application Insights
ApplicationInsights__ConnectionString="InstrumentationKey=..."

# Data Protection (Azure Blob Storage)
DataProtection__BlobStorageConnection="DefaultEndpointsProtocol=https;..."
```

## Application Settings

`appsettings.json` contains non-sensitive defaults:

```json
{
  "KeyVaultName": "",
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost,1433;Database=FundingRateArbDb;..."
  },
  "Exchanges": {
    "Hyperliquid": { "InfraKeys": [] },
    "Aster": { "InfraKeys": [] },
    "Lighter": { "ApiKeyIndex": 2, "InfraKeys": [] }
  },
  "Authentication": {
    "Google": { "ClientId": "", "ClientSecret": "" },
    "GitHub": { "ClientId": "", "ClientSecret": "" }
  },
  "SendGrid": {
    "ApiKey": "",
    "FromEmail": "noreply@fundingarb.com",
    "FromName": "FundingRateArb"
  },
  "Logging": {
    "LogLevel": { "Default": "Information" }
  }
}
```

The `InfraKeys` arrays allow multiple API keys per exchange for infrastructure-level rate polling (key rotation via `ExchangeConnectorFactory`).

## Bot Configuration

Bot trading parameters are stored in the database (`BotConfigurations` table) and edited via the admin dashboard at `/Admin/BotConfig`. See [Trading Engine](trading-engine.md) for full parameter reference.

The admin dashboard validates configuration via `IConfigValidator` before saving:
- Thresholds must be ordered: `OpenThreshold > AlertThreshold > CloseThreshold`
- Capital and leverage within valid ranges
- Allocation constraints consistent with position limits

## Identity Configuration

ASP.NET Core Identity is configured with:

| Setting | Value |
|---------|-------|
| Min password length | 12 characters |
| Require digit | Yes |
| Require lowercase | Yes |
| Require uppercase | Yes |
| Require special character | Yes |
| Lockout time | 15 minutes |
| Max failed attempts | 5 |

## Logging

Development:
- Console output with structured format
- Rolling file logs in `logs/fundingratearb-{date}.log` (30-day retention)

Production:
- Console output
- SQL Server audit table (`AuditLogs`) for Warning+ events (auto-created)
- Application Insights telemetry (when configured)
- Sensitive data masking via `SensitiveDataMaskingEnricher`

## Rate Limiting

| Policy | Window | Limit | Applies To |
|--------|--------|-------|------------|
| `auth` | 1 minute | 10 requests | Identity/Razor Pages |
| `signalr` | 10 seconds | 20 requests (queue: 5) | `/hubs/dashboard` |
| `general` | 1 minute | 200 requests | All MVC routes |

## Launch Profiles

| Profile | URL | Environment |
|---------|-----|-------------|
| http | `http://localhost:5273` | Development |
| https | `https://localhost:7185` | Development |

## Health Check

`GET /health` returns database connectivity status via `AddDbContextCheck<AppDbContext>()`.
