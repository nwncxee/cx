#!/usr/bin/env bash
# Sourced, not run: the staging the live scripts that load the docks quicksave share. Set DOCKS_WHO to the name the
# sourcing script prints its own errors under, then:
#
#   DOCKS_WHO=prewarm; . "$ROOT/tools/live/docks_run.sh"
#   docks_saves "$SAVES" || exit 2     # "" asks for a throwaway copy of the user's own docks quicksave
#   out="$(docks_run holdpre1 --cfg 'grass.enabled=false')" || exit $?   # 2: no run was made
#
# docks_saves copies the save rather than loading the user's own, and removes the copy when the script exits; it sets
# DOCKS_SAVES rather than printing it, because a copy staged inside a command substitution would be removed the moment
# that subshell ended. perf-save.sh gives the run a throwaway home, so nothing of the user's is read or written.
#
# Removing the copy is an EXIT trap, and it REPLACES the sourcing script's own: a caller that wants one installs it
# before staging and removes the copy itself, or chains the two.
: "${DOCKS_WHO:=docks}"
DOCKS_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

# The seconds a docks run is soaked for, long enough for the area's loading hold to lift and the first ten seconds
# after it to be counted.
DOCKS_SOAK=30

# The folder behind a `--saves` option, or 2 when the option was given with nothing after it: an empty value reads as
# "no --saves" to docks_saves, which would stage the user's own save -- the one thing the option is there to avoid.
docks_saves_arg() {
    [ -n "${1:-}" ] || { echo "$DOCKS_WHO: --saves needs a folder holding the docks save" >&2; return 2; }
    printf '%s\n' "$1"
}

# Sets DOCKS_SAVES to the folder perf-save.sh loads the docks save out of: $1 when the caller was given one, else a
# throwaway copy of the user's own quicksave, removed when this script exits. 2 when there is no docks save to copy.
docks_saves() {
    if [ -n "${1:-}" ]; then
        [ -d "$1" ] || { echo "$DOCKS_WHO: --saves needs a folder holding the docks save" >&2; return 2; }
        DOCKS_SAVES=$1
        return 0
    fi
    local userdir save saves
    userdir="$(python3 "$DOCKS_ROOT/tools/paths.py" NWN_USERDIR)" || return 2
    save="$userdir/saves/000000 - quicksave"
    grep -q Waterdeep "$save/savenfo.txt" 2>/dev/null \
        || { echo "$DOCKS_WHO: $save is not the docks save (savenfo.txt)" >&2; return 2; }
    saves="$(mktemp -d "${TMPDIR:-/tmp}/cx-$DOCKS_WHO-saves-XXXX")" || return 2
    cp -a "$save" "$saves/" || return 2
    # shellcheck disable=SC2064 # the folder to remove is this one, fixed now, not whatever $saves holds at exit
    trap "rm -rf '$saves'" EXIT
    DOCKS_SAVES=$saves
}

# The output folder perf-save.sh left for label $1 after epoch second $2: the newest by modification time, since a
# label is reused run to run. 2 when there is none, rather than an earlier run's folder.
# perf-save.sh names the folder after the clock time, which only sorts by name within one day, so this sorts by the
# time the folder was actually made.
docks_out() {
    local out
    out="$(find "${TMPDIR:-/tmp}" -maxdepth 1 -type d -name "cx-perf-$1-*" -newermt "@$2" -printf '%T@\t%p\n' \
           | sort -n | tail -1 | cut -f2-)"
    [ -n "$out" ] || { echo "$DOCKS_WHO: the run left no output folder for $1" >&2; return 2; }
    printf '%s\n' "$out"
}

# One docks run out of $DOCKS_SAVES under label $1, the rest of the arguments perf-save.sh's own; prints the output
# folder it left, which holds a cx.log that reached the area. 2 when no run was made or it never left the loading
# screen, so a caller never grades a run that did not happen.
docks_run() {
    local label="$1" saves="${DOCKS_SAVES:?docks_run: set DOCKS_SAVES from docks_saves first}"
    shift
    local before out rc
    before="$(date +%s)"
    "$DOCKS_ROOT/tools/live/perf-save.sh" --saves "$saves" --soak "$DOCKS_SOAK" --label "$label" "$@" >/dev/null
    rc=$?
    [ "$rc" = 3 ] && { echo "$DOCKS_WHO: a client is already running" >&2; return 2; }
    [ "$rc" = 0 ] || { echo "$DOCKS_WHO: perf-save.sh exited $rc" >&2; return 2; }
    out="$(docks_out "$label" "$before")" || return 2
    [ -f "$out/cx.log" ] || { echo "$DOCKS_WHO: no cx.log from the run ($out)" >&2; return 2; }
    grep -q "LightmapBake: hold released" "$out/cx.log" \
        || { echo "$DOCKS_WHO: the area never left its loading screen ($out/cx.log)" >&2; return 2; }
    printf '%s\n' "$out"
}
