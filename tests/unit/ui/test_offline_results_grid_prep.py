"""Preparation-only fail-closed static checks; NOT a Qt runtime test."""
from pathlib import Path
import unittest

SOURCE = Path(__file__).resolve().parents[3] / "resources/qml/OfflineResultsGridPrep.qml"

class GridPrepTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = SOURCE.read_text(encoding="utf-8")

    def test_layout(self):
        for item in ("model: 12", "columns: 4", "rows: 3", "index === 11"):
            self.assertIn(item, self.source)

    def test_disconnected(self):
        for item in ("DISCONNECTED", "NO DATA", "NOT VERIFIED", "productionWriteEnabled: false"):
            self.assertIn(item, self.source)

    def test_no_production_mutations(self):
        for term in ("XMLHttpRequest", "WebSocket", "writeRegister", "publishRelease", "Math.random"):
            self.assertNotIn(term, self.source)

    def test_no_hardcoded_ok(self):
        self.assertNotIn("quality: \"OK\"", self.source)
        self.assertNotIn("text: \"LIVE\"", self.source)

if __name__ == "__main__":
    unittest.main()
