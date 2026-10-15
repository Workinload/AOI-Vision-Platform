#pragma once
// SIM_ONLY, W035-W040 branch preview. Not a frozen C01 DTO.
#include <QJsonArray>
#include <QJsonObject>
#include <QHash>
#include <QQueue>
#include <QString>
#include <QStringList>
#include <QSet>
#include <QDateTime>
#include <QTimer>
#include <functional>
namespace aoi::runtime {
struct Event { QString id, workpiece, release, capture, channel, processing, quality; quint64 epoch=0; unsigned attempt=0; };
struct Piece { QString id, barcode, release; quint64 epoch=1; QHash<QString,Event> channel; QHash<QString,QString> seen; QString reason="not_complete"; QString outcome="HOLD"; };
class Engine {
 QHash<QString,Piece> pieces_; QString selected_; quint64 seq_=0; QQueue<Event> pending_; int rejects_=0; int capacity_=128;
public:
 bool begin(const QString &id,const QString &barcode,const QString &release, quint64 epoch) {
  if(id.isEmpty()||release.isEmpty()||!epoch||pieces_.contains(id)) {++rejects_;return false;}
  Piece p; p.id=id;p.barcode=barcode;p.release=release;p.epoch=epoch;pieces_.insert(id,p);selected_=id;++seq_;return true;
 }
 bool enqueue(const Event &e) { if(pending_.size()>=capacity_) {++rejects_;return false;} pending_.enqueue(e);return true; }
 int drain(int budget=8) { int count=0; while(!pending_.empty()&&count<budget) {apply(pending_.dequeue());++count;} return count; }
 bool apply(const Event &e) {
  auto it=pieces_.find(e.workpiece);
  if(it==pieces_.end()||e.id.isEmpty()||e.capture.isEmpty()||e.epoch!=it->epoch||e.release!=it->release||e.attempt==0|| !validChannel(e.channel)) {++rejects_;return false;}
  Piece &p=it.value(); const QString signature=e.workpiece+"|"+e.release+"|"+e.capture+"|"+e.channel+"|"+e.processing+"|"+e.quality+"|"+QString::number(e.epoch)+"|"+QString::number(e.attempt);
  if(p.seen.contains(e.id)){if(p.seen.value(e.id)==signature)return true;++rejects_;return false;}
  auto prior=p.channel.constFind(e.channel);
  if(prior!=p.channel.cend() && e.attempt<prior->attempt){++rejects_;return false;}
  if(prior!=p.channel.cend() && e.attempt==prior->attempt && prior->capture!=e.capture){++rejects_;return false;}
  p.seen.insert(e.id,signature);p.channel.insert(e.channel,e);recompute(p);++seq_;return true;
 }
 static bool validChannel(const QString &c) { if(c.size()!=4||!c.startsWith("CH"))return false; bool ok=false;int n=c.mid(2).toInt(&ok);return ok&&n>=1&&n<=11&&c==QString("CH%1").arg(n,2,10,QChar('0')); }
 static void recompute(Piece &p) {
  p.outcome="HOLD";p.reason="incomplete_channels";
  if(p.barcode.trimmed().isEmpty()){p.reason="barcode_unverified";return;}
  for(int n=1;n<=11;++n){const QString key=QString("CH%1").arg(n,2,10,QChar('0'));auto it=p.channel.constFind(key);if(it==p.channel.cend()){p.reason="missing_"+key;return;}
   if(it->processing!="SUCCEEDED"){p.reason="processing_"+it->processing;return;}
   if(n==1){if(it->quality!="UNDECIDED"){p.reason="positioning_quality_invalid";return;}}
   else if(it->quality!="OK"&&it->quality!="NG"){p.reason="quality_"+it->quality;return;}
  }
  for(int n=2;n<=11;++n){if(p.channel.value(QString("CH%1").arg(n,2,10,QChar('0'))).quality=="NG"){p.outcome="NG";p.reason="defect_ng";return;}}
  p.outcome="OK";p.reason="all_required_complete";
 }
 QJsonObject snapshot(const QString &role,const QString &requested={}) const {
  QJsonArray channels;const auto it=pieces_.constFind(requested.isEmpty()?selected_:requested);
  if(it!=pieces_.cend()) {for(int n=1;n<=11;++n){const QString channel=QString("CH%1").arg(n,2,10,QChar('0'));const auto found=it->channel.constFind(channel);QJsonObject c{{"channel_id",channel},{"workpiece_id",it->id},{"epoch",static_cast<qint64>(it->epoch)},{"release_id",it->release},{"source","SIMULATED"},{"host_id",role},{"physical_device_id",n==1?"SIM_LOCATE":n>=8?"SIM_LINE":"SIM_AREA"},{"parent_frame_id",found==it->channel.cend()?"":found->capture},{"asset_uri",""},{"asset_sha256",""},{"capture_id",found==it->channel.cend()?"":found->capture},{"attempt_no",found==it->channel.cend()?0:static_cast<int>(found->attempt)},{"processing_status",found==it->channel.cend()?"PENDING":found->processing},{"quality_status",found==it->channel.cend()?"UNDECIDED":found->quality}};channels.append(c);}}
  QJsonObject result{{"schema","aoi.preview.snapshot.v0"},{"mode","SIM_ONLY"},{"source","SIMULATED"},{"role",role},{"epoch",it==pieces_.cend()?0:static_cast<qint64>(it->epoch)},{"sequence",static_cast<qint64>(seq_)},{"workpiece_id",it==pieces_.cend()?"":it->id},{"release_id",it==pieces_.cend()?"":it->release},{"updated_utc",QDateTime::currentDateTimeUtc().toString(Qt::ISODateWithMs)},{"channels",channels},{"final",QJsonObject{{"outcome",role=="MASTER"&&it!=pieces_.cend()?it->outcome:"HOLD"},{"reason",it==pieces_.cend()?"no_workpiece":role=="MASTER"?it->reason:"slave_not_authoritative"}}},{"devices",QJsonArray{}},{"signals",QJsonArray{}},{"alarms",QJsonArray{}},{"rejected_events",rejects_},{"queue_depth",pending_.size()}}; return result;
 }
 int count() const {return pieces_.size();} int rejected()const{return rejects_;} int queueDepth()const{return pending_.size();}
};
}