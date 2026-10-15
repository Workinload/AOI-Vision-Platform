#!/bin/sh
# Run from repo root. Evidence generation is local only. No camera/PLC/UDP.
set -eu
: "${CXX:=c++}"
: "${AOI_W006_BUILD_DIR:=/tmp/aoi-w006}"
mkdir -p "$AOI_W006_BUILD_DIR"
"$CXX" -std=c++20 -DAOI_SIM_ONLY=1 -O1 -g -Wall -Wextra -Werror -pedantic \
  -Ilibs/plc/include libs/plc/src/locked_signal_registry.cpp \
  tests/unit/plc/w006_locked_registry_test.cpp -o "$AOI_W006_BUILD_DIR/w006_registry_test"
"$AOI_W006_BUILD_DIR/w006_registry_test" \
  reference/spec_v2_machine_readable/plc_signal_registry.csv \
  reference/spec_v2_machine_readable/field_verification_gates.csv
