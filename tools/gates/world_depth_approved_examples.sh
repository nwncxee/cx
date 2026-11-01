#!/usr/bin/env bash
# The world depth gates' approved examples (approved-spec, CX-405 E1 to E4 and E8): planted shaders, C# and a
# GL-depth list in a throwaway tree, run through tools/gates/world-depth.sh and depth-confined.sh. Oracle: the
# planted files and the lines written by hand below.
cd "$(dirname "$0")/../.."
repo=$PWD
rc=0
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

# A fresh tree under $tmp/<case>, holding the files given as path=text pairs, with the GL-depth list's file.
plant() {
  local root=$tmp/$1; shift
  mkdir -p "$root/managed/Alloy/Shaders" "$root/tools/analyzers"
  printf '%s\n' 'private static readonly string[] Allowed =' '{' '    "/managed/Alloy/Pass/CasterPass.Vk.cs",' '};' \
    > "$root/tools/analyzers/GlDepth.cs"
  mkdir -p "$root/managed/Alloy/Pass"
  printf '%s\n' 'namespace CX.Alloy;' > "$root/managed/Alloy/Pass/CasterPass.Vk.cs"
  while [ $# -gt 0 ]; do
    mkdir -p "$(dirname "$root/${1%%=*}")"
    printf '%b' "${1#*=}" > "$root/${1%%=*}"
    shift
  done
  git -C "$root" init -q && git -C "$root" add -A
  echo "$root"
}

# Gate $1 over tree $2 must fail and print every text after them; $3.. are what its output must hold.
refuses() {
  local gate=$1 root=$2 what=$3; shift 3
  local out
  if out=$(bash "$repo/tools/gates/$gate" "$root" 2>&1); then
    echo "!! $what: $gate passed it"; rc=1; return
  fi
  for want in "$@"; do
    case "$out" in *"$want"*) ;; *) echo "!! $what: $gate's refusal does not name $want -- it said: $out"; rc=1 ;; esac
  done
}

# Gate $1 over tree $2 must pass.
passes() {
  local out
  out=$(bash "$repo/tools/gates/$1" "$2" 2>&1) || { echo "!! $3: $1 refused it -- $out"; rc=1; }
}

# E1
# Given a shader that reads the scene depth and decodes it as GL's, d * 2.0 - 1.0
# When the build runs its gates
# Then the build fails, naming that shader file and line
r=$(plant e1 "managed/Alloy/Shaders/cx_e1.glsl=float d = texture(uSceneDepth, uv).r;\nfloat z = d * 2.0 - 1.0;\n")
refuses world-depth.sh "$r" "E1" "cx_e1.glsl:2"
# Boundary: the same decode of a shadow cascade's depth passes
r=$(plant e1b "managed/Alloy/Shaders/cx_e1.glsl=float d = texture(uShadowMap, uv).r;\nfloat z = d * 2.0 - 1.0;\n")
passes world-depth.sh "$r" "E1 boundary"

# E2
# Given a helper that takes a depth texture as a parameter and decodes it as GL's
# When a shader calls that helper with the scene depth and the build runs its gates
# Then the build fails, naming the helper's line
helper="managed/Alloy/Shaders/cx_e2lib.glsl=float CxE2(sampler2D depth, vec2 uv)\n{\n    return texture(depth, uv).r * 2.0 - 1.0;\n}\n"
r=$(plant e2 "$helper" "managed/Alloy/Shaders/cx_e2.glsl=void main() { float z = CxE2(uSceneDepth, vUV); }\n")
refuses world-depth.sh "$r" "E2" "cx_e2lib.glsl:3"
# Boundary: the same helper, called only with a shadow map, passes
r=$(plant e2b "$helper" "managed/Alloy/Shaders/cx_e2.glsl=void main() { float z = CxE2(uShadowMap, vUV); }\n")
passes world-depth.sh "$r" "E2 boundary"

# E3
# Given that GLSL reads the scene depth and tests it against GL's far plane, 1.0
# When the build runs its gates
# Then the build fails, naming the C# file and line
r=$(plant e3 "managed/Ui/E3.cs=internal static class E3\n{\n    const string Fs =\n        \"  if (texelFetch(uSceneDepth, p, 0).r >= 1.0) discard;\\\\n\";\n}\n")
refuses world-depth.sh "$r" "E3" "E3.cs:4"
# Boundary: the same string testing the reversed far plane, 0.0, passes
r=$(plant e3b "managed/Ui/E3.cs=internal static class E3\n{\n    const string Fs =\n        \"  if (texelFetch(uSceneDepth, p, 0).r <= 0.0) discard;\\\\n\";\n}\n")
passes world-depth.sh "$r" "E3 boundary"

# E4
# Given a shader that turns world depth into a distance with its own copy of the formula
# When the build runs its gates
# Then the build fails and names the shared decode, CxDepthViewZ, to call instead
r=$(plant e4 "managed/Alloy/Shaders/cx_e4.glsl=float d = texture(uSceneDepth, uv).r;\nfloat v = uProj[3].z / (d + uProj[2].z);\n")
refuses world-depth.sh "$r" "E4" "cx_e4.glsl:2" "CxDepthViewZ"
# Boundary: a shader that calls CxDepthViewZ passes
r=$(plant e4b "managed/Alloy/Shaders/cx_e4.glsl=float d = texture(uSceneDepth, uv).r;\nfloat v = CxDepthViewZ(d, uProj);\n")
passes world-depth.sh "$r" "E4 boundary"

# E8
# Given a file named on the GL-depth list no longer exists
# When the build runs its gates
# Then the build fails, naming that list entry
r=$(plant e8)
git -C "$r" rm -qf managed/Alloy/Pass/CasterPass.Vk.cs
refuses depth-confined.sh "$r" "E8" "/managed/Alloy/Pass/CasterPass.Vk.cs"
# Boundary: a list whose every entry names a file passes
r=$(plant e8b)
passes depth-confined.sh "$r" "E8 boundary"

[ $rc = 0 ] || echo "!! world depth approved examples: FAILED (above)" >&2
exit $rc
