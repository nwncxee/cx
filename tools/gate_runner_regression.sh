#!/usr/bin/env bash
# tools/gates/run.sh and tools/gates/approved-lines.sh in a scratch tree with planted gates and scripts: a failing
# gate fails the run and is named, --for keeps only the gates that reach a path, and an approved-examples script
# without an ordered Given, When and Then is refused, a hung gate is stopped at its timeout, the real gates an edit
# reaches leave out the ones that read elsewhere, and the scanners finish on an unterminated comment. Oracle: the exit
# codes and the names each prints.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2
rc=0
fail() { echo "!! gate runner: $*" >&2; rc=1; }
t=$(mktemp -d); trap 'rm -rf "$t"' EXIT
mkdir -p "$t/tools/gates" && cp tools/gates/run.sh tools/gates/approved-lines.sh "$t/tools/gates/"
printf '#!/usr/bin/env bash\nexit 0\n' > "$t/tools/gates/ok.sh"
printf '#!/usr/bin/env bash\n# reaches: docs/*\nexit 0\n' > "$t/tools/gates/docs-only.sh"
printf '#!/usr/bin/env bash\necho planted failure; exit 1\n' > "$t/tools/gates/bad.sh"
(cd "$t" && bash tools/gates/run.sh) > "$t/out" 2>&1 && fail "a failing gate left the run green"
grep -q 'gate failed: tools/gates/bad.sh' "$t/out" || fail "the failing gate is not named"
grep -q 'planted failure' "$t/out" || fail "the failing gate's log is not shown"
rm "$t/tools/gates/bad.sh"
(cd "$t" && bash tools/gates/run.sh) > /dev/null 2>&1 || fail "passing gates failed the run"
mine=$(cd "$t" && bash tools/gates/run.sh --list --for managed/X.cs | tr '\n' ' ')
[ "$mine" = "tools/gates/ok.sh " ] || fail "--for managed/X.cs chose '$mine'"
mine=$(cd "$t" && bash tools/gates/run.sh --list --for docs/a.md | tr '\n' ' ')
[ "$mine" = "tools/gates/docs-only.sh tools/gates/ok.sh " ] || fail "--for docs/a.md chose '$mine'"
mine=$(cd "$t" && bash tools/gates/run.sh --list --for tools/x_approved_examples.sh | tr '\n' ' ')
[ "$mine" = "tools/gates/approved-lines.sh tools/gates/ok.sh " ] || fail "--for tools/x_approved_examples.sh chose '$mine'"

for a in --for --timeout; do
  (cd "$t" && timeout 10 bash tools/gates/run.sh "$a") > /dev/null 2>&1
  case $? in 124) fail "run.sh $a with no value never ended" ;; 0) fail "run.sh $a with no value passed" ;; esac
done

printf '#!/usr/bin/env bash\nsleep 60\n' > "$t/tools/gates/hung.sh"
start=$SECONDS
(cd "$t" && timeout 30 bash tools/gates/run.sh --timeout 1) > "$t/out" 2>&1 && fail "a hung gate left the run green"
[ $((SECONDS - start)) -lt 10 ] || fail "a hung gate held the run $((SECONDS - start)) s past a 1 s timeout"
grep -q 'gate failed: tools/gates/hung.sh' "$t/out" || fail "the hung gate is not named"
rm "$t/tools/gates/hung.sh"

real=$(bash tools/gates/run.sh --list --for docs/README.md)
for g in statics alias-lanes knob-effect selfcheck file-size compute-pipes; do
  grep -qx "tools/gates/$g.sh" <<< "$real" && fail "an edit to docs/README.md runs $g, which reads only managed/"
done
grep -qx tools/gates/local-paths.sh <<< "$real" || fail "an edit to docs/README.md skips local-paths, which reads every file"
grep -qx tools/gates/retired-terms.sh <<< "$real" || fail "an edit to docs/README.md skips retired-terms, which reads docs/"
real=$(bash tools/gates/run.sh --list --for tools/gates/statics.baseline)
grep -qx tools/gates/statics.sh <<< "$real" || fail "an edit to a gate's baseline skips that gate"

