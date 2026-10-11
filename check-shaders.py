#!/usr/bin/env python3
"""Compile the owned pipeline's embedded GLSL with glslangValidator, offline.

    ./check-shaders.py      # exit 0 = every owned program compiles

The runtime failure is quiet: a lit stage that does not compile only says "clustered lit program
unavailable -- lit parts revert to engine drawing" in the log and the world keeps rendering, vanilla.
This turns that into a build-time error.

Composition is not written down here. Every stage is a `managed/Alloy/Shaders/*.glsl` file whose
`#include` lines name the libraries it is built from (#707), and this script compiles those same
files -- the ones the extender embeds -- so a stage checked here is the stage that ships, by
construction. Only program *linkage* -- which stages link together, including the depth-only ones
that have no resref -- lives below. `ShaderResource.Tokens`' sizing tokens are expanded with
stand-in values, since only the shapes matter here.

Why a second copy of a composition is never tolerable: a `CxDraw` record layout that lives in C#
was, until ADR 0012, also hand-copied into shipped `cx_*.shd`. When the record grew in #120 the
stale copies read every draw out of a neighbour's slot and the world went black. Nothing ships a copy
now, and nothing here restates one either.

The audit at the end is the other half of that: every `ShaderSource.Get("x.glsl")` in C# names a
file that exists, and every file is reached by something. A typo'd name is otherwise a stage that
composes to nothing, on a machine that reaches that feature.

Needs glslangValidator (Arch: `glslang`); the material pass also needs spirv-opt/spirv-dis
(Arch: `spirv-tools`), and skips itself with a notice where they are absent.
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.abspath(__file__))
SHADERS = os.path.join(ROOT, "managed/Alloy/Shaders")
MANAGED = os.path.join(ROOT, "managed")
# ShaderResource.Tokens' sizing tokens. Any legal value compiles the same shapes.
TOKENS = {"$CAP$": "128", "$SORT$": "32", "$DYNSPH$": "4", "$CUBES$": "64", "$GROUP$": "8", "$SRGB$": "2.2", "$BAND$": "45.0, 0.0667", "$AA$": "3",
          # #352: the hosted post chain built with sharpen + vibrance on (the two hosted option bits), high quality
          "$POSTMASK$": "9", "$SQM$": "2"}
INCLUDE = re.compile(r'^\s*#include\s+"([^"]+\.glsl)"\s*$')
GET = re.compile(r'ShaderSource\.Get\("([^"]+)"\)')
RESREF = re.compile(r'"(cx_\w+)"')


def expand_includes(name, depth=0):
    """Mirrors ShaderSource.Get: an #include line naming an embedded .glsl is spliced, anything else --
    the engine's own `#include "inc_scriptable"`, a line inside a comment block -- stands."""
    out = []
    lines = open(os.path.join(SHADERS, name)).read().split("\n")
    for i, line in enumerate(lines):
        m = INCLUDE.match(line)
        if m and depth < 8 and os.path.exists(os.path.join(SHADERS, m.group(1))):
            body = expand_includes(m.group(1), depth + 1)
            out.append(body if body.endswith("\n") else body + "\n")
        else:
            out.append(line + ("\n" if i < len(lines) - 1 else ""))
    return "".join(out)


def shaders():
    """Every embedded stage and library, keyed by file name without the extension, includes spliced."""
    out = {}
    for f in sorted(os.listdir(SHADERS)):
        if f.endswith(".glsl"):
            out[f[:-5]] = expand_includes(f)
    if not out:
        sys.exit(f"{SHADERS}: no .glsl files -- did the shaders move?")
    return out


def cs_sources():
    for base, _, files in os.walk(MANAGED):
        for f in sorted(files):
            if f.endswith(".cs"):
                yield os.path.join(base, f)


def audit(g):
    """Every name C# asks ShaderSource for exists, and every file is reached by something."""
    named, resrefs = set(), set()
    for p in cs_sources():
        text = open(p).read()
        named.update(GET.findall(text))
        resrefs.update(RESREF.findall(text))
    included = {m.group(1) for f in g
                for m in (INCLUDE.match(l) for l in open(os.path.join(SHADERS, f + ".glsl")))
                if m}
    files = {f + ".glsl" for f in g}
    bad = 0
    for n in sorted(named - files):
        print(f"audit: C# asks ShaderSource for {n}, which no file answers"); bad = 1
    # A stage file is named by resref, through ShaderStages.Source -- never by a literal.
    for f in sorted(files - named - included):
        if f[:-5] in resrefs:
            continue
        print(f"audit: {f} is not included, named from C# or an overridable resref -- dead text"); bad = 1
    print(f"audit: {len(files)} embedded .glsl, {len(named)} named from C#, {len(included)} included"
          + (" -- ok" if not bad else ""))
    return bad


# #346, the vanilla bridge: a stage can carry `// $VANILLA <resref>` … `// $VANILLA-END`, and at runtime
# the engine's own copy of that file replaces the region. The fallback inside it is a STUB -- the feature
# off -- so compiling that proves only that the world still renders. What is worth checking offline is the
# real content file against Alloy's environment, which is why this splices from a content checkout when it
# can find one. Set CX_CONTENT_SHD to point elsewhere; missing is not an error, it is the stub's case.
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "tools"))
import paths  # noqa: E402
CONTENT_SHD = os.environ.get("CX_CONTENT_SHD") or paths.get("CONTENT_SHADERS") or ""
# #352: the ENGINE's own includes a content file reaches (inc_random, inc_framebuffer, inc_postpr_debug),
# which the runtime resolves off the resource chain and a content checkout never carries. An extracted
# ovr.bif tree; missing means those includes drop, as an absent chain file would.
STOCK_SHD = os.environ.get("CX_STOCK_SHD", os.path.expanduser("~/nwnexplorer/shaders"))
VANILLA = re.compile(r"^([ \t]*)// \$VANILLA(-HOST)? (\w+)\n(.*?)^[ \t]*// \$VANILLA-END[ \t]*$",
                     re.S | re.M)
# Mirrors VanillaShader.Skip: includes the stage's own environment stands in for, never followed.
SKIP = re.compile(r"^[ \t]*// \$VANILLA-SKIP (.*)$", re.M)


# Mirrors VanillaShader.Supplied: the engine's uniform/varying interface, which the renderer's own
# environment block stands in for and which must never be pulled into an Alloy stage.
SUPPLIED = {"inc_common", "inc_uniforms"}
# ...and VanillaShader.Substituted: the one include a HOSTED stage gets a stand-in for (#350). The text is
# the embedded stand-in itself, so the two cannot drift.
SUBSTITUTED = {"inc_standard": "cx_vanstd_f"}


def find_shd(name):
    for base in (CONTENT_SHD, STOCK_SHD):
        if not os.path.isdir(base):
            continue
        for root, _, files in os.walk(base):
            if name + ".shd" in files:
                return os.path.join(root, name + ".shd")
    return None


