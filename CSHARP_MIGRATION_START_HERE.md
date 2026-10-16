# AOI C# / .NET 10 / WPF migration entrypoint

> This branch is isolated from legacy C++ main. Do not merge without independent review.

- Master coordination and architecture decision: https://github.com/Workinload/AOI-Vision-Platform/issues/54
- A0 solution/contracts: #55
- Agent01 Core/PeerLink: #56
- Agent02 PLC/cameras/vPLC: #57
- Agent03 Vision/data/inference: #58
- Agent04 WPF UI: #59
- Agent05 CI/QA: #60
- Agent06 Solutions: #61
- Agent07 Diagnostics/behavior: #62

## Core decisions
C# .NET 10, WPF MVVM, OpenCvSharp5, SQLite WAL, library-first, WPF/Core separated by IPC, dual-Core five-process Windows target; deterministic SIM_ONLY and safety-first. v2 original business requirements preserved. No real PLC writes until field gates are separately verified.

## Verification status
Coordination only. The presence of this document does not imply the C# solution compiles or any tests pass. In this execution environment, dotnet SDK was not found. Windows WPF native verification must be performed in an eligible Windows environment or CI.
