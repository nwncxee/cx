#!/usr/bin/env bash
# Regression check, not an approved test: the mutant runner (tools/selfcheck/mutants.py) wraps a mutant's text in a
# switch only where the wrap leaves the unmutated program what the tree is. Brackets around one operand of a longer
# expression regroup it (`x - a - b` with `a - b` switched computes `x - (a - b)`), and a wrap inside a string literal
# or a comment changes the text or vanishes; such a mutant is compiled on its own instead. No compile: it asks the
# runner's own decision about each case below, whose answers are written by hand. Exit 0 PASS, 1 FAIL.
cd "$(dirname "$0")/.." || exit 1
python3 -I - "$PWD/tools/selfcheck" <<'PY'
import sys
sys.path.insert(0, sys.argv[1])
import mutants

# (line of code, the mutant's old text, may it be switched)
CASES = [
    ("        return x - a - b;", "a - b", False),
    ("        return x - a - b;", "x - a", True),
    ("        if (s is null || s.Length is not (7 or 9) || s[0] != '#') return false;", "s[0] != '#'", True),
    ("        rgba = s.Length == 7 ? v << 8 | 0xFF : v;", "v << 8 | 0xFF", True),
    ("        return a * b + c;", "b + c", False),
    ("        return a * b + c;", "a * b", True),
    ("        bool r = a == b && c;", "b && c", False),
    ("        bool r = !a && b;", "a && b", False),
    ("        bool r = !a && b;", "a", True),
    ("        x = a + b;", "a + b", True),
    ("        x += a + b;", "a + b", True),
    ("        f(a, b + c);", "b + c", True),
    ("        string t = (a + b).ToString();", "a + b", True),
    ("        int t = a + b.c;", "a + b", False),
    ("        int t = (int)a + b;", "a + b", False),
    ("        var y = a ?? b + c;", "b + c", True),
    ("        var y = a + b ?? c;", "a + b", True),
    ("        string s = \"abcd\";", "bc", False),
    ("        string s = $\"a{count}b\" + c;", "count", False),
    ("        int n = 3; // limit 4", "4", False),
    ("        int n = 3; // limit 4", "3", True),
    ("        return n < 4;", "n < 4", True),
    ("        if (d < 0) return false;", "d < 0", True),
]
fails = 0
for line, old, want in CASES:
    text = "class C {\n    void M() {\n" + line + "\n    }\n}\n"
    at = text.index(line) + line.index(old)
    got = mutants.switchable(text, at, old)
    if got != want:
        fails += 1
        print(f"FAIL: {old!r} in {line.strip()!r}: switchable says {got}, not {want}")
print(f">> mutant switch regression: {len(CASES) - fails} of {len(CASES)} cases hold")
sys.exit(1 if fails else 0)
PY