def expand(text, done, g, depth=0):
    """A content file's own #includes, resolved out of the checkout -- the C# side resolves them through
    the resource chain, and a cx_* hook file is composed OF them. One visit each PER STAGE (`done` is
    shared across a stage's regions, as VanillaShader.Splice shares its list): the files carry #ifndef
    guards, so a second inclusion is already a no-op in GLSL. An include the checkout does not answer for is
    dropped, which is how the ones the renderer's environment supplies (inc_uniforms) stay out."""
    out = []
    for line in text.split("\n"):
        t = line.lstrip()
        if not t.startswith("#include"):
            out.append(line)
            continue
        m = re.search(r'"([^"]+)"', t)
        if not m or m.group(1) in done or m.group(1) in SUPPLIED or depth >= 4:
            continue
        done.add(m.group(1))
        if m.group(1) in SUBSTITUTED:
            out += expand(g[SUBSTITUTED[m.group(1)]], done, g, depth + 1)
            continue
        hit = find_shd(m.group(1))
        if hit:
            out += expand(open(hit).read(), done, g, depth + 1)
    return out


MAIN_FN = re.compile(r"\bvoid\s+main\s*\(\s*(?:void)?\s*\)")
# Mirrors VanillaShader.StubFn: a function DEFINITION in a stub, anchored on its opening brace.
STUB_FN = re.compile(r"^\s*(?:\w+\s+)+(\w+)\s*\([^)]*\)\s*\{", re.M)
OWN_DEFINE = re.compile(r"^[ \t]*#[ \t]*define[ \t]+(\w+)", re.M)


def host(stock, resref, done, g):
    """Mirrors VanillaShader.HostText (#350): a content FRAGMENT STAGE rewritten to run as a function of the
    lit program. main() renamed, gl_FragColor redirected into FragmentColor, the stage's OWN #defines scoped
    to it (undefined before and after, then the environment's macros re-issued), and its declaration of a
    name the environment supplies dropped. The wrapper reads the MATERIAL_*_VALUE terms back."""
    text = "\n".join(expand(stock, done, g))
    if not MAIN_FN.search(text):
        return None
    text = MAIN_FN.sub(f"void CxHostMain_{resref}()", text, count=1)
    text = re.sub(r"\bgl_FragColor\b", "FragmentColor", text)
    env = OWN_DEFINE.findall(g["cx_vanenv_defs"])
    text = re.sub(r"^[ \t]*uniform\b[^;\n]*\b(?:" + "|".join(env) + r")[ \t]*;[^\n]*\n?", "", text, flags=re.M)
    own = list(dict.fromkeys(OWN_DEFINE.findall(stock)))
    undef = "".join(f"#undef {n}\n" for n in own)
    return (undef + text + "\n"
            + f"void CxHost_{resref}()\n{{\n    CxHostMain_{resref}();\n"
            + "    fSpecularity = MATERIAL_SPECULARITY_VALUE;\n    fRoughness = MATERIAL_ROUGHNESS_VALUE;\n"
            + "    fMetallicness = MATERIAL_METALLICNESS_VALUE;\n    SpecularColor = MATERIAL_SPECULAR_COLOR_VALUE;\n}\n"
            + undef + g["cx_vanenv_defs"])


def splice(src, seen, g):
    """Replace each marked region with the content repo's copy of that .shd, where one exists."""
    done = set()
    for m in SKIP.finditer(src):
        done.update(m.group(1).split())
    def one(m):
        indent, hosted, resref, stub = m.group(1), m.group(2), m.group(3), m.group(4)
        hit = find_shd(resref)
        if hit is None:
            seen.setdefault(resref, None)
            return m.group(0)
        seen[resref] = hit
        if resref in done:      # an earlier region already included it: its functions exist
            return indent + f"// $VANILLA{hosted or ''} {resref}"
        done.add(resref)
        stock = open(hit).read()
        body = host(stock, resref, done, g) if hosted else "\n".join(expand(stock, done, g))
        # Mirrors VanillaShader.Validate: every function the stub defines must appear in the spliced text,
        # or the runtime compiles the stub for this region -- so the check does the same rather than
        # accepting text the runtime would reject.
        missing = [n for n in STUB_FN.findall(stub) if not re.search(r"\b" + n + r"\s*\(", body or "")]
        if body is None or missing:
            print(f"vanilla bridge: {resref}.shd " + ("has no main() to host" if body is None
                  else f"does not define {', '.join(missing)}") + " -- the stub stands")
            return m.group(0)
        return (indent + f"// $VANILLA{hosted or ''} {resref}\n"
                + "\n".join(indent + l if l.strip() else "" for l in body.split("\n")))
    return VANILLA.sub(one, src) if os.path.isdir(CONTENT_SHD) else src


def spliced(g):
    """Every stage with its vanilla-bridge regions replaced from a content checkout, where one exists."""
    seen = {}
    out = {k: splice(v, seen, g) for k, v in g.items()}
    for resref, path in sorted(seen.items()):
        print(f"vanilla bridge: {resref}.shd "
              + (f"spliced from {os.path.relpath(path, os.path.expanduser('~'))}" if path
                 else f"NOT FOUND under {CONTENT_SHD} -- compiled the stub, which checks nothing"))
    return out


# --- #122 material synthesis -------------------------------------------------
# The stock generators, transcribed from inc_material.shd's SetupSpecularity in the OLD generation
# (data/base_shaders.bif) -- the one the installed client loads. This is the REFERENCE side of the
# comparison: it is written from the engine's shader, not from ours, so a case where the two agree
# says the owned synthesis matches stock, and a case where they deliberately differ says so in place.
SPEC_MIN, ROUGH_MAX, ROUGH_MIN = 0.04, 0.6, 0.125          # fSpecularityMin, fGeneratedRoughness{Max,Min}
ENV_SPEC_MOD, ENV_SPEC_MAX, ENV_ROUGH_MOD = 8.0, 0.98, 3.0  # fLegacyEnvMap{Specularity,Roughness}*
SLOT_SPEC, SLOT_ROUGH, SLOT_HEIGHT, SLOT_ENV = 2, 4, 8, 64   # env moved up past #123's flow-map bit


def stock_pbr(declared, slots, env_level, spec_texel, rough_texel):
    """inc_material.shd SetupSpecularity: (fSpecularity, fRoughness, fMetallicness)."""
    mix = lambda a, b, t: a * (1.0 - t) + b * t
    sm = bool(slots & SLOT_SPEC)
    spec = declared[0]
    if spec <= 0.0:
        if sm:
            spec = spec_texel
        elif env_level > 0.0:
            spec = mix(SPEC_MIN, ENV_SPEC_MAX, min(env_level * ENV_SPEC_MOD, 1.0))
        else:
            spec = SPEC_MIN
    rough = declared[1]
    if rough <= 0.0:
        if slots & SLOT_ROUGH:
            rough = rough_texel          # a bound roughness map is taken whole -- no env curve on top
        else:
            if sm:
                rough = mix(ROUGH_MIN, ROUGH_MAX, (1.0 - spec) ** 2)
                if env_level > 0.0:
                    rough = min(rough, mix(ROUGH_MAX, ROUGH_MIN, min(env_level * ENV_ROUGH_MOD, 1.0)))
            elif env_level > 0.0:
                rough = mix(ROUGH_MAX, ROUGH_MIN, min(env_level * ENV_ROUGH_MOD, 1.0))
            else:
                rough = ROUGH_MAX
    metal = declared[2]
    if metal <= 0.0:
        # fMetallicnessModifier * fSpecularity - fMetallicnessModifier * fGeneratedMetallicnessOffset
        metal = min(max((3.0 * spec - 0.6) if sm else (10.0 * spec - 0.4), 0.0), 1.0)
    return spec, rough, metal


