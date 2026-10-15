# AOI Vision Platform — M00-r001 baseline

**Simulation-only starting project.** This ZIP is a real, Linux-buildable scaffold for Qt 6.11.2/C++20 and a ZIP-based multi-chat development workflow. It is **not** an operational AOI, SIM_READY, a FINS implementation, or a Windows release. No physical equipment control.

## First read

1. `docs/agents/START_HERE.md`: how to create the five ChatGPT sub-chats and transfer ZIP patches.
2. `BASELINE_LOCK.json`: SHA256 of immutable source specification v2.0, plan v1.1, SDK reference.
3. `docs/agents/OWNERSHIP.md` and `contracts/C00_DRAFT.md`: boundaries and contracts.
4. `docs/evidence/M00_TEST_REPORT.md`: tests that were actually run.

## Developer build — Debian 13 / Linux x64

Qt/OpenCV SDKs are **not included**. Use existing read-only `/工具开发` Pe SDK components 03 Qt base, 04 Qt Quick, 05 Qt dev; extract into the same external directory *preserving symlinks*. OpenCV5 is optional for compiling the base Core, but if its SDK exists M00 also builds and runs a real OpenCV 5.0.0 C++ pixel-operation probe. It is mandatory later in vision work.

```bash
export SDK=/your/cache/vision_sdk
cmake -S . -B build/linux -G Ninja -DCMAKE_PREFIX_PATH="$SDK/components/qt;$SDK/components/opencv" -DAOI_SIM_ONLY=ON
cmake --build build/linux -j 4
ctest --test-dir build/linux --output-on-failure
# run all five OS processes and inspect real Unix-domain IPC + UDP health
QT_QPA_PLATFORM=offscreen QSG_RHI_BACKEND=software \
python3 tests/integration/five_process_smoke.py --bin-dir build/linux
```

Run UI in desktop (with X11/Wayland available) using `aoi-ui --role master --ipc /absolute/path/master/core.sock`, after starting Core. In M00 this shows a SIM-only health screen; the final 11-channel QML UI and process integration is downstream work.

## Safety

`AOI_SIM_ONLY=ON` cannot be switched off in M00. No production FINS driver, address, camera SDK, or outbound PLC connection exists in the binaries. `vplc` has UDP loopback *health handshake only*; unknown datagrams, including FINS commands, are ignored. UI↔Core is strictly read-only. Do not claim an official production site firewall policy was validated.

## For ZIP integration

Read `docs/agents/ZIP_PROTOCOL.md`, distribute *this same M00 ZIP* to all subchats, and use `tools/validate_patch.py` / `tools/integrate_patch.py`; official integration only from A0 after A10 checks. Do not share raw source folder contents via chat copy-paste.

The Qt offscreen screenshot test can optionally use environment variable `AOI_M00_TEST_FONT_FILE=/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc` to register an existing Linux CJK font for headless CI. Font binaries are **not** bundled/distributed. The final UI appearance and Windows font fallback remain future QA.
