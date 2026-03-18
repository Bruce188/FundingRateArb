# Prompts: Pre-Deployment Review Fixes (v8)

> **Plan**: `docs/plan-pre-deploy.md`
> **Branch**: `feat/pre-deploy-hardening`
> **Pre-step**: `git checkout -b feat/pre-deploy-hardening`

---

## Stream A — Deploy/Infra Fixes

### Task A: Deploy Workflow + Infrastructure Fixes
> Model: sonnet | Effort: medium | Agent: worktree | /clear before starting

You are fixing the GitHub Actions deployment workflow and Azure infrastructure script. Working directory: `/home/bruce/bad/eindproject`.

**Do these 5 things:**

**A1. OIDC Subject Mismatch (BLOCKER)**
File: `infra/setup-azure.sh`
After the existing `az ad app federated-credential create` block (~line 130), add a SECOND federated credential for the `production` environment:
```bash
echo "==> Adding federated credential for production environment..."
az ad app federated-credential create --id "$OBJECT_ID" --parameters "{
  \"name\": \"github-production-env\",
  \"issuer\": \"https://token.actions.githubusercontent.com\",
  \"subject\": \"repo:\${GITHUB_REPO}:environment:production\",
  \"audiences\": [\"api://AzureADTokenExchange\"]
}" -o none
```

**A2. CI/Deploy Dedup (BLOCKER)**
File: `.github/workflows/ci.yml`
Change the `on:` trigger — remove `push: branches: [main]`, keep only `pull_request: branches: [main]`. Add a comment: `# Main branch builds handled by deploy.yml`.

**A3. EF Bundle --no-build**
File: `.github/workflows/deploy.yml:66-73`
Change the `dotnet ef migrations bundle` command to include `--no-build -c Release` flags.

**A4. SQL Connection String via Env Var**
File: `.github/workflows/deploy.yml:99-102`
Change from:
```yaml
- name: Run EF migrations
  run: |
    chmod +x ./publish/efbundle
    ./publish/efbundle --connection "${{ secrets.SQL_CONNECTION_STRING }}"
```
To:
```yaml
- name: Run EF migrations
  run: |
    chmod +x ./publish/efbundle
    ./publish/efbundle --connection "$SQL_CONN"
  env:
    SQL_CONN: ${{ secrets.SQL_CONNECTION_STRING }}
```

**A5. Docker Smoke Test**
File: `.github/workflows/ci.yml:73`
Remove `|| true` from the `docker run` command. Ensure `docker stop smoke-test` still has `|| true` (container may not have started).

Before reporting done: stage all your changes and commit with message `wip: stream A — deploy/infra fixes`. If a pre-commit hook fails, fix the issue and retry the commit. Do NOT report done without a successful commit — uncommitted worktree changes are lost on cleanup.

---

## Stream B — Program.cs Cloud Hardening

### Task B: Cloud Readiness Fixes in Program.cs + DbSeeder
> Model: sonnet | Effort: high | Agent: worktree | /clear before starting

You are hardening the ASP.NET Core 8 app for Azure App Service deployment. Working directory: `/home/bruce/bad/eindproject`.

**Do these 8 things:**

**B1. EnableRetryOnFailure (BLOCKER)**
File: `src/FundingRateArb.Web/Program.cs:87-88`
Change:
```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
```
To:
```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sqlOpts => sqlOpts.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(30),
            errorNumbersToAdd: null)));
```

**B2. Health Check Endpoint (BLOCKER)**
File: `src/FundingRateArb.Web/FundingRateArb.Web.csproj`
Add NuGet package:
```xml
<PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore" Version="8.0.*" />
```

File: `src/FundingRateArb.Web/Program.cs`
After `builder.Services.AddControllersWithViews();` (line 280), add:
```csharp
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>();
```
Add `using Microsoft.Extensions.Diagnostics.HealthChecks;` if needed.

After `app.UseRateLimiter();` (line 328), add:
```csharp
app.MapHealthChecks("/health");
```

**B3. Remove MigrateAsync from DbSeeder (BLOCKER)**
File: `src/FundingRateArb.Infrastructure/Seed/DbSeeder.cs:21`
Delete the line `await context.Database.MigrateAsync();`. Keep everything else in `SeedAsync` unchanged. The deploy pipeline's `efbundle` handles migrations.

