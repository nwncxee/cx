#!/usr/bin/env bash
# Live test harness: boot the REAL client with this worktree's CX, land in a playable area with a
# character, run it for N seconds, capture the evidence, shut it down, print PASS/FAIL.
#
#   tools/live/run-live.sh [--module NAME] [--seconds N] [--cfg 'key=value'] [--out DIR]
#                          [--keys 'script'] [--width W] [--height H] [--boot-timeout N] [--keep]
#   tools/live/run-live.sh --ladder            # the scripted verification ladder (== --module cxladder)
#   tools/live/run-live.sh --ladder --nui-close 48   # ...and close its NUI window at 48 s
#   tools/live/run-live.sh --nui --cfg 'nui.hitdebug=1' --keys 'sleep:...;move:x,y'   # a NUI window to point at
#   tools/live/run-live.sh --showcase creatures   # tools/live/showcase/creatures.{nss,policy,keys}: wiki shots
#   tools/live/run-live.sh --menu              # boot to the MAIN MENU and stop there (no module at all)
#   tools/live/run-live.sh --menu --saves DIR  # ...with saved games copied in, so Load has something
#   tools/live/run-live.sh --menu --expect-exit 25 --keys 'sleep:6000;click:640,524'
#                                              # ...and PASS only if that click made the client quit: a
#                                              # binary "GUI picking works" signal that needs no screenshot
#
# Nothing appears on the desktop: the client runs inside `gamescope --backend headless`, which gives
# it its own nested Xwayland on the real GPU (verified GL 4.6 / NVIDIA there). Nothing plays on the
# speakers: every audio route points nowhere (tools/live/isolate.sh). Nothing of the user's
# is touched either -- a throwaway HOME + user directory is built under the out dir, and the client is
# pointed at it with `-userdirectory`, so cx.cfg, cx.log, settings.tml, nwn.ini, the localvault and the
# crashreport folder are all the harness's own copies.
#
# How it gets into the game with no clicking: `+TestNewModule <mod>` makes the client host the module
# in-process (server mode), join itself as Player 1, and take the one character sitting in LOCALVAULT.
# So the harness drops a single stock pregenerated .bic there and the character select has nothing to
# ask about. Full write-up + limits: docs/live-harness.md.
#
# --ladder goes one step further: tools/live/ladder/ builds a module whose own scripts walk the PC,
# run a conversation, pick a fight and cross two area transitions on a timer, and each rung writes a
# CXLADDER marker the run grades. The module is derived from the installed stock content at run time.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# shellcheck source=tools/live/isolate.sh
. "$ROOT/tools/live/isolate.sh"
NWN_DIR="${NWN_DIR:-$(python3 "$ROOT/tools/paths.py" NWN_DIR)}"
REAL_USER_DIR="${NWN_USER_DIR:-$HOME/.local/share/Neverwinter Nights}"

MODULE="The Dark Ranger's Treasure"
SAVES_DIR=""
SECONDS_IN=20
BOOT_TIMEOUT=180
MIN_LIT=5
WIDTH=1280
HEIGHT=720
UI_SCALE=""
OUT=""
KEYS=""
KEEP=0
CFG_LINES=()
CHAR="${LIVE_CHAR:-fighter}"
LADDER_MODULE="cxladder"
LADDER=0
NUI_MODULE=0
NUI_CLOSE=
SHOWCASE=""
SHOWCASE_DIR="$ROOT/tools/live/showcase"
SHOWCASE_LATE=40
MENU=0
STOCK=0
EXPECT_EXIT=0
clicked_exit=0
SECONDS_SET=0

while [ $# -gt 0 ]; do
    case "$1" in
        --ladder)        MODULE="$LADDER_MODULE"; shift ;;
        # A stock-area module whose script opens one fixed NUI window with id'd elements (tools/live/ladder/cxnui.nss),
        # for driving the pointer over it with --keys.
        # Close the ladder's NUI window N seconds into the session (it stays open otherwise), so 07n-nui*.png catch a fade out.
        --nui-close)     NUI_CLOSE="$2"; shift 2 ;;
        --nui)           MODULE="cxnui"; NUI_MODULE=1; shift ;;
        # tools/live/showcase/<NAME>.nss as the module's script, <NAME>.policy fed in as the server's policy lines
        # (a @key@ in it is the id of the creature the script logged as "CXSHOW: id key hex"), <NAME>.keys as the
        # default --keys (@SHOTS@ is this run's shots folder). --showcase-late N: seconds after login the ids are applied.
        --showcase)      SHOWCASE="$2"; MODULE="cxshow"; NUI_MODULE=1; shift 2 ;;
        --showcase-late) SHOWCASE_LATE="$2"; shift 2 ;;
        # The client's UI scale (settings.tml ui.scale, 1.0 to 8.0), for a run that checks a layout at another scale.
        --ui-scale)      UI_SCALE="$2"; shift 2 ;;
        # The main menu is the one screen +TestNewModule skips, and it is a GUI session of its own:
        # its own scenes, its own panel models, its own fonts. Nothing else about the run changes.
        --menu)          MENU=1; shift ;;
        # The stock client, no CX loaded: for checking that what a server sends a CX client is harmless to one without it.
        # It waits on the module script's own enter marker in the engine log and grades no CX log line.
        --stock)         STOCK=1; shift ;;
        # Seconds to wait for the client to quit ON ITS OWN after --keys has run. The main menu's Exit is
        # the one control whose success is unambiguous without looking at a pixel: either the click was
        # picked and the client went, or it was not.
        --expect-exit)   EXPECT_EXIT="$2"; shift 2 ;;
        --module)        MODULE="$2"; shift 2 ;;
        --seconds)       SECONDS_IN="$2"; SECONDS_SET=1; shift 2 ;;
        --boot-timeout)  BOOT_TIMEOUT="$2"; shift 2 ;;
        --min-lit)       MIN_LIT="$2"; shift 2 ;;
        --cfg)           CFG_LINES+=("$2"); shift 2 ;;
        --out)           OUT="$2"; shift 2 ;;
        --keys)          KEYS="$2"; shift 2 ;;
        # graphics.second-story-tiles.mode: 0 auto, 1 never (the stock default), 2 always. The game maps it
        # to Scene::SetGeomFadeProperties' 1/2/4, so "never" is what a fresh user dir asks for and no roof
        # fades in either scene mode until this is 0 (CX-116).
        --second-story)  SECOND_STORY="$2"; shift 2 ;;
        --width)         WIDTH="$2"; shift 2 ;;
        --height)        HEIGHT="$2"; shift 2 ;;
        --char)          CHAR="$2"; shift 2 ;;
        # A directory of saved games to put in front of the Load panel. The harness user directory is
        # thrown away every run and never has one, so that screen cannot otherwise be reached. The source
        # is COPIED read-only into the throwaway dir -- never linked, and never written back.
        --saves)         SAVES_DIR="$2"; shift 2 ;;
        --keep)          KEEP=1; shift ;;
        -h|--help)       sed -n '2,20p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "run-live: unknown option $1" >&2; exit 2 ;;
    esac
