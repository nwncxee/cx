#!/usr/bin/env bash
# Regression check, not an approved test: a mutant boot stopped at its bound is booted again before it is called a
# hang, and an unmutated run that fails once is booted again before the runner refuses. Planted boots; no compile.
# Usage: tools/selfcheck/mutants_retry_test.sh [folder holding the mutants.py to test]. Exit 0 PASS, 1 FAIL.
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 2
dir=$(realpath "${1:-tools/selfcheck}")
python3 -I - "$dir" <<'PY'
import contextlib, io, sys
sys.path.insert(0, sys.argv[1])
import mutants as M

fail = []
T = '\n>> TIMEOUT: the run passed its 9s and was stopped\n'


class Boots:
    """Answers each boot with the next planted (exit, output, seconds), and keeps the bound each was given."""
    def __init__(self, *plan):
        self.plan, self.bounds = list(plan), []

    def __call__(self, t=None):
        self.bounds.append(t)
        return self.plan.pop(0)


def said(fn, *a):
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        r = fn(*a)
    return r, out.getvalue(), err.getvalue()


def expect(name, ok):
    print(('ok   ' if ok else 'FAIL ') + name)
    ok or fail.append(name)


class Mut:
    name = 'm'


boots = Boots((124, T, 9.0), (0, '', 20.0))
(rc, _), out, _ = said(M.patient, 'm', boots, 9.0)
expect('a slow boot is booted again with four times its bound', boots.bounds == [9.0, 36.0])
expect('a slow boot that finishes is judged by that boot', rc == 0 and 'slow, not hung' in out)

boots = Boots((124, T, 9.0), (124, T, 36.0))
(rc, output), _, _ = said(M.patient, 'm', boots, 9.0)
_, out, _ = said(M.verdict, Mut, rc, output)
expect('a boot stopped twice is killed by a hang', rc == 124 and 'by a hang' in out)

flood = '\n>> TIMEOUT: the run printed past 64 MB and was stopped\n'
boots = Boots((124, flood, 2.0))
(rc, output), _, _ = said(M.patient, 'm', boots, 9.0)
_, out, _ = said(M.verdict, Mut, rc, output)
expect('a flood is not booted again', len(boots.bounds) == 1)
expect('a flood is killed by its output, not a hang', 'by its output' in out and 'hang' not in out)

flaky = '[cx] DbScene.SelfCheck FAIL: an allocation the check counts\n'
boots = Boots((0, '', 11.0))
(rc, took), out, _ = said(M.steady, 1, flaky, 10.0, boots)
expect('an unmutated run that fails once is booted again and goes on', rc == 0 and took == 11.0)
expect('the first boot\'s FAIL line is named', 'DbScene.SelfCheck FAIL: an allocation the check counts' in out)

boots = Boots((1, flaky, 11.0))
(rc, _), _, err = said(M.steady, 1, flaky, 10.0, boots)
expect('an unmutated run that fails twice is refused', rc != 0)
expect('the refusal names the check that failed', 'DbScene.SelfCheck FAIL' in err)

boots = Boots()
(rc, _), _, _ = said(M.steady, 0, '', 10.0, boots)
expect('an unmutated run that passes boots once', rc == 0 and boots.bounds == [])
sys.exit(1 if fail else 0)
PY
