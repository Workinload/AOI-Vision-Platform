#!/usr/bin/env python3
"""Actual five executable Linux simctl lifecycle, no mocked pass or PLC access.

Arguments: --simctl path --bin-dir real-binaries --evidence-dir writable
Runs only on Linux with exact Qt6.11.2/OpenCV5 M00 binaries; if unavailable BLOCK.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
import sys
import tempfile
import unittest

args = argparse.ArgumentParser()
args.add_argument("--simctl", type=Path, required=True)
args.add_argument("--bin-dir", type=Path, required=True)
args.add_argument("--evidence-dir", type=Path)
FLAGS, _ = args.parse_known_args()


class ActualFiveProcess(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.root = (FLAGS.evidence_dir.resolve() if FLAGS.evidence_dir else Path(tempfile.mkdtemp(prefix="aoi_simctl_e2e_")))
        cls.root.mkdir(parents=True, exist_ok=True)
        cls.run_dir = cls.root / "managed"
        cls.bin = FLAGS.bin_dir.resolve()
        cls.script = FLAGS.simctl.resolve()
        cls.events = []
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            s.bind(("127.0.0.1", 0))
            cls.port = s.getsockname()[1]
        p = cls.invoke("up", "--bin-dir", str(cls.bin), "--port", str(cls.port), "--timeout", "18")
        if p.returncode != 0:
            raise RuntimeError("real five-process start failed: " + p.stdout + p.stderr)
        cls.start_state = json.loads(p.stdout)

    @classmethod
    def invoke(cls, command, *args):
        p = subprocess.run([sys.executable, str(cls.script), command, "--run-dir", str(cls.run_dir), *args],
                           capture_output=True, text=True, timeout=45,
                           env=os.environ.copy())
        cls.events.append({"command": [command, *args], "exit_code": p.returncode,
                           "stdout_sha256": hashlib.sha256(p.stdout.encode()).hexdigest(),
                           "stderr_sha256": hashlib.sha256(p.stderr.encode()).hexdigest(),
                           "stderr_excerpt": p.stderr[-300:]})
        return p

    @classmethod
    def tearDownClass(cls):
        try:
            down=cls.invoke("down")
            if down.returncode != 0:
                raise RuntimeError("cannot stop simctl live subprocesses: " + down.stderr)
            cls.invoke("reset", "--confirm-discard-sim-state")
        finally:
            (cls.root / "simctl_test_commands.json").write_text(json.dumps(cls.events,indent=2,ensure_ascii=False), encoding="utf-8")

    def test_a_five_real_os_pids_and_health(self):
        p = self.invoke("status")
        self.assertEqual(p.returncode, 0, p.stderr)
        data = json.loads(p.stdout)
        self.assertEqual(data["status"], "SIM_SCAFFOLD_HEALTHY")
        self.assertEqual(len(data["processes"]), 5)
        self.assertEqual(len(set(x["pid"] for x in data["processes"].values())), 5)
        self.assertTrue(all(x["owned_alive"] for x in data["processes"].values()))
        self.assertEqual(data["fins"], "NOT_IMPLEMENTED")
        self.assertEqual(data["peerlink"], "NOT_IMPLEMENTED")

    def test_b_two_role_distinct_actual_WAL_databases(self):
        for host,role in (("master","MASTER"),("slave","SLAVE")):
            db=self.run_dir / host / "sim_bootstrap.sqlite3"
            conn=sqlite3.connect(f"file:{db}?mode=ro", uri=True)
            try:
                self.assertEqual(conn.execute("PRAGMA journal_mode").fetchone()[0], "wal")
                self.assertEqual(conn.execute("SELECT value FROM m00_meta WHERE name='host_role'").fetchone()[0],role)
            finally:conn.close()
        self.assertNotEqual((self.run_dir/"master/sim_bootstrap.sqlite3").resolve(),
                            (self.run_dir/"slave/sim_bootstrap.sqlite3").resolve())

    def test_c_real_UI_QML_screen_png_and_logs(self):
        p=self.invoke("status");self.assertEqual(p.returncode,0,p.stderr)
        data=json.loads(p.stdout)
        self.assertTrue(all(data["screenshots"].values()))
        for host in ("master","slave"):
            self.assertGreater(data["screenshots"][host]["bytes"],1024)
            self.assertEqual(len(data["screenshots"][host]["sha256"]),64)
            logfile=(self.run_dir/"logs"/(host+"-ui.log")).read_text(errors="replace")
            self.assertIn("AOI_UI_IPC_CONNECTED "+host.upper(),logfile)

    def test_d_write_denied_by_actual_Core_IPC(self):
        p=self.invoke("inject", "--kind", "core_denies_plc_write")
        self.assertEqual(p.returncode,0,p.stderr)
        self.assertEqual(json.loads(p.stdout)["rejected_reason"],"M00_READ_ONLY_IPC")

    def test_e_unknown_Core_IPC_denied(self):
        p=self.invoke("inject", "--kind", "core_denies_unknown")
        self.assertEqual(p.returncode,0,p.stderr)
        self.assertEqual(json.loads(p.stdout)["status"],"PASS")

    def test_f_FINS_datagram_not_implemented_not_accepted(self):
        p=self.invoke("inject", "--kind", "vplc_ignores_fins")
        self.assertEqual(p.returncode,0,p.stderr)
        self.assertEqual(json.loads(p.stdout)["status"],"PASS")

    def test_g_unknown_injection_fails_closed(self):
        p=self.invoke("inject", "--kind", "real_plc_D1016_write")
        self.assertEqual(p.returncode,2,p.stdout+p.stderr)

    def test_h_unimplemented_Peer_fails_closed_not_PASS(self):
        p=self.invoke("inject", "--kind", "peer_disconnect")
        self.assertEqual(p.returncode,4,p.stdout+p.stderr)
        self.assertIn("BLOCKED_DEP",p.stderr)

    def test_i_second_up_is_rejected_without_duplicate_procs(self):
        p=self.invoke("up", "--bin-dir", str(self.bin), "--port", str(self.port))
        self.assertEqual(p.returncode,4,p.stdout+p.stderr)
        state=json.loads((self.run_dir/"simctl_state.json").read_text())
        self.assertEqual({k:v["pid"] for k,v in state["processes"].items()},self.start_state["pids"])

    def test_j_reset_needs_confirmation_and_disallows_live(self):
        p=self.invoke("reset")
        self.assertEqual(p.returncode,4,p.stdout+p.stderr)
        p=self.invoke("reset", "--confirm-discard-sim-state")
        self.assertEqual(p.returncode,4,p.stdout+p.stderr)
        self.assertIn("down first",p.stderr)

    def test_k_stale_pid_start_tick_not_accepted(self):
        import importlib.util
        spec=importlib.util.spec_from_file_location("aoi_simctl",self.script)
        mod=importlib.util.module_from_spec(spec);spec.loader.exec_module(mod)
        state=json.loads((self.run_dir/"simctl_state.json").read_text())
        entry=dict(state["processes"]["master-core"])
        self.assertTrue(mod.owns(entry))
        entry["start_ticks"]+=1
        self.assertFalse(mod.owns(entry),"PID recycling check bypassed")

    def test_l_corrupt_QML_png_rejects_health_and_evidence(self):
        screenshot=self.run_dir/"screenshots"/"master.png"
        original=screenshot.read_bytes()
        try:
            screenshot.write_bytes(original[:200])
            p=self.invoke("status")
            self.assertEqual(p.returncode,5,p.stdout+p.stderr)
            self.assertEqual(json.loads(p.stdout)["status"],"NOT_HEALTHY")
            p=self.invoke("evidence")
            self.assertEqual(p.returncode,5,p.stdout+p.stderr)
        finally:
            screenshot.write_bytes(original)

    def test_m_healthy_evidence_sha_and_offline_scope(self):
        p=self.invoke("evidence")
        self.assertEqual(p.returncode,0,p.stdout+p.stderr)
        report=json.loads(p.stdout)
        f=Path(report["file"])
        self.assertEqual(hashlib.sha256(f.read_bytes()).hexdigest(),report["sha256"])
        doc=json.loads(f.read_text())
        self.assertFalse(doc["full_functional_e2e"])
        self.assertEqual(doc["formal_tc_executed"],0)
        self.assertEqual(doc["camera"],"NOT_IMPLEMENTED")

    def test_n_wont_reset_unowned_arbitrary_dir(self):
        with tempfile.TemporaryDirectory() as d:
            folder=Path(d)/"foreign";folder.mkdir();(folder/"important.bin").write_bytes(b"keep")
            p=subprocess.run([sys.executable,str(self.script),"reset","--run-dir",str(folder),
                             "--confirm-discard-sim-state"],capture_output=True,text=True)
            self.assertEqual(p.returncode,4,p.stdout+p.stderr)
            self.assertEqual((folder/"important.bin").read_bytes(),b"keep")

if __name__ == "__main__":
    unittest.main(argv=[sys.argv[0],"-v"],verbosity=2)