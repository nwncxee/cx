#!/usr/bin/env bash
# CX-316: every non-opaque uniform lives in a std140 block on an explicit binding point, and the points live in
# managed/Alloy/Core/UboSlots.cs, read here by that file name. A point past the driver's budget fails to compile
# and costs the whole program; two blocks on one point are no error at all -- the reader gets the other block.
# Refused: a std140 binding literal at or past the budget, a slot constant at or past it, and a numeric index
# handed straight to BindBufferBase/BindBufferRange on UNIFORM_BUFFER, which no table or check would ever see.
cd "$(dirname "$0")/../.."
python3 - <<'EOF' || exit 1
import re, subprocess, sys
slots = open("managed/Alloy/Core/UboSlots.cs").read()
budget = int(re.search(r"const uint Budget = (\d+);", slots).group(1))
files = subprocess.run(["git", "ls-files", "--cached", "--others", "--exclude-standard", "managed"],
                       capture_output=True, text=True).stdout.split()
bad = []
for f in files:
    if not f.endswith((".cs", ".glsl")): continue
    for n, line in enumerate(open(f, encoding="utf-8", errors="replace"), 1):
        for m in re.finditer(r"std140,\s*binding\s*=\s*(\d+)", line):
            if int(m.group(1)) >= budget: bad.append(f"{f}:{n}: binding {m.group(1)}")
        if re.search(r"BindBuffer(Base|Range)\(Gl\.UNIFORM_BUFFER,\s*\d", line):
            bad.append(f"{f}:{n}: a literal uniform-block index -- name a UboSlots constant")
for grp in re.findall(r"internal const uint ([^;]+);", slots):
    for name, v in re.findall(r"(\w+)\s*=\s*(\d+)", grp):
        if name != "Budget" and int(v) >= budget: bad.append(f"UboSlots.{name} = {v}")
if bad:
    print("\n".join(bad), file=sys.stderr)
    print(f"!! ubo-bindings: a uniform-block binding point at or past the budget of {budget} (above); "
          "take a slot from managed/Alloy/Core/UboSlots.cs", file=sys.stderr)
    sys.exit(1)
EOF
