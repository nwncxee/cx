#!/usr/bin/env python3
"""CX-484: every declared knob has its row in docs/spec/unified-settings.md, and every key the spec rows is
declared. The spec is the settings tree's one description -- the panel, cx.cfg and the chat command are three
views of a declaration -- so a knob with no row is a feature nobody can read about, and a row with no
declaration is a promise the client does not keep.

Declarations: a group is `<var> = KnobGroup.Declare("<name>", ...)` inside some class, and its rows are the
factory calls on that var. A class's rows are spread over its partials and one file may hold several classes,
so a call is attributed to the INNERMOST class whose body contains it, and groups merge per class name across
files. Three shapes declare keys no factory literal shows, and each cost this census a wrong answer before it
was written down:
  - a `Declare` whose group name is a constructor parameter (the three camera modes) takes one group per
    `new <Class>("<name>"` site;
  - `new TextStyle(G, ..., prefix: "p")` declares `<group>.<p>font` / `typeface` / `outline`;
  - `Quality.Area("x", ...)` declares `quality.x`.
A group declared with `master:` also declares `<group>.enabled`. A `new KnobGroup(...)` is a self-check's
throwaway, registered nowhere, so an unattributed factory call is reported only under `-v` and fails nothing.

Rows: a table row's first cell, `group.knob`, or a summary cell naming a family (`group.a / b / c`). A
`group.prefix<...>` cell stands for its whole family (`ui.custom_<role>`, `radial.p<N>.*`), and `<mode>.x`
stands for all three camera modes.

Prints one line per key with no row and per row with no key; `-v` prints the whole census."""
import os, re, sys, collections

ROOT = os.environ.get('CX_ROOT', '.')
FACTORIES = ('Toggle', 'Slider', 'Dropdown', 'Swatch', 'Text', 'List', 'Button', 'Link', 'Bag')
DECL = re.compile(r'(?:(\w+)\s*=\s*)?KnobGroup\.Declare\(\s*(?:"([A-Za-z0-9_.\-]+)"|(\w+))')
FACT = re.compile(r'\b(\w+)\.(' + '|'.join(FACTORIES) + r')\s*(?:<[^>()]*>)?\s*\(')
TYPE = re.compile(r'\b(?:class|struct|record)\s+(\w+)')
NEWOF = re.compile(r'new\s+(\w+)\s*\(\s*"([a-z0-9]+)"')
TEXTSTYLE = re.compile(r'new\s+(?:[\w.]+\.)?TextStyle\s*(\()\s*(\w+)\s*,')
CAMERA_MODES = ('chase', 'topdown', 'driving')


def balanced(text, start, open_c='(', close_c=')'):
    """The span inside the brackets opening at `start`, string literals and comments skipped."""
    depth, i, n = 0, start, len(text)
    while i < n:
        c = text[i]
        if c == '"':
            i += 1
            while i < n and text[i] != '"':
                i += 2 if text[i] == '\\' else 1
        elif c == "'":
            i += 1
            while i < n and text[i] != "'":
                i += 2 if text[i] == '\\' else 1
        elif text.startswith('//', i):
            i = text.find('\n', i)
            if i < 0:
                return text[start + 1:], n
        elif text.startswith('/*', i):
            i = text.find('*/', i) + 1
        elif c == open_c:
            depth += 1
        elif c == close_c:
            depth -= 1
            if depth == 0:
                return text[start + 1:i], i
        i += 1
    return text[start + 1:], n


def walk():
    for root, _, files in os.walk(os.path.join(ROOT, 'managed')):
        for name in sorted(files):
            if name.endswith('.cs'):
                yield os.path.join(root, name)


texts = {p: open(p, encoding='utf-8', errors='replace').read() for p in walk()}
all_text = ''.join(texts.values())

bodies = collections.defaultdict(list)
for path, text in texts.items():
    for m in TYPE.finditer(text):
        brace = text.find('{', m.end())
        if brace < 0:
            continue
        semi = text.find(';', m.end())
        if 0 <= semi < brace:
            continue
        _body, end = balanced(text, brace, '{', '}')
        bodies[path].append((m.group(1), brace, end))


def owner(path, pos):
    """The innermost type whose body holds `pos`."""
    best, span = None, None
    for name, start, end in bodies[path]:
        if start < pos < end and (span is None or end - start < span):
            best, span = name, end - start
    return best


class_group = collections.defaultdict(set)
dynamic = {}
for path, text in texts.items():
    for m in DECL.finditer(text):
        var, group, _ident = m.group(1), m.group(2), m.group(3)
        cls = owner(path, m.start())
        if cls is None:
            continue
        if group:
            class_group[cls].add((var or 'G', group))
        else:
            dynamic[cls] = var or 'G'
for cls, var in dynamic.items():
    for newcls, name in NEWOF.findall(all_text):
        if newcls == cls:
            class_group[cls].add((var, name))

ARRAY = re.compile(r'\bNAME\s*=\s*(?:new\s*(?:\w+)?\[\]\s*)?\{([^}]*)\}')
LITERAL = re.compile(r'"([A-Za-z0-9_.\-]+)"')


def keys_of(text, body):
    """The key(s) a factory's first argument names: a literal, or every literal of the array a loop
    indexes (`G.Text(keyNames[set], ...)`), which is how the quickbar declares its three key rows."""
    lit = re.match(r'\s*"([A-Za-z0-9_.\-]+)"', body)
    if lit:
        return [lit.group(1)]
    idx = re.match(r'\s*(\w+)\s*\[', body)
    if not idx:
        return []
    arr = re.search(ARRAY.pattern.replace('NAME', re.escape(idx.group(1))), text)
    return LITERAL.findall(arr.group(1)) if arr else []


