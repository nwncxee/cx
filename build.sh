#!/usr/bin/env bash
set -e
cd "$(dirname "$0")"
mkdir -p build
# Bare `-j` is unbounded under make: OpenAL Soft then forks every TU at once and OOMs the host.
JOBS=${JOBS:-$(nproc)}

if [ ! -f build/libfunchook.a ] || [ ! -f build/libdistorm.a ]; then
  echo ">> building funchook (once)..."
  # Static, and PIC because it gets linked into the shared cx_managed.so (DirectPInvoke; see the csproj).
  cmake -S third_party/funchook -B build/funchook \
    -DFUNCHOOK_BUILD_SHARED=OFF -DFUNCHOOK_BUILD_STATIC=ON -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DFUNCHOOK_BUILD_TESTS=OFF -DCMAKE_BUILD_TYPE=Release >/dev/null
  cmake --build build/funchook -j"$JOBS" >/dev/null
  cp -a build/funchook/libfunchook.a build/funchook/libdistorm.a build/
fi

# #206: the extraction boundary. managed/Alloy/Scene/ holds the RE knowledge -- which engine field means
# what -- and that is the part a future backend would not rewrite, so it stays free of GL. A call that has
# to live there says why on its own line with a `gl-ok:` marker.
# ...including the `using static` that would let every call through as a bare name.
if grep -rnE '\bGl\.|using static .*\bGl;' managed/Alloy/Scene/ | grep -v 'gl-ok:'; then
  echo "!! Alloy/Scene must not call GL (see the lines above; #206). Move it to the submission side," >&2
  echo "!! or keep it and write the reason on the same line as a 'gl-ok: <why>' comment." >&2
  exit 1
fi

# #443 (#429 section 2's exit criterion), widened to all of managed/ by CX-36: an engine offset lives in
# EngineLayout and nowhere else, so that the unresolved counter is the truth. Graphics/ is GL enums only. The declaration form an offset takes here is a
# hex-valued integer const -- anything matching that has to say on its own line why it is NOT a place in
# memory (a GL enum, a shader-param index, a bit mask, a sentinel value).
if grep -rnE '(const|readonly)[[:space:]]+(int|uint|long|ulong|byte|ushort|short|nint)\b[^;]*0x' \
     --include='*.cs' --exclude-dir=Graphics --exclude='EngineLayout*.cs' --exclude=Views.g.cs managed/ \
     | grep -v 'not-an-offset:'; then
  echo "!! bare hex constant under managed/ (see the lines above; #443, CX-36). An engine offset belongs in" >&2
  echo "!! managed/Framework/EngineLayout.cs with a class and a form; anything else says why on the same" >&2
  echo "!! line as a 'not-an-offset: <why>' comment." >&2
  exit 1
fi

# #447 (#429 section 3): a symbol's Windows resolution is DECLARED, never absent. A missing _win key used to
# mean both "never looked" and "deliberately Linux-only"; now every `public const string` in SymbolMap.cs
# carries a row -- Nm(), At(), LinuxOnly("why") or Hole("why") -- and only Hole counts against the port.
# The census must see EVERY declarator, not the first per line: `public const string A = "a", B = "b";`
# and its continuation lines hid five constants from the first draft of this gate (and from the sweep that
# filled the table), which the Proton log then reported as unresolved. Read declarators, not lines.
missing=$(comm -23 \
  <(tr '\n' ' ' < managed/Framework/SymbolMap.cs | grep -oP 'public const string\s+\K[^;]*' \
      | grep -oP '(\w+)(?=\s*=\s*")' | sort -u) \
  <(sed -n '/Dictionary<string, Win> _win/,/^    };/p' managed/Framework/SymbolMap.cs \
      | grep -oP '^\s*\[\K\w+' | sort -u))
if [ -n "$missing" ]; then
  echo "!! symbol constant with no Windows row (#447):" >&2
  echo "$missing" | sed 's/^/!!   /' >&2
  echo "!! every symbol in SymbolMap.cs resolves to Nm(), At(), LinuxOnly(\"why\") or Hole(\"why\")." >&2
  exit 1
fi

# #673: the vendored xatlas + its C shim, static and PIC like funchook (DirectPInvoke "cxatlas"). Rebuilt
# only when a source is newer. XA_MULTITHREADED=0 keeps a mesh's chart byte-identical across runs and keeps
# libstdc++ out of the link; -fno-exceptions/-fno-rtti for the same reason.
if [ ! -f build/libcxatlas.a ] || [ native/xatlas/xatlas.cpp -nt build/libcxatlas.a ] || [ native/cxatlas.cpp -nt build/libcxatlas.a ]; then
  echo ">> building xatlas (libcxatlas.a)..."
  c++ -std=c++14 -O2 -fPIC -fno-exceptions -fno-rtti -DNDEBUG -DXA_MULTITHREADED=0 -c native/xatlas/xatlas.cpp -o build/xatlas.o
  c++ -std=c++14 -O2 -fPIC -fno-exceptions -fno-rtti -DNDEBUG -DXA_MULTITHREADED=0 -c native/cxatlas.cpp -o build/cxatlas.o
  rm -f build/libcxatlas.a && ar rcs build/libcxatlas.a build/xatlas.o build/cxatlas.o