def stock_spec_color(base, metal, slots):
    """The generated (not declared) branch of SetupSpecularity's specular colour, GAMMA_CORRECTION == 1."""
    mix = lambda a, b, t: a * (1.0 - t) + b * t
    if slots & SLOT_HEIGHT:
        return mix(1.0, base, metal)
    return mix(min(1.0, base / 0.2176), base, metal)     # ApplyColorSpace(0.5) at gamma 2.2


# (label, GLSL expression yielding one float, expected value). Every expression must fold to a
# constant -- no texture fetch, no uniform.
def material_cases():
    def pbr(declared, slots, env, st=0.0, rt=0.0):
        return f"CxSynthPbr(vec3({declared[0]}, {declared[1]}, {declared[2]}), {slots}, {env}, {st}, {rt})"

    cases = []
    for label, declared, slots, env, st, rt in [
        ("bare material (no maps, no env)", (0.0, 0.0, 0.0), 0, 0.0, 0.0, 0.0),
        ("declared beats the floor", (0.7, 0.3, 0.5), 0, 0.0, 0.0, 0.0),
        ("specular map, nothing declared", (0.0, 0.0, 0.0), SLOT_SPEC, 0.0, 0.25, 0.0),
        ("declared beats a specular map", (0.7, 0.0, 0.0), SLOT_SPEC, 0.0, 0.25, 0.0),
        ("roughness map beats the env curve", (0.0, 0.0, 0.0), SLOT_ROUGH | SLOT_ENV, 0.5, 0.0, 0.9),
        ("declared beats a roughness map", (0.0, 0.42, 0.0), SLOT_ROUGH, 0.0, 0.0, 0.9),
        ("legacy env-mapped, no maps", (0.0, 0.0, 0.0), SLOT_ENV, 0.4, 0.0, 0.0),
        ("legacy env-mapped, faintly shiny", (0.0, 0.0, 0.0), SLOT_ENV, 0.05, 0.0, 0.0),
        ("env + specular map", (0.0, 0.0, 0.0), SLOT_ENV | SLOT_SPEC, 0.3, 0.6, 0.0),
        ("declared metallicness beats both curves", (0.0, 0.0, 0.8), SLOT_SPEC, 0.0, 0.9, 0.0),
    ]:
        want = stock_pbr(declared, slots, env, st, rt)
        for k, comp in enumerate("xyz"):
            cases.append((f"{label} [{'spec rough metal'.split()[k]}]",
                          f"{pbr(declared, slots, env, st, rt)}.{comp}", want[k]))
    for label, base, metal, slots in [
        ("generated specular colour, dark albedo", 0.1, 0.0, 0),
        ("generated specular colour, metal", 0.4, 0.75, 0),
        ("height-mapped takes white as its base", 0.1, 0.25, SLOT_HEIGHT),
    ]:
        cases.append((label,
                      f"CxSpecColor(vec4(0.0), vec3({base}), {metal}, {slots}).r",
                      stock_spec_color(base, metal, slots)))
    # ...and the declared colour wins outright: 3.0 only if all three channels come back as
    # CustomSpecularColor itself. Compared per channel because a vec3 `==` lowers to OpAll, which is the one
    # step spirv-opt will not fold.
    got = "CxSpecColor(vec4(0.3, 0.6, 0.9, 1.0), vec3(0.1), 0.0, 0)"
    want = "CxLinear(vec3(0.3, 0.6, 0.9))"
    cases.append(("declared CustomSpecularColor wins",
                  " + ".join(f"float({got}.{ch} == {want}.{ch})" for ch in "rgb"), 3.0))
    return cases + physical_cases()


# --- ADR 0026 the glTF view ----------------------------------------------------
# glTF's metal/rough rules, transcribed independently of cx_material.glsl: the factor multiplies the map,
# a factor alone stands, a map alone is taken whole; no metal at all is a dielectric; no roughness at all
# keeps the legacy value; perceptual roughness squares into the engine's alpha, floored at 0.02.
def gltf_pbr(m0, orm_on, orm, legacy, base):
    metal = min(max((m0[0] if m0[0] >= 0 else (1.0 if orm_on else 0.0)) * (orm[2] if orm_on else 1.0), 0.0), 1.0)
    rough = legacy[1]
    if m0[1] >= 0 or orm_on:
        p = min(max((m0[1] if m0[1] >= 0 else 1.0) * (orm[1] if orm_on else 1.0), 0.0), 1.0)
        rough = max(p * p, 0.02)
    f0 = 0.04 + (base - 0.04) * metal
    return (f0, rough, metal)


def env_brdf(nov, perceptual):
    """Karis' published fit, (A, B)."""
    c0, c1 = (-1.0, -0.0275, -0.572, 0.022), (1.0, 0.0425, 1.04, -0.04)
    r = [perceptual * a + b for a, b in zip(c0, c1)]
    a004 = min(r[0] * r[0], 2 ** (-9.28 * nov)) * r[0] + r[1]
    return (-1.04 * a004 + r[2], 1.04 * a004 + r[3])


def physical_cases():
    cases = []
    legacy = (0.1, 0.6, 0.2)
    for label, m0, orm_on, orm, base in [
        ("gltf: declared metal + roughness", (1.0, 0.5, 0.0, 0.0), 0.0, (1.0, 1.0, 1.0), 0.8),
        ("gltf: roughness alone is a dielectric", (-1.0, 0.3, 0.0, 0.0), 0.0, (1.0, 1.0, 1.0), 0.8),
        ("gltf: metal alone keeps the legacy roughness", (0.7, -1.0, 0.0, 0.0), 0.0, (1.0, 1.0, 1.0), 0.5),
        ("gltf: ORM map taken whole", (-1.0, -1.0, 0.0, 0.0), 1.0, (1.0, 0.5, 1.0), 0.5),
        ("gltf: factors multiply the ORM map", (0.5, 0.8, 0.0, 0.0), 1.0, (1.0, 0.5, 0.6), 0.5),
        ("gltf: a mirror is floored", (0.0, 0.0, 0.0, 0.0), 0.0, (1.0, 1.0, 1.0), 0.5),
    ]:
        want = gltf_pbr(m0, orm_on, orm, legacy, base)
        call = (f"CxPhysicalPbr(vec4{m0}, {orm_on}, vec3{orm}, vec3{legacy}, vec3({base}), cxScratch)")
        for k, comp in enumerate("xyz"):
            cases.append((f"{label} [{'spec rough metal'.split()[k]}]", f"{call}.{comp}", want[k]))
    # F0's hue lives in the specular colour: a grey dielectric's is white
    cases.append(("gltf: dielectric specular colour is white",
                  "CxPhysicalColour(vec4(-1.0, 0.3, 0.0, 0.0), 0.0, vec3(1.0), vec3(0.1, 0.6, 0.2), vec3(0.8, 0.2, 0.1)).g", 1.0))
    cases.append(("gltf: nothing declared is not physical",
                  "float(CxPhysical(vec4(-1.0, -1.0, 0.0, -1.0), 0.0))", 0.0))
    cases.append(("gltf: an ORM map alone is physical",
                  "float(CxPhysical(vec4(-1.0, -1.0, 0.0, -1.0), 1.0))", 1.0))
    for nov, pr in [(1.0, 0.0), (0.5, 0.3), (0.1, 0.8)]:
        a, b = env_brdf(nov, pr)
        cases.append((f"env brdf fit A (NoV {nov}, rough {pr})", f"CxEnvBrdf({nov}, {pr}).x", a))
        cases.append((f"env brdf fit B (NoV {nov}, rough {pr})", f"CxEnvBrdf({nov}, {pr}).y", b))
        cases.append((f"multiscatter (F0 1, NoV {nov}, rough {pr})", f"CxMultiScatter(vec3(1.0), {nov}, {pr}).x",
                      1.0 + (1.0 / max(a + b, 0.05) - 1.0)))
    # a dielectric-free lobe gains nothing, and a fully open, head-on view is never specular-occluded
    cases.append(("multiscatter is 1 at F0 0", "CxMultiScatter(vec3(0.0), 0.5, 0.7).x", 1.0))
    # per channel: a coloured F0 gains in the channel it reflects and not in the one it does not
    a, b = env_brdf(0.5, 0.3)
    cases.append(("multiscatter is per channel (R of a red F0)", "CxMultiScatter(vec3(1.0, 0.0, 0.0), 0.5, 0.3).r",
                  1.0 + (1.0 / max(a + b, 0.05) - 1.0)))
    cases.append(("multiscatter is per channel (G of a red F0)", "CxMultiScatter(vec3(1.0, 0.0, 0.0), 0.5, 0.3).g", 1.0))
    cases.append(("specular occlusion is 1 unoccluded", "CxSpecOcclusion(1.0, 1.0, 0.3)", 1.0))
    return cases


