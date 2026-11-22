#!/usr/bin/env python3
"""Runs a list of hand mutants against the self-checks with one compile for the lot.

Usage: tools/selfcheck/mutants.py [--whole] [--logs DIR] <mutants.tsv>

  --whole     boot every suite for every mutant
  --logs DIR  keep each mutant boot's cx.log as DIR/<name>.cx.log

Each line of the list is a mutant, four fields split by tabs: a name, a file under managed/, the exact text to
change (it must occur in the file exactly once) and what it becomes (empty: the text is removed, which only a whole
statement may be). Blank lines and lines starting with # are skipped.

The tree is never edited. Every mutated file is copied to a folder of the runner's own, each mutant's text wrapped in
a switch -- an expression in `(on ? new : old)`, a statement (text ending in `;`) in `if (on) { new } else { old }` --
and that copy is compiled once with tools/selfcheck/run.sh --overlay. The switch class exists only in that copy and
reads which mutant is on from a file in the same folder, so a staged build carries no switch and reads no setting.
The unmutated run goes first and must pass, and writes down which unit (a registered suite, or one service's
Initialize) was running each time a mutant's site ran. Then each mutant is booted in turn on just those units; a
site that never ran boots every unit, and so does one whose units fail unmutated on their
own. A site that ran outside every unit runs in every boot. A mutant that survives its filtered boot is booted again with every unit, and that boot's verdict is the one
reported. A mutant whose switch does not
compile (a declaration, say) is compiled on its own instead, and the report says so.

Prints one line per mutant: `killed <name> <the first FAIL line>`, with every other FAIL line under it as
`also <line>`, `SURVIVED <name>`, or `UNTESTED <name>` when a mutant survives and no unit ran its site, with the
suites that SKIPPED on this host. Exit 0 when every mutant was
killed, 1 when one survived or went untested, 2 when the list is refused or the unmutated tree fails. Run it inside nix develop, and
under cap.sh on a shared machine. A mutant whose boot runs past 1.5 times the unmutated one plus 3 seconds is
killed by the timeout. Linux-only, like the rest of the developer tooling.
"""
import os
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import codehash  # noqa: E402 -- tools/ is only on the path once the line above has run

ROOT = os.path.realpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
RUN = os.path.join(ROOT, 'tools', 'selfcheck', 'run.sh')
SWITCH = 'CxMutantSwitch'
ACTIVE = 'cx-mutant-active'
ONLY = 'cx-mutant-suites'
UNITS = 'cx-mutant-units'
REACH = 'cx-mutant-reach'
OUTSIDE = '(host)'
COMPILE_TIMEOUT = 1200
BOOTING = '>> booting the self-checks'
POLL = 0.1  # the stop fires this early, so a poll's lag never carries a boot past its deadline


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
            m.alone = not switchable(text, m.at, m.old)
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


RIGHT_ASSOC = {'?', ':', '??', '=', '+=', '-=', '*=', '/=', '%=', '&=', '|=', '^=', '<<=', '>>=', '>>>=', '??=', '=>'}
STATEMENT_EDGE = set(';{}):')


def _masked(old):
    """``old`` with every comment and literal in it blanked to x's, so a bracket or operator inside one counts for nothing."""
    chars = list(old)
    for a, b in codehash.spans(old):
        for i in range(a + 1, b - 1):
            chars[i] = 'x'
    return ''.join(chars)


def _statement_ok(text, at, old):
    """A statement mutant is switched when it is one whole statement at a statement's start."""
    m = _masked(old)
    depth, ends = 0, 0
    for c in m:
        depth += c in '([{'
        depth -= c in ')]}'
        ends += c == ';' and depth == 0
    if ends != 1 or depth != 0:
        return False
    i = at - 1
    while i >= 0 and text[i].isspace():
        i -= 1
    word = re.search(r'([A-Za-z_]\w*)$', text[:i + 1])
    return i < 0 or text[i] in STATEMENT_EDGE or bool(word and word.group(1) in ('else', 'do'))


