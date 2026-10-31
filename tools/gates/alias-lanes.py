#!/usr/bin/env python3
"""An alias lane is a declaration, not a spelling. Two images share one allocation when their `Alias` strings
are EQUAL, so a lane written as a literal at the call site shares memory with whatever else happens to spell
it the same way -- and a typo silently buys a second allocation instead of failing. Every lane outside the
goldens is a member of `AliasLanes` (managed/Alloy/Core/AliasLanes.cs), whose doc says which targets are in
which lane and in what frame order; this gate refuses a literal in an alias argument.

An alias argument is found, not listed: any parameter named `alias` or `Alias` declares one, which is how
`ImageDesc`'s 11th positional, `VkTarget(.., alias:)` and each pass's own `Target(..)` helper are all covered
from the one rule. Argument matching is by (type, member) name and so is coarse: an overload of the same name
on the same type with a different parameter list can be read as the wrong one.

Also refused: `Alias = ...` with a literal, in a `with` expression or an object initializer.

Exception: managed/Alloy/Golden/, where a golden invents its own lane names to test the sharing itself.

Prints "<count> <file>" per offending file, for tools/gates/ratchet.sh; `-v` lists each site instead."""
import os, re, sys

EXEMPT = ('managed/Alloy/Golden/',)
PARAM = re.compile(r'\bstring\s*\??\s+[Aa]lias\b')
TYPE = re.compile(r'\b(?:class|struct|interface|record)\s+(?:struct\s+|class\s+)?([A-Za-z_]\w*)')
CALL = re.compile(r'(?:\b(new)\s+)?(?:\b([A-Za-z_]\w*)\s*\.\s*)?\b([A-Za-z_]\w*)\s*\(')
ASSIGN = re.compile(r'(?<![A-Za-z0-9_])Alias\s*=(?![=>])')
DECLARES = ('class', 'struct', 'record', 'interface')


def blank(text):
    """Comments and the insides of literals blanked out, every offset and newline kept, so the scanner below
    sees code only. The quotes themselves stay: a literal still reads as `""`, which is the mark looked for."""
    out, i, n = [], 0, len(text)
    while i < n:
        two = text[i:i + 2]
        if two == '//':
            j = text.find('\n', i)
            j = n if j < 0 else j
        elif two == '/*':
            j = text.find('*/', i + 2)
            j = n if j < 0 else j + 2
        elif two == '@"':
            j = i + 2
            while j < n:
                if text[j:j + 2] == '""':
                    j += 2
                elif text[j] == '"':
                    j += 1
                    break
                else:
                    j += 1
            out.append('"'); out.extend(' ' if c != '\n' else '\n' for c in text[i + 1:j - 1]); out.append('"')
            i = j
            continue
        elif text[i] in '"\'':
            q, j = text[i], i + 1
            while j < n and text[j] != '\n':
                if text[j] == '\\':
                    j += 2
                    continue
                if text[j] == q:
                    j += 1
                    break
                j += 1
            out.append(q); out.extend(' ' for _ in text[i + 1:j - 1]); out.append(text[j - 1] if j - 1 > i else q)
            i = j
            continue
        else:
            out.append(text[i]); i += 1
            continue
        out.extend(c if c == '\n' else ' ' for c in text[i:j])
        i = j
    return ''.join(out)


def spans_of(code, open_paren):
    """Top-level argument (or parameter) spans of the list whose '(' is at open_paren. None when unclosed."""
    depth, spans, start, i, n = 0, [], open_paren + 1, open_paren, len(code)
    while i < n:
        c = code[i]
        if c in '([{':
            depth += 1
        elif c in ')]}':
            if c == ')' and depth == 1:
                spans.append((start, i))
                return spans
            depth -= 1
        elif c == ',' and depth == 1:
            spans.append((start, i)); start = i + 1
        i += 1
    return None


def expr_after(code, at):
    """The expression starting at `at`, up to the top-level ',' ';' or closer that ends it."""
    depth, i, n = 0, at, len(code)
    while i < n:
        c = code[i]
        if c in '([{':
            depth += 1
        elif c in ')]}':
            if depth == 0:
                break
            depth -= 1
        elif depth == 0 and c in ',;':
            break
        i += 1
    return code[at:i]


def enclosing(code, at):
    """The type whose declaration most recently precedes `at` -- a file's own partial, near enough."""
    name = None
    for m in TYPE.finditer(code, 0, at):
        name = m.group(1)
    return name


def open_paren_left(code, at):
    """The '(' of the list `at` sits in, or None."""
    depth, i = 0, at - 1
    while i >= 0:
        c = code[i]
        if c == ')':
            depth += 1
        elif c == '(':
            if depth == 0:
                return i
            depth -= 1
        i -= 1
    return None


def declarations(code):
    """(type, member) -> the 1-based positions of its parameters named alias. A parameter list straight after
    `class`/`struct`/`record X` is a primary constructor, so its member name is the type's own."""
    found = {}
    for m in PARAM.finditer(code):
        paren = open_paren_left(code, m.start())
        spans = spans_of(code, paren) if paren is not None else None
        if spans is None:
            continue
        head = re.search(r'(?:\b(' + '|'.join(DECLARES) + r')\s+(?:struct\s+|class\s+)?)?([A-Za-z_]\w*)\s*$',
                         code[:paren])
        if not head:
            continue
        member = head.group(2)
        owner = member if head.group(1) else enclosing(code, paren)
        for i, (lo, hi) in enumerate(spans, 1):
            if lo <= m.start() < hi:
                found.setdefault((owner, member), set()).add(i)
    return found


def hits_in(path, code, decls):
    hits = []

    def say(off, what):
        hits.append(f'{path}:{code.count(chr(10), 0, off) + 1}: {what} is a string literal, not an AliasLanes member')

    for m in CALL.finditer(code):
        new, qual, name = m.groups()
        owner = name if new else (qual or enclosing(code, m.start()))
        want = decls.get((owner, name))
        if not want:
            continue
        spans = spans_of(code, m.end() - 1)
        if spans is None:
            continue
        for i in want:
            if i <= len(spans) and '"' in code[spans[i - 1][0]:spans[i - 1][1]]:
                say(spans[i - 1][0], f"the alias argument of {name}")
    for m in ASSIGN.finditer(code):
        if '"' in expr_after(code, m.end()):
            say(m.start(), 'an Alias assignment')
    return hits


def walk():
    for root, _, files in os.walk('managed'):
        for name in sorted(files):
            if name.endswith('.cs'):
                yield os.path.join(root, name).replace('\\', '/')


def main():
    code = {p: blank(open(p, encoding='utf-8', errors='replace').read()) for p in walk()}
    decls = {}
    for text in code.values():
        for key, where in declarations(text).items():
            decls.setdefault(key, set()).update(where)
    if ('ImageDesc', 'ImageDesc') not in decls:
        print("!! alias-lanes: ImageDesc's alias parameter was not found -- the collector is broken", file=sys.stderr)
        return 2

    verbose = '-v' in sys.argv
    for path, text in code.items():
        if any(e in path for e in EXEMPT) or 'lias' not in text:
            continue
        hits = hits_in(path, text, decls)
        if verbose:
            if hits:
                print('\n'.join(hits))
        elif hits:
            print(len(hits), path)
    return 0


sys.exit(main())
