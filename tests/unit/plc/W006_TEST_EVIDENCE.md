# Agent02 W006 self-test evidence — Draft / SIM_ONLY

Issue #36; Owner A2; W002=VERIFIED (scope: LEGACY_STATIC_FACTS_AND_UNCERTAINTY_REGISTER_ONLY) on main `cf11c7d436910c2d5da4566572b65c5d13c86fa4`. First code commit `3a81e07a01e15d6b1befa30eaa4c0a5e32c99558`.

## Source, behavior and release limits
Files: `libs/plc/include/aoi/plc/locked_signal_registry.hpp`, `libs/plc/src/locked_signal_registry.cpp`, `tests/unit/plc/w006_locked_registry_test.cpp`, `tests/unit/plc/w006_run.sh`. No modifications to source reference, baseline, A0 contract, main or FINS/camera mock. In-process C++20 strict CSV reader, pinned SHA256 for exact 45-signal / 14-gate v2 originals. Full 21/8 columns retained and read-only. Live field facts remain UNKNOWN. WritePolicy is a pure deny-only decision, with **no PLC socket or write gateway**. It must not be mistaken for W026 authorized transaction implementation.

## Actual local commands and exit codes
Commands run in local staging with byte-identical immutable v2 references (file SHA256 pinned to main BASELINE_LOCK). Direct `git clone` fails DNS resolution in this runtime; code was committed through authenticated GitHub connector; **complete main Qt/CTest CI on exact Git HEAD is NOT_VERIFIED**.

- `CXX=g++ AOI_W006_BUILD_DIR=/mnt/data/aoi_w006/bin sh tests/unit/plc/w006_run.sh` — first run **exit 1** (79 PASS / 1 FAIL). Identified incorrect `std::ifstream::eof()` requirement after iterator read. Corrected file import fail-closed check (must be !bad, nonempty, bounded); this failure was not concealed.
- Same command after fix — **exit 0**, 80 PASS / 0 FAIL (initial rerun). Another test was added before final commit.
- `CXX=g++ AOI_W006_BUILD_DIR=/mnt/data/aoi_w006/bin/gcc_second sh tests/unit/plc/w006_run.sh` — **exit 0**, **81 PASS / 0 FAIL**.
- `CXX=clang++ AOI_W006_BUILD_DIR=/mnt/data/aoi_w006/bin/clang sh tests/unit/plc/w006_run.sh` — **exit 0**, **81 PASS / 0 FAIL**.
- `g++ -std=c++20 -DAOI_SIM_ONLY=0 -Wall -Wextra -Werror -pedantic -Ilibs/plc/include -c libs/plc/src/locked_signal_registry.cpp -o /mnt/data/aoi_w006/bin/forbidden.o` — **exit 1 expected**; compilation explicitly rejects non-SIM_ONLY.
- `git hash-object reference/spec_v2_machine_readable/plc_signal_registry.csv` — `1d339f17270b6e2e64cf26e813a82ecae9a1e997` matches GitHub source blob; SHA256 `97ff0003f2416a1dacf86fecea18ef80ec45fd5f016526333725e0c977e3fee8`.
- `git hash-object reference/spec_v2_machine_readable/field_verification_gates.csv` — `fc96dbf514bc4be824b7804595ba72214c031fb6` matches GitHub source blob; SHA256 `c56d84b46176ca86afcb11088398005f6af86b7fc85d5ce0f9bf7a295871b6de`.

## Test coverage (81 assertions each GCC and Clang)
45/45 unique facts, 14/14 unsigned gates, 7 BLOCKED + 7 OPEN, no production write flags; full original 21/8 field capture; static UNKNOWN preserved for D1013, D1000/D1030, D2000 10-vs-20-word, D1016/D2099; mutation negatives (unknown/missing/duplicate IDs, modified address, invalid data types, write flag flips, signatures, FG status and byte checksums), malformed/absent input. WritePolicy refuses ADMIN/ROOT, stale/missing approvals, physical/non-loopback/ambiguous endpoint, unknown signals and all ostensibly approved loopback writes; no grant branch.

## Source SHA256 (exact local final bytes before Git commit)
- `libs/plc/include/aoi/plc/locked_signal_registry.hpp`: `0d02ecc1ece7a58032729e8291327d8602cef1c132f1856e5b32771d87399e70`
- `libs/plc/src/locked_signal_registry.cpp`: `19b115386a6fcf472b3cef104c992fdbe79978d0cab9c58af442465804685767`
- `tests/unit/plc/w006_locked_registry_test.cpp`: `1e1e60643038f5fc9354a6292954435673dfb4ac4cff9492ed47bd4fef1e5842`
- `tests/unit/plc/w006_run.sh`: `3de54b4cc60e441dbe19a3049a089d845284e1432a58b05b2e64f489933405d3`

## Independent gate
A10 must clone/fetch **exact PR head**, compile, re-run positive/negative tests twice, independently verify all source blobs and provenance, check no production device connection possible, then submit PASS/HOLD. A0 decides whether W006 is accepted/merged and modifies TASK_LEDGER exclusively. All 14 FG remain FIELD_PENDING; C01 remains separate NOT_FROZEN and this file does not assert full business runtime, W025 or W026.
