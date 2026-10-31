#!/usr/bin/env bash
# Runs the GPU goldens (run-demo.sh) twice under VK_LAYER_KHRONOS_validation and fails on any message.
#
#   pass A  core + synchronization validation -- the commands CX records: a missing barrier, a wait nothing
#           signals, a layout nobody transitioned.
#   pass B  core + GPU-assisted validation -- what the SHADERS read at runtime: a bindless index past the end
#           of the descriptor array, a descriptor whose image was destroyed, an out-of-bounds buffer read.
#           That is the crash class NVIDIA reads quietly and AMD takes the device down for.
#
# Run it OUTSIDE nix develop: inside, the stub host finds no ICD and every golden SKIPs (which is not a pass).
# run-demo.sh sends the host's stdout to /dev/null, so the layer must log to a file -- hence a settings file
# rather than the default stdout action. Settings go through vk_layer_settings.txt, and every key written is
# checked against the layer's own manifest first: the GPU-AV key names have changed across layer versions, so
# a typo would read as a clean run.
#
# Usage: tools/vk/validate.sh [--out DIR] [--pass a|b|both]
#   --out    where the settings files, layer logs and demo output land (default: a temp dir, kept)
#   --pass   which pass to run (default: both)
# CX_VK_LAYER_DIR overrides the explicit_layer.d the layer is loaded from; by default it comes from the
# repository's own pinned nixpkgs, so the layer version moves with flake.lock and nothing else.
#
# The same settings drive the live harness. tools/live/perf-save.sh passes the environment through, so a
# manual area-transition run under GPU-AV -- the churn the goldens do not cover -- is the same four variables:
#   DISABLE_GAMESCOPE_WSI=1 VK_ADD_LAYER_PATH=<explicit_layer.d> \
#   VK_INSTANCE_LAYERS=VK_LAYER_KHRONOS_validation VK_LAYER_SETTINGS_PATH=<DIR>/pass-b/vk_layer_settings.txt \
#   tools/live/perf-save.sh --saves <copy of a quicksave> ...
# with log_filename edited to an absolute path of your own. Count only messages after "VkBackend: LIVE".
set -u
cd "$(dirname "$0")/../.."
REPO=$PWD

out=""; which_pass=both
while [ $# -gt 0 ]; do
  case "$1" in
    --out) out=$2; shift 2 ;;
    --pass) which_pass=$2; shift 2 ;;
    -h|--help) sed -n '2,30p' "$0"; exit 0 ;;
    *) echo ">> unknown argument: $1" >&2; exit 2 ;;
  esac
done

[ -n "${IN_NIX_SHELL:-}" ] && {
  echo ">> refusing: this is a nix develop shell, where the goldens SKIP (no ICD). Run it outside nix." >&2
  exit 2; }
[ -f cx-linux-x64/cx_managed.so ] || { echo ">> refusing: nothing staged -- build first (nix develop -c ./build.sh)" >&2; exit 2; }
# A live client shares the GPU, which costs time and nothing else: this run builds nothing and writes its log
# and its HOME under --out, so the session's own cx.log is never touched.
pgrep -x nwmain-linux >/dev/null 2>&1 && echo ">> note: a client session is running and shares the GPU; the run will be slower"

[ -n "$out" ] || out=$(mktemp -d "${TMPDIR:-/tmp}/cx-vk-validate.XXXXXX")
mkdir -p "$out"
echo ">> work dir: $out"

# The layer is on no default loader path. Take it from the repository's pinned nixpkgs so the version is
# flake.lock's, not whatever the machine happens to have installed.
if [ -n "${CX_VK_LAYER_DIR:-}" ]; then
  layer_dir=$CX_VK_LAYER_DIR
else
  store=$(nix build --no-link --print-out-paths --inputs-from "$REPO" 'nixpkgs#vulkan-validation-layers') || {
    echo ">> could not build the pinned vulkan-validation-layers; set CX_VK_LAYER_DIR to an explicit_layer.d" >&2
    exit 2; }
  layer_dir=$store/share/vulkan/explicit_layer.d
fi
manifest=$layer_dir/VkLayer_khronos_validation.json
[ -f "$manifest" ] || { echo ">> no VkLayer_khronos_validation.json under $layer_dir" >&2; exit 2; }
echo ">> layer: $(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["layer"]["api_version"])' "$manifest") at $layer_dir"

# Keys common to both passes. debug_action + log_filename are what put the messages in a file at all, and
# `info` is in report_flags for one message only: the layer's "Khronos Validation Layer Active" notice, which
# names the features it turned on. That notice is the proof the settings file was read -- see tools/vk/report.py.
common="debug_action = VK_DBG_LAYER_ACTION_LOG_MSG
report_flags = error,warn,info
enable_message_limit = false"

