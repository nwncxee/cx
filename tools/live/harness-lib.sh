# Helpers run-live.sh sources, kept apart so tools/live_harness_approved_examples.sh can test them offline.

# Refuse a stage built before tools/live or the runtime sources changed: the old runtime and the new harness
# disagree (where cx.log goes, what a marker says) and the run reports a hang instead of the rebuild. A
# commit since the stamp, or an uncommitted or untracked source file newer than the stamp, makes it stale; after
# a dirty build any source file newer than the stamp does, since a revert also leaves the stage behind.
STAGE_SOURCES=(managed native build.sh tools/live packaging)
stage_stale() {
    local stage=$1 root=$2 from newer
    from=$(sed -n 1p "$stage/BUILT_FROM" 2>/dev/null)
    if [ -z "$from" ]; then
        echo "run-live: staged build in $stage carries no BUILT_FROM stamp: run ./build.sh"; return 1
    fi
    if ! git -C "$root" cat-file -e "$from^{commit}" 2>/dev/null \
        || ! git -C "$root" diff --quiet "$from" HEAD -- "${STAGE_SOURCES[@]}"; then
        echo "run-live: staged build ${from:0:8} is older than tools/live or the runtime sources ($(git -C "$root" rev-parse --short=8 HEAD)): run ./build.sh"
        return 1
    fi
    local which=(-m -o)
    grep -qx dirty "$stage/BUILT_FROM" && which=(-c -o)
    newer=$(cd "$root" && git ls-files "${which[@]}" --exclude-standard -- "${STAGE_SOURCES[@]}" \
        | while read -r f; do [ "$f" -nt "$stage/BUILT_FROM" ] && echo "$f"; done | head -3)
    if [ -n "$newer" ]; then
        echo "run-live: uncommitted edits are newer than staged build ${from:0:8} ($(echo $newer)): run ./build.sh"
        return 1
    fi
}

# The crash reports the engine wrote after the grade was taken, on their own line: a SIGTERM at shutdown can
# leave one that says nothing about the session, so it is named and never read as the verdict.
shutdown_crash_line() {
    local before=$1 after=$2
    [[ $before =~ ^[0-9]+$ && $after =~ ^[0-9]+$ ]] || return 0
    [ "$after" -gt "$before" ] && echo "shutdown crash  $(( after - before )) written after grading, not graded"
    return 0
}

# The recipe table's fault clauses on a session's frame lines, comma-joined; empty when there are none. The keys
# come from the line the client logs at boot, so a renamed key cannot disarm the grade.
recipe_fault_lines() {
    local log=$1 keys
    keys=$(grep -aoP 'dbfeed: recipe fault keys: \K.+' "$log" | tail -n 1)
    if [ -z "$keys" ]; then
        grep -aq 'dbfeed: frames' "$log" && echo "no recipe fault keys in cx.log"
        return 0
    fi
    grep -aoE "($keys) [0-9]+" "$log" | LC_ALL=C sort -u | paste -sd, -
}
