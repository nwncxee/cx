#!/usr/bin/env python3
"""CX-200: refuse self-check assertions that cannot fail, and skip paths that pass silently.

Scope: every `*SelfCheck*.cs` under managed/, plus the body of any method elsewhere whose name
contains "SelfCheck" or that runs a `Checker` of its own (TextureSlots.Check and friends are suites
whatever they are called). Inside that scope five shapes are refused:

  1. an Expect(...) whose condition is a compile-time constant (literal arithmetic only);
  2. an Expect(...) comparing a `const` declared in the same file against a plain literal;
  3. an Expect(...) whose two operands across == / != are syntactically identical;
  4. an Expect(...) comparing a value against a DECLARATION DERIVED FROM IT across == / != -- `A == B`
     where B is declared `= A + 1`, anywhere under managed/, so a sibling partial or another file is no
     escape (CX-290). Two independently declared constants held to each other are a real invariant and
     stay; a value read out of a second artifact is a local or a call and resolves to nothing;
  5. a hand-written "SKIPPED" log line -- a skip goes through Checker.Skip, which counts it.

`-v` lists each hit with its file and line. Exit 1 when anything is refused.
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
    """(start, end) offsets of the self-check scope in this file: a *SelfCheck* file is all of it; elsewhere
    it is the body of each method that NAMES itself a self-check, plus the block that runs a `Checker` of its
    own (TextureSlots.Check and friends are suites whatever they are called)."""
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
        out.append((body, matching(masked, body)))
    # ...and the block each `new Checker(` sits directly inside, which is the suite's own body
    if "new Checker(" in masked:
        # the INNERMOST block holding each `new Checker(`: scanning forward, the first `}` whose own `{`
        # opened before it is that block, and an inner block that opened after it must not claim it
        want = [m.start() for m in re.finditer(r"new Checker\s*\(", masked)]
        stack = []
        for i, c in enumerate(masked):
            if c == "{":
                stack.append(i)
            elif c == "}" and stack:
                open_at = stack.pop()
                claimed = [w for w in want if open_at < w < i]
                if claimed:
                    out.append((open_at, i))
                    want = [w for w in want if w not in claimed]
    return out


def matching(masked: str, brace: int) -> int:
    """The offset of the `}` closing the `{` at brace."""
    depth, e = 0, brace
    while e < len(masked):
        if masked[e] == "{":
            depth += 1
        elif masked[e] == "}":
            depth -= 1
            if depth == 0:
                return e
        e += 1
    return len(masked)


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


def comparison(part: str):
    """The offset of the single top-level == / != in part, else None."""
    found = None
    depth, i = 0, 0
    while i < len(part):
        c = part[i]
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        elif depth == 0 and part[i:i + 2] in ("==", "!="):
            if found is not None:
                return None
            found = i
            i += 2
            continue
        elif depth == 0 and c in "<>=!" and part[i:i + 2] not in ("==", "!=") and part[i + 1:i + 2] == "=":
            return None       # <= or >=: not an equality compare
        i += 1
    return found


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


CHAIN = re.compile(r"(?:[A-Za-z_]\w*\s*\.\s*)*[A-Za-z_]\w*")
TYPE_DECL = re.compile(r"\b(?:class|record|struct)\s+(\w+)")
FIELD = re.compile(r"\b(?:private|internal|public|protected)(?:\s+(?:private|internal|public|protected|static|new))*"
                   r"\s+(const|static\s+readonly|readonly\s+static)\s+([A-Za-z_][\w.]*)\s+([^;]+);")


def chains(expr: str):
    """Maximal dotted identifier chains READ in expr, with their whitespace squeezed out. Casts, keywords
    and anything immediately called drop out: `(int)` is not a name, and `Cell(1)` is a call whose result
    the declarations below say nothing about."""
    flat = NUMBER.sub(" ", expr)
    out = []
    for m in CHAIN.finditer(flat):
        name = "".join(m.group(0).split())
        if not name or name in KEYWORDS:
            continue
        if flat[m.end():].lstrip()[:1] == "(":
            continue
        out.append(name)
    return out


def declared_values(src: str):
    """(key, initialiser) for every class-level `const` and `static readonly` SCALAR field in src, keyed by
    bare name and by Type.Name. A generic or array field is skipped: its value is not a number to compare."""
    out = []
    for m in FIELD.finditer(src):
        kind, ty, body = m.group(1), m.group(2), m.group(3)
        if "[" in ty or "<" in ty:
            continue
        owner = None
        for t in TYPE_DECL.finditer(src, 0, m.start()):
            owner = t.group(1)
        for d0, d1 in split_top(body, (",",)):
            decl = body[d0:d1]
            if "=" not in decl:
                continue
            name, _, init = decl.partition("=")
            name = name.strip()
            if not name.isidentifier():
                continue
            init = init.strip()
            if "[" in init or "{" in init or "(" in init and ("new" in init or "=>" in init):
                continue
            out.append((name, owner, init, kind))
    return out


def constant_table(root: Path):
    """Every name under root whose value is fixed at compile time: a `const`, or a `static readonly` whose
    initialiser resolves to constants. Keyed by `Type.Name` always, and by the bare name only where exactly
    one declaration in the tree carries it -- an ambiguous bare name resolves to nothing rather than to the
    wrong file's constant."""
    decls, bare_count = [], {}
    for path in sorted(root.rglob("*.cs")):
        if path.name == "Views.g.cs":
            continue
        src = path.read_text(encoding="utf-8", errors="replace")
        if "const " not in src and "readonly " not in src:
            continue
        for name, owner, init, kind in declared_values(mask(src)):
            decls.append((name, owner, init, kind))
            bare_count[name] = bare_count.get(name, 0) + 1
    table = {}

    def put(name, owner, init):
        if owner:
            table[f"{owner}.{name}"] = init
        if bare_count.get(name) == 1:
            table[name] = init

    pending = []
    for name, owner, init, kind in decls:
        if kind == "const":
            put(name, owner, init)
        else:
            pending.append((name, owner, init))
    # a static readonly built out of constants is one too; two passes settle every chain that ships
    for _ in range(3):
        rest = []
        for name, owner, init in pending:
            if all(c in table for c in chains(init)):
                put(name, owner, init)
            else:
                rest.append((name, owner, init))
        if len(rest) == len(pending):
            break
        pending = rest
    return table


