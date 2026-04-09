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
| GET | `/Analytics?skip&take&days` | Yes | Trade performance metrics and hourly spread history per position |

### Diagnostics

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| GET | `/Diagnostics` | Yes | Pipeline diagnostics banner: ratings loaded, filtered, edge-guardrail rejections, passing count |

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

### Exchange Analytics

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/Admin/ExchangeAnalytics` | CoinGlass-driven view of exchange overviews, spread opportunities, rate comparisons, and discovery events |

### Connectivity Test

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/Admin/ConnectivityTest` | Admin diagnostics UI for testing per-user exchange connectivity |
| POST | `/Admin/ConnectivityTest/RunTest` | Runs a live test against a specific `(userId, exchangeId)` credential pair |
| GET | `/Admin/ConnectivityTest/GetUserExchanges?userId=` | Lists the exchanges a user has credentials for |

## SignalR Hub

**Endpoint:** `/hubs/dashboard`

The `DashboardHub` pushes real-time updates to connected clients. All messages are server-to-client.

### Hub Methods

The hub joins every connection to the `MarketData` group, a user-scoped group (`user-{userId}`), and — for admins — the `Admins` group.

| Method | Description |
|--------|-------------|
| `OnConnectedAsync` | Joins MarketData / user / Admins groups |
| `RejoinGroups` | Explicit re-join (client calls after reconnect) |
| `RequestFullUpdate` | Returns the current `DashboardDto` for the caller |

### Client Methods (IDashboardClient)

| Method | Payload | Trigger |
|--------|---------|---------|
| `ReceiveDashboardUpdate` | `DashboardDto` | Full dashboard refresh (bot state, positions, PnL, alerts) |
| `ReceiveFundingRateUpdate` | `List<FundingRateDto>` | New or updated funding rates |
| `ReceiveOpportunityUpdate` | `OpportunityResultDto` | Opportunity scan completed |
| `ReceivePositionUpdate` | `PositionSummaryDto` | Position opened, closed, or status changed |
| `ReceivePositionRemoval` | `int` (position id) | Position removed from dashboard view |
| `ReceiveBalanceUpdate` | `BalanceSnapshotDto` | Aggregated balance snapshot refreshed |
| `ReceiveAlert` | `AlertDto` | New alert created |
| `ReceiveNotification` | `string` | General notification toast |
| `ReceiveStatusExplanation` | `(string message, string severity)` | Bot status rationale (e.g. why trading is paused) |
| `ReceiveConnectivityLog` | `(string exchangeName, string message)` | Admin connectivity-test log line |

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

Uniform REST API interface for all exchanges. Optional members (default to stub implementations) let individual connectors opt in to capabilities like PnL queries or exchange-side position reconciliation.

```csharp
public interface IExchangeConnector
{
    string ExchangeName { get; }
    bool IsEstimatedFillExchange { get; }

    // Market data
    Task<List<FundingRateDto>> GetFundingRatesAsync(CancellationToken ct = default);
    Task<decimal> GetMarkPriceAsync(string asset, CancellationToken ct = default);
    Task<DateTime?> GetNextFundingTimeAsync(string asset, CancellationToken ct = default);

    // Trading
    Task<OrderResultDto> PlaceMarketOrderAsync(string asset, Side side,
        decimal sizeUsdc, int leverage, CancellationToken ct = default);
    Task<OrderResultDto> PlaceMarketOrderByQuantityAsync(string asset, Side side,
        decimal quantity, int leverage, CancellationToken ct = default);
    Task<OrderResultDto> ClosePositionAsync(string asset, Side side,
        CancellationToken ct = default);

    // Account / margin
    Task<decimal> GetAvailableBalanceAsync(CancellationToken ct = default);
    Task<int?> GetMaxLeverageAsync(string asset, CancellationToken ct = default);
    Task<LeverageTier[]?> GetLeverageTiersAsync(string asset, CancellationToken ct = default);
    Task<MarginStateDto?> GetPositionMarginStateAsync(string asset, CancellationToken ct = default);
    Task<int> GetQuantityPrecisionAsync(string asset, CancellationToken ct = default);

    // Position verification / reconciliation
    Task<bool> VerifyPositionOpenedAsync(string asset, Side side, CancellationToken ct = default);
    Task<bool?> HasOpenPositionAsync(string asset, Side side, CancellationToken ct = default);
    Task<bool?> CheckPositionExistsAsync(string asset, Side side, /* baseline args */ CancellationToken ct = default);
    Task<IReadOnlyDictionary<(string Symbol, string Side), decimal>?> CapturePositionSnapshotAsync(CancellationToken ct = default);

    // PnL / funding history (optional)
    Task<decimal?> GetRealizedPnlAsync(string asset, Side side, DateTime from, DateTime to, CancellationToken ct = default);
    Task<decimal?> GetFundingPaymentsAsync(string asset, Side side, DateTime from, DateTime to, CancellationToken ct = default);
}
```

