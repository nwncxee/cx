#!/usr/bin/env python3
"""CX-484: every declared knob has its row in docs/spec/unified-settings.md, and every key the spec rows is
declared. The spec is the settings tree's one description -- the panel, cx.cfg and the chat command are three
views of a declaration -- so a knob with no row is a feature nobody can read about, and a row with no
declaration is a promise the client does not keep.

Declarations: a group is `<var> = KnobGroup.Declare("<name>", ...)` inside some class, and its rows are the
factory calls on that var. A class's rows are spread over its partials and one file may hold several classes,
so a call is attributed to the INNERMOST class whose body contains it, and groups merge per class name across
files. Four shapes declare keys no factory literal shows, and each cost this census a wrong answer before it
was written down:
  - a `Declare` whose group name is a constructor parameter (the three camera modes) takes one group per
    `new <Class>("<name>"` site;
  - `new TextStyle(G, ..., prefix: "p")` declares `<group>.<p>font` / `typeface` / `outline`;
  - `Quality.Area("x", ...)` declares `quality.x`.
  - a class whose constructor takes a `KnobGroup` declares its rows on that parameter, and takes the group each
    `<Class>.Declare(<group var>, ...)` site passes (the release notice's knobs on General's About), and a
    method of any name with a `KnobGroup <p>` parameter gives `<p>` every group its shipped sites pass, through
    any number of hops.
A group declared with `master:` also declares `<group>.enabled`. A `new KnobGroup(...)` is a self-check's
throwaway, registered nowhere, so an unattributed factory call is reported only under `-v` and fails nothing.

Rows: a table row's first cell, `group.knob`, or a summary cell naming a family (`group.a / b / c`). A
`group.prefix<...>` cell stands for its whole family and for the bag key it is named after, and `<mode>.x`
stands for all three camera modes. A family covers the SHAPE its cell declares and nothing else:
`ui.custom_<role>` covers `ui.custom_hilite` but not `ui.customfrom`, and `chat.w<N>.*` covers `chat.w3.rect`
but not a future `chat.wrap` -- a stem taken as a bare prefix would exempt the whole family tree.

Prints one line per key with no row and per row with no key; `-v` prints the whole census."""
import bisect, os, re, sys, collections

ROOT = os.environ.get('CX_ROOT', '.')
FACTORIES = ('Toggle', 'Slider', 'Dropdown', 'Swatch', 'Text', 'List', 'Button', 'Link', 'Bag')
DECL = re.compile(r'(?:\b(\w++)\s*=\s*)?KnobGroup\.Declare\(\s*(?:"([A-Za-z0-9_.\-]+)"|(\w+))')
FACT = re.compile(r'\b(\w++)\.(' + '|'.join(FACTORIES) + r')\s*(?:<[^>()]*>)?\s*\(')
TYPE = re.compile(r'\b(?:class|struct|record)\s+(\w+)')
NEWOF = re.compile(r'new\s+(\w+)\s*\(\s*"([a-z0-9]+)"')
HANDED = re.compile(r'\b(?:private|public|internal)\s+(\w+)\s*\(\s*KnobGroup\s+(\w+)\s*[,)]')
RELAY = re.compile(r'\b(\w+)\s*\(([^()]*\bKnobGroup\s+\w+[^()]*)\)\s*(?:=>|\{)')
TEXTSTYLE = re.compile(r'new\s+(?:[\w.]+\.)?TextStyle\s*(\()\s*(\w+)\s*,')
CAMERA_MODES = ('chase', 'topdown', 'driving')


