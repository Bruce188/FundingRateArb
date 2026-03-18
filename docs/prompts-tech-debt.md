# Prompts: Clear Technical Debt

> **Plan**: `docs/plan-tech-debt.md`
> **Branch**: `feat/clear-technical-debt`

---

## Pre-Step: Lead Scaffolding

> Model: Opus | Effort: Low | Agent: none (lead does directly)

Create shared interfaces and enums on the branch so both worktree agents can compile. Files:

1. Create `src/FundingRateArb.Domain/Enums/AllocationStrategy.cs`:
```csharp
namespace FundingRateArb.Domain.Enums;

public enum AllocationStrategy
{
    Concentrated   = 0,
    WeightedSpread = 1,
    EqualSpread    = 2,
    RiskAdjusted   = 3,
}
```

2. Create `src/FundingRateArb.Application/Interfaces/IBotControl.cs`:
```csharp
namespace FundingRateArb.Application.Interfaces;

public interface IBotControl
{
    void ClearCooldowns();
    void TriggerImmediateCycle();
}
```

3. Add to `IAlertRepository.cs` (after existing methods): `Task MarkAllReadAsync(string userId);`

4. Add to `BotConfiguration.cs`:
```csharp
public AllocationStrategy AllocationStrategy { get; set; } = AllocationStrategy.Concentrated;

[Range(1, 20)]
public int AllocationTopN { get; set; } = 3;
```
Add `using FundingRateArb.Domain.Enums;` if not present.

5. Add to `IPositionSizer.cs`:
```csharp
Task<decimal[]> CalculateBatchSizesAsync(
    IReadOnlyList<ArbitrageOpportunityDto> opportunities,
    AllocationStrategy strategy);
```
Add necessary usings.

6. `dotnet build` — verify 0 errors.
7. Commit: `chore: scaffold shared interfaces for tech-debt streams`

---

## Stream A Prompt: Backend — Application + Infrastructure

> Model: Opus | Effort: High | Agent: worktree | /clear before starting

You are implementing backend changes for 6 technical debt items on the `feat/clear-technical-debt` branch. The shared interfaces and enums have already been scaffolded — you just need to implement them.

### Your files (exclusive — no other agent touches these):
- `src/FundingRateArb.Application/Services/SignalEngine.cs`
- `src/FundingRateArb.Application/Services/PositionSizer.cs`
- `src/FundingRateArb.Infrastructure/BackgroundServices/BotOrchestrator.cs`
- `src/FundingRateArb.Infrastructure/Repositories/AlertRepository.cs`
- `src/FundingRateArb.Web/Program.cs`
- `tests/FundingRateArb.Tests.Unit/Services/SignalEngineTests.cs`
- `tests/FundingRateArb.Tests.Unit/Services/PositionSizerTests.cs`
- `tests/FundingRateArb.Tests.Unit/Services/BotOrchestratorTests.cs`
- New EF migration

### Changes to make:

**1. SignalEngine.cs — Zero-volume filter (TD1)**
Read the file. In the inner loop where opportunities are built (around line 42, after `longR`/`shortR` are resolved), add:
```csharp
if (longR.Volume24hUsd <= 0 || shortR.Volume24hUsd <= 0) continue;
```

**2. SignalEngine.cs — Limit to 20 (TD3)**
At the return statement (~line 76), change:
```csharp
return [.. opportunities.OrderByDescending(o => o.NetYieldPerHour).Take(20)];
```

**3. BotOrchestrator.cs — Auto-skip on insufficient balance (TD2)**
Read `BotOrchestrator.cs`. Find the opportunity iteration loop (~lines 146-201). Currently the loop tries ONE opportunity and `break`s regardless of success or failure.

Change the loop to:
- On success → `break` (same as today)
- On insufficient-balance/margin error (check if error message contains "Insufficient margin" or "balance") → set a local `bool balanceExhausted = true` and `break` (no point trying other coins)
- On other errors (exchange error, IMF limit, etc.) → register cooldown as today, then `continue` to try the next opportunity (instead of breaking)
- Remove the unconditional `break` at the end of the loop

**4. BotOrchestrator.cs — Implement IBotControl (TD2)**
Make `BotOrchestrator` implement `IBotControl`:
```csharp
public class BotOrchestrator : BackgroundService, IBotControl
```

