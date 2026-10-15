# Agent01 Issue #43 overnight Core + PeerLink handoff (SIM_ONLY)

Implementation: preview-only Qt C++20 dual-role Core executable, a bounded workpiece runtime, read-only live JSON snapshots and loopback TCP PeerLink using per-host SQLite WAL inbox/outbox. Event duplicate MessageId replays are idempotent; durable inbox+effect transaction precedes PERSISTED_ACK. No external adapter or production PLC write. 

Task scope PRE-BRANCH W014/W017/W035-W040/W042-W045, no WBS VERIFIED. W058 actual OpenCV overlay awaits A4/A6. A0 C01 PR #29/#39 are drafts. The TCP envelope `aoi.peer.sim.v0` is NOT a final C01 API. Peer handshake only asserts role/version, **NOT authenticated/cryptographically protected**, and must not be exposed off loopback or merged as W042 auth completion. Permanent peer credential, mutual challenge, replay window, full 60s outage/failover, dead-letter, station asset contracts, fully bounded outbox and actual image processing are UNIMPLEMENTED. Do not treat any of this as SIM_READY.

QA performed in Debian Linux using bundled read-only Qt 6.11.2, OpenCV 5.0.0:

```
cmake -S . -B /mnt/data/aoi_night01/build -G Ninja -DCMAKE_BUILD_TYPE=Debug -DAOI_SIM_ONLY=ON -DAOI_BUILD_GUI=OFF -DAOI_PROBE_OPENCV5=ON -DCMAKE_PREFIX_PATH='/mnt/data/aoi_night01/sdk/vision_sdk/components/qt;/mnt/data/aoi_night01/sdk/vision_sdk/components/opencv' # EXIT 0
cmake --build /mnt/data/aoi_night01/build --parallel 3 # EXIT 0
python3 tests/unit/peer/night_real_tcp_core_test.py --bin-dir /mnt/data/aoi_night01/build --evidence-dir /mnt/data/aoi_night01/qa/run_final1 # EXIT 0, 17/17 PASS
ctest --test-dir /mnt/data/aoi_night01/build --output-on-failure # TIMED OUT / NOT VERIFIED for entire suite; first 2 PASS
```

The live test launches two OS processes, two distinct SQLite files, one actual loopback TCP listener and client, sends messages both ways, verifies persisted acknowledgements and effect dedup after restart, rejects IPC mutation, checks epoch quarantine and CH01..11 snapshot/NG/HOLD. The scenario file is synthetic fixture and image asset URIs are blank. A10 must recompile exact Git SHA, rerun with isolated data dirs and verify no real PLC/Camera I/O.

WBS dependencies: W014<-W011,W012; W017<-W014; W035<-W017,W018,W012; W042<-W013,W014; W043<-W018,W042; W044<-W043; W045<-W035,W042,W043. All unmet in verified TASK_LEDGER. Do NOT merge until A10 independent QA, A0 C01 freeze, and A0 formal signoff. Separate PR #19/#31 left intact.