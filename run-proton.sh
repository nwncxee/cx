#!/usr/bin/env sh
# The Windows hook-sanity loop (#48): run bin/win32/nwmain.exe under Proton with the cross-built DLLs
# dropped beside it, in a throwaway prefix under build/. Proves loading, symbol resolution and hooks
# on the real nwmain.exe; NOT a release gate -- that is a native Windows tester. Never touches Steam's
# Proton setting or the depot. Log: build/proton-pfx/pfx/drive_c/users/steamuser/Documents/Neverwinter Nights/cx.log
set -e
cd "$(dirname "$0")"
GAME="${NWN_WIN:-$(dirname "$(python3 tools/paths.py NWN_WIN_EXE)")}"
PROTON="${PROTON:-$HOME/.steam/root/steamapps/common/Proton - Experimental/proton}"
[ -f "$GAME/nwmain.exe" ] || { echo "!! no nwmain.exe under $GAME (set NWN_WIN)" >&2; exit 1; }
[ -f "$PROTON" ] || { echo "!! no proton at $PROTON (set PROTON)" >&2; exit 1; }
# Build first, always. This script COPIES the DLLs, so without it a run silently tests whatever
# build-win.sh last produced -- and since ./build.sh does not touch the Windows leg, "edit, build, run
# proton" tested a stale DLL and cost a whole diagnosis. ~20s when nothing changed; CX_NO_BUILD=1 to
# run exactly what is staged (bisecting, or a zip someone sent you).
[ -n "$CX_NO_BUILD" ] || ./build-win.sh
# Every DLL the build staged, not a hand-kept list: the install shape is "unpack the zip here", and a
# runtime library the proxy loads that this script forgot is the same stale-DLL diagnosis all over again.
cp cx-windows-x64/*.dll "$GAME/"
PFX="$PWD/build/proton-pfx"; mkdir -p "$PFX"
# The engine picks "Neverwinter Nights.0" (its .N instance suffix) under Proton, so seed the cfg in both.
DOCS="$PFX/pfx/drive_c/users/steamuser/Documents/Neverwinter Nights"
for d in "$DOCS" "$DOCS.0"; do mkdir -p "$d"; [ -f "$d/cx.cfg" ] || printf 'general.logs=1\n' > "$d/cx.cfg"; done
# #456: the bar is a SAME-SAVE two-platform compare, and a throwaway prefix otherwise means a fresh
# character -- "20 minutes into Chapter 1" is not a repeatable area. Mirror the Linux saves in (cp -Ru:
# newer only, so a Windows-side save is not clobbered by a stale Linux one).
SAVES="${NWN_SAVES:-$HOME/.local/share/Neverwinter Nights/saves}"
if [ -d "$SAVES" ]; then   # `[ ] &&` here would be a false command under set -e when there are no saves
  for d in "$DOCS" "$DOCS.0"; do mkdir -p "$d/saves"; cp -Ru "$SAVES/." "$d/saves/"; done
else echo ">> no saves at $SAVES -- the same-save compare needs one (NWN_SAVES overrides)" >&2; fi
rm -f "$DOCS/cx.log"
# Wine prefers its builtin version.dll; n,b makes the proxy beside nwmain.exe win.
cd "$GAME" && STEAM_COMPAT_DATA_PATH="$PFX" STEAM_COMPAT_CLIENT_INSTALL_PATH="$HOME/.steam/root" \
  WINEDLLOVERRIDES="version=n,b" exec "$PROTON" run "$GAME/nwmain.exe" "$@"