Add a `private volatile bool _immediateRunRequested;` field.

Implement:
```csharp
public void ClearCooldowns() => _failedOpCooldowns.Clear();
public void TriggerImmediateCycle() => _immediateRunRequested = true;
```

In `ExecuteAsync`, before `await _timer.WaitForNextTickAsync(ct)`, add:
```csharp
if (_immediateRunRequested)
{
    _immediateRunRequested = false;
    // Skip waiting, run cycle immediately
}
else
{
    await _timer.WaitForNextTickAsync(ct);
}
```

**5. BotOrchestrator.cs — Strategy-aware dispatch (TD6)**
Replace the single-opportunity iteration with strategy dispatch. Read the plan for the exact pseudocode. Key changes:
- Get `config.AllocationStrategy` and `config.AllocationTopN`
- Filter candidates (not open, not on cooldown)
- Take 1 for Concentrated, TopN for spread strategies
- Call `_positionSizer.CalculateBatchSizesAsync(candidates, strategy)`
- Iterate results, opening positions until slots exhausted or balance exhausted
- Inject `IPositionSizer` if not already injected

**6. AlertRepository.cs — MarkAllReadAsync (TD4)**
Read `AlertRepository.cs`. Add after the existing `Update` method:
```csharp
public Task MarkAllReadAsync(string userId) =>
    _context.Alerts
        .Where(a => a.UserId == userId && !a.IsRead)
        .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsRead, true));
```

**7. Program.cs — Register IBotControl (TD2)**
Read `Program.cs`. After the `AddHostedService<BotOrchestrator>()` registration, add:
```csharp
builder.Services.AddSingleton<IBotControl>(sp =>
    sp.GetServices<IHostedService>().OfType<BotOrchestrator>().Single());
```
Add `using FundingRateArb.Application.Interfaces;` if needed.

**8. EF Migration (TD6)**
After all entity changes compile, run:
```bash
dotnet ef migrations add AddAllocationStrategy \
  --project src/FundingRateArb.Infrastructure \
  --startup-project src/FundingRateArb.Web
```

**9. Tests**
Read existing test files first to match patterns.

Add to `SignalEngineTests.cs`:
- Test that opportunities with 0 volume on either leg are excluded
- Test that at most 20 opportunities are returned

Add to `PositionSizerTests.cs`:
- `CalculateBatchSizesAsync_Concentrated_SizesOnlyFirst`
- `CalculateBatchSizesAsync_WeightedSpread_DistributesProportionally`
- `CalculateBatchSizesAsync_EqualSpread_DividesEvenly`
- `CalculateBatchSizesAsync_RiskAdjusted_PenalizesLowVolume`

Add to `BotOrchestratorTests.cs`:
- Test: on non-balance failure, orchestrator continues to next opportunity
- Test: on balance failure, orchestrator stops iterating
- Test: ClearCooldowns empties the dictionary
- Test: with spread strategy, multiple positions opened per cycle

### Verification
After all changes: `dotnet build --nologo -v q` (0 errors, 0 warnings) and `dotnet test --nologo -v q` (all pass).

Before reporting done: stage all your changes and commit with message `wip: stream-a backend tech-debt (TD1-TD6)`. If a pre-commit hook fails, fix the issue and retry the commit. Do NOT report done without a successful commit — uncommitted worktree changes are lost on cleanup.

---

## Stream B Prompt: Web Layer — Controllers + Views

> Model: Opus | Effort: High | Agent: worktree | /clear before starting

You are implementing web layer changes for 6 technical debt items on the `feat/clear-technical-debt` branch. The shared interfaces, enums, and entity fields have already been scaffolded — you implement the controllers, views, and JavaScript.

### Your files (exclusive — no other agent touches these):
- `src/FundingRateArb.Web/Controllers/DashboardController.cs`
- `src/FundingRateArb.Web/ViewModels/DashboardViewModel.cs`
- `src/FundingRateArb.Web/Views/Dashboard/Index.cshtml`
- `src/FundingRateArb.Web/Controllers/OpportunitiesController.cs`
- `src/FundingRateArb.Web/ViewModels/OpportunityListViewModel.cs`
- `src/FundingRateArb.Web/Views/Opportunities/Index.cshtml`
- `src/FundingRateArb.Web/Controllers/AlertsController.cs`
- `src/FundingRateArb.Web/Views/Alerts/Index.cshtml`
- `src/FundingRateArb.Web/Views/Shared/_Layout.cshtml`
- `src/FundingRateArb.Web/wwwroot/js/dashboard.js`
- `src/FundingRateArb.Web/ViewModels/Admin/BotConfigViewModel.cs`
- `src/FundingRateArb.Web/Areas/Admin/Controllers/BotConfigController.cs`
- `src/FundingRateArb.Web/Areas/Admin/Views/BotConfig/Index.cshtml`