# CxPhysicalPbr returns its colour through an out parameter; these give a fold an lvalue and a way to read it.
FOLD_HELPERS = ("vec3 cxScratch;\n"
                "vec3 CxPhysicalColour(vec4 m0, float o, vec3 orm, vec3 l, vec3 b)\n"
                "{ vec3 c; CxPhysicalPbr(m0, o, orm, l, b, c); return c; }\n")


def fold(prelude, expr, tmp):
    """Compile a fragment stage whose only output is `expr` and read the constant it folds to."""
    src = (prelude + "\n" + FOLD_HELPERS + "layout(location = 0) out vec4 cxProbe;\n"
           "void main() { cxProbe = vec4(" + expr + "); }\n")
    frag, spv = os.path.join(tmp, "fold.frag"), os.path.join(tmp, "fold.spv")
    open(frag, "w").write(src)
    for cmd in (["glslangValidator", "-G", frag, "-o", spv], ["spirv-opt", "-O", spv, "-o", spv],
                ["spirv-dis", spv]):
        r = subprocess.run(cmd, capture_output=True, text=True)
        if r.returncode:
            return None, (r.stdout + r.stderr)
    dis = r.stdout
    consts = dict(re.findall(r"(%\S+) = OpConstant %float ([-\d.e+]+)", dis))
    comp = dict(re.findall(r"(%\S+) = OpConstantComposite %v4float (%\S+)", dis))
    m = re.search(r"OpStore %cxProbe (%\S+)", dis)
    if not m:
        return None, "no store to the probe output -- did the expression get optimised away?"
    v = m.group(1)
    if v in comp:
        v = comp[v]
    if v not in consts:
        return None, f"{v} did not fold to a constant (a uniform or texture fetch leaked into the expression)"
    return float(consts[v]), None


def check_material(g):
    """#122: the synthesis chain folded off-device and compared against the stock generators."""
    missing = [t for t in ("glslangValidator", "spirv-opt", "spirv-dis") if shutil.which(t) is None]
    if missing:
        print(f"material synthesis: SKIPPED -- no {', '.join(missing)} (Arch: spirv-tools)")
        return 0
    # The real lit stage minus its main, so the folds see whatever C# actually splices into the chain.
    # The cut is the stage's own `void main()` -- wherever in the #include chain it comes from -- because
    # every declaration the folds need is above it and the whole body below it belongs to that one function.
    lit = g["cx_ownlit_fs"]
    m = MAIN_FN.search(lit)
    if m is None:
        sys.exit("cx_ownlit_fs has no void main() -- the material prelude broke")
    prelude = lit[: m.start()]
    for tok, val in TOKENS.items():
        prelude = prelude.replace(tok, val)
    prelude = re.sub(r"(layout\(location = 0\) )?out vec4 frag;", "", prelude)   # our own probe output replaces it
    # SPIR-V wants an explicit location on every user in/out; the GL path leaves them implicit.
    loc = [0]
    def locate(m):
        # An ARRAY uniform consumes one location per element column, so it gets a generous stride; a scalar
        # or vector consumes one, and a mat4 four. Striding everything by 64 ran past the implementation
        # limit once the lit stage started carrying the engine's own uniform names too (#237).
        stride = 64 if "[" in m.group(0) else 4
        loc[0] += stride
        return f"layout(location = {loc[0] - stride}) {m.group(0)}"
    prelude = re.sub(r"^(?:flat )?in \w+ \w+;", locate, prelude, flags=re.M)
    # ...and on every non-opaque uniform. Samplers are opaque and must NOT get one.
    prelude = re.sub(r"^uniform (?!\w*sampler)\w+ \w+(\[[^\]]*\])?;", locate, prelude, flags=re.M)
    bad = 0
    with tempfile.TemporaryDirectory() as tmp:
        for label, expr, want in material_cases():
            got, err = fold(prelude, expr, tmp)
            if err is not None:
                print(f"material: {label}: FAILED -- {err.strip().splitlines()[0]}")
                bad = 1
            elif abs(got - want) > 1e-5:
                print(f"material: {label}: FAILED -- got {got:.6f}, stock says {want:.6f}")
                bad = 1
    print(f"material synthesis: {'ok' if not bad else 'FAILED'} "
          f"({len(material_cases())} cases against inc_material.shd)")
    return bad


# Program linkage: which stages link together, by file name -- an overridable stage is named by its
# resref, the depth-only ones (no resref) by the class and constant they were reached by. #339 took the
# tessellation out with the custom water shader: water is a plain vertex/fragment pair again, like every
# other class here.
def define(src, text):
    """CX.Alloy.ShaderStages.WithDefine, character for character: the #define goes immediately after the
    #version line, whatever that line says. A stage with no version line is a stage this would splice
    nothing into and link as the shipped program, which would report a variant as checked when it was
    not -- so it raises instead."""
    v = src.index("#version")
    e = src.index("\n", v) + 1
    return src[:e] + f"#define {text}\n" + src[e:]


def ablate(src, n):
    """The ablation variants are built only by an `/alloy bench` step, so this is the one place a rung that
    stopped compiling is caught before someone runs the bench and loses that rung of the ladder."""
    return define(src, f"CX_ABLATE {n}")


def mot(src):
    """The velocity arm of a stage (CX.Alloy.GrassField.MotionDefine): what turns a grass path's or the
    particle pass's stages into the program that writes the field instead of the picture."""
    return define(src, "CX_MOTION 1")


