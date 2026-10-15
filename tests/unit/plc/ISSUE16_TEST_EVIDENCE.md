# Agent02 Issue #16 — local isolated test evidence, 2026-10-15

**State:** BRANCH_DEV / SIM_ONLY / DRAFT / NOT_INTEGRATION_READY. Self-run tests, NOT independent A10 review. Base main: `d81f5b3fd35dd44963c6c7464cdb3a6f5a124b7c`. First implementation commit: `7ceb74dd2eafb7e93518dc51721ec67e235a9226`.

These are local execution records on Linux; the direct network Git clone failed with `Could not resolve host github.com`. Source for new tests was staged via the authenticated GitHub connector, without directly modifying main. The legacy original M00-r001 ZIP was used only to cross-check immutable v2 reference CSVs, NOT to build an M00-r003 worktree. Original baseline full Qt/CMake/CTest and Windows native testing for this exact Git head: **NOT_VERIFIED**. No real UDP packets, cameras, PLCs or image buffers were used.

## Commands and observed exit codes

All commands run from a local partial worktree containing the five new owner-scoped source files and an identical snapshot of the immutable three reference CSV files (their Git blobs match main). Executable files were in `/mnt/data/aoi_github02/bin`. The Python test files here were legacy r001 carry-forward copies **only for local review**, not part of this PR.

| Test | Command | Exit | Result |
|---|---|---:|---|
| GCC FINS build | `g++ -std=c++20 -DAOI_SIM_ONLY=1 -O0 -g -Wall -Wextra -Werror -pedantic tests/unit/plc/fins_read_preflight_test.cpp -o /mnt/data/aoi_github02/bin/fins_read_preflight_test` | 0 | COMPILED |
| GCC FINS run | `/mnt/data/aoi_github02/bin/fins_read_preflight_test` | 0 | **33 passed, 0 failed** |
| GCC capture build | `g++ -std=c++20 -DAOI_SIM_ONLY=1 -O0 -g -Wall -Wextra -Werror -pedantic tests/unit/acquisition/capture_topology_preflight_test.cpp -o /mnt/data/aoi_github02/bin/capture_topology_preflight_test` | 0 | COMPILED |
| GCC capture run | `/mnt/data/aoi_github02/bin/capture_topology_preflight_test` | 0 | **36 passed, 0 failed** |
| Clang FINS UBSan build | `clang++ -std=c++20 -DAOI_SIM_ONLY=1 -O1 -g -fsanitize=undefined -fno-omit-frame-pointer -Wall -Wextra -Werror -pedantic tests/unit/plc/fins_read_preflight_test.cpp -o /mnt/data/aoi_github02/bin/fins_clang_ubsan` | 0 | COMPILED |
| Clang FINS UBSan run | `/mnt/data/aoi_github02/bin/fins_clang_ubsan` | 0 | **33 passed, 0 failed** |
| Clang capture UBSan build | `clang++ -std=c++20 -DAOI_SIM_ONLY=1 -O1 -g -fsanitize=undefined -fno-omit-frame-pointer -Wall -Wextra -Werror -pedantic tests/unit/acquisition/capture_topology_preflight_test.cpp -o /mnt/data/aoi_github02/bin/capture_clang_ubsan` | 0 | COMPILED |
| Clang capture UBSan run | `/mnt/data/aoi_github02/bin/capture_clang_ubsan` | 0 | **36 passed, 0 failed** |
| Fail closed: production mode PLC | `g++ -std=c++20 -DAOI_SIM_ONLY=0 -Wall -Wextra -Werror tests/unit/plc/fins_read_preflight_test.cpp -o /mnt/data/aoi_github02/bin/must_not_build_plc` | **1 expected** | Compiler `#error FINS preflight fixture must be compiled only with AOI_SIM_ONLY=1` |
| Fail closed: production mode capture | `g++ -std=c++20 -DAOI_SIM_ONLY=0 -Wall -Wextra -Werror tests/unit/acquisition/capture_topology_preflight_test.cpp -o /mnt/data/aoi_github02/bin/must_not_build_capture` | **1 expected** | Compiler `#error Capture preflight fixture must be compiled only with AOI_SIM_ONLY=1` |
| Legacy static PLC checks | `python3 -m unittest discover -s tests/unit/plc -p test_preflight_contracts.py -v` | 0 | **9/9 PASS** (old fixture, not committed) |
| Legacy static acquisition checks | `python3 -m unittest discover -s tests/unit/acquisition -p test_preflight_mapping.py -v` | 0 | **5/5 PASS** (old fixture, not committed) |
| Mutation: enable PLC production writing in copy | same PLC Python command, after changing *temporary copy* first signal `ProductionWriteEnabled=TRUE` | **1 expected** | 1 assertion failure |
| Mutation: corrupt camera mapping in copy | same acquisition Python command, after changing *temporary copy* channel CH09 device identity | **1 expected** | 2 assertion failures |
| Python bytecode | `python3 -m py_compile tests/unit/plc/test_preflight_contracts.py tests/unit/acquisition/test_preflight_mapping.py` | 0 | PASS |

## Confirmed file SHA256 (pre-commit locally tested exact bytes)

- `libs/device/prep/capture_topology_preflight.hpp`: `d57cfd4bf865b2282e65a1ef2c7a60eb4928686458ad1a35bbbf6fbc528f8323`
- `libs/plc/prep/fins_read_preflight.hpp`: `38ea63f24979b7c242d5807aacf15c5cb55993cdf1ae2abefa6cfd419f8c606e`
- `tests/unit/plc/fins_read_preflight_test.cpp`: `d0f224d5437b0a59f24c4f3506519bdef8888a1ed781181caff6008908932645`
- `tests/unit/acquisition/capture_topology_preflight_test.cpp`: `be6769b0f0e5a5004164fd490d7b92e4c97e014a1ac6dfba50e1e899ec57e928`
- `libs/plc/prep/ISSUE16_PREP_SCOPE.md`: `b515c2caa10c50f584d9053468a763c08a584df2fbdcc06f77d75ab2523f735f`

Original reference **Git blobs**: `plc_signal_registry.csv=1d339f17270b6e2e64cf26e813a82ecae9a1e997`, `field_verification_gates.csv=fc96dbf514bc4be824b7804595ba72214c031fb6`, `station_channel_map.csv=9853a6475b8f51c8ae8f3763144f7fe02e959d8b`.

**Round 1 review:** Source/ownership/dependencies/immutable reference check. W002 DRAFT_PREPARED; C01 NOT_FROZEN. No new public contract or simulator server.

**Round 2 review:** GCC + Clang/UBSan, positive/negative packet validation, 9-to-11 topology, simulated parent consistency, production-mode compile denies, mutation negatives. No real UDP, PLC or camera operation.

**Blocking:** A10 independent rerun at exact head; W002, W013, W014, W017, W021 and related WBS dependencies require VERIFIED; A0 C01 freeze, field FG remains pending. Do not merge. Rollback: close Draft PR/delete its isolated branch, without modifying main.
