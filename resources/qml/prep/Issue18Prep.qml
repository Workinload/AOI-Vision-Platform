// Issue #18 standalone PREP only: not loaded by production main.qml.
// All content is illustrative MOCK/OFFLINE and cannot command Core/PLC.
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
ApplicationWindow {
    id: root
    width: 1920; height: 1080; minimumWidth: 1280; minimumHeight: 720
    visible: true
    title: "AOI ISSUE18 / MOCK OFFLINE / SIM_ONLY"
    color: "#f3f6fb"
    property int tab: 0
    property string selectedEntityId: "DEV01"
    property bool coreConnected: false
    readonly property var channels: ["CH01","CH02","CH03","CH04","CH05","CH06","CH07","CH08","CH09","CH10","CH11"]
    ListModel {
        id: deviceModel
        ListElement { entityId: "DEV01"; name: "Area 01"; signal: "MOCK-01" }
        ListElement { entityId: "DEV02"; name: "Area 02"; signal: "MOCK-02" }
        ListElement { entityId: "DEV03"; name: "Area 03"; signal: "MOCK-03" }
        ListElement { entityId: "DEV04"; name: "Area 04"; signal: "MOCK-04" }
        ListElement { entityId: "DEV05"; name: "Area 05"; signal: "MOCK-05" }
        ListElement { entityId: "DEV06"; name: "Area 06"; signal: "MOCK-06" }
        ListElement { entityId: "DEV07"; name: "Area 07"; signal: "MOCK-07" }
        ListElement { entityId: "DEV08"; name: "Line reverse"; signal: "MOCK-08" }
        ListElement { entityId: "DEV09"; name: "Line front"; signal: "MOCK-09" }
    }
    header: Rectangle {
        height: 78; color: "#ffffff"
        RowLayout {
            anchors.fill: parent; anchors.margins: 18
            Label { text: "AOI / ISSUE #18"; font.pixelSize: 26; font.bold: true }
            Label { text: "MOCK / OFFLINE / SIM_ONLY / NO PRODUCTION WRITE"; color: "#a04710"; font.bold: true }
            Item { Layout.fillWidth: true }
            Button { text: "4×3"; onClicked: root.tab = 0 }
            Button { text: "Equipment / Table"; onClicked: root.tab = 1 }
        }
    }
    StackLayout {
        anchors.fill: parent; anchors.margins: 18
        currentIndex: root.tab
        GridLayout {
            columns: 4; rows: 3; columnSpacing: 12; rowSpacing: 12
            Repeater {
                model: 12
                delegate: Rectangle {
                    required property int index
                    Layout.fillWidth: true; Layout.fillHeight: true
                    radius: 12; color: "#ffffff"; border.color: "#dbe4ed"
                    ColumnLayout {
                        anchors.fill: parent; anchors.margins: 12
                        Label { text: index === 11 ? "WHOLE PIECE" : root.channels[index]; font.bold: true; font.pixelSize: 21 }
                        Rectangle {
                            Layout.fillWidth: true; Layout.fillHeight: true
                            color: "#f0f4f8"; radius: 8
                            Label { anchors.centerIn: parent; horizontalAlignment: Text.AlignHCenter
                                text: "NO CORE RESULT\nMOCK LAYOUT ONLY"; color: "#73869a" }
                        }
                        Label { text: "OFFLINE / NO DATA / NOT JUDGED"; color: "#a04710" }
                    }
                }
            }
        }
        ColumnLayout {
            Label { text: "Equipment map + parameter table: MOCK, READ-ONLY"; font.pixelSize: 24 }
            RowLayout {
                Layout.fillWidth: true; Layout.fillHeight: true
                Rectangle {
                    Layout.fillWidth: true; Layout.fillHeight: true; color: "#ffffff"; radius: 12
                    GridLayout {
                        anchors.fill: parent; anchors.margins: 12; columns: 3
                        Repeater {
                            model: deviceModel
                            delegate: Button {
                                required property string entityId
                                required property string name
                                required property string signal
                                Layout.fillWidth: true; Layout.fillHeight: true
                                text: name + "\n" + entityId + "\nOFFLINE"
                                highlighted: root.selectedEntityId === entityId
                                onClicked: root.selectedEntityId = entityId
                            }
                        }
                    }
                }
                Rectangle {
                    Layout.preferredWidth: root.width * 0.43; Layout.fillHeight: true
                    color: "#ffffff"; radius: 12
                    ColumnLayout {
                        anchors.fill: parent; anchors.margins: 12
                        Label { text: "Shared entityId selection / READ-ONLY"; font.bold: true }
                        Repeater {
                            model: deviceModel
                            delegate: Button {
                                required property string entityId
                                required property string name
                                required property string signal
                                Layout.fillWidth: true
                                text: entityId + " | " + name + " | " + signal + " | READ-ONLY"
                                highlighted: root.selectedEntityId === entityId
                                onClicked: root.selectedEntityId = entityId
                            }
                        }
                        Label { text: "Selected: " + root.selectedEntityId + " / no PLC writes"; color: "#a04710" }
                        Item { Layout.fillHeight: true }
                    }
                }
            }
        }
    }
}