-- Backfill IsPhantomFeeBackfill flag for historical zero-fill EmergencyClosed positions.
-- Idempotent: the WHERE clause excludes already-flagged rows, so re-running changes 0 rows.
--
-- Usage:
--   sqlcmd -S <server> -d <db> -U <user> -P <pw> -i scripts/backfill-phantom-fee-flag.sql
-- or paste into SSMS / Azure Portal Query Editor.
--
-- Expected rows affected: ~38
-- Rollback: UPDATE ArbitragePositions SET IsPhantomFeeBackfill = 0
--             WHERE IsPhantomFeeBackfill = 1 AND Status = 'EmergencyClosed'
--               AND LongFilledQuantity = 0 AND ShortFilledQuantity = 0;

UPDATE ArbitragePositions
SET IsPhantomFeeBackfill = 1
WHERE LongFilledQuantity = 0
  AND ShortFilledQuantity = 0
  AND Status = 'EmergencyClosed'
  AND IsPhantomFeeBackfill = 0;
