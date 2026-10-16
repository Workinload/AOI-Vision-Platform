#!/bin/sh
set -eu
if [ "$#" -ne 1 ]; then echo 'Usage: scripts/build_offline.sh /path/to/offline-feed' >&2; exit 2; fi
unset PLATFORM
export NUGET_CLI_CERT_REVOCATION_MODE=offline
export DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$(dirname "$0")/.."
dotnet restore AOI.slnx --source "$1" -p:NuGetAudit=false
dotnet build AOI.slnx --no-restore -c Debug
dotnet test tests/AOI.Tests/AOI.Tests.csproj --no-build -c Debug
python3 scripts/e2e_sim_light.py
