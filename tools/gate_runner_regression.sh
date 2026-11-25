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

# A method of any name that takes a KnobGroup passes on every group its sites name, through any number of hops.
cat > "$t/param/managed/A.cs" << 'EOF2'
static class General { public static readonly KnobGroup About = KnobGroup.Declare("about", "General", "About"); }
static class Fen { public static readonly KnobGroup Fe = KnobGroup.Declare("fen", "General", "Fen"); }
sealed class Notes { private Notes(KnobGroup g, string s) { g.Toggle("ping", "Ping", false, help: "h"); } public static Notes Declare(KnobGroup g, string s) => new(g, s); }
sealed class Wire { void Init() { Hand(General.About, "x"); Outer.Pass(Fen.Fe); } internal static Notes Hand(KnobGroup about, string s) => Notes.Declare(about, s); }
static class Outer { internal static void Pass(KnobGroup g) { Wire.Hand(g, "z"); } }
static class Lone { static readonly KnobGroup Lo = KnobGroup.Declare("lone", "General", "Lone"); void I() { UnHand(Lo, "q"); } static void UnHand(object x, string s) { } }
EOF2
rm "$t/param/managed/W.SelfCheck.cs"
for i in $(seq 100); do echo "static class F$i { static readonly KnobGroup G = KnobGroup.Declare(\"f$i\"); static void I() { G.Toggle(\"k\", \"K\", false); } }"; done >> "$t/param/managed/A.cs"
out=$(row '| about.ping | Ping |')
grep -q "fen.ping is declared" <<< "$out" || fail "knob-spec-rows did not name fen.ping, handed Fen.Fe through Outer.Pass and Wire.Hand with no row -- $out"
out=$(row "$(printf '| about.ping | Ping |\n| fen.ping | Ping |')"); [ -z "$out" ] || fail "knob-spec-rows refused about.ping and fen.ping, each declared through Wire.Hand, beside a call to UnHand -- $out"

# A handed class declared at two sites takes both groups, and a relay passes the group from any argument position.
cat > "$t/param/managed/A.cs" << 'EOF2'
static class General { public static readonly KnobGroup About = KnobGroup.Declare("about", "General", "About"); }
static class Fen { public static readonly KnobGroup Fe = KnobGroup.Declare("fen", "General", "Fen"); }
EOF2
for i in $(seq 100); do echo "static class F$i { static readonly KnobGroup G = KnobGroup.Declare(\"f$i\"); static void I() { G.Toggle(\"k\", \"K\", false); } }"; done >> "$t/param/managed/A.cs"
# The handed class and the relay sit in a later file that declares no group of its own.
cat > "$t/param/managed/Z.cs" << 'EOF2'
sealed class Notes { private Notes(KnobGroup g) { g.Toggle("ping", "Ping", false, help: "h"); } public static Notes Declare(KnobGroup g) => new(g); }
sealed class Wire { void Init() { Notes.Declare(General.About); Late("x", Fen.Fe); } static void Late(string s, KnobGroup h) { Notes.Declare(h); } }
EOF2
out=$(row '| about.ping | Ping |')
[ "$(grep -c . <<< "$out")" = 1 ] && grep -q "fen.ping is declared" <<< "$out" || fail "knob-spec-rows did not take about.ping and fen.ping from Notes' two Declare sites, fen.ping handed as Late's second argument -- $out"

# A Declare site and a relay call count in a file that never names KnobGroup, and a call to MyNotes.Declare is no call to Notes.Declare,
# even beside a relay whose parameter shares the name of Notes.Declare's.
cat > "$t/param/managed/A.cs" << 'EOF2'
static class General { public static readonly KnobGroup About = KnobGroup.Declare("about", "General", "About"); }
static class Fen { public static readonly KnobGroup Fe = KnobGroup.Declare("fen", "General", "Fen"); }
static class Lone { public static readonly KnobGroup Lo = KnobGroup.Declare("lone", "General", "Lone"); }
sealed class Notes { private Notes(KnobGroup g) { g.Toggle("ping", "Ping", false, help: "h"); } public static Notes Declare(KnobGroup g) => new(g); }
static class Outer { internal static void Pass(KnobGroup p) { Notes.Declare(p); } static void Hand(KnobGroup g) { Notes.Declare(g); } }
EOF2
for i in $(seq 100); do echo "static class F$i { static readonly KnobGroup G = KnobGroup.Declare(\"f$i\"); static void I() { G.Toggle(\"k\", \"K\", false); } }"; done >> "$t/param/managed/A.cs"
cat > "$t/param/managed/Z.cs" << 'EOF2'
sealed class MyNotes { public static MyNotes Declare(object o) => new(); }
sealed class Wire { void Init() { Notes.Declare(General.About); Outer.Pass(Fen.Fe); MyNotes.Declare(Lone.Lo); } }
EOF2
out=$(row "$(printf '| about.ping | Ping |\n| fen.ping | Ping |')"); [ -z "$out" ] || fail "knob-spec-rows did not take about.ping and fen.ping from sites in a file that names no KnobGroup, or credited MyNotes' lone group to Notes -- $out"

# A relay or Declare argument counts whole: qualified, spaced, split over lines, after a comma inside a call or string, named,
# or for a parameter with a default.
for i in $(seq 11); do echo "static class K$i { public static readonly KnobGroup G$i = KnobGroup.Declare(\"k$i\", \"General\", \"K$i\"); }"; done > "$t/param/managed/A.cs"
for i in $(seq 100); do echo "static class F$i { static readonly KnobGroup G = KnobGroup.Declare(\"f$i\"); static void I() { G.Toggle(\"k\", \"K\", false); } }"; done >> "$t/param/managed/A.cs"
cat > "$t/param/managed/Z.cs" << 'EOF2'
sealed class Notes { private Notes(KnobGroup g) { g.Toggle("ping", "Ping", false, help: "h"); } public static Notes Declare(KnobGroup x) => new(x); }
static class W {
  static void Pass(KnobGroup p) { Notes.Declare(p); }
  static void Late(int a, KnobGroup h) { Notes.Declare(h); }
  static void Opt(KnobGroup h = null, KnobGroup x = null) { Notes.Declare(h); }
  void I() {
    Opt(x: K2.G2); Opt(K11.G11);
    Pass(Cx.K1.G1); Pass(K2.G2 ?? K1.G1); Late(Pick(), K3.G3);
    Notes.Declare(K4.G4 ?? K1.G1); Notes.Declare(K5.G5 ); Notes.Declare(
      K6.G6); Notes.Declare(Cx.K7.G7);
    Late(Max(1, 2), K8.G8); Late("a,b", K9.G9); Late(h: K10.G10, a: 0);
  }
}
EOF2
out=$(row "$(for i in 1 3 5 6 7 8 9 10 11; do echo "| k$i.ping | Ping |"; done)"); [ -z "$out" ] || fail "knob-spec-rows misread a relay or Declare argument (k2 and k4 take no ping, every other k does) -- $out"

lines() { (cd "$t" && git init -q 2>/dev/null; printf '%s\n' "$@" > tools/x_approved_examples.sh; bash tools/gates/approved-lines.sh) > /dev/null 2>&1; }
lines '# Given a' '# When b' '# Then c' || fail "approved-lines refused an ordered Given, When and Then"
lines '# Given a' '# When b' && fail "approved-lines passed a script with no Then"
lines '# Then c' '# When b' '# Given a' && fail "approved-lines passed Given, When and Then out of order"
[ $rc = 0 ] && echo "gate runner regression: passed"
exit $rc
