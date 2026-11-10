#!/usr/bin/env bash
# Regression check, not an approved test: the staging the four docks live scripts shared before they sourced one
# helper, so it could not go red first. It exercises tools/live/docks_run.sh against fixture folders -- no GPU, no
# client, no game -- which is why build.sh can run it. Exit 0 PASS, 1 FAIL.
#
#   tools/docks_staging_regression.sh
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/cx-docks-staging-XXXX")"
trap 'rm -rf "$WORK"' EXIT
fails=0
ok() { echo "  ok: $1"; }
bad() { echo "FAIL: $1" >&2; fails=$((fails + 1)); }

# A user directory whose quicksave savenfo names $2, under $WORK/$1.
userdir() {
    local d="$WORK/$1"
    mkdir -p "$d/saves/000000 - quicksave"
    printf 'NAME: a save\nAREANAME: %s\n' "$2" > "$d/saves/000000 - quicksave/savenfo.txt"
    printf 'bytes\n' > "$d/saves/000000 - quicksave/player.bic"
    printf '%s\n' "$d"
}

# docks_saves / docks_out in a subshell of their own, so one case's traps and temporary folders cannot reach the next.
# `staged` prints the DOCKS_SAVES it set and exits with its status; `outdir` prints what docks_out printed. A staged
# copy is removed when its subshell exits, so the case that reads the copy reads it inside one (see `staging`).
staged() { ( DOCKS_WHO=regression; . "$ROOT/tools/live/docks_run.sh"
             docks_saves "${1-}" || exit $?; printf '%s\n' "$DOCKS_SAVES" ); }
outdir() { ( DOCKS_WHO=regression; . "$ROOT/tools/live/docks_run.sh"; docks_out "$1" "$2" ); }

# Stages the user's quicksave, writes DOCKS_SAVES to $1 and, while that copy still exists, whether it holds the
# save's savenfo.txt to $2.
staging() {
    ( DOCKS_WHO=regression; . "$ROOT/tools/live/docks_run.sh"
      docks_saves || exit $?
      printf '%s\n' "$DOCKS_SAVES" > "$1"
      [ -f "$DOCKS_SAVES/000000 - quicksave/savenfo.txt" ] && printf 'copied\n' > "$2"
      exit 0 )
}

[ -f "$ROOT/tools/live/docks_run.sh" ] || { echo "FAIL: no tools/live/docks_run.sh to check" >&2; exit 1; }

# 1. Given a user directory whose `000000 - quicksave` holds a `savenfo.txt` naming Waterdeep
#    When a docks script stages the save
#    Then it is handed a temporary folder holding a copy of that save, and the user's own folder is untouched
mine="$(userdir good Waterdeep)"
where="$WORK/where" copied="$WORK/copied"
NWN_USERDIR="$mine" staging "$where" "$copied"; rc=$?
got="$(cat "$where" 2>/dev/null)"
if [ "$rc" != 0 ]; then bad "staging a Waterdeep quicksave exited $rc"
elif [ ! -s "$copied" ]; then bad "the staged folder $got held no copy of the save"
elif [ "$got" = "$mine/saves" ]; then bad "staging handed back the user's own saves folder"
elif [ ! -f "$mine/saves/000000 - quicksave/player.bic" ]; then bad "staging moved the user's own save instead of copying it"
else ok "a Waterdeep quicksave is staged as a copy, the user's own folder untouched"; fi

# 1b. ... and the copy is removed once the script that staged it has exited
if [ -z "$got" ]; then bad "staging printed no folder to check the removal of"
elif [ -e "$got" ]; then bad "the staged copy $got outlived the script that staged it"
else ok "the staged copy is removed when the script that staged it exits"; fi

# 2. Given a user directory whose quicksave `savenfo.txt` names somewhere other than Waterdeep
#    When a docks script stages the save
#    Then staging refuses with exit 2 and copies nothing
other="$(userdir elsewhere Neverwinter)"
before="$(find "${TMPDIR:-/tmp}" -maxdepth 1 -name 'cx-regression-saves-*' | wc -l)"
got="$(NWN_USERDIR="$other" staged 2>/dev/null)"; rc=$?
after="$(find "${TMPDIR:-/tmp}" -maxdepth 1 -name 'cx-regression-saves-*' | wc -l)"
if [ "$rc" != 2 ]; then bad "a quicksave from somewhere else exited $rc, not 2 (it printed '$got')"
elif [ "$before" != "$after" ]; then bad "a quicksave from somewhere else was copied anyway"
else ok "a quicksave that is not the docks refuses with 2 and copies nothing"; fi

