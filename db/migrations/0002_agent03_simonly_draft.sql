-- Agent03 standalone SIM_ONLY migration candidate. NOT production-approved C01.
-- Never replace or edit locked reference SQL. Revision approval remains A0/A10.
CREATE TABLE IF NOT EXISTS schema_migrations (
 version INTEGER PRIMARY KEY, source_sha256 TEXT NOT NULL, applied_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS workpieces (
 workpiece_id TEXT PRIMARY KEY NOT NULL, epoch INTEGER NOT NULL CHECK(epoch>=1),
 release_id TEXT NOT NULL, barcode TEXT, entered_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS raw_assets (
 asset_id TEXT PRIMARY KEY NOT NULL, workpiece_id TEXT NOT NULL REFERENCES workpieces(workpiece_id),
 host_id TEXT NOT NULL, capture_group_id TEXT NOT NULL, physical_device_id TEXT NOT NULL,
 capture_id TEXT NOT NULL, attempt_no INTEGER NOT NULL CHECK(attempt_no>=1),
 pixel_format TEXT NOT NULL, width_px INTEGER NOT NULL CHECK(width_px>0),
 height_px INTEGER NOT NULL CHECK(height_px>0), stride_bytes INTEGER NOT NULL CHECK(stride_bytes>0),
 expected_pixel_sha256 TEXT NOT NULL CHECK(length(expected_pixel_sha256)=64),
 storage_uri TEXT, codec TEXT NOT NULL DEFAULT 'RAW8', encoded_file_sha256 TEXT,
 decoded_pixel_sha256 TEXT, file_bytes INTEGER,
 save_state TEXT NOT NULL CHECK(save_state IN ('PENDING','WRITING','AVAILABLE','CORRUPT','FAILED')),
 created_utc TEXT NOT NULL,
 UNIQUE(host_id,capture_group_id,physical_device_id,attempt_no),
 UNIQUE(asset_id,capture_group_id,attempt_no),
 CHECK(save_state != 'AVAILABLE' OR (
   storage_uri IS NOT NULL AND length(trim(storage_uri))>0 AND
   encoded_file_sha256 IS NOT NULL AND length(encoded_file_sha256)=64 AND
   decoded_pixel_sha256 IS NOT NULL AND length(decoded_pixel_sha256)=64 AND
   file_bytes IS NOT NULL AND file_bytes>0))
);
CREATE INDEX IF NOT EXISTS idx_raw_piece ON raw_assets(workpiece_id,created_utc);
CREATE TABLE IF NOT EXISTS channel_asset_links (
 workpiece_id TEXT NOT NULL REFERENCES workpieces(workpiece_id),
 channel_id TEXT NOT NULL CHECK(channel_id IN ('CH01','CH02','CH03','CH04','CH05','CH06','CH07','CH08','CH09','CH10','CH11')),
 capture_group_id TEXT NOT NULL, attempt_no INTEGER NOT NULL CHECK(attempt_no>=1),
 raw_asset_id TEXT NOT NULL,
 PRIMARY KEY(workpiece_id,channel_id,capture_group_id,attempt_no),
 FOREIGN KEY(raw_asset_id,capture_group_id,attempt_no)
  REFERENCES raw_assets(asset_id,capture_group_id,attempt_no)
);
CREATE TABLE IF NOT EXISTS result_images (
 image_id TEXT PRIMARY KEY, workpiece_id TEXT NOT NULL REFERENCES workpieces(workpiece_id),
 channel_id TEXT NOT NULL, raw_asset_id TEXT REFERENCES raw_assets(asset_id),
 relative_uri TEXT NOT NULL, file_sha256 TEXT NOT NULL, pixel_sha256 TEXT NOT NULL,
 overlay_json TEXT NOT NULL DEFAULT '[]', created_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS channel_results (
 workpiece_id TEXT NOT NULL REFERENCES workpieces(workpiece_id),
 epoch INTEGER NOT NULL CHECK(epoch>=1), release_id TEXT NOT NULL,
 channel_id TEXT NOT NULL, capture_id TEXT NOT NULL, attempt_no INTEGER NOT NULL CHECK(attempt_no>=1),
 processing TEXT NOT NULL CHECK(processing IN ('SUCCEEDED','ERROR','MISSING','TIMEOUT','CANCELLED','SKIPPED_VERIFIED')),
 quality TEXT NOT NULL CHECK(quality IN ('OK','NG','UNDECIDED','MISSING')),
 model_sha256 TEXT, created_utc TEXT NOT NULL,
 PRIMARY KEY(workpiece_id,epoch,release_id,channel_id,capture_id,attempt_no),
 CHECK(processing='SUCCEEDED' OR quality!='OK')
);
CREATE TABLE IF NOT EXISTS final_results (
 workpiece_id TEXT PRIMARY KEY REFERENCES workpieces(workpiece_id),
 epoch INTEGER NOT NULL, release_id TEXT NOT NULL,
 quality TEXT NOT NULL CHECK(quality IN ('OK','NG','HOLD')),
 first_valid INTEGER NOT NULL CHECK(first_valid IN (0,1)),
 decided_utc TEXT NOT NULL, event_id TEXT NOT NULL UNIQUE
);
CREATE TABLE IF NOT EXISTS inbox_messages (
 sender_id TEXT NOT NULL, message_id TEXT NOT NULL, payload_sha256 TEXT NOT NULL,
 received_utc TEXT NOT NULL, PRIMARY KEY(sender_id,message_id)
);
CREATE TABLE IF NOT EXISTS effects (
 effect_id TEXT PRIMARY KEY, workpiece_id TEXT REFERENCES workpieces(workpiece_id),
 payload_sha256 TEXT NOT NULL, applied_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS outbox_messages (
 sender_id TEXT NOT NULL, message_id TEXT NOT NULL, payload_sha256 TEXT NOT NULL,
 payload_json TEXT NOT NULL, state TEXT NOT NULL DEFAULT 'PENDING'
 CHECK(state IN ('PENDING','SENT','ACKED','FAILED')),
 created_utc TEXT NOT NULL,
 PRIMARY KEY(sender_id,message_id)
);
CREATE TABLE IF NOT EXISTS trace_events (
 event_id TEXT PRIMARY KEY, workpiece_id TEXT REFERENCES workpieces(workpiece_id),
 source TEXT NOT NULL, trace_id TEXT NOT NULL, event_json TEXT NOT NULL,
 created_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_trace_piece ON trace_events(workpiece_id,created_utc);
CREATE TABLE IF NOT EXISTS defect_instances (
 defect_id TEXT PRIMARY KEY, workpiece_id TEXT NOT NULL REFERENCES workpieces(workpiece_id),
 channel_id TEXT NOT NULL, defect_class INTEGER NOT NULL CHECK(defect_class>=2),
 rect_json TEXT NOT NULL, created_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS rejudge_events (
 rejudge_id TEXT PRIMARY KEY, workpiece_id TEXT NOT NULL REFERENCES workpieces(workpiece_id),
 new_quality TEXT NOT NULL CHECK(new_quality IN ('OK','NG','HOLD')),
 actor TEXT NOT NULL, reason TEXT NOT NULL, created_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS lab_runs (
 run_id TEXT PRIMARY KEY, run_mode TEXT NOT NULL CHECK(run_mode IN ('SINGLE','BATCH','PIECE11','AB')),
 source_piece TEXT, recipe_sha256 TEXT NOT NULL, result_json TEXT NOT NULL, created_utc TEXT NOT NULL,
 run_context TEXT NOT NULL DEFAULT 'OFFLINE_LAB' CHECK(run_context='OFFLINE_LAB')
);
