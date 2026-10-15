# W008 exact-tree owner retest / Issue #37

- **A9 owner / SIM_ONLY**; security threat model and full adversarial harness retained. W002 VERIFIED solely for legacy static facts, 14 FG FIELD_PENDING. A10 independent review and A0 merge/acceptance still **PENDING**.
- **Tested PR #41 source blob integrity** (GitHub + local `git hash-object`):
  - `libs/auth/sim_only_policy.hpp`: Git blob `e9386f001180ee12649768cae55b13e152460f9c`; SHA256 `ddc70dcf213ee2c929032facbbd2531c56b0fdbc3ba2763513b615f287a3db8f`
  - `tests/unit/security/w008_sim_only_policy_test.cpp`: blob `c2c585ca7413658a21390aea76ceac1e8b3daf46`; SHA256 `3a6bc01f19f8a64835e0549f65d8d7744d167f247e6b272ca01ca7b0e5d57709`
  - `tests/unit/security/run_w008_local.sh`: blob `fe6fc23f69cba9e4ff8caf5a503edd1d88288d54`; SHA256 `01af626e621ce32cf6f7535c07b1ba4fe367b3afcb60e6286dc6cfb5c2fb0c4d`
- **Reproduction** from repository checkout (runner accepts explicit output directory, does not connect any socket):
  ```bash
  bash tests/unit/security/run_w008_local.sh /tmp/aoi-w008-evidence
  ```
  GCC `g++ (Debian 14.2.0-19) 14.2.0`; compiler flags debug `-std=c++20 -Wall -Wextra -Werror -pedantic -O0 -g`; optimized `-O2 -g -fsanitize=undefined -fno-sanitize-recover=all`.
- **Result**: script EXIT **0**; Debug compile EXIT 0, run EXIT 0, **76/76**; Optimized/UBSan compile EXIT 0, run EXIT 0, **76/76**; both logs end `W008_RESULT total=76 passed=76 failed=0 backend=MEMORY_ONLY external_io=NONE`.
- Debug and UBSan stdout SHA256 (each): `9820515d79c6e718b9e458549ff228e4f49f4ea9119b118c759d8f5011d04cc6`; build stderr each empty SHA256 `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`; local terminal `aaccacca77828bfdc900fa7119bc0c8e87b872488299abe3386e8392957e1cab`.
- 76 parametrized test cases cover strict endpoint identity/host/port, 500 deterministic endpoint mutation probes, unknown legacy aliases, physical write even admin, untrusted claimed role, unsigned site permits, offline, time expiry from trusted mock clock, stale readback, nonce/message replay/digest clash, unavailable and tampered in-memory audit, alarm ACK != CLEAR / stale/unauthorized clear.
- **Security limit**: all assertions apply to a C++20 injectable memory-only test fixture. The check of symbol table is not OS network sandbox proof; real PLC/camera hardware excluded by source only in this fixture. Durable W019 audit/actual backend auth W089/W021 egress/W026 FINS gateway/C01 contracts and all FIELD gates are not VERIFIED. No production I/O or UI integration claimed.
- **Reviewer action**: Agent05/A10 must rebuild exact PR head and inspect failure injection; only A0 may merge after required gate decision.
