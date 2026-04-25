# Phantom Fee Backfill

## What it does

Sets `IsPhantomFeeBackfill = 1` on historical `ArbitragePosition` rows where both legs
were never filled (`LongFilledQuantity = 0` and `ShortFilledQuantity = 0`) but the position
was emergency-closed, leaving phantom entry/exit fees in the database.

## When to run

Run once against production after deploying the migration that added the
`IsPhantomFeeBackfill` column (`20260425064612_AddIsPhantomFeeBackfillFlag`).

## Expected impact

Approximately **38 rows** updated. The script is idempotent — re-running it after the
initial pass changes 0 rows.

## How to run

```bash
sqlcmd -S <server> -d <database> -U <user> -P <password> \
  -i scripts/backfill-phantom-fee-flag.sql
```

Or paste the file contents into SSMS / Azure Portal Query Editor.

## Rollback

```sql
UPDATE ArbitragePositions
SET IsPhantomFeeBackfill = 0
WHERE IsPhantomFeeBackfill = 1
  AND Status = 'EmergencyClosed'
  AND LongFilledQuantity = 0
  AND ShortFilledQuantity = 0;
```

## What is NOT touched

`EntryFeesUsdc`, `ExitFeesUsdc`, `RealizedPnl`, and `Status` are never modified by this
script — only the `IsPhantomFeeBackfill` flag is written.
