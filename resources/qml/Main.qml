import QtQuick 6.11
import QtQuick.Controls 6.11
import QtQuick.Layouts 6.11
ApplicationWindow {
    id: window
    width: 1160
    height: 700
    minimumWidth: 650
    minimumHeight: 440
    visible: true
    title: "AOI · " + uiHostRole + " · SIM_ONLY M00"
    color: "#F4F7FB"
    readonly property color ink: "#14243A"
    header: Rectangle {
        height: 82
        color: "#FFFFFF"
        RowLayout {
            anchors.fill: parent
            anchors.leftMargin: 34
            anchors.rightMargin: 34
            spacing: 16
            ColumnLayout {
                spacing: 3
                Label { text: "AOI Vision Platform"; color: window.ink; font.pixelSize: 23; font.bold: true }
                Label { text: "统一模拟工程 · " + uiHostRole; color: "#6A7889"; font.pixelSize: 12 }
            }
            Item { Layout.fillWidth: true }
            Rectangle {
                width: 132; height: 32; radius: 16
                color: "#FFF0DC"
                Label { anchors.centerIn: parent; text: "SIMULATION ONLY"; color: "#A66312"; font.bold: true; font.pixelSize: 11 }
            }
        }
    }
    ColumnLayout {
        anchors.fill: parent
        anchors.margins: 34
        spacing: 20
        Label { text: "工程运行基线 · M00-r001"; font.pixelSize: 26; color: window.ink; font.bold: true }
        Label { text: "当前画面读取独立 AOI-Core 的实际 IPC 状态；生产检测、PLC 寄存器和 11 路图像尚未接入。"; color: "#68798B"; font.pixelSize: 13; wrapMode: Text.Wrap }
        Rectangle {
            Layout.fillWidth: true
            Layout.preferredHeight: 170
            radius: 14
            color: "white"
            border.color: "#E5EAF1"
            ColumnLayout {
                anchors.fill: parent; anchors.margins: 24; spacing: 12
                RowLayout {
                    Label { text: bridge.connected ? "●  Core 已连接" : "●  Core 未连接"; color: bridge.connected ? "#11805C" : "#B64643"; font.bold: true; font.pixelSize: 19 }
                    Item { Layout.fillWidth: true }
                    Label { text: bridge.mode; color: "#A66312"; font.pixelSize: 12 }
                }
                Label { text: "进程角色：" + bridge.coreRole + "     Core状态：" + bridge.coreState; color: window.ink; font.pixelSize: 14 }
                Label { text: "Core 运行时间：" + bridge.uptime; color: "#68798B"; font.pixelSize: 13 }
                Label { text: bridge.ipcError; visible: !bridge.connected && bridge.ipcError.length > 0; color: "#B64643"; font.pixelSize: 12 }
            }
        }
        Label { text: "开发模块占位（不代表功能完成）"; color: window.ink; font.pixelSize: 17; font.bold: true }
        GridLayout {
            Layout.fillWidth: true
            columns: 3; rowSpacing: 12; columnSpacing: 12
            Repeater {
                model: ["生产调度 / A1", "PLC通信 / A2", "相机SDK / A3", "视觉算法 / A4", "双机PeerLink / A5", "数据资产 / A6", "方案换型 / A7", "11图主屏 / A8", "权限与报警 / A9"]
                delegate: Rectangle {
                    Layout.fillWidth: true; Layout.preferredHeight: 62
                    radius: 10; color: "#FFFFFF"; border.color: "#E5EAF1"
                    Column {
                        anchors.centerIn: parent
                        spacing: 4
                        Label { text: modelData; font.pixelSize: 12; color: window.ink; font.bold: true }
                        Label { text: "NOT IMPLEMENTED"; color: "#A66312"; font.pixelSize: 9 }
                    }
                }
            }
        }
        Item { Layout.fillHeight: true }
        Label { text: "M00 scaffold is not SIM_READY · 14 field verification gates remain open · No production PLC writes"; color: "#82909F"; font.pixelSize: 11; wrapMode: Text.Wrap }
    }
}
