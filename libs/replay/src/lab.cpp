#include "aoi/replay/lab.hpp"
#include <opencv2/imgcodecs.hpp>
#include <iomanip>
#include <sstream>
#include <stdexcept>
#include <set>
namespace aoi::agent03::replay {
namespace fs=std::filesystem;
namespace {
std::string now_utc(){return "2026-10-16T00:00:00Z";} // deterministic fixture clock; never label actual clock
std::string render_result(const RunResult& r){
  std::ostringstream out;
  out<<"{\"source\":\"OFFLINE_LAB\",\"channel\":\""<<r.channel<<"\",\"processing\":\""<<r.processing
     <<"\",\"quality\":\""<<r.quality<<"\",\"defects\":"<<r.defect_count<<"}";
  return out.str();
}
}
LabRunner::LabRunner(const fs::path& root,const fs::path& sql):root_(fs::absolute(root)/"lab_only"),db_(fs::absolute(root)/"lab_only","OFFLINE_LAB",sql){
  fs::create_directories(root_/"inputs");root_=fs::canonical(root_);
}
fs::path LabRunner::validated_input(const fs::path& relative)const{
  if(relative.empty()||relative.is_absolute())throw std::invalid_argument("lab input must be relative");
  for(const auto& part:relative)if(part==".."||part==".")throw std::invalid_argument("lab input path traversal");
  auto path=fs::weakly_canonical(root_/"inputs"/relative);
  auto rel=path.lexically_relative(root_/"inputs");
  if(rel.empty()||*rel.begin()=="..")throw std::invalid_argument("lab input escapes sandbox");
  return path;
}
std::string LabRunner::recipe_hash(const Recipe& r)const{
  std::ostringstream s;s<<r.name<<":"<<r.detection_roi.x<<":"<<r.detection_roi.y<<":"<<r.detection_roi.width<<":"<<r.detection_roi.height
                        <<":"<<r.gray_threshold<<":"<<r.min_area<<":"<<r.seed<<":"<<r.simulated_mock;
  return sha256_hex(s.str());
}
void LabRunner::persist(const std::string& mode,const std::string& source,const Recipe& recipe,const std::string& value){
  db_.write_lab_run("LAB-"+std::to_string(++next_id_),mode,source,recipe_hash(recipe),value,now_utc());
}
RunResult LabRunner::single(const Sample& sample,const Recipe& recipe){
  RunResult result;result.channel=sample.channel;
  if(sample.channel.size()!=4||sample.channel.substr(0,2)!="CH")throw std::invalid_argument("invalid lab channel");
  auto path=validated_input(sample.relative_png);
  cv::Mat image=cv::imread(path.string(),cv::IMREAD_UNCHANGED);
  if(!image.empty()){
    vision::validate_gray8(image);
    result.raw_pixel_sha=vision::pixel_sha256(image);
    auto detection=recipe.simulated_mock?vision::deterministic_mock(image,recipe.seed):
         vision::threshold_defects(image,recipe.detection_roi,recipe.gray_threshold,recipe.min_area);
    result.source=detection.origin;
    result.processing=detection.processing;result.quality=detection.quality;
    result.defect_count=static_cast<int>(detection.defects.size());
    result.overlay_json=vision::overlay_json(detection);
    // Ensure artifact byte and pixel hashes describe an actual decoded lossless PNG.
    auto encoding=vision::lossless_png_roundtrip(image);
    result.file_sha=encoding.file_sha;
    if(encoding.decoded_pixel_sha!=result.raw_pixel_sha)throw std::runtime_error("lab pixel hash mismatch");
  }
  persist("SINGLE",sample.channel,recipe,render_result(result));return result;
}
std::vector<RunResult> LabRunner::batch(const std::vector<Sample>& input,const Recipe& recipe){
  if(input.size()>4096)throw std::invalid_argument("lab batch size too large");
  std::vector<RunResult> results;results.reserve(input.size());
  for(const auto& item:input)results.push_back(single(item,recipe));
  persist("BATCH","batch",recipe,"{\"items\":"+std::to_string(results.size())+"}");return results;
}
std::vector<RunResult> LabRunner::piece11(const std::vector<Sample>& items,const Recipe& recipe){
  if(items.size()!=11)throw std::invalid_argument("exactly 11 channel sample declarations required");
  std::set<std::string> expected;for(int c=1;c<=11;++c){std::ostringstream s;s<<"CH"<<std::setw(2)<<std::setfill('0')<<c;expected.insert(s.str());}
  for(const auto& item:items)if(expected.erase(item.channel)!=1)throw std::invalid_argument("duplicate or unknown channel");
  if(!expected.empty())throw std::invalid_argument("missing channel declaration");
  auto result=batch(items,recipe);
  for(const auto& item:result)if(item.processing=="MISSING"){
    persist("PIECE11","incomplete",recipe,"{\"outcome\":\"HOLD\"}");return result;
  }
  persist("PIECE11","complete",recipe,"{\"outcome\":\"LAB_ONLY\"}");return result;
}
ABResult LabRunner::ab(const Sample& sample,const Recipe& a,const Recipe& b){
  ABResult comparison;comparison.version_a=single(sample,a);comparison.version_b=single(sample,b);
  comparison.changed_quality=comparison.version_a.quality!=comparison.version_b.quality;
  comparison.changed_defect_count=comparison.version_b.defect_count-comparison.version_a.defect_count;
  persist("AB",sample.channel,a,"{\"changed_quality\":"+std::string(comparison.changed_quality?"true":"false")+
          ",\"delta_defects\":"+std::to_string(comparison.changed_defect_count)+"}");
  return comparison;
}
} // namespace aoi::agent03::replay
