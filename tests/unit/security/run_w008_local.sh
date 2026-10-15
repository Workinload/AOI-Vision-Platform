#!/usr/bin/env bash
# W008: two isolated profiles; no PLC/camera/socket construction.
set -euo pipefail
cd "$(dirname "$0")/../../.."
out="$1"
mkdir -p "$out"
g++ --version | head -n 1 > "$out/toolchain.txt"
sha256sum libs/auth/sim_only_policy.hpp tests/unit/security/w008_sim_only_policy_test.cpp tests/unit/security/run_w008_local.sh > "$out/source.sha256"
g++ -std=c++20 -Wall -Wextra -Werror -pedantic -O0 -g tests/unit/security/w008_sim_only_policy_test.cpp -o "$out/w008_debug" > "$out/build_debug.log" 2>&1
echo "BUILD_DEBUG_EXIT=0" > "$out/exits.txt"
timeout 30 "$out/w008_debug" > "$out/test_debug.log" 2>&1
echo "TEST_DEBUG_EXIT=0" >> "$out/exits.txt"
grep -q '^W008_RESULT total=76 passed=76 failed=0 backend=MEMORY_ONLY external_io=NONE$' "$out/test_debug.log"
g++ -std=c++20 -Wall -Wextra -Werror -pedantic -O2 -g -fsanitize=undefined -fno-sanitize-recover=all tests/unit/security/w008_sim_only_policy_test.cpp -o "$out/w008_ubsan" > "$out/build_ubsan.log" 2>&1
echo "BUILD_UBSAN_EXIT=0" >> "$out/exits.txt"
timeout 30 "$out/w008_ubsan" > "$out/test_ubsan.log" 2>&1
echo "TEST_UBSAN_EXIT=0" >> "$out/exits.txt"
grep -q '^W008_RESULT total=76 passed=76 failed=0 backend=MEMORY_ONLY external_io=NONE$' "$out/test_ubsan.log"
sha256sum "$out"/build_*.log "$out"/test_*.log > "$out/evidence.sha256"
printf 'W008_ALL_76_TWO_PROFILES_PASS\n'
