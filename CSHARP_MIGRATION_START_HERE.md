# AOI C# / .NET 10 / Avalonia migration entrypoint

> This branch is isolated from legacy C++ main. Do not merge without independent review.

- Master coordination and architecture decision: https://github.com/Workinload/AOI-Vision-Platform/issues/54
- A0 solution/contracts: #55
- Agent01 Core/PeerLink: #56
- Agent02 PLC/cameras/vPLC: #57
- Agent03 Vision/data/inference: #58
- Agent04 Avalonia UI: #59
- Agent05 CI/QA: #60
- Agent06 Solutions: #61
- Agent07 Diagnostics/behavior: #62

## Core decisions
C# .NET 10, Avalonia MVVM, OpenCvSharp5, SQLite WAL, library-first, Avalonia/Core separated by IPC, dual-Core five-process Windows target; deterministic SIM_ONLY and safety-first. v2 original business requirements preserved. No real PLC writes until field gates are separately verified.

## Verification status
Coordination only. The presence of this document does not imply the C# solution compiles or any tests pass. SDK 10.0.401 restored and ordinary C# build/run verified in this Linux container. Avalonia/OpenCvSharp5 native UI on Linux and Windows require separate restore/build/run proofs.

## Final UI decision
Avalonia 12.1.4 MVVM is the sole formal UI. The old WPF migration branch is obsolete. The stable cross-platform Core remains independent of UI. Use mature NuGet, verify full transitive offline packages and safety constraints.
