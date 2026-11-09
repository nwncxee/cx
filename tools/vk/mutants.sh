#!/usr/bin/env bash
# The positive controls for tools/vk/validate.sh: each mutant below is one line changed in the transfer-queue path or
# the bindless one, and a run counts as proof only when the mutated tree leaves a validation message the unmutated
# tree does not. "Fail on any message" is worth nothing on its own -- a layer that never loaded fails nothing -- so
# this is the check that the passes can still see a real fault.
#
# It edits the working tree, rebuilds, runs one validation pass and puts the file back, for each mutant in turn. Slow
# (a build plus a pass each), by hand, never in CI. Run it OUTSIDE nix develop, from a clean tree.
#
#   tools/vk/mutants.sh [--out DIR] [--only NAME] [--list]
#   --only  run one mutant by name: acquire, wait, order, release or bindless
#   --list  print the mutants and exit
#
# CX_GPU_LOCK and CX_BUILD_WRAP guard the card and the memory on a shared box; see the two below.
#
# Each mutant names the pass it belongs to: the four transfer ones are synchronization validation's (pass a), the
# bindless one GPU-assisted validation's (pass b), whose instrumented shaders are what see a descriptor read.
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 2

# name ~ pass ~ file ~ the line to change ~ what it becomes (empty: the line is removed)
MUTANTS=(
  "acquire~a~managed/Graphics/Vulkan/VkBackend.Images.cs~                Queue(Handover(img, acquire: true));~"
  "wait~a~managed/Graphics/Vulkan/VkBackend.Sync.cs~        if (shipped) w[nw++] = new VkSemaphoreSubmitInfo { semaphore = _transfer!.Timeline, value = (ulong)_shippedJoin, stageMask = VkPipelineStageFlags2.AllCommands };~"
  "order~a~managed/Graphics/Vulkan/VkBackend.Transfer.cs~                if (!u.Seen.Add(p.Buffer.Value)) { CopyOrder(cb); u.Seen.Clear(); u.Seen.Add(p.Buffer.Value); }~"
  "release~a~managed/Graphics/Vulkan/VkBackend.Transfer.cs~        Barriers(cb, release: true);~"
  "bindless~b~managed/Graphics/Vulkan/VkGlsl.cs~              .Append(\$\"uint {BindlessAt}(uint i) {{ return i < cxBindlessCap ? i : 0u; }}\\n\");~              .Append(\$\"uint {BindlessAt}(uint i) {{ return i; }}\\n\");"
)

out=""; only=""
while [ $# -gt 0 ]; do
  case "$1" in
    --out) out=$2; shift 2 ;;
    --only) only=$2; shift 2 ;;
    --list) for m in "${MUTANTS[@]}"; do IFS='~' read -r n p f _ _ <<<"$m"; printf '%-9s pass %s  %s\n' "$n" "$p" "$f"; done; exit 0 ;;
    -h|--help) sed -n "2,19p" "$0"; exit 0 ;;
    *) echo ">> unknown argument: $1" >&2; exit 2 ;;
  esac
done
[ -n "${IN_NIX_SHELL:-}" ] && { echo ">> refusing: run this outside nix develop, where the goldens SKIP" >&2; exit 2; }
git diff --quiet || { echo ">> refusing: the working tree is dirty -- a mutant has to be the only change in it" >&2; exit 2; }
[ -n "$out" ] || out=$(mktemp -d "${TMPDIR:-/tmp}/cx-vk-mutants.XXXXXX")
mkdir -p "$out"
echo ">> work dir: $out"

# A pass's distinct message ids, so a mutant counted as caught is caught by a NEW id and not by one the unmutated
# tree already leaves.
ids() { grep -oP '^Validation (Error|Warning|Performance Warning): \[ \K[^ ]+' "$1" 2>/dev/null | sort -u; }
# On a box where other work shares the card and the memory, each step runs behind its own guard: CX_GPU_LOCK is a lock
# file one GPU job at a time is taken under, CX_BUILD_WRAP a command each build is run through (the box's build-slot
# limiter, since a NativeAOT build takes GBs). The two are separate because a build wants the CPU while the card is
# idle, and holding the card through six builds would starve everything else of it.
gpu=(); [ -n "${CX_GPU_LOCK:-}" ] && gpu=(flock "$CX_GPU_LOCK")
wrap=(); read -r -a wrap <<<"${CX_BUILD_WRAP:-}"
build() { "${wrap[@]}" nix develop -c ./build.sh > "$1" 2>&1; }
validate() { "${gpu[@]}" tools/vk/validate.sh "$@"; }

echo ">> baseline: building and running both passes on the unmutated tree"
build "$out/base-build.log" || { echo ">> the baseline build failed, see $out/base-build.log" >&2; exit 2; }
validate --out "$out/base" > "$out/base-validate.log" 2>&1
base_rc=$?
for p in a b; do ids "$out/base/pass-$p/validation.log" > "$out/base-ids-$p"; done
echo ">>   pass a left $(wc -l < "$out/base-ids-a") distinct id(s), pass b $(wc -l < "$out/base-ids-b") (validate exit $base_rc)"
[ "$base_rc" = 0 ] || echo ">>   note: the unmutated tree is not clean; a mutant still counts only on an id these do not hold"

rc=0
for m in "${MUTANTS[@]}"; do
  IFS='~' read -r name pass file old new <<<"$m"
  [ -z "$only" ] || [ "$only" = "$name" ] || continue
  echo
  echo ">> mutant $name (pass $pass): $file"
  cp "$file" "$out/$name.orig" 2>/dev/null || { echo ">>   FAIL: no $file -- the control is stale" >&2; rc=1; continue; }
  if ! python3 -I tools/vk/mutate.py "$file" "$old" ${new:+"$new"}; then
    echo ">>   FAIL: the mutant did not apply, so this control checks nothing" >&2
    cp "$out/$name.orig" "$file"; rc=1; continue
  fi
  ok=1
  if build "$out/$name-build.log"; then
    validate --out "$out/$name" --pass "$pass" > "$out/$name-validate.log" 2>&1
    ids "$out/$name/pass-$pass/validation.log" > "$out/$name-ids"
    found=$(comm -13 "$out/base-ids-$pass" "$out/$name-ids")
    if [ -n "$found" ]; then
      echo ">>   CAUGHT by: $(tr '\n' ' ' <<<"$found")"
    else
      echo ">>   NOT CAUGHT: pass $pass left no id the unmutated tree does not -- see $out/$name-validate.log" >&2
      ok=0
    fi
  else
    echo ">>   FAIL: the mutated tree did not build, so the mutant proves nothing -- see $out/$name-build.log" >&2
    ok=0
  fi
  cp "$out/$name.orig" "$file"
  [ "$ok" = 1 ] || rc=1
done

echo
echo ">> putting the tree back and rebuilding it"
git diff --quiet || { echo "!! the tree is still mutated -- restore it by hand" >&2; exit 1; }
build "$out/restore-build.log" || { echo "!! the restored tree did not build, see $out/restore-build.log" >&2; exit 1; }
[ $rc -eq 0 ] && echo ">> every mutant was caught." || echo ">> a mutant went uncaught -- see the logs under $out" >&2
exit $rc
