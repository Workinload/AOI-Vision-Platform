#include "StatusBridge.hpp"
#include <QJsonDocument>
#include <QJsonObject>
#include <QDebug>
StatusBridge::StatusBridge(const QString& socketName,QObject* parent)
    : QObject(parent),m_socketName(socketName) {
    connect(&m_socket,&QLocalSocket::readyRead,this,&StatusBridge::onReady);
    connect(&m_socket,&QLocalSocket::disconnected,this,[this]() {
        m_connected=false; m_coreState="CORE 离线"; emit stateChanged();
    });
    connect(&m_socket,&QLocalSocket::errorOccurred,this,[this](QLocalSocket::LocalSocketError) {
        m_error=m_socket.errorString(); m_connected=false; emit stateChanged();
    });
    m_timer.setInterval(550);
    connect(&m_timer,&QTimer::timeout,this,[this]() {
        ensureConnected();
        if(m_socket.state()==QLocalSocket::ConnectedState) { m_socket.write("health\n");m_socket.flush(); }
    });
    m_timer.start();
    ensureConnected();
}
void StatusBridge::ensureConnected() {
    if(m_socket.state()==QLocalSocket::UnconnectedState) m_socket.connectToServer(m_socketName);
}
void StatusBridge::onReady() {
    while(m_socket.canReadLine()) {
        QJsonParseError error{};
        auto obj=QJsonDocument::fromJson(m_socket.readLine().trimmed(),&error).object();
        if(error.error!=QJsonParseError::NoError || obj.value("schema")!="aoi.core.health.v0") continue;
        m_connected=true;
        m_coreState=obj.value("state").toString("UNKNOWN");
        m_coreRole=obj.value("role").toString("UNKNOWN");
        m_mode=obj.value("mode").toString("UNKNOWN");
        m_uptime=QString::number(obj.value("uptime_ms").toInteger())+" ms";
        m_error.clear();
        emit stateChanged();
        qInfo().noquote() << "AOI_UI_IPC_CONNECTED" << m_coreRole << m_mode;
    }
}
