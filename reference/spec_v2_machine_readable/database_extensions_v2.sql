-- AOI Vision Platform V2 logical additive SQL. DRAFT, NOT a production migration.
-- Apply AFTER database_schema_sqlite.sql on a fresh/offline SQLite database for design tests.
PRAGMA foreign_keys = ON;
CREATE TABLE IF NOT EXISTS station_solution_snapshots (
  release_id TEXT NOT NULL REFERENCES solution_releases(release_id),
  channel_id TEXT NOT NULL,
  physical_device_id TEXT NOT NULL,
  station_manifest_sha256 TEXT NOT NULL,
  display_roi_json TEXT NOT NULL,
  detection_rois_json TEXT NOT NULL,
  algorithm_chain_json TEXT NOT NULL,
  model_bindings_json TEXT NOT NULL,
  published_at_utc TEXT NOT NULL,
  PRIMARY KEY(release_id,channel_id)
);
CREATE TABLE IF NOT EXISTS plc_transaction_journal (
  txn_id TEXT PRIMARY KEY,
  host_id TEXT NOT NULL,
  utc_time TEXT NOT NULL,
  epoch INTEGER,
  workpiece_id TEXT REFERENCES workpieces(workpiece_id),
  signal_id TEXT NOT NULL,
  station_id TEXT,
  direction TEXT NOT NULL CHECK(direction IN ('READ','WRITE','READBACK')),
  request_words_json TEXT,
  response_words_json TEXT,
  expected_before_json TEXT,
  outcome TEXT NOT NULL,
  correlation_id TEXT,
  source_profile_sha256 TEXT NOT NULL,
  actor_id TEXT,
  safety_gate_json TEXT
);
CREATE INDEX IF NOT EXISTS ix_plc_signal_time ON plc_transaction_journal(signal_id,utc_time);
CREATE INDEX IF NOT EXISTS ix_plc_piece_time ON plc_transaction_journal(workpiece_id,utc_time);
CREATE TABLE IF NOT EXISTS camera_readback_snapshots (
  readback_id TEXT PRIMARY KEY,
  deployment_id TEXT REFERENCES deployments(deployment_id),
  host_id TEXT NOT NULL,
  physical_device_id TEXT NOT NULL,
  capture_config_sha256 TEXT NOT NULL,
  requested_config_json TEXT NOT NULL,
  applied_readback_json TEXT NOT NULL,
  diff_json TEXT NOT NULL,
  verified INTEGER NOT NULL DEFAULT 0 CHECK(verified IN (0,1)),
  recorded_at_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS alarm_state_transitions (
  transition_id TEXT PRIMARY KEY,
  alarm_id TEXT NOT NULL REFERENCES alarms(alarm_id),
  old_state TEXT NOT NULL,
  new_state TEXT NOT NULL,
  trigger_event_id TEXT,
  changed_by TEXT,
  root_cause_evidence_json TEXT NOT NULL,
  recorded_at_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_alarm_transitions ON alarm_state_transitions(alarm_id,recorded_at_utc);
CREATE TABLE IF NOT EXISTS log_segment_registry (
  segment_id TEXT PRIMARY KEY,
  host_id TEXT NOT NULL,
  category TEXT NOT NULL,
  interval_start_utc TEXT NOT NULL,
  interval_end_utc TEXT,
  record_count INTEGER NOT NULL DEFAULT 0,
  storage_uri TEXT NOT NULL,
  compression TEXT,
  sha256 TEXT,
  archive_state TEXT NOT NULL CHECK(archive_state IN ('WRITING','SEALED','ARCHIVED','MISSING','CORRUPT')),
  recorded_at_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS data_retention_policies (
  policy_id TEXT PRIMARY KEY,
  version TEXT NOT NULL,
  object_kind TEXT NOT NULL,
  production_condition TEXT NOT NULL,
  retention_days INTEGER CHECK(retention_days>=0),
  legal_hold_exempt INTEGER NOT NULL DEFAULT 0 CHECK(legal_hold_exempt IN (0,1)),
  capacity_action TEXT NOT NULL,
  approved_by TEXT,
  activated_at_utc TEXT
);
CREATE TABLE IF NOT EXISTS station_draft_revisions (
  draft_id TEXT NOT NULL REFERENCES solution_drafts(draft_id),
  channel_id TEXT NOT NULL,
  revision INTEGER NOT NULL CHECK(revision>=1),
  base_revision INTEGER NOT NULL,
  patch_json TEXT NOT NULL,
  algorithm_diff_json TEXT,
  writer_id TEXT NOT NULL,
  saved_at_utc TEXT NOT NULL,
  PRIMARY KEY(draft_id,channel_id,revision)
);
CREATE TABLE IF NOT EXISTS capture_group_provenance (
  capture_group_id TEXT NOT NULL,
  source_host_id TEXT NOT NULL,
  physical_device_id TEXT NOT NULL,
  source_frame_id TEXT NOT NULL,
  raw_asset_id TEXT REFERENCES image_assets(asset_id),
  source_utc_time TEXT NOT NULL,
  split_recipe_sha256 TEXT,
  pixel_sha256 TEXT,
  PRIMARY KEY(capture_group_id,source_host_id,physical_device_id)
);
-- Does not modify/migrate installed databases. Real migration needs a separate versioned review.
