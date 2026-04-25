-- Backfill IsPhantomFeeBackfill flag for historical zero-fill EmergencyClosed positions.
-- Idempotent: the WHERE clause excludes already-flagged rows, so re-running changes 0 rows.
-- Status is stored as int; 4 = PositionStatus.EmergencyClosed.
-- LongFilledQuantity/ShortFilledQuantity are nullable — historical rows from the throw/fail
-- code paths in ExecutionEngine left them NULL, so the WHERE clause must treat NULL as zero.
--
-- Usage (do NOT pass the password on the command line — set it via env var):
--   SQLCMDPASSWORD=*** sqlcmd -S <server> -d <db> -U <user> -i scripts/backfill-phantom-fee-flag.sql
-- or paste into SSMS / Azure Portal Query Editor.
--
-- Expected rows affected: ~38. The transaction aborts if more than 50 rows would be
-- updated — protects against an over-broad WHERE clause distorting financial reporting.
--
-- Rollback:
--   UPDATE ArbitragePositions SET IsPhantomFeeBackfill = 0
--    WHERE IsPhantomFeeBackfill = 1 AND Status = 4
--      AND ISNULL(LongFilledQuantity, 0) = 0 AND ISNULL(ShortFilledQuantity, 0) = 0;

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('ArbitragePositions')
      AND name = 'IsPhantomFeeBackfill'
)
BEGIN
    THROW 50001, 'IsPhantomFeeBackfill column not found — run migration 20260425064612_AddIsPhantomFeeBackfillFlag first.', 1;
END

BEGIN TRAN;

UPDATE ArbitragePositions
SET IsPhantomFeeBackfill = 1
WHERE Status = 4
  AND ISNULL(LongFilledQuantity, 0) = 0
  AND ISNULL(ShortFilledQuantity, 0) = 0
  AND IsPhantomFeeBackfill = 0;

IF @@ROWCOUNT > 50
BEGIN
    ROLLBACK;
    THROW 50002, 'Backfill affected more than 50 rows — bound exceeded, no changes committed.', 1;
END

COMMIT;
