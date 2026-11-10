#!/usr/bin/env bash
# Regression check, not an approved test: the guards tools/vk/mutants.sh grew after a review, which held before it.
# The sweep edits tracked source and decides PASS/FAIL for the Vulkan validation passes, so each way it could report
# a clean sweep of nothing gets a case here.
#
# It runs the real mutants.sh, mutate.py and report.py against a fixture tree of its own -- a copy of the one engine
# file the `acquire` mutant names, a stub build, a stub validation pass and a stub `nix` -- so there is no GPU, no
# client and no NativeAOT build in it, which is why build.sh can run it. Exit 0 PASS, 1 FAIL.
#
#   tools/vk_mutants_regression.sh
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/cx-vk-mutants-XXXX")"
trap 'rm -rf "$WORK"' EXIT
fails=0
ok() { echo "  ok: $1"; }
bad() { echo "FAIL: $1" >&2; fails=$((fails + 1)); }

MUTANT_FILE=managed/Graphics/Vulkan/VkBackend.Images.cs
for f in tools/vk/mutants.sh tools/vk/mutate.py tools/vk/report.py "$MUTANT_FILE"; do
    [ -f "$ROOT/$f" ] || { echo "FAIL: no $f to check against" >&2; exit 1; }
done

# A fixture tree at $WORK/tree: the real sweep and its two python tools, the one engine file the `acquire` mutant
# edits, and stubs for everything that would cost a build or a GPU. Committed, because the sweep refuses a dirty tree
# and puts the file back by comparing against the commit.
mkdir -p "$WORK/tree/tools/vk" "$WORK/tree/$(dirname "$MUTANT_FILE")" "$WORK/bin"
for f in tools/vk/mutants.sh tools/vk/mutate.py tools/vk/report.py "$MUTANT_FILE"; do
    cp "$ROOT/$f" "$WORK/tree/$f"
done

# The build the sweep runs through `nix develop -c ./build.sh`. STUB_BUILD_SLEEP holds it open, so a case can
# interrupt a run while a mutant is live in the tree.
cat > "$WORK/tree/build.sh" <<'STUB'
#!/usr/bin/env bash
echo "stub build"
[ -z "${STUB_BUILD_SLEEP:-}" ] || sleep "$STUB_BUILD_SLEEP"
exit "${STUB_BUILD_RC:-0}"
STUB

# The validation pass. The baseline call names no --pass and so writes both logs with the layer's own notice alone;
# a mutant's call names one, and STUB_IDS is what that pass finds. STUB_NO_LAYER leaves the notice out, which is a
# log the layer was never in.
cat > "$WORK/tree/tools/vk/validate.sh" <<'STUB'
#!/usr/bin/env bash
set -uo pipefail
out=""; which_pass=both
while [ $# -gt 0 ]; do
  case "$1" in
    --out) out=$2; shift 2 ;;
    --pass) which_pass=$2; shift 2 ;;
    *) shift ;;
  esac
done
log() {
  mkdir -p "$out/pass-$1"
  {
    [ -n "${STUB_NO_LAYER:-}" ] || {
      printf 'Validation Information: [ WARNING-CreateInstance-status-message ] | MessageID = 0x23dfd876\n'
      printf 'vkCreateInstance(): Khronos Validation Layer Active:\n\n'; }
    [ "$which_pass" = both ] && return 0
    for id in ${STUB_IDS:-}; do
      printf 'Validation Error: [ %s ] | MessageID = 0x779fc816\n' "$id"
      printf 'vkCmdDraw(): the fixture pass found this.\n\n'
    done
  } > "$out/pass-$1/validation.log"
}
case "$which_pass" in a) log a ;; b) log b ;; *) log a; log b ;; esac
exit "${STUB_VALIDATE_RC:-0}"
STUB

# The sweep builds with `nix develop -c ./build.sh`; this stands in for the shell, which the fixture has no use for.
cat > "$WORK/bin/nix" <<'STUB'
#!/usr/bin/env bash
while [ "${1:-}" = develop ] || [ "${1:-}" = -c ]; do shift; done
exec "$@"
STUB
chmod +x "$WORK/tree/build.sh" "$WORK/tree/tools/vk/validate.sh" "$WORK/bin/nix"