_STOPS = {}
_ARG_STOPS = re.compile(r'"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'|[()\[\]{}]|,')
_TAILS = {'"': re.compile(r'(?:\\.|[^"\\])*', re.S), "'": re.compile(r"(?:\\.|[^'\\])*", re.S)}


def split_args(span):
    """A call's arguments as (name or None, text), split only at commas outside brackets and string literals."""
    parts, depth, last = [], 0, 0
    for m in _ARG_STOPS.finditer(span):
        t = m.group()
        depth += (t in '([{') - (t in ')]}')
        if t == ',' and depth == 0:
            parts.append(span[last:m.start()])
            last = m.end()
    parts.append(span[last:])
    return [re.fullmatch(r'\s*(?:(\w+)\s*:)?\s*(.*?)\s*', a, re.S).groups() for a in parts]


def balanced(text, start, open_c='(', close_c=')'):
    """The span inside the brackets opening at `start`, string literals and comments skipped. Jumps from one
    quote, comment or bracket to the next rather than stepping every character."""
    stops = _STOPS.get((open_c, close_c))
    if stops is None:
        stops = _STOPS[(open_c, close_c)] = re.compile('"|\'|//|/\\*|' + re.escape(open_c) + '|' + re.escape(close_c))
    depth, i, n = 0, start, len(text)
    while i < n:
        m = stops.search(text, i)
        if m is None:
            break
        j, t = m.start(), m.group()
        if t in _TAILS:
            i = _TAILS[t].match(text, j + 1).end() + 1
        elif t == '//':
            i = text.find('\n', j)
            if i < 0:
                break
            i += 1
        elif t == '/*':
            i = text.find('*/', j)
            if i < 0:
                break
            i += 2
        else:
            depth += 1 if t == open_c else -1
            if depth == 0:
                return text[start + 1:j], j
            i = j + 1
    return text[start + 1:], n


_NEWLINES = {}


def line_of(path, text, pos):
    """The 1-based line `pos` falls on, from the file's newline offsets, found once."""
    nl = _NEWLINES.get(path)
    if nl is None:
        nl = _NEWLINES[path] = [m.start() for m in re.finditer('\n', text)]
    return bisect.bisect_left(nl, pos) + 1


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
    for m in (DECL.finditer(text) if 'KnobGroup.Declare' in text else ()):
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
var_group = collections.defaultdict(set)
for pairs in list(class_group.values()):
    for v, g in pairs:
        var_group[v].add(g)
# Only a file that names KnobGroup can declare a relay or a handed class, so the hook's time goes on those alone.
grouped = {p: t for p, t in texts.items() if 'KnobGroup' in t}
relays = []
for path, text in grouped.items():
    for m in RELAY.finditer(text):
        home = owner(path, m.start())
        params = [a.split('=')[0].split() for a in m.group(2).split(',')]
        relays += [(home, m.group(1), i, a[-1]) for i, a in enumerate(params) if len(a) >= 2]
# Each relay's call sites, found in one scan of the shipped files: they stay put while the groups grow.
calls = collections.defaultdict(list)
if relays:
    sites = re.compile(r'(?<!\w)(?:(\w+)\.)?(' + '|'.join({name for _, name, _, _ in relays}) + r')\s*\(')
    for text in (t for q, t in texts.items() if 'SelfCheck' not in q):
        for site in sites.finditer(text):
            calls[site.group(2)].append((site.group(1), split_args(balanced(text, site.end() - 1)[0])))
for _turn in range(len(relays) + 1):
    grew = False
    for home, name, at, param in relays:
        # A site qualified by another class calls that class's method of the same name.
        for args in (a for q, a in calls[name] if q in (None, home, 'this', 'base')):
            given = next((v for n, v in args if n == param), None)
            if given is None and at < len(args) and args[at][0] is None:
                given = args[at][1]
            arg = re.fullmatch(r'(?:\w+\.)*(\w+)', given) if given is not None else None
            if arg and not var_group[arg.group(1)] <= var_group[param]:
                var_group[param] |= var_group[arg.group(1)]
                grew = True
    if not grew:
        break
handed = [m for text in grouped.values() for m in HANDED.findall(text)]
declares = re.compile(r'\b(' + '|'.join({c for c, _ in handed}) + r')\.Declare\(\s*(?:\w+\.)*(\w+)\s*[,)]')
passed = collections.defaultdict(list)
for cls, var in (m for t in texts.values() if '.Declare(' in t for m in declares.findall(t)):
    passed[cls].append(var)
for cls, param in handed:
    for var in passed[cls]:
        class_group[cls].update((param, g) for g in var_group[var])

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
        line = line_of(path, text, m.start())
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
        line = line_of(path, text, m.start())
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
    for m in (DECL.finditer(text) if 'KnobGroup.Declare' in text else ()):
        var, group, _ident = m.group(1), m.group(2), m.group(3)
        cls = owner(path, m.start())
        body, _ = balanced(text, text.index('(', m.start()))
        if not re.search(r'(?:^|[(,]\s*)master:\s*"', body):
            continue
        t = re.search(r'masterTier:\s*Tier\.(\w+)', body)
        line = line_of(path, text, m.start())
        for g in ([group] if group else sorted(g for v, g in class_group.get(cls, ()) if v == (var or 'G'))):
            knobs[f'{g}.enabled'] = (t.group(1) if t else 'User', path, line, 'master')

groups_declared = {g for pairs in class_group.values() for _v, g in pairs}
if not groups_declared or len(knobs) < 100:
    print(f'!! knob-spec-rows: {len(groups_declared)} group(s) and {len(knobs)} knob(s) found -- '
          'the collector is broken', file=sys.stderr)
    sys.exit(2)

FAMILY = re.compile(r'^([a-z0-9]+\.[a-z0-9_]*)(<[A-Za-z]+>)((?:\.\*)?)')


def family_shape(head):
    """The anchored pattern a `group.prefix<...>` cell declares, or None. `<N>` is an index, any other
    placeholder one key segment, and a trailing `.*` the rest of the family's path."""
    m = FAMILY.match(head)
    if not m:
        return None, None
    stem, place, tail = m.group(1), m.group(2), m.group(3)
    mid = r'\d+' if re.fullmatch(r'<[A-Z]>', place) else r'[a-z0-9_]+'
    return stem, re.compile('^' + re.escape(stem) + mid + (r'\..+' if tail else '') + '$')


spec_path = os.path.join(ROOT, 'docs/spec/unified-settings.md')
rowed, families = {}, []
for n, line in enumerate(open(spec_path, encoding='utf-8').read().splitlines(), 1):
    if not line.startswith('|'):
        continue
    first = [c.strip() for c in line.strip().strip('|').split('|')][0]
    first = first.strip('`').replace('&lt;', '<').replace('&gt;', '>')
    head = first.split('/')[0].strip().strip('`')
    stem, shape = family_shape(head)
    if shape:
        families.append((shape, n))
        rowed[stem] = n
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
    return key in rowed or any(shape.match(key) for shape, _n in families)


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