Implementations: `HyperliquidConnector`, `AsterConnector`, `LighterConnector`, `BinanceConnector`, `DydxConnector`, `CoinGlassConnector` (data-only).

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
AssetSymbol, LongExchangeName/Id, ShortExchangeName/Id,
LongRatePerHour, ShortRatePerHour, SpreadPerHour, NetYieldPerHour,
BoostedNetYieldPerHour, AnnualizedYield, Volume24h, MarkPrices,
PredictedRate, PredictedTrend, TrendUnconfirmed,
MinutesToNextSettlement, EffectiveLeverage, BreakEvenHours,
IsCoinGlassHot
```

### OrderResultDto
```
Success, OrderId, ExecutedPrice, ExecutedSize, Error
```

### PositionSummaryDto
```
Id, AssetSymbol, LongExchangeName, ShortExchangeName, SizeUsdc, MarginUsdc,
EntrySpreadPerHour, CurrentSpreadPerHour, AccumulatedFunding,
UnrealizedPnl, ExchangePnl, UnifiedPnl, DivergencePct, RealizedPnl,
Status, OpenedAt, ClosedAt, IsDryRun, WarningLevel, WarningTypes,
CollateralImbalancePct
```

### PositionDetailsDto
```
Id, AssetSymbol/Id, LongExchange/Id, ShortExchange/Id,
SizeUsdc, MarginUsdc, Leverage,
LongEntryPrice, ShortEntryPrice, EntrySpreadPerHour, CurrentSpreadPerHour,
AccumulatedFunding, RealizedPnl, RealizedDirectionalPnl,
TotalFeesUsdc, EntryFeesUsdc, ExitFeesUsdc,
Status, CloseReason, OpenedAt, ClosedAt, Notes, IsDryRun,
LongMarginUtilizationPct, ShortMarginUtilizationPct,
MaxSafeMovePctLong, MaxSafeMovePctShort, CyclesUntilLiquidation
```

### MarginStateDto
```
MarginUsed, MarginAvailable, LiquidationPrice, MarginUtilizationPct
```

### PnlDecompositionDto
```
Directional, Funding, Fees
Strategy = Directional + Funding - Fees
```

### PipelineDiagnosticsDto
```
TotalRatesLoaded, RatesAfterStalenessFilter,
TotalPairsEvaluated, PairsFilteredByVolume, PairsFilteredByThreshold,
NetPositiveBelowThreshold, NetPositiveBelowEdgeGuardrail,
PairsFilteredByBreakeven, PairsPassing,
BestRawSpread, StalenessMinutes, MinVolumeThreshold, OpenThreshold
```

### DashboardDto
```
BotEnabled, OperatingState, OpenPositionCount, OpeningPositionCount,
NeedsAttentionCount, TotalPnl, BestSpread, TotalAlerts
```

### Exchange Analytics DTOs
- `ExchangeOverviewDto` — `ExchangeName, CoinCount, HasDirectConnector, StatusBadge`
- `SpreadOpportunityDto` — `Symbol, LongExchange, ShortExchange, SpreadPerHour, NetYieldPerHour, ConnectorStatus`
- `RateComparisonDto` — `Symbol, DirectRate, CoinGlassRate, DivergencePercent`
- `DiscoveryEventDto` — `EventType, ExchangeName, Symbol, DiscoveredAt`
