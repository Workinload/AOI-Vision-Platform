#include "aoi/data/wal_store.hpp"
#include <openssl/evp.h>
#include <sqlite3.h>
#include <array>
#include <fstream>
#include <iomanip>
#include <sstream>
#include <stdexcept>
#include <string_view>
#include <utility>
namespace aoi::agent03 {
namespace {
void sql_check(int code, sqlite3* db, const std::string& context) {
  if (code != SQLITE_OK && code != SQLITE_DONE && code != SQLITE_ROW)
    throw std::runtime_error(context + ": " + sqlite3_errmsg(db));
}
void exec(sqlite3* db, const std::string& query) {
  char* err = nullptr;
  int rc = sqlite3_exec(db, query.c_str(), nullptr, nullptr, &err);
  if (rc != SQLITE_OK) {
    std::string msg = err ? err : sqlite3_errmsg(db);
    sqlite3_free(err);
    throw std::runtime_error("SQL: " + msg);
  }
}
class Stmt {
 public:
  Stmt(sqlite3* db,const char* sql):db_(db) {sql_check(sqlite3_prepare_v2(db,sql,-1,&stmt_,nullptr),db,"prepare");}
  ~Stmt(){sqlite3_finalize(stmt_);}
  Stmt(const Stmt&)=delete;
  void text(int n,const std::string& v) { sql_check(sqlite3_bind_text(stmt_,n,v.c_str(),static_cast<int>(v.size()),SQLITE_TRANSIENT),db_,"bind text"); }
  void num(int n,std::int64_t v) {sql_check(sqlite3_bind_int64(stmt_,n,v),db_,"bind int");}
  int step() {int c=sqlite3_step(stmt_);sql_check(c,db_,"step");return c;}
  std::string col(int n) const {const auto* v=sqlite3_column_text(stmt_,n);return v?reinterpret_cast<const char*>(v):"";}
  std::int64_t integer(int n) const {return sqlite3_column_int64(stmt_,n);}
  bool is_null(int n) const {return sqlite3_column_type(stmt_,n)==SQLITE_NULL;}
  int changes() const {return sqlite3_changes(db_);}
 private:sqlite3* db_;sqlite3_stmt* stmt_ = nullptr;
};
class Tx {
 public: explicit Tx(sqlite3* d):db(d){exec(db,"BEGIN IMMEDIATE");}
 ~Tx(){if(!committed)sqlite3_exec(db,"ROLLBACK",nullptr,nullptr,nullptr);}
 void commit(){exec(db,"COMMIT");committed=true;}
 private:sqlite3* db;bool committed=false;
};
std::string read_text(const fs::path& path) {
  std::ifstream f(path,std::ios::binary);if(!f)throw std::runtime_error("cannot read migration");
  std::ostringstream out;out<<f.rdbuf();return out.str();
}
void check_arg(bool condition,const char* msg) {if(!condition)throw std::invalid_argument(msg);}
void valid_hash(const std::string& h) {
  check_arg(h.size()==64,"SHA256 length invalid");
  for(char c:h)check_arg((c>='0'&&c<='9')||(c>='a'&&c<='f'),"SHA256 hex invalid");
}
}
std::string sha256_hex(const void* bytes, std::size_t len) {
  unsigned char hash[EVP_MAX_MD_SIZE];unsigned int n=0;
  if(EVP_Digest(bytes,len,hash,&n,EVP_sha256(),nullptr)!=1||n!=32)throw std::runtime_error("OpenSSL SHA256 failed");
  std::ostringstream out;out<<std::hex<<std::setfill('0');
  for(unsigned int i=0;i<n;++i)out<<std::setw(2)<<static_cast<int>(hash[i]);
  return out.str();
}
std::string sha256_hex(const std::string& content) {return sha256_hex(content.data(),content.size());}
WalStore::WalStore(const fs::path& root, const std::string& role,const fs::path& migration):host_(role) {
  if(role!="MASTER"&&role!="SLAVE"&&role!="OFFLINE_LAB")throw std::invalid_argument("invalid SIM_ONLY role");
  db_path_=root/role/(role=="OFFLINE_LAB"?"lab.sqlite":"core.sqlite");
  fs::create_directories(db_path_.parent_path());
  if(sqlite3_open_v2(db_path_.string().c_str(),&db_,SQLITE_OPEN_READWRITE|SQLITE_OPEN_CREATE|SQLITE_OPEN_FULLMUTEX,nullptr)!=SQLITE_OK){
    std::string e=db_?sqlite3_errmsg(db_):"open failed";if(db_)sqlite3_close(db_);db_=nullptr;throw std::runtime_error(e);
  }
  try {
    exec(db_,"PRAGMA foreign_keys=ON");
    exec(db_,"PRAGMA busy_timeout=5000");
    {Stmt stmt(db_,"PRAGMA journal_mode=WAL");if(stmt.step()!=SQLITE_ROW||stmt.col(0)!="wal")throw std::runtime_error("WAL unavailable");}
    exec(db_,"PRAGMA synchronous=FULL");
    const auto migration_sql=read_text(migration);
    const auto migration_hash=sha256_hex(migration_sql);
    Tx tx(db_);
    exec(db_,migration_sql);
    Stmt prior(db_,"SELECT source_sha256 FROM schema_migrations WHERE version=2");
    if(prior.step()==SQLITE_ROW)check_arg(prior.col(0)==migration_hash,"migration hash mismatch");
    else {
      Stmt add(db_,"INSERT INTO schema_migrations VALUES (2,?,datetime('now'))");
      add.text(1,migration_hash);add.step();
    }
    tx.commit();
  }catch(...){sqlite3_close(db_);db_=nullptr;throw;}
}
WalStore::~WalStore(){if(db_)sqlite3_close(db_);}
void WalStore::create_piece(const std::string& id,int epoch,const std::string& release,const std::string& barcode,const std::string& utc){
  check_arg(!id.empty()&&!release.empty()&&epoch>=1,"invalid piece key");
  Stmt st(db_,"INSERT INTO workpieces VALUES (?,?,?,?,?)");
  st.text(1,id);st.num(2,epoch);st.text(3,release);st.text(4,barcode);st.text(5,utc);st.step();
}
void WalStore::reserve_raw(const RawParent& p,const std::string& utc){
  check_arg(p.host_id==host_&&p.attempt_no>=1&&p.width>0&&p.height>0&&p.stride>=p.width,"invalid capture/host/shape");
  valid_hash(p.expected_pixel_sha);
  Stmt st(db_,"INSERT INTO raw_assets(asset_id,workpiece_id,host_id,capture_group_id,physical_device_id,capture_id,attempt_no,pixel_format,width_px,height_px,stride_bytes,expected_pixel_sha256,save_state,created_utc) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,'PENDING',?)");
  st.text(1,p.asset_id);st.text(2,p.workpiece_id);st.text(3,p.host_id);st.text(4,p.group_id);st.text(5,p.device_id);st.text(6,p.capture_id);st.num(7,p.attempt_no);st.text(8,p.pixel_format);st.num(9,p.width);st.num(10,p.height);st.num(11,p.stride);st.text(12,p.expected_pixel_sha);st.text(13,utc);st.step();
}
void WalStore::raw_writing(const std::string& id,const std::string& uri){
  check_arg(!uri.empty(),"empty asset URI");
  Stmt st(db_,"UPDATE raw_assets SET save_state='WRITING',storage_uri=? WHERE asset_id=? AND save_state='PENDING'");
  st.text(1,uri);st.text(2,id);st.step();check_arg(st.changes()==1,"raw WRITING transition denied");
}
void WalStore::raw_available(const std::string& id,const std::string& file_sha,const std::string& decoded_sha,std::int64_t bytes){
  valid_hash(file_sha);valid_hash(decoded_sha);check_arg(bytes>0,"no bytes");
  Stmt st(db_,"UPDATE raw_assets SET save_state='AVAILABLE',encoded_file_sha256=?,decoded_pixel_sha256=?,file_bytes=? WHERE asset_id=? AND save_state='WRITING' AND expected_pixel_sha256=?");
  // Check decoded pixel hash equals reservation explicitly. Never trust caller claimed success.
  st.text(1,file_sha);st.text(2,decoded_sha);st.num(3,bytes);st.text(4,id);st.text(5,decoded_sha);
  st.step();check_arg(st.changes()==1,"AVAILABLE requires WRITING and original pixel hash match");
}
void WalStore::raw_fail(const std::string& id,const std::string& state){
  check_arg(state=="CORRUPT"||state=="FAILED","failure state must fail closed");
  Stmt st(db_,"UPDATE raw_assets SET save_state=? WHERE asset_id=? AND save_state NOT IN ('CORRUPT','FAILED')");
  st.text(1,state);st.text(2,id);st.step();
}
std::optional<RawParent> WalStore::raw(const std::string& id) const {
  Stmt st(db_,"SELECT asset_id,workpiece_id,host_id,capture_group_id,physical_device_id,capture_id,attempt_no,pixel_format,width_px,height_px,stride_bytes,expected_pixel_sha256,COALESCE(storage_uri,''),COALESCE(encoded_file_sha256,''),COALESCE(decoded_pixel_sha256,''),COALESCE(codec,'RAW8'),save_state,COALESCE(file_bytes,0) FROM raw_assets WHERE asset_id=?");
  st.text(1,id);if(st.step()!=SQLITE_ROW)return std::nullopt;
  RawParent p;p.asset_id=st.col(0);p.workpiece_id=st.col(1);p.host_id=st.col(2);p.group_id=st.col(3);p.device_id=st.col(4);p.capture_id=st.col(5);p.attempt_no=static_cast<int>(st.integer(6));p.pixel_format=st.col(7);p.width=static_cast<int>(st.integer(8));p.height=static_cast<int>(st.integer(9));p.stride=static_cast<int>(st.integer(10));p.expected_pixel_sha=st.col(11);p.uri=st.col(12);p.file_sha=st.col(13);p.pixel_sha=st.col(14);p.codec=st.col(15);p.state=st.col(16);p.file_bytes=st.integer(17);return p;
}
std::vector<RawParent> WalStore::unfinished_raw() const{
  std::vector<std::string> ids;
  {Stmt st(db_,"SELECT asset_id FROM raw_assets WHERE save_state IN ('PENDING','WRITING') ORDER BY asset_id");while(st.step()==SQLITE_ROW)ids.push_back(st.col(0));}
  std::vector<RawParent> result;for(const auto& id:ids)result.push_back(*raw(id));return result;
}
void WalStore::link_raw(const ChannelLink& link){
  check_arg(link.channel_id.size()==4&&link.channel_id.substr(0,2)=="CH","invalid channel");
  Stmt check(db_,"SELECT workpiece_id FROM raw_assets WHERE asset_id=?");check.text(1,link.asset_id);
  if(check.step()!=SQLITE_ROW||check.col(0)!=link.workpiece_id)throw std::invalid_argument("asset cross-workpiece link");
  Stmt st(db_,"INSERT INTO channel_asset_links VALUES (?,?,?,?,?)");
  st.text(1,link.workpiece_id);st.text(2,link.channel_id);st.text(3,link.group_id);st.num(4,link.attempt_no);st.text(5,link.asset_id);st.step();
}
void WalStore::record_channel(const ChannelResult& p,const std::string& utc){
  if(p.processing!="SUCCEEDED"&&p.quality=="OK")throw std::invalid_argument("fault cannot be OK");
  Stmt st(db_,"INSERT INTO channel_results VALUES (?,?,?,?,?,?,?,?,?,?)");
  st.text(1,p.workpiece_id);st.num(2,p.epoch);st.text(3,p.release_id);st.text(4,p.channel_id);st.text(5,p.capture_id);st.num(6,p.attempt);st.text(7,p.processing);st.text(8,p.quality);st.text(9,p.model_sha);st.text(10,utc);st.step();
}
InboundAck WalStore::receive(const std::string& sender,const std::string& id,const std::string& payload,const std::string& effect,const std::string& piece,const std::string& utc,bool inject){
  const auto digest=sha256_hex(payload);Tx tx(db_);
  {Stmt st(db_,"SELECT payload_sha256 FROM inbox_messages WHERE sender_id=? AND message_id=?");st.text(1,sender);st.text(2,id);
    if(st.step()==SQLITE_ROW){if(st.col(0)!=digest)throw std::runtime_error("MessageId reused with different hash");tx.commit();return InboundAck::DuplicateSamePayload;}}
  {Stmt st(db_,"INSERT INTO inbox_messages VALUES (?,?,?,?)");st.text(1,sender);st.text(2,id);st.text(3,digest);st.text(4,utc);st.step();}
  {Stmt st(db_,"INSERT INTO effects VALUES (?,?,?,?)");st.text(1,effect);st.text(2,piece);st.text(3,digest);st.text(4,utc);st.step();}
  if(inject)throw std::runtime_error("SIMULATED_CRASH_BEFORE_COMMIT");
  tx.commit();return InboundAck::Persisted;
}
bool WalStore::finalize_and_outbox(const Final& f,const Outgoing& m){
  check_arg(f.quality=="OK"||f.quality=="NG"||f.quality=="HOLD","invalid final quality");
  check_arg(f.event_id==m.event_id&&f.workpiece_id==m.workpiece_id,"final/outbox identity mismatch");
  const auto digest=sha256_hex(m.payload);Tx tx(db_);
  {Stmt st(db_,"SELECT event_id,quality FROM final_results WHERE workpiece_id=?");st.text(1,f.workpiece_id);
    if(st.step()==SQLITE_ROW){if(st.col(0)!=f.event_id||st.col(1)!=f.quality)throw std::runtime_error("conflicting final replay");
      Stmt out(db_,"SELECT payload_sha256 FROM outbox_messages WHERE sender_id=? AND message_id=?");out.text(1,m.sender);out.text(2,m.message_id);
      if(out.step()!=SQLITE_ROW||out.col(0)!=digest)throw std::runtime_error("outbox replay mismatch");
      tx.commit();return false;}}
  {Stmt st(db_,"INSERT INTO final_results VALUES (?,?,?,?,?,?,?)");st.text(1,f.workpiece_id);st.num(2,f.epoch);st.text(3,f.release_id);st.text(4,f.quality);st.num(5,f.first_valid?1:0);st.text(6,f.decided_utc);st.text(7,f.event_id);st.step();}
  {Stmt st(db_,"INSERT INTO outbox_messages VALUES (?,?,?,?, 'PENDING', ?)");st.text(1,m.sender);st.text(2,m.message_id);st.text(3,digest);st.text(4,m.payload);st.text(5,f.decided_utc);st.step();}
  tx.commit();return true;
}
void WalStore::mark_outbox_acked(const std::string& sender,const std::string& id){
  Stmt st(db_,"UPDATE outbox_messages SET state='ACKED' WHERE sender_id=? AND message_id=? AND state IN ('PENDING','SENT','ACKED')");st.text(1,sender);st.text(2,id);st.step();if(st.changes()!=1)throw std::runtime_error("unknown outbox ACK");
}
int WalStore::pending_outbox() const{Stmt st(db_,"SELECT count(*) FROM outbox_messages WHERE state='PENDING'");st.step();return static_cast<int>(st.integer(0));}
void WalStore::record_defect(const std::string& id,const std::string& piece,const std::string& channel,int cls,const std::string& geometry,const std::string& utc){
  check_arg(cls>=2,"class 0=empty, 1=good cannot be defect");Stmt st(db_,"INSERT INTO defect_instances VALUES (?,?,?,?,?,?)");st.text(1,id);st.text(2,piece);st.text(3,channel);st.num(4,cls);st.text(5,geometry);st.text(6,utc);st.step();
}
void WalStore::append_rejudge(const std::string& id,const std::string& piece,const std::string& q,const std::string& actor,const std::string& reason,const std::string& utc){
  Stmt st(db_,"INSERT INTO rejudge_events VALUES (?,?,?,?,?,?)");st.text(1,id);st.text(2,piece);st.text(3,q);st.text(4,actor);st.text(5,reason);st.text(6,utc);st.step();
}
void WalStore::write_trace(const std::string& id,const std::string& piece,const std::string& source,const std::string& trace,const std::string& json,const std::string& utc){
  Stmt st(db_,"INSERT INTO trace_events VALUES (?,?,?,?,?,?)");st.text(1,id);st.text(2,piece);st.text(3,source);st.text(4,trace);st.text(5,json);st.text(6,utc);st.step();
}
void WalStore::add_result_image(const std::string& image_id,const std::string& piece,const std::string& channel,const std::string& parent,const std::string& uri,const std::string& file_hash,const std::string& pixel_hash,const std::string& overlay,const std::string& utc){
  valid_hash(file_hash);valid_hash(pixel_hash);Stmt st(db_,"INSERT INTO result_images VALUES (?,?,?,?,?,?,?,?,?)");st.text(1,image_id);st.text(2,piece);st.text(3,channel);st.text(4,parent);st.text(5,uri);st.text(6,file_hash);st.text(7,pixel_hash);st.text(8,overlay);st.text(9,utc);st.step();
}
std::string WalStore::gallery_json(const std::string& piece)const{
  Stmt st(db_,"SELECT a.channel_id,a.raw_asset_id,r.storage_uri,r.save_state,COALESCE(r.encoded_file_sha256,'') FROM channel_asset_links a JOIN raw_assets r ON r.asset_id=a.raw_asset_id WHERE a.workpiece_id=? ORDER BY a.channel_id,a.attempt_no");st.text(1,piece);
  std::ostringstream out;out<<"[";bool first=true;
  while(st.step()==SQLITE_ROW){if(!first)out<<",";first=false;
    out<<"{\"channel\":\""<<st.col(0)<<"\",\"raw_asset\":\""<<st.col(1)<<"\",\"uri\":\""<<st.col(2)<<"\",\"state\":\""<<st.col(3)<<"\",\"sha256\":\""<<st.col(4)<<"\"}";}
  return out.str()+"]";
}
void WalStore::write_lab_run(const std::string& id,const std::string& mode,const std::string& piece,const std::string& recipe,const std::string& result,const std::string& utc){
  check_arg(host_=="OFFLINE_LAB","production WAL must reject offline lab effects");
  valid_hash(recipe);
  Stmt st(db_,"INSERT INTO lab_runs(run_id,run_mode,source_piece,recipe_sha256,result_json,created_utc) VALUES (?,?,?,?,?,?)");
  st.text(1,id);st.text(2,mode);st.text(3,piece);st.text(4,recipe);st.text(5,result);st.text(6,utc);st.step();
}
void WalStore::online_backup(const fs::path& dest)const{
  fs::create_directories(dest.parent_path());sqlite3* target=nullptr;
  if(sqlite3_open_v2(dest.string().c_str(),&target,SQLITE_OPEN_READWRITE|SQLITE_OPEN_CREATE,nullptr)!=SQLITE_OK){if(target)sqlite3_close(target);throw std::runtime_error("backup open failed");}
  sqlite3_backup* b=sqlite3_backup_init(target,"main",db_,"main");
  if(!b){sqlite3_close(target);throw std::runtime_error("backup init failed");}
  const int rc=sqlite3_backup_step(b,-1);sqlite3_backup_finish(b);
  const int closer=sqlite3_close(target);
  if(rc!=SQLITE_DONE||closer!=SQLITE_OK)throw std::runtime_error("backup incomplete");
}
std::int64_t WalStore::count_for_test(const std::string& table)const{
  constexpr std::array<std::string_view,11> allowed={"workpieces","raw_assets","channel_asset_links","channel_results","final_results","inbox_messages","outbox_messages","effects","defect_instances","rejudge_events","trace_events"};
  bool valid=false;for(auto n:allowed)valid|=(table==n);check_arg(valid,"test table name forbidden");
  Stmt st(db_,("SELECT count(*) FROM "+table).c_str());st.step();return st.integer(0);
}
} // namespace aoi::agent03
