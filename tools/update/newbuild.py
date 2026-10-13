#!/usr/bin/env python3
"""Update day: write the build file for a new client build, and the worklist of what still needs a human.

    tools/update/newbuild.py OLD_BIN NEW_BIN --name 8193.38-1 [--write]

OLD_BIN / NEW_BIN are game `bin/` directories (`linux-x86/nwmain-linux`, `win32/nwmain.exe`) -- the live
Steam install for the new one, a `tools/update/snapshot.sh` copy for the old. OLD_BIN must be a build
some `managed/Framework/Builds/*.tsv` already describes: its values are the starting point.

What it does, and nothing it cannot back with evidence:
  * keys    -- the new build-id, CodeView GUID+age and PE stamp, read from the binaries
  * rva     -- every SymbolMap At() re-anchored by rvamatch (exports, RTTI, xref votes); weak ones are
               written as `# TODO` lines, which ClientBuilds.SelfCheck refuses until a human fills them
  * lva     -- every SymbolMap.LinuxAnchored static function, re-anchored the same way on the two ELFs
  * offset  -- layout.py: a row whose canary functions are byte-identical is copied from the old file; a
               changed or blind row is written with its old values commented out as `# CHECK`, which
               ClientBuilds.SelfCheck refuses until a human confirms or corrects it
  * symbols -- every Linux mangled name and Windows export CX resolves by name, checked present
"""
import argparse, glob, os, sys
from binfmt import Image
from rvamatch import Matcher, same
from tables import ROOT, FW, fmt, read_builds, symbols
import layout


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('old'); ap.add_argument('new')
    ap.add_argument('--name', help='build label, e.g. 8193.38-1 (default: revision from build.txt)')
    ap.add_argument('--write', action='store_true', help='write managed/Framework/Builds/<name>.tsv')
    ap.add_argument('--out', help='write the build file here instead (a rehearsal must not land in Builds/)')
    ap.add_argument('--rehearse', action='store_true',
                    help='run even if NEW_BIN is already known: OLD_BIN == NEW_BIN must come back all-carried')
    a = ap.parse_args()

    ol, nl = Image(f'{a.old}/linux-x86/nwmain-linux'), Image(f'{a.new}/linux-x86/nwmain-linux')
    ow, nw = Image(f'{a.old}/win32/nwmain.exe'), Image(f'{a.new}/win32/nwmain.exe')
    builds = read_builds()
    old = next((b for b in builds if b.get('linux') == ol.cv), None)
    if not old:
        sys.exit(f'OLD_BIN {a.old} (linux {ol.cv}) is not a known build; point it at a snapshot of one')
    if old.get('windows', '').upper() != (ow.cv or '').upper():
        sys.exit(f'OLD_BIN windows key {ow.cv} != {old["name"]}\'s {old.get("windows")}: mixed snapshot')
    known = next((b for b in builds if b.get('linux') == nl.cv or b.get('windows', '').upper() == (nw.cv or '').upper()), None)
    if known and not a.rehearse:
        print(f'NEW_BIN is already known as {known["name"]} ({os.path.relpath(known["path"], ROOT)}); nothing to do')
        return
    rev = open(f'{a.new}/linux-x86/build.txt').readline().strip() if os.path.exists(f'{a.new}/linux-x86/build.txt') else '?'
    name = a.name or rev[:10]

    consts, win = symbols()
    todo = []

    # ---- symbols CX resolves by name ----
    have_l = set(nl.names) | set(nl.data_names)
    had_l = set(ol.names) | set(ol.data_names)
    gone_l = sorted(c for c, v in consts.items() if v in had_l and v not in have_l)
    have_w = set(nw.names) | set(nw.data_names)
    gone_w = sorted(c for c, kind, v in win if kind == 'Nm' and v not in have_w)
    changed_hooks = sorted(c for c, v in consts.items()
                           if v in ol.names and v in nl.names
                           and not same(ol.read(ol.names[v], ol.funcs.get(ol.names[v], 0)),
                                        nl.read(nl.names[v], nl.funcs.get(nl.names[v], 0))))
    for c in gone_l:
        todo.append(f'linux symbol gone: {c} = {consts[c]} (renamed or re-signatured: code change)')
    for c in gone_w:
        todo.append(f'windows export gone: {c} (renamed or re-signatured: code change)')

    # ---- RVAs ----
    m = Matcher(ow, nw)
    rva_lines = []
    for c, kind, v in win:
        if kind != 'At':
            continue
        mangled = consts[c]
        before = old['rvas'].get(mangled)
        if before is None:
            rva_lines.append(f'# TODO rva\t{mangled}\t?\t# {c} -- {old["name"]} has no rva for it')
            todo.append(f'rva {c}: no old value in {old["name"]}')
            continue
        got, how, score = m.match(before)
        if got is not None and how in ('name', 'delta', 'interval', 'rtti', 'xref'):
            rva_lines.append(f'rva\t{mangled}\t{got:#x}\t# {c} ({how} {score:.2f})')
        else:
            cand = f'{got:#x}' if got is not None else '?'
            rva_lines.append(f'# TODO rva\t{mangled}\t{cand}\t# {c} {how} {score:.2f} -- confirm in Ghidra '
                             f'(old {before:#x}, docs/windows-symbols.md)')
            todo.append(f'rva {c}: {how} {score:.2f}, candidate {cand}')

    # ---- Linux static functions: the same matcher on the two ELFs ----
    ml = Matcher(ol, nl)
    lva_lines = []
    for sname, before in sorted(old['lvas'].items()):
        got, how, score = ml.match(before)
        if got is not None and how in ('name', 'delta', 'interval', 'xref'):
            lva_lines.append(f'lva\t{sname}\t{got:#x}\t# ({how} {score:.2f})')
        else:
            cand = f'{got:#x}' if got is not None else '?'
            lva_lines.append(f'# TODO lva\t{sname}\t{cand}\t# {how} {score:.2f} -- confirm in Ghidra (old {before:#x})')
            todo.append(f'lva {sname}: {how} {score:.2f}, candidate {cand}')

    # ---- offsets ----
    src = ''.join(open(f).read() for f in sorted(glob.glob(os.path.join(FW, 'EngineLayout*.cs'))))
    off_lines, verdicts = [], layout.check(src, old['offsets'], ol, nl, ow, nw)
    for (rname, cls, field, doc), verdict, notes in verdicts:
        lin, win_v = old['offsets'].get((cls, field), (None, None))
        cells = f'{cls}\t{field}\t{fmt(lin)}\t{fmt(win_v)}'
        if verdict == 'carried' and lin is not None:
            off_lines.append(f'offset\t{cells}\t# {rname}')
            continue
        # the old values, commented out: SelfCheck refuses the file until a human confirms or corrects them
        off_lines.append(f'# CHECK offset\t{cells}\t# {rname}: {verdict}; ' + '; '.join(notes[:3]))
        # 'likely' is a hint, not a verdict: 4 of 24 were stale on the 36->37 server backtest
        todo.append(f'offset {cls}.{field} ({rname}): {verdict} -- ' + '; '.join(notes[:3]))

    counts = {v: sum(1 for _, x, _ in verdicts if x == v) for v in ('carried', 'likely', 'changed', 'blind')}
    nrva = sum(1 for l in rva_lines if l.startswith('rva'))
    head = [f'# NWN:EE {name}, revision {rev}.',
            f'# Written by tools/update/newbuild.py from {old["name"]}. Format: ClientBuilds.cs.',
            f'# rva: {nrva}/{len(rva_lines)} re-anchored; offsets: {counts["carried"]} carried, {counts["likely"]} '
            f'likely, {counts["changed"]} changed, {counts["blind"]} blind (CHECK lines).',
            f'name\t{name}', f'linux\t{nl.cv}', f'windows\t{nw.cv}', f'stamp\t{nw.stamp:#010x}', '',
            '# offset\tclass\tfield\tlinux\twindows\t# EngineLayout row']
    text = '\n'.join(head + off_lines + ['', '# rva\tlinux symbol\twindows rva\t# SymbolMap constant'] + rva_lines
                     + ['', '# lva\tstatic function\tlinux nm address\t# SymbolMap.LinuxAnchored constant'] + lva_lines) + '\n'

    print(f'{old["name"]} -> {name}: linux {nl.cv}, windows {nw.cv}, stamp {nw.stamp:#010x}')
    print(f'  rva     {nrva}/{len(rva_lines)} re-anchored')
    print(f'  lva     {sum(1 for l in lva_lines if l.startswith("lva"))}/{len(lva_lines)} re-anchored')
    print(f'  offsets {counts["carried"]} carried, {counts["likely"]} likely (displacement survives), '
          f'{counts["changed"]} changed, {counts["blind"]} blind')
    print(f'  symbols {len(gone_l)} linux gone, {len(gone_w)} windows gone; '
          f'{len(changed_hooks)} named functions changed body (read them: behaviour may have moved)')
    if changed_hooks:
        print('    ' + ', '.join(changed_hooks))
    print(f'\nWORKLIST ({len(todo)}):' if todo else '\nWORKLIST: empty -- build, run-demo, live-boot both OSes')
    for t in todo:
        print(f'  - {t}')
    if a.write or a.out:
        out = a.out or os.path.join(FW, 'Builds', f'{name}.tsv')
        open(out, 'w').write(text)
        print(f'\nwrote {os.path.relpath(out, ROOT)}')
    else:
        print('\n(dry run; --write to emit the build file)')


if __name__ == '__main__':
    main()
