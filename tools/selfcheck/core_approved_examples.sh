#!/usr/bin/env bash
# The engine-free core builds as its own assembly (CX-625). It has no approved examples, being a refactor; these are
# its Done when lines. Heavy -- build.sh, the AOT demo, the fast path, the gates and build-win.sh -- so build.sh does
# not run it; run it by hand inside nix develop. Oracle: the service count BASE_REV's boot logs (examples_lib.sh), the
# type names the ticket lists, and the core assembly's own metadata (core_types.fsx). Exit 0 PASS, 1 FAIL, 2 setup.
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 2
. tools/selfcheck/examples_lib.sh
rc=0
tmp=$(mktemp -d "${TMPDIR:-/tmp}/cx-core-examples.XXXXXX")
trap 'rm -rf "$tmp"' EXIT
fail() { echo "!! $1"; rc=1; }
command -v dotnet >/dev/null || { echo ">> run this inside nix develop" >&2; exit 2; }
core=managed/Core/cx_core.csproj

# Done when: the engine-free code (text and file parsers, config, logs, shader text rules, the command list) builds in
# its own project that names no engine view and no Vulkan call
[ -f "$core" ] || fail "no core project at $core"
if [ -f "$core" ]; then
  dotnet build "$core" -c Release --disable-build-servers -m:1 -nologo -v:q -p:UseSharedCompilation=false -o "$tmp/core" > "$tmp/core.out" 2>&1 \
    || fail "the core project does not build on its own: $(grep -E 'error' "$tmp/core.out" | head -3)"
  dotnet fsi --quiet --exec tools/selfcheck/core_types.fsx "$tmp/core/cx_core.dll" > "$tmp/types" 2>&1 || fail "the core assembly's metadata cannot be read: $(head -3 "$tmp/types")"
  for t in HexColour ConfigFile KnobFile LogParts ShaderContract CommandList; do
    grep -qE "^def .*\.$t$" "$tmp/types" || fail "the core assembly defines no $t"
  done
  grep -E '^asm cx_managed$' "$tmp/types" && fail "the core assembly references cx_managed"
  grep -E '^ref CX\.(Engine|Graphics)\.|^ref .*\.Vk[A-Z]' "$tmp/types" | head -3 | while read -r l; do fail "the core assembly names an engine view or a Vulkan type: $l"; done
  grep -qE '^ref CX\.(Engine|Graphics)\.|^ref .*\.Vk[A-Z]' "$tmp/types" && rc=1
fi

# Done when: ./build.sh, ./run-demo.sh, tools/selfcheck/run.sh, the gates and build-win.sh are green
./build.sh > "$tmp/build.out" 2>&1 || fail "build.sh failed: $(tail -3 "$tmp/build.out")"
CX_NO_BUILD=1 ./run-demo.sh > "$tmp/demo.out" 2>&1 || fail "run-demo.sh failed: $(tail -3 "$tmp/demo.out")"
tools/selfcheck/run.sh --out "$tmp/fast" > "$tmp/fast.out" 2>&1 || fail "the fast path failed: $(tail -3 "$tmp/fast.out")"
bash tools/gates/run.sh > "$tmp/gates.out" 2>&1 || fail "the gates failed: $(tail -3 "$tmp/gates.out")"
nix develop .#win -c ./build-win.sh > "$tmp/win.out" 2>&1 || fail "build-win.sh failed: $(tail -3 "$tmp/win.out")"

# Done when: the shipped image is still one cx_managed.so and one version.dll, and every service is still discovered
[ -f cx-linux-x64/cx_managed.so ] || fail "cx-linux-x64 holds no cx_managed.so"
[ -f cx-windows-x64/version.dll ] || fail "cx-windows-x64 holds no version.dll"
for f in cx-linux-x64/* cx-windows-x64/*; do
  case "${f##*/}" in *[Cc]ore*) fail "$f ships beside the one managed image" ;; esac
done
baseline
want=$(grep -o 'host: [0-9]* service(s) discovered' "$BASE_LOG" | head -1)
demolog=$(sed -n 's/^>> \(.*cx\.log\) (written from C#):$/\1/p' "$tmp/demo.out")
got=$(grep -o 'host: [0-9]* service(s) discovered' "$demolog" 2>/dev/null | head -1)
[ -n "$want" ] && [ "$got" = "$want" ] || fail "the AOT image logs '$got', not $BASE_REV's '$want'"
gotfast=$(grep -o 'host: [0-9]* service(s) discovered' "$tmp/fast/cx.log" 2>/dev/null | head -1)
[ "$gotfast" = "$want" ] || fail "the fast path logs '$gotfast', not $BASE_REV's '$want'"

[ $rc = 0 ] && echo ">> core assembly checks: every Done when line holds" || echo "!! core assembly checks: FAILED (above)" >&2
exit $rc
