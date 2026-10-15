#pragma once
// Agent03 SIM_ONLY data seam, NOT the A0-frozen C01 interface.
#include <cstdint>
#include <filesystem>
#include <optional>
#include <stdexcept>
#include <string>
#include <vector>
struct sqlite3;
namespace aoi::agent03 {
namespace fs = std::filesystem;
std::string sha256_hex(const void* bytes, std::size_t length);
std::string sha256_hex(const std::string& content);

struct RawParent {
  std::string asset_id, workpiece_id, host_id, group_id, device_id, capture_id;
  int attempt_no = 1;
  std::string pixel_format = "GRAY8";
  int width = 0, height = 0, stride = 0;
  std::string expected_pixel_sha;
  std::string uri, file_sha, pixel_sha, codec, state;
  std::int64_t file_bytes = 0;
};
struct ChannelLink {
  std::string workpiece_id, channel_id, group_id, asset_id;
  int attempt_no = 1;
};
struct ChannelResult {
  std::string workpiece_id, release_id, channel_id, capture_id, processing, quality, model_sha;
  int epoch = 1, attempt = 1;
};
struct Outgoing {
  std::string sender, message_id, payload, workpiece_id, event_id;
};
struct Final {
  std::string workpiece_id, release_id, quality, event_id, decided_utc;
  int epoch = 1;
  bool first_valid = false;
};
enum class InboundAck { Persisted, DuplicateSamePayload };
class WalStore {
 public:
  WalStore(const fs::path& storage_root, const std::string& role,
           const fs::path& migration_sql);
  ~WalStore();
  WalStore(const WalStore&) = delete;
  WalStore& operator=(const WalStore&) = delete;
  const fs::path& db_path() const { return db_path_; }
  const std::string& host() const { return host_; }
  void create_piece(const std::string& id, int epoch, const std::string& release,
                    const std::string& barcode, const std::string& utc);
  void reserve_raw(const RawParent& parent, const std::string& utc);
  void raw_writing(const std::string& id, const std::string& uri);
  void raw_available(const std::string& id, const std::string& file_sha,
                     const std::string& decoded_sha, std::int64_t bytes);
  void raw_fail(const std::string& id, const std::string& state);
  std::optional<RawParent> raw(const std::string& id) const;
  std::vector<RawParent> unfinished_raw() const;
  void link_raw(const ChannelLink& link);
  void record_channel(const ChannelResult& result, const std::string& utc);
  InboundAck receive(const std::string& sender, const std::string& message_id,
                     const std::string& payload, const std::string& effect_id,
                     const std::string& workpiece_id, const std::string& utc,
                     bool inject_crash_before_commit = false);
  bool finalize_and_outbox(const Final& final, const Outgoing& message);
  void mark_outbox_acked(const std::string& sender, const std::string& message_id);
  int pending_outbox() const;
  void record_defect(const std::string& defect_id, const std::string& piece,
                     const std::string& channel, int cls,
                     const std::string& geometry, const std::string& utc);
  void append_rejudge(const std::string& id, const std::string& piece,
                      const std::string& quality, const std::string& actor,
                      const std::string& reason, const std::string& utc);
  void write_trace(const std::string& id, const std::string& piece,
                   const std::string& source, const std::string& trace,
                   const std::string& json, const std::string& utc);
  void add_result_image(const std::string& image_id, const std::string& piece,
                        const std::string& channel, const std::string& parent,
                        const std::string& uri, const std::string& file_hash,
                        const std::string& pixel_hash,
                        const std::string& overlay_json, const std::string& utc);
  std::string gallery_json(const std::string& piece) const;
  void write_lab_run(const std::string& run_id, const std::string& run_mode,
                     const std::string& source_piece, const std::string& recipe_sha,
                     const std::string& result_json, const std::string& utc);
  void online_backup(const fs::path& destination) const;
  // Isolated native test diagnostics only; NEVER expose to production UI/IPC.
  std::int64_t count_for_test(const std::string& table) const;
 private:
  sqlite3* db_ = nullptr;
  fs::path db_path_;
  std::string host_;
};
} // namespace aoi::agent03
