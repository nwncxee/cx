#!/usr/bin/env bash
# The WINDOWS live run (handoff "Work left" 3): nwmain.exe under Proton, with the cross-built version.dll,
# inside the same headless gamescope and the same machine-wide live lock the Linux harness uses -- so it
# can be run unattended without anything reaching the desktop and without overlapping a Linux run.
#
# Nothing of the user's is written. The game tree is MIRRORED by symlink into the output directory and only
# bin/win32/version.dll is a real file, so the depot keeps whatever version.dll it had; the Windows user
# directory lives inside a throwaway Proton prefix, so the user's saves and nwn.ini are never opened.
#
#   tools/live/run-proton-live.sh [--seconds N] [--width W] [--height H] [--cfg k=v]... [--menu] [--out DIR]
#                                 [--nui | --showcase NAME] [--keys 'script']
#
# --nui hosts the fixed NUI window module of run-live.sh (tools/live/ladder/cxnui.nss) built into the prefix's user
# directory; --showcase NAME hosts run-live.sh's showcase of that name, its .policy as the card file and its .keys as the
# default --keys; --keys runs an xtool.py key/shot script 60 seconds in, after the client has had time to load.
#
# Default is the Linux harness's own module, so the shots line up with a `run-live.sh` run of the same name.
set -u
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# shellcheck source=tools/live/isolate.sh
. "$ROOT/tools/live/isolate.sh"
NWN_DIR="${NWN_DIR:-$(python3 "$ROOT/tools/paths.py" NWN_DIR)}"
PROTON="${PROTON:-$HOME/.steam/root/steamapps/common/Proton - Experimental/proton}"
MODULE="The Dark Ranger's Treasure"
WIDTH=1280; HEIGHT=720; SECONDS_TO_RUN=60; MENU=0; REUSE=0; NUI=0; KEYS=""; KEYS_AT=60; SHOWCASE=""
OUT="$ROOT/.scratch/live/proton"
CFGS=()

while [ $# -gt 0 ]; do
    case "$1" in
        --seconds) SECONDS_TO_RUN="$2"; shift 2 ;;
        --width)   WIDTH="$2"; shift 2 ;;
        --height)  HEIGHT="$2"; shift 2 ;;
        --out)     OUT="$2"; shift 2 ;;
        --module)  MODULE="$2"; shift 2 ;;
        --cfg)     CFGS+=("$2"); shift 2 ;;
        --menu)    MENU=1; shift ;;
        --reuse)   REUSE=1; shift ;;
        --nui)     NUI=1; MODULE="cxnui"; shift ;;
        --showcase) SHOWCASE="$2"; NUI=1; MODULE="cxshow"; shift 2 ;;
        --keys)    KEYS="$2"; shift 2 ;;
        *) echo "run-proton-live: unknown option $1" >&2; exit 2 ;;
    esac
done

SHOW_DIR="$ROOT/tools/live/showcase"; SHOW_SRC="$SHOW_DIR/$SHOWCASE.nss"
[ -z "$SHOWCASE" ] || [ -f "$SHOW_SRC" ] || { echo "run-proton-live: no showcase '$SHOWCASE' (needs $SHOW_SRC)" >&2; exit 2; }
command -v gamescope >/dev/null || { echo "run-proton-live: gamescope not installed" >&2; exit 2; }
[ -f "$NWN_DIR/bin/win32/nwmain.exe" ] || { echo "run-proton-live: no nwmain.exe under $NWN_DIR/bin/win32" >&2; exit 2; }
[ -f "$PROTON" ] || { echo "run-proton-live: no proton at $PROTON (set PROTON)" >&2; exit 2; }
[ -f "$ROOT/cx-windows-x64/version.dll" ] || { echo "run-proton-live: build-win.sh first" >&2; exit 2; }

# The machine-wide live lock: one client on this box at a time, Linux or Windows.
LIVE_LOCK="${XDG_CACHE_HOME:-$HOME/.cache}/cx-build-slots/live.lock"
mkdir -p "$(dirname "$LIVE_LOCK")"
exec 9>"$LIVE_LOCK"
flock -w 2400 9 || { echo "run-proton-live: waited 40 min for $LIVE_LOCK -- giving up" >&2; exit 2; }

