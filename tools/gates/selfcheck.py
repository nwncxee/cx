#!/usr/bin/env python3
"""CX-200: refuse self-check assertions that cannot fail, and skip paths that pass silently.

Scope: every `*SelfCheck*.cs` under managed/, plus the body of any method elsewhere whose name
contains "SelfCheck". Inside that scope four shapes are refused:

  1. an Expect(...) whose condition is a compile-time constant (literal arithmetic only);
  2. an Expect(...) comparing a `const` against a plain literal, over == != < > <= >=. The const may be
     named bare (declared in the same file) or as Type.CONST (declared anywhere under managed/);
  3. an Expect(...) whose two operands across a comparison are syntactically identical;
  4. a hand-written "SKIPPED" log line -- a skip goes through Checker.Skip, which counts it.

`-v` lists each hit with its file and line. Exit 1 when a shape other than 2 is refused; the standing
instances of 2 are a shrink-only baseline instead (`--census` feeds ratchet.sh), so one may be removed
but never added.
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MANAGED = ROOT / "managed"

CASTS = {
    "int", "uint", "long", "ulong", "short", "ushort", "byte", "sbyte",
    "float", "double", "decimal", "char", "bool", "nint", "nuint",
}
KEYWORDS = CASTS | {"true", "false"}

IDENT = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
NUMBER = re.compile(r"0[xXbB][0-9a-fA-F_]+[uUlL]*|\.?[0-9][0-9_]*(?:\.[0-9_]+)?(?:[eE][+-]?[0-9]+)?[fFdDmMuUlL]*")


def mask(src: str, strings: bool = True) -> str:
    """Blank out comments and (unless asked not to) string and char literals, keeping every offset."""
    out = list(src)
    i, n = 0, len(src)
    while i < n:
        c = src[i]
        if c == "/" and i + 1 < n and src[i + 1] == "/":
            while i < n and src[i] != "\n":
                out[i] = " "
                i += 1
            continue
        if c == "/" and i + 1 < n and src[i + 1] == "*":
            while i < n and not (src[i] == "*" and i + 1 < n and src[i + 1] == "/"):
                if src[i] != "\n":
                    out[i] = " "
                i += 1
            for k in range(i, min(i + 2, n)):
                out[k] = " "
            i += 2
            continue
        if c == '"' and strings:
            raw = i >= 1 and src[i - 1] in "@" or (i >= 2 and src[i - 2:i] == '@$')
            i += 1
            while i < n:
                if src[i] == "\\" and not raw:
                    out[i] = out[i + 1] = " "
                    i += 2
                    continue
                if src[i] == '"':
                    if raw and i + 1 < n and src[i + 1] == '"':
                        out[i] = out[i + 1] = " "
                        i += 2
                        continue
                    out[i] = " "
                    i += 1
                    break
                if src[i] != "\n":
                    out[i] = " "
                i += 1
            continue
        if c == "'" and strings:
            i += 1
            while i < n:
                if src[i] == "\\":
                    out[i] = out[i + 1] = " "
                    i += 2
                    continue
                if src[i] == "'":
                    out[i] = " "
                    i += 1
                    break
                out[i] = " "
                i += 1
            continue
        i += 1
    if not strings:
        return "".join(out)
    # the opening quote/apostrophe itself
    return "".join(" " if ch in "\"'" else ch for ch in out)


def regions(path: Path, masked: str):
    """(start, end) offsets of the self-check scope in this file."""
    if "SelfCheck" in path.name:
        return [(0, len(masked))]
    out = []
    for m in re.finditer(r"\b[A-Za-z0-9_]*SelfCheck[A-Za-z0-9_]*\s*\(", masked):
        # a declaration, not a call: the matching ) is followed by a body brace
        j = masked.find("(", m.start())
        depth, k = 0, j
        while k < len(masked):
            if masked[k] == "(":
                depth += 1
            elif masked[k] == ")":
                depth -= 1
                if depth == 0:
                    break
            k += 1
        rest = masked[k + 1:k + 200].lstrip()
        if not rest.startswith("{"):
            continue
        body = masked.index("{", k)
        depth, e = 0, body
        while e < len(masked):
            if masked[e] == "{":
                depth += 1
            elif masked[e] == "}":
                depth -= 1
                if depth == 0:
                    break
            e += 1
        out.append((body, e))
    return out


def first_arg(masked: str, open_paren: int):
    """The text of the call's first argument, and its offset."""
    depth, i, start = 0, open_paren, open_paren + 1
    while i < len(masked):
        c = masked[i]
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
            if depth == 0:
                return masked[start:i], start
        elif c == "," and depth == 1:
            return masked[start:i], start
        i += 1
    return None, start


