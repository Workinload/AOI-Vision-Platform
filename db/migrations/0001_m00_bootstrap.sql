-- M00 bootstrap table only, not the 27-table AOI production schema.
-- A6 owns all later db/migrations and will replace this prototype through a reviewed migration.
PRAGMA foreign_keys=ON;
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS m00_meta (name TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS m00_events (seq INTEGER PRIMARY KEY AUTOINCREMENT, utc TEXT NOT NULL, kind TEXT NOT NULL);