def canon(name: str, table):
    """One key per declaration, however the reader spelled it: the bare name where that is unambiguous
    across the tree, else the Type.Name form. None for a name no declaration answers."""
    tail = name.rsplit(".", 1)[-1]
    if tail in table:
        return tail
    return name if name in table else None


def declared(expr: str, table):
    """The declarations an expression READS, as canonical keys."""
    return {k for k in (canon(c, table) for c in chains(expr)) if k}


def closure(name, table, seen=None):
    """Every declaration reachable from name's initialiser, transitively."""
    seen = seen if seen is not None else set()
    for key in declared(table.get(name, ""), table):
        if key in seen:
            continue
        seen.add(key)
        closure(key, table, seen)
    return seen


def restates(left: str, right: str, table):
    """A declaration on one side built out of a declaration on the other: the assertion reads that
    initialiser back rather than offering a second opinion on it. The SAME name on both sides is not this
    -- a mask test and a function of its own input are both real checks. Returns the pair, or None."""
    ln, rn = declared(left, table), declared(right, table)
    for a, b in ((ln, rn), (rn, ln)):
        for name in b:
            reach = closure(name, table)
            for other in a:
                if other != name and other in reach:
                    return other, name
    return None


def scan(path: Path, table=None):
    src = path.read_text(encoding="utf-8", errors="replace")
    # Cheap pre-filter before the character walk: nothing to say about a file with no self-check in it,
    # or one whose self-check asserts nothing and logs no skip.
    if ("SelfCheck" not in src and "new Checker(" not in src) or ("Expect(" not in src and "SKIPPED" not in src):
        return []
    masked = mask(src)
    # Strings kept, so two calls that differ only in a string argument are not read as one text.
    literal = mask(src, strings=False)
    regs = regions(path, masked)
    if not regs:
        return []
    decl = consts(masked)
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
                    hits.append((line_of(at), "a compile-time constant condition", flat))
                continue
            for a0, a1 in split_top(cond, ("&&", "||")):
                eq = comparison(cond[a0:a1])
                if eq is None:
                    continue
                # Cut the two operands out of the string-bearing copy: two calls that differ only in a
                # string argument look identical once the strings are blanked.
                base = at + a0
                lt = "".join(literal[base:base + eq].split())
                rt = "".join(literal[base + eq + 2:at + a1].split())
                ln, rn = lt.strip("()"), rt.strip("()")
                if not ln or not rn:
                    continue
                if ln == rn:
                    hits.append((line_of(at), "both operands of == / != are the same text", f"{lt} == {rt}"))
                    continue
                flagged = False
                for a, b in ((ln, rn), (rn, ln)):
                    if a in decl and is_literal(b):
                        hits.append((line_of(at), f"the const {a} against its own kind of literal", f"{lt} == {rt}"))
                        flagged = True
                        break
                if flagged or table is None:
                    continue
                pair = restates(ln, rn, table)
                if pair:
                    hits.append((line_of(at),
                                 f"{pair[1]} is DECLARED from {pair[0]}, so the compare restates that declaration",
                                 f"{lt} == {rt}"))
        for m in re.finditer(r"\.(?:Always|Info|Warn|Error)\s*\(", window):
            stmt_end = window.find(";", m.end())
            stmt = src[lo + m.start():lo + (stmt_end if stmt_end > 0 else m.end())]
            if "SKIPPED" in stmt:
                hits.append((line_of(lo + m.start()),
                             "a hand-written SKIPPED log -- use Checker.Skip so the skip is counted",
                             " ".join(stmt.split())[:90]))
    return hits


def main():
    verbose = "-v" in sys.argv
    total = 0
    table = constant_table(MANAGED)
    for path in sorted(MANAGED.rglob("*.cs")):
        if path.name == "Views.g.cs":
            continue
        for line, why, text in scan(path, table):
            total += 1
            rel = path.relative_to(ROOT)
            print(f"  {rel}:{line}: {why}" + (f"\n      {text}" if verbose else ""), file=sys.stderr)
    if total:
        print(f"!! selfcheck: {total} assertion(s) that cannot fail, or an uncounted skip (CX-200).", file=sys.stderr)
        print("!! A self-check's expected value comes from an INDEPENDENT oracle: a hand-written literal,", file=sys.stderr)
        print("!! golden data, or a second artifact (GLSL text, a build .tsv, engine bytes). Never from the", file=sys.stderr)
        print("!! code under test, a copy of it, or the declaration it restates.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
