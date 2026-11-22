#!/usr/bin/env python3
"""Census of loose mutable statics: per .cs file under the scanned roots, the number of non-readonly
`static` fields that are not a lifetime owner. Prints "<count> <file>" for tools/gates/ratchet.sh;
`-v` lists each field instead.

Counted too: a static auto-property with a `set` or `init` accessor, whose hidden backing field is one.
Not counted: `static readonly`, `const`, static methods / computed properties / events / constructors, and a
**lifetime owner** -- a plain (non-array, non-generic) field whose type name ends in `State` and names a
class or record declared under managed/. One owner per lifetime is the shape the renderer wants: a reset
replaces the object, so nothing has to remember the fields."""
import os, re, sys

ROOTS = ('managed/Alloy', 'managed/Services')
MODIFIERS = frozenset((
    'public', 'private', 'protected', 'internal', 'static', 'readonly', 'const', 'volatile',
    'unsafe', 'new', 'extern', 'partial', 'sealed', 'abstract', 'override', 'virtual', 'required',
    'file', 'fixed', 'async'))
NOT_A_FIELD = frozenset(('class', 'struct', 'record', 'interface', 'enum', 'delegate', 'event'))
OWNER_TYPE = re.compile(r'^[A-Za-z_][\w.]*State\??$')
DECLARATOR = re.compile(r'\s*[A-Za-z_]\w*\s*[=,;]')
ACCESS = r'(?:(?:public|private|protected|internal)\s+)*'
AUTO_SETTER = re.compile(r'\{\s*' + ACCESS + r'get\s*;\s*' + ACCESS + r'(?:set|init)\s*;\s*\}'
                         + r'|\{\s*' + ACCESS + r'(?:set|init)\s*;\s*' + ACCESS + r'get\s*;\s*\}')


_BLANK_STOPS = re.compile(r'//|/\*|[@$]{1,2}"|["\']')
_NOT_NL = re.compile(r'[^\n]')


def blank(src):
    """The source with every comment and string literal replaced by spaces (newlines kept)."""
    out, i, n = [], 0, len(src)

    def eat_string(i):
        """Index just past the literal starting at i (which is a quote or a prefixed quote)."""
        interp = verbatim = False
        while src[i] in '@$':
            interp |= src[i] == '$'
            verbatim |= src[i] == '@'
            i += 1
        q = i
        while q < n and src[q] == '"':
            q += 1
        fence = q - i                       # 3+ quotes = a raw string literal
        i = q
        if fence >= 3:
            while i < n:
                if src[i] == '"':
                    run = 0
                    while i + run < n and src[i + run] == '"':
                        run += 1
                    if run >= fence:
                        return i + run
                    i += run
                else:
                    i += 1
            return n
        if fence == 2:
            return i                        # "" -- the empty literal, already consumed
        while i < n:
            c = src[i]
            if c == '\\' and not verbatim:
                i += 2
            elif c == '"':
                if verbatim and i + 1 < n and src[i + 1] == '"':
                    i += 2
                else:
                    return i + 1
            elif interp and c == '{':
                if i + 1 < n and src[i + 1] == '{':
                    i += 2
                    continue
                i, depth = i + 1, 1         # an interpolation hole: code, possibly with its own strings
                while i < n and depth:
                    if src[i] in '"\'' or (src[i] in '@$' and i + 1 < n and src[i + 1] in '@$"'):
                        i = eat_string(i) if src[i] != "'" else eat_char(i)
                    else:
                        depth += (src[i] == '{') - (src[i] == '}')
                        i += 1
            else:
                i += 1
        return n

    def eat_char(i):
        i += 1
        while i < n:
            if src[i] == '\\':
                i += 2
            elif src[i] == "'":
                return i + 1
            else:
                i += 1
        return n

    while i < n:
        m = _BLANK_STOPS.search(src, i)
        if m is None:
            out.append(src[i:])
            break
        if m.start() > i:
            out.append(src[i:m.start()])
            i = m.start()
        c = src[i]
        if c == '/' and i + 1 < n and src[i + 1] == '/':
            j = src.find('\n', i)
            j = n if j < 0 else j
        elif c == '/' and i + 1 < n and src[i + 1] == '*':
            j = src.find('*/', i)
            j = n if j < 0 else j + 2
        elif c == '"' or (c in '@$' and re.match(r'[@$]{1,2}"', src[i:i + 3] or ' ')):
            j = eat_string(i)
        elif c == "'":
            j = eat_char(i)
        else:
            out.append(c)
            i += 1
            continue
        out.append(_NOT_NL.sub(' ', src[i:j]))
        i = j
    return ''.join(out)


