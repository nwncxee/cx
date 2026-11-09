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

A field is matched to its declaration by name (`HitCheck = G.Toggle("hitcheck", ...)`), so a field name two
classes both declare cannot be told apart; those are listed under `-v` and fail nothing."""
import os, re, sys, collections

ROOT = os.environ.get('CX_ROOT', '.')
FACT = re.compile(r'\b(\w+)\s*=\s*\w+\.(?:Toggle|Slider|Dropdown|Swatch|Text|List)\s*(?:<[^>()]*>)?\s*(\()\s*"([A-Za-z0-9_.\-]+)"')
READ = re.compile(r'\b(\w+)\.(V|Boot)\b')


def balanced(text, start):
    """The span inside the brackets opening at `start`, string literals and comments skipped."""
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
                return text[start + 1:]
        elif text.startswith('/*', i):
            i = text.find('*/', i) + 1
        elif c == '(':
            depth += 1
        elif c == ')':
            depth -= 1
            if depth == 0:
                return text[start + 1:i]
        i += 1
    return text[start + 1:]


texts = {}
for root, _, files in os.walk(os.path.join(ROOT, 'managed')):
    for name in sorted(files):
        if name.endswith('.cs'):
            path = os.path.join(root, name)
            texts[path] = open(path, encoding='utf-8', errors='replace').read()

decl = collections.defaultdict(list)
for path, text in texts.items():
    for m in FACT.finditer(text):
        field, open_at, key = m.group(1), m.start(2), m.group(3)
        args = balanced(text, open_at)
        line = text[:m.start()].count('\n') + 1
        decl[field].append((key, 'Effect.Restart' in args, path, line))

if len(decl) < 100:
    print(f'!! knob-effect: {len(decl)} declaration(s) found -- the collector is broken', file=sys.stderr)
    sys.exit(2)

reads = collections.defaultdict(list)
for path, text in texts.items():
    for m in READ.finditer(text):
        if m.group(1) in decl:
            reads[m.group(1)].append((path, text[:m.start()].count('\n') + 1, m.group(2)))

verbose = '-v' in sys.argv
ambiguous = sorted(f for f, ds in decl.items() if len(ds) > 1)
for field, ds in sorted(decl.items()):
    if len(ds) > 1:
        continue
    key, restart, path, line = ds[0]
    sites = reads[field]
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
    print(f'declarations: {len(decl)}   fields two classes both declare, which fail nothing: {len(ambiguous)}')
    for f in ambiguous:
        print('    ' + f + ': ' + ', '.join(k for k, _r, _p, _l in decl[f]))