def tokens(expr: str):
    """Identifiers left in an expression once literals, operators and primitive casts are gone."""
    rest = NUMBER.sub(" ", expr)
    return [t for t in IDENT.findall(rest) if t not in KEYWORDS]


STR = re.compile(r"\"(?:[^\"\\]|\\.)*\"")


def is_literal(expr: str) -> bool:
    """A plain literal expression: numbers, strings, true/false and arithmetic over them."""
    e = STR.sub(' "" ', expr.strip())
    return bool(e) and not tokens(e) and bool(re.search(r'[0-9]|true|false|""', e))


def split_top(expr: str, ops):
    """Spans of expr between its top-level occurrences of ops, as (start, end) offsets. Offsets, not
    text, so the same cut can be taken out of a parallel copy of the source that still has its
    strings."""
    spans, depth, last, i = [], 0, 0, 0
    while i < len(expr):
        c = expr[i]
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        elif depth == 0:
            for op in ops:
                if expr.startswith(op, i):
                    spans.append((last, i))
                    i += len(op)
                    last = i
                    break
            else:
                i += 1
                continue
            continue
        i += 1
    spans.append((last, len(expr)))
    return spans


GENERIC_INNER = re.compile(r"^[A-Za-z0-9_,.\[\]\?\s]*$")
COMPARES = ("==", "!=", "<=", ">=", "<", ">")


def angles(expr: str) -> str:
    """A copy of expr with generic argument lists blanked, offsets kept: `Std140.BlockBytes<Block>() == 208`
    carries two angle brackets that are not comparisons, and reading them as such loses the == beside them."""
    out = list(expr)
    i = 0
    while i < len(expr):
        if expr[i] == "<" and i and (expr[i - 1].isalnum() or expr[i - 1] == "_"):
            depth, j, stop = 0, i, False
            while j < len(expr):
                if expr[j] == "<":
                    depth += 1
                elif expr[j] == ">":
                    depth -= 1
                    if depth == 0:
                        break
                elif expr[j] in "();{}":
                    stop = True
                    break
                j += 1
            if not stop and j < len(expr) and GENERIC_INNER.match(expr[i + 1:j]):
                for k in range(i, j + 1):
                    out[k] = " "
                i = j + 1
                continue
        i += 1
    return "".join(out)


def comparison(part: str):
    """The offset and text of the single top-level comparison in part, else (None, None). A second one means
    the operands cannot be cut out unambiguously, so the part is left alone."""
    found, op = None, None
    bare = angles(part)
    depth, i = 0, 0
    while i < len(bare):
        c = bare[i]
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        elif depth == 0:
            for cand in COMPARES:
                if bare.startswith(cand, i):
                    if found is not None:
                        return None, None
                    found, op = i, cand
                    i += len(cand)
                    break
            else:
                i += 1
                continue
            continue
        i += 1
    return found, op


def consts(masked: str):
    """Class-level const name -> initialiser. Only declarations carrying an access modifier: a
    method-local `const` shares a namespace with the locals around it, and treating one as a field
    would read an unrelated local of the same name as a constant."""
    out = {}
    for m in re.finditer(r"\b(?:private|internal|public|protected)(?:\s+(?:private|internal|public|protected|static|new))*"
                         r"\s+const\s+([A-Za-z_][A-Za-z0-9_]*)\s+([^;]+);", masked):
        body = m.group(2)
        for d0, d1 in split_top(body, (",",)):
            decl = body[d0:d1]
            if "=" not in decl:
                continue
            name, _, init = decl.partition("=")
            out[name.strip()] = init.strip()
    return out


TYPE_DECL = re.compile(r"\b(?:class|struct|record|interface)\s+([A-Za-z_][A-Za-z0-9_]*)")


def qualified_consts():
    """Every class-level const under managed/, keyed Type.CONST. Qualified only: a bare name is resolved
    against the file that declares it, because two files may well name a const the same thing."""
    table = {}
    for path in sorted(MANAGED.rglob("*.cs")):
        if path.name == "Views.g.cs":
            continue
        src = path.read_text(encoding="utf-8", errors="replace")
        if " const " not in src:
            continue
        masked = mask(src)
        local = consts(masked)
        if not local:
            continue
        for t in set(TYPE_DECL.findall(masked)):
            for name, init in local.items():
                table.setdefault(f"{t}.{name}", init)
    return table


