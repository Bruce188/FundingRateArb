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

## Model & Effort Guide (per task)

| Phase | Task | Model | Effort | Notes |
|-------|------|-------|--------|-------|
| 4 | 4.1 Connector interface + factory | Sonnet | medium | Clean interface design |
| 4 | 4.2 Hyperliquid connector | Sonnet | high | HyperLiquid.Net SDK |
| 4 | 4.3 Lighter connector | Opus | high | Custom HttpClient, tricky auth |
| 4 | 4.4 Aster connector | Sonnet | high | JKorf.Aster.Net SDK |
| 4 | 4.5 Polly resilience | Sonnet | medium | Retry + circuit breaker |
| 4 | Agent teammates (4.2–4.4 parallel) | Sonnet | medium | Each connector is isolated |
| 5 | 5.1 YieldCalculator | Sonnet | high | Pure math, thorough TDD |
| 5 | 5.2 SignalEngine | Sonnet | high | Opportunity ranking logic |
| 5 | 5.3 PositionSizer | Sonnet | high | 3-limit calc + RoundToStepSize TDD |
| 5 | 5.4 ExecutionEngine | Opus | high | Dual-leg open/close, emergency logic |
| 5 | 5.5 PositionHealthMonitor | Opus | high | Auto-close criteria, spread monitoring |
| 6 | 6.1 FundingRateFetcher | Sonnet | high | BackgroundService, all 3 exchanges |
| 6 | 6.2 BotOrchestrator | Sonnet | high | SemaphoreSlim, full cycle logic |
| 6 | 6.3 Hosted service registration | Sonnet | medium | DI wiring |
| 7 | 7.1 Serilog setup | Sonnet | medium | Console + File sinks |
| 7 | 7.2 AuditLog sink | Sonnet | medium | SQL Server sink |
| 7 | 7.3 ApiKeyVault + secrets | Sonnet | medium | Data Protection API |
| 8 | 8.1 DashboardHub | Sonnet | medium | Strongly-typed SignalR hub |
| 8 | 8.2 JS client | Sonnet | medium | dashboard.js SignalR client |
| 8 | 8.3 BackgroundService → Hub | Sonnet | medium | IHubContext injection |
| 9 | 9.1 DashboardController | Sonnet | low | KPI cards, bot status |
| 9 | 9.2 OpportunitiesController | Sonnet | low | Live opportunity table |
| 9 | 9.3 PositionsController | Sonnet | low | Open/closed, PnL, manual close |
| 9 | 9.4 AlertsController | Sonnet | low | Unread count, mark as read |
| 9 | 9.5–9.8 Admin CRUD | Sonnet | low | Exchange, Asset, BotConfig, Users |
| 9 | 9.9 Layout + navigation | Sonnet | medium | _Layout.cshtml, SignalR status |
| 10 | 10.1 Security audit | Opus | high | Full OWASP review |
| 10 | 10.2 Code quality review | Opus | medium | SOLID, Clean Architecture |
| 10 | 10.3 Playwright UI tests | Sonnet | medium | Automated browser testing |
| 10 | 10.4 Edge case tests | Opus | medium | Corner cases, error paths |
| 10 | 10.5 Performance review | Opus | medium | Polling, DB queries, SignalR |
| 10 | 10.6 CSS polish | Sonnet | medium | Responsive design |

## Agent Team Rules

Agent teams run in parallel when tasks have **zero file overlap**. Enable with:
```bash
# Already set in ~/.claude/settings.json:
{ "env": { "CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS": "1" } }
```

**When to spawn agent teams:**
- **Phase 4 (4.2–4.4)**: 3 teammates in parallel — each connector is a separate file with separate tests. No overlap.
- **Phase 5 (5.1–5.3)**: 3 teammates in parallel — each service is a separate file with separate tests.
- **Phase 9 (9.1–9.8)**: Up to 4 teammates — each controller + views is a separate folder.

**Agent team teammate model**: Always Sonnet + medium (teammates follow clear specs, lead reviews).

**Lead role**: You (the lead) read progress.md, spawn the team, then merge + run `dotnet build && dotnet test`.

**Teammate prompt template**:
```
You are Teammate [A/B/C] working on [Task X.X: description].
Read docs/plan.md section [N] for implementation details.
Your files: [list exact files — no overlap with other teammates]
Write tests FIRST (TDD). Run dotnet test before reporting done.
Do NOT modify any files outside your assigned list.
```

## Workflow Rules
1. **Start of session**: Read `docs/progress.md` to see what's next
2. **Set model/effort**: Use `/model` and `/effort` per the table above for each task
3. **Before coding**: Read the relevant task prompt in `docs/prompts.md`
4. **For implementation details**: Read the specific section of `docs/plan.md` referenced in the prompt
5. **Agent team tasks**: Spawn teammates with exact file assignments; merge after all finish
6. **After completing each task**: Immediately update `docs/progress.md`:
   - Check the box `[x]` for the completed task
   - Update "Current Status" with the next task
   - Add a line to the Session Log
7. **After each task**: Run `dotnet build` and `dotnet test` to verify

## Current Phase
Check `docs/progress.md` for current status and next task.
