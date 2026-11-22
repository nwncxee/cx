#!/usr/bin/env bash
# FSR's build steps decide from the content of their inputs, not their times (CX-633, E3 and E4). Runs
# tools/ffx/prepare.sh and tools/ffx/build-lib.sh in a scratch copy, with stand-ins for the compiler, ar's inputs,
# patch and the shader generator, so no SDK is fetched and nothing is compiled.
# Oracle: the lines each step prints when it does its work, and a log the stand-in patch writes.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2
rc=0
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
fail() { echo "!! ffx cache: $*" >&2; rc=1; }

t=$tmp/tree
mkdir -p "$t/tools" "$t/native" "$t/bin" "$t/build/deps/FidelityFX-SDK-1.1.4/sdk/src"
cp -r tools/ffx "$t/tools/ffx"; cp -r native/ffx "$t/native/ffx"; cp tools/deps.py tools/deps.json "$t/tools/"
for name in ffx-sdk vulkan-headers; do
  python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))[sys.argv[2]]["sha256"])' tools/deps.json "$name" \
    > "$t/build/deps/.$name.sha256"
done
cat > "$t/tools/ffx/ffx_shaders.py" << 'PY'
import pathlib, sys
pathlib.Path(sys.argv[3]).mkdir(parents=True, exist_ok=True)
PY
cat > "$t/bin/patch" << 'SH'
#!/usr/bin/env bash
cat > /dev/null; echo patched >> "$PATCH_LOG"
SH
cat > "$t/bin/c++" << 'SH'
#!/usr/bin/env bash
[ "${1:-}" = --version ] && { echo "c++ ${CXX_VERSION:-1}"; exit 0; }
while [ $# -gt 0 ]; do [ "$1" = -o ] && { : > "$2"; exit 0; }; shift; done
SH
chmod +x "$t/bin/patch" "$t/bin/c++"
build() { (cd "$t" && PATH="$t/bin:$PATH" PATCH_LOG="$tmp/patch.log" timeout 60 \
  bash -c 'bash tools/ffx/prepare.sh python3 glslang && bash tools/ffx/build-lib.sh') > "$tmp/out" 2>&1; }
built() { grep -q 'building FSR 3.1 (libcxfsr.a)' "$tmp/out"; }
patched() { [ -s "$tmp/patch.log" ]; }

build || fail "the first build failed: $(tail -3 "$tmp/out")"
built || fail "the first build did not build FSR"

# E4
# Given a restored cache and a fresh checkout
# When build.sh runs
# Then it decides from the content of its inputs, not their times, and skips the FSR build
: > "$tmp/patch.log"
find "$t/tools" "$t/native" -type f -exec touch -d '+1 hour' {} +
build || fail "E4: the build failed: $(tail -3 "$tmp/out")"
built && fail "E4: a fresh checkout's times rebuilt FSR from unchanged inputs"
patched && fail "E4: a fresh checkout's times patched the SDK again"
grep -q 'generating' "$tmp/out" && fail "E4: a fresh checkout's times generated the shaders again"

# E3
# Given a pull request that changes native/ffx/cxfsr.cpp or native/ffx/sdk-1.1.4.patch
# When the ci job runs
# Then FSR builds again, even with a restored library present
echo '// changed' >> "$t/native/ffx/cxfsr.cpp"
touch -d '-1 hour' "$t/native/ffx/cxfsr.cpp"
build || fail "E3: the build failed: $(tail -3 "$tmp/out")"
built || fail "E3: a changed cxfsr.cpp, older than the library, did not rebuild FSR"
: > "$tmp/patch.log"
echo '' >> "$t/native/ffx/sdk-1.1.4.patch"
touch -d '-1 hour' "$t/native/ffx/sdk-1.1.4.patch"
build || fail "E3: the build failed: $(tail -3 "$tmp/out")"
patched || fail "E3: a changed patch did not patch the SDK again"
built || fail "E3: a changed patch did not rebuild FSR"

# Regression, not an approved example: every other input of the key rebuilds too.
for f in tools/ffx/cxxflags.sh native/ffx/compat.h tools/ffx/sources.txt; do
  if [ "$f" = tools/ffx/sources.txt ]; then echo planted.cpp >> "$t/$f"; else echo '# changed' >> "$t/$f"; fi
  build || fail "the build failed after $f changed: $(tail -3 "$tmp/out")"
  built || fail "a changed $f did not rebuild FSR"
done
CXX_VERSION=2 build || fail "the build failed under a new compiler: $(tail -3 "$tmp/out")"
built || fail "a new compiler version did not rebuild FSR"
: > "$tmp/patch.log"
echo 0000 > "$t/build/deps/.ffx-sdk.sha256"
build || fail "the build failed after the SDK pin changed: $(tail -3 "$tmp/out")"
patched || fail "a changed SDK pin did not patch the SDK again"

[ $rc = 0 ] && echo "ffx cache approved examples: E3 and E4 pass"
exit $rc
