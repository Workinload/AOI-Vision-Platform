#pragma once
// A7 SIM_ONLY isolated preview. Local contracts; NOT C01 wire ABI.
#include <openssl/sha.h>
#include <array>
#include <cstdint>
#include <iomanip>
#include <map>
#include <set>
#include <sstream>
#include <stdexcept>
#include <string>
#include <utility>
namespace aoi::a7::night {
inline std::string digest(const std::string& bytes) {
  unsigned char hash[SHA256_DIGEST_LENGTH];
  SHA256(reinterpret_cast<const unsigned char*>(bytes.data()),bytes.size(),hash);
  std::ostringstream s; for(auto c:hash)s<<std::hex<<std::setw(2)<<std::setfill('0')<<int(c);
  return s.str();
}
struct MachineProfile {
 std::string id,registry_version; std::map<std::string,std::string> device_role;
 bool valid()const {if(id.empty()||registry_version.empty()||device_role.size()!=9)return false;
   for(auto& [d,r]:device_role)if(d.empty()||(r!="MASTER"&&r!="SLAVE"))return false; return true;}
 std::string sha256()const {std::string v=id+"|"+registry_version;for(auto& [d,r]:device_role)v+="|"+d+":"+r;return digest(v);}
};
struct Station {
 std::string channel,device,role,model,model_sha,calibration_sha,detection_roi;double threshold=0;std::uint64_t revision=1;
};
struct Draft {std::string id,product,base_release,profile_sha;std::map<std::string,Station> stations;std::uint64_t revision=1;};
struct Release {std::string id,product,content_sha,profile_sha;std::map<std::string,Station> stations;};
enum class Result {OK,INVALID,NOT_FOUND,CONFLICT,STALE_BASE,INCOMPLETE,HASH_MISMATCH,DUPLICATE};
class Registry {
 MachineProfile machine_;
 std::map<std::string,std::pair<std::string,std::string>> model_bytes_;
 std::map<std::string,Draft> drafts_;
 std::map<std::string,Release> releases_;
 std::map<std::string,std::string> active_;
public:
 explicit Registry(MachineProfile machine):machine_(std::move(machine)){}
 Result model(std::string id,std::string bytes,std::string sha){
  if(id.empty()||bytes.empty()||digest(bytes)!=sha)return Result::HASH_MISMATCH;
  model_bytes_.insert_or_assign(std::move(id),std::make_pair(std::move(bytes),std::move(sha)));return Result::OK;
 }
 Result draft(Draft d){
  if(!machine_.valid()||d.id.empty()||d.product.empty()||d.profile_sha!=machine_.sha256()||drafts_.contains(d.id))return Result::INVALID;
  auto active=active_.find(d.product);auto old=active==active_.end()?std::string{}:active->second;
  if(d.base_release!=old)return Result::STALE_BASE;drafts_.emplace(d.id,std::move(d));return Result::OK;
 }
 Result patch(const std::string& id,const std::string& station,std::uint64_t expected,const Station& next,const std::string& expected_base){
  auto d=drafts_.find(id);if(d==drafts_.end())return Result::NOT_FOUND;
  auto a=active_.find(d->second.product);auto active=a==active_.end()?std::string{}:a->second;
  if(d->second.base_release!=expected_base||expected_base!=active)return Result::STALE_BASE;
  auto st=d->second.stations.find(station);if(st==d->second.stations.end()||next.channel!=station||next.detection_roi.empty())return Result::INVALID;
  if(st->second.revision!=expected)return Result::CONFLICT;
  st->second=next;st->second.revision=expected+1;++d->second.revision;return Result::OK;
 }
 Result freeze(const std::string& draft_id,const std::string& release_id){
  if(releases_.contains(release_id))return Result::DUPLICATE;
  auto it=drafts_.find(draft_id);if(it==drafts_.end())return Result::NOT_FOUND;
  auto& d=it->second;if(d.profile_sha!=machine_.sha256())return Result::HASH_MISMATCH;
  if(d.stations.size()!=11)return Result::INCOMPLETE;
  std::string manifest=d.product+"|"+d.profile_sha+"|"+std::to_string(d.revision);
  std::set<std::string> seen;
  for(auto& [id,s]:d.stations){
   if(id!=s.channel||s.detection_roi.empty()||s.model.empty()||s.calibration_sha.size()!=64||s.threshold<0||s.threshold>1)return Result::INCOMPLETE;
   auto m=model_bytes_.find(s.model);if(m==model_bytes_.end()||digest(m->second.first)!=s.model_sha||s.model_sha!=m->second.second)return Result::HASH_MISMATCH;
   auto device=machine_.device_role.find(s.device);
   if(device==machine_.device_role.end()||device->second!=s.role)return Result::INCOMPLETE;
   seen.insert(id);manifest+="|"+id+":"+s.device+":"+s.model_sha+":"+s.calibration_sha+":"+s.detection_roi+":"+std::to_string(s.threshold);
  }
  for(int i=1;i<=11;i++){char key[5];std::snprintf(key,sizeof(key),"CH%02d",i);if(!seen.contains(key))return Result::INCOMPLETE;}
  if(d.stations.at("CH08").device!=d.stations.at("CH09").device||d.stations.at("CH10").device!=d.stations.at("CH11").device)return Result::INCOMPLETE;
  releases_.emplace(release_id,Release{release_id,d.product,digest(manifest),d.profile_sha,d.stations});return Result::OK;
 }
 const Release* release(const std::string& id)const {auto i=releases_.find(id);return i==releases_.end()?nullptr:&i->second;}
 const Draft* get_draft(const std::string& id)const {auto i=drafts_.find(id);return i==drafts_.end()?nullptr:&i->second;}
 std::string active(const std::string& product)const {auto i=active_.find(product);return i==active_.end()?std::string{}:i->second;}
 // ONLY after both hosts and virtual-PLC postcommit verification.
 Result activate_after_verified_readback(const std::string& id){auto r=release(id);if(!r)return Result::NOT_FOUND;active_[r->product]=id;return Result::OK;}
};
} // namespace aoi::a7::night
