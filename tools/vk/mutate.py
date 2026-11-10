#!/usr/bin/env python3
"""Replaces one whole line of a file with another, or removes it, for tools/vk/mutants.sh.

Usage: mutate.py <file> <old line> [<new line>]

The old line must be in the file exactly once, counting its indentation and ignoring its line ending: a mutant that
lands in two places, or in none because the code moved, is a control that checks nothing, so it exits 1 and says so
rather than editing. With no new line the old one is removed.
"""
import sys


def main(path, old, new):
    lines = open(path, encoding='utf-8').read().splitlines(keepends=True)
    at = [i for i, l in enumerate(lines) if l.rstrip('\r\n') == old]
    if len(at) != 1:
        print(f'!! {path} holds that line {len(at)} time(s), not once', file=sys.stderr)
        return 1
    i = at[0]
    end = lines[i][len(lines[i].rstrip('\r\n')):]
    lines[i:i + 1] = [] if new is None else [new + end]
    open(path, 'w', encoding='utf-8').write(''.join(lines))
    return 0


if len(sys.argv) not in (3, 4):
    print(__doc__, file=sys.stderr)
    sys.exit(2)
sys.exit(main(sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) == 4 else None))
