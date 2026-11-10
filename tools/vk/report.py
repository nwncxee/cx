#!/usr/bin/env python3
"""Reads one validation-layer log and says whether the run was clean. Prints a count per message id and the
first few distinct messages; exits non-zero when anything is left after the layer's own notices.

Usage: report.py <log> <label> <expected VK_VALIDATION_FEATURE_ENABLE_* token>
       report.py --ids <log>     # every distinct message id in it, one per line, sorted

Two positive controls, because "no findings" is otherwise indistinguishable from "the layer never ran": the
log has to hold the layer's `Khronos Validation Layer Active` notice (which is why the passes report at info
severity), and its `Current Enables` line has to name the feature the pass is for. A log that fails either is
a FAILURE, not a clean run.

A layer log carries object names straight out of the engine and is not text: `grep -oP` over one silently
prints nothing, which reads exactly like a clean log. That is why --ids is here rather than a grep in the
caller -- one parser for the file, in Python, which reads the bytes it is given."""
import re, sys

HEAD = re.compile(r'Validation (Error|Warning|Performance Warning|Information):\s*\[\s*([^\]]+?)\s*\]')
ACTIVE = 'Khronos Validation Layer Active'
# The layer talking about itself, not about what CX did: it is loaded, core and GPU-AV together are slow, and
# it forced on the device features its own instrumentation needs.
OWN = {'WARNING-CreateInstance-status-message', 'WARNING-CreateDevice-status-message',
       'VALIDATION-SETTINGS', 'WARNING-Setting-Limit-Adjusted'}
SHOW = 20


def blocks(text):
    """(severity, id, first line) per message, in order."""
    out, cur = [], None
    for line in text.splitlines():
        m = HEAD.search(line)
        if m:
            cur = [m.group(1), m.group(2), '']
            out.append(cur)
        elif cur is not None and not cur[2] and line.strip():
            cur[2] = line.strip()
    return out


def read(path):
    return open(path, encoding='utf-8', errors='replace').read()


def ids(path):
    """Every distinct id in the log, the layer's own notices included: a caller comparing two runs wants the
    whole set, so it can tell a new id from one both runs leave."""
    for mid in sorted({mid for _, mid, _ in blocks(read(path))}):
        print(mid)
    return 0


def main(path, label, expect):
    try:
        text = read(path)
    except OSError:
        print(f'>>   FAIL: {label}: no log at {path} -- the layer never loaded, so nothing was validated',
              file=sys.stderr)
        return 1
    if ACTIVE not in text:
        print(f'>>   FAIL: {label}: {path} holds no "{ACTIVE}" notice -- the layer was not in the instance',
              file=sys.stderr)
        return 1
    enables = next((l for l in text.splitlines() if 'Current Enables' in l), '')
    if expect not in enables:
        print(f'>>   FAIL: {label}: the layer enabled "{enables.strip()}", which does not name {expect}',
              file=sys.stderr)
        return 1

    own, findings, order = 0, {}, []
    for sev, mid, first in blocks(text):
        if sev == 'Information' or mid in OWN:
            own += 1
            continue
        if mid not in findings:
            order.append(mid)
        findings.setdefault(mid, []).append(f'{sev}: {first}')

    print(f'>>   {label}: {len(findings)} distinct id(s), '
          f'{sum(len(v) for v in findings.values())} message(s), {own} of the layer\'s own')
    for mid in order[:SHOW]:
        print(f'>>     {len(findings[mid])}x {mid}')
        print(f'>>       {findings[mid][0][:400]}')
    if len(order) > SHOW:
        print(f'>>     ... and {len(order) - SHOW} more distinct')
    return 1 if findings else 0


if len(sys.argv) == 3 and sys.argv[1] == '--ids':
    sys.exit(ids(sys.argv[2]))
if len(sys.argv) != 4:
    print(__doc__, file=sys.stderr)
    sys.exit(2)
sys.exit(main(sys.argv[1], sys.argv[2], sys.argv[3]))
