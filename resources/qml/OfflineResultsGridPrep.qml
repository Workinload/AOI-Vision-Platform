import QtQuick 6.11
import QtQuick.Controls 6.11
import QtQuick.Layouts 6.11
// W068 PREP only: no Core result binding, never represent this as production data.
Rectangle {
    id: root
    width: 2560; height: 1440
    color: "#F4F7FB"
    readonly property bool productionWriteEnabled: false
    readonly property string dataStatus: "DISCONNECTED"
    readonly property string evidenceLevel: "MOCK / OFFLINE / NOT VERIFIED"
    signal cardRequested(int channelIndex)
    ColumnLayout {
        anchors.fill: parent
        anchors.margins: 24
        spacing: 10
        RowLayout {
            Layout.fillWidth: true
            Label { text: "AOI · 11 通道检测视图"; font.bold: true; font.pixelSize: 22 }
            Item { Layout.fillWidth: true }
            Label { text: root.evidenceLevel; color: "#995C18"; font.bold: true }
        }
        Label { text: "Core 未接入：所有检测结果不可用 · 生产写入禁用"; color: "#A33B36" }
        GridLayout {
            Layout.fillWidth: true
            Layout.fillHeight: true
            columns: 4; rows: 3
            columnSpacing: 12; rowSpacing: 12
            Repeater {
                model: 12
                delegate: Rectangle {
                    required property int index
                    readonly property bool isSummary: index === 11
                    Layout.fillWidth: true
                    Layout.fillHeight: true
                    color: "white"; radius: 12
                    border.color: "#D7DFE9"
                    ColumnLayout {
                        anchors.fill: parent
                        anchors.margins: 16
                        Label {
                            text: isSummary ? "整料汇总" : ("CH" + String(index + 1).padStart(2, "0"))
                            font.bold: true; font.pixelSize: 17
                        }
                        Item { Layout.fillHeight: true }
                        Label {
                            Layout.alignment: Qt.AlignHCenter
                            text: isSummary ? "无法计算整料质量" : "未接入 Core 图像/判定"
                            color: "#586B80"
                        }
                        Label { Layout.alignment: Qt.AlignHCenter; text: "NO DATA · DISCONNECTED"; color: "#A33B36" }
                        Item { Layout.fillHeight: true }
                    }
                    MouseArea { anchors.fill: parent; onClicked: root.cardRequested(index) }
                }
            }
        }
        Label { text: "SIM_ONLY · W015/W067/W068 未验收 · 不显示虚构质量判定"; color: "#68798B" }
    }
}
