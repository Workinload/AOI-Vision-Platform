// M00 simulated QtQuick UI: displays actual Core IPC health, never invented PLC state.
#include "StatusBridge.hpp"
#include <QGuiApplication>
#include <QCommandLineParser>
#include <QQmlApplicationEngine>
#include <QQmlContext>
#include <QQuickStyle>
#include <QDebug>
#include <QTimer>
#include <QQuickWindow>
#include <QImage>
#include <QFont>
#include <QFontDatabase>
int main(int argc,char**argv) {
    QGuiApplication app(argc,argv);
    app.setApplicationName("aoi-ui");
    const QString fontPath=qEnvironmentVariable("AOI_M00_TEST_FONT_FILE");
    if (!fontPath.isEmpty()) {
        const auto id=QFontDatabase::addApplicationFont(fontPath);
        qInfo()<<"AOI_M00_FONT_TEST_IMPORT_ID"<<id;
        if (id>=0) qInfo()<<"AOI_M00_FONT_FAMILY"<<QFontDatabase::applicationFontFamilies(id);
    }
    app.setFont(QFont("Noto Sans CJK SC", 11));
    QCommandLineParser p;
    p.addHelpOption();
    QCommandLineOption roleArg("role", "master or slave", "role");
    QCommandLineOption ipcArg("ipc", "Core IPC absolute socket path", "path");
    QCommandLineOption screenshotArg("screenshot", "Save an offscreen Qt Quick screenshot (M00 smoke)", "path");
    p.addOption(roleArg);p.addOption(ipcArg);p.addOption(screenshotArg);p.process(app);
    QString role=p.value(roleArg).toLower();
    if((role!="master"&&role!="slave") || !p.isSet(ipcArg)) {
        qCritical() << "Require --role master|slave --ipc /absolute/path";return 2;
    }
    StatusBridge bridge(p.value(ipcArg));
    QQuickStyle::setStyle("Basic");
    qInfo()<<"AOI_M00_FONT_AVAILABLE"<<QFontDatabase::families().contains("Noto Sans CJK SC");
    QQmlApplicationEngine engine;
    engine.rootContext()->setContextProperty("bridge", &bridge);
    engine.rootContext()->setContextProperty("uiHostRole", role.toUpper());
    QObject::connect(&engine,&QQmlApplicationEngine::objectCreationFailed,&app,[]() {QCoreApplication::exit(3);},Qt::QueuedConnection);
    engine.loadFromModule("AOI.UI","Main");
    if(engine.rootObjects().isEmpty()) {qCritical()<<"QML load failed";return 3;}
    qInfo().noquote()<<"AOI_UI_READY"<<role.toUpper()<<"SIM_ONLY";
    if (p.isSet(screenshotArg)) {
        const QString path=p.value(screenshotArg);
        QTimer::singleShot(1800,&app,[root=engine.rootObjects().first(),path]() {
            auto* win=qobject_cast<QQuickWindow*>(root);
            if (!win) { qWarning()<<"M00_SCREENSHOT_NOT_VERIFIED: root is not QQuickWindow"; return; }
            const auto image=win->grabWindow();
            if (image.isNull() || !image.save(path)) qWarning()<<"M00_SCREENSHOT_NOT_VERIFIED"<<path;
            else qInfo().noquote()<<"M00_QML_SCREENSHOT_SAVED"<<path<<image.size();
        });
    }
    return app.exec();
}
