# FundingRateArb — Automated Funding Rate Arbitrage Bot

## What This Is
An automated funding rate arbitrage bot that opens delta-neutral positions across DEX exchanges, earning the funding rate differential. Also a .NET 8 MVC school project for Odisee BAD [OBI43a].

## Architecture
- **.NET 8 MVC**, Clean Architecture: `Domain` -> `Application` -> `Infrastructure` -> `Web`
- **TDD mandatory**: xUnit + Moq + FluentAssertions. Write tests FIRST, then implementation.
- **EF Core 8 + MS SQL Server** (school requirement). Unit of Work + Repository pattern.
- **SignalR** for real-time browser dashboard updates.
- **Serilog** for structured logging (Console + File + SQL Server AuditLog).
- **Polly v8** (`Microsoft.Extensions.Resilience`) for retry + circuit breaker on all exchange calls.

## Exchanges (all 3 from day 1)
| Exchange | SDK | Auth | Collateral | Funding Interval |
|----------|-----|------|------------|-----------------|
| Hyperliquid | `HyperLiquid.Net` (JKorf) | EIP-712 wallet signing | USDC | 1 hour |
| Aster DEX | `JKorf.Aster.Net` | HMAC-SHA256 | USDT | 4 hours |
| Lighter DEX | Custom `HttpClient` | API key + nonce | USDC | 1 hour |

## Key Domain Entities (8 total, excl. Identity)
Exchange, Asset, FundingRateSnapshot, ArbitragePosition, Alert, BotConfiguration, ExchangeAssetConfig, AuditLog

## Service Interfaces (all in Application layer)
- `ISignalEngine` — detects arbitrage opportunities, ranks by net yield
- `IPositionSizer` — calculates optimal size (3 limits: liquidity, capital, break-even) + tick/lot rounding
- `IExecutionEngine` — opens/closes dual-leg positions, emergency close on leg failure
- `IPositionHealthMonitor` — monitors spreads, auto-closes on 3 criteria
- `IYieldCalculator` — PnL projections, break-even calculations
- `IExchangeConnector` — single interface for all exchange operations
- `IExchangeConnectorFactory` — resolves connector by exchange name
- `IUnitOfWork` — Unit of Work with all repositories (from cursus BankingApp pattern)
- `IApiKeyVault` — encrypts/decrypts API keys via Data Protection API

## Conventions
- **English** for all code, comments, and commit messages
- Interfaces for ALL services (for DI and mocking in tests)
- Exchange connectors behind `IExchangeConnector` abstraction
- Funding rates always normalized to **per-hour** for comparison (Aster rate / 4)
- `SemaphoreSlim` in `BotOrchestrator` to prevent overlapping cycles
- `[ValidateAntiForgeryToken]` on ALL POST actions
- API keys in **User Secrets** only — never in appsettings.json or source control
- Order sizes rounded DOWN to exchange-specific `stepSize`/`szDecimals`

## Roles
- **Admin**: full CRUD, bot config, kill switch, all users' positions
- **Trader**: view opportunities, own positions, own alerts, manual close own positions

## Small Capital Strategy
- Starting capital: ~100 EUR (~$107 USDC/USDT)
- Default leverage: 5x per leg (~$270 notional)
- Max 1 concurrent position (can't diversify at this scale)
- Preferred pair: Hyperliquid + Lighter (zero fees on Lighter side)

## Reference Docs
- **Full implementation plan**: `docs/plan.md` (3000+ lines, every detail)
- **Task prompts**: `docs/prompts.md` (phased prompts with acceptance criteria)
- **Progress tracker**: `docs/progress.md` (checkboxes, updated after each session)
- **School requirements PDF**: `projectrichtlijnen.pdf`
- **Cursus examples**: `\\wsl.localhost\Ubuntu-24.04\home\bruce\bad\cursus\`

## Model & Effort Guide
- **Opus + high effort**: Phases 4-5 (exchange connectors, trading logic), Phase 10 (security audit, debugging)
- **Sonnet + high effort**: Phase 3 (persistence), Phase 6 (background services)
- **Sonnet + medium effort**: Phases 1-2 (skeleton, domain), Phases 7-8 (logging, SignalR)
- **Sonnet/Haiku + medium effort**: Phase 9 (MVC CRUD views)
- **Agent team teammates**: Default to Sonnet (they follow clear specs from the lead)

## Workflow Rules
1. **Start of session**: Read `docs/progress.md` to see what's next
2. **Set model/effort**: Use `/model` and `/effort` based on the guide above
3. **Before coding**: Read the relevant task prompt in `docs/prompts.md`
4. **For implementation details**: Read the specific section of `docs/plan.md` referenced in the prompt
5. **After completing each task**: Immediately update `docs/progress.md`:
   - Check the box `[x]` for the completed task
   - Update "Current Status" with the next task
   - Add a line to the Session Log
6. **After each task**: Run `dotnet build` and `dotnet test` to verify

## Current Phase
Check `docs/progress.md` for current status and next task.
