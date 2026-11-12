#!/usr/bin/env python3
"""Doc comments that hold two <summary> elements, per .cs file under managed/, for the ratchet.

    python3 tools/gates/doc-stack.py       # "<count> <file>" lines
    python3 tools/gates/doc-stack.py -v    # ...and every block, file:line

A second <summary> in one `///` block is a doc left behind when its declaration moved or went away. The
declaration under the block shows the last summary, so the first one describes something else, or nothing.
"""
import os
import sys

verbose = '-v' in sys.argv
for root, dirs, files in os.walk('managed'):
    dirs[:] = sorted(d for d in dirs if d not in ('bin', 'obj'))
    for name in sorted(files):
        if not name.endswith('.cs') or name.endswith('.g.cs'):
            continue
        path = os.path.join(root, name)
        hits, summaries, first = [], 0, 0
        for n, line in enumerate(open(path, encoding='utf-8', errors='replace'), 1):
            s = line.lstrip()
            if s.startswith('///'):
                first = first or n
                summaries += s.count('<summary>')
                continue
            if summaries > 1:
                hits.append(f'{path}:{first}: doc comment holds {summaries} <summary> elements')
            summaries, first = 0, 0
        if verbose:
            for h in hits:
                print(h)
        elif hits:
            print(len(hits), path)
