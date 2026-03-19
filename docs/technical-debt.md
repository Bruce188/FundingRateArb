# Technical Debt — Deferred Items

> Items identified during code review that are worth fixing but deferred for now.
> Review periodically and promote to a plan when the time is right.

---

## B-W4: Zero-volume opportunities not filtered in SignalEngine

**Source**: Review V6, backend-reviewer
**File**: `src/FundingRateArb.Application/Services/SignalEngine.cs:22-76`
**Severity**: Warning

Opportunities are constructed with `LongVolume24h` and `ShortVolume24h` from snapshot data, but there is no minimum volume threshold applied before adding to the list. A pair with 0 volume on one exchange passes the `net >= config.OpenThreshold` gate. The PositionSizer will produce `liquidityLimit = 0 * VolumeFraction = 0` and return 0, correctly blocking execution — but it is wasteful to surface zero-volume opportunities to the dashboard and run them through the sizer every cycle.

**Fix**: Add a volume guard in SignalEngine before adding to the opportunity list:
```csharp
var minVolume = Math.Min(longSnapshot.Volume24h, shortSnapshot.Volume24h);
if (minVolume <= 0) continue;
```

**Why deferred**: PositionSizer already blocks zero-volume opportunities from execution. The only impact is unnecessary dashboard noise and wasted sizer CPU cycles, which is negligible at current scale.

---

## A9/B-S2: Position UserId coupled to config.UpdatedByUserId — wrong semantics

**Source**: Review V6, arch-reviewer + backend-reviewer
**File**: `src/FundingRateArb.Application/Services/ExecutionEngine.cs:42`
**Severity**: Warning (architecture/domain modelling)

When the bot auto-opens a position, it stamps `position.UserId = config.UpdatedByUserId` — the user who last saved the BotConfiguration. This means:
- If Admin A updates thresholds, all future auto-opened positions are "owned" by Admin A
- If Admin B later changes the kill switch, ownership silently shifts to Admin B
- The "owner" of a position has no relationship to who authorized the trade

The correct fix requires a dedicated `OperatorUserId` field on `BotConfiguration` (or a service account concept), but the `BotOrchestrator` runs as a `BackgroundService` with no `HttpContext` — there is no logged-in user to attribute positions to at execution time.

**Fix**: Add an `OperatorUserId` field to `BotConfiguration` that is set explicitly in the Admin UI (separate from `UpdatedByUserId`). Use that for `position.UserId` in `ExecutionEngine.OpenPositionAsync`. Requires: new domain field, EF migration, BotConfig admin form update, ExecutionEngine change.

**Why deferred**: Single-operator school project with one admin account. The semantic error has no practical impact until multi-user operation is needed.

---

## S12: UnrealizedPnl populated from AccumulatedFunding — financially misleading

**Source**: Review V6, backend-reviewer
**File**: `src/FundingRateArb.Infrastructure/BackgroundServices/BotOrchestrator.cs:294`
**Severity**: Suggestion

`UnrealizedPnl = pos.AccumulatedFunding` — the comment says "best estimate until live mark-to-market" but accumulated funding is not the same as unrealized PnL. True unrealized PnL should include mark-to-market price changes on both legs (delta exposure from price drift). The current value only reflects funding payments received, not position value changes.

**Fix**: Compute proper unrealized PnL by fetching current mark prices for both legs and calculating:
```
unrealizedPnl = accumulatedFunding
    + (currentLongMark - entryLongPrice) * longQuantity
    - (currentShortMark - entryShortPrice) * shortQuantity
```
This requires mark price data in the orchestrator cycle (already available via `PositionHealthMonitor`'s cache).

**Why deferred**: For delta-neutral positions the mark-to-market component is near-zero by design (that's the point of the hedge). The funding component dominates PnL. The approximation is acceptable for the current use case.

---

## S8: RequireConfirmedAccount = false — no email verification

**Source**: Review V6, sec-reviewer
**File**: `src/FundingRateArb.Web/Program.cs:96`
**Severity**: Warning (security)

`options.SignIn.RequireConfirmedAccount = false` allows any registered user to log in without email verification. For a financial app with real exchange credentials, this is a security gap if registration is ever opened to external users.

**Fix**:
1. Create a Gmail account for the app (or use an existing one)
2. Configure SMTP via User Secrets (`Email:SmtpHost`, `Email:SmtpPort`, `Email:Username`, `Email:Password`)
3. Implement `IEmailSender` (or use a library like `FluentEmail`)
4. Set `RequireConfirmedAccount = true`
5. Add email confirmation flow views

**Why deferred**: No email provider configured. Registration is currently admin-controlled (seed user only). Will implement when a Gmail SMTP account is set up.

---

## UI: Auto-skip to next coin on insufficient balance/margin + manual retry button

**Source**: Production observation (V7 monitoring)
**Severity**: High (UX + operational)

When a trade fails due to insufficient balance or margin, the bot should automatically skip that opportunity and try the next available coin in the same cycle. Currently it fails one opportunity per cycle, gets a cooldown, and the next coin isn't attempted until the following cycle.

Additionally, add a **manual retry button** on the dashboard that clears all cooldowns and forces the bot to re-evaluate opportunities immediately (useful after depositing funds).

**Fix**:
1. In `BotOrchestrator.RunCycleAsync`: on `Insufficient margin/balance` errors, continue iterating through remaining opportunities instead of returning
2. Add `POST /api/bot/retry` endpoint that clears `_failedOpCooldowns` and triggers an immediate cycle
3. Add a "Retry Now" button in the dashboard UI that calls this endpoint

---

## UI: Limit opportunities table to 20 rows

**Source**: User request
**Severity**: Low (UX)

The Opportunities page currently shows all discovered opportunities. Limit the default display to 20 rows to reduce page load time and visual noise.

**Fix**: Add `.Take(20)` in `SignalEngine` or the controller, ordered by net APR descending so the best opportunities are shown first. Optionally add a "Show all" toggle.

---

## UI: Mark all alerts as read button

**Source**: User request
**Severity**: Medium (UX)

No bulk action exists for dismissing alerts. Users must mark each alert as read individually.

**Fix**: Add `POST /Alerts/MarkAllRead` endpoint + button in the Alerts view (and the alert dropdown in the nav bar). Call `UPDATE Alerts SET IsRead = 1 WHERE IsRead = 0` via a new `MarkAllAsReadAsync()` method on `IAlertRepository`.

---

## UI: Consolidate Dashboard and Opportunities into single page

**Source**: User request
**Severity**: Medium (UX)

The Dashboard and Opportunities pages show related information that would be more useful on a single view. Merge them so the main dashboard shows KPI cards, bot status, live rates, AND the opportunities table together.

**Fix**: Move the opportunities table (from `OpportunitiesController/Index`) into the `Dashboard/Index` view below the existing KPI cards and positions section. Keep the Opportunities controller for API/JSON endpoints but remove the separate page from navigation. Update SignalR `ReceiveOpportunities` handler to target the consolidated view.