def switchable(text, at, old):
    """True when the switch around ``old``, at ``at`` in ``text``, leaves the switched-off program what the text is:
    no edge inside a comment or literal, and no operator beside it that the wrap's brackets would regroup."""
    end = at + len(old)
    if any(a < at < b or a < end < b for a, b in codehash.spans(text)):
        return False
    if old.rstrip().endswith(';'):
        return _statement_ok(text, at, old)
    inner = _loosest_inside(_masked(old))
    i = end
    while i < len(text) and text[i].isspace():
        i += 1
    if text[i:i + 1] in ('.', '[', '(') or text[i:i + 2] in ('?.', '?[', '++', '--', '->') or \
            (text[i:i + 1] == '!' and text[i:i + 2] != '!='):
        return re.fullmatch(r'@?[A-Za-z_]\w*(\.[A-Za-z_]\w*)*', old.strip()) is not None
    if inner < 0:
        return True
    j = i
    while j < len(text) and text[j] in OPCHARS:
        j += 1
    right = text[i:j]
    if not right:
        if not (i >= len(text) or text[i] in CLOSE_EDGE):
            return False
    elif right not in PREC or PREC[right] < inner or (PREC[right] == inner and right in RIGHT_ASSOC):
        return False
    i = at - 1
    while i >= 0 and text[i].isspace():
        i -= 1
    j = i
    while j >= 0 and text[j] in OPCHARS:
        j -= 1
    left = text[j + 1:i + 1]
    if not left:
        word = re.search(r'([A-Za-z_]\w*)$', text[:i + 1])
        return i < 0 or text[i] in OPEN_EDGE or bool(word and word.group(1) in EDGE_WORDS)
    k = j
    while k >= 0 and text[k].isspace():
        k -= 1
    unary = k < 0 or text[k] in OPCHARS or text[k] in OPEN_EDGE
    return not unary and left in PREC and PREC[left] > inner


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
    os.makedirs(os.path.join(work, 'managed'), exist_ok=True)
    with open(os.path.join(work, 'managed', SWITCH + '.cs'), 'w', encoding='utf-8') as f:
        f.write(SWITCH_CS.replace('@WORK@', work))



# The switch class, and the registry's filter seam, in the overlay only. An unmutated boot (mutant 0) writes down which
# unit was running each time a mutant's site ran, and every unit it was asked about; a mutant boot reads the units to run.
SWITCH_CS = '''namespace CX
{
    /// <summary>Which mutant the runner turned on, read once from its own folder, and where each site ran.</summary>
    internal static class CxMutantSwitch
    {
        internal const string Work = @"@WORK@";
        internal static readonly int Active = int.Parse(System.IO.File.ReadAllText(Work + "/cx-mutant-active").Trim());
        private static readonly System.Collections.Generic.HashSet<(int, string)> Seen = new();

        /// <summary>True for the mutant numbered <paramref name="k"/> when it is the one on.</summary>
        internal static bool On(int k)
        {
            if (Active == 0) Note(k);
            return Active == k;
        }

        /// <summary>Writes down the unit running site k, once per pair.</summary>
        private static void Note(int k)
        {
            string unit = global::CX.Framework.SelfCheckUnits.Current ?? "(host)";
            lock (Seen)
                if (Seen.Add((k, unit))) System.IO.File.AppendAllText(Work + "/cx-mutant-reach", k + "\\t" + unit + "\\n");
        }
    }
}

namespace CX.Framework
{
    public static partial class SelfCheckUnits
    {
        private static readonly System.Collections.Generic.HashSet<string>? Only =
            System.IO.File.Exists(global::CX.CxMutantSwitch.Work + "/cx-mutant-suites")
                ? new(System.IO.File.ReadAllLines(global::CX.CxMutantSwitch.Work + "/cx-mutant-suites"))
                : null;

        static partial void Admit(string unit, ref bool run)
        {
            if (Only != null) run = Only.Contains(unit);
            else if (global::CX.CxMutantSwitch.Active == 0)
                System.IO.File.AppendAllText(global::CX.CxMutantSwitch.Work + "/cx-mutant-units", unit + "\\n");
        }
    }
}
'''


