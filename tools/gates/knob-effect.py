#!/usr/bin/env python3
"""CX-503: a knob's declared effect and the way its readers read it are one thing. `Effect.Restart` means
"readers take `Boot`, and the panel shows the restart badge"; the default `Effect.Live` means "a set applies
now". Either label can be wrong in a way nothing shows: `alloy.hitcheck` was Live and every reader took
`Boot`, so a chat set changed nothing and said nothing, and `alloy.texcache` was Restart with one reader on
`V`, so a dial the panel badged as needing a launch half-applied at once.

Two rules, both on the readers:
  - a knob declared Live that is read through `Boot` somewhere and through `V` nowhere outside the file it is
    declared in -- a status line or the panel reads `V` to SHOW the value, which is not a reader of the
    setting -- is a Restart knob wearing the wrong label;
  - a knob declared Restart and read through `V` outside the file it is declared in has a reader the badge
    lies to.

A field is matched to its declaration by the class that declares it (`HitCheck = G.Toggle("hitcheck", ...)` in
`Renderer`), and a read to that declaration by its qualifier (`Renderer.HitCheck.Boot`), or, bare, by the class
or the file it is read in. `Size`, `Viz`, `X` and the like are declared by several passes, so a bare read in a
third class names no declaration; those reads are listed under `-v` and fail nothing."""
import os, re, sys, collections

ROOT = os.environ.get('CX_ROOT', '.')
FACT = re.compile(r'\b(\w+)\s*=\s*\w+\.(?:Toggle|Slider|Dropdown|Swatch|Text|List)\s*(?:<[^>()]*>)?\s*(\()\s*"([A-Za-z0-9_.\-]+)"')
READ = re.compile(r'(?:(\w+)\s*\.\s*)?\b(\w+)\.(V|Boot)\b')
TYPE = re.compile(r'\b(?:class|struct|record)\s+(\w+)')


def balanced_end(text, start, open_c='(', close_c=')'):
    """The index of the bracket closing the one at `start`, string literals and comments skipped."""
    depth, i, n = 0, start, len(text)
    while i < n:
        c = text[i]
        if c == '"':
            i += 1
            while i < n and text[i] != '"':
                i += 2 if text[i] == '\\' else 1
        elif text.startswith('//', i):
            i = text.find('\n', i)
            if i < 0:
                return n
        elif text.startswith('/*', i):
            i = text.find('*/', i) + 1
        elif c == open_c:
            depth += 1
        elif c == close_c:
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return n


def balanced(text, start):
    """The span inside the brackets opening at `start`."""
    return text[start + 1:balanced_end(text, start)]


texts = {}
for root, _, files in os.walk(os.path.join(ROOT, 'managed')):
    for name in sorted(files):
        if name.endswith('.cs'):
            path = os.path.join(root, name)
            texts[path] = open(path, encoding='utf-8', errors='replace').read()

bodies = collections.defaultdict(list)
for path, text in texts.items():
    for m in TYPE.finditer(text):
        brace = text.find('{', m.end())
        if brace < 0:
            continue
        semi = text.find(';', m.end())
        if 0 <= semi < brace:
            continue
        end = balanced_end(text, brace, '{', '}')
        bodies[path].append((m.group(1), brace, end))


def owner(path, pos):
    """The innermost type whose body holds `pos`."""
    best, span = None, None
    for name, start, end in bodies[path]:
        if start < pos < end and (span is None or end - start < span):
            best, span = name, end - start
    return best


decl = {}
by_field = collections.defaultdict(list)
for path, text in texts.items():
    name = os.path.basename(path)
    if 'SelfCheck' in name or 'ApprovedExamples' in name:
        continue   # a check's own group is registered nowhere: no panel badge and no chat reply to be wrong
    for m in FACT.finditer(text):
        field, open_at, key = m.group(1), m.start(2), m.group(3)
        args = balanced(text, open_at)
        line = text[:m.start()].count('\n') + 1
        cls = owner(path, m.start()) or ''
        decl[(cls, field)] = (key, 'Effect.Restart' in args, path, line)
        by_field[field].append((cls, path))

if len(decl) < 100:
    print(f'!! knob-effect: {len(decl)} declaration(s) found -- the collector is broken', file=sys.stderr)
    sys.exit(2)

reads = collections.defaultdict(list)
unresolved = []
for path, text in texts.items():
    for m in READ.finditer(text):
        qual, field, how = m.group(1), m.group(2), m.group(3)
        candidates = by_field.get(field)
        if not candidates:
            continue
        line = text[:m.start()].count('\n') + 1
        if len(candidates) == 1:
            pick = candidates[0][0]
        else:
            named = [c for c, _p in candidates if c == qual]
            here = [c for c, p in candidates if p == path] or [c for c, _p in candidates if c == owner(path, m.start())]
            pick = named[0] if len(named) == 1 else here[0] if len(here) == 1 else None
        if pick is None:
            unresolved.append(f'{os.path.relpath(path, ROOT)}:{line}: {field}.{how} '
                              f'({len(candidates)} classes declare {field})')
            continue
        reads[(pick, field)].append((path, line, how))

if '--census' in sys.argv:
    per = collections.Counter(u.split(':')[0] for u in unresolved)
    for f, n in sorted(per.items()):
        print(n, f)
    sys.exit(0)

verbose = '-v' in sys.argv
for (cls, field), (key, restart, path, line) in sorted(decl.items()):
    sites = reads[(cls, field)]
    outside_v = [s for s in sites if s[2] == 'V' and s[0] != path]
    boots = [s for s in sites if s[2] == 'Boot']
    rel = os.path.relpath(path, ROOT)
    if restart:
        for p, n, _ in outside_v:
            print(f'{os.path.relpath(p, ROOT)}:{n}: {key} is declared Restart, so this reader takes Boot, '
                  f'not V (declared at {rel}:{line})')
    elif boots and not outside_v:
        where = ', '.join(f'{os.path.relpath(p, ROOT)}:{n}' for p, n, _ in boots[:4])
        print(f'{rel}:{line}: {key} is declared Live but every reader takes Boot ({where}) -- '
              'declare Effect.Restart, or read V')

if verbose:
    print(f'declarations: {len(decl)}   reads named no one declaration, which fail nothing: {len(unresolved)}')
    for u in unresolved:
        print('    ' + u)