def nosun(src):
    """CX.Alloy.Programs.WithoutSunSoft: the one define that compiles the sun's blocker search and disc out.
    A driver short of the two raw cascade views links THIS text, so the no-soft-sun arm has to compile and
    link too -- it is the fallback that keeps a lit program at all on such a driver."""
    return src.replace("#define CX_SUN_SOFT 1", "// CX-49: the soft sun filter is OFF -- too few fragment texture units for its raw cascade views")


# CX-188: each ablated pass's ladder length -- the rungs CX.Alloy.Ablation can be asked for, which is what
# AlloyRenderer.Bench's own step list walks. Rung 0 is the shipped program, a program in its own right below.
ABLATIONS = {"cx_deferred_cs": 12, "cx_defshadow_cs": 2, "cx_pairdebt": 2}


def programs(g):
    return {
        **{f"{stage} (ablate {n})": (ablate(g[stage], n),)
           for stage, rungs in ABLATIONS.items() for n in range(1, rungs + 1)},
        "fullbright": (g["cx_own_vs"], g["cx_own_fs"]),
        "depth prepass": (g["cx_depth_v"], g["cx_depth_f"]),
        "keyhole depth prepass": (g["cx_depthkey_v"], g["cx_depthkey_f"]),
        "caster depth": (g["cx_cast_vs"], g["cx_cast_fs"]),
        "caster depth (alpha-tested)": (g["cx_castmask_v"], g["cx_castmask_f"]),
        "clustered lit": (g["cx_ownlit_vs"], g["cx_ownlit_fs"]),
        # CX-49: the arm Programs.WithoutSunSoft builds where the driver cannot seat the sun's raw cascade
        # views -- plain and deferred, because the twins are spliced from whichever text the lit build used
        "clustered lit (no soft sun)": (g["cx_ownlit_vs"], nosun(g["cx_ownlit_fs"])),
        "clustered lit (no soft sun, deferred twin)": (g["cx_ownlit_vs"], nosun(g["cx_ownlit_fs"]).replace("#version 460 core\n", "#version 460 core\n#define CX_DEFERRED 1\n#define CX_GBUF_OUT 1\n", 1)),
        "clustered lit (OIT lists)": (g["cx_ownlit_vs"], g["cx_ownlit_fs"].replace("#version 460 core\n", "#version 460 core\n#define CX_OIT_LISTS 1\n", 1)),
        # CX-188: the deferred twin -- the same two defines Programs.WithDeferred injects, in the same place
        "clustered lit (deferred twin)": (g["cx_ownlit_vs"], g["cx_ownlit_fs"].replace("#version 460 core\n", "#version 460 core\n#define CX_DEFERRED 1\n#define CX_GBUF_OUT 1\n", 1)),
        # CX-188: /alloy bench's blended ladder -- Ablation.LitDefines, in the same place
        **{f"clustered lit (blended ablate {n}{tw})": (g["cx_ownlit_vs"], g["cx_ownlit_fs"].replace("#version 460 core\n", f"#version 460 core\n{lx}{d}", 1))
           for tw, lx in (("", ""), (", OIT lists", "#define CX_OIT_LISTS 1\n"))
           for n, d in ((3, "#define CX_BLEND_FLAT 1\n"), (4, "#define CX_NO_HOT 1\n#define CX_ABLATE 2\n"),
                        (5, "#define CX_NO_HOT 1\n#define CX_ABLATE 4\n"), (6, "#define CX_NO_HOT 1\n"))},
        # CX-189: the coverage layer's mask variant -- Programs.EnsureMaskProgram's define, in the same place
        "clustered lit (A2C mask)": (g["cx_ownlit_vs"], g["cx_ownlit_fs"].replace("#version 460 core\n", "#version 460 core\n#define CX_A2C_MASK 1\n", 1)),
        "coverage layer seed": (g["cx_fullscreen_v"], g["cx_a2cseed_f"]),
        "coverage layer depth": (g["cx_fullscreen_v"], g["cx_a2cdepth_f"]),
        "post bloom downsample": (g["cx_post_vs"], g["cx_bloomdn_fs"]),
        "post bloom upsample": (g["cx_post_vs"], g["cx_bloomup_fs"]),
        "post resolve": (g["cx_post_vs"], g["cx_resolve_fs"]),
        "post stack gamma": (g["cx_post_vs"], g["cx_postgam_fs"]),
        "post stack toon": (g["cx_post_vs"], g["cx_toon_fs"]),
        "post stack sharpen": (g["cx_post_vs"], g["cx_sharpen_fs"]),
        "post stack vibrance": (g["cx_post_vs"], g["cx_vibrance_fs"]),
        "post stack fbpost (hosted content chain)": (g["cx_post_vs"], g["cx_fbpost_fs"]),
        "post stack aa edges": (g["cx_post_vs"], g["cx_smaaedge_fs"]),
        "post stack aa weights": (g["cx_post_vs"], g["cx_smaawgt_fs"]),
        "post stack aa blend": (g["cx_post_vs"], g["cx_smaablend_fs"]),
        "ssao visibility": (g["cx_post_vs"], g["cx_ssao_fs"]),
        "ssao blur": (g["cx_post_vs"], g["cx_ssaoblur_fs"]),
        "halos": (g["cx_halo_vs"], g["cx_halo_fs"]),
        "weather rain": (g["cx_weather_vs"], g["cx_rain_fs"]),
        "weather snow": (g["cx_weather_vs"], g["cx_snow_fs"]),
        "weather splash": (g["cx_splash_vs"], g["cx_splash_fs"]),
        "ssao temporal": (g["cx_fullscreen_v"], g["cx_ssaotemp_f"]),
        "motion vectors": (g["cx_fullscreen_v"], g["cx_motioncam_f"]),
        "mover motion": (g["cx_motion_v"], g["cx_motion_f"]),
        "taa resolve": (g["cx_fullscreen_v"], g["cx_taa_f"]),
        "post stack taa": (g["cx_post_vs"], g["cx_taa_fs"]),
        "highlight composite": (g["cx_fullscreen_v"], g["cx_hilitecomp_f"]),
        "owned water": (g["cx_water_vs"], g["cx_water_fs"]),
        "instanced grass": (g["cx_grass_vs"], g["cx_grass_fs"]),
        "grass depth prepass": (g["cx_grass_vs"], g["cx_grassdepth_f"]),
        "grass scatter": (g["cx_grass_c"],),
        "grass blades": (g["cx_gblade_vs"], g["cx_gblade_fs"]),
        "grass blade depth": (g["cx_gblade_vs"], g["cx_gblade_df"]),
        # CX-48: the velocity arm of each grass path -- GrassField.Motion's define, in the same place
        "grass motion vectors": (mot(g["cx_grass_vs"]), mot(g["cx_grassdepth_f"])),
        "grass blade motion vectors": (mot(g["cx_gblade_vs"]), mot(g["cx_gblade_df"])),
        "particle motion vectors": (mot(g["cx_part_v"]), mot(g["cx_part_f"])),
        "clustered light build": (g["cx_cluster_cs"],),
        "deferred lights": (g["cx_deferred_cs"],),
        # CX-188: the film's own dispatch (DeferredLights._progFilm), and the "every light" escapes' variant
        # (DeferredLights.SelectEscape): the walk's in-loop hardware tap
        "deferred lights (film pass)": (g["cx_deferred_cs"].replace("#version 460 core\n", "#version 460 core\n#define CX_FILM_PASS 1\n", 1),),
        "deferred lights (in-loop taps)": (g["cx_deferred_cs"].replace("#version 460 core\n", "#version 460 core\n#define CX_SH_INLOOP 1\n", 1),),
        "deferred shadow record": (g["cx_defshadow_cs"],),
        "pair debt": (g["cx_pairdebt"],),
        "gui": (g["cx_gui_v"], g["cx_gui_f"]),
        "trigger highlight": (g["cx_fullscreen_v"], g["cx_hilitetrig_f"]),
        "highlight glow": (g["cx_hiliteglow_v"], g["cx_hiliteglow_f"]),
        "halo occlusion probe": (g["cx_haloocc_c"],),
        "oit resolve": (g["cx_fullscreen_v"], g["cx_oitresolve_f"]),
        "oit clear": (g["cx_fullscreen_v"], g["cx_oitclear_f"]),
        "light beams": (g["cx_beam_v"], g["cx_beam_f"]),
        "light beams (scene database)": (g["cx_beamdb_v"], g["cx_beam_f"]),
        "lightmap bake": (g["cx_bake_v"], g["cx_bake_f"]),
        "lightmap encoder": (g["cx_bakeenc_c"],),
        "weather map scatter": (g["cx_wx_scat_c"],),
        "weather map integrate": (g["cx_wx_int_c"],),
        "weather shelter cache": (g["cx_wx_shel_c"],),
        "weather noise tile": (g["cx_wx_noise_c"],),
        "weather ripple tile": (g["cx_wx_ripl_c"],),
    }


