# Start five ChatGPT subchats (current chat = A0 lead)

1. Keep `/AOI开发项目` and `/工具开发` READ ONLY. Create a separate personal Library folder `/AOI开发成果` if useful for archiving versions; ChatGPT Project resources aren't a shared editable Git working tree.
2. Download and preserve the exact `AOI_Vision_Platform_M00_r001_SOURCE.zip`, preferably a backup on local PC. The ZIP SHA256 is in `docs/evidence/M00_TEST_REPORT.md` and external `.sha256` file.
3. Open five new chats **inside this same `AOI软件开发` ChatGPT project**. Give each the identical SOURCE ZIP as attachment, plus its respective `CHAT_START_*.md` text from this package.
4. Open Agent05 QA first to independently verify M00 tests/W001 evidence and check PLAN v1.1 dependencies. Current A0 proceeds W002/W003/W004; do not greenlight downstream W011/W023 until prerequisites verified.
5. Each agent returns a **patch ZIP**, not an entire tree that would overwrite others. Copy resulting patch ZIP to main A0 chat. A0 validates and creates a candidate integrated ZIP; Agent05 builds/regresses; A0 publishes only then.
6. No background auto-collaboration or automatic cross-chat shared filesystem. The user may need to manually upload/deliver each ZIP. Unchanged but large SDK parts should be accessed from `/工具开发` separately and are excluded from this SOURCE ZIP.
7. M00 binaries can be built with Qt6.11.2 Linux SDK and Qt Quick. Windows builds and actual AOI business functions remain NOT_VERIFIED/NOT_IMPLEMENTED.

First QA command on Linux with extracted correct Qt SDK:
```bash
cmake -S . -B build/linux-sim -G Ninja -DCMAKE_PREFIX_PATH="$SDK/components/qt" -DAOI_SIM_ONLY=ON
cmake --build build/linux-sim -j 4
ctest --test-dir build/linux-sim --output-on-failure
```
