# API Reference

FundingRateArb is a server-rendered MVC application with SignalR for real-time updates. This document covers all controller routes and the SignalR hub.

## Web Routes

### Dashboard

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| GET | `/` | Yes | Main dashboard (redirects to `/Dashboard`) |
| GET | `/Dashboard` | No* | Dashboard with bot status, opportunities. Authenticated users also see positions, alerts, P&L. *Anonymous users see cached opportunities with 30s TTL. |

### Positions

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| GET | `/Positions` | Yes | List positions (user's own, or all if admin) |
| GET | `/Positions/Details/{id}` | Yes | Position detail: entry/exit prices, fees, funding history, P&L chart |
| POST | `/Positions/Close/{id}` | Yes | Close an open position (triggers dual-leg close via ExecutionEngine) |

### Alerts

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| GET | `/Alerts` | Yes | List alerts with severity filtering |

### Opportunities

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| GET | `/Opportunities` | Yes | Current arbitrage opportunities (redirects to Dashboard) |

### Settings

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| GET | `/Settings` | Yes | User preferences page |
| POST | `/Settings/ApiKeys` | Yes | Save exchange API credentials (encrypted). Validates Ethereum address format for SubAccountAddress and integer range 2-254 for ApiKeyIndex. |
| POST | `/Settings/Preferences` | Yes | Update exchange/asset toggles, email settings |

### Analytics

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| GET | `/Analytics` | Yes | Analytics views |
| GET | `/Analytics/RateAnalytics` | Yes | Funding rate trends and statistics |
| GET | `/Analytics/TradeAnalytics` | Yes | Trade performance metrics |

### Authentication

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| GET | `/Identity/Account/Login` | No | Login page |
| GET | `/Identity/Account/Register` | No | Registration page |
| POST | `/Identity/Account/Logout` | Yes | Logout |
| GET | `/Account/ExternalLogin` | No | OAuth callback (Google, GitHub) |

### Health

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| GET | `/health` | No | Database health check |

## Admin Area

All admin routes require the `Admin` role.

### Overview

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/Admin/Overview` | System-wide dashboard: all positions, alerts, exchange health |

### Bot Configuration

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/Admin/BotConfig` | View/edit global bot configuration |
| POST | `/Admin/BotConfig` | Save bot configuration (validated by `IConfigValidator`) |

### Exchanges

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/Admin/Exchange` | List all exchanges |
| GET | `/Admin/Exchange/Create` | New exchange form |
| POST | `/Admin/Exchange/Create` | Create exchange |
| GET | `/Admin/Exchange/Edit/{id}` | Edit exchange form |
| POST | `/Admin/Exchange/Edit/{id}` | Update exchange |
| POST | `/Admin/Exchange/Delete/{id}` | Delete exchange |

### Assets

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/Admin/Asset` | List all assets |
| GET | `/Admin/Asset/Create` | New asset form |
| POST | `/Admin/Asset/Create` | Create asset |
| GET | `/Admin/Asset/Edit/{id}` | Edit asset form |
| POST | `/Admin/Asset/Edit/{id}` | Update asset |
| POST | `/Admin/Asset/Delete/{id}` | Delete asset |

### Users

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/Admin/Users` | List all users with roles |
| POST | `/Admin/Users/AssignRole` | Assign role to user |

## SignalR Hub

**Endpoint:** `/hubs/dashboard`

The `DashboardHub` pushes real-time updates to connected clients. All messages are server-to-client.

### Client Methods (IDashboardClient)

| Method | Payload | Trigger |
|--------|---------|---------|
| `ReceiveRateUpdate` | `FundingRateDto` | New funding rate received |
| `ReceiveOpportunity` | `ArbitrageOpportunityDto` | New opportunity above threshold |
| `ReceivePositionUpdate` | `PositionSummaryDto` | Position opened, closed, or status changed |
| `ReceiveAlert` | `AlertDto` | New alert created |
| `ReceiveBotStatus` | `{ enabled: bool }` | Bot enabled/disabled |

### Connection

```javascript
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/hubs/dashboard")
    .withAutomaticReconnect()
    .build();

connection.on("ReceiveRateUpdate", (rate) => { /* update UI */ });
connection.on("ReceiveAlert", (alert) => { /* show notification */ });

await connection.start();
```

## Core Interfaces

### IExchangeConnector

Uniform REST API interface for all exchanges:

```csharp
public interface IExchangeConnector
{
    string ExchangeName { get; }
    Task<List<FundingRateDto>> GetFundingRatesAsync(CancellationToken ct = default);
    Task<OrderResultDto> PlaceMarketOrderAsync(string asset, Side side,
        decimal sizeUsdc, int leverage, CancellationToken ct = default);
    Task<OrderResultDto> ClosePositionAsync(string asset, Side side,
        CancellationToken ct = default);
    Task<decimal> GetMarkPriceAsync(string asset, CancellationToken ct = default);
    Task<decimal> GetAvailableBalanceAsync(CancellationToken ct = default);
    Task<int?> GetMaxLeverageAsync(string asset, CancellationToken ct = default);
    Task<DateTime?> GetNextFundingTimeAsync(string asset, CancellationToken ct = default);
}
```

Implementations: `HyperliquidConnector`, `AsterConnector`, `LighterConnector`, `CoinGlassConnector` (data-only)

### IMarketDataStream

WebSocket interface for real-time data:

```csharp
public interface IMarketDataStream : IAsyncDisposable
{
    string ExchangeName { get; }
    bool IsConnected { get; }
    event Action<FundingRateDto>? OnRateUpdate;
    event Action<string, string>? OnDisconnected;
    Task StartAsync(IEnumerable<string> symbols, CancellationToken ct);
    Task StopAsync();
}
```

Implementations: `HyperliquidMarketDataStream`, `AsterMarketDataStream`, `LighterMarketDataStream`

### IUnitOfWork

Coordinates all repository access:

```csharp
public interface IUnitOfWork : IDisposable
{
    IExchangeRepository Exchanges { get; }
    IAssetRepository Assets { get; }
    IFundingRateRepository FundingRates { get; }
    IPositionRepository Positions { get; }
    IAlertRepository Alerts { get; }
    IBotConfigRepository BotConfig { get; }
    IExchangeAssetConfigRepository ExchangeAssetConfigs { get; }
    IUserExchangeCredentialRepository UserCredentials { get; }
    IUserConfigurationRepository UserConfigurations { get; }
    IUserPreferenceRepository UserPreferences { get; }
    IOpportunitySnapshotRepository OpportunitySnapshots { get; }
    Task<int> SaveAsync(CancellationToken ct = default);
}
```

## Key DTOs

### FundingRateDto
```
ExchangeName, Symbol, RatePerHour, RawRate, MarkPrice, IndexPrice,
Volume24hUsd, NextSettlementUtc
```

### ArbitrageOpportunityDto
```
AssetSymbol, LongExchangeName, ShortExchangeName,
LongRatePerHour, ShortRatePerHour, SpreadPerHour, NetYieldPerHour,
AnnualizedYield, Volume24h, MarkPrices,
PredictedSpread, PredictionConfidence, MinutesToNextSettlement
```

### OrderResultDto
```
Success, OrderId, ExecutedPrice, ExecutedSize, Error
```

### PositionSummaryDto
```
Id, AssetSymbol, LongExchangeName, ShortExchangeName, SizeUsdc,
EntrySpreadPerHour, CurrentSpreadPerHour, AccumulatedFunding,
UnrealizedPnl, RealizedPnl, Status, OpenedAt
```
