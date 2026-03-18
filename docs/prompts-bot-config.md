# Prompts: Bot Config Rework

> Plan: `docs/plan-bot-config.md`

---

## Pre-step: Config Model + Validation

> Model: sonnet | Effort: medium | Agent: none (lead does this) | /clear before starting

Done by lead before spawning agents. See plan for exact fields and ConfigValidator spec.

---

## Stream A: Signal Engine + Position Sizing

> Model: opus | Effort: high | Agent: worktree (stream-a-signal-sizing) | /clear before starting

### Task: Fix SignalEngine + PositionSizer — 7 correctness issues

You are fixing critical correctness issues in the signal engine and position sizing logic for a funding rate arbitrage bot. The plan is at `docs/plan-bot-config.md` — read it for full context.

**IMPORTANT**: Before reporting done: stage all your changes and commit with message `wip: stream A — signal engine + position sizer fixes`. If a pre-commit hook fails, fix the issue and retry the commit. Do NOT report done without a successful commit — uncommitted worktree changes are lost on cleanup.

#### Files you OWN (write):
- `src/FundingRateArb.Application/Services/SignalEngine.cs`
- `src/FundingRateArb.Application/Services/PositionSizer.cs`
- `src/FundingRateArb.Application/Services/IPositionSizer.cs`
- `tests/FundingRateArb.Tests.Unit/Services/SignalEngineTests.cs`
- `tests/FundingRateArb.Tests.Unit/Services/PositionSizerTests.cs`

#### Files you READ (do not modify):
- `src/FundingRateArb.Domain/Entities/BotConfiguration.cs` — has new fields: `FeeAmortizationHours`, `MinPositionSizeUsdc`, `MinVolume24hUsdc`, `RateStalenessMinutes`
- `src/FundingRateArb.Application/DTOs/ArbitrageOpportunityDto.cs`
- `src/FundingRateArb.Application/Common/Repositories/IUnitOfWork.cs`
- `src/FundingRateArb.Application/Services/IYieldCalculator.cs`

#### Changes (implement in this order):

**1. SignalEngine.cs — C3+C4: Use FeeAmortizationHours for fee calculation**
```csharp
// Line 53: CHANGE from:
var feePerHour = (longFee + shortFee) / config.MaxHoldTimeHours;
// TO:
var feePerHour = (longFee + shortFee) / config.FeeAmortizationHours;
```

**2. SignalEngine.cs — H3: Rate staleness check**
After line 26 (`var rates = latestRates.Where(...)`), add a staleness filter:
```csharp
var cutoff = DateTime.UtcNow.AddMinutes(-config.RateStalenessMinutes);
rates = rates.Where(r => r.FetchedAt >= cutoff).ToList();
```
Check if `FetchedAt` exists on the FundingRate entity. If it doesn't exist, use `CreatedAt` or the closest timestamp field available. Read the entity to confirm.

**3. SignalEngine.cs — M2: Minimum volume filter**
Replace line 44:
```csharp
// FROM:
if (longR.Volume24hUsd <= 0 || shortR.Volume24hUsd <= 0) continue;
// TO:
if (longR.Volume24hUsd < config.MinVolume24hUsdc || shortR.Volume24hUsd < config.MinVolume24hUsdc) continue;
```

**4. PositionSizer.cs — C1: Add breakeven check to CalculateBatchSizesAsync**
After the liquidity cap loop (line 97), add a breakeven validation loop:
```csharp
// Breakeven gate: reject positions that can't break even in time
for (int i = 0; i < sizes.Length; i++)
{
    if (sizes[i] <= 0) continue;
    var opp = opportunities[i];
    var entryFeeRate = opp.SpreadPerHour - opp.NetYieldPerHour;
    if (entryFeeRate < 0) { sizes[i] = 0; continue; }
    var breakEvenHours = _yieldCalculator.BreakEvenHours(entryFeeRate, opp.NetYieldPerHour);
    if (breakEvenHours > config.BreakevenHoursMax)
        sizes[i] = 0;
}
```

**5. PositionSizer.cs — C2: Fix liquidity check to use notional**
In the liquidity cap loop (~line 92-97), the comparison should use notional, not margin:
```csharp
for (int i = 0; i < sizes.Length; i++)
{
    var minVol = Math.Min(opportunities[i].LongVolume24h, opportunities[i].ShortVolume24h);
    var liquidityLimit = minVol * config.VolumeFraction;
    // sizes[i] is margin; notional = margin * leverage. Compare notional to liquidity.
    var notional = sizes[i] * config.DefaultLeverage;
    if (notional > liquidityLimit)
        sizes[i] = liquidityLimit / config.DefaultLeverage;
}
```