fi

# The C wrappers for engine functions that throw (native/cxthunk.c, DirectPInvoke "cxthunk"). Unwind tables are
# the point: a C++ exception thrown by the wrapped function unwinds through this frame.
if [ ! -f build/libcxthunk.a ] || [ native/cxthunk.c -nt build/libcxthunk.a ]; then
  cc -O2 -fPIC -fasynchronous-unwind-tables -c native/cxthunk.c -o build/cxthunk.o
  rm -f build/libcxthunk.a && ar rcs build/libcxthunk.a build/cxthunk.o
fi

# CX-57: the pinned audio runtime (tools/deps.json, fetched by tools/deps.py: sha256-checked, cached in
# ~/.cache/cx-deps). OpenAL Soft is built from its release source into its own shared object -- LGPL-2.1, so it
# ships beside us rather than inside us -- by tools/build-openal.sh. Steam Audio's libphonon.so is Valve's
# binary, shipped as is.
python3 tools/deps.py openal-src build/deps
python3 tools/deps.py steamaudio build/deps/sa
OAL=build/deps/openal-soft-1.25.2
# The flake's dev shells set OPENAL_LIB to the copy Nix built from the same pin and script, once per pin.
if [ -n "${OPENAL_LIB:-}" ]; then
  install -D -m 644 "$OPENAL_LIB" build/openal/libopenal.so.1
elif [ ! -f build/openal/libopenal.so.1 ]; then
  echo ">> building OpenAL Soft 1.25.2 (once)..."
  JOBS=$JOBS bash tools/build-openal.sh "$OAL" build/openal-build build/openal/libopenal.so.1 > build/openal-build.log 2>&1 \
    || { echo "!! OpenAL Soft build failed:"; tail -20 build/openal-build.log; exit 1; }
fi
# The mixer behind it (native/cxaudio.cpp), static and PIC like xatlas (DirectPInvoke "cxaudio").
if [ ! -f build/libcxaudio.a ] || [ native/cxaudio.cpp -nt build/libcxaudio.a ]; then
  echo ">> building the audio core (libcxaudio.a)..."
  c++ -std=c++17 -O2 -fPIC -fno-exceptions -fno-rtti -DNDEBUG -Ithird_party/steamaudio -I"$OAL/include" \
    -c native/cxaudio.cpp -o build/cxaudio.o
  rm -f build/libcxaudio.a && ar rcs build/libcxaudio.a build/cxaudio.o
fi

# The typed engine views (managed/Framework/Engine/Views.g.cs) are generated from EngineLayout's rows;
# checked in because the Windows builds compile it without running python.
python3 tools/engine/views.py

# CX-192: every rule in CLAUDE.md that a script can check is a gate under tools/gates/ (after views.py: the
# engine-views gate reads the generated members). CI and the Claude Code hook run the same runner.
# --no-gates: the release build, whose tagged tree already passed ci / gates on its pull request.
if [ "$1" != --no-gates ]; then bash tools/gates/run.sh; fi

echo ">> building managed lib (C# NativeAOT -> cx_managed.so)..."
CX_VERSION=$(git describe --tags --always 2>/dev/null || echo dev)
# Quiet on success, but NEVER swallow a failure: discarding stderr here once hid a compile error behind a
# stale staged .so, and run-demo.sh happily booted the old build.
if ! dotnet publish managed/cx_managed.csproj -c Release -o build \
     -p:InformationalVersion="$CX_VERSION" > build-managed.log 2>&1; then
  echo ">> managed build FAILED:"; grep -E "error|rror" build-managed.log | head -20; exit 1
fi

echo ">> building native bootstrap (libcx.so)..."
cc -shared -fPIC -O2 -o build/libcx.so native/bootstrap.c -ldl

echo ">> staging runtime dir (cx-linux-x64/)..."
# One ready-to-run/ship folder: the wrapper loads the .so files beside it. The zip holds its
# CONTENTS, not the folder, so "unpack into bin/linux-x86" is literally the whole install.
OUT=cx-linux-x64
rm -rf "$OUT" && mkdir -p "$OUT"
cp -a build/libcx.so build/cx_managed.so "$OUT/"
cp build/openal/libopenal.so.1 build/deps/sa/libphonon.so "$OUT/"
cp packaging/cx-launch "$OUT/cx-launch"
sed "s/@VERSION@/$CX_VERSION/g" packaging/README.txt > "$OUT/README.txt"
packaging/licenses.sh "$OUT/LICENSES.txt"
chmod +x "$OUT/cx-launch"

python3 -c "import shutil; shutil.make_archive('$OUT', 'zip', '$OUT')"
echo ">> artifacts ($OUT/, $OUT.zip):"
ls -1 "$OUT"
