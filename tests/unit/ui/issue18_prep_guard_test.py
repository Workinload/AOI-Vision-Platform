"""Issue #18 branch-prep static guard, not a Qt runtime or Core integration test."""
from pathlib import Path
import re
import unittest

QML = Path(__file__).resolve().parents[3] / "resources/qml/prep/Issue18Prep.qml"

class Issue18PrepGuards(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.src = QML.read_text(encoding="utf-8")

    def test_twelve_tiles_and_whole_piece(self):
        self.assertRegex(self.src, r"columns:\s*4;\s*rows:\s*3")
        self.assertRegex(self.src, r"model:\s*12")
        self.assertIn('"WHOLE PIECE"', self.src)
        self.assertEqual(len(re.findall(r'"CH\d\d"', self.src)), 11)

    def test_shared_nine_device_selection(self):
        entities = re.findall(r'ListElement\s*\{\s*entityId:\s*"(DEV\d+)"', self.src)
        self.assertEqual(len(set(entities)), 9)
        self.assertEqual(self.src.count('model: deviceModel'), 2)
        self.assertGreaterEqual(self.src.count('root.selectedEntityId = entityId'), 2)

    def test_never_claim_live_or_write(self):
        self.assertIn('property bool coreConnected: false', self.src)
        for required in ("SIM_ONLY", "MOCK", "OFFLINE", "NO CORE RESULT", "NOT JUDGED", "READ-ONLY", "NO PRODUCTION WRITE"):
            self.assertIn(required, self.src)
        for prohibited in ("writeRegister(", "sendCommand(", "PLCGateway.write", 'qualityStatus: "OK"'):
            self.assertNotIn(prohibited, self.src)

if __name__ == "__main__":
    unittest.main(verbosity=2)