# The sweep refuses a dirty tree, so a case that moves the file or the line it names commits that first.
commit() { git -C "$WORK/tree" add -A . && git -C "$WORK/tree" \
    -c user.email=fixture@invalid -c user.name=fixture -c commit.gpgsign=false commit -q -m "$1" \
    || { echo "FAIL: the fixture tree would not commit '$1'" >&2; exit 1; }; }
git -C "$WORK/tree" init -q -b main
commit fixture

# The sweep over the fixture tree, its output in $WORK/out-$1 and the rest of the arguments its own. Prints what it
# said and leaves its exit status in `rc`.
sweep() {
    local tag=$1; shift
    PATH="$WORK/bin:$PATH" env -u IN_NIX_SHELL -u CX_BUILD_WRAP -u CX_GPU_LOCK \
        bash "$WORK/tree/tools/vk/mutants.sh" --out "$WORK/out-$tag" "$@" > "$WORK/log-$tag" 2>&1
    rc=$?
    cat "$WORK/log-$tag"
}
clean_tree() { git -C "$WORK/tree" diff --quiet; }

# 1. Given the sweep's fixed list of mutants
#    When it is asked to list them
#    Then it names each one and the pass it belongs to, and makes no build
sweep list --list > /dev/null
got="$(wc -l < "$WORK/log-list")"
if [ "$rc" != 0 ]; then bad "--list exited $rc"
elif [ "$got" != 5 ]; then bad "--list printed $got lines, not the five mutants"
elif ! grep -q '^acquire  *pass a' "$WORK/log-list"; then bad "--list does not name acquire and its pass"
else ok "--list names the five mutants and the pass each belongs to"; fi

# 2. Given a `--only` naming no mutant
#    When the sweep runs
#    Then it refuses with 2, rather than skipping every mutant and reporting a clean sweep of nothing
sweep typo --only acquier > /dev/null
if [ "$rc" != 2 ]; then bad "--only naming no mutant exited $rc, not 2"
elif grep -q 'every mutant was caught' "$WORK/log-typo"; then bad "--only naming no mutant reported every mutant caught"
elif [ -e "$WORK/out-typo/base-build.log" ]; then bad "--only naming no mutant built anyway"
else ok "a --only naming no mutant refuses with 2 and builds nothing"; fi

# 3. Given a mutant whose pass leaves a message id the unmutated tree does not
#    When the sweep runs it
#    Then it reports CAUGHT by that id and exits 0, and the tree is back as it was
STUB_IDS=SYNC-HAZARD-WRITE-AFTER-WRITE sweep caught --only acquire > /dev/null
if [ "$rc" != 0 ]; then bad "a mutant its pass sees exited $rc, not 0"
elif ! grep -q 'CAUGHT by: SYNC-HAZARD-WRITE-AFTER-WRITE' "$WORK/log-caught"; then bad "a mutant its pass sees was not reported CAUGHT by the new id"
elif ! clean_tree; then bad "the tree is still mutated after a caught mutant"
else ok "a mutant whose pass leaves a new id is CAUGHT, and the tree is put back"; fi

# 4. Given a mutant whose pass leaves no id the unmutated tree does not
#    When the sweep runs it
#    Then it reports NOT CAUGHT and exits non-zero
sweep uncaught --only acquire > /dev/null
if [ "$rc" = 0 ]; then bad "a mutant its pass never saw exited 0"
elif ! grep -q 'NOT CAUGHT' "$WORK/log-uncaught"; then bad "a mutant its pass never saw was not reported NOT CAUGHT"
elif ! clean_tree; then bad "the tree is still mutated after an uncaught mutant"
else ok "a mutant whose pass leaves no new id is NOT CAUGHT"; fi

# 5. Given a baseline log the validation layer was never in
#    When the sweep runs
#    Then it refuses with 2, rather than reading every id of every mutant as new
STUB_NO_LAYER=1 STUB_IDS=SYNC-HAZARD-WRITE-AFTER-WRITE sweep nolayer --only acquire > /dev/null
if [ "$rc" != 2 ]; then bad "a baseline the layer was not in exited $rc, not 2"
elif grep -q 'CAUGHT by' "$WORK/log-nolayer"; then bad "a baseline the layer was not in still reported a mutant caught"
else ok "a baseline holding no layer-loaded notice refuses with 2 and judges no mutant"; fi