knobs, unattributed = {}, []
for path, text in texts.items():
    for m in FACT.finditer(text):
        var, factory = m.group(1), m.group(2)
        body, _ = balanced(text, m.end() - 1)
        names = keys_of(text, body)
        if not names:
            continue
        line = text[:m.start()].count('\n') + 1
        cls = owner(path, m.start())
        groups = sorted(g for v, g in class_group.get(cls, ()) if v == var)
        if not groups:
            unattributed.append(f'{path}:{line}: {var}.{factory}("{names[0]}") in class {cls}')
            continue
        t = re.search(r'Tier\.(\w+)', body)
        for g in groups:
            for name in names:
                knobs[f'{g}.{name}'] = (t.group(1) if t else 'User', path, line, factory)

for path, text in texts.items():
    for m in TEXTSTYLE.finditer(text):
        var = m.group(2)
        body, _ = balanced(text, m.start(1))
        cls = owner(path, m.start())
        line = text[:m.start()].count('\n') + 1
        pre = re.search(r'prefix:\s*"([a-z0-9_]*)"', body)
        prefix = pre.group(1) if pre else ''
        t = re.search(r'tier:\s*Tier\.(\w+)', body)
        rows = ['typeface', 'outline'] + ([] if re.search(r'sized:\s*false', body) else ['font'])
        for g in sorted(gg for v, gg in class_group.get(cls, ()) if v == var):
            for row in rows:
                knobs[f'{g}.{prefix}{row}'] = (t.group(1) if t else 'User', path, line, 'TextStyle')

quality = os.path.join(ROOT, 'managed/Alloy/Core/Quality.cs')
for m in re.finditer(r'=\s*Area\(\s*"([a-z0-9]+)"', texts.get(quality, '')):
    knobs[f'quality.{m.group(1)}'] = ('User', quality, 0, 'Quality.Area')

for path, text in texts.items():
    for m in DECL.finditer(text):
        var, group, _ident = m.group(1), m.group(2), m.group(3)
        cls = owner(path, m.start())
        body, _ = balanced(text, text.index('(', m.start()))
        if not re.search(r'(?:^|[(,]\s*)master:\s*"', body):
            continue
        t = re.search(r'masterTier:\s*Tier\.(\w+)', body)
        line = text[:m.start()].count('\n') + 1
        for g in ([group] if group else sorted(g for v, g in class_group.get(cls, ()) if v == (var or 'G'))):
            knobs[f'{g}.enabled'] = (t.group(1) if t else 'User', path, line, 'master')

groups_declared = {g for pairs in class_group.values() for _v, g in pairs}
if not groups_declared or len(knobs) < 100:
    print(f'!! knob-spec-rows: {len(groups_declared)} group(s) and {len(knobs)} knob(s) found -- '
          'the collector is broken', file=sys.stderr)
    sys.exit(2)

spec_path = os.path.join(ROOT, 'docs/spec/unified-settings.md')
rowed, prefix_rows = {}, {}
for n, line in enumerate(open(spec_path, encoding='utf-8').read().splitlines(), 1):
    if not line.startswith('|'):
        continue
    first = [c.strip() for c in line.strip().strip('|').split('|')][0]
    first = first.strip('`').replace('&lt;', '<').replace('&gt;', '>')
    head = first.split('/')[0].strip().strip('`')
    pfx = re.match(r'([a-z0-9]+\.[a-z0-9_]*)<', head)
    if pfx:
        prefix_rows[pfx.group(1)] = n
    is_mode = head.startswith('<mode>.')
    if is_mode:
        head = 'chase.' + head.split('.', 1)[1]
    if not re.fullmatch(r'[a-z0-9]+\.[a-z0-9_]+', head):
        continue
    group = head.split('.')[0]
    for part in first.split('/'):
        part = part.strip().strip('`').replace('<mode>', 'chase')
        if not part or part == '—':
            continue
        key = part if '.' in part else f'{group}.{part}'
        if not re.fullmatch(r'[a-z0-9]+\.[a-z0-9_]+', key):
            continue
        rowed[key] = n
        if is_mode or key.split('.')[0] in CAMERA_MODES:
            for mode in CAMERA_MODES:
                rowed[mode + '.' + key.split('.', 1)[1]] = n


def covered(key):
    return key in rowed or any(key.startswith(p) or p.startswith(key) for p in prefix_rows)


verbose = '-v' in sys.argv
missing = sorted(k for k in knobs if not covered(k))
extra = sorted(k for k in rowed if k not in knobs and k.split('.')[0] in groups_declared)

if verbose:
    print(f'declared groups: {len(groups_declared)}   declared knobs: {len(knobs)}   '
          f'spec row keys: {len(rowed)}')
for k in missing:
    tier, path, line, fac = knobs[k]
    print(f'{os.path.relpath(path, ROOT)}:{line}: {k} is declared ({tier}, {fac}) with no row in the spec')
for k in extra:
    print(f'docs/spec/unified-settings.md:{rowed[k]}: {k} has a row but is declared nowhere')
if verbose and unattributed:
    print(f'--- factory calls no registered group claims ({len(unattributed)}), which fail nothing:')
    for u in unattributed:
        print('    ' + u.replace(ROOT + '/', ''))
