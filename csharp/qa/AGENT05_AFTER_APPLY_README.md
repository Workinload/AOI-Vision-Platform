# Agent05 P0 — independent after-applied FINS ACK-loss / restart evidence

**Ownership:** `csharp/qa/**` only. This is a QA-only increment for existing
Draft PR #120 (`agent/05/p0-fins-ack-loss-recovery-20261017`), **not** a new
PLC driver or a substitute for A0/A1/A2/A7 production integration.

## What is verified

Run this **after** the real five-process source script
`csharp/qa/e2e_terminal_ack_fault.py` (already present in PR #120) completes
and writes its `evidence-root` folder. That source script starts vPLC,
Master Core, Slave Core, Master Avalonia UI and Slave Avalonia UI; injects a
specific terminal FINS fault in the external localhost A2 shadow; independently
queries the PLC with real FINS READs after the fault; probes Master restart;
and writes `master-restart-probe.log` plus
`a5-terminal-fault-evidence.json`.

The additional **read-only** verifier here independently parses the vPLC
*raw* FINS UDP request/reply hex, including exact SID, swapped node routing,
opcode, DM address, payload, end-code, transaction number, UTC and monotonic
order. It requires:

- For healthy workpieces 1 and 2: genuine D1016=2 followed by D2099=2,
  with the first independent readback after each ACK, plus real D4003 ejection.
- For held workpiece 3: durable Master SQLite `workpiece_holds` before the
  first terminal output, no Final, 33 Master/18 Slave PNG-derived channels,
  and a genuine D4303 end scanner offer, D2099 request and D2100 barcode read.
- For `ack-first`: real D2099=3/4 applied, but the raw UDP reply is absent,
  the Master records **zero** confirmed output steps and sends no D1016.
- For `ack-second`: D2099=3/4 has ACK + independent readback, D1016=2 is
  applied with missing response, and the Master records **one** confirmed step.
- For `reject-first`: the PLC actually responds with FINS end-code 0x0005,
  and the first D2099 write must not change the physical end-scanner state.
- For all faults: new actual FINS/UDP READs after the fault prove D2099/D1016
  values; a checksum-free FINS/UDP response with wrong SID is not accepted.
  No duplicate terminal write, D1016=1 GOOD, or token3 D4003 physical exit.
- By default: a separate Master restart probe log contains
  `SIM_FULL_RESTART_HOLD` and the real driver manifest confirms its completed
  non-replay probe. The manifest is **driver attestation**, not cryptographic
  proof of process execution. Observed raw UDP writes are verified separately.

### Developer commands

```bash
cd csharp
python -m unittest discover -s qa -p 'test_p0_after_apply_wire_reconcile.py' -v

# After e2e_terminal_ack_fault.py created this folder:
python qa/p0_after_apply_wire_reconcile.py \
    --evidence-root .artifacts/a5-ack-first-mismatch \
    --scenario mismatch --fault ack-first \
    --report .artifacts/a5-ack-first-mismatch/a5-after-apply-review.json
```

Similarly use:

```
--evidence-root .artifacts/a5-ack-second-mismatch --scenario mismatch --fault ack-second
--evidence-root .artifacts/a5-scanner-unreadable-reject --scenario unreadable --fault reject-first
```

Suggested **A5-owned** dedicated workflow steps, following the real
`e2e_terminal_ack_fault.py` run for each case:

```yaml
- name: A5 independently reconcile first applied write with lost ACK
  shell: bash
  run: >-
    python qa/p0_after_apply_wire_reconcile.py
    --evidence-root .artifacts/a5-ack-first-mismatch
    --scenario mismatch --fault ack-first
- name: A5 independently reconcile second applied write with lost ACK
  shell: bash
  run: >-
    python qa/p0_after_apply_wire_reconcile.py
    --evidence-root .artifacts/a5-ack-second-mismatch
    --scenario mismatch --fault ack-second
- name: A5 independently reconcile unreadable scanner FINS refusal
  shell: bash
  run: >-
    python qa/p0_after_apply_wire_reconcile.py
    --evidence-root .artifacts/a5-scanner-unreadable-reject
    --scenario unreadable --fault reject-first
```

## Acceptance semantics and limitations

Exit `0` = observed raw-wire/WAL/restart-attestation consistency under
**SIM_ONLY**; exit `1` = observed unsafe/contradictory data; exit `3` =
`NOT_VERIFIED` because actual raw wire/PLC readbacks or restart proof is absent.
`--wire-only` is for diagnostics and deliberately sets `restartEvidence=false`.

The `14` included standard-library unittest cases are **synthetic test
fixtures**, useful only to validate this *auditor*. They are not real AOI
five-process proof. A real Host run of the new audit is **NOT_VERIFIED** in
this local offline copy. No OEM timing, nodes, signal interpretation, field
safety validation, or REAL PLC write enablement is implied.

For production reconciliation after process restart, A0 must separately
integrate A1's persisted output intent + A7 final-output evidence before
asserting the complete product safety closure. This A5 audit does not write
an operator-unlock flag, mutate any WAL, or replay any UDP WRITE.