def owner_types():
    """Every `...State` class or record declared under managed/ -- the types a lifetime owner may have."""
    found = set()
    decl = re.compile(r'\b(?:class|record)\s+(\w*State)\b')
    for root, _, files in os.walk('managed'):
        for name in files:
            if name.endswith('.cs'):
                with open(os.path.join(root, name), encoding='utf-8', errors='replace') as f:
                    found.update(decl.findall(f.read()))
    return found


def head_end(code, at):
    """Index of the character that decides what `at` declares: `(` method, `{`/`=>` property, `=`/`;`/`,`
    field. Balanced `<>`/`[]` in the type, and a leading tuple type, are stepped over."""
    i, n, angle = at, len(code), 0
    while i < n:
        c = code[i]
        if c == '(' and not angle and not code[at:i].strip():
            depth = 1                        # a tuple type opens the declaration: step over it
            i += 1
            while i < n and depth:
                depth += (code[i] == '(') - (code[i] == ')')
                i += 1
            continue
        if c == '<':
            angle += 1
        elif c == '>' and angle:
            angle -= 1
        elif angle:
            pass
        elif c == '[':                       # an array rank or an attribute inside the type
            depth = 1
            i += 1
            while i < n and depth:
                depth += (code[i] == '[') - (code[i] == ']')
                i += 1
            continue
        elif c in '({;,=' or code.startswith('=>', i):
            return i
        i += 1
    return n


def declarators(code, at):
    """How many names a field declaration starting at `at` declares (`static int a, b;` is two)."""
    i, n, count, depth = at, len(code), 1, 0
    while i < n:
        c = code[i]
        if c in '([{':
            depth += 1
        elif c in ')]}':
            depth -= 1
        elif depth == 0 and c == ';':
            break
        elif depth == 0 and c == ',' and DECLARATOR.match(code, i + 1):
            count += 1
        i += 1
    return count


def fields(path, owners):
    """Every loose mutable static field in the file, as (line, text) pairs."""
    with open(path, encoding='utf-8', errors='replace') as f:
        src = f.read()
    code = blank(src)
    hits = []
    for m in re.finditer(r'\bstatic\b', code):
        start = max(code.rfind(c, 0, m.start()) for c in ';{}')
        mods, bad = code[start + 1:m.start()], False
        for tok in re.sub(r'\[[^\[\]]*\]', ' ', mods).split():
            if tok not in MODIFIERS:
                bad = True
        if bad or 'readonly' in mods.split() or 'const' in mods.split():
            continue
        rest = code[m.end():]
        tail = rest[:head_end(rest, 0)]
        words = re.sub(r'\bdelegate\s*\*', 'delegateptr', tail).split()
        if not words or set(words) & NOT_A_FIELD:
            continue
        if 'readonly' in words or 'const' in words:
            continue
        end = m.end() + len(tail)
        if end < len(code) and code[end] == '{' and AUTO_SETTER.match(code, end):
            hits.append((code.count('\n', 0, m.start()) + 1, ' '.join(words), 1))
            continue                         # an auto-property with a setter: a loose static behind a name
        if end >= len(code) or code[end] == '(' or code[end] == '{' or code.startswith('=>', end):
            continue                         # method, constructor, property
        if len(words) >= 2 and OWNER_TYPE.match(words[-2]) and words[-2].rstrip('?').split('.')[-1] in owners:
            continue                         # the lifetime owner itself
        hits.append((code.count('\n', 0, m.start()) + 1, ' '.join(words), declarators(code, end)))
    return hits


def main():
    verbose = '-v' in sys.argv
    owners = owner_types()
    for root in ROOTS:
        for dirpath, _, files in os.walk(root):
            for name in sorted(files):
                if not name.endswith('.cs') or name.endswith('.g.cs'):
                    continue
                path = os.path.join(dirpath, name)
                hits = fields(path, owners)
                if verbose:
                    for line, text, count in hits:
                        print(f'{path}:{line}: static {text} ({count})')
                elif hits:
                    print(sum(c for _, _, c in hits), path)


if __name__ == '__main__':
    main()