### Changes to make:

**1. Consolidate Dashboard + Opportunities (TD5)**

Read ALL of these files first: `DashboardController.cs`, `DashboardViewModel.cs`, `Dashboard/Index.cshtml`, `OpportunitiesController.cs`, `OpportunityListViewModel.cs`, `Opportunities/Index.cshtml`, `dashboard.js`.

a) **DashboardViewModel.cs** (~14 lines): Add:
```csharp
public List<ArbitrageOpportunityDto> Opportunities { get; set; } = [];
public decimal NotionalPerLeg { get; set; }
public decimal VolumeFraction { get; set; }
```
Add `using FundingRateArb.Application.DTOs;`

b) **DashboardController.cs**: Inject `ISignalEngine` via constructor. In the `Index` action, call:
```csharp
var opportunities = await _signalEngine.GetOpportunitiesAsync(ct);
```
And populate the new ViewModel fields. Also compute `NotionalPerLeg` and `VolumeFraction` from the bot config (look at how `OpportunitiesController` does it).

Also inject `IBotControl` for the retry button (see item 3 below).

c) **Dashboard/Index.cshtml**: After the KPI cards section (before or after the funding rates table), insert the opportunities table. Copy the table structure from `Opportunities/Index.cshtml` (lines ~11-68). CRITICAL: preserve these attributes on the `<tbody>`:
- `id="opportunities-table-body"`
- `data-notional-per-leg="@Model.NotionalPerLeg"`
- `data-volume-fraction="@Model.VolumeFraction"`

These are required by `dashboard.js` SignalR handler `ReceiveOpportunityUpdate` (lines 101-177). Change `@model` references from `OpportunityListViewModel` to `DashboardViewModel` property names.

d) **_Layout.cshtml**: Remove or comment out the Opportunities nav link (~line 29-31). The opportunities are now visible on the Dashboard.

e) **dashboard.js**: Read lines 101-177 (`ReceiveOpportunityUpdate` handler). Verify it targets `#opportunities-table-body` — no change needed if the id is preserved in the merged view. Check for any references to `data-notional-per-leg` and `data-volume-fraction` on `#opportunities-table-body` or its parent.

f) **OpportunitiesController.cs**: Keep the controller (it may be used for API calls) but its `Index` view action can redirect to Dashboard:
```csharp
public IActionResult Index() => RedirectToAction("Index", "Dashboard");
```

**2. Mark All Alerts as Read (TD4)**

Read `AlertsController.cs` and `Alerts/Index.cshtml`.

a) **AlertsController.cs**: After the existing `MarkAsRead` POST action (~line 68), add:
```csharp
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> MarkAllRead()
{
    var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    await _uow.Alerts.MarkAllReadAsync(userId);
    await _uow.SaveChangesAsync();
    TempData["Success"] = "All alerts marked as read.";
    return RedirectToAction(nameof(Index));
}
```
Ensure `using System.Security.Claims;` is present.

b) **Alerts/Index.cshtml**: In the header area (~lines 8-16), add a "Mark All as Read" button next to the heading:
```html
<form asp-action="MarkAllRead" method="post" class="d-inline ms-2">
    @Html.AntiForgeryToken()
    <button type="submit" class="btn btn-outline-secondary btn-sm"
            disabled="@(Model.Alerts.All(a => a.IsRead) ? "disabled" : null)">
        Mark All as Read
    </button>
</form>
```

**3. Retry Now Button (TD2)**

a) **DashboardController.cs**: Inject `IBotControl` (from `FundingRateArb.Application.Interfaces`). Add action:
```csharp
[HttpPost]
[ValidateAntiForgeryToken]
[Authorize(Roles = "Admin")]
public IActionResult RetryNow()
{
    _botControl.ClearCooldowns();
    _botControl.TriggerImmediateCycle();
    TempData["Success"] = "Cooldowns cleared — next cycle triggered.";
    return RedirectToAction(nameof(Index));
}
```