**B4. Remove TrustServerCertificate from Production Connection String**
File: `src/FundingRateArb.Web/appsettings.json`
Remove `TrustServerCertificate=True` from the DefaultConnection string. The Azure connection string (set via App Service Configuration) will use `Encrypt=True` without `TrustServerCertificate`. Keep `TrustServerCertificate=True` in `appsettings.Development.json` for local SQL Server.

**B5. Connection Pool Sizing**
File: `src/FundingRateArb.Web/appsettings.json`
Add `Max Pool Size=20;Connection Timeout=30;` to the DefaultConnection string (after the existing params).

**B6. Global Rate Limiting on Controllers**
File: `src/FundingRateArb.Web/Program.cs:256-271`
Add a `general` rate limiter after the `signalr` limiter:
```csharp
options.AddFixedWindowLimiter("general", opt =>
{
    opt.Window = TimeSpan.FromMinutes(1);
    opt.PermitLimit = 60;
    opt.QueueLimit = 0;
});
```

Then change the two `MapControllerRoute` calls (around line 330-331) to add `.RequireRateLimiting("general")`:
```csharp
app.MapControllerRoute("areas", "{area:exists}/{controller=Home}/{action=Index}/{id?}")
    .RequireRateLimiting("general");
app.MapControllerRoute("default", "{controller=Dashboard}/{action=Index}/{id?}")
    .RequireRateLimiting("general");
```

**B7. Disable File Log Sink in Production**
File: `src/FundingRateArb.Web/Program.cs:62-68`
Wrap the `.WriteTo.File(...)` call inside `if (isDevelopment)`:
```csharp
if (isDevelopment)
{
    lc.WriteTo.File(
        path: "logs/fundingratearb-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate:
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] " +
            "{SourceContext} | {Message:lj} | {Properties:j}{NewLine}{Exception}");
}
```

**B8. AutoValidateAntiforgeryToken Global Filter**
File: `src/FundingRateArb.Web/Program.cs:280`
Change:
```csharp
builder.Services.AddControllersWithViews();
```
To:
```csharp
builder.Services.AddControllersWithViews(options =>
    options.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute()));
```

After all changes, run `dotnet build` to verify 0 errors.

Before reporting done: stage all your changes and commit with message `wip: stream B — cloud hardening`. If a pre-commit hook fails, fix the issue and retry the commit. Do NOT report done without a successful commit — uncommitted worktree changes are lost on cleanup.

---

## Stream C — Business Logic Safety

### Task C: Fix Trading Safety Issues in Orchestrator, ExecutionEngine, HealthMonitor
> Model: sonnet | Effort: high | Agent: worktree | /clear before starting

You are fixing critical business logic safety issues in the trading pipeline. Working directory: `/home/bruce/bad/eindproject`.

**Do these 4 things:**

**C1. Fix _consecutiveLosses Tracking (CRITICAL)**
File: `src/FundingRateArb.Infrastructure/BackgroundServices/BotOrchestrator.cs`

The `_consecutiveLosses` field is incorrectly incremented on failed opens and reset on successful opens. It should track actual trading LOSSES (position closed with negative PnL).

Changes:
1. **Remove** `_consecutiveLosses = 0;` from the successful open branch (~line 245)
2. **Remove** `_consecutiveLosses++;` and the surrounding if-block from the failed open else branch (~lines 263-269). Keep the exponential cooldown logic that follows.
3. Add a public method for recording close results:
```csharp
public void RecordCloseResult(decimal realizedPnl)
{
    if (realizedPnl < 0)
        _consecutiveLosses++;
    else
        _consecutiveLosses = 0;
}
```
4. In the health monitor close loop (the section where `ClosePositionAsync` is called after health check), call `RecordCloseResult` after each successful close. Look at `RunCycleAsync` — the health monitor returns positions to close via `CheckAndActAsync`. After each `ClosePositionAsync` completes, read the position's `RealizedPnl` and call `RecordCloseResult`.

Find where closes happen in the orchestrator cycle. After `executionEngine.ClosePositionAsync(pos, reason, ct)` is called, the position entity should have `RealizedPnl` set. Call:
```csharp
RecordCloseResult(pos.RealizedPnl ?? 0m);
```

**C2. Serialize Emergency Close Operations (CRITICAL)**
File: `src/FundingRateArb.Application/Services/ExecutionEngine.cs:165-173`

Replace `Task.WhenAll` with sequential execution for emergency closes. The current parallel execution shares a non-thread-safe DbContext.

