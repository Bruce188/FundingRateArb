# Runbook: Azure Production Stabilization (2026-04-09)

## Context

Active deployment `686a332e-f712-4341-b4c2-bab4ed7492b7` activated at `2026-04-09T10:29:38Z` (commits `928b1f9` PR #122 + `d5533d8` PR #123) introduced a native crash loop (7 SIGSEGVs in 48 minutes), intermittent SQL login-phase failures (104 occurrences on 04-09), WLFI Lighter/Aster trade execution drift, a 100% CoinGlass v3 screening failure, and a latent dYdX `oraclePrice: null` parse bug.

This runbook captures the Azure-side operations needed alongside the code fixes landed by this PR. All code fixes land in the branch `fix/azure-production-stabilization-2026-04-09`; this runbook is what you execute after the branch is merged and redeployed.

## Pre-merge sanity checks

Run these on the feature branch before merging:

```bash
dotnet build
dotnet test
```

Both must be clean. Local smoke test:

```bash
dotnet run --project src/FundingRateArb.Web
# In another terminal:
curl -sS http://localhost:5000/healthz | jq
curl -sS http://localhost:5000/ -I
```

`/healthz` must return HTTP 200 and a JSON body containing `"status": "Healthy"` (or `"Degraded"` if you're testing against a down database on purpose). `/` must return HTTP 200.

## Step 1 — Enable minidump capture

The next native crash needs a core dump so we can analyze the unmanaged frame. Without this, we are bisecting PR #122 blind.

```bash
az webapp config appsettings set \
    --resource-group rg-fundingratearb1 \
    --name fundingratearb \
    --settings \
        DOTNET_DbgEnableMiniDump=1 \
        DOTNET_DbgMiniDumpType=4 \
        "DOTNET_DbgMiniDumpName=/home/LogFiles/dumps/core.%d.%t" \
        DOTNET_EnableCrashReport=1
```

Notes:
- `%d` is the PID and `%t` is the UTC timestamp. Quote the full value so your shell does not expand them.
- `DOTNET_DbgMiniDumpType=4` = heap dump (largest). Use `2` (mini with heap) if disk space is tight.
- `/home/LogFiles/` is writable by the App Service process on Linux by default.

Verify:

```bash
az webapp config appsettings list \
    --resource-group rg-fundingratearb1 \
    --name fundingratearb \
    --query "[?starts_with(name, 'DOTNET_Dbg') || name == 'DOTNET_EnableCrashReport'].[name,value]" \
    -o table
```

Expected output: 4 rows, all values non-empty.

The setting change triggers an automatic restart.

## Step 2 — Disable Application Insights Snapshot Debugger

The Snapshot Debugger and Profiler extensions inject native instrumentation and are known to interact badly with unmanaged crashes. Remove them temporarily to rule out instrumentation interference:

```bash
az webapp config appsettings delete \
    --resource-group rg-fundingratearb1 \
    --name fundingratearb \
    --setting-names \
        SnapshotDebugger_EXTENSION_VERSION \
        APPINSIGHTS_SNAPSHOTFEATURE_VERSION \
        APPINSIGHTS_PROFILERFEATURE_VERSION \
        DiagnosticServices_EXTENSION_VERSION
```

Keep `APPLICATIONINSIGHTS_CONNECTION_STRING` — only the native hooks are being removed. Managed telemetry still flows.

Restart:

```bash
az webapp restart --resource-group rg-fundingratearb1 --name fundingratearb
```

## Step 3 — SQL outbound IP firewall reconciliation

Most likely cause of the 104 login-phase failures on 04-09: the deploy triggered a container restart, the new container picked up an outbound IP that was not in the Azure SQL firewall allowlist.

```bash
# Current App Service outbound IPs
az webapp show \
    --resource-group rg-fundingratearb1 \
    --name fundingratearb \
    --query 'possibleOutboundIpAddresses' -o tsv \
    | tr ',' '\n' | sort -u > /tmp/as-ips.txt

# Current SQL firewall rules
az sql server firewall-rule list \
    --server sql-fundingratearb \
    --resource-group rg-fundingratearb1 \
    --query '[].[name,startIpAddress,endIpAddress]' -o tsv > /tmp/sql-rules.txt

cat /tmp/as-ips.txt
cat /tmp/sql-rules.txt
```

Compare the two. For each App Service IP not present in the SQL firewall list:

```bash
az sql server firewall-rule create \
    --server sql-fundingratearb \
    --resource-group rg-fundingratearb1 \
    --name "AppService-$(date +%s)-$i" \
    --start-ip-address <ip> \
    --end-ip-address <ip>
```

Alternatively, enable `Allow Azure services and resources to access this server` on the SQL server (less precise — allows all Azure tenants).

Verify after adding rules: retry a SQL-dependent endpoint and confirm no `TCP Provider, error: 35` in Application Insights `exceptions` (which is now populated by the unhandled-exception handlers landed in Task 1.1 and by the degraded-dashboard handling landed in Task 3.2).

## Step 4 — CoinGlass API key verification

Application Insights shows `open-api-v3.coinglass.com` at 13/13 failures. The key in Key Vault is either missing, invalid, or rotated.

```bash
KEY=$(az keyvault secret show \
    --vault-name kv-fundingratearb \
    --name CoinGlassApiKey \
    --query value -o tsv)

if [ -z "$KEY" ]; then
    echo "FAIL: CoinGlassApiKey is empty in Key Vault"
    exit 1
fi

curl -sS -o /tmp/cg-v3.json -w "v3 http=%{http_code}\n" \
    -H "CG-API-KEY: $KEY" \
    "https://open-api-v3.coinglass.com/public/v2/funding_rates_chart?symbol=BTC&type=C&interval=h1"

curl -sS -o /tmp/cg-v4.json -w "v4 http=%{http_code}\n" \
    -H "CG-API-KEY: $KEY" \
    "https://open-api-v4.coinglass.com/api/futures/funding-rate/arbitrage?usd=10000"

jq -r '.code, .msg, (.data | length)' /tmp/cg-v3.json
jq -r '.code, .msg, (.data | length)' /tmp/cg-v4.json
```

Expected: both `http=200` and `.code == "0"`. If either returns a non-zero code or 4xx:

1. Log into the CoinGlass dashboard and confirm the key is still valid for both v3 and v4 endpoints. Many accounts are v4-only now.
2. Rotate the key if needed:
   ```bash
   az keyvault secret set \
       --vault-name kv-fundingratearb \
       --name CoinGlassApiKey \
       --value '<new-key-from-dashboard>'
   ```
3. Restart the app: `az webapp restart -g rg-fundingratearb1 -n fundingratearb`.

Note: the SignalEngine treats CoinGlass as optional enrichment — the app will not fail if CoinGlass is down, it just loses the prioritization hint. Fix at your leisure but do fix it.

## Step 5 — Post-deploy monitoring window

Watch for at least 1 hour after redeploying.

**Application Insights queries (paste into portal, last 1h):**

```kql
// Any managed unhandled exceptions now reach Insights via the new handlers
exceptions
| where timestamp > ago(1h)
| summarize count() by problemId, type
| order by count_ desc
```

```kql
// SQL login-phase failures should be zero
exceptions
| where timestamp > ago(1h)
| where outerMessage contains "error: 35" or outerMessage contains "Connection reset by peer"
| count
```

```kql
// CoinGlass dependency calls — should be healthy or cleanly failing, not exception-throwing
dependencies
| where timestamp > ago(1h)
| where target contains "coinglass"
| summarize total=count(), failed=countif(success == false) by target
```

```kql
// HyperLiquid WebSocket — watch disconnect rate; new throttle kicks in above 20/min
traces
| where timestamp > ago(1h)
| where message contains "HyperLiquid disconnect rate"
| project timestamp, message
```

**Kudu file manager (https://fundingratearb.scm.azurewebsites.net/newui/fileManager):**
- Navigate to `/home/LogFiles/dumps/`.
- If any `core.<pid>.<ts>` files appear, proceed to Step 6.
- If none appear after 1 hour, the crash loop is stabilized.

## Step 6 — Analyze a minidump (only if one appears)

SSH into the App Service container:

```bash
az webapp ssh --resource-group rg-fundingratearb1 --name fundingratearb
```

Inside the container:

```bash
cd /home/LogFiles/dumps
ls -la
dotnet tool install -g dotnet-dump || true
export PATH="$PATH:/root/.dotnet/tools"

dotnet-dump analyze /home/LogFiles/dumps/core.<pid>.<ts>
```

Once inside the `dotnet-dump` REPL:

```
> clrstack -all
> clrthreads
> pe
> dumpheap -stat
```

Capture the output and file a follow-up fix based on the native frame. Typical suspects given the 2026-04-09 symptoms:
- HyperLiquid SDK `ClientWebSocket` native interop
- Polly circuit / Polly.Extensions.Http native handler interop
- `LeverageTierRefresher` or `CoinGlassScreeningService` HttpClient interop
- Application Insights Snapshot Debugger (which Step 2 should have disabled — if the crash recurs WITH Step 2 applied, the Snapshot Debugger is cleared as a suspect)

## Step 7 — Run Playwright availability test against production

After the rest of the runbook is complete and the app is stable, run the long-duration availability check:

```bash
E2E_AZURE_BASE_URL=https://fundingratearb.azurewebsites.net \
    npx playwright test \
        tests/FundingRateArb.Tests.E2E/availability-after-deploy.spec.ts
```

Pass criteria: 15 successful probes on `/` and `/healthz`, no response > 30 s, zero non-2xx.

If the test file does not exist yet, it is a deferred item from plan-v60 Task 9.2 (see "Deferred work" below).

## Step 8 — Bisect PR #122 if Steps 1-4 do not stabilize

If minidumps keep appearing or crash frequency is unchanged, bisect the three biggest additions in PR #122 one at a time:

1. `LeverageTierRefresher` — add a feature flag `Features:LeverageTierRefresher` (default `true`) and wire it in `Program.cs` so the background service is only registered when the flag is `true`. Redeploy with the flag `false`. Observe.
2. `CoinGlassScreeningService` — the v4 screening service. Gate its registration behind `Features:CoinGlassScreening`. Redeploy disabled. Observe.
3. `PositionHealthMonitor` — the +193 lines added in PR #122 include the new margin-monitoring and reconciliation logic. If the above two are cleared, audit this file next for unsafe patterns and add targeted logging.

Report the first toggle that stabilizes the process in the follow-up fix PR.

## Rollback plan (last resort)

The user has explicitly requested NOT to roll back unless all other options fail. Use only if Steps 1-8 fail:

```bash
git checkout main
git checkout -b revert/pr-122-123
git revert 928b1f9 d5533d8
git push -u origin revert/pr-122-123
gh pr create --title "revert: back out PR #122 + PR #123 pending native crash fix" \
    --body "Temporary revert to stabilize production while we bisect the native crash in PR #122. Will reopen after the offending code path is identified."
```

Merge and redeploy. Once stable, open a dedicated bisect ticket before re-landing the reverted work.

## Deferred work

The following items are intentionally deferred from plan-v60 — they do not block this stabilization PR but should be picked up in a follow-up iteration:

| Item | Source | Why deferred |
|------|--------|--------------|
| CoinGlass diagnostic logging + Polly circuit breaker | plan-v60 Task 7.1 | Current graceful-degrade path already returns `IReadOnlySet<string>.Empty` on failure; SignalEngine tolerates it. Hardening is nice-to-have, not P0. |
| CoinGlass response body logging on non-2xx | plan-v60 Task 7.1 | Same — nice for future debugging but not blocking. |
| WebSocket `funding_rate` fallback + negative rate tests | plan-v60 Task 8.1 (review-v117 NB1, N1) | Deferred test-debt. |
| Test helper undisposed HttpClient refactor | plan-v60 Task 8.2 (review-v117 N2) | Test-only nit. Pair with Task 7.1 when that lands. |
| `WebHostStartupTests`, `WlfiPairScenarioTests`, `StartupReconciliationIntegrationTests`, `SignalEnginePollyRetryIntegrationTests`, `HealthzDegradedIntegrationTests`, `CoinGlassScreeningServiceHttpIntegrationTests` | plan-v60 Task 9.1 | 6 new integration test files. Unit coverage for the same logic is in place; integration coverage is nice-to-have. |
| Playwright `availability-after-deploy.spec.ts`, `positions-page-reconciliation.spec.ts`, `dashboard-degraded-on-db-outage.spec.ts` | plan-v60 Task 9.2 | Post-deploy gated; will be written against the new admin endpoints in a dedicated E2E iteration. |
| Cooldown counter persistence fix | plan-v60 Task 5.3 | Log shows "(1 consecutive)" on each WLFI failure. Counter logic at `BotOrchestrator.cs:931` is wired correctly; the symptom comes from `CircuitBreakerManager.GetCooldownEntry` returning default entries after TTL expiry. Acceptable behavior for the current exponential-backoff cooldown design; revisit if pair failures continue to bypass the circuit breaker. |

## What IS landed in this PR

- Task 1.1 — `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` handlers (`src/FundingRateArb.Web/Infrastructure/UnhandledExceptionHandlers.cs`)
- Task 1.2 — `LeverageTierRefresher` audit + safety tests
- Task 2.1 — HyperLiquid stream `SemaphoreSlim` + disconnect throttle + handler try/catch (`HyperliquidMarketDataStream.cs`)
- Task 3.1 — `DatabaseHealthCheck` returning `Degraded` on failure + expanded EF retry error codes (`src/FundingRateArb.Infrastructure/HealthChecks/DatabaseHealthCheck.cs`, `Program.cs`)
- Task 3.2 — `SignalEngine` degraded result + `DashboardController` banner + centralized `SqlTransientErrorNumbers` (`SignalEngine.cs`, `FundingRateRepository.cs`, `DashboardController.cs`, `Views/Dashboard/Index.cshtml`)
- Task 4.1 — `StringDecimalConverter` handles `JsonTokenType.Null` (`DydxIndexerModels.cs`)
- Task 5.1 — `AsterConnector.GetSymbolConstraintsAsync` with 6h TTL cache (`AsterConnector.cs`)
- Task 5.2 — No-op (already satisfied by plan-v53 `ExecutionEngine.cs:340-391`)
- Task 5.3 — No-op for startup reconciliation half (already satisfied by plan-v58/v59 `PositionHealthMonitor.ReconcileOpenPositionsAsync`); cooldown counter fix deferred (see above)
- Task 6.1 — `SignalEngine` per-symbol notional cap filter via `IExchangeSymbolConstraintsProvider` (new interface in Application, implementation in Infrastructure)
- Task 9.3 — This runbook
