-- AOI Vision Platform: SQLite initial logical schema DRAFT v1.0
-- Not a production migration; verify DB migrations, WAL backup, persistence, concurrency, indexes on actual load.
-- All times are ISO-8601 UTC text. Image bytes live in AssetStore, not SQLite BLOBs.
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS machine_profiles (
  machine_profile_version TEXT PRIMARY KEY,
  profile_json TEXT NOT NULL,
  source_sha256 TEXT,
  field_verified INTEGER NOT NULL DEFAULT 0 CHECK(field_verified IN (0,1)),
  created_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS products (
  product_id TEXT PRIMARY KEY,
  name_cn TEXT NOT NULL,
  customer_code TEXT,
  created_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS solution_releases (
  release_id TEXT PRIMARY KEY,
  product_id TEXT NOT NULL REFERENCES products(product_id),
  release_version TEXT NOT NULL,
  machine_profile_version TEXT NOT NULL REFERENCES machine_profiles(machine_profile_version),
  manifest_sha256 TEXT NOT NULL,
  manifest_uri TEXT NOT NULL,
  validation_report_uri TEXT,
  published_by TEXT NOT NULL,
  published_at_utc TEXT NOT NULL,
  UNIQUE(product_id, release_version)
);

CREATE TABLE IF NOT EXISTS solution_drafts (
  draft_id TEXT PRIMARY KEY,
  product_id TEXT NOT NULL REFERENCES products(product_id),
  base_release_id TEXT REFERENCES solution_releases(release_id),
  revision INTEGER NOT NULL DEFAULT 1 CHECK(revision>=1),
  draft_manifest_json TEXT NOT NULL,
  status TEXT NOT NULL CHECK(status IN ('DRAFT','VALIDATED','REJECTED','MERGED')),
  updated_by TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS deployments (
  deployment_id TEXT PRIMARY KEY,
  release_id TEXT NOT NULL REFERENCES solution_releases(release_id),
  target_epoch INTEGER,
  state TEXT NOT NULL CHECK(state IN ('STAGING','PREPARING','PREPARED','COMMITTING','ACTIVE','ABORTED','FAILED','RETIRED')),
  master_readback_sha256 TEXT,
  slave_readback_sha256 TEXT,
  started_at_utc TEXT NOT NULL,
  completed_at_utc TEXT,
  failure_reason TEXT
);

CREATE TABLE IF NOT EXISTS model_artifacts (
  model_id TEXT NOT NULL,
  version TEXT NOT NULL,
  sha256 TEXT NOT NULL,
  asset_uri TEXT NOT NULL,
  format TEXT NOT NULL,
  runtime_version TEXT,
  input_contract_json TEXT NOT NULL,
  output_contract_json TEXT NOT NULL,
  PRIMARY KEY(model_id, version, sha256)
);

CREATE TABLE IF NOT EXISTS production_epochs (
  epoch INTEGER PRIMARY KEY CHECK(epoch>=1),
  release_id TEXT NOT NULL REFERENCES solution_releases(release_id),
  started_at_utc TEXT NOT NULL,
  ended_at_utc TEXT,
  reason TEXT NOT NULL,
  active INTEGER NOT NULL DEFAULT 0 CHECK(active IN (0,1))
);

CREATE TABLE IF NOT EXISTS workpieces (
  workpiece_id TEXT PRIMARY KEY,
  epoch INTEGER NOT NULL REFERENCES production_epochs(epoch),
  release_id TEXT NOT NULL REFERENCES solution_releases(release_id),
  raw_barcode TEXT,
  normalized_barcode TEXT,
  barcode_rule_version TEXT,
  slot_context_json TEXT,
  state TEXT NOT NULL,
  entered_at_utc TEXT NOT NULL,
  closed_at_utc TEXT,
  CHECK(length(workpiece_id) > 0)
);
CREATE INDEX IF NOT EXISTS ix_wp_barcode ON workpieces(normalized_barcode,entered_at_utc);
CREATE INDEX IF NOT EXISTS ix_wp_epoch_state ON workpieces(epoch,state);

CREATE TABLE IF NOT EXISTS acquisitions (
  acquisition_id TEXT PRIMARY KEY,
  workpiece_id TEXT NOT NULL REFERENCES workpieces(workpiece_id),
  epoch INTEGER NOT NULL,
  station_id TEXT NOT NULL,
  channel_id TEXT NOT NULL,
  physical_device_id TEXT NOT NULL,
  capture_group_id TEXT NOT NULL,
  attempt_no INTEGER NOT NULL CHECK(attempt_no>=1),
  frame_id TEXT,
  plc_correlation_id TEXT,
  status TEXT NOT NULL,
  capture_at_utc TEXT,
  created_at_utc TEXT NOT NULL,
  UNIQUE(workpiece_id,channel_id,epoch,attempt_no)
);
CREATE INDEX IF NOT EXISTS ix_acq_capture_group ON acquisitions(capture_group_id,physical_device_id);
CREATE INDEX IF NOT EXISTS ix_acq_workpiece ON acquisitions(workpiece_id,channel_id);

CREATE TABLE IF NOT EXISTS channel_results (
  result_id TEXT PRIMARY KEY,
  acquisition_id TEXT NOT NULL UNIQUE REFERENCES acquisitions(acquisition_id),
  release_id TEXT NOT NULL REFERENCES solution_releases(release_id),
  station_recipe_sha256 TEXT NOT NULL,
  model_sha256 TEXT,
  processing_status TEXT NOT NULL CHECK(processing_status IN ('EXECUTED','ERROR','MISSING','SKIPPED_VERIFIED','CANCELED')),
  quality_status TEXT NOT NULL CHECK(quality_status IN ('OK','NG','UNDECIDED','NOT_APPLICABLE')),
  defect_count INTEGER NOT NULL DEFAULT 0 CHECK(defect_count>=0),
  processing_ms REAL,
  superseded INTEGER NOT NULL DEFAULT 0 CHECK(superseded IN (0,1)),
  decided_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS defect_instances (
  defect_id TEXT PRIMARY KEY,
  result_id TEXT NOT NULL REFERENCES channel_results(result_id),
  defect_class_id TEXT NOT NULL,
  display_name_cn TEXT NOT NULL,
  score REAL,
  geometry_json TEXT NOT NULL,
  measurement_json TEXT,
  masked_by_policy INTEGER NOT NULL DEFAULT 0 CHECK(masked_by_policy IN (0,1))
);
CREATE INDEX IF NOT EXISTS ix_defect_class ON defect_instances(defect_class_id,result_id);

CREATE TABLE IF NOT EXISTS final_results (
  final_id TEXT PRIMARY KEY,
  workpiece_id TEXT NOT NULL UNIQUE REFERENCES workpieces(workpiece_id),
  release_id TEXT NOT NULL REFERENCES solution_releases(release_id),
  outcome TEXT NOT NULL CHECK(outcome IN ('OK','NG','UNDECIDED')),
  valid_first_decision INTEGER NOT NULL CHECK(valid_first_decision IN (0,1)),
  expected_channels_json TEXT NOT NULL,
  received_channels_json TEXT NOT NULL,
  end_barcode_match TEXT NOT NULL CHECK(end_barcode_match IN ('MATCH','MISMATCH','FAILED','UNKNOWN','NOT_REQUIRED')),
  plc_commit_state TEXT NOT NULL,
  decided_at_utc TEXT NOT NULL,
  decided_event_id TEXT NOT NULL UNIQUE
);
CREATE INDEX IF NOT EXISTS ix_final_decided ON final_results(decided_at_utc,outcome);

CREATE TABLE IF NOT EXISTS image_assets (
  asset_id TEXT PRIMARY KEY,
  workpiece_id TEXT REFERENCES workpieces(workpiece_id),
  capture_group_id TEXT,
  acquisition_id TEXT REFERENCES acquisitions(acquisition_id),
  parent_asset_id TEXT REFERENCES image_assets(asset_id),
  kind TEXT NOT NULL CHECK(kind IN ('RAW_FRAME','RESULT_IMAGE','PREVIEW_IMAGE')),
  origin_host_id TEXT NOT NULL,
  physical_device_id TEXT,
  channel_id TEXT,
  storage_uri TEXT,
  codec TEXT,
  width_px INTEGER CHECK(width_px>0),
  height_px INTEGER CHECK(height_px>0),
  stride_bytes INTEGER,
  pixel_format TEXT,
  file_sha256 TEXT,
  decoded_pixel_sha256 TEXT,
  file_bytes INTEGER,
  save_state TEXT NOT NULL CHECK(save_state IN ('PENDING','WRITING','AVAILABLE','FAILED','CORRUPT','ARCHIVED','EXPIRED')),
  release_id TEXT REFERENCES solution_releases(release_id),
  transform_chain_json TEXT,
  created_at_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_asset_workpiece ON image_assets(workpiece_id,channel_id,kind);
CREATE INDEX IF NOT EXISTS ix_asset_capture ON image_assets(capture_group_id,physical_device_id,kind);

CREATE TABLE IF NOT EXISTS alarms (
  alarm_id TEXT PRIMARY KEY,
  alarm_code TEXT NOT NULL,
  host_id TEXT NOT NULL,
  station_id TEXT,
  signal_id TEXT,
  physical_device_id TEXT,
  workpiece_id TEXT REFERENCES workpieces(workpiece_id),
  parent_alarm_id TEXT REFERENCES alarms(alarm_id),
  severity TEXT NOT NULL,
  state TEXT NOT NULL CHECK(state IN ('ACTIVE','ACKNOWLEDGED','CLEARED','CLOSED')),
  raised_at_utc TEXT NOT NULL,
  last_seen_at_utc TEXT NOT NULL,
  occurrence_count INTEGER NOT NULL DEFAULT 1,
  ack_by TEXT,
  ack_at_utc TEXT,
  cleared_at_utc TEXT,
  evidence_json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_alarm_active ON alarms(state,raised_at_utc);

CREATE TABLE IF NOT EXISTS trace_events (
  event_id TEXT PRIMARY KEY,
  utc_time TEXT NOT NULL,
  host_id TEXT NOT NULL,
  trace_id TEXT NOT NULL,
  event_type TEXT NOT NULL,
  severity TEXT NOT NULL,
  workpiece_id TEXT REFERENCES workpieces(workpiece_id),
  station_id TEXT,
  channel_id TEXT,
  signal_id TEXT,
  epoch INTEGER,
  release_id TEXT,
  event_json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_trace_piece ON trace_events(workpiece_id,utc_time);
CREATE INDEX IF NOT EXISTS ix_trace_id ON trace_events(trace_id,utc_time);
CREATE INDEX IF NOT EXISTS ix_trace_station ON trace_events(station_id,utc_time);

CREATE TABLE IF NOT EXISTS audit_events (
  audit_id TEXT PRIMARY KEY,
  actor_id TEXT NOT NULL,
  host_id TEXT NOT NULL,
  action_type TEXT NOT NULL,
  target_entity_id TEXT NOT NULL,
  base_version TEXT,
  new_version TEXT,
  reason TEXT,
  decision TEXT NOT NULL,
  evidence_json TEXT NOT NULL,
  at_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_audit_target ON audit_events(target_entity_id,at_utc);

CREATE TABLE IF NOT EXISTS outbox_messages (
  sender_id TEXT NOT NULL,
  message_id TEXT NOT NULL,
  epoch INTEGER,
  payload_type TEXT NOT NULL,
  payload_sha256 TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  state TEXT NOT NULL CHECK(state IN ('PENDING','SENT','ACKED','FAILED','DEAD_LETTER')),
  retry_count INTEGER NOT NULL DEFAULT 0,
  next_retry_utc TEXT,
  created_at_utc TEXT NOT NULL,
  PRIMARY KEY(sender_id,message_id)
);
CREATE INDEX IF NOT EXISTS ix_outbox_pending ON outbox_messages(state,next_retry_utc);

CREATE TABLE IF NOT EXISTS inbox_dedup (
  sender_id TEXT NOT NULL,
  message_id TEXT NOT NULL,
  payload_sha256 TEXT NOT NULL,
  persisted_at_utc TEXT NOT NULL,
  PRIMARY KEY(sender_id,message_id)
);

CREATE TABLE IF NOT EXISTS lab_runs (
  lab_run_id TEXT PRIMARY KEY,
  run_context TEXT NOT NULL CHECK(run_context='OFFLINE_LAB'),
  source_workpiece_id TEXT,
  target_release_sha256 TEXT NOT NULL,
  created_by TEXT NOT NULL,
  started_at_utc TEXT NOT NULL,
  ended_at_utc TEXT,
  result_summary_json TEXT,
  report_uri TEXT
);
-- Offline lab runs do not FK to production final_results and must never increment production KPIs.