# 3. Given a user directory with no quicksave at all
#    When a docks script stages the save
#    Then staging refuses with exit 2
mkdir -p "$WORK/empty"
NWN_USERDIR="$WORK/empty" staged >/dev/null 2>&1; rc=$?
if [ "$rc" = 2 ]; then ok "a user directory with no quicksave refuses with 2"; else bad "no quicksave exited $rc, not 2"; fi

# 4. Given `--saves DIR` naming a folder that does not exist
#    When a docks script stages the save
#    Then staging refuses with exit 2
staged "$WORK/not-here" >/dev/null 2>&1; rc=$?
if [ "$rc" = 2 ]; then ok "a --saves folder that does not exist refuses with 2"; else bad "a missing --saves folder exited $rc, not 2"; fi

# 5. Given `--saves DIR` naming a folder that does exist
#    When a docks script stages the save
#    Then that folder is used as given and nothing is copied
mkdir -p "$WORK/given"
got="$(staged "$WORK/given")"; rc=$?
if [ "$rc" != 0 ]; then bad "an explicit --saves folder exited $rc"
elif [ "$got" != "$WORK/given" ]; then bad "an explicit --saves folder came back as '$got'"
else ok "an explicit --saves folder is used as given"; fi

# 6. Given an older output folder for label L and two this run left
#    When a docks script asks for its run's output folder
#    Then it is handed the newest of them, not an older one and not whichever sorts last by name
# The two this run left are named against the clock order on purpose: `zzz` is the older one, so a search that takes
# the last name, or the first of the newest-first order, answers with the wrong folder.
runs="$WORK/runs"
mkdir -p "$runs/cx-perf-fixture-old" "$runs/cx-perf-fixture-zzz" "$runs/cx-perf-fixture-aaa" "$runs/cx-perf-other-aaa"
touch -d "@$(($(date +%s) - 600))" "$runs/cx-perf-fixture-old"
mark=$(date +%s); sleep 1
touch -d "@$((mark + 1))" "$runs/cx-perf-fixture-zzz" "$runs/cx-perf-other-aaa"
touch -d "@$((mark + 2))" "$runs/cx-perf-fixture-aaa"
got="$(TMPDIR="$runs" outdir fixture "$mark")"; rc=$?
if [ "$rc" != 0 ]; then bad "finding the run's output folder exited $rc"
elif [ "$got" != "$runs/cx-perf-fixture-aaa" ]; then bad "the run's output folder came back as '$got', not the newest one this run left"
else ok "the newest output folder for the label, made after the run started, is the one found"; fi

# 7. Given no output folder for label L newer than the run started
#    When a docks script asks for its run's output folder
#    Then it refuses with exit 2 rather than grading an earlier run's log
sleep 1
got="$(TMPDIR="$runs" outdir fixture "$(date +%s)" 2>/dev/null)"; rc=$?
if [ "$rc" != 2 ]; then bad "no output folder for this run exited $rc, not 2 (it printed '$got')"
else ok "no output folder newer than the run refuses with 2"; fi

# 8. Given `--saves` with no folder after it
#    When a docks script reads its options
#    Then it refuses with exit 2, rather than reading as "no --saves" and staging the user's own save
savesarg() { ( DOCKS_WHO=regression; . "$ROOT/tools/live/docks_run.sh"; docks_saves_arg "${1-}" ); }
got="$(savesarg 2>/dev/null)"; rc=$?
if [ "$rc" != 2 ]; then bad "--saves with no folder after it exited $rc, not 2 (it printed '$got')"
elif [ -n "$got" ]; then bad "--saves with no folder after it came back as '$got'"
else ok "--saves with no folder after it refuses with 2"; fi
got="$(savesarg "$WORK/given")"; rc=$?
if [ "$rc" != 0 ] || [ "$got" != "$WORK/given" ]; then bad "--saves with a folder exited $rc and printed '$got'"
else ok "--saves with a folder hands that folder on"; fi

[ "$fails" = 0 ] && { echo "PASS: the docks staging helper answers all 10 examples"; exit 0; }
echo "FAIL: $fails of the docks staging examples" >&2
exit 1