done

# The ladder is just a module, so --module cxladder and --ladder are the same request. Its own
# timeline runs to t+48 s, so a shorter soak would grade rungs that had not been reached yet.
[ "$MODULE" = "$LADDER_MODULE" ] && LADDER=1
# --menu never hosts a module, so the ladder's module build and its rung grading are both off.
if [ "$MENU" = 1 ]; then LADDER=0; MODULE="(main menu)"; fi
if [ "$LADDER" = 1 ] && [ "$SECONDS_SET" = 0 ]; then SECONDS_IN=70; fi
if [ "$LADDER" = 1 ] && [ "$SECONDS_IN" -lt 60 ]; then
    echo "run-live: --seconds $SECONDS_IN is shorter than the ladder (needs >= 60) -- raising it to 60"
    SECONDS_IN=60
fi

STAGE="$ROOT/cx-linux-x64"
GAME_BIN="$NWN_DIR/bin/linux-x86/nwmain-linux"
[ -f "$STAGE/libcx.so" ]   || { echo "run-live: no build staged -- run ./build.sh in $ROOT" >&2; exit 2; }
[ -f "$STAGE/cx-launch" ]  || { echo "run-live: $STAGE/cx-launch missing" >&2; exit 2; }
[ -f "$GAME_BIN" ]         || { echo "run-live: client not found: $GAME_BIN (set NWN_DIR)" >&2; exit 2; }
command -v gamescope >/dev/null || { echo "run-live: gamescope not installed" >&2; exit 2; }

# One client at a time: two nwmain processes fight over the GPU and the box has other agents building.
# Parallel worktree agents queue on one machine-wide lock (up to 40 min) instead of refusing each other.
LIVE_LOCK="${XDG_CACHE_HOME:-$HOME/.cache}/cx-build-slots/live.lock"
mkdir -p "$(dirname "$LIVE_LOCK")"
exec 9>"$LIVE_LOCK"
flock -w 2400 9 || { echo "run-live: waited 40 min for $LIVE_LOCK -- giving up" >&2; exit 2; }
if pgrep -x nwmain-linux >/dev/null; then
    echo "run-live: a client is already running (pgrep nwmain-linux) -- refusing to start a second" >&2
    exit 2
fi

[ -n "$OUT" ] || OUT="$ROOT/.scratch/live/$(date +%Y%m%d-%H%M%S)"
mkdir -p "$OUT" || exit 2
OUT="$(cd "$OUT" && pwd)"
RUN_TAG="run-live:$$:$OUT"

FAKE_HOME="$OUT/home"
USER_DIR="$FAKE_HOME/.local/share/Neverwinter Nights"
CX_LOG="$FAKE_HOME/cx.log"
ENGINE_OUT="$OUT/engine-stdout.log"
GS_LOG="$OUT/gamescope.log"
SHOTS="$OUT/shots"
mkdir -p "$USER_DIR/localvault" "$FAKE_HOME/.cache" "$FAKE_HOME/.config" "$SHOTS"

# ---- the throwaway user directory -------------------------------------------------------------
# nwn.ini is nothing but absolute Alias paths, so it is copied with every one of them rewritten --
# that single file is what keeps the engine's saves/cache/crashreport/logs inside $OUT.
if [ -f "$REAL_USER_DIR/nwn.ini" ]; then
    sed "s|$REAL_USER_DIR|$USER_DIR|g" "$REAL_USER_DIR/nwn.ini" > "$USER_DIR/nwn.ini"
fi
# cdkey.ini is the only file with no sane default; settings.tml carries the graphics/UI state we then
# override. Both are copies, never links: the client rewrites them on exit.
for f in cdkey.ini nwnplayer.ini settings.tml; do
    [ -f "$REAL_USER_DIR/$f" ] && cp -f "$REAL_USER_DIR/$f" "$USER_DIR/$f"
done
# The character. One stock pregenerated .bic and ONLY one: test mode takes the single local-vault
# character without asking, and a second one would put a selection panel in front of the harness.
PREGEN="$NWN_DIR/data/lcv/$CHAR.bic"
[ -f "$PREGEN" ] || { echo "run-live: no pregenerated character $PREGEN (--char)" >&2; exit 2; }
rm -f "$USER_DIR/localvault/"*.bic
cp -f "$PREGEN" "$USER_DIR/localvault/"

# Saved games, when the run asks for them (--saves). Copied, so the client's own writes land in the
# throwaway dir and the source directory is only ever read.
if [ -n "$SAVES_DIR" ]; then
    [ -d "$SAVES_DIR" ] || { echo "run-live: --saves $SAVES_DIR is not a directory" >&2; exit 2; }
    mkdir -p "$USER_DIR/saves"
    cp -r --no-preserve=mode "$SAVES_DIR"/. "$USER_DIR/saves/" || exit 2
    echo "run-live: saves      $(find "$USER_DIR/saves" -mindepth 1 -maxdepth 1 -type d | wc -l) from $SAVES_DIR"
fi