# --reuse keeps the Proton prefix, which costs a minute to build on every fresh run.
[ "$REUSE" = 1 ] && [ -d "$OUT/pfx" ] || rm -rf "$OUT"
mkdir -p "$OUT"
# Absolute from here on: Proton resolves STEAM_COMPAT_DATA_PATH from its own cwd, not ours.
OUT="$(cd "$OUT" && pwd)"
RUN_TAG="run-proton-live:$$:$OUT"
rm -rf "$OUT/shots" "$OUT/game" "$OUT/cx.log"
SHOTS="$OUT/shots"; mkdir -p "$SHOTS"
GS_LOG="$OUT/gamescope.log"

# ---- the mirrored game tree: symlinks everywhere, one real file -----------------------------------
GAME="$OUT/game"
mkdir -p "$GAME/bin/win32"
for e in "$NWN_DIR"/*; do
    [ "$(basename "$e")" = bin ] && continue
    ln -s "$e" "$GAME/$(basename "$e")"
done
for e in "$NWN_DIR"/bin/win32/*; do
    [ "$(basename "$e")" = version.dll ] && continue
    ln -s "$e" "$GAME/bin/win32/$(basename "$e")"
done
cp "$ROOT/cx-windows-x64/version.dll" "$GAME/bin/win32/version.dll"
cp "$ROOT/cx-windows-x64"/*.dll "$GAME/bin/win32/" 2>/dev/null

# ---- the throwaway prefix, and the cx.cfg inside it -----------------------------------------------
PFX="$OUT/pfx"; mkdir -p "$PFX"
# -userdirectory, which CX now honours pre-main too (Native.LaunchUserDirectory). Without that the
# engine moved and cx.cfg/cx.log did not, and the Documents tree cannot be seeded instead: the engine
# takes the first instance suffix that does NOT exist, so pre-creating "Neverwinter Nights" to drop a cfg
# in simply pushes it to ".0", then ".1", and the knobs are never read.
DOCS="$PFX/pfx/drive_c/cxuser"
mkdir -p "$DOCS"
rm -f "$DOCS/cx.log"
if [ "$NUI" = 1 ]; then
    mkdir -p "$DOCS/modules"
    NWN_DIR="$NWN_DIR" "$ROOT/tools/live/ladder/build-ladder.sh" "$DOCS/modules" "$MODULE" ${SHOWCASE:+"$SHOW_SRC"} \
        || { echo "run-proton-live: the NUI module did not build" >&2; exit 2; }
fi
if [ -n "$SHOWCASE" ]; then
    CFGS+=("nui.cardfile=Z:$SHOW_DIR/$SHOWCASE.policy")
    [ -z "$KEYS" ] && [ -f "$SHOW_DIR/$SHOWCASE.keys" ] && KEYS=$(sed "s|@SHOTS@|$SHOTS|g" "$SHOW_DIR/$SHOWCASE.keys" | tr -d '\n')
fi
{ echo "general.logs=1"; for c in ${CFGS+"${CFGS[@]}"}; do echo "$c"; done; } > "$DOCS/cx.cfg"

echo "run-proton-live: out    $OUT"
echo "run-proton-live: prefix $PFX (throwaway)"
echo "run-proton-live: cfg    $(tr "\n" " " < "$DOCS/cx.cfg" 2>/dev/null)"

# ---- launch ---------------------------------------------------------------------------------------
ARGS=(-userdirectory 'C:\cxuser')
[ "$MENU" = 1 ] || ARGS+=(+TestNewModule "$MODULE")
(
    cd "$GAME/bin/win32" || exit 1
    export STEAM_COMPAT_DATA_PATH="$PFX"
    export STEAM_COMPAT_CLIENT_INSTALL_PATH="$HOME/.steam/root"
    live_isolate "$RUN_TAG"
    # Wine prefers its builtin version.dll; n,b makes the proxy beside nwmain.exe win. No wine audio driver loads.
    export WINEDLLOVERRIDES="version=n,b;winepulse.drv=d;winealsa.drv=d;wineoss.drv=d"
    exec gamescope --backend headless -W "$WIDTH" -H "$HEIGHT" -w "$WIDTH" -h "$HEIGHT" -- \
        "$PROTON" run "$GAME/bin/win32/nwmain.exe" ${ARGS+"${ARGS[@]}"}
) > "$GS_LOG" 2>&1 &
GS_PID=$!

tree_pids() {
    local roots="$1" next="" pid kid
    while [ -n "$roots" ]; do
        next=""
        for pid in $roots; do
            for kid in $(pgrep -P "$pid" 2>/dev/null); do echo "$kid"; next="$next $kid"; done
        done
        roots="$next"
    done
}
cleanup() {
    local pid
    for pid in $(tree_pids "${GS_PID:-}") ${GS_PID:-}; do kill -KILL "$pid" 2>/dev/null; done
    live_sweep "$RUN_TAG"
}
trap cleanup EXIT INT TERM

DISP=""
for _ in $(seq 1 200); do
    DISP=$(grep -oE 'Starting Xwayland on :[0-9]+' "$GS_LOG" 2>/dev/null | head -1 | grep -oE ':[0-9]+$')
    [ -n "$DISP" ] && break
    kill -0 "$GS_PID" 2>/dev/null || break
    sleep 0.3
done
[ -n "$DISP" ] || { echo "run-proton-live: gamescope never started an Xwayland (see $GS_LOG)"; exit 1; }
echo "run-proton-live: display $DISP (nested, headless)"

# Proton's first run builds the prefix, so the client can be a minute away. Shots are taken on a clock
# rather than on a rung: there is no ladder here, the question is only whether it boots and draws.
end=$(( $(date +%s) + SECONDS_TO_RUN ))
n=0
start=$(date +%s); keys_done=0
audio_clients=""
while [ "$(date +%s)" -lt "$end" ] && kill -0 "$GS_PID" 2>/dev/null; do
    sleep 10
    n=$((n + 1))
    if [ -n "$KEYS" ] && [ "$keys_done" = 0 ] && [ $(( $(date +%s) - start )) -ge "$KEYS_AT" ]; then
        keys_done=1
        python3 "$ROOT/tools/live/xtool.py" keys "$DISP" "$KEYS" >> "$OUT/xtool.log" 2>&1 \
            || echo "run-proton-live: key injection failed (see $OUT/xtool.log)"
    fi
    [ -n "$audio_clients" ] || audio_clients=$(live_audio_clients "$RUN_TAG")
    python3 "$ROOT/tools/live/xtool.py" shot "$DISP" "$(printf '%s/%02d.png' "$SHOTS" "$n")" \
        >> "$OUT/xtool.log" 2>&1 || true
done

kill -TERM "$GS_PID" 2>/dev/null
sleep 5
cleanup
sleep 1
survivors=$(live_survivors "$RUN_TAG" | tr '\n' ',' | sed 's/,$//')
live_sweep "$RUN_TAG"

# The Windows cx.log lives in the prefix, beside the Windows nwn.ini.
[ -f "$DOCS/cx.log" ] && cp "$DOCS/cx.log" "$OUT/cx.log"
shots=$(find "$SHOTS" -name '*.png' -size +1k 2>/dev/null | wc -l)
echo "run-proton-live: shots  $shots in $SHOTS"
if [ -f "$OUT/cx.log" ]; then
    echo "run-proton-live: cx.log $(wc -l < "$OUT/cx.log") line(s) -- $OUT/cx.log"
else
    echo "run-proton-live: NO cx.log -- the extender never wrote one (see $GS_LOG)"
fi
verdict=0
case "$audio_clients" in
    "") echo "run-proton-live: sound  not reached" ;;
    unchecked) echo "run-proton-live: sound  unchecked (no pw-dump or jq)" ;;
    *) echo "run-proton-live: AUDIO  the run reached the desktop's sound server [$(echo "$audio_clients" | tr '\n' ',' | sed 's/,$//')]"; verdict=1 ;;
esac
if [ -n "$survivors" ]; then
    echo "run-proton-live: LEFTOVER process(es) outlived the run and were killed [$survivors]"
    verdict=1
fi
exit $verdict