# Stage count -> the file extensions glslangValidator infers each stage from.
EXTS = {1: ("comp",), 2: ("vert", "frag"), 4: ("vert", "tesc", "tese", "frag")}


SIDE_EFFECTS = re.compile(r"\b(atomic\w*|imageStore|imageAtomic\w*|gl_FragDepth)\b|\blayout\([^)]*\)\s*(?!readonly)(?:coherent\s+|volatile\s+|restrict\s+)*buffer\b")


def check_early_z(progs):
    """A fragment-stage side effect (SSBO/image store or atomic, gl_FragDepth) costs the program its early-Z,
    so it may reach ONLY the OIT-lists twin: every other stage an opaque draw uses must preprocess clean.
    (The fullbright stage takes the block at RUNTIME through OitPass.WithLists -- a deliberate exception.)
    CX-188's deferred twin is checked here too: it draws the OPAQUE and CUTOUT classes, so early-Z is the
    whole point of it, and its four G-buffer outputs are plain fragment outputs, not side effects."""
    with tempfile.TemporaryDirectory() as tmp:
        def effects(name):
            src = progs[name][1]
            for tok, val in TOKENS.items():
                src = src.replace(tok, val)
            p = os.path.join(tmp, "ez.frag"); open(p, "w").write(src)
            r = subprocess.run(["glslangValidator", "-E", p], capture_output=True, text=True)
            return sorted({m.group(0).split("(")[0].strip() for m in SIDE_EFFECTS.finditer(r.stdout)})
        bad = 0
        for name, want in (("clustered lit", False), ("clustered lit (OIT lists)", True),
                           ("clustered lit (deferred twin)", False), ("clustered lit (A2C mask)", False),
                           ("owned water", False), ("grass blades", False)):
            got = effects(name)
            ok = bool(got) == want
            print(f"early-z: {name}: {'ok' if ok else 'FAILED'} ({'side effects: ' + ', '.join(got) if got else 'no side effects'})")
            bad |= not ok
        return bad


# The shipped sizes, for the driver link below: NVIDIA's linker range-checks constant indices the inliner
# produces, so the shapes glslang accepts at any size can still be refused at these.
DRIVER_TOKENS = dict(TOKENS, **{"$DYNSPH$": "16", "$GROUP$": "64"})
NV_EGL = "/usr/share/glvnd/egl_vendor.d/10_nvidia.json"


_NV = {}


def nv_context():
    """A current GL 4.6 context on the NVIDIA driver through a headless EGL device, made once; returns its
    proc lookup `fn(name, restype, *argtypes)`, or None after printing why there is none."""
    if "fn" in _NV:
        return _NV["fn"]
    _NV["fn"] = None
    if not os.path.exists(NV_EGL):
        print("driver: skipped (no NVIDIA EGL vendor)")
        return None
    os.environ["__EGL_VENDOR_LIBRARY_FILENAMES"] = NV_EGL
    import ctypes as C
    try:
        egl = C.CDLL("libEGL.so.1")
    except OSError:
        print("driver: skipped (no libEGL)")
        return None
    egl.eglGetProcAddress.restype = C.c_void_p
    egl.eglGetProcAddress.argtypes = [C.c_char_p]
    def fn(name, res, *args):
        p = egl.eglGetProcAddress(name.encode())
        return C.CFUNCTYPE(res, *args)(p) if p else None
    qd = fn("eglQueryDevicesEXT", C.c_uint, C.c_int, C.POINTER(C.c_void_p), C.POINTER(C.c_int))
    gpd = fn("eglGetPlatformDisplayEXT", C.c_void_p, C.c_uint, C.c_void_p, C.c_void_p)
    devs = (C.c_void_p * 8)(); n = C.c_int(0)
    if not qd or not gpd or not qd(8, devs, C.byref(n)) or n.value == 0:
        print("driver: skipped (no EGL device)")
        return None
    egl.eglInitialize.argtypes = [C.c_void_p, C.c_void_p, C.c_void_p]
    egl.eglCreateContext.restype = C.c_void_p
    egl.eglCreateContext.argtypes = [C.c_void_p, C.c_void_p, C.c_void_p, C.POINTER(C.c_int)]
    egl.eglMakeCurrent.argtypes = [C.c_void_p] * 4
    dpy = gpd(0x313F, devs[0], None)   # not-an-offset: EGL_PLATFORM_DEVICE_EXT
    if not dpy or not egl.eglInitialize(dpy, None, None):
        print("driver: skipped (EGL device did not initialise)")
        return None
    egl.eglBindAPI(0x30A2)   # not-an-offset: EGL_OPENGL_API
    attrs = (C.c_int * 5)(0x3098, 4, 0x30FB, 6, 0x3038)   # not-an-offset: MAJOR 4, MINOR 6, EGL_NONE
    ctx = egl.eglCreateContext(dpy, None, None, attrs)
    if not ctx or not egl.eglMakeCurrent(dpy, None, None, ctx):
        print("driver: skipped (no 4.6 context on the EGL device)")
        return None
    _NV["fn"] = fn
    return fn


