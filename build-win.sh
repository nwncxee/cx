#!/usr/bin/env sh
# Cross-build the Windows leg on Linux (#48 M1): version.dll, the NativeAOT image itself, with
# funchook, the UI font and the export-forwarding proxy all linked in, and CX-57's audio runtime beside it.
# Needs clang-cl, lld-link, llvm-lib, cmake, the .NET SDK, and a Windows 10 SDK + MSVC lib/include
# tree (`nix develop .#win` fetches both; SDK / MSVC in the environment or local.env name your own). Nothing here is
# supported by Microsoft; it works
# because ILC emits COFF on any host and lld-link links it.
set -e
cd "$(dirname "$0")"
# llvm-rc preprocesses with the clang on PATH. Under nix develop that is the cc-wrapper, whose hardening adds
# -fPIC, and clang refuses -fPIC for a Windows target.
export NIX_HARDENING_ENABLE=
mkdir -p build

SDK="${SDK:-$(python3 tools/paths.py SDK)}"
SDKVER="${SDKVER:-10.0.19041.0}"
MSVC="${MSVC:-$(python3 tools/paths.py MSVC)}"
[ -f "$SDK/Lib/$SDKVER/um/x64/kernel32.Lib" ] || { echo "!! no Windows SDK under $SDK (set SDK/SDKVER)" >&2; exit 1; }
[ -f "$MSVC/lib/x64/libvcruntime.lib" ] || { echo "!! no MSVC tools under $MSVC (set MSVC)" >&2; exit 1; }

# Windows resolves headers and libs case-insensitively; the NTFS mount does not. Headers go through a
# clang VFS overlay with case-sensitive:false (the SDK includes itself in mixed case, so a renamed
# mirror is not enough); libs get a lowercase+UPPERCASE symlink mirror for lld-link and the
# /DEFAULTLIB:LIBCMT directives MSVC objects carry.
W=build/winsdk
if [ ! -d "$W" ]; then
  echo ">> mirroring Windows SDK + MSVC libs into $W, writing the include overlay..."
  mkdir -p "$W/lib"
  for d in "$SDK/Lib/$SDKVER/um/x64" "$SDK/Lib/$SDKVER/ucrt/x64" "$MSVC/lib/x64"; do
    find "$d" -maxdepth 1 -type f -iname '*.lib' | while read -r f; do
      b=$(basename "$f"); b="${b%.*}"
      ln -sf "$f" "$W/lib/$(echo "$b" | tr 'A-Z' 'a-z').lib"
      ln -sf "$f" "$W/lib/$(echo "$b" | tr 'a-z' 'A-Z').lib"
    done
  done
  # Space-free directory symlinks: the paths ride through CMake flag strings unquoted.
  mkdir -p "$W/inc"
  ln -sfn "$SDK/Include/$SDKVER/um" "$W/inc/um"; ln -sfn "$SDK/Include/$SDKVER/shared" "$W/inc/shared"
  ln -sfn "$SDK/Include/$SDKVER/ucrt" "$W/inc/ucrt"; ln -sfn "$MSVC/include" "$W/inc/msvc"
  python3 - "$W/vfs.yaml" "$(cd "$W" && pwd)/inc" um shared ucrt msvc <<'PY'
import os, sys, json
out, base, names = sys.argv[1], sys.argv[2], sys.argv[3:]
roots = [{"name": os.path.join(base, n), "type": "directory", "contents": [
    {"name": f, "type": "file", "external-contents": os.path.join(base, n, f)}
    for f in os.listdir(os.path.join(base, n)) if os.path.isfile(os.path.join(base, n, f))]} for n in names]
json.dump({"version": 0, "case-sensitive": "false", "roots": roots}, open(out, "w"))
PY
fi
WABS=$(cd "$W" && pwd)

if [ ! -f build/funchook.lib ] || [ ! -f build/distorm.lib ]; then
  # A fresh clone or a git worktree starts with the submodule and its nested diStorm empty.
  [ -d third_party/funchook/distorm/src ] || git submodule update --init --recursive third_party/funchook
  # funchook's Windows page allocator only walks UP from the target; with the NativeAOT GC reservation
  # sitting right above nwmain.exe (Wine) it leaves rel32 range and the jump truncates. Applied in place,
  # idempotent; the submodule stays dirty on purpose.
  if git -C third_party/funchook apply --check ../../native/funchook-win-rel32.patch 2>/dev/null; then
    git -C third_party/funchook apply ../../native/funchook-win-rel32.patch
  fi
  echo ">> building funchook.lib (clang-cl cross)..."
  # Static: it is linked into cx_managed.dll (DirectPInvoke; see the csproj), not shipped beside it.
  cmake -S third_party/funchook -B build/funchook-win -DCMAKE_TOOLCHAIN_FILE="$PWD/native/win-toolchain.cmake" \
    -DWINSDK="$WABS" -DCMAKE_POLICY_DEFAULT_CMP0091=NEW -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded -DFUNCHOOK_BUILD_SHARED=OFF -DFUNCHOOK_BUILD_STATIC=ON \
    -DFUNCHOOK_BUILD_TESTS=OFF -DCMAKE_BUILD_TYPE=Release >/dev/null
  cmake --build build/funchook-win -j"${JOBS:-$(nproc)}" >/dev/null
  cp build/funchook-win/funchook.lib build/funchook-win/distorm.lib build/
