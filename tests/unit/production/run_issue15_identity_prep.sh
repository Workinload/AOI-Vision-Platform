#!/usr/bin/env bash
# Agent01 Issue #15 SIM_ONLY standalone local preflight runner.
set -u -o pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
src="$root/tests/unit/production/issue15_identity_epoch_mock_test.cpp"
out="${1:-$(mktemp -d)}"
mkdir -p "$out"
manifest="$out/COMMANDS_AND_EXIT_CODES.tsv"
printf 'step\tactual_exit\texpected_exit\tverdict\n' > "$manifest"
failed=0
run() {
    local label="$1" expected="$2" log="$3"
    shift 3
    local rc
    printf 'COMMAND[%s]:' "$label"
    printf ' %q' "$@"
    printf '\n'
    if "$@" > "$out/$log" 2>&1; then rc=0; else rc=$?; fi
    local verdict=PASS
    if [[ "$expected" == nonzero ]]; then
       if [[ "$rc" -eq 0 ]]; then verdict=FAIL; fi
    elif [[ "$rc" -ne "$expected" ]]; then verdict=FAIL; fi
    printf '%s\t%s\t%s\t%s\n' "$label" "$rc" "$expected" "$verdict" >> "$manifest"
    printf 'RESULT[%s] exit=%s expected=%s verdict=%s\n' "$label" "$rc" "$expected" "$verdict"
    [[ "$verdict" == PASS ]] || failed=1
}
flags=(-std=c++20 -O0 -g -Wall -Wextra -Wpedantic -Werror -I "$root/libs/domain/include")
run gcc_build 0 gcc_build.log g++ "${flags[@]}" "$src" -o "$out/issue15_gcc"
if [[ -x "$out/issue15_gcc" ]]; then run gcc_16_cases 0 gcc_16_cases.log "$out/issue15_gcc"; else failed=1; fi
if command -v clang++ >/dev/null 2>&1; then
    run clang_build 0 clang_build.log clang++ -std=c++20 -O2 -Wall -Wextra -Wpedantic -Werror -I "$root/libs/domain/include" "$src" -o "$out/issue15_clang"
    if [[ -x "$out/issue15_clang" ]]; then run clang_16_cases 0 clang_16_cases.log "$out/issue15_clang"; else failed=1; fi
else
    printf 'clang\tNOT_VERIFIED\tNOT_VERIFIED\tSKIP\n' >> "$manifest"
fi
run ubsan_build 0 ubsan_build.log g++ "${flags[@]}" -fsanitize=undefined -fno-sanitize-recover=undefined "$src" -o "$out/issue15_ubsan"
if [[ -x "$out/issue15_ubsan" ]]; then run ubsan_16_cases 0 ubsan_16_cases.log "$out/issue15_ubsan"; else failed=1; fi
run mutate_epoch_build 0 mutate_epoch_build.log g++ "${flags[@]}" -DAOI_TEST_ONLY_MUTATE_EPOCH_GUARD=1 "$src" -o "$out/issue15_mutate_epoch"
if [[ -x "$out/issue15_mutate_epoch" ]]; then
    run mutate_epoch_detect nonzero mutate_epoch_detect.log "$out/issue15_mutate_epoch"
    grep -Fq 'FAIL old Epoch result quarantined after explicit reset' "$out/mutate_epoch_detect.log" || failed=1
else failed=1; fi
run mutate_workpiece_build 0 mutate_workpiece_build.log g++ "${flags[@]}" -DAOI_TEST_ONLY_MUTATE_WORKPIECE_GUARD=1 "$src" -o "$out/issue15_mutate_workpiece"
if [[ -x "$out/issue15_mutate_workpiece" ]]; then
    run mutate_workpiece_detect nonzero mutate_workpiece_detect.log "$out/issue15_mutate_workpiece"
    grep -Fq 'FAIL same barcode never chooses WorkpieceId' "$out/mutate_workpiece_detect.log" || failed=1
else failed=1; fi
{ printf 'gcc_version='; g++ --version | head -1; printf 'clang_version='; clang++ --version 2>/dev/null | head -1 || true; printf 'kernel='; uname -sr; printf 'cwd=%s\n' "$root"; printf 'sim_mode=SIM_ONLY; no device/network/db calls in fixture\n'; } > "$out/ENVIRONMENT.txt"
(cd "$out"; sha256sum COMMANDS_AND_EXIT_CODES.tsv ENVIRONMENT.txt *.log > SHA256SUMS.txt)
printf 'SOURCE_SHA256='; sha256sum "$src"
printf 'LOG_MANIFEST_SHA256='; sha256sum "$out/SHA256SUMS.txt"
if [[ "$failed" -ne 0 ]]; then echo PREP_FIXTURE_RESULT=FAIL; exit 1; fi
echo 'PREP_FIXTURE_RESULT=PASS; mutation runs deliberately exit nonzero'