def check_driver_link(progs, g):
    """Link every program on the NVIDIA driver through a headless EGL device context, where one exists.
    glslang compiled the lit stage clean while the driver refused it at LINK ("array index out of bounds" on
    a path the inliner proved constant) and the whole world drew black -- this is the gate that sees that."""
    fn = nv_context()
    if fn is None:
        return 0
    import ctypes as C
    u, i, p_ = C.c_uint, C.c_int, C.c_char_p
    create = fn("glCreateShader", u, u)
    source = fn("glShaderSource", None, u, i, C.POINTER(p_), C.c_void_p)
    compile_ = fn("glCompileShader", None, u)
    program = fn("glCreateProgram", u)
    attach = fn("glAttachShader", None, u, u)
    link = fn("glLinkProgram", None, u)
    status = fn("glGetProgramiv", None, u, u, C.POINTER(i))
    infolog = fn("glGetProgramInfoLog", None, u, i, C.c_void_p, C.c_char_p)
    kinds = {"vert": 0x8B31, "geom": 0x8DD9, "frag": 0x8B30, "comp": 0x91B9,   # not-an-offset: GL stage enums
             "tesc": 0x8E88, "tese": 0x8E87}   # not-an-offset: GL stage enums
    bad = 0
    for name, stages in progs.items():
        exts = EXTS[len(stages)]
        if name == "lightmap bake":   # glslang links it without its geometry stage; the driver needs it
            stages, exts = (stages[0], g["cx_bake_g"], stages[1]), ("vert", "geom", "frag")
        prog = program()
        for src, ext in zip(stages, exts):
            for tok, val in DRIVER_TOKENS.items():
                src = src.replace(tok, val)
            s = create(kinds[ext]); buf = p_(src.encode())
            source(s, 1, C.byref(buf), None); compile_(s); attach(prog, s)
        link(prog)
        ok = i(0); status(prog, 0x8B82, C.byref(ok))   # not-an-offset: GL_LINK_STATUS
        if not ok.value:
            log = C.create_string_buffer(8192); infolog(prog, 8192, None, log)
            errs = [l for l in log.value.decode(errors="replace").splitlines() if "error" in l]
            print(f"driver link: {name}: FAILED\n  " + "\n  ".join(errs[:8]))
            bad = 1
    print(f"driver link: {'ok' if not bad else 'FAILED'} ({len(progs)} programs on the NVIDIA driver)")
    return bad


LM_N = 64   # the encoder fixture's side, texels


def lm_fixture():
    """A small bake's scratch planes and what the finish makes of each texel, written out here from the
    finish's rule (covered = as baked, else the nearest covered texel within two, else none) -- the encoder
    is never asked. Two lights over a hard shadow edge, with a hole whose middle the dilation cannot reach."""
    import struct
    half = lambda x: struct.unpack("e", struct.pack("e", x))[0]
    cov = lambda x, y: 6 <= x < 58 and 6 <= y < 58 and not (27 <= x < 39 and 20 <= y < 44)
    scr = {}
    for y in range(LM_N):
        for x in range(LM_N):
            d1 = ((x - 16) ** 2 + (y - 16) ** 2) / 600.0   # half strength 24 texels out: 3 m at the default 8 px/m
            d2 = ((x - 48) ** 2 + (y - 40) ** 2) / 600.0
            shade = 0.15 if x + y < 40 else 1.0
            irr = [shade * (3.0 * a / (1 + d1) + 2.0 * b / (1 + d2)) for a, b in zip((1.0, 0.6, 0.3), (0.2, 0.4, 1.0))]
            lum = 0.2126 * irr[0] + 0.7152 * irr[1] + 0.0722 * irr[2]
            v = ((x - 32) / 40.0, (y - 32) / 50.0, 1.0)
            share = (x / 63.0, y / 63.0, 0.5, 1.0 - x / 63.0)
            on = cov(x, y)
            scr[x, y] = ([half(c) for c in irr] + [2.0 if on else 0.0],
                         [half(c * lum * 0.7) for c in v] + [0.0],
                         [half(c * lum) for c in share])
    want = {}
    for (x, y), (a, b, c) in scr.items():
        if a[3] <= 0.0:
            best = 1e9
            for dy in range(-2, 3):
                for dx in range(-2, 3):
                    q = (x + dx, y + dy)
                    if (dx, dy) == (0, 0) or q not in scr or scr[q][0][3] <= 0.0 or dx * dx + dy * dy >= best:
                        continue
                    best = dx * dx + dy * dy
                    a, b, c = scr[q]
            if best > 1e8:
                continue
        l = sum(k * k for k in b[:3]) ** 0.5
        d = [k / l for k in b[:3]] if l > 1e-6 else [0.0, 0.0, 1.0]
        lum = max(0.2126 * a[0] + 0.7152 * a[1] + 0.0722 * a[2], 1e-6)
        want[x, y] = ([max(k, 0.0) for k in a[:3]], [k * 0.5 + 0.5 for k in d], [min(max(k / lum, 0.0), 1.0) for k in c])
    return scr, want


