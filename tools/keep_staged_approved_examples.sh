#!/usr/bin/env bash
# keep-staged.sh's and where.sh's approved examples (CX-590, E1 to E3): a staged build is kept, and called CURRENT,
# only across a commit that changes comments or Markdown. Run against a fixture checkout of its own, so no build and
# no real staged folder is involved. Oracle: the fixture's sources and the expectations written by hand below.
cd "$(dirname "$0")/.."
repo=$PWD
rc=0
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

# A checkout at $tmp/<case> holding the three scripts, a C# file with comments and GLSL in strings, a shader and
# a Markdown file, committed as A, with a staged folder stamped as the build of A.
fixture() {
  local d=$tmp/$1
  mkdir -p "$d/tools" "$d/managed/Shaders" "$d/cx-linux-x64"
  cp tools/codehash.py tools/keep-staged.sh tools/where.sh "$d/tools/"
  cat > "$d/managed/Thing.cs" <<'CS'
namespace CX;

/// <summary>A thing.</summary>
internal static class Thing
{
    // the answer
    internal const int Answer = 42; /* not 41 */
    internal const string Url = "http://example.org/a//b";
    internal const string Glsl = """
        // a comment the shader keeps
        void main() { }
        """;
    // after the shader
    internal static string Hole(bool b) => $"{(b ? "//" : "/*")} {Answer}";
}
CS
  printf '// the stage\nvoid main() { gl_FragColor = vec4(1.0); }\n' > "$d/managed/Shaders/cx_thing.glsl"
  printf '# Notes\n\nSome notes.\n' > "$d/NOTES.md"
  printf 'cx-linux-x64/\n' > "$d/.gitignore"
  mkdir -p "$d/third_party/lib" && printf '# Notice\n\nCopyright the authors.\n' > "$d/third_party/lib/NOTICE.md"
  (cd "$d" && git init -q && git add -A && git -c user.email=t@t -c user.name=t commit -qm A) || exit 1
  : > "$d/cx-linux-x64/libcx.so"; : > "$d/cx-linux-x64/cx_managed.so"
  printf '#!/bin/sh\n' > "$d/cx-linux-x64/cx-launch"; chmod +x "$d/cx-linux-x64/cx-launch"
  { git -C "$d" rev-parse HEAD; echo "code $(python3 "$d/tools/codehash.py" "$d")"; } > "$d/cx-linux-x64/BUILT_FROM"
  echo "$d"
}

# Commits the edit `sed -i $2` to file $3 in checkout $1 as B.
commit_b() {
  sed -i "$2" "$1/$3" || exit 1
  (cd "$1" && git -c user.email=t@t -c user.name=t commit -qam B) || exit 1
}

keeps() {
  local out
  out=$(cd "$1" && bash tools/keep-staged.sh 2>&1) || { echo "!! $2: keep-staged refused a comment-only change -- $out"; rc=1; return; }
  local where
  where=$(bash "$1/tools/where.sh" "$1" 2>&1)
  local a; a=$(git -C "$1" rev-parse --short HEAD~1)
  case "$where" in *"build     CURRENT"*"$a"*"same code"*) ;; *) echo "!! $2: where.sh does not call it CURRENT, built from $a with the same code -- $where"; rc=1 ;; esac
}

rebuilds() {
  local out
  out=$(cd "$1" && bash tools/keep-staged.sh 2>&1) && { echo "!! $2: keep-staged kept the staged build -- $out"; rc=1; return; }
  local where
  where=$(bash "$1/tools/where.sh" "$1" 2>&1)
  case "$where" in *"build     STALE"*) ;; *) echo "!! $2: where.sh does not call it STALE -- $where"; rc=1 ;; esac
}

# E1
# Given cx-linux-x64/ built from commit A
# When commit B changes only comments in .cs files, or only Markdown files
# Then build.sh runs the gates but skips the AOT publish
# And tools/where.sh reports the staged build as CURRENT for B, built from A with the same code
d=$(fixture e1-line); commit_b "$d" 's|// the answer|// the answer to everything|' managed/Thing.cs; keeps "$d" "E1 line comment"
d=$(fixture e1-block); commit_b "$d" 's|/\* not 41 \*/|/* not 41, nor 43 */|' managed/Thing.cs; keeps "$d" "E1 block comment"
d=$(fixture e1-doc); commit_b "$d" 's|/// <summary>A thing.</summary>|/// <summary>A thing.</summary>\n/// <remarks>More.</remarks>|' managed/Thing.cs; keeps "$d" "E1 doc comment"
d=$(fixture e1-after); commit_b "$d" 's|// after the shader|// after the shader text|' managed/Thing.cs; keeps "$d" "E1 comment after a raw string"
d=$(fixture e1-md); commit_b "$d" 's|Some notes.|Other notes.|' NOTES.md; keeps "$d" "E1 Markdown"

# E2
# Given cx-linux-x64/ built from commit A
# When commit B changes a comment inside a .glsl file or inside GLSL text held in a C# string
# Then build.sh rebuilds, because self-checks pin shader text
d=$(fixture e2-glsl); commit_b "$d" 's|// the stage|// the lit stage|' managed/Shaders/cx_thing.glsl; rebuilds "$d" "E2 .glsl comment"
d=$(fixture e2-raw); commit_b "$d" 's|// a comment the shader keeps|// a comment the shader keeps too|' managed/Thing.cs; rebuilds "$d" "E2 raw string"
d=$(fixture e2-str); commit_b "$d" 's|a//b|a//c|' managed/Thing.cs; rebuilds "$d" "E2 // in a string"
d=$(fixture e2-hole); commit_b "$d" 's|"/\*")} {Answer}|"/*")} {Answer}!|' managed/Thing.cs; rebuilds "$d" "E2 text after a hole's string"

# E3
# Given cx-linux-x64/ built from commit A
# When commit B changes anything that is not a comment in a file the build reads
# Then build.sh rebuilds as it does today
d=$(fixture e3-code); commit_b "$d" 's|Answer = 42;|Answer = 41;|' managed/Thing.cs; rebuilds "$d" "E3 code"
d=$(fixture e3-glsl); commit_b "$d" 's|vec4(1.0)|vec4(0.5)|' managed/Shaders/cx_thing.glsl; rebuilds "$d" "E3 shader code"
d=$(fixture e3-notice); commit_b "$d" 's|the authors|its authors|' third_party/lib/NOTICE.md; rebuilds "$d" "E3 a Markdown notice the build ships"
d=$(fixture e3-new); printf 'namespace CX;\n' > "$d/managed/New.cs"; rebuilds "$d" "E3 a new file, uncommitted"

[ $rc = 0 ] || echo "!! keep-staged approved examples: FAILED (above)" >&2
[ $rc = 0 ] && echo ">> keep-staged approved examples: E1 to E3 pass"
exit $rc
