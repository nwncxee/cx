#!/usr/bin/env bash
# The GPU goldens run in the fast path (CX-630, E1 and E2). Heavy -- an IL compile and two boots -- so build.sh does not
# run it; run it by hand inside nix develop. E1 runs only on a machine with a Vulkan GPU and says it was not run on one
# without. E2 runs everywhere: it hides the driver from the loader (VK_ICD_FILENAMES, the loader's own setting), as
# CI's runner has none. Oracle: vulkaninfo's own device list, read with the dev shell's library path taken out, and
# the two suites' SKIPPED reasons at main 9e89af8e. Exit 0 PASS, 1 FAIL, 2 setup.
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 2
rc=0
tmp=$(mktemp -d "${TMPDIR:-/tmp}/cx-gpu-examples.XXXXXX")
trap 'rm -rf "$tmp"' EXIT
fail() { echo "!! $1"; rc=1; }
command -v dotnet >/dev/null || { echo ">> run this inside nix develop" >&2; exit 2; }
tools/selfcheck/run.sh --out "$tmp/build" > "$tmp/compile.out" 2>&1 || [ $? = 1 ] || { echo ">> the fast path does not build" >&2; exit 2; }

# E1
# Given a machine with a Vulkan device
# When the fast self-check command runs inside nix develop
# Then GpuGolden and VulkanToolchain run and pass, and the SKIPPED count is 0
vi=$(command -v vulkaninfo)
devices=$([ -n "$vi" ] && env -u LD_LIBRARY_PATH -u VK_ICD_FILENAMES -u VK_DRIVER_FILES "$vi" --summary 2>/dev/null)
if grep -q 'deviceType *= *PHYSICAL_DEVICE_TYPE_\(DISCRETE\|INTEGRATED\)_GPU' <<< "$devices"; then
  tools/selfcheck/run.sh --out "$tmp/build" --no-build > "$tmp/e1.out" 2>&1 || fail "E1: the fast path failed with a device -- $(grep -E 'FAIL|SIG|exited' "$tmp/e1.out" | head -3)"
  cp "$tmp/build/cx.log" "$tmp/e1.log" 2>/dev/null
  grep -qE '^>> [0-9]+ SelfCheck lines, none failed, 0 SKIPPED\.' "$tmp/e1.out" || fail "E1: the verdict is '$(grep -E '^>> [0-9]+ SelfCheck' "$tmp/e1.out")', not 0 SKIPPED"
  for s in GpuGolden VulkanToolchain; do
    grep -q "\[cx\] $s\.SelfCheck SKIPPED" "$tmp/e1.log" 2>/dev/null && fail "E1: $s still SKIPS"
    grep -qE "\[cx\] $s\.SelfCheck.*passed" "$tmp/e1.log" 2>/dev/null || fail "E1: $s logs no passed line"
  done
else
  echo ">> E1 not run: vulkaninfo finds no Vulkan GPU on this machine"
fi

# E2
# Given a machine with no Vulkan device, such as CI's runner
# When the fast self-check command runs
# Then both still SKIP with their reason, as they do today
rm -f "$tmp/build/cx.log"
VK_ICD_FILENAMES=$tmp/no-driver.json tools/selfcheck/run.sh --out "$tmp/build" --no-build > "$tmp/e2.out" 2>&1 || fail "E2: the fast path failed with no device -- $(tail -3 "$tmp/e2.out")"
grep -qE '\[cx\] VulkanToolchain\.SelfCheck SKIPPED: .+' "$tmp/build/cx.log" || fail "E2: VulkanToolchain does not SKIP with a reason"
grep -qE '\[cx\] GpuGolden\.SelfCheck SKIPPED: no Vulkan device to run the goldens on' "$tmp/build/cx.log" || fail "E2: GpuGolden does not SKIP with its reason"

[ $rc = 0 ] && echo ">> GPU fast path approved examples: pass" || echo "!! GPU fast path approved examples: FAILED (above)" >&2
exit $rc