def run(work, out, build, k, timeout, only=None):
    """Boots the self-checks with mutant k on (0: none), compiling first when asked, running only the units ``only``
    names (None: every unit). Returns (exit, output, the boot's seconds). The compile has COMPILE_TIMEOUT and the boot,
    timed from run.sh's booting line, has ``timeout``; past either the whole process group is stopped, exit 124."""
    with open(os.path.join(work, ACTIVE), 'w') as f:
        f.write(str(k))
    only_file = os.path.join(work, ONLY)
    if only is None:
        if os.path.exists(only_file):
            os.remove(only_file)
    else:
        with open(only_file, 'w', encoding='utf-8') as f:
            f.write(''.join(u + '\n' for u in sorted(only)))
    cmd = [RUN, '--out', out, '--overlay', work] + ([] if build else ['--no-build'])
    p = subprocess.Popen(cmd, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                         start_new_session=True)
    lines, booted = [], []

    def pump():
        for line in p.stdout:
            lines.append(line)
            if not booted and line.startswith(BOOTING):
                booted.append(time.monotonic())

    reader = threading.Thread(target=pump, daemon=True)
    reader.start()
    began, rc, stopped = time.monotonic(), None, None
    try:
        while p.poll() is None:
            now = time.monotonic()
            if now - (booted[0] if booted else began) > (timeout if booted else COMPILE_TIMEOUT) - POLL:
                stopped = now
                os.killpg(p.pid, signal.SIGKILL)
                p.wait()
                rc = 124
                break
            time.sleep(POLL / 2)
        reader.join()
    except BaseException:  # a Ctrl-C or a TERM: the compile and the host go down with the runner, not after it
        os.killpg(p.pid, signal.SIGKILL)
        p.wait()
        raise
    took = (stopped or time.monotonic()) - booted[0] if booted else 0.0
    output = ''.join(lines)
    if rc is None:
        rc = p.returncode
    else:
        output += f'\n>> TIMEOUT: the run passed its {timeout if booted else COMPILE_TIMEOUT:.0f}s and was stopped\n'
    sys.stdout.write(''.join(l + '\n' for l in output.splitlines() if l.startswith('>> compiling')))
    sys.stdout.flush()
    return rc, output, took


def read_reach(work):
    """What the unmutated boots wrote down: every unit, in order, and {mutant: the units its site ran in}."""
    units, reach = [], {}
    path = os.path.join(work, UNITS)
    if os.path.isfile(path):
        for u in open(path, encoding='utf-8').read().split('\n'):
            if u and u not in units:
                units.append(u)
    path = os.path.join(work, REACH)
    if os.path.isfile(path):
        for line in open(path, encoding='utf-8').read().split('\n'):
            if '\t' in line:
                k, unit = line.split('\t', 1)
                reach.setdefault(int(k), set()).add(unit)
    return units, reach


def filtered(ran_in):
    """The units a mutant's boot needs, or None for every unit when its site ran in none. Code outside every unit runs
    in every boot, so a unit beside it is all the boot needs."""
    units = (ran_in or set()) - {OUTSIDE}
    return units or None


def failed_lines(output, work):
    """The overlay lines a failed compile names: {(file under managed/, line)}."""
    hits = set()
    for m in re.finditer(re.escape(work) + r'/(managed/[^(]+)\((\d+),\d+\): error', output):
        hits.add((m.group(1), int(m.group(2))))
    return hits


def untested(m, skips):
    """Reports a mutant no unit's run reached: the suites that skipped on this host and belong to its file's class, or
    every one that skipped, are why. Always counts against the run, since no check judged it."""
    cls = os.path.basename(m.path).split('.')[0]
    why = [s for s in skips if s.startswith(cls + '.')] or skips
    print(f'UNTESTED {m.name:<20} no running check reaches it' + ''.join(f'; {s}' for s in why), flush=True)
    return True


