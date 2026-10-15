#pragma once
#include <QObject>
#include <QLocalSocket>
#include <QTimer>
#include <QString>
class StatusBridge : public QObject {
    Q_OBJECT
    Q_PROPERTY(bool connected READ connected NOTIFY stateChanged)
    Q_PROPERTY(QString coreState READ coreState NOTIFY stateChanged)
    Q_PROPERTY(QString coreRole READ coreRole NOTIFY stateChanged)
    Q_PROPERTY(QString mode READ mode NOTIFY stateChanged)
    Q_PROPERTY(QString uptime READ uptime NOTIFY stateChanged)
    Q_PROPERTY(QString ipcError READ ipcError NOTIFY stateChanged)
public:
    explicit StatusBridge(const QString& socketName,QObject* parent=nullptr);
    bool connected() const noexcept { return m_connected; }
    QString coreState() const { return m_coreState; }
    QString coreRole() const { return m_coreRole; }
    QString mode() const { return m_mode; }
    QString uptime() const { return m_uptime; }
    QString ipcError() const { return m_error; }
signals:
    void stateChanged();
private:
    void ensureConnected();
    void onReady();
    QLocalSocket m_socket;
    QTimer m_timer;
    QString m_socketName;
    QString m_coreState{"CORE 离线"};
    QString m_coreRole{"UNKNOWN"};
    QString m_mode{"SIM_ONLY"};
    QString m_uptime{"0 ms"};
    QString m_error{};
    bool m_connected{false};
};
