#pragma once
// Local, SIM_ONLY RAW8 lossless persisted asset implementation. C01 not frozen.
#include "aoi/data/wal_store.hpp"
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <span>
#include <string>
namespace aoi::agent03 {
struct RecoveryReport {int promoted = 0, failed = 0, pending = 0;};
class LosslessStore {
 public:
  LosslessStore(WalStore& data, const std::filesystem::path& asset_root);
  void write_raw_bytes(const std::string& asset_id,const std::string& uri,
                       std::span<const std::uint8_t> raw,
                       bool inject_stop_after_rename=false);
  void write_raw_file(const std::string& asset_id,const std::string& uri,
                      const std::filesystem::path& input,
                      bool inject_stop_after_rename=false);
  bool verify_raw(const std::string& asset_id);
  RecoveryReport reconcile();
  const std::filesystem::path& root() const {return root_;}
  // No network URIs: only strictly relative immutable local files.
  std::filesystem::path resolve_relative(const std::string& uri) const;
 private:
  void validate_raw(const RawParent& parent,std::uintmax_t length,const std::string& hash) const;
  void finish_stage(const RawParent& parent,const std::string& uri,
                    const std::filesystem::path& temp,
                    bool inject);
  WalStore& data_;
  std::filesystem::path root_;
};
}
