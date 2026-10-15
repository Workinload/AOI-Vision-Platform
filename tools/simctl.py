#!/usr/bin/env python3
"""AOI SIM_ONLY Linux process supervisor (v0 M00 scaffold; no device writes).

This is an independent A10 developer utility, not a production service or C01.
It invokes only locally supplied aoi-core, aoi-ui and vplc binaries, checks
identity before signals, and never connects to a real PLC or camera. The
baseline vPLC understands HEALTH only; FINS commands return NOT_IMPLEMENTED.
"""
from __future__ import annotations

import argparse
import contextlib
import fcntl
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import sqlite3
import struct
import subprocess
import sys
import tempfile
import time
import uuid
import zlib

SCHEMA = "aoi.simctl.local.v1"
PROCESS_NAMES = ("vplc", "master-core", "slave-core", "master-ui", "slave-ui")
IPC_REQUEST_MAX = 65536
EXIT_INVALID = 2
EXIT_DOWN = 3
EXIT_BLOCKED = 4
EXIT_FAILED = 5


class SimError(RuntimeError):
    def __init__(self, message, code=EXIT_FAILED):
        super().__init__(message)
        self.code = code


def atomic_json(path: Path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(prefix=".simctl.", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as fh:
            json.dump(data, fh, indent=2, ensure_ascii=False, sort_keys=True)
            fh.write("\n")
            fh.flush()
            os.fsync(fh.fileno())
        os.replace(tmp, path)
        with contextlib.suppress(OSError):
            dfd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
            try:
                os.fsync(dfd)
            finally:
                os.close(dfd)
    finally:
        with contextlib.suppress(FileNotFoundError):
            os.unlink(tmp)


def safe_root(arg):
    raw = Path(arg).absolute()
    if raw.is_symlink() or not raw.is_absolute() or str(raw) in ("/", "/tmp", "/mnt/data", "/home"):
        raise SimError("unsafe runtime directory", EXIT_INVALID)
    if any(p == ".." for p in Path(arg).parts):
        raise SimError("relative parent traversal forbidden", EXIT_INVALID)
    if raw.is_relative_to(Path("/proc")) or raw.is_relative_to(Path("/sys")) or raw.is_relative_to(Path("/dev")):
        raise SimError("special filesystem not supported", EXIT_INVALID)
    return raw


@contextlib.contextmanager
def root_lock(root):
    root.mkdir(parents=True, exist_ok=True)
    path = root / ".simctl.lock"
    if path.is_symlink():
        raise SimError("symlink lockfile refused", EXIT_INVALID)
    with path.open("a+b") as f:
        fcntl.flock(f, fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(f, fcntl.LOCK_UN)


def read_state(root):
    path = root / "simctl_state.json"
    if path.is_symlink():
        raise SimError("symlink state file refused", EXIT_INVALID)
    if not path.exists():
        raise SimError("no simctl instance registered", EXIT_DOWN)
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError) as e:
        raise SimError("state file corrupted: " + str(e)) from e
    if data.get("schema") != SCHEMA or not isinstance(data.get("processes"), dict):
        raise SimError("untrusted instance state")
    return data


def proc_identity(pid):
    """Linux PID start tick prevents accidental killing of a reused PID."""
    try:
        stat = Path(f"/proc/{pid}/stat").read_text()
        # comm may contain spaces/parentheses, so parse after the final ')'.
        after = stat.rsplit(")", 1)[1].strip().split()
        ticks = int(after[19])  # field #22 (zero-index after pid+comm => 19)
        command = Path(f"/proc/{pid}/cmdline").read_bytes().split(b"\0")
        cmd0 = os.fsdecode(command[0]) if command and command[0] else ""
        state = after[0]
        return (ticks, os.path.realpath(cmd0), state)
    except (OSError, ValueError, IndexError):
        return None


def owns(entry):
    try:
        pid = int(entry["pid"])
        current = proc_identity(pid)
        return (current is not None and current[0] == int(entry["start_ticks"])
                and current[1] == str(Path(entry["exe"]).resolve())
                and current[2] != "Z")
    except (TypeError, ValueError, KeyError, OSError):
        return False


def boot_id():
    return Path("/proc/sys/kernel/random/boot_id").read_text().strip()


def command_ipc(path, payload, timeout=1.5):
    if not path.is_absolute():
        raise SimError("IPC path must be absolute", EXIT_INVALID)
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as sock:
        sock.settimeout(timeout)
        sock.connect(str(path))
        sock.sendall(payload + b"\n")
        answer = bytearray()
        while not answer.endswith(b"\n"):
            chunk = sock.recv(4096)
            if not chunk:
                raise SimError("IPC disconnected without full JSON")
            answer.extend(chunk)
            if len(answer) > IPC_REQUEST_MAX:
                raise SimError("IPC reply exceeds 64K cap")
        return json.loads(answer)


def check_udp(port, timeout=1.5):
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.settimeout(timeout)
        sock.sendto(b"M00_HEALTH_V1", ("127.0.0.1", int(port)))
        response, addr = sock.recvfrom(128)
        if addr[0] != "127.0.0.1" or response != b"M00_SIM_ONLY_FINS_NOT_IMPLEMENTED":
            raise SimError("unexpected vPLC health response; FINS is NOT implemented")
        return "HEALTH_ONLY__FINS_NOT_IMPLEMENTED"


def verify_png(path):
    if not path.is_file() or path.is_symlink() or path.stat().st_size < 1024:
        return None
    data = path.read_bytes()
    if not data.startswith(b"\x89PNG\r\n\x1a\n"):
        return None
    offset, size, n_idat = 8, None, 0
    while offset + 12 <= len(data):
        n = struct.unpack_from(">I", data, offset)[0]
        if n > 32 * 1024 * 1024 or offset + n + 12 > len(data):
            return None
        piece = data[offset + 4:offset + 8 + n]
        crc = struct.unpack_from(">I", data, offset + 8 + n)[0]
        if zlib.crc32(piece) & 0xffffffff != crc:
            return None
        tag = piece[:4]
        if offset == 8 and tag != b"IHDR":
            return None
        if tag == b"IHDR":
            if n != 13:
                return None
            size = struct.unpack_from(">II", piece, 4)
        if tag == b"IDAT":
            n_idat += 1
        offset += n + 12
        if tag == b"IEND":
            if offset != len(data) or n_idat == 0 or not size or min(size) < 32:
                return None
            return {"width": size[0], "height": size[1],
                    "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}
    return None


def assert_binary(root, name):
    path = (root / name).resolve()
    if not path.is_file() or not os.access(path, os.X_OK):
        raise SimError("missing executable: " + str(path), EXIT_INVALID)
    if path.read_bytes()[:4] != b"\x7fELF":
        raise SimError("Linux ELF executable required: " + str(path), EXIT_INVALID)
    return path


def verify_db(dirpath, role):
    filename = dirpath / "sim_bootstrap.sqlite3"
    if not filename.exists() or filename.is_symlink():
        raise SimError("missing SQLite for " + role)
    # Read-only attach to WAL; no schema mutation.
    conn = sqlite3.connect(f"file:{filename}?mode=ro", uri=True, timeout=1)
    try:
        journal = conn.execute("PRAGMA journal_mode").fetchone()[0]
        stored = conn.execute("SELECT value FROM m00_meta WHERE name='host_role'").fetchone()
        if journal.lower() != "wal" or not stored or stored[0] != role:
            raise SimError("SQLite WAL role mismatch " + role)
    finally:
        conn.close()
    return {"role": role, "path": str(filename), "wal": True}


def identity_summary(state):
    oldboot = state.get("boot_id") == boot_id()
    return {name: {"pid": entry.get("pid"), "owned_alive": oldboot and owns(entry)}
            for name, entry in state["processes"].items()}


def report(root, state, require_live=False):
    live = identity_summary(state)
    errors = []
    if set(live) != set(PROCESS_NAMES):
        errors.append("not exactly five named processes")
    if not all(e["owned_alive"] for e in live.values()):
        errors.append("one or more registered process exited/unowned")
    health = {}
    db = {}
    for name, role in (("master", "MASTER"), ("slave", "SLAVE")):
        try:
            reply = command_ipc(root / name / "core.ipc.sock", b"health")
            if reply.get("mode") != "SIM_ONLY" or reply.get("role") != role:
                raise SimError("role/mode mismatch")
            if reply.get("state") != "SCAFFOLD_ONLY":
                raise SimError("unknown source state, require new A10 contract")
            health[name] = reply
            db[name] = verify_db(root / name, role)
        except (OSError, ValueError, SimError, sqlite3.Error) as e:
            errors.append(f"{name} Core unavailable: {e}")
    if "master" in db and "slave" in db and db["master"]["path"] == db["slave"]["path"]:
        errors.append("shared master/slave SQLite is forbidden")
    try:
        udp = check_udp(state["vplc_port"])
    except (OSError, ValueError, SimError) as e:
        udp = "NOT_HEALTHY"
        errors.append("vPLC HEALTH_ONLY probe failed: " + str(e))
    screenshots = {role: verify_png(root / "screenshots" / (role + ".png"))
                   for role in ("master", "slave")}
    if state.get("screenshots_expected") and any(not v for v in screenshots.values()):
        errors.append("Qt Quick PNG evidence missing/corrupt")
    logs = {}
    for name in PROCESS_NAMES:
        logfile = root / "logs" / (name + ".log")
        if logfile.exists():
            logs[name] = {"path": str(logfile), "sha256": hashlib.sha256(logfile.read_bytes()).hexdigest()}
    for role in ("master", "slave"):
        logfile = root / "logs" / (role + "-ui.log")
        if logfile.exists() and ("AOI_UI_IPC_CONNECTED " + role.upper()) not in logfile.read_text(errors="replace"):
            errors.append("Qt Quick UI never confirmed Core IPC " + role)
    status = "SIM_SCAFFOLD_HEALTHY" if not errors else "NOT_HEALTHY"
    data = {"schema": SCHEMA, "status": status, "mode": "SIM_ONLY",
            "source": "ACTUAL_OS_PROCESSES_M00_SCAFFOLD",
            "fins": "NOT_IMPLEMENTED", "camera": "NOT_IMPLEMENTED",
            "peerlink": "NOT_IMPLEMENTED", "formal_tc_executed": 0,
            "full_functional_e2e": False, "run_id": state["run_id"],
            "processes": live, "core_health": health, "sqlite": db,
            "vplc": udp, "screenshots": screenshots, "logs": logs, "errors": errors}
    if require_live and errors:
        raise SimError("; ".join(errors))
    return data


def stop_owned(state, timeout=3):
    entries = state.get("processes", {})
    for name in reversed(PROCESS_NAMES):
        entry = entries.get(name)
        if not entry or not owns(entry) or state.get("boot_id") != boot_id():
            continue
        with contextlib.suppress(ProcessLookupError, PermissionError):
            os.kill(int(entry["pid"]), signal.SIGTERM)
    until = time.monotonic() + timeout
    while time.monotonic() < until:
        if not any(owns(x) for x in entries.values()):
            break
        time.sleep(0.07)
    for name, entry in entries.items():
        if owns(entry) and state.get("boot_id") == boot_id():
            with contextlib.suppress(ProcessLookupError, PermissionError):
                os.kill(int(entry["pid"]), signal.SIGKILL)


def build_state(root, binary_dir, port, screenshots):
    return {"schema": SCHEMA, "mode": "SIM_ONLY", "run_id": str(uuid.uuid4()),
            "boot_id": boot_id(), "source": "M00_SCAFFOLD_ONLY",
            "binary_dir": str(binary_dir), "vplc_port": port,
            "screenshots_expected": screenshots, "processes": {}}


def up(args):
    root = safe_root(args.run_dir)
    binary_dir = Path(args.bin_dir).resolve()
    binaries = {x: assert_binary(binary_dir, x) for x in ("aoi-core", "aoi-ui", "vplc")}
    with root_lock(root):
        try:
            state = read_state(root)
            if any(owns(x) for x in state["processes"].values()):
                raise SimError("instance already running; down before up", EXIT_BLOCKED)
        except SimError as e:
            if e.code != EXIT_DOWN:
                raise
        mark = root / ".aoi_simctl_owned"
        if mark.is_symlink():
            raise SimError("symlink ownership marker refused", EXIT_INVALID)
        if mark.exists():
            owner = mark.read_text().strip()
            if owner != SCHEMA:
                raise SimError("foreign runtime marker, refusing takeover", EXIT_BLOCKED)
        else:
            if set(p.name for p in root.iterdir()) - {".simctl.lock"}:
                raise SimError("unowned nonempty runtime directory; cannot safely reuse", EXIT_BLOCKED)
            mark.write_text(SCHEMA + "\n")
        for dirname in ("logs", "screenshots", "master", "slave"):
            (root / dirname).mkdir(exist_ok=True)
        if not 1024 <= args.port <= 65535:
            raise SimError("loopback vPLC port must be >=1024 and <=65535", EXIT_INVALID)
        env = os.environ.copy()
        env.setdefault("QT_QPA_PLATFORM", "offscreen")
        env.setdefault("QSG_RHI_BACKEND", "software")
        env.setdefault("QT_QUICK_BACKEND", "software")
        state = build_state(root, binary_dir, args.port, not args.no_screenshots)
        file_handles = []
        try:
            cmds = {
                "vplc": [binaries["vplc"], "--bind", "127.0.0.1", "--port", str(args.port)],
                "master-core": [binaries["aoi-core"], "--role", "master", "--data-dir", root / "master"],
                "slave-core": [binaries["aoi-core"], "--role", "slave", "--data-dir", root / "slave"],
                "master-ui": [binaries["aoi-ui"], "--role", "master", "--ipc", root / "master" / "core.ipc.sock"],
                "slave-ui": [binaries["aoi-ui"], "--role", "slave", "--ipc", root / "slave" / "core.ipc.sock"],
            }
            if not args.no_screenshots:
                for role in ("master", "slave"):
                    cmds[role + "-ui"] += ["--screenshot", root / "screenshots" / (role + ".png")]
            for name, argv in cmds.items():
                log = (root / "logs" / (name + ".log")).open("ab", buffering=0)
                file_handles.append(log)
                p = subprocess.Popen([str(x) for x in argv], env=env,
                                     stdin=subprocess.DEVNULL, stdout=log,
                                     stderr=subprocess.STDOUT, close_fds=True,
                                     start_new_session=True)
                identity = None
                for _ in range(50):
                    identity = proc_identity(p.pid)
                    if identity:
                        break
                    time.sleep(0.005)
                if not identity:
                    raise SimError("cannot record exact PID start identity")
                state["processes"][name] = {"pid": p.pid, "start_ticks": identity[0],
                                             "exe": str(argv[0])}
                atomic_json(root / "simctl_state.json", state)
            until = time.monotonic() + args.timeout
            last = None
            while time.monotonic() < until:
                try:
                    if all(owns(x) for x in state["processes"].values()):
                        snapshot = report(root, state)
                        if snapshot["status"] == "SIM_SCAFFOLD_HEALTHY":
                            atomic_json(root / "simctl_evidence.json", snapshot)
                            print(json.dumps({"status": "UP", "scope": snapshot["status"],
                                              "run_id": state["run_id"],
                                              "pids": {n:v["pid"] for n,v in state["processes"].items()},
                                              "run_dir": str(root)}, ensure_ascii=False))
                            return 0
                        last = snapshot["errors"]
                    else:
                        last = "OS process died during startup"
                        break
                except (OSError, ValueError, SimError) as e:
                    last = str(e)
                time.sleep(0.13)
            raise SimError("five-process startup not verified: " + str(last))
        except BaseException:
            stop_owned(state)
            # An unsuccessful launch is not eligible for evidence PASS.
            with contextlib.suppress(OSError):
                (root / "simctl_state.json").unlink()
            raise
        finally:
            for f in file_handles:
                f.close()


def down(args):
    root = safe_root(args.run_dir)
    with root_lock(root):
        state = read_state(root)
        stop_owned(state, timeout=args.timeout)
        still = [n for n,e in state["processes"].items() if owns(e)]
        if still:
            raise SimError("could not stop owned processes: " + repr(still))
        with contextlib.suppress(FileNotFoundError):
            (root / "simctl_state.json").unlink()
        print(json.dumps({"status": "DOWN", "scope": "SIM_ONLY", "run_id": state["run_id"]}))
        return 0


def status(args):
    root = safe_root(args.run_dir)
    with root_lock(root):
        state = read_state(root)
        result = report(root, state)
        print(json.dumps(result, ensure_ascii=False, sort_keys=True))
        if result["status"] != "SIM_SCAFFOLD_HEALTHY":
            raise SimError("instance not healthy")
        return 0


def inject(args):
    root = safe_root(args.run_dir)
    if args.kind not in ("core_denies_plc_write", "core_denies_unknown", "vplc_ignores_fins",
                         "peer_disconnect", "stale_epoch", "missing_frame"):
        raise SimError("injection type not allowlisted", EXIT_INVALID)
    if args.kind in ("peer_disconnect", "stale_epoch", "missing_frame"):
        raise SimError("BLOCKED_DEP: actual Core/Peer/camera feature absent, injection not simulated as PASS",
                       EXIT_BLOCKED)
    with root_lock(root):
        state = read_state(root)
        if set(state["processes"]) != set(PROCESS_NAMES) or not all(
                owns(x) for x in state["processes"].values()):
            raise SimError("process identity not healthy")
        if args.kind in ("core_denies_plc_write", "core_denies_unknown"):
            msg = b"write D1016 1" if args.kind == "core_denies_plc_write" else b"UNTRUSTED_WRITE"
            response = command_ipc(root / "master" / "core.ipc.sock", msg)
            if response.get("status") != "REJECTED":
                raise SimError("FAIL: Core accepted a non-readonly command")
            result = {"status": "PASS", "injection": args.kind,
                      "rejected_reason": response.get("reason")}
        else:
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
                s.settimeout(0.55)
                s.sendto(b"FINS_WRITE_REQUEST_FORBIDDEN", ("127.0.0.1", state["vplc_port"]))
                try:
                    bad, _ = s.recvfrom(128)
                    raise SimError("FAIL: vPLC replied to untrusted FINS " + bad.hex())
                except socket.timeout:
                    result = {"status": "PASS", "injection": args.kind,
                              "verification": "no_response_to_unimplemented_FINS"}
        record = root / "simctl_injections.jsonl"
        with record.open("a", encoding="utf-8") as f:
            f.write(json.dumps(result, sort_keys=True) + "\n")
            f.flush();os.fsync(f.fileno())
        print(json.dumps(result, ensure_ascii=False))
        return 0


def evidence(args):
    root = safe_root(args.run_dir)
    with root_lock(root):
        state = read_state(root)
        result = report(root, state, require_live=True)
        if result["status"] != "SIM_SCAFFOLD_HEALTHY":
            raise SimError("only successful evidence may be exported")
        atomic_json(root / "simctl_evidence.json", result)
        print(json.dumps({"status": "PASS", "scope": result["status"],
                          "file": str(root / "simctl_evidence.json"),
                          "sha256": hashlib.sha256((root / "simctl_evidence.json").read_bytes()).hexdigest()}))
        return 0


def reset(args):
    if not args.confirm_discard_sim_state:
        raise SimError("reset requires --confirm-discard-sim-state", EXIT_BLOCKED)
    root = safe_root(args.run_dir)
    with root_lock(root):
        mark = root / ".aoi_simctl_owned"
        if not mark.is_file() or mark.is_symlink() or mark.read_text().strip() != SCHEMA:
            raise SimError("runtime path was not created by simctl", EXIT_BLOCKED)
        try:
            state = read_state(root)
            if any(owns(x) for x in state["processes"].values()):
                raise SimError("cannot reset live processes; down first", EXIT_BLOCKED)
        except SimError as e:
            if e.code != EXIT_DOWN:
                raise
        # Only known simctl runtime outputs are deleted; never wipe arbitrary files.
        known_files = ("simctl_state.json", "simctl_evidence.json", "simctl_injections.jsonl")
        known_dirs = ("master", "slave", "logs", "screenshots")
        unexpected = {p.name for p in root.iterdir()} - set(known_files) - set(known_dirs) - {
            ".simctl.lock", ".aoi_simctl_owned"}
        if unexpected:
            raise SimError("unrecognized content blocks reset: " + repr(sorted(unexpected)), EXIT_BLOCKED)
        for name in known_dirs:
            path = root / name
            if path.is_symlink():
                raise SimError("refuse to delete symlink dir: " + name, EXIT_BLOCKED)
        for name in known_files:
            with contextlib.suppress(FileNotFoundError):
                (root / name).unlink()
        for name in known_dirs:
            shutil.rmtree(root / name, ignore_errors=False) if (root / name).exists() else None
        print(json.dumps({"status": "RESET", "scope": "SIM_ONLY", "root": str(root)}))
        return 0


def main(argv=None):
    if sys.platform != "linux":
        print("BLOCKED_ENV: simctl supervisor v1 is Linux-only; Windows native not verified",
              file=sys.stderr)
        return EXIT_BLOCKED
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("up", "down", "status", "inject", "evidence", "reset"):
        p = sub.add_parser(name)
        p.add_argument("--run-dir", required=True)
        if name == "up":
            p.add_argument("--bin-dir", required=True)
            p.add_argument("--port", type=int, default=19600)
            p.add_argument("--timeout", type=float, default=16)
            p.add_argument("--no-screenshots", action="store_true")
        if name == "down":
            p.add_argument("--timeout", type=float, default=3)
        if name == "inject":
            p.add_argument("--kind", required=True)
        if name == "reset":
            p.add_argument("--confirm-discard-sim-state", action="store_true")
    try:
        return {"up": up,"down": down,"status": status,"inject": inject,
                "evidence": evidence,"reset": reset}[parser.parse_args(argv).command](parser.parse_args(argv))
    except SimError as e:
        print(json.dumps({"status": "FAIL_CLOSED", "error": str(e), "exit": e.code},
                         ensure_ascii=False), file=sys.stderr)
        return e.code

if __name__ == "__main__":
    sys.exit(main())