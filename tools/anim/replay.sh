#!/usr/bin/env bash
# Grades a leg recording offline: for each character, the most extra knee bend, the most foot slide and the fastest hip turn.
#   tools/anim/replay.sh <recording>        a cx-legs-*.cxlegs file written with `/anim record on`
#   tools/anim/replay.sh --fixture <out>    writes the small hand-checked recording the approved example replays
# Runs cx_managed's leg core as a plain console app (JIT): no game, no stub host. Needs the .NET SDK (nix develop).
set -e
cd "$(dirname "$0")/../.."
OUT=build/anim-replay
mkdir -p build
dotnet build tools/anim/replay.csproj -c Release -o "$OUT" -v quiet -nologo -clp:NoSummary >"$OUT.log" 2>&1 \
  || { cat "$OUT.log" >&2; echo "replay: the build failed" >&2; exit 2; }
exec dotnet "$OUT/replay.dll" "$@"