Change from:
```csharp
var emergencyTasks = new List<Task>();
if (longSuccess)
    emergencyTasks.Add(TryEmergencyCloseWithRetryAsync(longConnector, opp.AssetSymbol, Side.Long, config.UpdatedByUserId, ct));
if (shortSuccess)
    emergencyTasks.Add(TryEmergencyCloseWithRetryAsync(shortConnector, opp.AssetSymbol, Side.Short, config.UpdatedByUserId, ct));

if (emergencyTasks.Count > 0)
    await Task.WhenAll(emergencyTasks);
```
To:
```csharp
if (longSuccess)
    await TryEmergencyCloseWithRetryAsync(longConnector, opp.AssetSymbol, Side.Long, config.UpdatedByUserId, ct);
if (shortSuccess)
    await TryEmergencyCloseWithRetryAsync(shortConnector, opp.AssetSymbol, Side.Short, config.UpdatedByUserId, ct);
```

**C3. PositionHealthMonitor — Price Feed Failure Escalation (HIGH)**
File: `src/FundingRateArb.Application/Services/PositionHealthMonitor.cs`

Add a `ConcurrentDictionary<int, int> _priceFetchFailures` field to track consecutive failures per position ID.

In the catch block at line 132-135, change from just logging to:
```csharp
catch (Exception ex)
{
    var failures = _priceFetchFailures.AddOrUpdate(pos.Id, 1, (_, v) => v + 1);
    _logger.LogWarning(ex, "Failed to check health for position #{Id} ({Failures} consecutive): {Message}",
        pos.Id, failures, ex.Message);

    if (failures >= 5)
    {
        _uow.Alerts.Add(new Alert
        {
            UserId = pos.UserId,
            ArbitragePositionId = pos.Id,
            Type = AlertType.PriceFeedFailure,
            Severity = AlertSeverity.Critical,
            Message = $"Price feed failed {failures} consecutive times for " +
                      $"{pos.Asset?.Symbol ?? "unknown"}. " +
                      "Stop-loss protection is INACTIVE. Consider manual close.",
        });
    }
}
```

On successful health check (before the `continue` on line 108, or after the alert check at line 130), reset the counter:
```csharp
_priceFetchFailures.TryRemove(pos.Id, out _);
```

File: `src/FundingRateArb.Domain/Enums/AlertType.cs`
Add `PriceFeedFailure` to the `AlertType` enum.

**C4. Remove Entry Price Overwrite (HIGH)**
File: `src/FundingRateArb.Application/Services/ExecutionEngine.cs:272-275`
If these lines still exist in the faulted-exception branch of `ClosePositionAsync`:
```csharp
if (!longFailed && longCloseTask.IsCompletedSuccessfully)
    position.LongEntryPrice = longCloseTask.Result.FilledPrice;
if (!shortFailed && shortCloseTask.IsCompletedSuccessfully)
    position.ShortEntryPrice = shortCloseTask.Result.FilledPrice;
```
Delete them. The close fill data is already stored in the alert message. Entry prices must be preserved for manual PnL computation.

After all changes, run `dotnet build` to verify 0 errors.

Before reporting done: stage all your changes and commit with message `wip: stream C — business logic safety`. If a pre-commit hook fails, fix the issue and retry the commit. Do NOT report done without a successful commit — uncommitted worktree changes are lost on cleanup.

---

## Stream D — Critical Test Gaps

### Task D: Tests for ConfigValidator + Updated BotOrchestrator + HealthMonitor
> Model: sonnet | Effort: high | Agent: worktree | /clear before starting

You are writing tests for critical untested components and updating existing tests for the changes made in Stream C. Working directory: `/home/bruce/bad/eindproject`.

