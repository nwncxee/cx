#!/usr/bin/env sh
# Proves the extender loads and runs C# inside a foreign process -- using a linked-here stub host so no
# game/X11/GL is needed to see it work. The stub carries the client's build-id because #457's rot gate
# refuses an unknown host before discovery; a copy of sh is then run as the rot case.
set -e
cd "$(dirname "$0")"
B="$PWD/cx-linux-x64"
# cx_managed.so opens libicu by name, as the release build does; the game's runtime carries it, a bare host
# may not, and the dev shell names one (ICU_LIB).
LIBS="$B${ICU_LIB:+:$ICU_LIB}"

# A build and a boot take the machine from whoever is measuring or playing there, a session in another checkout included.
if pgrep -x nwmain-linux >/dev/null; then
  echo "run-demo: a client is running (pgrep nwmain-linux) -- refusing to start beside it" >&2
  exit 3
fi

# Same reason as run-proton.sh: this runs the STAGED artifacts, so without a build it happily
# smoke-tests the previous edit. ~20s when nothing changed; CX_NO_BUILD=1 to run what is staged.
[ -n "$CX_NO_BUILD" ] || ./build.sh
[ -f "$B/libcx.so" ] || { echo "run ./build.sh first"; exit 1; }
# ExtenderLog writes to the logs folder of the client's user directory, which is where a real SESSION's log
# lives too -- a headless run there would push the last game session's log along its parts. Give the demo its
# own HOME and XDG_DATA_HOME so the two can never collide: the settings file and the logs folder sit in the
# client's user directory under it, and a player's cx.cfg moves the defaults the SelfCheck suites assert.
DEMOHOME="${TMPDIR:-/tmp}/cx-demo"
mkdir -p "$DEMOHOME"
# The logs folder of the user directory under a demo home: no engine reads nwn.ini here, so no alias moves it.
logs_of() { printf '%s' "$1/.local/share/Neverwinter Nights/logs"; }
LOG="$(logs_of "$DEMOHOME")/cx.log"

# #457: rot stands the WHOLE extender down, before discovery, keyed on the host's .note.gnu.build-id.
# /usr/bin/env is by definition a wrong build, so a demo that ran in it would prove the refusal and nothing
# else -- and every SelfCheck suite lives inside a service's Initialize. So the demo links a do-nothing host
# carrying a known build's id (tools/stub-host.sh) and runs in that.
# Named nwmain-*: the bootstrap loads into nothing else (the Steam runtime preloads its helpers too).
STUB="$DEMOHOME/nwmain-demo"
bash tools/stub-host.sh "$STUB" || exit 1

echo ">> launching the demo host with the extender preloaded..."
HOME="$DEMOHOME" XDG_DATA_HOME="$DEMOHOME/.local/share" CX_MANAGED="$B/cx_managed.so" LD_LIBRARY_PATH="$LIBS" LD_PRELOAD="$B/libcx.so" "$STUB" >/dev/null

echo
echo ">> $LOG (written from C#):"
cat "$LOG"

# The verdict, shared with the fast self-check run (tools/selfcheck/run.sh) so the two end on the same lines.
sh tools/selfcheck/verdict.sh "$LOG" || exit 1

# Given a game build CX does not support
# When the player starts the game
# Then cx.log, in the logs folder beside nwn.ini, says why CX stood down
# The other half of #457: the refusal itself, run for real rather than asserted in a unit. A copy of sh IS
# the rot case -- a host the offsets were never measured against -- so the gate must discover nothing.
# (sh, not env: a multi-call coreutils dispatches on argv[0] and refuses the name nwmain-rot; #633 needs it.)
ROTHOME="${TMPDIR:-/tmp}/cx-demo-rot"
rm -rf "$ROTHOME"; mkdir -p "$ROTHOME"
cp "$(readlink -f "$(command -v sh)")" "$ROTHOME/nwmain-rot"
HOME="$ROTHOME" XDG_DATA_HOME="$ROTHOME/.local/share" CX_MANAGED="$B/cx_managed.so" LD_LIBRARY_PATH="$LIBS" LD_PRELOAD="$B/libcx.so" "$ROTHOME/nwmain-rot" -c true >/dev/null
echo
ROTLOG="$(logs_of "$ROTHOME")/cx.log"
echo ">> rot case ($ROTLOG):"
grep 'host:' "$ROTLOG" || true
if ! grep -q 'standing down WHOLE' "$ROTLOG"; then
  echo ">> FAIL: rot gate did not stand the extender down in a foreign host"; exit 1
fi
if ! grep -q '0 service(s) discovered, 0 hook(s) installed' "$ROTLOG"; then
  echo ">> FAIL: the rot line did not report zero services and zero hooks"; exit 1
fi
echo ">> rot: extender stood down whole, nothing discovered."

# Given cx.cfg still holds general.logs=0 from an older CX, and a cx.log from the last launch
# When the client launches
# Then cx.log holds this launch's lines and cx.1.log holds the last launch's
OFFHOME="${TMPDIR:-/tmp}/cx-demo-logs-off"
rm -rf "$OFFHOME"; mkdir -p "$OFFHOME/.local/share/Neverwinter Nights"
echo "general.logs=0" > "$OFFHOME/.local/share/Neverwinter Nights/cx.cfg"
OFFLOGS="$(logs_of "$OFFHOME")"
mkdir -p "$OFFLOGS"
echo "the last launch" > "$OFFLOGS/cx.log"
HOME="$OFFHOME" XDG_DATA_HOME="$OFFHOME/.local/share" CX_MANAGED="$B/cx_managed.so" LD_LIBRARY_PATH="$LIBS" LD_PRELOAD="$B/libcx.so" "$STUB" >/dev/null
echo
if [ "$(cat "$OFFLOGS/cx.1.log" 2>/dev/null)" != "the last launch" ] || grep -q 'the last launch' "$OFFLOGS/cx.log" || ! grep -q 'KnobFile.SelfCheck: passed' "$OFFLOGS/cx.log"; then
  echo ">> FAIL: with an old general.logs=0, a launch did not write cx.log and rotate the last one into cx.1.log ($OFFLOGS)"; exit 1
fi
echo ">> old general.logs=0: the launch wrote cx.log and rotated the last one."

echo
echo "Attach to the real client with the launcher:"
echo "  NWN_BIN=/path/to/bin/linux-x86/nwmain-linux $B/cx-launch"