def scan(path: Path, qualified=None):
    src = path.read_text(encoding="utf-8", errors="replace")
    # Cheap pre-filter before the character walk: nothing to say about a file with no self-check in it,
    # or one whose self-check asserts nothing and logs no skip.
    if "SelfCheck" not in src or ("Expect(" not in src and "SKIPPED" not in src):
        return []
    masked = mask(src)
    # Strings kept, so two calls that differ only in a string argument are not read as one text.
    literal = mask(src, strings=False)
    regs = regions(path, masked)
    if not regs:
        return []
    decl = dict(qualified or {})
    decl.update(consts(masked))
    hits = []

    def line_of(off):
        return src.count("\n", 0, off) + 1

    for lo, hi in regs:
        window = masked[lo:hi]
        for m in re.finditer(r"(?<![A-Za-z0-9_.])(?:[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)?Expect\s*\(", window):
            op = lo + m.end() - 1
            cond, at = first_arg(masked, op)
            if cond is None:
                continue
            flat = " ".join(cond.split())
            if not flat:
                continue
            if not tokens(flat):
                # `Expect(false, ...)` is a deliberate unconditional failure, not an assertion.
                if flat != "false":
                    hits.append((line_of(at), "a compile-time constant condition", flat, "hard"))
                continue
            for a0, a1 in split_top(cond, ("&&", "||")):
                eq, op = comparison(cond[a0:a1])
                if eq is None:
                    continue
                # Cut the two operands out of the string-bearing copy: two calls that differ only in a
                # string argument look identical once the strings are blanked.
                base = at + a0
                lt = "".join(literal[base:base + eq].split())
                rt = "".join(literal[base + eq + len(op):at + a1].split())
                ln, rn = lt.strip("()"), rt.strip("()")
                if not ln or not rn:
                    continue
                shown = f"{lt} {op} {rt}"
                if ln == rn:
                    hits.append((line_of(at), f"both operands of {op} are the same text", shown, "hard"))
                    continue
                for a, b in ((ln, rn), (rn, ln)):
                    if a in decl and is_literal(b):
                        hits.append((line_of(at), f"the const {a} against its own kind of literal", shown, "const"))
                        break
        for m in re.finditer(r"\.(?:Always|Info|Warn|Error)\s*\(", window):
            stmt_end = window.find(";", m.end())
            stmt = src[lo + m.start():lo + (stmt_end if stmt_end > 0 else m.end())]
            if "SKIPPED" in stmt:
                hits.append((line_of(lo + m.start()),
                             "a hand-written SKIPPED log -- use Checker.Skip so the skip is counted",
                             " ".join(stmt.split())[:90], "hard"))
    return hits


def main():
    """Default: report every hit and fail on any but the const-against-literal shape, whose standing
    instances are a shrink-only census instead (`--census`, fed to ratchet.sh by selfcheck.sh)."""
    verbose = "-v" in sys.argv
    census = "--census" in sys.argv
    hard, counts = 0, {}
    qualified = qualified_consts()
    for path in sorted(MANAGED.rglob("*.cs")):
        if path.name == "Views.g.cs":
            continue
        rel = path.relative_to(ROOT)
        for line, why, text, kind in scan(path, qualified):
            if kind == "const":
                counts[str(rel)] = counts.get(str(rel), 0) + 1
                if census:
                    continue
            else:
                hard += 1
            if not census:
                print(f"  {rel}:{line}: {why}" + (f"\n      {text}" if verbose else ""), file=sys.stderr)
    if census:
        for name in sorted(counts):
            print(f"{counts[name]} {name}")
        return 0
    if hard:
        print(f"!! selfcheck: {hard} assertion(s) that cannot fail, or an uncounted skip (CX-200).", file=sys.stderr)
        print("!! A self-check's expected value comes from an INDEPENDENT oracle: a hand-written literal,", file=sys.stderr)
        print("!! golden data, or a second artifact (GLSL text, a build .tsv, engine bytes). Never from the", file=sys.stderr)
        print("!! code under test, a copy of it, or the declaration it restates.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
