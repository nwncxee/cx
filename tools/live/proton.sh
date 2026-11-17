#!/usr/bin/env bash
# Sourced: picks the Proton that run-proton.sh and the live harness start the Windows client with.
# proton_pick sets PROTON (an explicit PROTON wins, then Steam's Proton Experimental, then the .#win shell's
# GE-Proton) and FHS, the wrapper Proton's Wine binaries need on NixOS, which has no FHS system: steam-run from
# programs.steam. It prints why and returns 1 when either is missing.
# shellcheck disable=SC2034 # PROTON and FHS are read by the scripts that source this
proton_pick() {
    local steam="$HOME/.steam/root/steamapps/common/Proton - Experimental/proton"
    if [ -z "${PROTON:-}" ]; then
        if [ -f "$steam" ]; then PROTON="$steam"; else PROTON="${PROTON_GE:-}"; fi
    fi
    [ -n "$PROTON" ] && [ -f "$PROTON" ] \
        || { echo "no Proton: install Proton Experimental in Steam, run in nix develop .#win, or set PROTON" >&2; return 1; }
    FHS=()
    if [ -f /etc/NIXOS ]; then
        command -v steam-run >/dev/null || { echo "NixOS needs steam-run on PATH (programs.steam.enable)" >&2; return 1; }
        FHS=(steam-run)
    fi
    return 0
}
