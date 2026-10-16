# A0 independent execution evidence — 2026-10-17 SIM_LIGHT

Environment: Linux x86-64 Debian 13 container, .NET SDK 10.0.401 / runtime 10.0.12; offline user NuGet feed 227 packages. Materialized SDK ZIP SHA256 `d7348a7bae686419901bb9520fbdd32d19a2c58e1054efaccb1168962a371333`, NuGet ZIP SHA256 `15158edd29f06547dc09621d0efbf59dfd4f174edbd8a597ba29226a457eabd0`.

1. `dotnet restore csharp/AOI.slnx --source /local/feed -p:NuGetAudit=false` — **PASS** after using `NUGET_CLI_CERT_REVOCATION_MODE=offline`. No NuGet.org fallback.
2. `dotnet build csharp/AOI.slnx --no-restore -c Debug` — **PASS, 0 errors, 0 warnings**, seven real C# projects.
3. `dotnet test csharp/tests/AOI.Tests/AOI.Tests.csproj --no-build -c Debug` — **PASS 8/8**, including native OpenCV pixels and SQLite WAL.
4. `python3 csharp/scripts/e2e_sim_light.py --dotnet /local/dotnet` — **PASS**: five OS processes launched; loopback virtual FINS/UDP, real TCP PeerLink, independent SQLite WAL and SHA256/outbox/inbox persisted ACK; CH02+CH06 synthetic OpenCV channels; Master simulated final = GOOD (marked SIM_ONLY), vPLC D1016=1 then D2099=2; two independent Avalonia Headless UIs obtained live IPC snapshots.
5. No Windows native run, real PLC link, field handshake validation, 11 real camera frames or entire 114-TC suite was performed. All remain **NOT_VERIFIED**.

**Limits:** one synthetic workpiece, two synthetic area-camera channels; end-barcode echo from synthetic source, not a true separate end-station read; no production inference; TCP/IPC unsecured beyond loopback isolation; runtime modules are the first vertical slice to be replaced/extended.
