#!/usr/bin/env bash
# Alloy is Vulkan-only, so GL survives in a named set of files and nowhere else. Outside that set a `Gl.` member
# may only be a SCREAMING_CASE token -- the enum values the engine's own draw, blend, sampler and texture-format
# data is expressed in, which have to be read whatever renders -- and never an entry point, whose every name in
# managed/Graphics/Gl.cs is PascalCase. A new GL call site outside the set fails rather than being counted.
#
# The set, and why each file is in it:
#   the GL bindings and the UI windows CX draws on the vanilla renderer, plus the one stand-down stub pair
#   the engine's own model views, which have no Alloy twin and run on the engine's context
#   the engine texture path, whose GL arm is the stood-down session's (gated on GlContextService.Available)
#   the uniform shim and its callers: a `Gl.Uniform*` there lands in a Vulkan block, ratcheted by uniform-blocks.sh
cd "$(dirname "$0")/../.."
python3 - <<'EOF' || exit 1
import re, subprocess, sys

ALLOW = {
    "managed/Graphics/Gl.cs", "managed/Graphics/GlProgram.cs", "managed/Graphics/GlContextService.cs",
    "managed/Graphics/RenderTarget.cs", "managed/Graphics/ProgramBinary.cs", "managed/Graphics/GlProbe.cs",
    "managed/Graphics/GlTexture.cs", "managed/Graphics/GpuGraveyard.cs",
    "managed/Graphics/NullGl.cs", "managed/Graphics/NullGl.Stubs.cs",
    "managed/Ui/TextRig.Replay.cs", "managed/Ui/TextRig.cs", "managed/Ui/UiHostService.cs",
    "managed/Ui/KnobPanelService.cs", "managed/Alloy/Pass/TextAtlas.cs",
    "managed/Services/Nui/NuiModelEngine.cs", "managed/Services/Nui/NuiModelEngine.Solve.cs",
    "managed/Services/Nui/NuiModelView.Gl.cs", "managed/Services/Perf/DrawStatsService.cs",
    "managed/Alloy/DdsReader.Formats.cs", "managed/Alloy/Db/TexRegistry.Load.cs",
    "managed/Alloy/OwnedTextures.cs", "managed/Alloy/OwnedTextures.Upload.cs",
    "managed/Graphics/Vulkan/VkUniformShim.cs",
    "managed/Alloy/AlloyRenderer.Uniforms.cs", "managed/Alloy/Programs.cs",
    "managed/Alloy/Pass/GrassField.cs", "managed/Alloy/Pass/FogVolume.cs", "managed/Alloy/Pass/SceneFog.cs",
    "managed/Alloy/Pass/StageEnv.cs", "managed/Alloy/Pass/GuiContent.cs",
    "managed/Alloy/Pass/Weather.Surface.cs", "managed/Alloy/Pass/Weather.Map.cs",
    "managed/Alloy/Pass/Weather.Shelter.cs", "managed/Alloy/Pass/Weather.Esm.cs",
}
REF = re.compile(r"\bGl\.([A-Za-z_][A-Za-z0-9_]*)")
files = subprocess.run(["git", "ls-files", "--cached", "--others", "--exclude-standard", "managed"],
                       capture_output=True, text=True).stdout.split()
bad, stale = [], sorted(f for f in ALLOW if f not in files)
for f in sorted(files):
    if not f.endswith(".cs") or f in ALLOW:
        continue
    for n, line in enumerate(open(f, encoding="utf-8", errors="replace"), 1):
        for m in REF.finditer(line):
            name = m.group(1)
            if name != name.upper():
                bad.append(f"{f}:{n}: Gl.{name} -- an entry point outside the GL set")
if stale:
    bad += [f"{f}: listed in the GL set but not in the tree -- drop the line" for f in stale]
if bad:
    print("\n".join(bad), file=sys.stderr)
    print("!! gl-confined: GL outside the files that keep it (above). Alloy renders on Vulkan: record the work "
          "on the seam (managed/Rhi/) instead, or, if the file really has to hold GL, say why in "
          "tools/gates/gl-confined.sh's list", file=sys.stderr)
    sys.exit(1)
EOF