b) **Dashboard/Index.cshtml**: Near the bot status / kill switch area, add an Admin-only retry button:
```html
@if (User.IsInRole("Admin"))
{
    <form asp-action="RetryNow" method="post" class="d-inline ms-2">
        @Html.AntiForgeryToken()
        <button type="submit" class="btn btn-warning btn-sm">Retry Now</button>
    </form>
}
```

**4. Allocation Strategy Admin UI (TD6)**

Read `BotConfigViewModel.cs`, `BotConfigController.cs`, `BotConfig/Index.cshtml`.

a) **BotConfigViewModel.cs**: Add:
```csharp
[Required]
[Display(Name = "Allocation Strategy")]
public AllocationStrategy? AllocationStrategy { get; set; }

[Required]
[Range(1, 20)]
[Display(Name = "Top N Opportunities")]
public int? AllocationTopN { get; set; }
```
Add `using FundingRateArb.Domain.Enums;`

b) **BotConfigController.cs**:
- In GET action (~line 33), add to ViewModel initialization:
  ```csharp
  AllocationStrategy = config.AllocationStrategy,
  AllocationTopN = config.AllocationTopN,
  ```
- In POST action (~line 58), add to config update:
  ```csharp
  config.AllocationStrategy = model.AllocationStrategy!.Value;
  config.AllocationTopN = model.AllocationTopN!.Value;
  ```

c) **BotConfig/Index.cshtml**: After the Capital & Leverage section (~line 87) and before Risk Management, add:
```html
<h5 class="card-title mb-3">Capital Allocation</h5>
<div class="row g-3 mb-4">
    <div class="col-md-6">
        <label asp-for="AllocationStrategy" class="form-label"></label>
        <select asp-for="AllocationStrategy" class="form-select"
                asp-items="Html.GetEnumSelectList<AllocationStrategy>()"></select>
        <div class="form-text">
            Concentrated = all capital on best APR.
            Spread modes distribute across top N opportunities.
        </div>
        <span asp-validation-for="AllocationStrategy" class="text-danger"></span>
    </div>
    <div class="col-md-6">
        <label asp-for="AllocationTopN" class="form-label"></label>
        <input asp-for="AllocationTopN" class="form-control" type="number" min="1" max="20" />
        <div class="form-text">
            Number of opportunities to spread capital across (ignored for Concentrated).
        </div>
        <span asp-validation-for="AllocationTopN" class="text-danger"></span>
    </div>
</div>
```
Add `@using FundingRateArb.Domain.Enums` at the top if needed.

### Verification
After all changes: `dotnet build --nologo -v q` (0 errors, 0 warnings).

Before reporting done: stage all your changes and commit with message `wip: stream-b web-layer tech-debt (TD2-TD6)`. If a pre-commit hook fails, fix the issue and retry the commit. Do NOT report done without a successful commit — uncommitted worktree changes are lost on cleanup.

---

## Lead: Merge & Verification

> Model: Opus | Effort: Medium | Agent: none (lead does directly)

After both worktree agents complete:

1. Check each worktree branch has commits: `git log worktree-branch --oneline -5`
2. Merge into `feat/clear-technical-debt`: `git merge worktree-branch --no-ff`
3. Repeat for second branch
4. `dotnet build --nologo -v q` — 0 errors, 0 warnings
5. `dotnet test --nologo -v q` — all pass (235 existing + new)
6. Apply migration: `dotnet ef database update --project src/FundingRateArb.Infrastructure --startup-project src/FundingRateArb.Web`
7. Kill and restart app: `fuser -k 5273/tcp; sleep 2; dotnet run --project src/FundingRateArb.Web &`
8. Verify in browser:
   - Dashboard now shows opportunities table inline
   - "Retry Now" button visible for admin, clears cooldowns
   - Alerts page has "Mark All as Read" button
   - Admin > Bot Config has "Capital Allocation" section with strategy dropdown
   - Check logs: zero-volume opps not attempted, max 20 in any list
9. Update `docs/progress.md`
10. Mark completed items in `docs/technical-debt.md`
11. Clean up worktree branches
