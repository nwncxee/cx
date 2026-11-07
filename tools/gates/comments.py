#!/usr/bin/env python3
"""CX-194 census: per .cs file under managed/ and C/C++ file under native/, the doc-comment blocks over MAX_LINES
text lines (bare <summary>/</summary> lines don't count) plus the `///` lines that
cite a ticket. Prints "<count> <file>" for tools/gates/ratchet.sh; `-v` lists each offending line instead."""
import os, re, sys

MAX_LINES = 4
TAG_ONLY = re.compile(r'^///\s*</?(summary|remarks|para)>\s*$')
TICKET = re.compile(r'(?<![\w&])#\d+\b|\bCX-\d+\b|(?<![\w!=])!\d+\b')

verbose = '-v' in sys.argv
SOURCES = (('managed', ('.cs',)), ('native', ('.c', '.cpp', '.h')))
for top, exts in SOURCES:
  for root, dirs, files in os.walk(top):
    dirs[:] = [d for d in dirs if not (top == 'native' and d in ('ffx', 'xatlas'))]
    for name in sorted(files):
        if not name.endswith(exts) or name.endswith('.g.cs'):
            continue
        path = os.path.join(root, name)
        hits, block = [], []
        for n, line in enumerate(open(path, encoding='utf-8', errors='replace'), 1):
            s = line.lstrip()
            if s.startswith('///'):
                if not TAG_ONLY.match(s):
                    block.append(n)
                if TICKET.search(s):
                    hits.append(f'{path}:{n}: ticket reference in a doc comment')
                continue
            if len(block) > MAX_LINES:
                hits.append(f'{path}:{block[0]}: doc comment is {len(block)} lines (max {MAX_LINES})')
            block = []
        if len(block) > MAX_LINES:
            hits.append(f'{path}:{block[0]}: doc comment is {len(block)} lines (max {MAX_LINES})')
        if verbose:
            print('\n'.join(hits)) if hits else None
        elif hits:
            print(len(hits), path)
