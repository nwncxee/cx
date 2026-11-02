#!/usr/bin/env python3
"""regcount.py [program ...]: the NVIDIA driver's REAL register count for Alloy's compute programs, offline.

An OpenGL program binary carries only NV assembly (its TEMP count is a weak proxy), so this compiles the same
spliced source check-shaders.py links -- ablation rungs included -- to SPIR-V (glslang's relaxed mode, each
binding kind shifted into its own range so they cannot collide), builds a Vulkan compute pipeline with a
descriptor layout reflected off the module, and prints VK_KHR_pipeline_executable_properties' statistics.
Vulkan codegen is a proxy for GL's -- the same backend compiler, not the same front end -- so compare
programs against each other, not against a GL number. Occupancy at a 16x16 group on a 64k-register SM:
64 registers = 4 groups, 80 = 3, 128 = 2.

No argument = every compute program. Names are check-shaders.py's, e.g. "deferred lights",
"cx_deferred_cs (ablate 5)". Needs glslangValidator, spirv-dis, a C compiler and the Vulkan loader + headers.
ponytail: compute only -- a fragment stage needs a render pass and a vertex stage; add it when one is measured.
"""
import contextlib, importlib.util, io, os, re, subprocess, sys, tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
HERE = os.path.dirname(os.path.abspath(__file__))
# not-an-offset: VkDescriptorType numbers
COMBINED, SAMPLED, STORAGE_IMAGE, UBO, SSBO = 1, 2, 3, 6, 7


def programs():
    os.chdir(ROOT)
    spec = importlib.util.spec_from_file_location("cs", os.path.join(ROOT, "check-shaders.py"))
    cs = importlib.util.module_from_spec(spec)
    with contextlib.redirect_stdout(io.StringIO()):
        spec.loader.exec_module(cs)
        progs = cs.programs(cs.spliced(cs.shaders()))
    out = {}
    for name, stages in progs.items():
        if len(stages) != 1:
            continue
        src = stages[0]
        for tok, val in getattr(cs, "DRIVER_TOKENS", cs.TOKENS).items():
            src = src.replace(tok, val)
        out[name] = src
    return out


def reflect(spv):
    """'binding:type' for every descriptor the module declares."""
    d = subprocess.run(["spirv-dis", "--raw-id", spv], capture_output=True, text=True, check=True).stdout
    binding, block, types, var = {}, set(), {}, {}
    for l in d.splitlines():
        if m := re.match(r'\s*OpDecorate (%\d+) Binding (\d+)', l): binding[m[1]] = int(m[2])
        elif m := re.match(r'\s*OpDecorate (%\d+) (Block|BufferBlock)', l): block.add((m[1], m[2]))
        elif m := re.match(r'\s*(%\d+) = (OpType\w+)(.*)', l): types[m[1]] = (m[2], m[3].split())
        elif m := re.match(r'\s*(%\d+) = OpVariable (%\d+) (\w+)', l): var[m[1]] = (m[2], m[3])
    out = []
    for v, b in binding.items():
        ptr, sc = var[v]
        t = types[ptr][1][1]
        while types[t][0] in ("OpTypeArray", "OpTypeRuntimeArray"):
            t = types[t][1][0]
        k = types[t][0]
        if k == "OpTypeSampledImage": vt = COMBINED
        elif k == "OpTypeImage": vt = STORAGE_IMAGE if types[t][1][5] == "2" else SAMPLED
        elif sc == "StorageBuffer" or (t, "BufferBlock") in block: vt = SSBO
        else: vt = UBO
        out.append(f"{b}:{vt}")
    return out


def main():
    progs = programs()
    want = sys.argv[1:] or list(progs)
    tool = os.path.join(tempfile.gettempdir(), "cx-vkregs")
    src = os.path.join(HERE, "vkregs.c")
    if not os.path.exists(tool) or os.path.getmtime(tool) < os.path.getmtime(src):
        subprocess.run(["cc", "-O1", "-o", tool, src, "-lvulkan"], check=True)
    bad = 0
    with tempfile.TemporaryDirectory() as tmp:
        for name in want:
            if name not in progs:
                print(f"{name}: no such compute program (check-shaders.py names them)"); bad = 1; continue
            comp, spv = os.path.join(tmp, "p.comp"), os.path.join(tmp, "p.spv")
            open(comp, "w").write(progs[name])
            r = subprocess.run(["glslangValidator", "-V", "-R", "--amb", "--aml", "--sib", "100", "--sbb", "300",
                                "--sub", "200", "-o", spv, comp], capture_output=True, text=True)
            if r.returncode:
                print(f"{name}: glslang refused it\n{r.stdout}"); bad = 1; continue
            r = subprocess.run([tool, spv] + reflect(spv), capture_output=True, text=True)
            stats = dict(re.findall(r'\s+([^=]+?)=(\S+)', r.stdout.split(":", 1)[-1]))
            if r.returncode or not stats:
                print(f"{name}: the driver gave no statistics\n{r.stderr}"); bad = 1; continue
            print(f"{name}: {stats.get('Register Count', '?')} registers, shared {stats.get('Shared Memory Size', '?')} B, "
                  f"binary {stats.get('Binary Size', '?')} B")
    return bad


if __name__ == "__main__":
    sys.exit(main())