pass_a="validate_core = true
validate_sync = true
syncval_shader_accesses_heuristic = true
$common"
expect_a=VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION_EXT

# gpuav_enable instruments the shaders; the three checks under it are the descriptor and buffer ones. There is
# no reserve-binding-slot setting any more: GPU-AV takes a descriptor set of its own, and CX's pipeline layout
# uses sets 0 and 1 (VulkanToolchain.BindlessSet), so one is free on any device worth running. Sync validation
# is off here -- GPU-AV is slow and the two together say nothing the separate passes do not.
pass_b="validate_core = true
validate_sync = false
gpuav_enable = true
gpuav_descriptor_checks = true
gpuav_post_process_descriptor_indexing = true
gpuav_buffers_validation = true
$common"
expect_b=VK_VALIDATION_FEATURE_ENABLE_GPU_ASSISTED_EXT

# Every key written has to be one the installed layer declares, or a clean log would mean "misspelt", not "no
# findings". The manifest lists them all under its settings tree.
keys_ok() {
  python3 -I - "$manifest" "$1" <<'EOF'
import json, re, sys
seen = set()
def walk(o):
    if isinstance(o, dict):
        if isinstance(o.get('key'), str): seen.add(o['key'])
        for v in o.values(): walk(v)
    elif isinstance(o, list):
        for v in o: walk(v)
walk(json.load(open(sys.argv[1]))['layer'])
bad = [k for k in re.findall(r'^(\w+) =', sys.argv[2], re.M) if k not in seen]
if bad:
    print('!! this layer declares no setting named: ' + ', '.join(bad), file=sys.stderr)
    sys.exit(1)
EOF
}

run_pass() {
  name=$1; settings=$2; expect=$3
  dir=$out/pass-$name
  mkdir -p "$dir/tmp"
  log=$dir/validation.log
  rm -f "$log"
  keys_ok "$settings" || return 1
  { echo "$settings"; echo "log_filename = $log"; } | sed 's/^/khronos_validation./' > "$dir/vk_layer_settings.txt"

  echo
  echo ">> pass $name: $(echo "$settings" | tr '\n' ' ')"
  # DISABLE_GAMESCOPE_WSI: gamescope's WSI layer injects extensions into our device and logs errors that are
  # not ours. The VK_KHRONOS_VALIDATION_* environment variables override the settings file, so they go.
  env -u VK_KHRONOS_VALIDATION_DEBUG_ACTION -u VK_KHRONOS_VALIDATION_LOG_FILENAME \
      -u VK_KHRONOS_VALIDATION_VALIDATE_SYNC -u VK_KHRONOS_VALIDATION_VALIDATE_CORE \
      -u VK_KHRONOS_VALIDATION_GPUAV_ENABLE -u VK_KHRONOS_VALIDATION_REPORT_FLAGS \
      DISABLE_GAMESCOPE_WSI=1 \
      VK_ADD_LAYER_PATH="$layer_dir" \
      VK_INSTANCE_LAYERS=VK_LAYER_KHRONOS_validation \
      VK_LAYER_SETTINGS_PATH="$dir/vk_layer_settings.txt" \
      CX_NO_BUILD=1 TMPDIR="$dir/tmp" \
      ./run-demo.sh > "$dir/demo.log" 2>&1
  demo_rc=$?
  echo ">>   run-demo exit $demo_rc, output $dir/demo.log"

  demo_cx=$dir/tmp/cx-demo/cx.log
  goldens=$(grep -c 'GpuGolden' "$demo_cx" 2>/dev/null); goldens=${goldens:-0}
  skipped=$(grep -c 'GpuGolden.*SKIPPED' "$demo_cx" 2>/dev/null); skipped=${skipped:-0}
  echo ">>   $goldens GpuGolden lines, $skipped SKIPPED"

  python3 -I tools/vk/report.py "$log" "pass $name" "$expect"
  rc=$?
  [ "$demo_rc" -ne 0 ] && rc=1
  # A golden that stood down proves nothing: the layer had no device work to check.
  [ "$goldens" -eq 0 ] || [ "$skipped" -gt 0 ] && {
    echo ">>   FAIL: the goldens did not all run -- validation saw no GPU work to check" >&2; rc=1; }
  return $rc
}

rc=0
case "$which_pass" in
  a|both) run_pass a "$pass_a" "$expect_a" || rc=1 ;;
esac
case "$which_pass" in
  b|both) run_pass b "$pass_b" "$expect_b" || rc=1 ;;
esac
echo
[ $rc -eq 0 ] && echo ">> validation clean." || echo ">> validation FAILED -- see the logs under $out" >&2
exit $rc
