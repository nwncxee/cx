#!/usr/bin/env sh
# Proves the extender loads and runs C# inside a foreign process -- using a linked-here stub host so no
# game/X11/GL is needed to see it work. The stub carries the client's build-id because #457's rot gate
# refuses an unknown host before discovery; a copy of sh is then run as the rot case.
set -e
cd "$(dirname "$0")"
B="$PWD/cx-linux-x64"

# Same reason as run-proton.sh: this runs the STAGED artifacts, so without a build it happily
# smoke-tests the previous edit. ~20s when nothing changed; CX_NO_BUILD=1 to run what is staged.
[ -n "$CX_NO_BUILD" ] || ./build.sh
[ -f "$B/libcx.so" ] || { echo "run ./build.sh first"; exit 1; }
# ExtenderLog writes to $HOME/cx.log, which is where a real SESSION's log lives too -- a headless run
# here would truncate the log the last game session left behind, which is the only evidence a live-verified
# ticket has. Give the demo its own HOME so the two can never collide, and its own XDG_DATA_HOME: the
# settings file sits in the client's user directory under it, and a player's cx.cfg moves the defaults
# the SelfCheck suites assert.
DEMOHOME="${TMPDIR:-/tmp}/cx-demo"
mkdir -p "$DEMOHOME"
LOG="$DEMOHOME/cx.log"
# ROTATE, never delete. This is the same file the real client writes, and a client session's log is
# the only record of what happened in a live area -- deleting it means a run-demo after a play session
# destroys the evidence you were about to read. (It has: a large-area diagnosis was mid-flight.)
[ -f "$LOG" ] && mv -f "$LOG" "$LOG.prev"

# #457: rot stands the WHOLE extender down, before discovery, keyed on the host's .note.gnu.build-id.
# /usr/bin/env is by definition a wrong build, so a demo that ran in it would prove the refusal and nothing
# else -- and every SelfCheck suite lives inside a service's Initialize. So the demo links a do-nothing host
# carrying a known build's id and runs in that. The key comes from the build files, never a second copy.
BID=$(awk -F'\t' '$1 == "linux" { print $2; exit }' managed/Framework/Builds/*.tsv)
[ -n "$BID" ] || { echo ">> FAIL: no linux key in managed/Framework/Builds/*.tsv"; exit 1; }
# Named nwmain-*: the bootstrap loads into nothing else (the Steam runtime preloads its helpers too).
STUB="$DEMOHOME/nwmain-demo"
# CX-55: NwSyncReader resolves sqlite3/zstd out of the GLOBAL scope (dlsym(NULL)), so linking the system copies
# into the stub is what lets its self-check really build a store and read it from two threads at once. Not every
# box has the two -devel packages; there the check says SKIPPED, and this says which of the two ran.
if ! echo 'int main(void){return 0;}' | cc -x c - -o "$STUB" -Wl,--build-id=0x"$BID" \
       -Wl,--no-as-needed -lsqlite3 -lzstd 2>/dev/null; then
  echo ">> no libsqlite3/libzstd to link: NwSyncReader.SelfCheck will SKIP (install them to run it)"
  echo 'int main(void){return 0;}' | cc -x c - -o "$STUB" -Wl,--build-id=0x"$BID"
fi

echo ">> launching the demo host with the extender preloaded..."
HOME="$DEMOHOME" XDG_DATA_HOME="$DEMOHOME/.local/share" CX_MANAGED="$B/cx_managed.so" LD_LIBRARY_PATH="$B" LD_PRELOAD="$B/libcx.so" "$STUB" >/dev/null

echo
echo ">> $LOG (written from C#):"
cat "$LOG"

# The gate: the SelfCheck suites assert real invariants, and until #157 the log could say FAILED
# all day and CI still went green. Six spellings exist across the call sites -- FAILED, FAIL, both
# colon forms, and "had FAILURES" -- so match the suite name and the word loosely rather than
# enumerating them. A log with no SelfCheck line at all is also a failure: the services never ran.
echo
if ! grep -q 'SelfCheck' "$LOG"; then
  echo ">> FAIL: no SelfCheck lines in $LOG -- services never ran"; exit 1
fi
if grep -qE 'SelfCheck.*FAIL' "$LOG"; then
  echo ">> FAIL: a SelfCheck suite failed:"; grep -E 'SelfCheck.*FAIL' "$LOG"; exit 1
fi
# A suite that THROWS takes its service's whole Initialize with it and prints no FAILED line -- #673 lost 29
# SelfCheck lines to an ArgumentOutOfRange and the gate above went green.
if grep -q 'init threw' "$LOG"; then
  echo ">> FAIL: a service's Initialize threw:"; grep 'init threw' "$LOG"; exit 1
fi
# A suite that stood down is neither a pass nor a failure, and it is named here so "none failed" cannot
# be read as "all ran" (CX-200: every skip goes through Checker.Skip and says SKIPPED).
echo ">> $(grep -c 'SelfCheck' "$LOG") SelfCheck lines, none failed, $(grep -c 'SelfCheck.*SKIPPED' "$LOG") SKIPPED."
grep -E 'SelfCheck.*SKIPPED' "$LOG" | sed 's/^/>>   /' || true

# The other half of #457: the refusal itself, run for real rather than asserted in a unit. A copy of sh IS
# the rot case -- a host the offsets were never measured against -- so the gate must discover nothing.
# (sh, not env: a multi-call coreutils dispatches on argv[0] and refuses the name nwmain-rot; #633 needs it.)
ROTHOME="${TMPDIR:-/tmp}/cx-demo-rot"
rm -rf "$ROTHOME"; mkdir -p "$ROTHOME"
cp "$(readlink -f "$(command -v sh)")" "$ROTHOME/nwmain-rot"
HOME="$ROTHOME" XDG_DATA_HOME="$ROTHOME/.local/share" CX_MANAGED="$B/cx_managed.so" LD_LIBRARY_PATH="$B" LD_PRELOAD="$B/libcx.so" "$ROTHOME/nwmain-rot" -c true >/dev/null
echo
echo ">> rot case ($ROTHOME/cx.log):"
grep 'host:' "$ROTHOME/cx.log" || true
if ! grep -q 'standing down WHOLE' "$ROTHOME/cx.log"; then
  echo ">> FAIL: rot gate did not stand the extender down in a foreign host"; exit 1
fi
if ! grep -q '0 service(s) discovered, 0 hook(s) installed' "$ROTHOME/cx.log"; then
  echo ">> FAIL: the rot line did not report zero services and zero hooks"; exit 1
fi
echo ">> rot: extender stood down whole, nothing discovered."

echo
echo "Attach to the real client with the launcher:"
echo "  NWN_BIN=/path/to/bin/linux-x86/nwmain-linux $B/cx-launch"
