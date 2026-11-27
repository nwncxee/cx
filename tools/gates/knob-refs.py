#!/usr/bin/env python3
"""CX-198: every `<group>.<knob>` key named in a string literal under managed/ has to be a knob that is
actually declared -- a log line or a help text naming a knob that no longer exists is a lie the compiler
cannot catch (`alloy.renderthread=1 but alloy.scenedb=0`, where `alloy.scenedb` had been deleted).

Declarations: `KnobGroup.Declare("<group>", ...)` for the group, and the factories
(`Toggle`/`Slider`/`Dropdown`/`Swatch`/`Text`/`List`/`Button`/`Bag`) for the knob segment. A group's master
row is always `<group>.enabled`. Group and knob names are collected tree-wide rather than per file, because a
group's rows are often declared across its partials (the `weather` group is eight files).

Three shapes are NOT knob keys and are skipped, or every one of them would read as a violation:
  - a dotted token whose tail is a file extension (`hotkeys.cfg`, `ui.ttf`);
  - a key on a line that reaches `ServerPolicy.` or `Routed(` -- the server-policy and action-id namespaces,
    which share the shape and are not knobs;
  - a key `Settings.RetiredKeys` lists: an older CX wrote it, cx.cfg drops it on read, and the self-checks that
    prove so have to name it. The list is read out of Settings.cs, never restated; an entry that names a
    `const string` of Settings.cs counts as that const's value.

Prints "<count> <file>" per offending file, for tools/gates/ratchet.sh; `-v` lists each key instead."""
import os, re, sys

DECL = re.compile(r'KnobGroup\.Declare\(\s*"([A-Za-z0-9_.\-]+)"')
FACT = re.compile(r'\.(?:Toggle|Slider|Dropdown|Swatch|Text|List|Button|Bag)\s*(?:<[^>()]*>)?\s*\(\s*"([A-Za-z0-9_.\-]+)"')
LIT = re.compile(r'"((?:[^"\\\n]|\\.)*)"')
EXT = {'cfg', 'ttf', 'glsl', 'shd', 'cs', 'md', 'log', 'txt', 'tsv', 'json', 'ini', 'png', 'jpg', 'dds',
       'tga', 'mdl', 'lod', 'mtr', 'txi', 'bmf', 'set', 'are', '2da', 'tlk', 'so', 'dll', 'exe', 'zip',
       'wav', 'py', 'sh', 'html', 'xml', 'bin', 'sqlite', 'key', 'bif', 'hak'}
OTHER_NS = ('ServerPolicy.', 'Routed(')
RETIRED = re.compile(r'RetiredKeys\s*=\s*\[([^\]]*)\]')


def walk():
    for root, _, files in os.walk('managed'):
        for name in sorted(files):
            if name.endswith('.cs'):
                yield os.path.join(root, name)


groups, knobs, paths = set(), {'enabled'}, list(walk())
for path in paths:
    text = open(path, encoding='utf-8', errors='replace').read()
    groups |= set(DECL.findall(text))
    knobs |= set(FACT.findall(text))
retired = set()
settings = open('managed/Framework/Settings.cs', encoding='utf-8').read()
consts = dict(re.findall(r'const\s+string\s+(\w+)\s*=\s*"([^"]+)"', settings))
for m in RETIRED.finditer(settings):
    retired |= set(re.findall(r'"([^"]+)"', m.group(1)))
    retired |= {consts[n] for n in re.findall(r'\b([A-Za-z_]\w*)\b', LIT.sub('', m.group(1))) if n in consts}
if not retired:
    print('!! knob-refs: no Settings.RetiredKeys found -- the collector is broken', file=sys.stderr)
    sys.exit(2)
if not groups:
    print('!! knob-refs: no KnobGroup.Declare found -- the collector is broken', file=sys.stderr)
    sys.exit(2)

REF = re.compile(r'(?<![A-Za-z0-9_.])(' + '|'.join(sorted(groups, key=len, reverse=True)) + r')\.([a-z][a-z0-9_]*)\b')

verbose = '-v' in sys.argv
for path in paths:
    hits = []
    for n, line in enumerate(open(path, encoding='utf-8', errors='replace'), 1):
        if any(k in line for k in OTHER_NS):
            continue
        for lit in LIT.findall(line):
            for group, knob in REF.findall(lit):
                if knob in knobs or knob in EXT or f'{group}.{knob}' in retired:
                    continue
                hits.append(f'{path}:{n}: "{group}.{knob}" is not a declared knob')
    if verbose:
        if hits:
            print('\n'.join(hits))
    elif hits:
        print(len(hits), path)