**6. PositionSizer.cs — H2: Minimum position size enforcement**
After the breakeven loop, add:
```csharp
// Enforce minimum position size (exchange minimums)
for (int i = 0; i < sizes.Length; i++)
{
    if (sizes[i] > 0 && sizes[i] < config.MinPositionSizeUsdc)
        sizes[i] = 0;
}
```

**7. IPositionSizer.cs — Remove CalculateOptimalSizeAsync**
Remove the `CalculateOptimalSizeAsync` method from the interface. Remove its implementation from `PositionSizer.cs`. It is dead code — only `CalculateBatchSizesAsync` is called by the orchestrator.

**8. Tests** — Write tests for each fix:
- Rate staleness: mock rates with old timestamps → filtered out
- Min volume: mock rate with $10k volume, config min $50k → filtered out
- FeeAmortizationHours: verify fee calc uses new field, not MaxHoldTimeHours
- Breakeven rejection: mock opportunity where breakeven > BreakevenHoursMax → size = 0
- Notional liquidity: margin $100, leverage 5x, volume limit $400 → size capped to $80
- Min position size: allocation produces $5 → zeroed out
- CalculateOptimalSizeAsync removed: verify it doesn't exist on interface

Run `dotnet build && dotnet test` before committing. All existing + new tests must pass.

---

## Stream B: Orchestrator + Alert Flow

> Model: opus | Effort: high | Agent: worktree (stream-b-orchestrator) | /clear before starting

### Task: Fix BotOrchestrator — cycle trigger, health monitor, drawdown, alerts

You are fixing critical issues in the bot orchestrator for a funding rate arbitrage bot. The plan is at `docs/plan-bot-config.md` — read it for full context.

**IMPORTANT**: Before reporting done: stage all your changes and commit with message `wip: stream B — orchestrator + alert flow fixes`. If a pre-commit hook fails, fix the issue and retry the commit. Do NOT report done without a successful commit — uncommitted worktree changes are lost on cleanup.

#### Files you OWN (write):
- `src/FundingRateArb.Infrastructure/BackgroundServices/BotOrchestrator.cs`
- `tests/FundingRateArb.Tests.Unit/BackgroundServices/BotOrchestratorTests.cs`

#### Files you READ (do not modify):
- `src/FundingRateArb.Domain/Entities/BotConfiguration.cs` — has new fields: `DailyDrawdownPausePct`, `ConsecutiveLossPause`
- `src/FundingRateArb.Application/Interfaces/IBotControl.cs`
- `src/FundingRateArb.Application/Hubs/IDashboardClient.cs`
- `src/FundingRateArb.Application/Common/Repositories/IUnitOfWork.cs`

#### Changes (implement in this order):

**1. C6: Fix immediate cycle trigger with CancellationTokenSource**
Replace `_immediateRunRequested` bool with a CancellationTokenSource approach:
```csharp
private CancellationTokenSource _immediateCts = new();

public void TriggerImmediateCycle()
{
    _immediateCts.Cancel();
}
```
In `ExecuteAsync`, replace the current check:
```csharp
// Instead of checking _immediateRunRequested at loop top,
// use a combined cancellation approach:
try
{
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _immediateCts.Token);
    await timer.WaitForNextTickAsync(linkedCts.Token);
}
catch (OperationCanceledException) when (_immediateCts.IsCancellationRequested)
{
    // Immediate cycle requested — reset the CTS for next use
    _immediateCts.Dispose();
    _immediateCts = new CancellationTokenSource();
}
```
Remove `_immediateRunRequested` field entirely. Update `ClearCooldowns` to also include cooldown clearing (already does). Dispose `_immediateCts` in `Dispose()`.

**2. H1: Always run health monitor**
Move health monitor OUTSIDE the `config.IsEnabled` gate. Currently at line 114:
```csharp
// FROM:
if (config.IsEnabled)
    await healthMonitor.CheckAndActAsync(ct);
// TO (unconditional):
await healthMonitor.CheckAndActAsync(ct);
```
This ensures positions are always monitored even when the kill switch is off.

