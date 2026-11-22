"""Summarise a Stryker mutation report: per-file counts, then each survivor and each mutant no test covers.
Exit 1 when any mutant survived or went untested, 0 otherwise."""
import json, sys

r = json.load(open(sys.argv[1]))
bad = []
for f, v in sorted(r['files'].items()):
    ms = [m for m in v['mutants'] if m['status'] not in ('Ignored', 'CompileError')]
    if not ms:
        continue
    name = f.split('/')[-1]
    n = lambda s: sum(m['status'] == s for m in ms)
    print(f'{name}: {n("Killed")} killed, {n("Survived")} survived, {n("Timeout")} timed out, {n("NoCoverage")} untested')
    label = {'Survived': 'SURVIVED', 'NoCoverage': 'UNTESTED'}
    bad += [f"{label[m['status']]} {name}:{m['location']['start']['line']} {m['mutatorName']} {m.get('replacement', '')}".rstrip()
            for m in ms if m['status'] in label]
if bad:
    print('\n'.join(bad))
sys.exit(1 if bad else 0)
