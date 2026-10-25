#!/usr/bin/env bash
# Native Windows build of version.dll (#504): MSVC + the Windows SDK on the machine itself, under Git
# Bash inside a VS developer shell (cl, cmake, dotnet on PATH). This is what releases ship;
# build-win.sh is the Linux cross-build, a developer tool only.
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build

if [ ! -f build/funchook.lib ] || [ ! -f build/distorm.lib ]; then
  # funchook's Windows page allocator only walks UP from the target; with the NativeAOT GC reservation
  # sitting right above nwmain.exe it leaves rel32 range and the jump truncates. Applied in place.
  if ! git -C third_party/funchook apply --reverse --check ../../native/funchook-win-rel32.patch 2>/dev/null; then
    git -C third_party/funchook apply ../../native/funchook-win-rel32.patch
  fi
  echo ">> building funchook.lib (MSVC)..."
  # Static: it is linked into cx_managed.dll (DirectPInvoke; see the csproj), not shipped beside it.
  cmake -S third_party/funchook -B build/funchook-win -A x64 \
    -DCMAKE_POLICY_DEFAULT_CMP0091=NEW -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded \
    -DFUNCHOOK_BUILD_SHARED=OFF -DFUNCHOOK_BUILD_STATIC=ON -DFUNCHOOK_BUILD_TESTS=OFF >/dev/null
  cmake --build build/funchook-win --config Release >/dev/null
  cp build/funchook-win/Release/funchook.lib build/funchook-win/Release/distorm.lib build/
fi

echo ">> compiling the version.dll proxy half (native/bootstrap_win.c -> .obj)..."
# Dash flags: Git Bash rewrites /nologo-style args into C:/Program Files/Git/nologo paths.
cl -nologo -c -O2 -GS- native/bootstrap_win.c -Fobuild/bootstrap_win.obj
cl -nologo -c -O2 -MT -GS- native/cxthunk.c -Fobuild/cxthunk_win.obj   # the throwing-function wrappers (see build.sh)
# #673: xatlas for version.dll (see build.sh for the flags' reasons); /MT to match NativeAOT's static CRT.
if [ ! -f build/cxatlas.lib ] || [ native/xatlas/xatlas.cpp -nt build/cxatlas.lib ] || [ native/cxatlas.cpp -nt build/cxatlas.lib ]; then
  echo ">> building xatlas (cxatlas.lib, MSVC)..."
  cl -nologo -c -O2 -MT -GS- -EHs-c- -GR- -DNDEBUG -DXA_MULTITHREADED=0 native/xatlas/xatlas.cpp -Fobuild/xatlas_win.obj
  cl -nologo -c -O2 -MT -GS- -EHs-c- -GR- -DNDEBUG -DXA_MULTITHREADED=0 native/cxatlas.cpp -Fobuild/cxatlas_win.obj
  lib -nologo -out:build/cxatlas.lib build/xatlas_win.obj build/cxatlas_win.obj
fi

# CX-57: the pinned audio runtime and the mixer (see build-win.sh).
python tools/deps.py openal-src build/deps
python tools/deps.py openal-win build/deps/win
python tools/deps.py steamaudio build/deps/sa
OAL=build/deps/openal-soft-1.25.2
if [ ! -f build/cxaudio.lib ] || [ native/cxaudio.cpp -nt build/cxaudio.lib ]; then
  echo ">> building the audio core (cxaudio.lib, MSVC)..."
  cl -nologo -c -std:c++17 -O2 -MT -GS- -EHs-c- -GR- -DNDEBUG -Ithird_party/steamaudio -I"$OAL/include" \
    native/cxaudio.cpp -Fobuild/cxaudio_win.obj
  lib -nologo -out:build/cxaudio.lib build/cxaudio_win.obj
fi

# tools/abi/check-win-symbols.py needs llvm-undname and a real nwmain.exe; a CI runner has neither.
echo ">> skipping the symbol check (no nwmain.exe here; build-win.sh runs it on the dev box)" >&2

echo ">> building cx_managed.dll (NativeAOT win-x64, MSVC link)..."
CX_VERSION=$(git describe --tags --always 2>/dev/null || echo dev)
if ! dotnet publish managed/cx_managed.csproj -c Release -r win-x64 -o build/win \
     -p:InformationalVersion="$CX_VERSION" > build-managed-win.log 2>&1; then
  echo ">> managed win build FAILED:"; grep -E "error" build-managed-win.log | head -20; exit 1
fi

OUT=cx-windows-x64
rm -rf "$OUT" && mkdir -p "$OUT"
# nwmain.exe imports VERSION.dll and the loader takes the copy beside the exe first; the audio runtime rides
# beside it (build-win.sh says why cx_openal.dll is renamed).
cp build/win/cx_managed.dll "$OUT/version.dll"
cp build/deps/win/soft_oal.dll "$OUT/cx_openal.dll"
cp build/deps/sa/phonon.dll "$OUT/phonon.dll"
# CX-320: the Vulkan toolchain's native halves, dropped in build/win/ by the publish above (NuGet
# runtimes/win-x64/native). They ship beside version.dll, the way the audio runtime does. Named, not
# globbed: a publish that stops copying one has to fail here rather than ship an install missing it.
for lib in shaderc_shared.dll vma.dll; do
  [ -f "build/win/$lib" ] || { echo "!! build/win/$lib is missing -- the Vulkan toolchain packages did not restore" >&2; exit 1; }
  cp "build/win/$lib" "$OUT/"
done
BUILDS=$(awk -F'\t' '$1 == "name" { printf "%s%s", sep, $2; sep = ", " }' managed/Framework/Builds/*.tsv)
sed "s/@VERSION@/$CX_VERSION/g; s/@BUILDS@/$BUILDS/g" packaging/README-win.txt > "$OUT/README.txt"
packaging/licenses.sh "$OUT/LICENSES.txt"
# Contents, not the folder: the README says "unpack here" and that has to be true.
python -c "import shutil; shutil.make_archive('$OUT', 'zip', '$OUT')"
echo ">> artifacts ($OUT/, $OUT.zip):"; ls -1 "$OUT"
