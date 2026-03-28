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
# SignerPrivateKey: Your signing private key
# ApiKey: Integer 2-254 — the key index assigned when creating an API key on Lighter
# AccountIndex: Your numeric account index on Lighter
dotnet user-secrets set "Exchanges:Lighter:SignerPrivateKey" "<your-private-key>"
dotnet user-secrets set "Exchanges:Lighter:ApiKey" "<api-key-index-2-to-254>"
dotnet user-secrets set "Exchanges:Lighter:AccountIndex" "<your-numeric-account-index>"

# Aster (DEX)
dotnet user-secrets set "Exchanges:Aster:ApiKey" "<your-key>"
dotnet user-secrets set "Exchanges:Aster:ApiSecret" "<your-secret>"

# Hyperliquid (DEX)
# SubAccountAddress: Optional vault address for sub-account trading. Leave empty for main account.
dotnet user-secrets set "Exchanges:Hyperliquid:WalletAddress" "<0x...>"
dotnet user-secrets set "Exchanges:Hyperliquid:PrivateKey" "<your-key>"
dotnet user-secrets set "Exchanges:Hyperliquid:SubAccountAddress" "<0x... optional vault address>"

# OAuth — optional
dotnet user-secrets set "Authentication:Google:ClientId" "<id>"
dotnet user-secrets set "Authentication:Google:ClientSecret" "<secret>"
dotnet user-secrets set "Authentication:GitHub:ClientId" "<id>"
dotnet user-secrets set "Authentication:GitHub:ClientSecret" "<secret>"

# SendGrid — optional (for email notifications)
dotnet user-secrets set "SendGrid:ApiKey" "<key>"
```

### Production Setup

In production, use Azure Key Vault or App Service configuration slots as the primary method for storing secrets. Avoid setting secrets via shell `export` commands or `.bashrc` -- values set this way are visible in `/proc/*/environ`.

If environment variables must be used directly:

```bash
# Required
ConnectionStrings__DefaultConnection="Server=...;Database=...;Password=..."
Seed__AdminPassword="..."

# Lighter (DEX)
Exchanges__Lighter__SignerPrivateKey="..."
Exchanges__Lighter__ApiKey="<2-254>"
Exchanges__Lighter__AccountIndex="<numeric>"

# Aster (DEX)
Exchanges__Aster__ApiKey="..."
Exchanges__Aster__ApiSecret="..."

# Hyperliquid (DEX)
Exchanges__Hyperliquid__WalletAddress="0x..."
Exchanges__Hyperliquid__PrivateKey="..."
Exchanges__Hyperliquid__SubAccountAddress="0x... (optional)"

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
    "Google": { "ClientId": "REPLACE_VIA_USER_SECRETS", "ClientSecret": "REPLACE_VIA_USER_SECRETS" },
    "GitHub": { "ClientId": "REPLACE_VIA_USER_SECRETS", "ClientSecret": "REPLACE_VIA_USER_SECRETS" }
  },
  "SendGrid": {
    "ApiKey": "REPLACE_VIA_USER_SECRETS",
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
