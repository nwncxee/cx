#!/usr/bin/env bash
# Runs every self-check from the managed sources compiled to IL (no NativeAOT publish),
# booted in a JIT host that carries a known client build-id, as ./run-demo.sh's stub does. It ends on the verdict
# run-demo.sh ends on (tools/selfcheck/verdict.sh). An addition to ./run-demo.sh, never a replacement: CI and the
# shipped image run the AOT build, so a change is proven there before it merges. Linux-only, like the rest of
# the developer tooling. Run it inside `nix develop`, and under cap.sh on a shared machine.
#
#   tools/selfcheck/run.sh [--out DIR] [--overlay DIR]
#   --out      the build folder (default build/selfcheck); the mutant runner gives each run its own
#   --overlay  a folder whose managed/ holds .cs files that replace or add to managed/ in this build only
#   --no-build boot what --out already holds (the mutant runner boots one build once per mutant)
#   --no-analyzers  compile without the analyzers, which judge the tree and not a mutant (the mutant runner's compiles)
#
# Exit 0 when no self-check failed, 1 when one did, 2 for a setup problem.
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 2
repo=$PWD
out=$repo/build/selfcheck
overlay=""
build=1
analyzers=true
while [ $# -gt 0 ]; do
  case "$1" in
    --out) out=$(realpath -m -- "$2"); shift 2 ;;
    --overlay) overlay=$(realpath -- "$2"); shift 2 ;;
    --no-build) build=0; shift ;;
    --no-analyzers) analyzers=false; shift ;;
    -h|--help) awk 'NR > 1 { if (!/^#/) exit; print }' "$0"; exit 0 ;;
    *) echo ">> unknown argument: $1" >&2; exit 2 ;;
  esac
