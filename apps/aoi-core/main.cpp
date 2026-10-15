// M00: two role-specific cores using the same executable. Read-only Unix-domain IPC.
// This is not a production PLC, capture, or decision engine.
#include <QCoreApplication>
#include <QCommandLineParser>
#include <QDateTime>
#include <QDir>
#include <QElapsedTimer>
#include <QFileInfo>
#include <QFile>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QLocalServer>
#include <QLocalSocket>
#include <QLockFile>
#include <QSqlDatabase>
#include <QSqlError>
#include <QSqlQuery>
#include <QTimer>
#include <QDebug>
#include <memory>
#include "../../libs/runtime/PreviewRuntime.hpp"
#include "../../libs/peer/PeerLink.hpp"

int main(int argc, char** argv) {
    QCoreApplication app(argc, argv);
    app.setApplicationName("aoi-core");
    QCommandLineParser parser;
    parser.setApplicationDescription("AOI M00 simulation-only core, no PLC/camera implementation");
    parser.addHelpOption();
    QCommandLineOption roleArg("role", "master or slave", "role");
    QCommandLineOption dataArg("data-dir", "Separate writable root for this host", "directory");
    parser.addOption(roleArg);
    parser.addOption(dataArg);
    QCommandLineOption peerPortArg("peer-port", "SIM loopback TCP peer port (0 disables)", "port", "0");
    parser.addOption(peerPortArg);
    QCommandLineOption fixtureArg("sim-fixture", "SIM_ONLY read-only startup JSON fixture", "path");
    parser.addOption(fixtureArg);
    QCommandLineOption peerDemoArg("peer-demo-event", "SIM_ONLY typed event ID to enqueue on this host", "id");
    parser.addOption(peerDemoArg);
    parser.process(app);
    const auto role = parser.value(roleArg).toLower();
    if ((role != "master" && role != "slave") || !parser.isSet(dataArg)) {
        qCritical() << "M00 refuses startup: require --role master|slave --data-dir DIRECTORY";
        return 2;
    }
    QDir dataDir(parser.value(dataArg));
    if (!dataDir.exists() && !QDir().mkpath(dataDir.absolutePath())) {
        qCritical() << "Cannot create data root"; return 3;
    }
    const QString root = dataDir.canonicalPath();
    if (root.isEmpty()) { qCritical() << "Invalid data root"; return 3; }
    QLockFile lock(root + "/core.lock");
    lock.setStaleLockTime(0);
    if (!lock.tryLock(50)) {
        qCritical() << "Data directory is already locked; each Core needs isolated storage";
        return 4;
    }
    const QString dbFile = root + "/sim_bootstrap.sqlite3";
    QSqlDatabase db = QSqlDatabase::addDatabase("QSQLITE", "m00_core");
    db.setDatabaseName(dbFile);
    if (!db.open()) {
        qCritical().noquote() << "SQLite plugin/open failure:" << db.lastError().text();
        return 5;
    }
    auto sql = [&](const QString &command) -> bool {
        QSqlQuery q(db);
        if (!q.exec(command)) {
            qCritical().noquote() << "SQLite schema error:" << q.lastError().text();
            return false;
        }
        return true;
    };
    if (!sql("PRAGMA journal_mode=WAL") || !sql("PRAGMA foreign_keys=ON") ||
        !sql("CREATE TABLE IF NOT EXISTS m00_meta (name TEXT PRIMARY KEY, value TEXT NOT NULL)") ||
        !sql("CREATE TABLE IF NOT EXISTS m00_events (seq INTEGER PRIMARY KEY AUTOINCREMENT, utc TEXT NOT NULL, kind TEXT NOT NULL)")) return 6;
    QSqlQuery existing(db);
    existing.prepare("SELECT value FROM m00_meta WHERE name='host_role'");
    if (!existing.exec()) return 6;
    if (existing.next() && existing.value(0).toString() != role.toUpper()) {
        qCritical() << "Role mismatch for existing database; do not share Master/Slave writable storage";
        return 7;
    }
    QSqlQuery upsert(db);
    upsert.prepare("INSERT OR REPLACE INTO m00_meta(name,value) VALUES ('host_role',?)");
    upsert.addBindValue(role.toUpper());
    if (!upsert.exec()) return 6;
    // Branch-only live simulated state; no automatic successful results are fabricated.
    aoi::runtime::Engine engine;
    engine.begin("SIM-"+role.toUpper()+"-WP-0001", "SIMULATED-TEST-BARCODE", "SIM-REL-0001", 1);
    if(parser.isSet(fixtureArg)) {
        QFile source(parser.value(fixtureArg));
        if(!source.open(QIODevice::ReadOnly) || source.size()>262144){qCritical()<<"Invalid fixture";return 11;}
        QJsonParseError err; auto doc=QJsonDocument::fromJson(source.readAll(),&err);
        if(err.error!=QJsonParseError::NoError||!doc.isObject()||doc.object().value("schema")!="aoi.sim.fixture.v0")return 11;
        const auto rootObj=doc.object();
        for(const auto &v:rootObj.value("pieces").toArray()) {
            const auto item=v.toObject();
            if(!engine.begin(item.value("workpiece_id").toString(),item.value("barcode").toString(),item.value("release_id").toString(),static_cast<quint64>(item.value("epoch").toInteger())))return 12;
        }
        for(const auto &v:rootObj.value("events").toArray()) {
            const auto o=v.toObject();
            aoi::runtime::Event e;
            e.id=o.value("event_id").toString();e.workpiece=o.value("workpiece_id").toString();
            e.release=o.value("release_id").toString();e.capture=o.value("capture_id").toString();
            e.channel=o.value("channel_id").toString();e.epoch=static_cast<quint64>(o.value("epoch").toInteger());
            e.attempt=static_cast<unsigned>(o.value("attempt_no").toInt());e.processing=o.value("processing_status").toString();e.quality=o.value("quality_status").toString();
            if(!engine.enqueue(e)) return 13;
        }
        engine.drain(128);
    }
    QTimer scheduler; scheduler.setInterval(15);
    QObject::connect(&scheduler,&QTimer::timeout,&app,[&engine]{ engine.drain(8); }); scheduler.start();
    bool portOk=false; int rawPort=parser.value(peerPortArg).toInt(&portOk);
    if(!portOk||rawPort<0||rawPort>65535){qCritical()<<"Invalid loopback peer port";return 9;}
    std::unique_ptr<aoi::peer::PeerLink> link;
    if(rawPort>0) {
        link=std::make_unique<aoi::peer::PeerLink>(db,role.toUpper(),static_cast<quint16>(rawPort),&app);
        if(role=="master"&&!link->listen()){qCritical()<<"Loopback peer TCP listen failed";return 10;}
        if(parser.isSet(peerDemoArg)) {
            const QString id=parser.value(peerDemoArg);
            if(!link->queue(id,{{"kind","RESULT"},{"epoch",1},{"workpiece_id","SIM-"+role.toUpper()+"-WP-0001"},{"quality","NG"},{"source","SIMULATED"}}))return 14;
        }
    }
    const QString socketName = root + "/core.ipc.sock";
    QLocalServer::removeServer(socketName); // process lock ensures no live Core owns this directory
    QLocalServer server;
    if (!server.listen(socketName)) {
        qCritical().noquote() << "IPC listen failed:" << server.errorString();
        return 8;
    }
    QElapsedTimer clock;
    clock.start();
    QJsonObject bootInfo{{"schema","aoi.core.health.v0"},
                         {"role",role.toUpper()},
                         {"mode","SIM_ONLY"},
                         {"state","SCAFFOLD_ONLY"},
                         {"uptime_ms",0},
                         {"db_file",dbFile},
                         {"ipc_path",socketName},
                         {"fins","NOT_IMPLEMENTED"},
                         {"camera","NOT_IMPLEMENTED"},
                         {"peerlink","NOT_IMPLEMENTED"}};
    auto health = [&]() {
        auto value = bootInfo;
        value["uptime_ms"] = static_cast<double>(clock.elapsed());
        return QJsonDocument(value).toJson(QJsonDocument::Compact) + '\n';
    };
    auto preview = [&](const QString &requested=QString()) { return QJsonDocument(engine.snapshot(role.toUpper(),requested)).toJson(QJsonDocument::Compact)+'\n'; };
    QObject::connect(&server, &QLocalServer::newConnection, &app, [&]() {
        while (server.hasPendingConnections()) {
            QLocalSocket* peer = server.nextPendingConnection();
            QObject::connect(peer, &QLocalSocket::disconnected, peer, &QLocalSocket::deleteLater);
            QObject::connect(peer, &QLocalSocket::readyRead, peer, [peer, health, preview]() {
                if (peer->bytesAvailable() > 4096) { peer->disconnectFromServer(); return; }
                while (peer->canReadLine()) {
                    const QByteArray command = peer->readLine().trimmed();
                    if (command == "health" || command == "status") {
                        peer->write(health());
                    } else if (command == "preview_snapshot") {
                        peer->write(preview());
                    } else if (command.startsWith("preview_snapshot ") && command.size()<150) {
                        peer->write(preview(QString::fromUtf8(command.mid(17))));
                    } else {
                        QJsonObject denied{{"schema","aoi.core.error.v0"},
                                           {"status","REJECTED"},
                                           {"reason","M00_READ_ONLY_IPC"}};
                        peer->write(QJsonDocument(denied).toJson(QJsonDocument::Compact) + '\n');
                    }
                }
                peer->flush();
            });
        }
    });
    qInfo().noquote() << "AOI_CORE_READY" << role.toUpper() << socketName << dbFile << "SIM_ONLY";
    const int ret = app.exec();
    server.close();
    db.close();
    return ret;
}