fi

echo ">> compiling the version.dll proxy half (native/bootstrap_win.c -> .obj)..."
# Linked INTO cx_managed.dll by the publish below, which is why it is compiled first.
clang-cl -c -O2 -GS- native/bootstrap_win.c -Fo:build/bootstrap_win.obj
# The C wrappers (see build.sh); the crash stamp uses stdio, so the CRT headers, /MT as NativeAOT's static CRT.
WINC="/I$WABS/inc/um /I$WABS/inc/shared /I$WABS/inc/ucrt /I$WABS/inc/msvc -Xclang -ivfsoverlay -Xclang $WABS/vfs.yaml -w"
clang-cl -c -O2 -MT -GS- $WINC native/cxthunk.c -Fo:build/cxthunk_win.obj
# #673: xatlas for version.dll, the same flags as build.sh's (see there); /MT to match NativeAOT's static CRT.
if [ ! -f build/cxatlas.lib ] || [ native/xatlas/xatlas.cpp -nt build/cxatlas.lib ] || [ native/cxatlas.cpp -nt build/cxatlas.lib ]; then
  echo ">> building xatlas (cxatlas.lib, clang-cl cross)..."
  clang-cl -c -O2 -MT -GS- -EHs-c- -GR- -DNDEBUG -DXA_MULTITHREADED=0 $WINC native/xatlas/xatlas.cpp -Fo:build/xatlas_win.obj
  clang-cl -c -O2 -MT -GS- -EHs-c- -GR- -DNDEBUG -DXA_MULTITHREADED=0 $WINC native/cxatlas.cpp -Fo:build/cxatlas_win.obj
  llvm-lib -out:build/cxatlas.lib build/xatlas_win.obj build/cxatlas_win.obj
fi

# CX-57: the pinned audio runtime -- OpenAL Soft's own Win64 release build and Steam Audio's phonon.dll, shipped
# beside version.dll (see build.sh for why OpenAL is a separate file) -- and the mixer, linked in.
python3 tools/deps.py openal-src build/deps
python3 tools/deps.py openal-win build/deps/win
python3 tools/deps.py steamaudio build/deps/sa
OAL=build/deps/openal-soft-1.25.2
if [ ! -f build/cxaudio.lib ] || [ native/cxaudio.cpp -nt build/cxaudio.lib ]; then
  echo ">> building the audio core (cxaudio.lib, clang-cl cross)..."
  clang-cl -c -std:c++17 -O2 -MT -GS- -EHs-c- -GR- -DNDEBUG $WINC -Ithird_party/steamaudio -I"$OAL/include" \
    native/cxaudio.cpp -Fo:build/cxaudio_win.obj
  llvm-lib -out:build/cxaudio.lib build/cxaudio_win.obj
fi

# #456: the Windows symbol gate runs BEFORE the build, not as a report nobody reads. It checks every Nm row
# names a real export, every At row lands in the image, and -- the clause the #456 crash bought -- that every
# instance member returning a class by value has had its (this, sret) argument order decided at the call site.
if command -v llvm-undname >/dev/null 2>&1 && [ -f "$(python3 tools/paths.py NWN_WIN_EXE 2>/dev/null)" ]; then
  echo ">> checking Windows symbol rows..."
  ./tools/abi/check-win-symbols.py || { echo "!! symbol rows are wrong -- fix them before shipping a DLL" >&2; exit 1; }
else
  echo ">> skipping the symbol check (no llvm-undname, or no nwmain.exe to check against)" >&2
fi

echo ">> building cx_managed.dll (NativeAOT win-x64, lld-link)..."
CX_VERSION=$(git describe --tags --always 2>/dev/null || echo dev)
# CppLinker must be a global property: the ILCompiler targets reset it to `link` otherwise.
if ! dotnet publish managed/cx_managed.csproj -c Release -r win-x64 -o build/win \
     -p:CppLinker=lld-link -p:CppLibCreator=llvm-lib -p:WinLib="$WABS/lib" \
     -p:InformationalVersion="$CX_VERSION" > build-managed-win.log 2>&1; then
  echo ">> managed win build FAILED:"; grep -E "error" build-managed-win.log | head -20; exit 1
fi

OUT=cx-windows-x64
rm -rf "$OUT" && mkdir -p "$OUT"
# funchook, the UI font and the proxy all ride inside the image; the name is the whole trick, since nwmain.exe
# imports VERSION.dll and the loader takes the copy beside the exe first. CX-57's audio runtime rides beside it:
# cx_openal.dll is upstream's soft_oal.dll renamed so it cannot collide with the game's own soft_oal.dll.
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
python3 -c "import shutil; shutil.make_archive('$OUT', 'zip', '$OUT')"
echo ">> artifacts ($OUT/, $OUT.zip):"; ls -1 "$OUT"
