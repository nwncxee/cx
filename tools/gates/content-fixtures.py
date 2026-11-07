#!/usr/bin/env python3
"""A cx_fix* stage the C# self-checks translate must be one tools/content_stage.py holds too, and the reverse: the two
translations answer to one set of hand-written stages. Text differences are not read: C# and Python spell strings apart."""
import re
import sys

names = lambda path: set(re.findall(r'"(cx_fix\w*)"\]?\s*[=:]', open(path, encoding="utf-8").read()))
cs = names("managed/Alloy/Core/ContentStage.SelfCheck.cs") | names("managed/Alloy/Core/ContentStageApprovedExamples.cs")
py = names("tools/content_stage.py")
for n in sorted(cs ^ py):
    print(f"!! content-fixtures: {n} is only in {'the C# self-checks' if n in cs else 'tools/content_stage.py'}")
sys.exit(1 if cs ^ py else 0)
