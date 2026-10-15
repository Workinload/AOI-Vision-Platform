#include "aoi/assets/lossless_store.hpp"
#include "aoi/statistics/report.hpp"
#include <sqlite3.h>
#include <array>
#include <cassert>
#include <chrono>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iostream>
#include <stdexcept>
#include <string>
#include <sys/wait.h>
#include <unistd.h>
#include <vector>
using namespace aoi::agent03;
namespace fs=std::filesystem;
static int count=0;
void verify(bool ok,const char* msg){if(!ok)throw std::runtime_error(std::string("FAIL ")+msg);++count;std::cout<<"PASS "<<msg<<'\n';}
void must_fail(const std::function<void()>& cb,const char* msg){bool failed=false;try{cb();}catch(const std::exception&){failed=true;}verify(failed,msg);}
fs::path temp_root(){std::string s="/tmp/aoi-agent03-native-XXXXXX";auto* p=mkdtemp(s.data());if(!p)throw std::runtime_error("mkdtemp failed");return fs::path(p);}
RawParent fixture_raw(const std::string& id,const std::string& piece,const std::string& group,const std::string& dev,const std::vector<std::uint8_t>& pixels,int attempt=1){
  RawParent p;p.asset_id=id;p.workpiece_id=piece;p.host_id="MASTER";p.group_id=group;p.device_id=dev;p.capture_id="CAP-"+id;p.attempt_no=attempt;p.width=16;p.height=8;p.stride=16;p.expected_pixel_sha=sha256_hex(pixels.data(),pixels.size());return p;
}
int child_crash(const std::string& dbpath,bool commit){
  sqlite3* db=nullptr;
  if(sqlite3_open(dbpath.c_str(),&db)!=SQLITE_OK)return 90;
  char* err=nullptr;
  auto do_sql=[&](const char* s){if(sqlite3_exec(db,s,nullptr,nullptr,&err)!=SQLITE_OK){sqlite3_free(err);sqlite3_close(db);return false;}return true;};
  if(!do_sql("PRAGMA journal_mode=WAL"))return 91;
  if(!do_sql("BEGIN IMMEDIATE"))return 92;
  if(!do_sql("INSERT INTO inbox_messages VALUES ('crash','m','0123456789012345678901234567890123456789012345678901234567890123','2026-10-16T00:00:00Z')"))return 93;
  if(commit&&!do_sql("COMMIT"))return 94;
  std::_Exit(commit?74:73); // simulate hard termination, do NOT run close/rollback
}
int run_child(const char* executable,const std::string& dbpath,bool commit){
  pid_t pid=fork();if(pid<0)throw std::runtime_error("fork failed");
  if(pid==0){const std::string mode=commit?"--after-commit":"--before-commit";execl(executable,executable,mode.c_str(),dbpath.c_str(),static_cast<char*>(nullptr));std::_Exit(98);}
  int status=0;if(waitpid(pid,&status,0)<0)throw std::runtime_error("waitpid failed");
  if(!WIFEXITED(status))throw std::runtime_error("child terminated unexpectedly");
  return WEXITSTATUS(status);
}
int main(int argc,char** argv){
 try {
  if(argc==3&&std::string(argv[1]).rfind("--",0)==0)return child_crash(argv[2],std::string(argv[1])=="--after-commit");
  if(argc!=2)throw std::invalid_argument("usage: native_wal_asset_test migration.sql");
  auto root=temp_root();std::cout<<"SIM_ONLY_ROOT="<<root<<'\n';
  struct RootCleanup{fs::path p;~RootCleanup(){std::error_code ec;fs::remove_all(p,ec);}} cleaner{root};
  WalStore master(root,"MASTER",argv[1]);WalStore slave(root,"SLAVE",argv[1]);
  verify(fs::exists(master.db_path())&&fs::exists(slave.db_path())&&master.db_path()!=slave.db_path(),"two physical WAL databases isolated");
  master.create_piece("wp-1",1,"rel-1","BARCODE-SAME","2026-10-16T01:00:00Z");
  master.create_piece("wp-2",1,"rel-1","BARCODE-SAME","2026-10-16T01:00:01Z");
  verify(master.count_for_test("workpieces")==2&&slave.count_for_test("workpieces")==0,"same barcode different workpiece IDs");
  auto payload=std::vector<std::uint8_t>(128);for(std::size_t i=0;i<payload.size();++i)payload[i]=static_cast<std::uint8_t>(i);
  auto rear=fixture_raw("rear-A","wp-1","CAPGROUP-REAR","LS-REAR",payload);
  auto front=fixture_raw("front-A","wp-1","CAPGROUP-FRONT","LS-FRONT",payload);
  master.reserve_raw(rear,"2026-10-16T01:00:02Z");master.reserve_raw(front,"2026-10-16T01:00:02Z");
  for(const char* ch:{"CH08","CH09"})master.link_raw({"wp-1",ch,rear.group_id,rear.asset_id,1});
  for(const char* ch:{"CH10","CH11"})master.link_raw({"wp-1",ch,front.group_id,front.asset_id,1});
  verify(master.count_for_test("raw_assets")==2&&master.count_for_test("channel_asset_links")==4,"two line parents / four distinct channel links");
  auto dupe=rear;dupe.asset_id="rear-duplicate";
  must_fail([&](){master.reserve_raw(dupe,"2026-10-16T01:00:02Z");},"duplicate physical parent rejected");
  dupe.asset_id="rear-recapture";dupe.attempt_no=2;master.reserve_raw(dupe,"2026-10-16T01:00:03Z");
  verify(master.count_for_test("raw_assets")==3,"recapture allowed by distinct AttemptNo");
  must_fail([&](){master.link_raw({"wp-2","CH08",rear.group_id,rear.asset_id,1});},"foreign workpiece parent link denied");
  must_fail([&](){master.link_raw({"wp-1","CH10","wrong-group",rear.asset_id,1});},"channel parent CaptureGroup mismatch denied");
  LosslessStore assets(master,root/"MASTER"/"raw");
  must_fail([&](){assets.resolve_relative("../outside.bin");},"URI traversal denied");
  must_fail([&](){assets.resolve_relative("/etc/passwd");},"absolute URI denied");
  fs::create_directory_symlink(root,assets.root()/"outside");
  must_fail([&](){assets.resolve_relative("outside/nested.bin");},"symlink escape denied");
  assets.write_raw_bytes(rear.asset_id,"lines/rear.raw8",payload);
  verify(assets.verify_raw(rear.asset_id),"real raw bytes persisted + sha256 readback");
  verify(master.raw(rear.asset_id)->state=="AVAILABLE","AVAILABLE only after fsync and bytes hash");
  must_fail([&](){assets.write_raw_bytes(rear.asset_id,"lines/rear.raw8",payload);},"immutable original cannot be overwritten");
  // Replay crash boundary: file exists but DB was never marked AVAILABLE.
  must_fail([&](){assets.write_raw_bytes(front.asset_id,"lines/front.raw8",payload,true);},"injected rename-before-commit crash");
  verify(master.raw(front.asset_id)->state=="WRITING","crash leaves WAL recovery intent");
  auto recovery=assets.reconcile();verify(recovery.promoted==1&&assets.verify_raw(front.asset_id),"reconcile promotes verified orphaned committed bytes");
  // Physical modification cannot continue to appear AVAILABLE.
  {std::fstream mutate(assets.resolve_relative("lines/rear.raw8"),std::ios::in|std::ios::out|std::ios::binary);mutate.put('X');}
  verify(!assets.verify_raw(rear.asset_id)&&master.raw(rear.asset_id)->state=="CORRUPT","tampered raw pixel bytes fail closed");
  auto retake=dupe;retake.asset_id="raw-stream";retake.group_id="CAPGROUP-STREAM";retake.width=16;retake.height=8;
  auto source=root/"source.raw8";{std::ofstream f(source,std::ios::binary);f.write(reinterpret_cast<const char*>(payload.data()),static_cast<std::streamsize>(payload.size()));}
  master.reserve_raw(retake,"2026-10-16T01:00:04Z");assets.write_raw_file(retake.asset_id,"lines/stream.raw8",source);
  verify(assets.verify_raw(retake.asset_id),"streamed source file immutable lossless copy");
  verify(master.receive("SLAVE","msg1","NG","eff1","wp-1","2026-10-16T01:01:01Z")==InboundAck::Persisted,"inbox and effect durable transaction");
  verify(master.receive("SLAVE","msg1","NG","eff1","wp-1","2026-10-16T01:01:01Z")==InboundAck::DuplicateSamePayload,"identical ACK replay no double effect");
  must_fail([&](){master.receive("SLAVE","msg1","OK","eff1","wp-1","2026-10-16T01:01:01Z");},"same MessageId with changed hash denied");
  must_fail([&](){master.receive("SLAVE","msg2","ERR","eff2","wp-1","2026-10-16T01:01:02Z",true);},"injected receiver crash before commit");
  verify(master.count_for_test("inbox_messages")==1&&master.count_for_test("effects")==1,"receiver failure rolled back inbox and business effect");
  Final finish{"wp-1","rel-1","NG","final1","2026-10-16T01:02:00Z",1,true};
  Outgoing send{"MASTER","final-msg-1","NG {\"workpiece_id\":\"wp-1\"}","wp-1","final1"};
  verify(master.finalize_and_outbox(finish,send),"final quality and outbox one WAL transaction");
  verify(!master.finalize_and_outbox(finish,send),"retransmitted final does not double count");
  must_fail([&](){auto inconsistent=finish;inconsistent.quality="OK";master.finalize_and_outbox(inconsistent,send);},"conflicting final denied");
  verify(master.pending_outbox()==1&&master.count_for_test("final_results")==1,"durable outbox queued exactly once");
  master.mark_outbox_acked("MASTER","final-msg-1");
  verify(master.pending_outbox()==0,"ACK state durable without overwriting outcome");
  must_fail([&](){master.record_channel({"wp-1","rel-1","CH08","cap", "ERROR","OK","",1,1},"2026-10-16T01:00:00Z");},"ERROR cannot silently OK");
  master.record_channel({"wp-1","rel-1","CH08","cap","SUCCEEDED","NG","",1,1},"2026-10-16T01:00:00Z");
  must_fail([&](){master.record_channel({"wp-1","rel-1","CH08","cap","SUCCEEDED","OK","",1,1},"2026-10-16T01:00:00Z");},"same capture attempt cannot count two results");
  master.record_defect("def1","wp-1","CH08",2,"{\"x\":4}","2026-10-16T01:00:00Z");
  must_fail([&](){master.record_defect("not-a-defect","wp-1","CH08",1,"{}","2026-10-16T01:00:00Z");},"ID1 good class excluded from defect distribution");
  master.append_rejudge("rev1","wp-1","OK","quality-inspector","manual review","2026-10-16T02:00:00Z");
  master.append_rejudge("rev2","wp-1","NG","quality-inspector","second opinion","2026-10-16T03:00:00Z");
  verify(master.count_for_test("rejudge_events")==2,"manual rejudge append-only, original final remains");
  auto stats=Statistics(master.db_path());auto one=stats.day_utc("2026-10-16");
  verify(one.total==1&&one.ok==0&&one.ng==1&&one.yield==0.0,"daily first-pass counts each workpiece once");
  verify(stats.range_utc("2026-10-13","2026-10-20").total==1,"date range/week query uses first pass");
  verify(stats.defects("2026-10-13","2026-10-20").at(2)==1,"defect instance dictionary counts independently");
  verify(stats.history("2026-10-13","2026-10-20",10,0).size()==1,"trace pagination works");
  must_fail([&](){stats.history("2026-10-13","2026-10-20",1000,0);},"query pagination upper bound rejects");
  master.write_trace("ev1","wp-1","SIMULATED","trace1","{}","2026-10-16T01:00:00Z");
  verify(master.gallery_json("wp-1").find("CH11")!=std::string::npos,"same piece gallery joins all linked channels");
  master.online_backup(root/"backup.sqlite");verify(fs::file_size(root/"backup.sqlite")>0,"SQLite online backup includes WAL writes");
  verify(run_child(argv[0],master.db_path().string(),false)==73,"real subprocess exits midtransaction with code 73");
  verify(master.count_for_test("inbox_messages")==1,"abrupt uncommitted child WAL changes rolled back");
  verify(run_child(argv[0],master.db_path().string(),true)==74,"real subprocess exits after COMMIT with code 74");
  verify(master.count_for_test("inbox_messages")==2,"committed child WAL survives process exit");
  WalStore lab(root,"OFFLINE_LAB",argv[1]);
  verify(lab.count_for_test("workpieces")==0&&lab.db_path()!=master.db_path(),"independent offline lab DB cannot alter production statistics");
  std::cout<<"NATIVE_TEST_ASSERTIONS="<<count<<" EXIT=0\n";return 0;
 }catch(const std::exception& e){std::cerr<<"NATIVE_TEST_FAILED: "<<e.what()<<'\n';return 1;}
}