python3 - "$USER_DIR/settings.tml" "$WIDTH" "$HEIGHT" "${SECOND_STORY:-}" "${UI_SCALE:-}" <<'PY'
# Windowed at the harness size, no intro movies, no vsync (we want frames as fast as the GPU gives
# them, and a headless gamescope has no refresh to sync to). Written as a whole-table rewrite so the
# shape of the user's file does not matter.
import re, sys
path, w, h = sys.argv[1], sys.argv[2], sys.argv[3]
ss = sys.argv[4] if len(sys.argv) > 4 else ""
ui_scale = sys.argv[5] if len(sys.argv) > 5 else ""
try:
    t = open(path).read()
except FileNotFoundError:
    t = ""

def put(table, key, value, text):
    pat = re.compile(r'(^\[?' + re.escape(table) + r'\]?\s*$)(.*?)(?=^\s*\[|\Z)',
                     re.M | re.S)
    m = pat.search(text)
    if not m:
        return text
    body = m.group(2)
    kpat = re.compile(r'^(\s*)' + re.escape(key) + r'\s*=.*$', re.M)
    if kpat.search(body):
        body = kpat.sub(lambda mm: f"{mm.group(1)}{key} = {value}", body, count=1)
    else:
        body = body.rstrip("\n") + f"\n\t\t\t{key} = {value}\n"
    return text[:m.start(2)] + body + text[m.end(2):]

for table, key, value in [
    ("\t[graphics.window]", "mode", '"windowed"'),
    ("\t\t[graphics.window.windowed]", "width", w),
    ("\t\t[graphics.window.windowed]", "height", h),
    ("\t\t[graphics.window.windowed]", "borderless", "false"),
    ("\t\t\t[graphics.window.windowed.position]", "mode", '"centered"'),
    ("\t\t[graphics.video.vsync]", "mode", '"off"'),
    ("\t[graphics.movies]", "enabled", "false"),
    ("\t\t[graphics.movies.intro]", "enabled", "false"),
    ("\t\t[graphics.intro.splash]", "enabled", "false"),
    ("\t[masterserver]", "enabled", "false"),
] + ([("\t[graphics.second-story-tiles]", "mode", ss)] if ss else []) + ([("[ui]", "scale", ui_scale), ("[ui]", "unconstrain-scale", "true")] if ui_scale else []):
    t = put(table, key, value, t)
open(path, "w").write(t)
PY

# ---- the showcase ---------------------------------------------------------------------------------
# The policy is read at login, before the script has made the creatures it names, so the lines with a @key@ in
# them wait in a second file the poller below fills in once every id is in the log; nui.cardlatefile applies it.
if [ -n "$SHOWCASE" ]; then
    SHOW_SRC="$SHOWCASE_DIR/$SHOWCASE.nss"
    SHOW_POLICY="$SHOWCASE_DIR/$SHOWCASE.policy"
    [ -f "$SHOW_SRC" ] && [ -f "$SHOW_POLICY" ] || { echo "run-live: no showcase '$SHOWCASE' (needs $SHOW_SRC and $SHOW_POLICY)" >&2; exit 2; }
    SHOW_LATE="$OUT/showcase-late.policy"
    grep -v '@[A-Za-z0-9_]*@' "$SHOW_POLICY" > "$SHOW_LATE"
    CFG_LINES+=("nui.cardfile=$SHOW_POLICY" "nui.cardlatefile=$SHOW_LATE" "nui.cardlatesec=$SHOWCASE_LATE")
    if [ -z "$KEYS" ] && [ -f "$SHOWCASE_DIR/$SHOWCASE.keys" ]; then
        KEYS=$(sed "s|@SHOTS@|$SHOTS|g" "$SHOWCASE_DIR/$SHOWCASE.keys" | tr -d '\n')
        [ "$SECONDS_SET" = 1 ] || SECONDS_IN=2
    fi
    # A window over the scene is the point of the shot, so the world need not be lit under it.
    [ "$MIN_LIT" != 5 ] || MIN_LIT=0
fi

# ---- cx.cfg -------------------------------------------------------------------------------------
# general.logs=1 is not a nicety: every marker this harness grades on is a CX log line.
{
    echo "general.logs=1"
    for l in ${CFG_LINES+"${CFG_LINES[@]}"}; do echo "$l"; done
} > "$USER_DIR/cx.cfg"

CRASH_DIR="$USER_DIR/crashreport"
mkdir -p "$CRASH_DIR"

# ---- the ladder module ---------------------------------------------------------------------------
# Built into the THROWAWAY user directory, never the user's own: nothing of the installed game ends up
# in the repo and nothing the repo builds ends up in the user's modules folder.
if [ "$LADDER" = 1 ] || [ "$NUI_MODULE" = 1 ]; then
    mkdir -p "$USER_DIR/modules"
    NWN_DIR="$NWN_DIR" LADDER_NUI_CLOSE="$NUI_CLOSE" "$ROOT/tools/live/ladder/build-ladder.sh" "$USER_DIR/modules" "$MODULE" ${SHOWCASE:+"$SHOW_SRC"} \
        || { echo "run-live: the ladder module did not build" >&2; exit 2; }
fi

