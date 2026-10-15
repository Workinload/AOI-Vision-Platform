# Issue #16 — Agent02 isolated PLC/FINS and 7+2 capture preparation

Status: **BRANCH_DEV / DRAFT_PR / NOT_INTEGRATION_READY / SIM_ONLY**.
These modules are **test-only preflight fixtures**; no FINS UDP server, PLC gateway,
PLC SignalRegistry freeze, hardware drivers, public C01/C02/C03 schema, or real
camera acquisition has been implemented.

## Provenance and no duplication

- Based on GitHub `main` commit `d81f5b3fd35dd44963c6c7464cdb3a6f5a124b7c` (verify again before PR).
- Existing legacy Agent02 M00-r001 preparation ZIP in `/AOI开发成果` was reviewed first.
  The existing `FINS_GOLDEN_VECTORS_DRAFT.json`, `test_preflight_contracts.py`
  and `test_preflight_mapping.py` are carried forward from that ZIP as
  **byte-identical historical draft fixtures**, not claimed as new implementation.
- New C++20 preflight files validate request/reply SID, length, route, count,
  end codes, deny all writes and all nonliteral-loopback targets; they create
  **no sockets**. Passing a parser test does not mean a network FINS stack works.
- The 7 area + 2 line mocks are an **in-memory static topology** matching
  immutable `reference/spec_v2_machine_readable/station_channel_map.csv`.
  They check CH08/09 and CH10/11 share simulated parents, but do not implement
  image acquisition or the 123MB line-scan stress test.

## Unchanged safety gates

W006 requires W002 VERIFIED, but ledger has W002=DRAFT_PREPARED.
W023 requires W013+W021; W024 requires W023; W025 requires W006+W023;
W028/W029 require W014+W017. None are VERIFIED. C01 remains NOT_FROZEN.
All 45 source PLC signals have `ProductionWriteEnabled=FALSE`; all 14 FG have
`ProductionWriteAllowed=FALSE` (7 BLOCKED + 7 OPEN). D1000/D1030 XYU,
D1013 dual-use, D2000 barcode 10/20-WORD, and D1016/D2099 semantics remain
explicitly UNKNOWN/PENDING trusted field signoff.

## Local test invocation

From repository root with C++20 compiler and Python3:

```sh
mkdir -p /tmp/aoi02-prep-test
c++ -std=c++20 -DAOI_SIM_ONLY=1 -Wall -Wextra -Werror -pedantic \
  tests/unit/plc/fins_read_preflight_test.cpp -o /tmp/aoi02-prep-test/plc
/tmp/aoi02-prep-test/plc
c++ -std=c++20 -DAOI_SIM_ONLY=1 -Wall -Wextra -Werror -pedantic \
  tests/unit/acquisition/capture_topology_preflight_test.cpp -o /tmp/aoi02-prep-test/capture
/tmp/aoi02-prep-test/capture
python3 -m unittest discover -s tests/unit/plc -p 'test_preflight_contracts.py' -v
python3 -m unittest discover -s tests/unit/acquisition -p 'test_preflight_mapping.py' -v
```

A10 must independently re-run on the exact Draft PR head and A0 must approve any
later integration **only after** WBS dependencies are VERIFIED and C01 is frozen.
