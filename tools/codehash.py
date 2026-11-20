#!/usr/bin/env python3
"""Prints the code fingerprint of a checkout: what build.sh would compile and stage, less what cannot change it.

Usage: codehash.py [checkout]     (default: the checkout this script lives in)
       codehash.py --strip <file>  (the text a .cs file is hashed as, for its tests)

Every tracked or untracked file the repo's .gitignore files do not ignore counts except Markdown outside third_party/
(whose notices packaging/licenses.sh ships), a submodule counts by its checked-out commit, and a .cs file counts
with its comments removed and its blank lines dropped. Everything inside a C# string literal counts, a `//` line
held in a raw string being shader text rather than a comment, so this lexes C# instead of matching lines. A .glsl
file counts whole. build.sh stamps the fingerprint into cx-linux-x64/BUILT_FROM and keeps a staged build whose
fingerprint matches; tools/where.sh reads it to call that build CURRENT. Linux-only, like build.sh.
"""
import hashlib
import re
import os
import subprocess
import sys


def _string_end(s, i, quotes, verbatim, dollars):
    """Index just past the string literal whose opening quote run (``quotes`` long) starts at ``i``. A raw string
    closes on the same run of quotes; a verbatim one escapes a quote by doubling it, a regular one with a backslash.
    An interpolation hole (``dollars`` braces open one, one brace for a non-raw string) is code, lexed for its
    own strings so that a quote inside it does not close the literal."""
    n = len(s)
    raw = quotes >= 3
    i += quotes
    while i < n:
        c = s[i]
        if raw and s.startswith('"' * quotes, i):
            return i + quotes
        if not raw and c == '"':
            if verbatim and i + 1 < n and s[i + 1] == '"':
                i += 2
                continue
            return i + 1
        if not raw and not verbatim and c == '\\':
            i += 2
            continue
        if not raw and not verbatim and c == '\n':
            return i
        if dollars:
            opener = '{' * max(dollars, 1)
            if s.startswith(opener, i):
                if not raw and s.startswith('{{', i):
                    i += 2
                    continue
                i = _code(s, i + len(opener), None, stop='}')[1]
                continue
        i += 1
    return n


def _code(s, i, out, stop=None, spans=None):
    """Lexes C# code from ``i``, appending what is not a comment to ``out`` (None: discard), and returns
    (out, index). With ``stop`` it returns at that character at brace depth zero, just past it. ``spans`` collects
    the (start, end) of each comment and literal it passes."""
    n = len(s)
    depth = 0
    while i < n:
        c = s[i]
        if stop and c == stop and depth == 0:
            if out is not None:
                out.append(c)
            return out, i + 1
        if c == '{':
            depth += 1
        elif c == '}':
            depth -= 1
        if s.startswith('//', i):
            j = s.find('\n', i)
            j = n if j < 0 else j
            if spans is not None:
                spans.append((i, j))
            i = j
            continue
        if s.startswith('/*', i):
            j = s.find('*/', i + 2)
            j = n if j < 0 else j + 2
            if spans is not None:
                spans.append((i, j))
            if out is not None and 0 < i and j < n and not s[i - 1].isspace() and not s[j].isspace():
                out.append(' ')  # it kept two tokens apart: -/**/-x is not --x
            i = j
            continue
        if c == "'":
            j = i + 1
            while j < n and s[j] != "'" and s[j] != '\n':
                j += 2 if s[j] == '\\' else 1
            j = min(j + 1, n)
            if spans is not None:
                spans.append((i, j))
            if out is not None:
                out.append((s[i:j],))
            i = j
            continue
        # a string literal, with its $ and @ prefixes in either order
        j = i
        dollars = 0
        verbatim = False
        while j < n and s[j] in '$@':
            if s[j] == '$':
                dollars += 1
            else:
                verbatim = True
            j += 1
        if j < n and s[j] == '"':
            q = j
            while q < n and s[q] == '"':
                q += 1
            quotes = q - j
            if verbatim or quotes == 2:  # a verbatim string is never raw, and "" is the empty string
                quotes = 1
            end = _string_end(s, j, quotes, verbatim, dollars)
            if spans is not None:
                spans.append((i, end))
            if out is not None:
                out.append((s[i:end],))
            i = end
            continue
        if out is not None:
            out.append(c)
        i += 1
    return out, i


def spans(text):
    """The (start, end) of every comment and every string or char literal in C# ``text``."""
    found = []
    _code(text, 0, None, spans=found)
    return found


def strip_cs(text):
    """The text a .cs file is hashed as: comments removed and, in code but never inside a literal, each line's
    trailing blanks cut and blank lines dropped."""
    parts = _code(text.replace('\r\n', '\n'), 0, [])[0]
    out, code = [], []
    for p in parts + [('',)]:
        if isinstance(p, str):
            code.append(p)
            continue
        out.append(re.sub(r'\n(?:[ \t]*\n)+', '\n', re.sub(r'[ \t]+\n', '\n', ''.join(code))))
        out.append(p[0])
        code = []
    return ''.join(out).strip('\n')


def files(root):
    # The repo's .gitignore files only: a private exclude (.git/info/exclude, a global one) does not stop MSBuild.
    listed = subprocess.run(['git', '-C', root, 'ls-files', '-z', '--cached', '--others', '--exclude-per-directory=.gitignore'],
                            check=True, capture_output=True).stdout.split(b'\0')
    return sorted({p.decode() for p in listed if p})


def fingerprint(root):
    h = hashlib.sha256()
    for path in files(root):
        if path.endswith('.md') and not path.startswith('third_party/'):
            continue
        full = os.path.join(root, path)
        if os.path.islink(full):
            data = b'link:' + os.readlink(full).encode()
        elif os.path.isdir(full):  # a submodule: its commit, which is what its files are
            head = subprocess.run(['git', '-C', full, 'rev-parse', 'HEAD'], capture_output=True).stdout.strip()
            data = b'submodule:' + head
        elif not os.path.isfile(full):
            continue  # deleted in the tree: listed by git, read by nothing
        else:
            with open(full, 'rb') as f:
                data = f.read()
            if path.endswith('.cs'):
                data = strip_cs(data.decode('utf-8', 'surrogateescape')).encode('utf-8', 'surrogateescape')
        h.update(path.encode() + b'\0' + hashlib.sha256(data).digest())
    return h.hexdigest()


def main(argv):
    if len(argv) == 3 and argv[1] == '--strip':
        with open(argv[2], encoding='utf-8', errors='surrogateescape') as f:
            sys.stdout.write(strip_cs(f.read()) + '\n')
        return 0
    if len(argv) > 2:
        print(__doc__, file=sys.stderr)
        return 2
    root = argv[1] if len(argv) == 2 else os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
    print(fingerprint(os.path.realpath(root)))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
