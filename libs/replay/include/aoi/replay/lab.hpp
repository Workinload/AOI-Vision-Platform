#pragma once
// Separate OFFLINE_LAB role: no PLC, no network, no production counters.
#include "aoi/data/wal_store.hpp"
#include "aoi/vision/pipeline.hpp"
#include <filesystem>
#include <string>
#include <vector>
namespace aoi::agent03::replay {
struct Recipe {
  std::string name="A";
  vision::Roi detection_roi;
  int gray_threshold=200,min_area=1;
  bool simulated_mock=false;
  std::uint64_t seed=0;
};
struct Sample {std::string channel;std::filesystem::path relative_png;};
struct RunResult {
  std::string channel,processing="MISSING",quality="UNDECIDED",source="OFFLINE_LAB";
  std::string raw_pixel_sha,file_sha,overlay_json="[]";
  int defect_count=0;
};
struct ABResult {
  RunResult version_a,version_b;
  bool changed_quality=false;
  int changed_defect_count=0;
};
class LabRunner {
 public:
  LabRunner(const std::filesystem::path& isolated_root,
            const std::filesystem::path& migration_sql);
  RunResult single(const Sample& sample,const Recipe& recipe);
  std::vector<RunResult> batch(const std::vector<Sample>& samples,const Recipe& recipe);
  std::vector<RunResult> piece11(const std::vector<Sample>& samples,const Recipe& recipe);
  ABResult ab(const Sample& sample,const Recipe& a,const Recipe& b);
  const std::filesystem::path& db_path()const{return db_.db_path();}
 private:
  std::filesystem::path validated_input(const std::filesystem::path& relative)const;
  std::string recipe_hash(const Recipe& r)const;
  void persist(const std::string& mode,const std::string& source,
               const Recipe& recipe,const std::string& json);
  std::filesystem::path root_;
  WalStore db_;
  std::uint64_t next_id_=0;
};
}
