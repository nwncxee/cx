# Helpers run-live.sh sources, kept apart so tools/live_harness_approved_examples.sh can test them offline.

# Refuse a stage built before tools/live or the runtime sources changed: the old runtime and the new harness
# disagree (where cx.log goes, what a marker says) and the run reports a hang instead of the rebuild.
stage_stale() {
    local stage=$1 root=$2 from
    from=$(sed -n 1p "$stage/BUILT_FROM" 2>/dev/null)
    if [ -z "$from" ]; then
        echo "run-live: staged build in $stage carries no BUILT_FROM stamp: run ./build.sh"; return 1
    fi
    if ! git -C "$root" cat-file -e "$from^{commit}" 2>/dev/null \
        || ! git -C "$root" diff --quiet "$from" -- managed native build.sh tools/live packaging; then
        echo "run-live: staged build ${from:0:8} is older than tools/live or the runtime sources ($(git -C "$root" rev-parse --short=8 HEAD)): run ./build.sh"
        return 1
    fi
}

# The crash reports the engine wrote after the grade was taken, on their own line: a SIGTERM at shutdown can
# leave one that says nothing about the session, so it is named and never read as the verdict.
shutdown_crash_line() {
    local before=$1 after=$2 dir=$3 n=$(( $2 - $1 ))
    [ "$n" -gt 0 ] && echo "shutdown crash  $n written after grading, not graded ($dir)"
    return 0
}
