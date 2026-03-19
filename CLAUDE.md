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

IMPLEMENTATION:
- Write tests FIRST (TDD).
- Run dotnet build && dotnet test before reporting done.
- If tests fail: fix the issue. Do NOT report done with failing tests.

BEFORE REPORTING DONE (agent commit — automatic, no user confirmation needed):
- Stage all changed files and commit with message "wip: [stream A/B/C description]".
- If a pre-commit hook fails: fix the issue and retry the commit.
- Do NOT report done without a successful commit — uncommitted worktree changes are lost on cleanup.

RULES:
- Do NOT modify any files outside your assigned list.
```

**Note for review-cycle prompts**: The same template applies to review-fix teammates. When writing review-v{N}-prompts.md, include the commit instruction in every teammate prompt. Review prompts that omit the commit instruction will cause work loss.

## Workflow Rules
1. **Start of session**: Read `docs/progress.md` to see what's next
2. **Set model/effort**: Each task prompt in `docs/prompts.md` specifies its model and effort level
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
