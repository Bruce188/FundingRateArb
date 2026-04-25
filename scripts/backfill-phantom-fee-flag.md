# Phantom Fee Backfill

## What it does

Sets `IsPhantomFeeBackfill = 1` on historical `ArbitragePosition` rows where both legs
were never filled (`LongFilledQuantity` is NULL or 0 AND `ShortFilledQuantity` is NULL or 0)
but the position was emergency-closed, leaving phantom entry/exit fees in the database.

`Status` is stored as an integer; 4 corresponds to `PositionStatus.EmergencyClosed`.
`LongFilledQuantity` and `ShortFilledQuantity` are nullable — historical rows from the
throw/fail code paths in `ExecutionEngine` left them NULL, so the WHERE clause treats
NULL as zero.

## When to run

Run once against production after deploying the migration that added the
`IsPhantomFeeBackfill` column (`20260425064612_AddIsPhantomFeeBackfillFlag`). The script
preflight-checks the column's existence and aborts if the migration has not been applied.

## Expected impact

Approximately **38 rows** updated. The script is idempotent — re-running it after the
initial pass changes 0 rows. The transaction aborts if more than 50 rows would be
updated, protecting against an over-broad WHERE clause distorting financial reporting.

## How to run

Set the password via env var (do NOT pass it on the command line — it leaks into shell
history and `ps auxe`):

```bash
SQLCMDPASSWORD='***' sqlcmd -S <server> -d <database> -U <user> \
  -i scripts/backfill-phantom-fee-flag.sql
```

Azure AD auth (`-G`) or integrated auth (`-E`) are preferable when available. Or paste
the file contents into SSMS / Azure Portal Query Editor.

## Rollback

```sql
UPDATE ArbitragePositions
SET IsPhantomFeeBackfill = 0
WHERE IsPhantomFeeBackfill = 1
  AND Status = 4
  AND ISNULL(LongFilledQuantity, 0) = 0
  AND ISNULL(ShortFilledQuantity, 0) = 0;
```

## What is NOT touched

`EntryFeesUsdc`, `ExitFeesUsdc`, `RealizedPnl`, and `Status` are never modified by this
script — only the `IsPhantomFeeBackfill` flag is written.