done
command -v dotnet >/dev/null || { echo ">> no dotnet on PATH: run this inside nix develop" >&2; exit 2; }
dotnet_root=$(dirname "$(readlink -f "$(command -v dotnet)")")
fxr=$(ls -d "$dotnet_root"/host/fxr/*/libhostfxr.so 2>/dev/null | sort -V | tail -1)
[ -n "$fxr" ] || { echo ">> no libhostfxr.so under $dotnet_root/host/fxr" >&2; exit 2; }
bin=$out/bin/Release/net10.0/linux-x64
# Shared by every --out, so a mutant run's own build folders do not rebuild funchook and FSR each time.
native=$repo/build/selfcheck-native
mkdir -p "$native" "$out"

# The libraries the AOT image links in statically (DirectPInvoke) are found by name at run time under a JIT, so each
# static archive build.sh made becomes a shared one here. Rebuilt only when its archive is newer.
shim() {
  local name=$1; shift
  local so=$native/lib$name.so stale=0
  [ -f "$so" ] || stale=1
  for a in "$@"; do
    [ -f "$repo/build/$a" ] || { echo ">> no build/$a: run ./build.sh once in this checkout for the native libraries" >&2; exit 2; }
    [ "$repo/build/$a" -nt "$so" ] && stale=1
  done
  [ "$stale" = 1 ] || return 0
  (cd "$repo/build" && c++ -shared -o "$so" -Wl,--whole-archive "$@" -Wl,--no-whole-archive -ldl) \
    || { echo ">> could not link $so" >&2; exit 2; }
}
# funchook's static build hides every symbol, so it is built shared, once, from the same submodule.
if [ ! -f "$native/libfunchook.so" ]; then
  [ -d third_party/funchook/distorm/src ] || git submodule update --init --recursive third_party/funchook
  cmake -S third_party/funchook -B "$native/funchook-build" -DFUNCHOOK_BUILD_SHARED=ON -DFUNCHOOK_BUILD_STATIC=OFF \
    -DFUNCHOOK_BUILD_TESTS=OFF -DCMAKE_BUILD_TYPE=Release >/dev/null \
    && cmake --build "$native/funchook-build" -j2 >/dev/null && cp -L "$native/funchook-build/libfunchook.so" "$native/" \
    || { echo ">> could not build funchook shared" >&2; exit 2; }
fi
shim cxatlas libcxatlas.a
shim cxthunk libcxthunk.a
shim cxaudio libcxaudio.a
# FSR's objects are compiled with their symbols hidden, its C API too: that one file is rebuilt visible and linked
# beside the rest, whose symbols only it calls.
if [ ! -f "$native/libcxfsr.so" ] || [ "$repo/build/libcxfsr.a" -nt "$native/libcxfsr.so" ]; then
  [ -f build/libcxfsr.a ] || { echo ">> no build/libcxfsr.a: run ./build.sh once in this checkout" >&2; exit 2; }
  . tools/ffx/cxxflags.sh
  c++ "${FFX_CXXFLAGS[@]}" -c native/ffx/cxfsr.cpp -o "$native/cxfsr.o" \
    && c++ -shared -o "$native/libcxfsr.so" "$native/cxfsr.o" $(ls build/ffx/obj/*.o | grep -v '/cxfsr\.o$') \
    || { echo ">> could not link libcxfsr.so" >&2; exit 2; }
fi

if [ "$build" = 1 ]; then
  echo ">> compiling managed/ to IL (no NativeAOT)..."
  props=(-p:DirectoryBuildPropsPath="$repo/tools/selfcheck/jit.props" -p:DirectoryBuildTargetsPath="$repo/tools/selfcheck/jit.targets"
         -p:CxJitOut="$out"
         # PublishAot writes the image's feature switches into the runtimeconfig, which keeps the JIT run on the
         # image's code paths; one of them refuses the native host this build is loaded by, and it alone goes back on.
         -p:_EnableConsumingManagedCodeFromNativeHosting=true -p:RunAnalyzers=$analyzers)
  [ -z "$overlay" ] || props+=(-p:CxOverlay="$overlay")
  # One process and no build server: the compile stays inside the caller's memory scope and ends with the run.
  if ! dotnet build managed/cx_managed.csproj -c Release --disable-build-servers -m:1 -nologo -v:q -clp:ErrorsOnly \
       -p:UseSharedCompilation=false "${props[@]}" > "$out/build.log" 2>&1; then
    echo ">> FAIL: the managed build failed:"; grep -E 'error' "$out/build.log" | sort -u | head -20; exit 1
  fi
fi
[ -f "$bin/cx_managed.dll" ] || { echo ">> no $bin/cx_managed.dll: run without --no-build first" >&2; exit 2; }

# The demo's stub host (tools/stub-host.sh), hosting the runtime itself: the rot gate reads this executable's build-id.
host=$out/nwmain-selfcheck
bash tools/stub-host.sh "$host" native/selfcheck_host.c || exit 2

# Its own HOME, as run-demo.sh gives the demo: a player's cx.cfg would move the defaults the suites assert.
home=$out/home
rm -rf "$home"; mkdir -p "$home"
log="$home/.local/share/Neverwinter Nights/logs/cx.log"
echo ">> booting the self-checks in the JIT host..."
HOME="$home" XDG_DATA_HOME="$home/.local/share" DOTNET_ROOT="$dotnet_root" LD_LIBRARY_PATH="$native:$bin" \
  "$host" "$fxr" "$bin/cx_managed.runtimeconfig.json" "$bin/cx_managed.dll" > "$out/host.log" 2>&1
rc=$?
[ -f "$log" ] || { echo ">> FAIL: the host wrote no cx.log (exit $rc):"; tail -20 "$out/host.log"; exit 1; }
cp "$log" "$out/cx.log"
echo ">> $out/cx.log"
sh tools/selfcheck/verdict.sh "$out/cx.log" || exit 1
[ "$rc" = 0 ] || { echo ">> FAIL: the host exited $rc:"; tail -20 "$out/host.log"; exit 1; }
