# W008 A9 reproducible local execution evidence (Issue #37)

**Claim**: W008 attack model + memory-only fail-closed guard unit suite built and executed. **NOT** C01-frozen ABI, W021 OS outbound isolation, W026 PLC gateway, W089 RBAC, W019 durable audit, W092 production alarm lifecycle, or hardware verification.

- Source base: `Workinload/AOI-Vision-Platform/main` commit `cf11c7d436910c2d5da4566572b65c5d13c86fa4`.
- W002: official VERIFIED only for locked legacy static facts per `docs/agents/TASK_LEDGER.json` on this main.
- C01: NOT_FROZEN; fourteen FIELD gates PENDING; no physical PLC/camera processes/ports accessed.
- Compiler: `g++ (Debian 14.2.0-19) 14.2.0`; language C++20.

## Real commands / captured exits

Execute from the repository root, using only A9 test files:

```bash
bash tests/unit/security/run_w008_local.sh /tmp/aoi-w008-review
```

The script issues and logs the real compiler and runner commands:

```bash
g++ -std=c++20 -Wall -Wextra -Werror -pedantic -O0 -g tests/unit/security/w008_sim_only_policy_test.cpp -o /tmp/aoi-w008-review/w008_debug
timeout 30 /tmp/aoi-w008-review/w008_debug
g++ -std=c++20 -Wall -Wextra -Werror -pedantic -O2 -g -fsanitize=undefined -fno-sanitize-recover=all tests/unit/security/w008_sim_only_policy_test.cpp -o /tmp/aoi-w008-review/w008_opt-ubsan
timeout 30 /tmp/aoi-w008-review/w008_opt-ubsan
```

Actual local run: `bash tests/unit/security/run_w008_local.sh /mnt/data/AOI_Agent04_W008_20261016/evidence/two_rounds_final` -> **exit 0**. Debug compile **exit 0**; Debug execute **exit 0**, `76 passed / 0 failed`. Optimized + UBSan compile **exit 0**; optimized + UBSan execute **exit 0**, `76 passed / 0 failed` with no UBSan diagnostics. The two independently compiled binaries print `backend=MEMORY_ONLY external_io=NONE` because their only write target is an in-memory mock counter. This is not an OS-level sandbox proof.

### Exact SHA256 of submitted source and full stdout case logs

| Path | SHA256 |
| --- | --- |
| `libs/auth/sim_only_policy.hpp` | `ddc70dcf213ee2c929032facbbd2531c56b0fdbc3ba2763513b615f287a3db8f` |
| `libs/auth/W008_SIM_ONLY_THREAT_MODEL.md` | `fbb2acec31d10410c05d3483d4795a12967c8149d9c9c6ce2c30a58179c498ed` |
| `tests/unit/security/w008_sim_only_policy_test.cpp` | `3a6bc01f19f8a64835e0549f65d8d7744d167f247e6b272ca01ca7b0e5d57709` |
| `tests/unit/security/run_w008_local.sh` | `484dd86053d3aef4fb7f9e95327dbce42ba90f62bc0debf2cb4dc05559a9d54c` |
| `tests/unit/security/evidence/W008_debug_76cases.log` | `9820515d79c6e718b9e458549ff228e4f49f4ea9119b118c759d8f5011d04cc6` |
| `tests/unit/security/evidence/W008_ubsan_76cases.log` | `9820515d79c6e718b9e458549ff228e4f49f4ea9119b118c759d8f5011d04cc6` |

`git hash-object` of source on disk: `sim_only_policy.hpp` `e9386f001180ee12649768cae55b13e152460f9c`; test cpp `c2c585ca7413658a21390aea76ceac1e8b3daf46`; run script `5c8e459542870f5254feda53355e3596e9792601`.

Source bytes must be compared with GitHub's **actual PR HEAD** blobs after push. A10 must rerun from exact PR SHA rather than trusting local tests or this report alone.

## Negative and positive coverage

76 independent case names in both committed logs. Denies: unknown port/IP/DNS/IPv6 alias, forged physical IO, historical D1000/D1013/D1030/D2000/D1016/D2099, admin bypass and claimed field permit, anonymous/revoked/expired, forged UI role, core/device disconnected, stale/future epoch, duplicate message/different payload hash and nonce, bad trace or hash, stale readback, audit unavailable or tampered, repeated ACK/CLEAR and CLEAR while active. Positive memory-only allowed SIM write, authenticated read, ACK-only, and CLEAR after verified mock restoration.

Additional best-effort static binary inspection: `nm -u w008_debug | grep -E ' (socket|connect|sendto|recvfrom|bind|listen)@'` found no matching imports (`grep` **exit 1**, as expected). `strace` is **not installed**, so there is **no traced syscall evidence**, and OS-level egress prevention remains **NOT_VERIFIED**.

## Review/merge authority

Task `W008` depends only on W002, now VERIFIED for source provenance. Independent A10 review of exact HEAD and A0 signed status update remain required before W008 VERIFIED. This PR is Draft; no WBS status mutation, shared DTO change, C01 freeze, or main merge authorized. Backout: close Draft and delete isolated branch; production Core/PLC behavior unchanged.