def verdict(m, rc, output):
    if '>> FAIL: the managed build failed' in output:
        print(f'ERROR    {m.name:<20} the mutated file does not compile', flush=True)
        return False
    fails = [l for l in output.splitlines() if re.search(r'SelfCheck.*FAIL|init threw', l) and not l.startswith('>>')]
    if rc == 124:
        print(f'killed   {m.name:<20} by a timeout: the self-checks never finished', flush=True)
        return True
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
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(143))  # so the work folder's cleanup below runs on a TERM
    args, logs = argv[1:], None
    whole = '--whole' in args
    if whole:
        args.remove('--whole')
    if '--logs' in args:
        i = args.index('--logs')
        if i + 1 >= len(args):
            print(__doc__, file=sys.stderr)
            return 2
        logs = os.path.abspath(args[i + 1])
        del args[i:i + 2]
        os.makedirs(logs, exist_ok=True)
    if len(args) != 1:
        print(__doc__, file=sys.stderr)
        return 2
    keep = (lambda name, out: shutil.copy(os.path.join(out, 'cx.log'), os.path.join(logs, name + '.cx.log'))
            if os.path.isfile(os.path.join(out, 'cx.log')) else None) if logs else (lambda name, out: None)
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
            rc, output, _ = run(work, out, True, 0, COMPILE_TIMEOUT)
            if rc != 1 or '>> FAIL: the managed build failed' not in output:
                break
            broken = failed_lines(output, work)
            lines = {m: (m.path, open(os.path.join(ROOT, m.path), encoding='utf-8').read().count('\n', 0, m.at) + 1)
                     for m in switched}
            alone = [m for m in switched if lines[m] in broken]
            if not alone:  # an error the compiler puts elsewhere, such as a method that lost its return
                files = {path for path, _ in broken}
                alone = [m for m in switched if m.path in files]
            if not alone:
                print('!! the unmutated tree does not compile:', file=sys.stderr)
                print(output, file=sys.stderr)
                return 2
            for m in alone:
                m.alone = True
                print(f'>> {m.name}: its file does not compile switched, so it is compiled on its own', flush=True)
            switched = [m for m in switched if not m.alone]
        if rc != 0:
            print('!! the unmutated tree fails its self-checks, so no mutant can be judged:', file=sys.stderr)
            print(output, file=sys.stderr)
            return 2
        print(f'>> 1 compile for {len(switched)} switched mutant(s); the unmutated run passes', flush=True)
        rc, output, took = run(work, out, False, 0, COMPILE_TIMEOUT)
        if rc != 0:
            print('!! the unmutated tree fails its self-checks on a second boot:', file=sys.stderr)
            print(output, file=sys.stderr)
            return 2
        timeout = 1.5 * took + 3
        print(f'>> the unmutated boot took {took:.1f}s; a mutant is stopped at {timeout:.1f}s', flush=True)
        skips = [re.sub(r'^.*?\[cx\] ', '', l) for l in open(os.path.join(out, 'cx.log'), encoding='utf-8').read().splitlines()
                 if re.search(r'SelfCheck\w* SKIPPED:', l)]
        units, reach = read_reach(work)
        clean = {}

        def judge(m, only):
            """Boots mutant m on the units ``only`` names (None: every unit), and says which and how long."""
            names = ', '.join(sorted(only)) if only is not None else 'every suite'
            print(f'>> {m.name}: booting {len(units) if only is None else len(only)} of {len(units)} suites: {names}',
                  flush=True)
            rc, output, took = run(work, out, False, m.k, timeout, only)
            print(f'>> {m.name}: {"stopped after" if rc == 124 else "booted in"} {took:.1f}s', flush=True)
            return rc, output

        survived = 0
        for m in switched:
            only = None if whole else filtered(reach.get(m.k))
            if only is not None:
                key = frozenset(only)
                if key not in clean:
                    clean[key] = run(work, out, False, 0, timeout, only)[0] == 0
                if not clean[key]:
                    print(f'>> {m.name}: its suites fail unmutated on their own, so it boots every suite', flush=True)
                    only = None
            rc, output = judge(m, only)
            if only is not None and rc == 0:
                print(f'>> {m.name}: survived the filtered boot, booting it with every suite', flush=True)
                rc, output = judge(m, None)
            keep(m.name, out)
            if rc == 0 and not filtered(reach.get(m.k)):
                survived += untested(m, skips)
            else:
                survived += not verdict(m, rc, output)
        for m in [m for m in mutants if m.alone]:
            overlay(work, [m], False)
            alone = os.path.join(work, 'alone-' + str(m.k))
            rc, output, took = run(work, alone, True, 0, timeout)
            print(f'>> {m.name}: {"stopped after" if rc == 124 else "booted in"} {took:.1f}s', flush=True)
            keep(m.name, alone)
            survived += not verdict(m, rc, output)
        return 1 if survived else 0
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == '__main__':
    sys.exit(main(sys.argv))
