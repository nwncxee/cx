# shellcheck shell=bash
# Sourced by run-live.sh and run-proton-live.sh: a live run reaches no speaker, and nothing it starts outlives it.
# Every process the run launches carries CX_LIVE_RUN=<tag> in its environment. Daemons such as wineserver leave
# the launch tree, so the tag, not the tree, is how a run finds its own processes.

# Exports the tag and points every audio route at a socket that does not exist. Call it in the launch subshell.
live_isolate() {
    export CX_LIVE_RUN="$1"
    export ALSOFT_DRIVERS=null
    export SDL_AUDIODRIVER=dummy SDL_AUDIO_DRIVER=dummy
    export PULSE_SERVER="unix:/proc/self/cx-no-audio"
    export PIPEWIRE_REMOTE="/proc/self/cx-no-audio"
    export JACK_NO_START_SERVER=1
}

# The pids whose environment carries tag $1, the caller's own shell excluded.
live_tagged() {
    local f pid
    for f in /proc/[0-9]*/environ; do
        pid=${f#/proc/}; pid=${pid%/environ}
        [ "$pid" = "$$" ] && continue
        grep -qzxF "CX_LIVE_RUN=$1" "$f" 2>/dev/null && echo "$pid"
    done
}

# "pid name" for each process of run $1 still alive.
live_survivors() {
    local pid
    for pid in $(live_tagged "$1"); do
        echo "$pid $(cat "/proc/$pid/comm" 2>/dev/null)"
    done
}

# Kills every process of run $1.
live_sweep() {
    local pid
    for pid in $(live_tagged "$1"); do kill -KILL "$pid" 2>/dev/null; done
    return 0
}

# "pid binary" for each sound-server client that belongs to run $1. Prints "unchecked" without pw-dump and jq.
live_audio_clients() {
    if ! command -v pw-dump >/dev/null || ! command -v jq >/dev/null; then echo "unchecked"; return 0; fi
    local tagged pid bin
    tagged=" $(live_tagged "$1" | tr '\n' ' ') "
    pw-dump 2>/dev/null \
        | jq -r '.[] | select(.type == "PipeWire:Interface:Client") | .info.props
                 | "\(.["application.process.id"] // "-") \(.["application.process.binary"] // "-")"' \
        | while read -r pid bin; do
            case "$tagged" in *" $pid "*) echo "$pid $bin" ;; esac
        done
}
