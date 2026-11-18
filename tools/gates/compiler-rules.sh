#!/usr/bin/env bash
# ADR 0031: a compiler rule stays on. CX0001 is never switched off; RS0030 is disabled only by the helper its ban
# names, under managed/Framework/, with the reason on the pragma line; no project, props or editorconfig lowers either rule,
# and every CX rule an analyzer declares has its error line in managed/.editorconfig.
cd "$(dirname "$0")/../.."
rc=0
hits=$(git -c color.grep=never grep --untracked -nIE '#pragma\s+warning\s+disable[^/]*\bCX[0-9]{4}\b|SuppressMessage\([^)]*CX[0-9]{4}' -- managed)
[ -n "$hits" ] && { echo "$hits" >&2; echo "!! compiler-rules: a CX rule switched off in code (above); port the site instead" >&2; rc=1; }
hits=$(git -c color.grep=never grep --untracked -nIE '#pragma\s+warning\s+disable[^/]*\bRS0030\b' -- managed | grep -vE 'RS0030\s*//\s*\S')
[ -n "$hits" ] && { echo "$hits" >&2; echo "!! compiler-rules: an RS0030 pragma without '// <reason>' on its line (above)" >&2; rc=1; }
hits=$(git -c color.grep=never grep --untracked -nIE '#pragma\s+warning\s+disable[^/]*\bRS0030\b' -- managed ':!managed/Framework/')
[ -n "$hits" ] && { echo "$hits" >&2; echo "!! compiler-rules: an RS0030 pragma outside managed/Framework/ (above); a ban's helper is its one legal home" >&2; rc=1; }
hits=$(git -c color.grep=never grep --untracked -nIE 'SuppressMessage\([^)]*RS0030' -- managed)
[ -n "$hits" ] && { echo "$hits" >&2; echo "!! compiler-rules: RS0030 suppressed by attribute (above); use the reasoned pragma in the helper" >&2; rc=1; }
hits=$(git -c color.grep=never grep --untracked -nIE '(NoWarn|WarningsNotAsErrors)[^<]*\b(CX[0-9]{4}|RS0030)\b|dotnet_diagnostic\.(CX[0-9]{4}|RS0030)\.severity\s*=\s*(none|silent|suggestion)' \
  -- '*.csproj' '*.props' '*.targets' '*.editorconfig' '*.globalconfig')
[ -n "$hits" ] && { echo "$hits" >&2; echo "!! compiler-rules: a compiler rule lowered in build config (above)" >&2; rc=1; }
for id in $(grep -ohE 'Id = "CX[0-9]{4}"' tools/analyzers/*.cs | grep -oE 'CX[0-9]{4}' | sort -u); do
  grep -qE "^dotnet_diagnostic\.$id\.severity\s*=\s*error" managed/.editorconfig \
    || { echo "!! compiler-rules: $id has no 'dotnet_diagnostic.$id.severity = error' line in managed/.editorconfig" >&2; rc=1; }
done
exit $rc
