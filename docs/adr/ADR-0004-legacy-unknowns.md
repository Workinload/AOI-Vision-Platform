# ADR-0004 · Historical/static evidence must not become machine authority

Status: Prepared for W002, pending A0/A10 review. Do NOT mark FIELD_ACCEPTED.

| Topic | Available static evidence | Not established without field readback | SIM policy |
|---|---|---|---|
| XYU result | Historic D1030/1032/1034 output vs D1000/1002/1004 PLC consumption and D1000/1001 alias | Current address, actual word format, ability to write | BLOCKED / FG-01 |
| D1013 | line capture result signal may alias another clear condition | Exact PLC clearing/edge behavior | BLOCKED / FG-02 |
| Barcode D2000 | old PC read 20 WORD, PLC wrote 10 WORD | Current barcode length and NUL/BA handling | BLOCKED / FG-03 |
| Last barcode source | D2100 role/source not confirmed | Which device actually supplies final barcode | BLOCKED / FG-04 |
| OK/NG route | D1016=1/2 references final quality | physical diverter NG branch not evidenced | BLOCKED / FG-05 |
| Product switch | historic master/slave changes asymmetric | current cycle safety & readback | BLOCKED / FG-06 |
| Trigger/UserSet | historic hard trigger & camera settings | installed device API/parameter readback | BLOCKED / FG-07 |

Simulation may emulate proposed timing only with clear `SIM_ONLY_SYNTHETIC` labels and HOLD on mismatch; do not imply current machine registers or hardware conditions were measured.