def check_lmenc(g):
    """The lightmap encoder against the driver's own BC6H/BC7 decoder: the fixture is encoded, the blocks are
    copied into real BPTC textures and read back decoded. The bounds are the format's, measured: one or two
    lines a block cannot follow two coloured lights crossing (RG11B10F's rounding was 0.64% mean, 1.5% max)
    nor a 6x shadow step past mode 1's delta range; directions and mover shares hold to a few 8-bit steps."""
    fn = nv_context()
    if fn is None:
        return 0
    import ctypes as C
    u, i, vp = C.c_uint, C.c_int, C.c_void_p
    gl = lambda name, *a: fn(name, None, *a)
    create_tex = gl("glCreateTextures", u, i, C.POINTER(u))
    storage2 = gl("glTextureStorage2D", u, i, u, i, i)
    storage3 = gl("glTextureStorage3D", u, i, u, i, i, i)
    sub2 = gl("glTextureSubImage2D", u, i, i, i, i, i, u, u, vp)
    clear = gl("glClearTexImage", u, i, u, u, vp)
    unit = gl("glBindTextureUnit", u, u)
    image = gl("glBindImageTexture", u, u, i, C.c_ubyte, i, u, u)
    copy = gl("glCopyImageSubData", u, u, i, i, i, i, u, u, i, i, i, i, i, i, i)
    getsub = gl("glGetTextureSubImage", u, i, i, i, i, i, i, i, u, u, i, vp)
    errors = fn("glGetError", u)
    use = gl("glUseProgram", u)
    loc = fn("glGetUniformLocation", i, u, C.c_char_p)
    uni4 = gl("glProgramUniform4i", u, i, i, i, i, i)
    dispatch = gl("glDispatchCompute", u, u, u)
    barrier = gl("glMemoryBarrier", u)
    finish = gl("glFinish")
    shader, src_, comp, prog_, attach, link = (fn("glCreateShader", u, u), gl("glShaderSource", u, i, C.POINTER(C.c_char_p), vp),
                                               gl("glCompileShader", u), fn("glCreateProgram", u), gl("glAttachShader", u, u), gl("glLinkProgram", u))
    status = gl("glGetProgramiv", u, u, C.POINTER(i))
    TEX2D, TEX2DA, RGBA, FLOAT, UBYTE, UINT = 0x0DE1, 0x8C1A, 0x1908, 0x1406, 0x1401, 0x1405   # not-an-offset: GL enums
    RGBA16F, RGBA32UI, RGBA8, RGBA_INT, BC6, BC7 = 0x881A, 0x8D70, 0x8058, 0x8D99, 0x8E8F, 0x8E8C   # not-an-offset: GL enums
    N, B = LM_N, LM_N // 4

    def tex(target):
        t = u(0); create_tex(target, 1, C.byref(t)); return t.value
    scr, want = lm_fixture()
    planes = []
    for k in range(3):
        t = tex(TEX2D); storage2(t, 1, RGBA16F, N, N)
        buf = (C.c_float * (N * N * 4))(*[v for y in range(N) for x in range(N) for v in scr[x, y][k]])
        sub2(t, 0, 0, 0, N, N, RGBA, FLOAT, buf)
        unit(k, t); planes.append(t)
    blk = []
    for k in range(2):
        t = tex(TEX2D); storage2(t, 1, RGBA32UI, B, B)
        clear(t, 0, RGBA_INT, UINT, (C.c_uint * 4)()); blk.append(t)
    page_c = tex(TEX2DA); storage3(page_c, 1, RGBA8, N, N, 2)
    clear(page_c, 0, RGBA, UBYTE, (C.c_ubyte * 4)(85, 85, 85, 85))
    s = shader(0x91B9); buf = C.c_char_p(g["cx_bakeenc_c"].encode())   # not-an-offset: GL_COMPUTE_SHADER
    src_(s, 1, C.byref(buf), None); comp(s)
    prog = prog_(); attach(prog, s); link(prog)
    ok = i(0); status(prog, 0x8B82, C.byref(ok))   # not-an-offset: GL_LINK_STATUS
    if not ok.value:
        print("lightmap encoder: FAILED (did not link on the driver)")
        return 1
    use(prog)
    uni4(prog, loc(prog, b"uRegion"), 1, 1, B - 2, B - 2)
    image(0, blk[0], 0, 0, 0, 0x88B9, RGBA32UI)   # not-an-offset: GL_WRITE_ONLY
    image(1, blk[1], 0, 0, 0, 0x88B9, RGBA32UI)   # not-an-offset: GL_WRITE_ONLY
    image(2, page_c, 0, 0, 1, 0x88B9, RGBA8)   # not-an-offset: GL_WRITE_ONLY; layer 1
    dispatch((B - 2 + 7) // 8, (B - 2 + 7) // 8, 1)
    barrier(0xFFFFFFFF)   # not-an-offset: GL_ALL_BARRIER_BITS
    got = []
    for k, fmt in ((0, BC6), (1, BC7)):
        t = tex(TEX2DA); storage3(t, 1, fmt, N, N, 2)
        copy(blk[k], TEX2D, 0, 0, 0, 0, t, TEX2DA, 0, 0, 0, 1, B, B, 1)
        out = (C.c_float * (N * N * 4))()
        getsub(t, 0, 0, 0, 1, N, N, 1, RGBA, FLOAT, C.sizeof(out), out); got.append(out)
    out_c = (C.c_ubyte * (N * N * 4))()
    getsub(page_c, 0, 0, 0, 1, N, N, 1, RGBA, UBYTE, C.sizeof(out_c), out_c)
    finish()
    if errors():
        print("lightmap encoder: FAILED (a GL error in the fixture)")
        return 1
    px = lambda arr, x, y: arr[(y * N + x) * 4:(y * N + x) * 4 + 4]
    region = lambda x, y: 4 <= x < N - 4 and 4 <= y < N - 4
    edge = lambda x, y: (x & ~3) + (y & ~3) < 40 <= (x | 3) + (y | 3)
    a_smooth = a_all = a_sum = b_err = c_err = 0.0
    a_n = 0
    c_stray = zero_bad = 0
    for y in range(N):
        for x in range(N):
            w = want.get((x, y)) if region(x, y) else None
            blk_valid = region(x, y) and any((bx, by) in want for bx in range(x & ~3, (x & ~3) + 4) for by in range(y & ~3, (y & ~3) + 4))
            if w is None:
                if px(out_c, x, y) != [85, 85, 85, 85]:
                    c_stray += 1
                if not blk_valid and any(px(got[0], x, y)[:3]):
                    zero_bad += 1
                continue
            rel = max(abs(d - e) / max(e, 1e-3) for d, e in zip(px(got[0], x, y)[:3], w[0]))
            a_all = max(a_all, rel); a_sum += rel; a_n += 1
            if not edge(x, y):
                a_smooth = max(a_smooth, rel)
            b_err = max(b_err, max(abs(d - e) * 255 for d, e in zip(px(got[1], x, y)[:3], w[1])))
            c_err = max(c_err, max(abs(d / 255.0 - e) * 255 for d, e in zip(px(out_c, x, y), w[2])))
    a_mean = a_sum / max(a_n, 1)
    bad = a_mean > 0.015 or a_smooth > 0.04 or a_all > 0.10 or b_err > 6.0 or c_err > 1.01 or c_stray or zero_bad
    print(f"lightmap encoder: {'FAILED' if bad else 'ok'} (irradiance {a_mean * 100:.2f}% mean, {a_smooth * 100:.2f}% max off the shadow edge, {a_all * 100:.2f}% on it,"
          f" direction {b_err:.1f} and shares {c_err:.2f} 8-bit steps, {c_stray} share texels written past the finish,"
          f" {zero_bad} texels of empty blocks not black)")
    return int(bad)


SSBO_BUDGET = int(re.search(r"const uint Budget = (\d+);", open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "managed/Alloy/Core/SsboSlots.cs")).read()).group(1))
SSBO_DECL = re.compile(r"layout\(std430,\s*binding\s*=\s*(\d+)\)\s*(?:\w+\s+)*?buffer\s+(\w+)")


def check_ssbo(progs):
    """Across a program's stages (SsboSlots.Check sees one stage at a time): every SSBO point under the budget,
    and no point claimed by two different blocks -- a vertex and a fragment stage that disagree link cleanly
    and read one buffer as the other."""
    bad = 0
    for name, stages in progs.items():
        claimed = {}
        for src in stages:
            for at, block in SSBO_DECL.findall(src):
                at = int(at)
                if at >= SSBO_BUDGET:
                    print(f"ssbo: {name}: {block} on point {at}, past the budget of {SSBO_BUDGET}")
                    bad = 1
                elif claimed.setdefault(at, block) != block:
                    print(f"ssbo: {name}: {block} and {claimed[at]} both claim point {at}")
                    bad = 1
    print(f"ssbo: {'ok' if not bad else 'FAILED'} ({len(progs)} programs under {SSBO_BUDGET} points, one block per point)")
    return bad


def main():
    g = spliced(shaders())
    try:
        progs = programs(g)
    except KeyError as e:
        sys.exit(f"could not find shader file {e} -- did a stage get renamed?")

    bad = 0
    with tempfile.TemporaryDirectory() as tmp:
        for name, stages in progs.items():
            paths = []
            for src, ext in zip(stages, EXTS[len(stages)]):
                for tok, val in TOKENS.items():
                    src = src.replace(tok, val)
                p = os.path.join(tmp, f"{name.replace(' ', '_')}.{ext}")
                open(p, "w").write(src)
                paths.append(p)
            r = subprocess.run(["glslangValidator"] + paths, capture_output=True, text=True)
            print(f"{name}: {'ok' if r.returncode == 0 else 'FAILED'}")
            if r.returncode:
                bad = 1
                print(r.stdout, r.stderr)
    print(f"{len(progs)} programs from {len(g)} embedded .glsl")
    bad |= check_ssbo(progs)
    bad |= check_driver_link(progs, g)
    bad |= check_lmenc(g)
    bad |= check_early_z(progs)
    bad |= check_material(g)
    bad |= audit(g)
    return bad


if __name__ == "__main__":
    sys.exit(main())
