#!/usr/bin/env bash
# An SSBO binding point past the driver's budget fails to compile and costs the whole program (Intel's Windows GL has
# 16 points, which left the whole GUI undrawn). The budget and every slot live in managed/Alloy/Core/SsboSlots.cs, read
# here by that file name. Refused: an std430 binding literal at or past the budget, a slot constant at or past it, and
# a numeric SSBO index handed straight to BindBufferBase/BindBufferRange, which no table or check would ever see -- or
# to a seam BufferBinding (uniform or storage; managed/Rhi/'s own self-check exempt).
cd "$(dirname "$0")/../.."
python3 - <<'EOF' || exit 1
import re, subprocess, sys
slots = open("managed/Alloy/Core/SsboSlots.cs").read()
budget = int(re.search(r"const uint Budget = (\d+);", slots).group(1))
files = subprocess.run(["git", "ls-files", "--cached", "--others", "--exclude-standard", "managed"],
                       capture_output=True, text=True).stdout.split()
bad = []
for f in files:
    if not f.endswith((".cs", ".glsl")): continue
    for n, line in enumerate(open(f, encoding="utf-8", errors="replace"), 1):
        for m in re.finditer(r"std430,\s*binding\s*=\s*(\d+)", line):
            if int(m.group(1)) >= budget: bad.append(f"{f}:{n}: binding {m.group(1)}")
        if re.search(r"BindBuffer(Base|Range)\(Gl\.SHADER_STORAGE_BUFFER,\s*\d", line):
            bad.append(f"{f}:{n}: a literal SSBO index -- name a SsboSlots constant")
        if not f.startswith("managed/Rhi/") and re.search(r"\bBufferBinding\(\s*\d", line):
            bad.append(f"{f}:{n}: a literal seam binding slot -- name a SsboSlots or UboSlots constant")
for grp in re.findall(r"internal const uint ([^;]+);", slots):
    for name, v in re.findall(r"(\w+)\s*=\s*(\d+)", grp):
        if name != "Budget" and int(v) >= budget: bad.append(f"SsboSlots.{name} = {v}")
if bad:
    print("\n".join(bad), file=sys.stderr)
    print(f"!! ssbo-bindings: an SSBO binding point at or past the budget of {budget} (above); "
          "take a slot from managed/Alloy/Core/SsboSlots.cs", file=sys.stderr)
    sys.exit(1)
EOF
