#pragma once
// SIM_ONLY prototype. All receipts originate from caller-injected virtual devices;
// real Core/PLC adapters and C01 wire formats deliberately NOT connected.
#include "../solutions/night_solution.hpp"
#include <array>
#include <filesystem>
#include <fstream>
#include <stdexcept>
namespace aoi::a7::night {
enum class Phase {STAGED,PREPARING_DEVICES,READBACK_DEVICE,READBACK_PLC,PREPARED_BOTH,COMMITTING,VERIFYING_ACTIVE,ACTIVE_SIMULATED,FAILED_SAFE,ROLLBACK_VERIFIED_HOLD};
struct Receipt {std::string transaction,host,release_hash,actual_hash;std::uint64_t epoch=0;bool device=false,plc=false,postcommit=false;};
class Deployment {
 Registry& registry_;std::string tx_,release_,hash_,previous_,path_;std::uint64_t epoch_;Phase phase_=Phase::STAGED;
 std::array<bool,2> devices_{false,false},plc_{false,false},active_{false,false};
 static int host(const std::string& h){return h=="MASTER"?0:h=="SLAVE"?1:-1;}
 bool persist(){std::ofstream out(path_+".tmp",std::ios::binary|std::ios::trunc);if(!out)return false;
 out<<tx_<<'\n'<<release_<<'\n'<<hash_<<'\n'<<previous_<<'\n'<<epoch_<<'\n'<<int(phase_)<<'\n';out.close();
 std::error_code ec;std::filesystem::rename(path_+".tmp",path_,ec);return !ec;}
 bool matches(const Receipt& r)const {return host(r.host)>=0&&r.transaction==tx_&&r.release_hash==hash_&&r.actual_hash==hash_&&r.epoch==epoch_;}
 void fail(){phase_=Phase::FAILED_SAFE;persist();}
public:
 Deployment(Registry& registry,std::string tx,std::string release,std::uint64_t epoch,std::string journal)
 :registry_(registry),tx_(std::move(tx)),release_(std::move(release)),path_(std::move(journal)),epoch_(epoch){
  const auto* r=registry_.release(release_);if(!r||tx_.empty()||!epoch_)throw std::invalid_argument("staged Release must exist");
  hash_=r->content_sha;previous_=registry_.active(r->product);if(!persist())throw std::runtime_error("journal write failed");
 }
 Phase phase()const{return phase_;}bool production_ready()const{return false;}const std::string& expected_hash()const{return hash_;}
 bool begin(){if(phase_!=Phase::STAGED)return false;phase_=Phase::PREPARING_DEVICES;return persist();}
 bool device(const Receipt& r){
  if((phase_!=Phase::PREPARING_DEVICES&&phase_!=Phase::READBACK_DEVICE)||!matches(r)||!r.device){fail();return false;}
  devices_[host(r.host)]=true;phase_=devices_[0]&&devices_[1]?Phase::READBACK_PLC:Phase::READBACK_DEVICE;return persist();
 }
 bool virtual_plc(const Receipt& r){
  if(phase_!=Phase::READBACK_PLC||!matches(r)||!r.plc){fail();return false;}
  plc_[host(r.host)]=true;if(plc_[0]&&plc_[1])phase_=Phase::PREPARED_BOTH;return persist();
 }
 bool commit(){if(phase_!=Phase::PREPARED_BOTH){fail();return false;}
  phase_=Phase::COMMITTING;if(!persist()){fail();return false;}
  phase_=Phase::VERIFYING_ACTIVE;return persist();
 }
 bool active(const Receipt& r){
  if(phase_!=Phase::VERIFYING_ACTIVE||!matches(r)||!r.postcommit){fail();return false;}
  active_[host(r.host)]=true;
  if(active_[0]&&active_[1]){if(registry_.activate_after_verified_readback(release_)!=Result::OK){fail();return false;}phase_=Phase::ACTIVE_SIMULATED;}
  return persist();
 }
 void disconnected(){fail();}void timeout(){fail();}
 bool rollback_verified(const Receipt& master,const Receipt& slave){
  if(phase_!=Phase::FAILED_SAFE||previous_.empty())return false;
  const auto* old=registry_.release(previous_);if(!old)return false;
  auto ok=[&](const Receipt& r,const char* role){return r.host==role&&r.transaction==tx_&&r.release_hash==old->content_sha&&r.actual_hash==old->content_sha&&r.device&&r.plc&&r.postcommit;};
  if(!ok(master,"MASTER")||!ok(slave,"SLAVE")){fail();return false;}
  phase_=Phase::ROLLBACK_VERIFIED_HOLD;return persist();
 }
 // Crash recovery never grants READY from a journal. Explicit fresh readbacks required.
 static bool requires_operator_hold_on_restart(const std::string&){return true;}
};
} // namespace aoi::a7::night