**IMPORTANT**: This stream depends on Stream C's changes to `BotOrchestrator`, `PositionHealthMonitor`, and `ExecutionEngine`. Write tests against the NEW behavior described below. If the source code doesn't match yet (because Stream C hasn't merged), write the tests to match the EXPECTED new behavior — they will pass after Stream C merges.

**Do these 4 things:**

**D1. ConfigValidatorTests.cs (new file)**
File: `tests/FundingRateArb.Tests.Unit/Services/ConfigValidatorTests.cs`

Create a new test class with tests for all 11 validation rules in `ConfigValidator.cs`. Use a helper method that creates a valid `BotConfiguration` as a baseline, then mutate one property per test:

```csharp
public class ConfigValidatorTests
{
    private readonly ConfigValidator _sut = new();

    private static BotConfiguration ValidConfig() => new()
    {
        OpenThreshold = 0.0003m,
        AlertThreshold = 0.0001m,
        CloseThreshold = 0m,
        FeeAmortizationHours = 24,
        RateStalenessMinutes = 15,
        MaxHoldTimeHours = 72,
        DefaultLeverage = 5,
        MaxConcurrentPositions = 3,
        AllocationTopN = 3,
        AllocationStrategy = AllocationStrategy.EqualWeight,
        MinPositionSizeUsdc = 10m,
        DailyDrawdownPausePct = 0.05m,
    };
```

Tests needed (each should assert `result.IsValid` and check `result.Errors` contains the expected message):
1. `ValidConfig_ReturnsValid`
2. `OpenThreshold_LessThanOrEqualAlert_Invalid`
3. `AlertThreshold_Zero_Invalid`
4. `AlertThreshold_Negative_Invalid`
5. `CloseThreshold_Negative_Invalid`
6. `FeeAmortizationHours_Zero_Invalid`
7. `RateStalenessMinutes_Zero_Invalid`
8. `FeeAmortization_ExceedsMaxHold_Invalid`
9. `Leverage_Above10_Invalid`
10. `MaxPositions_LessThanTopN_NonConcentrated_Invalid`
11. `MaxPositions_LessThanTopN_Concentrated_Valid` (Concentrated strategy doesn't require this)
12. `MinPositionSize_Zero_Invalid`
13. `DailyDrawdown_Zero_Invalid`
14. `DailyDrawdown_AboveOne_Invalid`

**D2. BotOrchestrator Consecutive Loss Tests (update existing)**
File: `tests/FundingRateArb.Tests.Unit/BackgroundServices/BotOrchestratorTests.cs`

Add or update tests for the new C1 behavior:
1. `OpenFails_DoesNotIncrementConsecutiveLosses` — mock OpenPositionAsync to return (false, "API error"). Verify `ConsecutiveLosses` is still 0.
2. `RecordCloseResult_NegativePnl_IncrementsCounter` — call `RecordCloseResult(-5m)` 3 times. Verify `ConsecutiveLosses == 3`.
3. `RecordCloseResult_PositivePnl_ResetsCounter` — increment to 2, then call `RecordCloseResult(1m)`. Verify `ConsecutiveLosses == 0`.
4. `ConsecutiveLosses_ReachesLimit_PausesOpens` — set `ConsecutiveLossPause = 3` in config, call `RecordCloseResult(-1m)` 3 times, run a cycle, verify SignalEngine is called but no `OpenPositionAsync`.

**D3. PositionHealthMonitor Price Feed Failure Tests (add to existing)**
File: `tests/FundingRateArb.Tests.Unit/Services/PositionHealthMonitorTests.cs`

Add tests for C3 escalation:
1. `PriceFeedFailure_SingleFailure_NoAlert` — mock `GetMarkPriceAsync` to throw once. Verify no Critical alert added.
2. `PriceFeedFailure_FiveConsecutive_CreatesCriticalAlert` — mock to throw 5 times. Verify `AlertType.PriceFeedFailure` alert added with Critical severity.
3. `PriceFeedFailure_RecoveryResetsCounter` — fail 3 times, then succeed. Verify counter is reset (fail 3 more → still no alert because counter restarted at 0).

**D4. ExecutionEngine Emergency Close Serialization Test (add to existing)**
File: `tests/FundingRateArb.Tests.Unit/Services/ExecutionEngineTests.cs`

Add a test verifying C2:
1. `OpenPosition_BothLegsSucceedThenOneFails_EmergencyCloseRunsSequentially` — mock both legs to succeed, but set up the second connector to fail on close. Verify that `TryEmergencyCloseWithRetryAsync` is called sequentially (not via Task.WhenAll). You can verify this by checking that `_uow.SaveAsync` is not called while another close is in progress (use a callback on the mock to assert single-threaded access).

After writing all tests, run `dotnet build` and `dotnet test` to verify they compile (some may fail if Stream C hasn't merged yet — that's expected).

Before reporting done: stage all your changes and commit with message `wip: stream D — critical test gaps`. If a pre-commit hook fails, fix the issue and retry the commit. Do NOT report done without a successful commit — uncommitted worktree changes are lost on cleanup.

---

## Lead: Post-Merge Verification

After all 4 streams complete:

1. Merge all worktree branches into `feat/pre-deploy-hardening`
2. `dotnet build` — 0 errors
3. `dotnet test` — all pass
4. Restart app on localhost:5273
5. Playwright: verify dashboard, SignalR "Live", opportunities, `/health` returns 200
6. Commit, push, merge to main, `/post-merge`
