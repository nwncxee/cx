#!/usr/bin/env python3
"""Runs a list of hand mutants against the self-checks with one compile for the lot.

Usage: tools/selfcheck/mutants.py <mutants.tsv> [--keep]

Each line of the list is a mutant, four fields split by tabs: a name, a file under managed/, the exact text to
change (it must occur in the file exactly once) and what it becomes (empty: the text is removed, which only a whole
statement may be). Blank lines and lines starting with # are skipped.

The tree is never edited. Every mutated file is copied to a folder of the runner's own, each mutant's text wrapped in
a switch -- an expression in `(on ? new : old)`, a statement (text ending in `;`) in `if (on) { new } else { old }` --
and that copy is compiled once with tools/selfcheck/run.sh --overlay. The switch class exists only in that copy and
reads which mutant is on from a file in the same folder, so a staged build carries no switch and reads no setting.
The unmutated run goes first and must pass; then each mutant is booted in turn. A mutant whose switch does not
compile (a declaration, say) is compiled on its own instead, and the report says so.

Prints one line per mutant: `killed <name> <the first FAIL line>`, with every other FAIL line under it as
`also <line>`, or `SURVIVED <name>`. Exit 0 when every mutant was
killed, 1 when one survived, 2 when the list is refused or the unmutated tree fails. Run it inside nix develop, and
under cap.sh on a shared machine. Linux-only, like the rest of the developer tooling.
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import codehash  # noqa: E402 -- tools/ is only on the path once the line above has run

ROOT = os.path.realpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
RUN = os.path.join(ROOT, 'tools', 'selfcheck', 'run.sh')
SWITCH = 'CxMutantSwitch'
ACTIVE = 'cx-mutant-active'


class Mutant:
    def __init__(self, k, name, path, old, new):
        self.k, self.name, self.path, self.old, self.new = k, name, path, old, new
        self.alone = False
        self.at = -1

    def statement(self):
        return self.old.rstrip().endswith(';')


def parse(listing):
    """The mutants of the list, or a refusal per bad line, each naming its mutant."""
    mutants, refusals = [], []
    with open(listing, encoding='utf-8') as f:
        for n, line in enumerate(f, 1):
            line = line.rstrip('\n')
            if not line.strip() or line.lstrip().startswith('#'):
                continue
            fields = line.split('\t')
            if len(fields) != 4:
                refusals.append(f'line {n}: {len(fields)} tab-separated fields, not 4 (name, file, old, new)')
                continue
            name, path, old, new = fields
            mutants.append(Mutant(len(mutants) + 1, name, path, old, new))
    names = [m.name for m in mutants]
    for m in mutants:
        full = os.path.join(ROOT, m.path)
        if names.count(m.name) > 1:
            refusals.append(f'{m.name}: the name is used {names.count(m.name)} times')
        if not m.path.startswith('managed/') or not m.path.endswith('.cs') or not os.path.isfile(full):
            refusals.append(f'{m.name}: {m.path} is not a .cs file under managed/')
            continue
        text = open(full, encoding='utf-8').read()
        count = text.count(m.old) if m.old else 0
        if count != 1:
            refusals.append(f'{m.name}: its text occurs {count} time(s) in {m.path}, not once')
            continue
        m.at = text.index(m.old)
        if not m.new and not m.statement():
            refusals.append(f'{m.name}: only a whole statement (text ending in ;) may be removed')
    by_file = {}
    for m in mutants:
        if m.at >= 0:
            by_file.setdefault(m.path, []).append(m)
    for path, ms in by_file.items():
        text = open(os.path.join(ROOT, path), encoding='utf-8').read()
        for m in ms:
            m.alone = not m.statement() and not switchable(text, m.at, m.old)
        ms.sort(key=lambda m: m.at)
        for a, b in zip(ms, ms[1:]):
            if a.at + len(a.old) > b.at:
                refusals.append(f'{b.name}: its text overlaps {a.name}\'s in {path}')
    return mutants, refusals


# C#'s binary operators by precedence, tightest first; a wrap's brackets are safe when the operators beside the old
# text bind looser than the loosest operator inside it (and a left-associative tie on its right only).
PREC = {op: p for p, ops in enumerate([
    ['*', '/', '%'], ['+', '-'], ['<<', '>>', '>>>'], ['<', '>', '<=', '>=', 'is', 'as'], ['==', '!='],
    ['&'], ['^'], ['|'], ['&&'], ['||'], ['??'], ['?', ':'],
    ['=', '+=', '-=', '*=', '/=', '%=', '&=', '|=', '^=', '<<=', '>>=', '>>>=', '??=', '=>']]) for op in ops}
LOOSEST = max(PREC.values()) + 1
OPCHARS = set('+-*/%<>=!&|^?:~')
OPEN_EDGE = set('([{,;')
CLOSE_EDGE = set(')]},;')
EDGE_WORDS = {'return', 'throw', 'in', 'case', 'when', 'else', 'yield'}


def _loosest_inside(old):
    """The precedence rank of the loosest binary operator at the top level of ``old`` (-1: none, an atom)."""
    depth, worst, i, prev_operand = 0, -1, 0, False
    while i < len(old):
        c = old[i]
        if c in '([{':
            depth += 1
        elif c in ')]}':
            depth -= 1
        if c in OPCHARS:
            j = i
            while j < len(old) and old[j] in OPCHARS:
                j += 1
            op = old[i:j]
            if depth == 0 and prev_operand and op in PREC:
                worst = max(worst, PREC[op])
            elif depth == 0 and prev_operand:
                worst = LOOSEST
            prev_operand = False
            i = j
            continue
        word = re.match(r'[A-Za-z_]\w*', old[i:])
        if word and depth == 0 and prev_operand and word.group() in ('is', 'as'):
            worst = max(worst, PREC['is'])
            prev_operand = False
            i += len(word.group())
            continue
        if not c.isspace():
            prev_operand = c not in '([{'
        i += len(word.group()) if word else 1
    return worst


def switchable(text, at, old):
    """True when wrapping ``old``, at ``at`` in ``text``, as ``(on ? (new) : (old))`` leaves the switched-off program
    what the text is: not inside a comment or literal, and no operator beside it that the brackets would regroup."""
    end = at + len(old)
    if any(a < at < b or a < end < b for a, b in codehash.spans(text)):
        return False
    inner = _loosest_inside(old)
    if inner < 0:
        return True
    i = at - 1
    while i >= 0 and text[i].isspace():
        i -= 1
    j = i
    while j >= 0 and text[j] in OPCHARS:
        j -= 1
    left = text[j + 1:i + 1]
    if not left:
        word = re.search(r'([A-Za-z_]\w*)$', text[:i + 1])
        if not (i < 0 or text[i] in OPEN_EDGE or (word and word.group(1) in EDGE_WORDS)):
            return False
    else:
        k = j
        while k >= 0 and text[k].isspace():
            k -= 1
        unary = k < 0 or text[k] in OPCHARS or text[k] in OPEN_EDGE
        if unary or left not in PREC or PREC[left] <= inner:
            return False
    i = end
    while i < len(text) and text[i].isspace():
        i += 1
    j = i
    while j < len(text) and text[j] in OPCHARS:
        j += 1
    right = text[i:j]
    if not right:
        return i >= len(text) or text[i] in CLOSE_EDGE
    if right.startswith('?.') or right not in PREC:
        return False
    return PREC[right] >= inner


def wrap(m):
    on = f'global::CX.{SWITCH}.On({m.k})'
    if m.statement():
        return f'if ({on}) {{ {m.new} }} else {{ {m.old} }}' if m.new else f'if (!{on}) {{ {m.old} }}'
    return f'({on} ? ({m.new}) : ({m.old}))'


def overlay(work, mutants, switched):
    """Writes the overlay folder: each mutated file with its switched mutants wrapped, its others plain (one alone
    mutant per build), and the switch class."""
    shutil.rmtree(os.path.join(work, 'managed'), ignore_errors=True)
    by_file = {}
    for m in mutants:
        by_file.setdefault(m.path, []).append(m)
    for path, ms in by_file.items():
        text = open(os.path.join(ROOT, path), encoding='utf-8').read()
        for m in sorted(ms, key=lambda m: m.at, reverse=True):
            text = text[:m.at] + (wrap(m) if switched else m.new) + text[m.at + len(m.old):]
        dest = os.path.join(work, path)
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        open(dest, 'w', encoding='utf-8').write(text)
    active = os.path.join(work, ACTIVE)
    with open(os.path.join(work, 'managed', SWITCH + '.cs'), 'w', encoding='utf-8') as f:
        f.write('namespace CX;\n\n'
                '/// <summary>Which mutant the runner turned on, read once from its own folder.</summary>\n'
                f'internal static class {SWITCH}\n{{\n'
                f'    private static readonly int Active = int.Parse(System.IO.File.ReadAllText(@"{active}").Trim());\n\n'
                '    /// <summary>True for the mutant numbered <paramref name="k"/> when it is the one on.</summary>\n'
                '    internal static bool On(int k) => Active == k;\n}\n')


def run(work, out, build, k):
    """Boots the self-checks with mutant k on (0: none), compiling first when asked. Returns (exit, output)."""
    with open(os.path.join(work, ACTIVE), 'w') as f:
        f.write(str(k))
    cmd = [RUN, '--out', out, '--overlay', work] + ([] if build else ['--no-build'])
    p = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True)
    sys.stdout.write(''.join(l + '\n' for l in p.stdout.splitlines() if l.startswith('>> compiling')))
    sys.stdout.flush()
    return p.returncode, p.stdout + p.stderr


def failed_lines(output, work):
    """The overlay lines a failed compile names: {(file under managed/, line)}."""
    hits = set()
    for m in re.finditer(re.escape(work) + r'/(managed/[^(]+)\((\d+),\d+\): error', output):
        hits.add((m.group(1), int(m.group(2))))
    return hits


def verdict(m, rc, output):
    if '>> FAIL: the managed build failed' in output:
        print(f'ERROR    {m.name:<20} the mutated file does not compile', flush=True)
        return False
    fails = [l for l in output.splitlines() if re.search(r'SelfCheck.*FAIL|init threw', l) and not l.startswith('>>')]
    if rc == 1:
        line = fails[0] if fails else output.strip().splitlines()[-1]
        print(f'killed   {m.name:<20} {re.sub(r"^.*?\[cx\] ", "", line)}', flush=True)
        for more in fails[1:]:
            print(f'           also {re.sub(r"^.*?\[cx\] ", "", more)}', flush=True)
        return True
    if rc == 0:
        print(f'SURVIVED {m.name}', flush=True)
        return False
    print(f'ERROR    {m.name:<20} the run exited {rc}: {output.strip().splitlines()[-1] if output.strip() else ""}', flush=True)
    return False


def main(argv):
    args = [a for a in argv[1:] if a != '--keep']
    if len(args) != 1:
        print(__doc__, file=sys.stderr)
        return 2
    mutants, refusals = parse(args[0])
    if refusals or not mutants:
        for r in refusals or ['the list holds no mutant']:
            print(f'!! refusing {r}', file=sys.stderr)
        return 2
    work = tempfile.mkdtemp(prefix='cx-mutants.')
    try:
        out = os.path.join(work, 'build')
        for m in mutants:
            if m.alone:
                print(f'>> {m.name}: a switch would regroup or land in a literal, so it is compiled on its own', flush=True)
        switched = [m for m in mutants if not m.alone]
        while True:
            overlay(work, switched, True)
            rc, output = run(work, out, True, 0)
            if rc != 1 or '>> FAIL: the managed build failed' not in output:
                break
            broken = failed_lines(output, work)
            lines = {m: (m.path, open(os.path.join(ROOT, m.path), encoding='utf-8').read().count('\n', 0, m.at) + 1)
                     for m in switched}
            alone = [m for m in switched if lines[m] in broken]
            if not alone:
                print('!! the unmutated tree does not compile:', file=sys.stderr)
                print(output, file=sys.stderr)
                return 2
            for m in alone:
                m.alone = True
                print(f'>> {m.name}: its switch does not compile, so it is compiled on its own', flush=True)
            switched = [m for m in switched if not m.alone]
        if rc != 0:
            print('!! the unmutated tree fails its self-checks, so no mutant can be judged:', file=sys.stderr)
            print(output, file=sys.stderr)
            return 2
        print(f'>> 1 compile for {len(switched)} switched mutant(s); the unmutated run passes', flush=True)
        survived = 0
        for m in switched:
            rc, output = run(work, out, False, m.k)
            survived += not verdict(m, rc, output)
        for m in [m for m in mutants if m.alone]:
            overlay(work, [m], False)
            rc, output = run(work, os.path.join(work, 'alone-' + str(m.k)), True, 0)
            survived += not verdict(m, rc, output)
        return 1 if survived else 0
    finally:
        if '--keep' in argv:
            print(f'>> kept {work}', file=sys.stderr)
        else:
            shutil.rmtree(work, ignore_errors=True)


if __name__ == '__main__':
    sys.exit(main(sys.argv))
