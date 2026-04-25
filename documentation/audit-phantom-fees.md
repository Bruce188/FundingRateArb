# Audit — Phantom Fees on Never-Filled Positions

Diagnosis target: 38 of 70 closed positions in production are `Status=EmergencyClosed`,
`HoldSec<60`, `LongFilledQuantity=0 AND ShortFilledQuantity=0`, yet record an average
0.082 USDC of `EntryFeesUsdc + ExitFeesUsdc` (total $3.13). The legs never filled.

## Root cause: hybrid (A + B)

Both candidate root causes contribute. The phantom rows are produced by failure branches
that (a) called `SetEmergencyCloseFees` against an `OrderResultDto` whose `FilledQuantity`
was zero (that method computed `notional = price * 0 = 0`, so fees came out zero — but
the surviving DB rows weren't always zero, see B), AND (b) routed those zero-fill outcomes
to `PositionStatus.EmergencyClosed` instead of a no-trade terminal status, persisting them
in the realized-PnL universe.

## Suspect paths (file:line, current behavior, required change)

### `ExecutionEngine.cs:475-480` — concurrent-fail branch (one leg succeeds, other fails)
- Before fix: `Status = neverExisted ? Failed : EmergencyClosed`. When the surviving leg
  reported `Success=true, FilledQuantity=0`, `neverExisted` was false (a close call ran)
  → row landed in `EmergencyClosed` with the zero-quantity leg's "fees" persisted.
- Fix (task 2.1, commit 0081e22): added `bothEffectivelyZero` guard — when both effective
  fill quantities are zero, route to `Failed` regardless.

### `ExecutionEngine.cs:521`, `:553` — sequential throw + sequential fail branches
- Before fix: same shape as the concurrent-fail branch, but the zero-fill guard was
  not initially mirrored here. Discovered during /review v251.
- Fix (commit fcdfaf5): added the same `firstResult.FilledQuantity <= 0m` guard to the
  status assignment in both branches.

### `ExecutionEngine.cs:633` — concurrent-exception path
- Before fix: when one task threw and the other returned `Success=true, FilledQuantity=0`,
  `allNeverExisted=false` after the close call → row landed in `EmergencyClosed`.
- Fix (commit fcdfaf5): added a `concurrentExBothEffectivelyZero` check that mirrors the
  fail-branch guard.

### `ExecutionEngine.cs:755` — post-fill end-guard
- Before fix: when both legs returned `Success=true` but with zero `FilledQuantity`,
  the post-fill guard fired and routed to `EmergencyClosed`. Same root cause.
- Fix (task 2.1, commit 0081e22): rewrote as `(longQty > 0m || shortQty > 0m) ? EmergencyClosed : Failed`.

### `EmergencyCloseHandler.SetEmergencyCloseFees` — fee-write helper
- Before fix: when called with `FilledQuantity=0`, performed the math (`notional * feeRate`,
  arithmetic gives zero) but did not explicitly clear `EntryFeesUsdc`/`ExitFeesUsdc`/`RealizedPnl`.
  Stale phantom values from prior writes could survive on the same row.
- Fix (task 2.1, commit 0081e22): added explicit `<= 0m` early-return that zeros all three
  fields. Negative-quantity branch (commit 645ee4a) additionally logs at Error level.

### `BotOrchestrator.RunBootSweepAsync` — boot recovery (NOT a phantom-fee path)
- Initially modified by task 2.2 (commit 5e235af) with a zero-fill short-circuit, but
  this was unsafe at boot time because in-memory state is wiped — DB `FilledQuantity=0`
  cannot be trusted as a substitute for exchange verification.
- Fix (commit 019947c): removed the short-circuit. BootSweep now always delegates to
  `ConfirmOrRollbackAsync` → `AwaitBothLegConfirmationAsync`, which already handles
  the OpenConfirm-timeout zero-fill case correctly inside `ExecutionEngine`.

## Were exchange-reported fees flowing in?

No. All `EntryFeesUsdc` / `ExitFeesUsdc` writes in the failure branches were synthetic
(`notional × ExchangeFeeConstants.GetTakerFeeRate(name)`). No connector reports actual
exchange-charged fees back into these fields today. This is tracked as a separate feature
(`fix/exchange-reported-fees-source-of-truth`) and out of scope for this PR.

## Not retroactively rewritten

Per the constraint: the 38 historical phantom rows are flagged via `IsPhantomFeeBackfill`
(see `scripts/backfill-phantom-fee-flag.{md,sql}`). Their `EntryFeesUsdc`, `ExitFeesUsdc`,
`RealizedPnl`, and `Status` fields are untouched.
