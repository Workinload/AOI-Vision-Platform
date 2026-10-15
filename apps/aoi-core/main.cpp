// M00: two role-specific cores using the same executable. Read-only Unix-domain IPC.
// This is not a production PLC, capture, or decision engine.
#include <QCoreApplication>
#include <QCommandLineParser>
#include <QDateTime>
#include <QDir>
#include <QElapsedTimer>
#include <QFileInfo>
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
    QObject::connect(&server, &QLocalServer::newConnection, &app, [&]() {
        while (server.hasPendingConnections()) {
            QLocalSocket* peer = server.nextPendingConnection();
            QObject::connect(peer, &QLocalSocket::disconnected, peer, &QLocalSocket::deleteLater);
            QObject::connect(peer, &QLocalSocket::readyRead, peer, [peer, health]() {
                if (peer->bytesAvailable() > 4096) { peer->disconnectFromServer(); return; }
                while (peer->canReadLine()) {
                    const QByteArray command = peer->readLine().trimmed();
                    if (command == "health" || command == "status") {
                        peer->write(health());
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