# 6. Given a mutant naming a file that is no longer in the tree
#    When the sweep runs it
#    Then it fails the control as stale rather than passing it
cp "$WORK/tree/$MUTANT_FILE" "$WORK/held"
rm "$WORK/tree/$MUTANT_FILE"
commit "the file moved"
STUB_IDS=SYNC-HAZARD-WRITE-AFTER-WRITE sweep stale --only acquire > /dev/null
if [ "$rc" = 0 ]; then bad "a mutant whose file is gone exited 0"
elif ! grep -q 'the control is stale' "$WORK/log-stale"; then bad "a mutant whose file is gone was not called stale"
else ok "a mutant naming a file that is gone fails as a stale control"; fi
cp "$WORK/held" "$WORK/tree/$MUTANT_FILE"
commit "the file is back"

# 7. Given a mutant whose line is no longer in the file it names
#    When the sweep runs it
#    Then it fails because the mutant did not apply, and leaves the file as it found it
grep -v 'Queue(Handover(img, acquire: true));' "$WORK/tree/$MUTANT_FILE" > "$WORK/trimmed"
cp "$WORK/trimmed" "$WORK/tree/$MUTANT_FILE"
commit "the line moved"
STUB_IDS=SYNC-HAZARD-WRITE-AFTER-WRITE sweep moved --only acquire > /dev/null
if [ "$rc" = 0 ]; then bad "a mutant whose line is gone exited 0"
elif ! grep -q 'did not apply' "$WORK/log-moved"; then bad "a mutant whose line is gone was not reported as not applying"
elif ! cmp -s "$WORK/trimmed" "$WORK/tree/$MUTANT_FILE"; then bad "a mutant that did not apply changed the file anyway"
else ok "a mutant whose line is gone fails, and the file is left as it was found"; fi
cp "$WORK/held" "$WORK/tree/$MUTANT_FILE"
commit "the line is back"

# 8. Given a run cut short while a mutant is live in the tree
#    When it is sent a TERM
#    Then it puts the mutated file back, so the next build stages no mutant
STUB_BUILD_SLEEP=2 STUB_IDS=SYNC-HAZARD-WRITE-AFTER-WRITE \
    PATH="$WORK/bin:$PATH" env -u IN_NIX_SHELL -u CX_BUILD_WRAP -u CX_GPU_LOCK \
    bash "$WORK/tree/tools/vk/mutants.sh" --out "$WORK/out-term" --only acquire > "$WORK/log-term" 2>&1 &
pid=$!
mutated=0
for _ in $(seq 1 100); do
    clean_tree || { mutated=1; break; }
    sleep 0.2
done
if [ "$mutated" != 1 ]; then
    kill "$pid" 2>/dev/null; wait "$pid" 2>/dev/null
    bad "the run never put a mutant in the tree, so the interrupt case checks nothing"
else
    kill -TERM "$pid"; wait "$pid" 2>/dev/null; trc=$?
    if ! clean_tree; then bad "a run cut short left the tree mutated ($(git -C "$WORK/tree" diff --name-only | tr '\n' ' '))"
    elif [ "$trc" = 0 ]; then bad "a run cut short exited 0"
    else ok "a run cut short while a mutant is live puts the file back"; fi
fi
git -C "$WORK/tree" checkout -q -- .

# 9. Given a working tree with a change of its own in it
#    When the sweep runs
#    Then it refuses with 2, so the restore cannot write over that change
printf '\n// not the sweep\n' >> "$WORK/tree/$MUTANT_FILE"
sweep dirty --only acquire > /dev/null
if [ "$rc" != 2 ]; then bad "a dirty working tree exited $rc, not 2"
elif [ -e "$WORK/out-dirty/base-build.log" ]; then bad "a dirty working tree built anyway"
else ok "a working tree with a change of its own refuses with 2"; fi
git -C "$WORK/tree" checkout -q -- .

[ "$fails" = 0 ] && { echo "PASS: the mutant sweep answers all 9 examples"; exit 0; }
echo "FAIL: $fails of the mutant sweep examples" >&2
exit 1
