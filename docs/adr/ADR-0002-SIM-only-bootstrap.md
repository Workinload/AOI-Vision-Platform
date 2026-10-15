# ADR-0002 — M00 explicitly cannot control actual equipment

Date: 2026-10-15. Status: Accepted for bootstrap, pending A10 independent review. CMake aborts on `AOI_SIM_ONLY=OFF`; no production PLC address or driver link is compiled. vPLC listens only at local 127.0.0.1, with health-only protocol. GUI and Core IPC only permit health reads and deny all modifications. Site-based verification FG-01..FG-14 remains blocked/open and unmapped to production permission.

W023 must implement actual FINS/UDP carefully with independent safety tests; M00 vPLC health check shall **not** be described as FINS protocol pass.
