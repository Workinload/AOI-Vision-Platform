#pragma once
// SHA/shape/classes gate; consumes files only in SIM_ONLY offline mode.
#include <filesystem>
#include <map>
#include <optional>
#include <string>
#include <vector>
namespace aoi::agent03::models {
struct ModelManifest {
  std::string id,version,format,expected_sha256;
  std::filesystem::path local_model;
  int batch=1,channels=1,height=0,width=0;
  std::vector<std::string> classes;
};
std::string file_sha256(const std::filesystem::path& file);
class ModelRegistry {
 public:
  void register_model(const ModelManifest& manifest);
  const ModelManifest& lookup(const std::string& id,const std::string& version)const;
  std::size_t size()const{return registry_.size();}
 private:std::map<std::string,ModelManifest> registry_;
};
}
