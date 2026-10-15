#include "aoi/statistics/report.hpp"
#include <sqlite3.h>
#include <memory>
#include <stdexcept>
#include <string_view>
namespace aoi::agent03 {
namespace {
using DBOwner=std::unique_ptr<sqlite3,decltype(&sqlite3_close)>;
DBOwner read_db(const std::filesystem::path& path){
  sqlite3* raw=nullptr;
  if(sqlite3_open_v2(path.string().c_str(),&raw,SQLITE_OPEN_READONLY,nullptr)!=SQLITE_OK){if(raw)sqlite3_close(raw);throw std::runtime_error("stats DB inaccessible");}
  return DBOwner(raw,&sqlite3_close);
}
class Query {
 public:Query(sqlite3* db,const char* sql){if(sqlite3_prepare_v2(db,sql,-1,&stmt,nullptr)!=SQLITE_OK)throw std::runtime_error("stats prepare failed");}
  ~Query(){sqlite3_finalize(stmt);}void str(int i,const std::string& s){if(sqlite3_bind_text(stmt,i,s.c_str(),-1,SQLITE_TRANSIENT)!=SQLITE_OK)throw std::runtime_error("stats bind failed");}
  void num(int i,int v){if(sqlite3_bind_int(stmt,i,v)!=SQLITE_OK)throw std::runtime_error("stats bind failed");}
  bool row(){auto rc=sqlite3_step(stmt);if(rc==SQLITE_ROW)return true;if(rc==SQLITE_DONE)return false;throw std::runtime_error("stats step failed");}
  int integer(int i){return sqlite3_column_int(stmt,i);}std::string text(int i){auto p=sqlite3_column_text(stmt,i);return p?reinterpret_cast<const char*>(p):"";}
 private:sqlite3_stmt* stmt=nullptr;
};
void valid_date(const std::string& date){
  if(date.size()!=10||date[4]!='-'||date[7]!='-')throw std::invalid_argument("ISO day format required");
  for(std::size_t i=0;i<date.size();++i)if(i!=4&&i!=7&&(date[i]<'0'||date[i]>'9'))throw std::invalid_argument("ISO date digits required");
}
}
Statistics::Statistics(const std::filesystem::path& db):db_(db){}
FirstPassYield Statistics::day_utc(const std::string& date)const{
  valid_date(date);const auto db=read_db(db_);
  Query q(db.get(),"SELECT COALESCE(SUM(first_valid),0),COALESCE(SUM(CASE WHEN first_valid=1 AND quality='OK' THEN 1 ELSE 0 END),0),COALESCE(SUM(CASE WHEN first_valid=1 AND quality='NG' THEN 1 ELSE 0 END),0),COALESCE(SUM(CASE WHEN first_valid=1 AND quality='HOLD' THEN 1 ELSE 0 END),0) FROM final_results WHERE substr(decided_utc,1,10)=?");
  q.str(1,date);q.row();FirstPassYield x;x.total=q.integer(0);x.ok=q.integer(1);x.ng=q.integer(2);x.hold=q.integer(3);x.yield=x.total?static_cast<double>(x.ok)/x.total:0.0;return x;
}
FirstPassYield Statistics::range_utc(const std::string& start,const std::string& end)const{
  valid_date(start);valid_date(end);if(end<=start)throw std::invalid_argument("invalid day range");const auto db=read_db(db_);
  Query q(db.get(),"SELECT COALESCE(SUM(first_valid),0),COALESCE(SUM(CASE WHEN first_valid=1 AND quality='OK' THEN 1 ELSE 0 END),0),COALESCE(SUM(CASE WHEN first_valid=1 AND quality='NG' THEN 1 ELSE 0 END),0),COALESCE(SUM(CASE WHEN first_valid=1 AND quality='HOLD' THEN 1 ELSE 0 END),0) FROM final_results WHERE substr(decided_utc,1,10)>=? AND substr(decided_utc,1,10)<?");
  q.str(1,start);q.str(2,end);q.row();FirstPassYield x;x.total=q.integer(0);x.ok=q.integer(1);x.ng=q.integer(2);x.hold=q.integer(3);x.yield=x.total?static_cast<double>(x.ok)/x.total:0.0;return x;
}
std::map<int,int> Statistics::defects(const std::string& start,const std::string& end)const{
  valid_date(start);valid_date(end);auto db=read_db(db_);
  Query q(db.get(),"SELECT defect_class,count(*) FROM defect_instances WHERE defect_class>=2 AND substr(created_utc,1,10)>=? AND substr(created_utc,1,10)<? GROUP BY defect_class ORDER BY defect_class");
  q.str(1,start);q.str(2,end);std::map<int,int> result;while(q.row())result.emplace(q.integer(0),q.integer(1));return result;
}
std::vector<PieceTrace> Statistics::history(const std::string& start,const std::string& end,int limit,int offset)const{
  valid_date(start);valid_date(end);if(limit<=0||limit>500||offset<0)throw std::invalid_argument("pagination out of bounds");
  auto db=read_db(db_);Query q(db.get(),"SELECT workpiece_id,quality,decided_utc FROM final_results WHERE substr(decided_utc,1,10)>=? AND substr(decided_utc,1,10)<? ORDER BY decided_utc DESC,workpiece_id DESC LIMIT ? OFFSET ?");
  q.str(1,start);q.str(2,end);q.num(3,limit);q.num(4,offset);
  std::vector<PieceTrace> r;while(q.row())r.push_back({q.text(0),q.text(1),q.text(2)});return r;
}
}