mkdir -p "$t/scan/managed" "$t/scan/docs/spec"
printf 'class A { void F() { Foo(1, /* never closed\n' > "$t/scan/managed/A.cs"
for py in knob-effect knob-spec-rows; do
  CX_ROOT="$t/scan" timeout 20 python3 -I "tools/gates/$py.py" > /dev/null 2>&1
  [ $? = 124 ] && fail "$py.py never finished on an unterminated /* comment"
done

# A class that declares its rows on a KnobGroup it is handed takes the group each Declare(<group>) site passes.
mkdir -p "$t/param/managed" "$t/param/docs/spec"
cat > "$t/param/managed/A.cs" << 'EOF2'
static class General { public static readonly KnobGroup About = KnobGroup.Declare("about", "General", "About"); }
sealed class Notes { private Notes(KnobGroup g) { g.Toggle("ping", "Ping", false, help: "h"); } public static Notes Declare(KnobGroup g) => new(g); }
sealed class Wire { void Init() { Notes.Declare(General.About); } }
EOF2
for i in $(seq 100); do echo "static class F$i { static readonly KnobGroup G = KnobGroup.Declare(\"f$i\"); static void I() { G.Toggle(\"k\", \"K\", false); } }"; done >> "$t/param/managed/A.cs"
row() {
  { printf '| key | label |\n|---|---|\n%s\n' "$1"; for i in $(seq 100); do echo "| f$i.k | K |"; done; } > "$t/param/docs/spec/unified-settings.md"
  CX_ROOT="$t/param" python3 -I tools/gates/knob-spec-rows.py 2>&1
}
out=$(row '| about.ping | Ping |'); [ -z "$out" ] || fail "knob-spec-rows refused about.ping, declared through Notes.Declare(General.About) -- $out"
out=$(row '| about.other | Other |')
grep -q "about.ping is declared" <<< "$out" || fail "knob-spec-rows did not name about.ping, declared with no row -- $out"

# A Declare that hands its group on to another class's Declare passes that class the group its own site names.
cat > "$t/param/managed/A.cs" << 'EOF2'
static class General { public static readonly KnobGroup About = KnobGroup.Declare("about", "General", "About"); }
sealed class Notes { private Notes(KnobGroup g, string s) { g.Toggle("ping", "Ping", false, help: "h"); } public static Notes Declare(KnobGroup g, string s) => new(g, s); }
sealed class Wire { void Init() { Declare(General.About, "x"); } internal static Notes Declare(KnobGroup about, string s) => Notes.Declare(about, s); }
static class Fen { static readonly KnobGroup g = KnobGroup.Declare("fen", "General", "Fen"); }
EOF2
printf 'class W { void T(KnobGroup g) { Wire.Declare(g, "y"); } }\n' > "$t/param/managed/W.SelfCheck.cs"
for i in $(seq 100); do echo "static class F$i { static readonly KnobGroup G = KnobGroup.Declare(\"f$i\"); static void I() { G.Toggle(\"k\", \"K\", false); } }"; done >> "$t/param/managed/A.cs"
out=$(row '| about.ping | Ping |'); [ -z "$out" ] || fail "knob-spec-rows refused about.ping, declared through Wire.Declare(General.About, ...), with a self-check passing a g that names another group -- $out"

lines() { (cd "$t" && git init -q 2>/dev/null; printf '%s\n' "$@" > tools/x_approved_examples.sh; bash tools/gates/approved-lines.sh) > /dev/null 2>&1; }
lines '# Given a' '# When b' '# Then c' || fail "approved-lines refused an ordered Given, When and Then"
lines '# Given a' '# When b' && fail "approved-lines passed a script with no Then"
lines '# Then c' '# When b' '# Given a' && fail "approved-lines passed Given, When and Then out of order"
[ $rc = 0 ] && echo "gate runner regression: passed"
exit $rc