**3. H4: Fix position ownership**
Currently `ExecutionEngine` uses `config.UpdatedByUserId`. This is wrong because it makes whoever last edited the config the "owner" of all bot-created positions. The fix is to pass the admin user ID from the seeder. In `RunCycleAsync`, after getting the config, resolve the system admin user:
```csharp
// After getting config, resolve system user for position ownership
var systemUserId = config.UpdatedByUserId; // Keep as fallback for now
// TODO: In future, resolve from UserManager or a dedicated system user
```
Note: A full fix requires changes to `ExecutionEngine.cs` (not in this stream's ownership). For now, add a `// TODO` comment and document the issue. The stream C agent will add a note in the admin UI.

**4. H6: Fix alert dedup**
In `PushNewAlertsAsync`, track pushed alert IDs to prevent duplicate pushes:
```csharp
private readonly HashSet<int> _pushedAlertIds = new();

// In PushNewAlertsAsync, after querying alerts:
foreach (var alert in recentAlerts)
{
    if (!_pushedAlertIds.Add(alert.Id)) continue; // Skip already-pushed
    // ... existing push logic
}

// Prune old IDs periodically (keep last 1000)
if (_pushedAlertIds.Count > 1000)
    _pushedAlertIds.Clear();
```

**5. H7: Daily drawdown circuit breaker**
Before the position-opening section (after "Step 3: Push dashboard"), add a drawdown check:
```csharp
// Step 4a: Daily drawdown circuit breaker
var closedToday = await uow.Positions.GetClosedSinceAsync(DateTime.UtcNow.Date);
var dailyPnl = closedToday.Sum(p => p.RealizedPnl) + openPositions.Sum(p => p.AccumulatedFunding);
var drawdownLimit = config.TotalCapitalUsdc * config.DailyDrawdownPausePct;
if (dailyPnl < -drawdownLimit)
{
    _logger.LogWarning(
        "Daily drawdown limit hit: {DailyPnl:F2} USDC (limit: -{Limit:F2}). Pausing position opens for this cycle.",
        dailyPnl, drawdownLimit);
    return;
}
```
Check if `IPositionRepository` has `GetClosedSinceAsync`. If not, you'll need to use the existing `GetClosedAsync` with a date filter, or read the interface to find the best method. If no suitable method exists, use `GetAllAsync` with a LINQ filter — but add a `// TODO: add GetClosedSinceAsync to repo` comment.

**6. M6: Consecutive loss tracking**
Add a field to track consecutive losses:
```csharp
private int _consecutiveLosses;
```
After a position-open failure (in the else branch at ~line 217):
```csharp
_consecutiveLosses++;
if (_consecutiveLosses >= config.ConsecutiveLossPause)
{
    _logger.LogWarning("Consecutive loss limit reached ({Count}). Pausing.", _consecutiveLosses);
    break;
}
```
Reset on success: `_consecutiveLosses = 0;` after successful open.

**7. Tests** — Write/update tests for:
- CTS-based immediate trigger: verify cycle runs without waiting for timer
- Always-on health monitor: verify `CheckAndActAsync` called even when `IsEnabled = false`
- Alert dedup: push same alert twice → only one push
- Drawdown pause: daily PnL below threshold → no position opens
- Consecutive loss pause: after N failures → break

Run `dotnet build && dotnet test` before committing. All existing + new tests must pass.

---

## Stream C: Web Layer

> Model: opus | Effort: high | Agent: worktree (stream-c-web) | /clear before starting

### Task: Fix RetryNow, alert display, config validation UI

You are fixing web layer issues for a funding rate arbitrage bot. The plan is at `docs/plan-bot-config.md` — read it for full context.

**IMPORTANT**: Before reporting done: stage all your changes and commit with message `wip: stream C — web layer fixes`. If a pre-commit hook fails, fix the issue and retry the commit. Do NOT report done without a successful commit — uncommitted worktree changes are lost on cleanup.

#### Files you OWN (write):
- `src/FundingRateArb.Web/Controllers/DashboardController.cs`
- `src/FundingRateArb.Web/Views/Dashboard/Index.cshtml`
- `src/FundingRateArb.Web/wwwroot/js/dashboard.js`
- `src/FundingRateArb.Web/Controllers/Admin/BotConfigController.cs`
- `src/FundingRateArb.Web/Views/Admin/BotConfig/Edit.cshtml`
- `src/FundingRateArb.Web/ViewModels/BotConfigViewModel.cs`
- `tests/FundingRateArb.Tests.Unit/Controllers/DashboardControllerTests.cs`

#### Files you READ (do not modify):
- `src/FundingRateArb.Domain/Entities/BotConfiguration.cs` — has new fields
- `src/FundingRateArb.Application/Services/IConfigValidator.cs` — new interface
- `src/FundingRateArb.Application/Services/ConfigValidator.cs` — implementation
- `src/FundingRateArb.Application/Interfaces/IBotControl.cs`
- `src/FundingRateArb.Web/Program.cs` — DI registrations

#### Changes (implement in this order):

**1. C5: RetryNow → AJAX (no page reload)**

In `DashboardController.cs`, change `RetryNow` from redirect to JSON:
```csharp
[HttpPost]
[ValidateAntiForgeryToken]
[Authorize(Roles = "Admin")]
public IActionResult RetryNow()
{
    _botControl.ClearCooldowns();
    _botControl.TriggerImmediateCycle();
    return Json(new { success = true, message = "Cooldowns cleared — next cycle triggered." });
}
```

In `Dashboard/Index.cshtml`, replace the form with a button + JS:
```html
<button id="retry-now-btn" class="btn btn-warning btn-sm">Retry Now</button>
```

In `dashboard.js`, add the AJAX handler:
```javascript
const retryBtn = document.getElementById("retry-now-btn");
if (retryBtn) {
    retryBtn.addEventListener("click", async () => {
        retryBtn.disabled = true;
        retryBtn.textContent = "Triggering...";
        try {
            const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
            const resp = await fetch("/Dashboard/RetryNow", {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "RequestVerificationToken": token
                }
            });
            const data = await resp.json();
            if (data.success) {
                retryBtn.textContent = "Triggered!";
                setTimeout(() => { retryBtn.textContent = "Retry Now"; retryBtn.disabled = false; }, 3000);
            }
        } catch (e) {
            retryBtn.textContent = "Retry Now";
            retryBtn.disabled = false;
        }
    });
}
```
IMPORTANT: You still need the anti-forgery token. Add a hidden `@Html.AntiForgeryToken()` somewhere on the page (e.g., in a hidden form or as a standalone input).

**2. M9: Alert toast display**
In `dashboard.js`, update the `ReceiveAlert` handler to show the alert as a toast:
```javascript
connection.on("ReceiveAlert", (alert) => {
    // Update badge count
    const badge = document.getElementById("alert-badge");
    if (badge) {
        const current = parseInt(badge.textContent) || 0;
        badge.textContent = current + 1;
        badge.classList.remove("d-none");
    }

    // Show alert as toast
    const container = document.getElementById("notification-toast-container");
    if (!container || !alert.message) return;

    const severityClass = alert.severity === 1 ? "text-bg-danger"
        : alert.severity === 2 ? "text-bg-warning"
        : "text-bg-info";

    const toast = document.createElement("div");
    toast.className = "toast align-items-center " + severityClass + " border-0 show";
    toast.setAttribute("role", "alert");

    const dFlex = document.createElement("div");
    dFlex.className = "d-flex";

    const toastBody = document.createElement("div");
    toastBody.className = "toast-body";
    toastBody.textContent = alert.message;
    dFlex.appendChild(toastBody);

    const closeBtn = document.createElement("button");
    closeBtn.type = "button";
    closeBtn.className = "btn-close btn-close-white me-2 m-auto";
    closeBtn.setAttribute("data-bs-dismiss", "toast");
    dFlex.appendChild(closeBtn);

    toast.appendChild(dFlex);
    container.appendChild(toast);
    setTimeout(() => toast.remove(), 8000);
});
```

**3. H5: Config validation in BotConfigController**
Read `BotConfigController.cs` first to understand the current Edit action.

In the Edit POST action, inject and call `IConfigValidator`:
```csharp
var validation = await _configValidator.ValidateAsync(config);
if (!validation.IsValid)
{
    foreach (var error in validation.Errors)
        ModelState.AddModelError(string.Empty, error);
    return View(vm);
}
```

**4. BotConfigViewModel — Add new fields**
Read `BotConfigViewModel.cs` first. Add properties matching the new `BotConfiguration` fields:
```csharp
public int FeeAmortizationHours { get; set; } = 24;
public decimal MinPositionSizeUsdc { get; set; } = 10m;
public decimal MinVolume24hUsdc { get; set; } = 50_000m;
public int RateStalenessMinutes { get; set; } = 15;
public decimal DailyDrawdownPausePct { get; set; } = 0.05m;
public int ConsecutiveLossPause { get; set; } = 3;
```

**5. BotConfig/Edit.cshtml — Add form fields for new config**
Read the current Edit view first. Add form groups for each new field with appropriate labels and validation. Group them under a "Risk Management" section:
```html
<h5 class="mt-4 mb-3">Risk Management</h5>
<!-- FeeAmortizationHours, MinPositionSizeUsdc, MinVolume24hUsdc, etc. -->
```

**6. Tests** — Write/update:
- RetryNow returns JSON (not redirect)
- Config validation rejection returns view with errors

Run `dotnet build && dotnet test` before committing. All existing + new tests must pass.
