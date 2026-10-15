#pragma once
// Localhost-only peer transport preview. Production C01 auth/wire format NOT FROZEN.
#include <QTcpServer>
#include <QTcpSocket>
#include <QHostAddress>
#include <QJsonDocument>
#include <QJsonObject>
#include <QSqlDatabase>
#include <QSqlQuery>
#include <QCryptographicHash>
#include <QTimer>
#include <QPointer>
#include <functional>
#include <memory>
namespace aoi::peer {
class PeerLink: public QObject {
 QTcpServer server_;QTcpSocket client_;QSqlDatabase db_;QString role_;QString remote_;quint16 port_;QPointer<QTcpSocket> inbound_;QByteArray pendingInput_;QTimer retry_;
 bool exec(const QString& sql){QSqlQuery q(db_);return q.exec(sql);}
 static QByteArray hash(const QByteArray& b){return QCryptographicHash::hash(b,QCryptographicHash::Sha256).toHex();}
 void send(QTcpSocket* socket,const QJsonObject& obj){if(socket&&socket->state()==QAbstractSocket::ConnectedState){socket->write(QJsonDocument(obj).toJson(QJsonDocument::Compact)+'\n');}}
 void parse(QTcpSocket* sock,QByteArray &buffer){buffer+=sock->readAll();if(buffer.size()>262144){sock->disconnectFromHost();buffer.clear();return;}while(buffer.contains('\n')){int k=buffer.indexOf('\n');QByteArray line=buffer.left(k);buffer.remove(0,k+1);if(line.size()>16384){sock->disconnectFromHost();return;}QJsonParseError err;auto doc=QJsonDocument::fromJson(line,&err);if(err.error==QJsonParseError::NoError&&doc.isObject())receive(sock,doc.object());}}
 void receive(QTcpSocket* sock,const QJsonObject& obj){const auto kind=obj.value("kind").toString();if(kind=="HELLO"){if(obj.value("schema")!="aoi.peer.sim.v0"||obj.value("role").toString()==role_||obj.value("role").toString().isEmpty()){sock->disconnectFromHost();return;}send(sock,{{"kind","HELLO_ACK"},{"schema","aoi.peer.sim.v0"},{"role",role_}});return;}
  if(kind=="PERSISTED_ACK"){const QString id=obj.value("message_id").toString(),dig=obj.value("hash").toString();QSqlQuery q(db_);q.prepare("UPDATE peer_outbox SET ack=1 WHERE id=? AND hash=?");q.addBindValue(id);q.addBindValue(dig);q.exec();return;}
  if(kind!="EVENT"||obj.value("schema")!="aoi.peer.sim.v0")return;
  const QString id=obj.value("message_id").toString(),dig=obj.value("hash").toString(),origin=obj.value("role").toString();const auto payload=obj.value("payload").toObject();const QByteArray raw=QJsonDocument(payload).toJson(QJsonDocument::Compact);
  if(id.isEmpty()||dig!=QString::fromLatin1(hash(raw))||origin==role_||payload.value("kind")=="FINAL"||payload.value("epoch").toInt()<=0){send(sock,{{"kind","NACK"},{"message_id",id},{"reason","INVALID_OR_NONAUTHORITATIVE"}});return;}
  if(!db_.transaction())return;QSqlQuery q(db_);q.prepare("SELECT hash FROM peer_inbox WHERE id=?");q.addBindValue(id);if(!q.exec()){db_.rollback();return;}if(q.next()){if(q.value(0).toString()!=dig){db_.rollback();send(sock,{{"kind","NACK"},{"message_id",id},{"reason","HASH_CONFLICT"}});return;}db_.commit();send(sock,{{"kind","PERSISTED_ACK"},{"message_id",id},{"hash",dig}});return;}
  QSqlQuery insert(db_);insert.prepare("INSERT INTO peer_inbox(id,hash,payload) VALUES (?,?,?)");insert.addBindValue(id);insert.addBindValue(dig);insert.addBindValue(QString::fromUtf8(raw));if(!insert.exec()||!db_.commit()){db_.rollback();return;}send(sock,{{"kind","PERSISTED_ACK"},{"message_id",id},{"hash",dig}});
 }
 void pump(){QTcpSocket *sendSocket=role_=="MASTER"?inbound_.data():&client_;if(!sendSocket||sendSocket->state()!=QAbstractSocket::ConnectedState)return;QSqlQuery q(db_);if(!q.exec("SELECT id,hash,payload FROM peer_outbox WHERE ack=0 ORDER BY rowid LIMIT 16"))return;while(q.next()){auto payload=QJsonDocument::fromJson(q.value(2).toByteArray()).object();send(sendSocket,{{"kind","EVENT"},{"schema","aoi.peer.sim.v0"},{"role",role_},{"message_id",q.value(0).toString()},{"hash",q.value(1).toString()},{"payload",payload}});}}
public:
 explicit PeerLink(QSqlDatabase db,QString role,quint16 port,QObject *parent=nullptr):QObject(parent),db_(db),role_(std::move(role)),port_(port) {
  exec("CREATE TABLE IF NOT EXISTS peer_inbox(id TEXT PRIMARY KEY,hash TEXT NOT NULL,payload TEXT NOT NULL)");exec("CREATE TABLE IF NOT EXISTS peer_outbox(id TEXT PRIMARY KEY,hash TEXT NOT NULL,payload TEXT NOT NULL,ack INTEGER NOT NULL DEFAULT 0)");
  connect(&retry_,&QTimer::timeout,this,[this]{if(role_=="SLAVE"&&client_.state()==QAbstractSocket::UnconnectedState)client_.connectToHost(QHostAddress::LocalHost,port_);pump();});retry_.start(500);
  connect(&client_,&QTcpSocket::connected,this,[this]{send(&client_,{{"kind","HELLO"},{"schema","aoi.peer.sim.v0"},{"role",role_}});});
  connect(&client_,&QTcpSocket::readyRead,this,[this]{parse(&client_,pendingInput_);});
  connect(&server_,&QTcpServer::newConnection,this,[this]{while(server_.hasPendingConnections()){QTcpSocket* s=server_.nextPendingConnection();if(inbound_){s->disconnectFromHost();s->deleteLater();continue;}inbound_=s;auto buf=std::make_shared<QByteArray>();connect(s,&QTcpSocket::readyRead,this,[this,s,buf]{parse(s,*buf);});connect(s,&QTcpSocket::disconnected,this,[this,s]{if(inbound_==s)inbound_.clear();s->deleteLater();});}});
 }
 bool listen(){return server_.listen(QHostAddress::LocalHost,port_);}bool queue(const QString &id,const QJsonObject &payload){if(id.isEmpty())return false;const auto raw=QJsonDocument(payload).toJson(QJsonDocument::Compact);QSqlQuery q(db_);q.prepare("INSERT OR IGNORE INTO peer_outbox(id,hash,payload) VALUES (?,?,?)");q.addBindValue(id);q.addBindValue(QString::fromLatin1(hash(raw)));q.addBindValue(QString::fromUtf8(raw));return q.exec()&&q.numRowsAffected()==1;}
 int pending()const {QSqlQuery q(db_);if(!q.exec("SELECT COUNT(*) FROM peer_outbox WHERE ack=0")||!q.next())return -1;return q.value(0).toInt();}
};
}