if [ -n "$SHOWCASE" ]; then
    (
        for _ in $(seq 1 $(( BOOT_TIMEOUT + 120 ))); do
            resolved=$(cat "$SHOW_POLICY")
            for k in $(grep -o '@[A-Za-z0-9_]*@' "$SHOW_POLICY" | sort -u | tr -d @); do
                id=$(grep -h "CXSHOW: id $k " "$USER_DIR"/logs/*.txt "$ENGINE_OUT" 2>/dev/null | head -1 | awk '{print $NF}')
                [ -z "$id" ] || resolved=${resolved//@$k@/$id}
            done
            # What is resolved lands at once; a key whose creature never came stays out, and the poller keeps waiting for it.
            grep -v '@[A-Za-z0-9_]*@' <<<"$resolved" > "$SHOW_LATE.tmp" && mv "$SHOW_LATE.tmp" "$SHOW_LATE"
            grep -q '@[A-Za-z0-9_]*@' <<<"$resolved" || exit 0
            sleep 0.5
        done
    ) &
    SHOW_PID=$!
fi

echo "run-live: out        $OUT"
echo "run-live: module    $MODULE"
echo "run-live: character  $CHAR"
echo "run-live: user dir   $USER_DIR"

# ---- launch -------------------------------------------------------------------------------------
# cwd MUST be bin/linux-x86: the client divines the base data directory from it and gives up otherwise
# ("Could not divine nwn base data directory"). cx-launch is used in its Steam shape -- the command is
# our argument -- because its manual mode only forwards leading-dash options and the module flag is `+`.
START_EPOCH=$(date +%s.%N)
(
    cd "$NWN_DIR/bin/linux-x86" || exit 1
    export HOME="$FAKE_HOME"
    export XDG_DATA_HOME="$FAKE_HOME/.local/share"
    export XDG_CACHE_HOME="$FAKE_HOME/.cache"
    export XDG_CONFIG_HOME="$FAKE_HOME/.config"
    live_isolate "$RUN_TAG"
    if [ "$MENU" = 1 ]; then
        exec gamescope --backend headless -W "$WIDTH" -H "$HEIGHT" -w "$WIDTH" -h "$HEIGHT" -- \
            "$STAGE/cx-launch" ./nwmain-linux -userdirectory "$USER_DIR"
    fi
    LAUNCH=("$STAGE/cx-launch" ./nwmain-linux)
    [ "$STOCK" = 1 ] && LAUNCH=(./nwmain-linux)
    exec gamescope --backend headless -W "$WIDTH" -H "$HEIGHT" -w "$WIDTH" -h "$HEIGHT" -- \
        "${LAUNCH[@]}" -userdirectory "$USER_DIR" \
        +TestNewModule "$MODULE"
) > "$GS_LOG" 2>&1 &
GS_PID=$!

# Is $1 the process $2, or somewhere under it? Walks /proc's ppid links, which is how the harness tells
# ITS client from one the user launched: a kill or a pid lookup BY NAME (`pkill -x nwmain-linux`,
# `pgrep -x nwmain-linux | head -1`) would reach the user's own game, and the trap below fires on every
# exit including Ctrl-C. The ppid is read past the comm field's closing paren -- field 2 of what follows --
# because a comm may contain spaces.
under() {
    local pid="$1" root="$2" hops=0
    while [ "$pid" -gt 1 ] && [ "$hops" -lt 32 ]; do
        [ "$pid" = "$root" ] && return 0
        pid=$(sed 's/^.*) //' "/proc/$pid/stat" 2>/dev/null | awk '{print $2}')
        [ -n "$pid" ] || return 1
        hops=$((hops + 1))
    done
    return 1
}

# Every live process under $1, parents before children.
tree_pids() {
    local roots="$1" next="" pid kid
    while [ -n "$roots" ]; do
        next=""
        for pid in $roots; do
            for kid in $(pgrep -P "$pid" 2>/dev/null); do
                echo "$kid"
                next="$next $kid"
            done
        done
        roots="$next"
    done
}

cleanup() {
    # Only this run's own processes: the gamescope we started, its tree, and the client we identified
    # (a client whose gamescope already died is reparented to init and has left that tree).
    local pid
    for pid in $(tree_pids "${GS_PID:-}") ${CLIENT_PID:-} ${GS_PID:-} ${SHOW_PID:-}; do
        kill -KILL "$pid" 2>/dev/null
    done
    live_sweep "$RUN_TAG"
}
trap cleanup EXIT INT TERM

# A crashed client can sit as a zombie its parent never reaps, and `kill -0` succeeds on a zombie: test
# the /proc state so a dead client ends the run instead of holding the machine-wide lock to the timeout.
alive() { [ -r "/proc/$1/stat" ] && [ "$(awk '{print $3}' "/proc/$1/stat" 2>/dev/null)" != "Z" ]; }

# gamescope picks the first free X display number; it is in its own log and nowhere else.
DISP=""
for _ in $(seq 1 100); do
    DISP=$(grep -oE 'Starting Xwayland on :[0-9]+' "$GS_LOG" 2>/dev/null | head -1 | grep -oE ':[0-9]+$')
    [ -n "$DISP" ] && break
    kill -0 "$GS_PID" 2>/dev/null || break
    sleep 0.3
done
[ -n "$DISP" ] || { echo "run-live: gamescope never started an Xwayland (see $GS_LOG)"; exit 1; }
echo "run-live: display    $DISP (nested, headless)"

CLIENT_PID=""
for _ in $(seq 1 100); do
    # OURS, not the first one on the box: a client the user launched during the run must never be the
    # pid this harness waits on, screenshots, signals or kills.
    for pid in $(pgrep -x nwmain-linux 2>/dev/null); do
        under "$pid" "$GS_PID" && { CLIENT_PID=$pid; break; }
    done
    [ -n "$CLIENT_PID" ] && break
    sleep 0.3
done
[ -n "$CLIENT_PID" ] || { echo "run-live: the client never started (see $GS_LOG)"; exit 1; }
echo "run-live: client pid $CLIENT_PID"

# ---- wait for the area --------------------------------------------------------------------------
# The marker is the AreaLoaded send actually LEAVING: the client sends SendPlayerToServerArea_AreaLoaded only
# once it has a creature standing in a loaded area, which is exactly "playable" -- but the shadow bake HOLDS that
# send behind the loading screen, so "holding the AreaLoaded send" is the start of the wait, not its end (it once
# graded a stuck loading screen PASS). Released, passed through, or -- when the send never resolved, so nothing
# holds it -- the area edge itself.
AREA_MARK='LightmapBake: hold released|LightmapBake: AreaLoaded send passed through'
AREA_MARK_NOHOLD='\[areaedge\] AreaLoaded send|PartTable: AreaLoaded send'
# Alloy off runs no shadow bake and no part table, so neither mark exists: the session gate seeing a module is the edge left.
AREA_MARK_ENGINE='\[uigate\] win=0x[1-9a-f][0-9a-f]* .*inModule=True'
FRAME_MARK='first frame presented -- hook is LIVE'
if [ "$STOCK" = 1 ]; then
    CX_LOG="$USER_DIR/logs/nwclientLog1.txt"
    AREA_MARK='CX(SHOW|NUI|LADDER): enter '
fi
reached=0
area_epoch=""
peak_rss=0
hang=0
last_size=0
stall=0
deadline=$(( $(date +%s) + BOOT_TIMEOUT ))
menu_up=0
while [ "$(date +%s)" -lt "$deadline" ]; do
    alive "$CLIENT_PID" || break
    [ -r "/proc/$CLIENT_PID/status" ] && peak_rss=$(awk '/VmHWM/{print $2}' "/proc/$CLIENT_PID/status" 2>/dev/null || echo "$peak_rss")
    # The menu's own mark is the first present: no module loads, so no area mark will ever come.
    if [ "$MENU" = 1 ] && grep -qF "$FRAME_MARK" "$CX_LOG" 2>/dev/null; then
        menu_up=1
        area_epoch=$(date +%s.%N)
        break
    fi
    if [ "$MENU" = 0 ] && { grep -qE "$AREA_MARK" "$CX_LOG" 2>/dev/null \
       || { grep -q '\[alloygate\] alloy.enabled=0 -- the engine renders' "$CX_LOG" 2>/dev/null && grep -qE "$AREA_MARK_ENGINE" "$CX_LOG" 2>/dev/null; } \
       || { grep -q 'the AreaLoaded send did not resolve' "$CX_LOG" 2>/dev/null && grep -qE "$AREA_MARK_NOHOLD" "$CX_LOG" 2>/dev/null; }; }; then
        reached=1
        area_epoch=$(date +%s.%N)
        break
    fi
    # A frozen client writes nothing: a cx.log that has not grown for 60 s while the process is alive
    # is the hang signature (see the nativeaot-hang-forensics note).
    size=$(stat -c %s "$CX_LOG" 2>/dev/null || echo 0)
    if [ "$size" = "$last_size" ]; then stall=$((stall + 1)); else stall=0; last_size=$size; fi
    if [ "$stall" -ge 120 ]; then hang=1; break; fi
    sleep 0.5
done

thread_states=""
if [ "$hang" = 1 ] || ! alive "$CLIENT_PID"; then
    thread_states=$(awk '{print $3}' /proc/"$CLIENT_PID"/task/*/stat 2>/dev/null | sort | uniq -c | tr '\n' ' ')
fi

# ---- soak, act, capture -------------------------------------------------------------------------
shots=0
# The ladder's own timeline (see tools/live/ladder/cxladder.nss) starts at the PC's login, a second or
# two before the area is "reached", so these waits trail each rung rather than racing it: a shot lands
# inside the rung's window, not on its edge. The pointer sweeps during the fight so the engine runs its
# cursor hit-check over the goblins -- the "hovered a creature" half of a pick.
LADDER_KEYS="sleep:5000;shot:$SHOTS/03-walk.png"
for b in a b c d e; do
    LADDER_KEYS="$LADDER_KEYS;sleep:250;shot:$SHOTS/03-walk-$b.png"
done
LADDER_KEYS="$LADDER_KEYS;sleep:3000;shot:$SHOTS/04-talk.png"
LADDER_KEYS="$LADDER_KEYS;move:$((WIDTH / 2)),$((HEIGHT * 46 / 100));sleep:800;key:1;sleep:2000"
LADDER_KEYS="$LADDER_KEYS;shot:$SHOTS/05-talk2.png;key:1;sleep:5000"
LADDER_KEYS="$LADDER_KEYS;shot:$SHOTS/06-combat.png"
for b in a b c d e; do
    LADDER_KEYS="$LADDER_KEYS;sleep:250;shot:$SHOTS/06-combat-$b.png"
done
# The particle rungs (beam, fireball, bolt impact) fire inside this window; 400 ms apart so a
# short-lived detonation cannot fall between two shots.
for b in a b c d e f g; do
    LADDER_KEYS="$LADDER_KEYS;sleep:400;shot:$SHOTS/06s-spell-$b.png"
done
for dy in 40 44 48 52 46; do
    LADDER_KEYS="$LADDER_KEYS;move:$((WIDTH / 2)),$((HEIGHT * dy / 100));sleep:250"
done
LADDER_KEYS="$LADDER_KEYS;sleep:7700;shot:$SHOTS/06f-equipped.png"
LADDER_KEYS="$LADDER_KEYS;sleep:4000;shot:$SHOTS/06g-stripped.png"
LADDER_KEYS="$LADDER_KEYS;sleep:4000;shot:$SHOTS/06h-reequipped.png"
LADDER_KEYS="$LADDER_KEYS;sleep:500;shot:$SHOTS/06i-reequipped2.png"
LADDER_KEYS="$LADDER_KEYS;sleep:6000;shot:$SHOTS/07-area2.png"
LADDER_KEYS="$LADDER_KEYS;sleep:3000;move:550,360;sleep:400;shot:$SHOTS/07n-nui.png;sleep:1000;shot:$SHOTS/07n-nui-b.png;sleep:1000;shot:$SHOTS/07n-nui-c.png"
LADDER_KEYS="$LADDER_KEYS;sleep:11000;shot:$SHOTS/08-area1.png"
# The ladder owns the key channel, so a --keys of your own runs after it rather than being dropped.
[ -n "$KEYS" ] && LADDER_KEYS="$LADDER_KEYS;$KEYS"

if [ "$reached" = 1 ] && [ "$LADDER" = 1 ]; then
    echo "run-live: reached the area -- running the ladder, ${SECONDS_IN}s soak"
    python3 "$ROOT/tools/live/xtool.py" shot "$DISP" "$SHOTS/01-early.png" >> "$OUT/xtool.log" 2>&1
    ladder_start=$(date +%s)
    python3 "$ROOT/tools/live/xtool.py" keys "$DISP" "$LADDER_KEYS" >> "$OUT/xtool.log" 2>&1 \
        || echo "run-live: the ladder key/shot script failed (see $OUT/xtool.log)"
    rest=$(( SECONDS_IN - ($(date +%s) - ladder_start) ))
    [ "$rest" -gt 0 ] && sleep "$rest"
    python3 "$ROOT/tools/live/xtool.py" shot "$DISP" "$SHOTS/02-settled.png" >> "$OUT/xtool.log" 2>&1
    shots=$(find "$SHOTS" -name '*.png' | wc -l)
    [ -r "/proc/$CLIENT_PID/status" ] && peak_rss=$(awk '/VmHWM/{print $2}' "/proc/$CLIENT_PID/status" 2>/dev/null || echo "$peak_rss")
elif [ "$MENU" = 1 ] && [ "$menu_up" = 1 ]; then
    # The first present is the frame the splash is on; the menu's own panels need a few seconds more to
    # build and settle. Both shots are of the menu, so a diff between them catches a half-built one.
    echo "run-live: the main menu has presented -- settling ${SECONDS_IN}s"
    half=$(( SECONDS_IN / 2 )); [ "$half" -lt 3 ] && half=3
    sleep "$half"
    python3 "$ROOT/tools/live/xtool.py" shot "$DISP" "$SHOTS/01-early.png" >> "$OUT/xtool.log" 2>&1 \
        && shots=$((shots + 1))
    if [ -n "$KEYS" ]; then
        echo "run-live: keys       $KEYS"
        python3 "$ROOT/tools/live/xtool.py" keys "$DISP" "$KEYS" >> "$OUT/xtool.log" 2>&1 \
            || echo "run-live: key injection failed (see $OUT/xtool.log)"
    fi
    if [ "$EXPECT_EXIT" -gt 0 ]; then
        echo "run-live: waiting up to ${EXPECT_EXIT}s for the click to quit the client"
        for _ in $(seq 1 $(( EXPECT_EXIT * 2 ))); do
            alive "$CLIENT_PID" || { clicked_exit=1; break; }
            sleep 0.5
        done
        echo "run-live: click test  $([ "$clicked_exit" = 1 ] && echo 'the client quit -- picking took' || echo 'the client is still up -- NOT PICKED')"
    fi
    sleep "$half"
    python3 "$ROOT/tools/live/xtool.py" shot "$DISP" "$SHOTS/02-settled.png" >> "$OUT/xtool.log" 2>&1 \
        && shots=$((shots + 1))
    [ -r "/proc/$CLIENT_PID/status" ] && peak_rss=$(awk '/VmHWM/{print $2}' "/proc/$CLIENT_PID/status" 2>/dev/null || echo "$peak_rss")
elif [ "$reached" = 1 ]; then
    echo "run-live: reached the area -- soaking ${SECONDS_IN}s"
    half=$(( SECONDS_IN / 2 )); [ "$half" -lt 1 ] && half=1
    sleep "$half"
    if python3 "$ROOT/tools/live/xtool.py" shot "$DISP" "$SHOTS/01-early.png" >> "$OUT/xtool.log" 2>&1; then
        shots=$((shots + 1))
    fi
    if [ -n "$KEYS" ]; then
        echo "run-live: keys       $KEYS"
        python3 "$ROOT/tools/live/xtool.py" keys "$DISP" "$KEYS" >> "$OUT/xtool.log" 2>&1 \
            || echo "run-live: key injection failed (see $OUT/xtool.log)"
    fi
    sleep "$half"
    if python3 "$ROOT/tools/live/xtool.py" shot "$DISP" "$SHOTS/02-settled.png" >> "$OUT/xtool.log" 2>&1; then
        shots=$((shots + 1))
    fi
    [ -r "/proc/$CLIENT_PID/status" ] && peak_rss=$(awk '/VmHWM/{print $2}' "/proc/$CLIENT_PID/status" 2>/dev/null || echo "$peak_rss")
fi

# Crashes are graded on what exists BEFORE we start shutting down: the engine installs signal handlers,
# so a SIGTERM of our own can leave a crash file that means nothing about the session.
crashes=$(find "$CRASH_DIR" -name '*.nwcrash.txt' 2>/dev/null | wc -l)
alive_before_shutdown=0
alive "$CLIENT_PID" && alive_before_shutdown=1
audio_clients=$(live_audio_clients "$RUN_TAG")

# ---- shutdown -----------------------------------------------------------------------------------
# In-game the engine swallows SDL_QUIT -- the WM close box and SDL's own SIGTERM handler both reach it
# and both are ignored -- so the clean exit has to go through the UI. Escape opens the in-game Options
# panel; the entry a fixed distance below its centre is either "Quit Game" or "Exit to Main Menu"
# depending on the session, so the sequence answers a "Quit Neverwinter Nights?" prompt AND then presses
# the main menu's own Exit and answers that prompt too. Both branches end the process; the clicks that
# land on nothing cost a few idle seconds. The offsets are pixel constants, which is only sound because
# the harness fixes the window size, so every step is best-effort and the escalation below is what
# guarantees the client is gone.
shutdown_how="already gone"
wait_gone() { for _ in $(seq 1 "$1"); do alive "$CLIENT_PID" || return 0; sleep 0.5; done; return 1; }
if [ "$alive_before_shutdown" = 1 ]; then
    shutdown_how="UI quit"
    yes_click="click:$((WIDTH / 2 - 160)),$((HEIGHT / 2 + 97))"
    menu_quit="click:$((WIDTH / 2)),$((HEIGHT - 65));sleep:1500;$yes_click;sleep:3000"
    if [ "$reached" = 1 ]; then
        python3 "$ROOT/tools/live/xtool.py" keys "$DISP" \
            "key:Escape;sleep:1200;click:$((WIDTH / 2)),$((HEIGHT / 2 + 127));sleep:1500;$yes_click;sleep:1500;$menu_quit" \
            >> "$OUT/xtool.log" 2>&1
    else
        # Never got in: the client is sitting at the main menu, so only the menu's own Exit applies.
        python3 "$ROOT/tools/live/xtool.py" keys "$DISP" "$menu_quit" >> "$OUT/xtool.log" 2>&1
    fi
    python3 "$ROOT/tools/live/xtool.py" shot "$DISP" "$SHOTS/03-quit.png" >> "$OUT/xtool.log" 2>&1
    python3 "$ROOT/tools/live/xtool.py" close "$DISP" >> "$OUT/xtool.log" 2>&1
    if ! wait_gone 20; then
        shutdown_how="SIGTERM"
        kill -TERM "$CLIENT_PID" 2>/dev/null
        if ! wait_gone 10; then
            shutdown_how="SIGKILL"
            kill -KILL "$CLIENT_PID" 2>/dev/null
            sleep 1
        fi
    fi
fi
kill -TERM "$GS_PID" 2>/dev/null
for _ in $(seq 1 20); do kill -0 "$GS_PID" 2>/dev/null || break; sleep 0.5; done
if kill -0 "$GS_PID" 2>/dev/null; then
    echo "run-live: gamescope ignored SIGTERM for 10 s -- killing it"
    kill -KILL "$GS_PID" 2>/dev/null
fi
wait "$GS_PID" 2>/dev/null
GS_EXIT=$?
sleep 2
survivors=$(live_survivors "$RUN_TAG" | tr '\n' ',' | sed 's/,$//')
live_sweep "$RUN_TAG"

# ---- collect ------------------------------------------------------------------------------------
cp -f "$CX_LOG" "$OUT/cx.log" 2>/dev/null
grep -v '^\[cx\]' "$GS_LOG" > "$ENGINE_OUT" 2>/dev/null
# nwclientLog1.txt is where the hosted module's own WriteTimestampedLogEntry lands in a +TestNewModule
# session -- one process, one log, the client's. nwserverLog1.txt is listed because the client formats
# LOGS:%sLog%01d with both names and a future build could route the server half there instead.
for f in nwengineLog.txt nwclientLog1.txt nwclientLog2.txt nwserverLog1.txt nwserverLog2.txt; do
    [ -f "$USER_DIR/logs/$f" ] && cp -f "$USER_DIR/logs/$f" "$OUT/$f"
done
cp -f "$CRASH_DIR"/*.nwcrash.txt "$OUT/" 2>/dev/null

frames=0
grep -qF "$FRAME_MARK" "$OUT/cx.log" 2>/dev/null && frames=1
module_up=0
grep -q 'LoadModule): Loading Module:' "$OUT/engine-stdout.log" 2>/dev/null && module_up=1
selfcheck_fail=$(grep -cE 'SelfCheck.*FAIL|init threw' "$OUT/cx.log" 2>/dev/null)
parts=$(grep -oE '\[alloy\] [0-9]+/[0-9]+ parts owned' "$OUT/cx.log" 2>/dev/null | tail -1 | grep -oE '[0-9]+' | head -1)
[ -n "$parts" ] || parts=0
present_count=$(grep -oE 'over [0-9]+ presents' "$OUT/cx.log" 2>/dev/null | tail -1 | grep -oE '[0-9]+')
boot_to_area="n/a"
if [ -n "$area_epoch" ]; then
    boot_to_area=$(awk -v a="$area_epoch" -v s="$START_EPOCH" 'BEGIN{printf "%.1fs", a-s}')
fi

# ---- verdict ------------------------------------------------------------------------------------
fails=()
[ "$frames" = 1 ] || [ "$STOCK" = 1 ] || fails+=("no frame was presented (CX never saw SwapBuffers)")
if [ "$MENU" = 0 ] && [ "$STOCK" = 0 ]; then
    [ -n "$present_count" ] || fails+=("no 'over N presents' period line in cx.log -- the frame count is unknown, so the soak is unproven")
fi
if [ "$MENU" = 1 ]; then
    [ "$menu_up" = 1 ] || fails+=("the main menu never presented a frame within ${BOOT_TIMEOUT}s")
    if [ "$EXPECT_EXIT" -gt 0 ] && [ "$clicked_exit" != 1 ]; then
        fails+=("the Exit click did not quit the client within ${EXPECT_EXIT}s -- the main menu did not PICK it")
    fi
else
    [ "$module_up" = 1 ]   || fails+=("the module never finished loading")
    [ "$reached" = 1 ]     || fails+=("never reached a playable area within ${BOOT_TIMEOUT}s")
fi
[ "$hang" = 0 ]        || fails+=("HANG: cx.log stopped growing for 60s with the process alive [threads: $thread_states]")
[ "$crashes" = 0 ]     || fails+=("CRASH: $crashes crash report(s) in $CRASH_DIR")
# a bindless breach is a crash on AMD's driver even where this GPU let it pass
ledger=$(grep -m1 'BindlessLedger: VIOLATION' "$OUT/cx.log" 2>/dev/null)
[ -z "$ledger" ]       || fails+=("BINDLESS: ${ledger#*BindlessLedger: }")
[ "$shots" -ge 1 ]     || fails+=("no screenshot captured")
case "$audio_clients" in
    ""|unchecked) ;;
    *) fails+=("AUDIO: the run reached the desktop's sound server [$(echo "$audio_clients" | tr '\n' ',' | sed 's/,$//')]") ;;
esac
[ -z "$survivors" ] || fails+=("LEFTOVER: process(es) outlived the run and were killed [$survivors]")
# A black world still presents frames (2026-10-01: a fog-lane polarity bug blacked out every lit pixel and PASSed):
# the settled shot's centre must be lit. --min-lit 0 turns it off for a scene that is legitimately dark.
world_lit=$(grep -o "02-settled.png [0-9x]* nonblack=[0-9/]* world_lit=[0-9]*%" "$OUT/xtool.log" 2>/dev/null | tail -1 | sed 's/.*world_lit=\([0-9]*\)%/\1/')
if [ "$reached" = 1 ] && [ -n "$world_lit" ] && [ "$world_lit" -lt "$MIN_LIT" ]; then
    fails+=("BLACK WORLD: only ${world_lit}% of the settled frame's centre is lit (floor ${MIN_LIT}%) -- look at $SHOTS/02-settled.png")
fi
world_spread=$(grep -o "02-settled.png .*world_spread=[0-9]*" "$OUT/xtool.log" 2>/dev/null | tail -1 | sed 's/.*world_spread=\([0-9]*\)/\1/')
if [ "$reached" = 1 ] && [ -n "$world_spread" ] && [ "$MIN_LIT" -gt 0 ] && [ "$world_spread" -lt 4 ]; then
    fails+=("FLAT WORLD: the settled frame's centre is a uniform fill (luma spread ${world_spread}) -- nothing drawn? look at $SHOTS/02-settled.png")
fi
[ "${selfcheck_fail:-0}" = 0 ] || fails+=("$selfcheck_fail failing SelfCheck/init line(s) in cx.log")
if [ "$clicked_exit" != 1 ]; then
    [ "$alive_before_shutdown" = 1 ] || fails+=("the client exited on its own before the soak was over")
fi

# ---- the showcase's own failures ------------------------------------------------------------------
if [ -n "$SHOWCASE" ]; then
    grep -h 'CXSHOW:' "$USER_DIR"/logs/*.txt "$ENGINE_OUT" 2>/dev/null | sed 's/.*CXSHOW:/CXSHOW:/' | sort -u > "$OUT/showcase-markers.txt"
    grep -q '^CXSHOW: enter ' "$OUT/showcase-markers.txt" || fails+=("SHOWCASE: the script never logged 'CXSHOW: enter' -- did OnClientEnter run?")
    grep -q -- '-FAILED' "$OUT/showcase-markers.txt" && fails+=("SHOWCASE: $(grep -m1 -- '-FAILED' "$OUT/showcase-markers.txt")")
    for k in $(grep -o '@[A-Za-z0-9_]*@' "$SHOW_POLICY" | sort -u | tr -d @); do
        grep -q "^CXSHOW: id $k " "$OUT/showcase-markers.txt" || fails+=("SHOWCASE: no creature id logged for @$k@")
    done
fi

# ---- the ladder's rungs -------------------------------------------------------------------------
# Each rung is one CXLADDER line the module's own script wrote; a missing one is named, because
# "the step did not happen" is the finding and a silent pass would hide it.
LADDER_STEPS=""
if [ "$LADDER" = 1 ]; then
    LADDER_MARKS="$OUT/ladder-markers.txt"
    grep -h 'CXLADDER:' "$USER_DIR"/logs/*.txt "$OUT/engine-stdout.log" 2>/dev/null \
        | sed 's/.*CXLADDER:/CXLADDER:/' | sort -u > "$LADDER_MARKS"
    for step in enter walk talk combat area2 area1 done; do
        if grep -qE "^CXLADDER: $step( |\$)" "$LADDER_MARKS"; then
            LADDER_STEPS="$LADDER_STEPS $step"
        else
            fails+=("LADDER: the '$step' rung never happened (no 'CXLADDER: $step' in the engine log)")
        fi
    done
    if grep -q 'CXLADDER: .*-FAILED' "$LADDER_MARKS"; then
        fails+=("LADDER: a rung reported its own failure -- $(grep -m1 -- '-FAILED' "$LADDER_MARKS")")
    fi
    # "the rung ran" is not "the rung did anything": the first ladder run issued its walk against an
    # area the PC was not in yet and logged a 0.0 m walk, which the step grep happily passed.
    walked=$(grep -oE 'CXLADDER: walked [0-9.]+m' "$LADDER_MARKS" | tail -1 | grep -oE '[0-9.]+')
    if [ -n "$walked" ] && awk -v d="$walked" 'BEGIN{exit !(d < 1.0)}'; then
        fails+=("LADDER: the PC covered ${walked}m -- the walk rung ran but the character never moved")
    fi
    downed=$(grep -oE 'CXLADDER: combat-result [0-9]+/' "$LADDER_MARKS" | tail -1 | grep -oE '[0-9]+')
    [ "${downed:-1}" != 0 ] || fails+=("LADDER: combat-result 0/3 -- the fight started but nothing went down")
    [ -s "$LADDER_MARKS" ] || fails+=("LADDER: no CXLADDER marker anywhere -- did OnClientEnter run? see $OUT/nwserverLog1.txt")
fi

{
    echo "module          $MODULE"
    echo "character       $CHAR"
    echo "display         $DISP (gamescope headless, ${WIDTH}x${HEIGHT})"
    echo "boot to area    $boot_to_area"
    echo "presents logged ${present_count:-unknown} (last reporting period)"
    echo "alloy parts     $parts owned at the last report"
    echo "peak RSS        $(awk -v k="$peak_rss" 'BEGIN{printf "%.0f MiB", k/1024}')"
    echo "shutdown        $shutdown_how (gamescope exit $GS_EXIT)"
    echo "screenshots     $shots in $SHOTS"
    echo "world lit       ${world_lit:-?}% of the settled frame's centre (floor ${MIN_LIT}%), luma spread ${world_spread:-?}"
    echo "crash reports   $crashes"
    echo "sound server    $([ "$audio_clients" = unchecked ] && echo 'unchecked (no pw-dump or jq)' || echo "${audio_clients:-not reached}")"
    echo "leftovers       ${survivors:-none}"
    if [ "$LADDER" = 1 ]; then
        echo "ladder rungs   ${LADDER_STEPS:- none}"
        sed 's/^/ladder          /' "$OUT/ladder-markers.txt" 2>/dev/null
    fi
} | tee "$OUT/summary.txt"

echo
if [ "${#fails[@]}" -eq 0 ]; then
    echo "PASS  reached an area, $present_count presents, no crash, no hang, $shots screenshot(s) -- $OUT"
    verdict=0
else
    echo "FAIL  $OUT"
    for f in "${fails[@]}"; do echo "  - $f"; done
    verdict=1
fi
echo "FAIL-reasons: ${#fails[@]}" >> "$OUT/summary.txt"

# The throwaway HOME is the bulky part (the engine's cache/, database/, temp/); the logs and shots stay.
if [ "$KEEP" = 0 ]; then
    rm -rf "$FAKE_HOME/.cache" "$USER_DIR/cache" "$USER_DIR/temp" "$USER_DIR/tempclient" \
           "$USER_DIR/currentgame" 2>/dev/null
fi
exit $verdict
