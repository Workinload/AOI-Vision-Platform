#pragma once
// Read-only SQL projections over one host's SIM_ONLY WAL database.
#include <filesystem>
#include <map>
#include <string>
#include <vector>
namespace aoi::agent03 {
struct FirstPassYield {int total=0,ok=0,ng=0,hold=0;double yield=0.0;};
struct PieceTrace {std::string workpiece, quality, decided_utc;};
class Statistics {
 public:
  explicit Statistics(const std::filesystem::path& db_file);
  FirstPassYield day_utc(const std::string& yyyy_mm_dd) const;
  FirstPassYield range_utc(const std::string& from_day,const std::string& exclusive_end) const;
  std::map<int,int> defects(const std::string& from_day,const std::string& exclusive_end) const;
  std::vector<PieceTrace> history(const std::string& from_day,const std::string& exclusive_end,int limit,int offset) const;
 private:std::filesystem::path db_;
};
}
