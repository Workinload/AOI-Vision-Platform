// M00 vPLC: loopback-only UDP HEALTH endpoint. FINS is NOT implemented yet.
#include <QCoreApplication>
#include <QCommandLineParser>
#include <QHostAddress>
#include <QUdpSocket>
#include <QDebug>

int main(int argc,char** argv) {
    QCoreApplication app(argc, argv);
    app.setApplicationName("vplc");
    QCommandLineParser p;
    p.setApplicationDescription("M00 loopback-only mock UDP health endpoint, not FINS");
    p.addHelpOption();
    QCommandLineOption portOption("port", "UDP port for mock HEALTH ONLY", "port", "19600");
    QCommandLineOption bindOption("bind", "Must be 127.0.0.1", "host", "127.0.0.1");
    p.addOption(portOption);
    p.addOption(bindOption);
    p.process(app);
    bool ok = false;
    const int port = p.value(portOption).toInt(&ok);
    if (p.value(bindOption) != "127.0.0.1" || !ok || port < 1024 || port > 65535) {
        qCritical() << "Refuse unsafe bind/port. M00 UDP is allowed only on 127.0.0.1 high port";
        return 2;
    }
    QUdpSocket socket;
    if (!socket.bind(QHostAddress::LocalHost, static_cast<quint16>(port))) {
        qCritical().noquote() << "M00 UDP socket bind failed:" << socket.errorString();
        return 3;
    }
    QObject::connect(&socket,&QUdpSocket::readyRead,&app,[&]() {
        while (socket.hasPendingDatagrams()) {
            QByteArray bytes;
            bytes.resize(static_cast<qsizetype>(socket.pendingDatagramSize()));
            QHostAddress source; quint16 sourcePort = 0;
            socket.readDatagram(bytes.data(),bytes.size(),&source,&sourcePort);
            if (!source.isLoopback() || bytes != "M00_HEALTH_V1") {
                // Never parse/write arbitrary PLC memory; reject unknown/FINS frames.
                qWarning() << "M00 vPLC ignored unsupported request, bytes=" << bytes.size();
                continue;
            }
            const QByteArray reply = "M00_SIM_ONLY_FINS_NOT_IMPLEMENTED";
            socket.writeDatagram(reply, source, sourcePort);
        }
    });
    qInfo().noquote() << "AOI_VPLC_READY" << "127.0.0.1" << port << "HEALTH_ONLY";
    return app.exec();
}
