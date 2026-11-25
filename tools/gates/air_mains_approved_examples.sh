#!/usr/bin/env bash
# The air-mains gate's approved examples: planted shaders in a throwaway tree, run through tools/gates/air-mains.sh.
# Oracle: the planted files and the lines written by hand below.
cd "$(dirname "$0")/../.."
repo=$PWD
rc=0
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

# A fresh tree under $tmp/<case> holding the mote and wisp mains as shipped, then the files given as path=text pairs.
plant() {
  local root=$tmp/$1; shift
  mkdir -p "$root/managed/Alloy/Shaders"
  printf '%b' '#include "cx_air.glsl"\nvoid main() { vec3 c = CxAirLit(vec3(0.0), vec3(1.0), 1.0, 5.0) + CxAirSky(vec3(0.5), vec3(1.0)); }\n' > "$root/managed/Alloy/Shaders/cx_airmote_cs.glsl"
  printf '%b' '#include "cx_air.glsl"\nvoid main() { vec3 c = CxAirSky(vec3(0.5), vec3(1.0)); }\n' > "$root/managed/Alloy/Shaders/cx_airwisp_cs.glsl"
  while [ $# -gt 0 ]; do
    printf '%b' "${1#*=}" > "$root/${1%%=*}"
    shift
  done
  echo "$root"
}

# The gate over tree $1 must fail, its output holding each text after the label $2.
refuses() {
  local root=$1 what=$2; shift 2
  local out
  if out=$(bash "$repo/tools/gates/air-mains.sh" "$root" 2>&1); then
    echo "!! $what: air-mains.sh passed it"; rc=1; return
  fi
  for want in "$@"; do
    case "$out" in *"$want"*) ;; *) echo "!! $what: the refusal does not name $want -- it said: $out"; rc=1 ;; esac
  done
}

# The gate over tree $1 must pass.
passes() {
  local out
  out=$(bash "$repo/tools/gates/air-mains.sh" "$1" 2>&1) || { echo "!! $2: air-mains.sh refused it -- $out"; rc=1; }
}

dust='managed/Alloy/Shaders/cx_airdust_cs.glsl=#include "cx_air.glsl"\nvoid main() { float d = CxAirDust(0.1, 0.3); vec3 c = CxAirLit(vec3(0.0), vec3(1.0), 1.0, 5.0); }\n'

# Given the dust main includes cx_air and calls both its looks
# When the build runs its gates
# Then the gate passes
passes "$(plant ok "$dust")" "the shipped shape"

# Given the dust main lays its own profile instead of calling CxAirDust
# When the build runs its gates
# Then the build fails, naming that main and CxAirDust
r=$(plant own 'managed/Alloy/Shaders/cx_airdust_cs.glsl=#include "cx_air.glsl"\nvoid main() { float d = 2.0 * (1.0 - 0.1 / 0.3) / 0.3; vec3 c = CxAirLit(vec3(0.0), vec3(1.0), 1.0, 5.0); }\n')
refuses "$r" "own profile" "cx_airdust_cs.glsl" "CxAirDust"

# Given the dust main names CxAirLit only in a comment
# When the build runs its gates
# Then the build fails, naming CxAirLit
r=$(plant comment 'managed/Alloy/Shaders/cx_airdust_cs.glsl=#include "cx_air.glsl"\n// CxAirLit(ambient, lamp, d, r) would light it\nvoid main() { float d = CxAirDust(0.1, 0.3); }\n')
refuses "$r" "a call in a comment" "CxAirLit"

# Given a new air main the gate's list does not name
# When the build runs its gates
# Then the build fails, naming that main
r=$(plant new "$dust" 'managed/Alloy/Shaders/cx_airmist_cs.glsl=#include "cx_air.glsl"\nvoid main() { }\n')
refuses "$r" "an unlisted main" "cx_airmist_cs.glsl"

# Given the dust main calls both looks but copies them in rather than including cx_air
# When the build runs its gates
# Then the build fails, naming that main and cx_air.glsl
r=$(plant copied 'managed/Alloy/Shaders/cx_airdust_cs.glsl=float CxAirDust(float z, float t) { return 0.0; }\nvoid main() { float d = CxAirDust(0.1, 0.3); vec3 c = CxAirLit(vec3(0.0), vec3(1.0), 1.0, 5.0); }\n')
refuses "$r" "a copied library" "cx_airdust_cs.glsl" "cx_air.glsl"

# Given the gate's list names the dust main and no such stage is in the tree
# When the build runs its gates
# Then the build fails, naming the missing main
r=$(plant gone 'managed/Alloy/Shaders/cx_air.glsl=float CxAirDust(float z, float t) { return 0.0; }\n')
refuses "$r" "a listed main that is gone" "cx_airdust_cs.glsl" "not in the tree"

# Given the dust main names CxAirDust only inside a block comment
# When the build runs its gates
# Then the build fails, naming CxAirDust
r=$(plant block 'managed/Alloy/Shaders/cx_airdust_cs.glsl=#include "cx_air.glsl"\n/* CxAirDust(z, top) would lay it */\nvoid main() { vec3 c = CxAirLit(vec3(0.0), vec3(1.0), 1.0, 5.0); }\n')
refuses "$r" "a call in a block comment" "CxAirDust"

# Given a tree with no shader folder to read, as from a wrong root
# When the build runs its gates
# Then the build fails, saying it found nothing to read
mkdir -p "$tmp/empty"
refuses "$tmp/empty" "no shader folder" "no managed/Alloy/Shaders to read"

exit $rc
