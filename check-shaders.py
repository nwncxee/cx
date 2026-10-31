#!/usr/bin/env python3
"""Compile the owned pipeline's embedded GLSL to SPIR-V with glslangValidator, offline.

    ./check-shaders.py      # exit 0 = every owned program compiles and every module validates

The runtime failure is quiet: a lit stage that does not compile only says "clustered lit program
unavailable -- lit parts revert to engine drawing" in the log and the world keeps rendering, vanilla.
This turns that into a build-time error.

The job here is text and SPIR-V: every stage parses as GLSL and compiles to a module the Vulkan
environment accepts. What only a device can answer is `./run-demo.sh`'s: a pipeline refused at
creation (`VkBackend.SelfCheck.Stage`), and the goldens that run a stage on the GPU and judge
what it answered -- the lightmap block encoder, the G-buffer's lanes, the parallax march, the shading rate (`GpuGolden`).

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

Needs glslangValidator (Arch: `glslang`); the module validation and the material pass also need
spirv-val and spirv-opt/spirv-dis (Arch: `spirv-tools`), and skip themselves with a notice where
they are absent.
"""
import atexit
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
# A file is reached when C# names it, whether it hands the name straight to ShaderSource.Get or to a helper
# that does (GuiPassVk.Text, say), so reachability reads the quoted file name itself.
NAMED = re.compile(r'"(cx_\w+\.glsl)"')

# --- the SPIR-V compile -------------------------------------------------------------------------------------
# The runtime hands each stage to shaderc for one Vulkan environment, after VkGlsl has rewritten the text; the
# numbers that say which environment and where the bindless array sits are read off that C# so the two cannot
# drift. Offline the same text goes through glslangValidator's Vulkan target: its relaxed rules gather a
# stage's loose uniforms into one block, as VkGlsl's own CxLoose block does, and --amb/--aml stand in for the
# sampler units and varying locations VkGlsl hands shaderc. A module the compiler accepts is then validated.
# What only a device can answer -- a pipeline refused at creation -- is VkBackend.SelfCheck.Stage's job, which
# ./run-demo.sh runs.
VK_TOOLCHAIN_CS = os.path.join(MANAGED, "Graphics/Vulkan/VulkanToolchain.cs")
_VK_SRC = open(VK_TOOLCHAIN_CS).read()
VK_ENV = "vulkan" + re.search(r"TargetEnvironmentVersion\.Vulkan_(\d+)_(\d+)", _VK_SRC).expand(r"\1.\2")
VK_UNIFORM_BASE, VK_STORAGE_BASE, VK_IMAGE_BASE = (
    re.search(r"UniformBase = (\d+), StorageBase = (\d+), ImageBase = (\d+)", _VK_SRC).groups())
VK_BINDLESS_SET = re.search(r"BindlessSet = (\d+)", _VK_SRC).group(1)
VK_BINDLESS_ARRAY = re.search(r'BindlessArray = "(\w+)"',
                              open(os.path.join(MANAGED, "Graphics/Vulkan/VkGlsl.cs")).read()).group(1)
VK_FLAGS = ["-V", "--target-env", VK_ENV, "-R", "--amb", "--aml",
            "--sub", VK_UNIFORM_BASE, "--sbb", VK_STORAGE_BASE, "--sib", VK_IMAGE_BASE]
VK_BINDLESS_EXT = re.compile(r"^[ \t]*#extension\s+GL_ARB_bindless_texture\b[^\n]*\n?", re.M)
VK_CONSTRUCT = re.compile(r"\bsampler2D\s*\(")
VK_BUILTINS = ((r"\bgl_DrawID\b", "gl_DrawIDARB"), (r"\bgl_BaseInstance\b", "gl_BaseInstanceARB"),
               (r"\bgl_BaseVertex\b", "gl_BaseVertexARB"), (r"\bgl_VertexID\b", "gl_VertexIndex"),
               (r"\bgl_InstanceID\b", "(gl_InstanceIndex - gl_BaseInstanceARB)"))


def vk_text(src):
    """Mirrors VkGlsl: every `sampler2D(handle)` construction as an index into the bindless sampler array, the
    extension line that declared handles gone, and the draw and vertex built-ins in Vulkan's spellings."""
    out, at, s = [], 0, VK_BINDLESS_EXT.sub("", src)
    for m in VK_CONSTRUCT.finditer(s):
        if m.start() < at:
            continue
        depth, i = 0, m.end() - 1
        while i < len(s):
            depth += (s[i] == "(") - (s[i] == ")")
            if depth == 0:
                break
            i += 1
        if i >= len(s):
            sys.exit(f"an unclosed sampler2D( construction at character {m.start()}")
        out.append(s[at:m.start()])
        out.append(f"{VK_BINDLESS_ARRAY}[nonuniformEXT(({s[m.end():i]}).x)]")
        at = i + 1
    out.append(s[at:])
    s = "".join(out)
    for pat, rep in VK_BUILTINS:
        s = re.sub(pat, rep, s)
    head = ("#extension GL_EXT_nonuniform_qualifier : require\n"
            "#extension GL_ARB_shader_draw_parameters : require\n")
    if f"{VK_BINDLESS_ARRAY}[" in s:
        head += f"layout(set = {VK_BINDLESS_SET}, binding = 0) uniform sampler2D {VK_BINDLESS_ARRAY}[];\n"
    v = s.index("#version")
    e = s.index("\n", v) + 1
    return s[:e] + head + s[e:]




def expand_includes(name, depth=0, swap=None):
    """Mirrors ShaderSource.Get: an #include line naming an embedded .glsl is spliced, anything else --
    the engine's own `#include "inc_scriptable"`, a line inside a comment block -- stands. `swap` replaces a
    named library's text, as ContentPrograms replaces cx_host_f with a content stage's region."""
    out = []
    lines = open(os.path.join(SHADERS, name)).read().split("\n")
    for i, line in enumerate(lines):
        m = INCLUDE.match(line)
        if m and swap and m.group(1) in swap:
            out.append(swap[m.group(1)])
        elif m and depth < 8 and os.path.exists(os.path.join(SHADERS, m.group(1))):
            body = expand_includes(m.group(1), depth + 1, swap)
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
    named, resrefs, literals = set(), set(), set()
    for p in cs_sources():
        text = open(p).read()
        named.update(GET.findall(text))
        resrefs.update(RESREF.findall(text))
        literals.update(NAMED.findall(text))
    included = {m.group(1) for f in g
                for m in (INCLUDE.match(l) for l in open(os.path.join(SHADERS, f + ".glsl")))
                if m}
    files = {f + ".glsl" for f in g}
    bad = 0
    for n in sorted(named - files):
        print(f"audit: C# asks ShaderSource for {n}, which no file answers"); bad = 1
    # A stage file is named by resref, through ShaderStages.Source -- never by a literal.
    for f in sorted(files - named - literals - included):
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
import content_stage  # noqa: E402


def stock_tree():
    """The game's own .shd as the client resolves them (content_stage.KEYS: ovr.bif's current set over the old
    base_shaders.bif), written to a temporary tree; empty when the install is not found."""
    d = tempfile.mkdtemp(prefix="cx-stock-")
    atexit.register(shutil.rmtree, d, True)
    for name, text in content_stage.stock_shaders(paths.get("NWN_DIR") or "").items():
        with open(os.path.join(d, name + ".shd"), "w", encoding="latin1") as f:
            f.write(text)
    return d


# #352: the ENGINE's own includes a content file reaches (inc_random, inc_framebuffer, inc_postpr_debug),
# which the runtime resolves off the resource chain and a content checkout never carries. Set CX_STOCK_SHD to
# resolve them from another tree; missing files drop those includes, as an absent chain file would.
STOCK_SHD = os.environ.get("CX_STOCK_SHD") or stock_tree()
# group 2 is the WHOLE-STAGE marker (VanillaShader.Main); a resref spelled as a token ($SKYFS$) is not matched
VANILLA = re.compile(r"^([ \t]*)// \$VANILLA(-MAIN)? (\w+)\n(.*?)^[ \t]*// \$VANILLA-END[ \t]*$",
                     re.S | re.M)
# Mirrors VanillaShader.Skip: includes the stage's own environment stands in for, never followed.
SKIP = re.compile(r"^[ \t]*// \$VANILLA-SKIP (.*)$", re.M)


# Mirrors VanillaShader.Supplied: the engine's uniform/varying interface, which the renderer's own
# environment block stands in for and which must never be pulled into an Alloy stage.
SUPPLIED = {"inc_common", "inc_uniforms"}
# ...and VanillaShader.Substituted: the one include a spliced file gets a stand-in for (#350) -- the lit stand-in, or a
# whole stage's. The text is the embedded stand-in itself, so the two cannot drift.
SUBSTITUTED, STANDARD, WHOLE_STANDARD = "inc_standard", "cx_vanstd_f", "cx_vanwhole_f"


def find_shd(name):
    for base in (CONTENT_SHD, STOCK_SHD):
        if not os.path.isdir(base):
            continue
        for root, _, files in os.walk(base):
            if name + ".shd" in files:
                return os.path.join(root, name + ".shd")
    return None


def read_shd(name):
    hit = find_shd(name)
    return open(hit).read() if hit else None


# Mirrors VanillaShader.MaxDepth: an include that many files deep is dropped, not followed.
INCLUDE_DEPTH = int(re.search(r"const int MaxDepth = (\d+)", open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "managed/Alloy/Core/VanillaShader.cs")).read()).group(1))


def expand(text, done, g, depth=0, read=read_shd, standard=STANDARD):
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
        if not m or m.group(1) in done or m.group(1) in SUPPLIED or depth >= INCLUDE_DEPTH:
            continue
        done.add(m.group(1))
        if m.group(1) == SUBSTITUTED:
            out += expand(g[standard], done, g, depth + 1, read, standard)
            continue
        hit = read(m.group(1))
        if hit is not None:
            out += expand(hit, done, g, depth + 1, read, standard)
    return out


def main_text(stock, done, g, env, read):
    """Mirrors VanillaShader.MainText: a stage run WHOLE -- includes resolved against the whole-stage stand-in, main()
    renamed CxVanMain, gl_FragColor redirected, every uniform the environment ahead supplies as a macro dropped, and the
    stage's own top-level defines undefined around it. None when it has no main()."""
    text = "\n".join(expand(stock, done, g, read=read, standard=WHOLE_STANDARD))
    if not MAIN_FN.search(text):
        return None
    text = re.sub(r"\bgl_FragColor\b", "FragmentColor", MAIN_FN.sub("void CxVanMain()", text, 1))
    names = sorted(set(OWN_DEFINE.findall(env)))
    if names:
        text = re.sub(r"(?m)^[ \t]*uniform\b[^;=\n]*\b(?:" + "|".join(names) + r")[ \t]*(?:=[^;\n]*)?;[^\n]*\n?", "", text)
    undef = "".join(f"#undef {n}\n" for n in dict.fromkeys(OWN_DEFINE.findall(stock)))
    return undef + text + "\n" + undef


MAIN_FN = re.compile(r"\bvoid\s+main\s*\(\s*(?:void)?\s*\)")
# Mirrors VanillaShader.StubFn: a function DEFINITION in a stub, anchored on its opening brace.
STUB_FN = re.compile(r"^\s*(?:\w+\s+)+(\w+)\s*\([^)]*\)\s*\{", re.M)
OWN_DEFINE = re.compile(r"^[ \t]*#[ \t]*define[ \t]+(\w+)", re.M)


def splice(src, seen, g, done=None, read=None):
    """Replace each marked region with the content repo's copy of that .shd, where one exists -- or, given `read`, with
    that resource chain's. `done` collects the files spliced, which a later content region of the same program treats
    as already included."""
    done = set() if done is None else done
    for m in SKIP.finditer(src):
        done.update(m.group(1).split())
    def one(m):
        indent, hosted, resref, stub = m.group(1), m.group(2), m.group(3), m.group(4)
        hit = find_shd(resref) if read is None else ("the chain" if read(resref) is not None else None)
        if hit is None:
            seen.setdefault(resref, None)
            return m.group(0)
        seen[resref] = hit
        if resref in done:      # an earlier region already included it: its functions exist
            return indent + f"// $VANILLA{hosted or ''} {resref}"
        done.add(resref)
        stock = open(hit).read() if read is None else read(resref)
        if hosted:
            body = main_text(stock, done, g, src[:m.start()], read or read_shd)
            if body is None:
                print(f"vanilla bridge: {resref}.shd has no main() to run -- the stub stands")
                return m.group(0)
            return indent + f"// $VANILLA{hosted} {resref}\n" + body
        body = "\n".join(expand(stock, done, g, read=read or read_shd))
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
    return VANILLA.sub(one, src) if read is not None or os.path.isdir(CONTENT_SHD) else src


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
# The stock generators, transcribed from the current inc_material.shd's SetupSpecularity (ovr.bif, the set the
# client runs over base_shaders.bif -- content_stage.KEYS), at SHADER_QUALITY_MODE 2. This is the REFERENCE side of the
# comparison: it is written from the engine's shader, not from ours, so a case where the two agree
# says the owned synthesis matches stock, and a case where they deliberately differ says so in place.
SPEC_MIN, ROUGH_MAX, ROUGH_MIN = 0.04, 0.55, 0.125         # fGeneratedSpecularityMin, fGeneratedRoughness{Max,Min}
ENV_SPEC_MOD, ENV_SPEC_MAX, ENV_ROUGH_MOD = 8.0, 0.98, 2.5  # fLegacyEnvMap{Specularity,Roughness}*
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
        # with a specular map: m = fSpecularity^2, then 25m / (1 + 24m); without one, fMetallicnessModifier 10 *
        # fSpecularity - 10 * fGeneratedSpecularityMin
        m = spec * spec
        metal = min(max((25.0 * m / (1.0 + 24.0 * m)) if sm else (10.0 * spec - 0.4), 0.0), 1.0)
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
        ("faint specular map: metal below the old curve's offset", (0.0, 0.0, 0.0), SLOT_SPEC, 0.0, 0.1, 0.0),
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
    # inc_material.shd:312 and inc_lighting.shd:341: the per-light surface fade, fSurfaceFadeModifier from the height
    # occlusion and the factor clamp(dot(vSurfaceNormal, L) * fSurfaceFadeModifier, 0, 1); w 0 is a surface without one
    for ao in (1.0, 0.5, 0.0):
        cases.append((f"surface fade from height occlusion {ao}", f"CxSurfFadeOf({ao})", 1.0 / (1.0 + (0.1 - 1.0) * ao * ao)))
    for label, f, L, want in [
        ("surface fade: open surface, light near the horizon", (0.0, 0.0, 1.0, 10.0), (0.9950372, 0.0, 0.0995037), 0.995037),
        ("surface fade: open surface, light overhead", (0.0, 0.0, 1.0, 10.0), (0.0, 0.0, 1.0), 1.0),
        ("surface fade: crevice, light at 60 degrees", (0.0, 0.0, 1.0, 1.0), (0.8660254, 0.0, 0.5), 0.5),
        ("surface fade: light behind the geometry", (0.0, 0.0, 1.0, 10.0), (0.6, 0.0, -0.8), 0.0),
        ("surface fade: none declared", (0.0, 0.0, 0.0, 0.0), (0.6, 0.0, -0.8), 1.0),
    ]:
        cases.append((label, f"CxSurfFadeAt(vec4{f}, vec3{L})", want))
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
                "{ vec3 c; CxPhysicalPbr(m0, o, orm, l, b, c); return c; }\n"
                "float CxSurfFadeAt(vec4 f, vec3 L) { cxSurfFade = f; return CxSurfFade(L); }\n")


def fold(prelude, expr, tmp):
    """Compile a fragment stage whose only output is `expr` and read the constant it folds to."""
    src = (prelude + "\n" + FOLD_HELPERS + "layout(location = 0) out vec4 cxProbe;\n"
           "void main() { cxProbe = vec4(" + expr + "); }\n")
    frag, spv = os.path.join(tmp, "fold.frag"), os.path.join(tmp, "fold.spv")
    open(frag, "w").write(src)
    for cmd in (["glslangValidator"] + VK_FLAGS + ["-o", spv, frag], ["spirv-opt", "-O", spv, "-o", spv],
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


def nosun(src):
    """CX.Alloy.Programs.WithoutSunSoft: the one define that compiles the sun's blocker search and disc out.
    A device short of the two raw cascade views runs THIS text, so the no-soft-sun arm has to compile too --
    it is the fallback that keeps a lit program at all on such a device."""
    return src.replace("#define CX_SUN_SOFT 1", "// CX-49: the soft sun filter is OFF -- too few fragment texture units for its raw cascade views")


def programs(g):
    return {
        "fullbright": (g["cx_own_vs"], g["cx_own_fs"]),
        "depth prepass": (g["cx_depth_v"], g["cx_depth_f"]),
        "keyhole depth prepass": (g["cx_depthkey_v"], g["cx_depthkey_f"]),
        # Programs.EnsureDepthFeatProgram: the lit vertex text under the cutout/displaced depth stage
        "feature depth prepass": (g["cx_ownlit_vs"], g["cx_depthfeat_f"]),
        "caster depth": (g["cx_cast_vs"], g["cx_cast_fs"]),
        "caster depth (alpha-tested)": (g["cx_castmask_v"], g["cx_castmask_f"]),
        "clustered lit": (g["cx_ownlit_vs"], g["cx_ownlit_fs"]),
        # CX-49: the arm Programs.WithoutSunSoft builds where the device cannot seat the sun's raw cascade
        # views -- plain and deferred, because the twins are spliced from whichever text the lit build used
        "clustered lit (no soft sun)": (g["cx_ownlit_vs"], nosun(g["cx_ownlit_fs"])),
        "clustered lit (no soft sun, deferred twin)": (g["cx_ownlit_vs"], nosun(g["cx_ownlit_fs"]).replace("#version 460 core\n", "#version 460 core\n#define CX_DEFERRED 1\n#define CX_GBUF_OUT 1\n", 1)),
        # CX-188: the deferred twin -- the same two defines Programs.WithDeferred injects, in the same place
        "clustered lit (deferred twin)": (g["cx_ownlit_vs"], g["cx_ownlit_fs"].replace("#version 460 core\n", "#version 460 core\n#define CX_DEFERRED 1\n#define CX_GBUF_OUT 1\n", 1)),
        "clustered lit (decal layer writer)": (g["cx_ownlit_vs"], g["cx_ownlit_fs"].replace("#version 460 core\n", "#version 460 core\n#define CX_DECAL_WRITE 1\n", 1)),
        "coverage layer seed": (g["cx_fullscreen_v"], g["cx_a2cseed_f"]),
        "coverage layer depth": (g["cx_fullscreen_v"], g["cx_a2cdepth_f"]),
        "coverage layer resolve": (g["cx_fullscreen_v"], g["cx_a2cres_f"]),
        "post bloom downsample": (g["cx_post_vs"], g["cx_bloomdn_fs"]),
        "post bloom upsample": (g["cx_post_vs"], g["cx_bloomup_fs"]),
        "post bloom (compute)": (g["cx_bloom_cs"],),
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
        "motion vectors": (g["cx_fullscreen_v"], g["cx_motioncam_f"]),
        "mover motion": (g["cx_motion_v"], g["cx_motion_f"]),
        "taa resolve": (g["cx_fullscreen_v"], g["cx_taa_f"]),
        "ssr hi-Z": (define(g["cx_ssrhiz_c"], "CX_SSR_STORAGE 1"),),
        "ssr colour chain": (define(g["cx_ssrhiz_c"], "CX_SSR_COLOUR 1"),),
        "ssr trace": (g["cx_fullscreen_v"], g["cx_ssrtrace_f"]),
        "ssr resolve": (g["cx_fullscreen_v"], g["cx_ssrres_f"]),
        "ssr history": (g["cx_fullscreen_v"], g["cx_ssrhist_f"]),
        "post stack taa": (g["cx_post_vs"], g["cx_taa_fs"]),
        "highlight composite": (g["cx_fullscreen_v"], g["cx_hilitecomp_f"]),
        "owned water": (g["cx_water_vs"], g["cx_water_fs"]),
        "instanced grass": (g["cx_grass_vs"], g["cx_grass_fs"]),
        "grass depth prepass": (define(g["cx_grass_vs"], "CX_GRASS_UNLIT 1"), g["cx_grassdepth_f"]),
        "grass scatter": (g["cx_grass_c"],),
        "hi-Z reduce (from depth)": (g["cx_hiz_c"].replace("#version 460 core\n", "#version 460 core\n#define CX_HIZ_FIRST 1\n", 1),),
        "hi-Z reduce (mip to mip)": (g["cx_hiz_c"],),
        **{f"read-back ({t} x{n})": (g["cx_readback_c"].replace("#version 460 core\n", f"#version 460 core\n{d}#define CX_READBACK_WORDS {n}\n", 1),)
           for t, d, n in (("float", "", 1), ("float", "", 2), ("uint", "#define CX_READBACK_UINT 1\n", 1), ("uint", "#define CX_READBACK_UINT 1\n", 4))},
        "grass blades": (g["cx_gblade_vs"], g["cx_gblade_fs"]),
        # the depth and velocity variants compile the vertex light rig out (GrassField.UnlitOn), and the velocity
        # arm in (GrassField.MotionOn), as GrassField.VkDepthTexts makes them
        "grass blade depth": (define(g["cx_gblade_vs"], "CX_GRASS_UNLIT 1"), g["cx_gblade_df"]),
        "grass motion vectors": (define(define(g["cx_grass_vs"], "CX_GRASS_UNLIT 1"), "CX_MOTION 1"), define(g["cx_grassdepth_f"], "CX_MOTION 1")),
        "grass blade motion vectors": (define(define(g["cx_gblade_vs"], "CX_GRASS_UNLIT 1"), "CX_MOTION 1"), define(g["cx_gblade_df"], "CX_MOTION 1")),
        "particles": (g["cx_part_v"], g["cx_part_f"]),
        "clustered light build": (g["cx_cluster_cs"],),
        "deferred lights": (g["cx_deferred_cs"],),
        # CX-188: the film's own dispatch (DeferredLights._progFilm), and the "every light" escapes' variant
        # (DeferredLights.SelectEscape): the walk's in-loop hardware tap
        "deferred lights (film pass)": (g["cx_deferred_cs"].replace("#version 460 core\n", "#version 460 core\n#define CX_FILM_PASS 1\n", 1),),
        "deferred lights (in-loop taps)": (g["cx_deferred_cs"].replace("#version 460 core\n", "#version 460 core\n#define CX_SH_INLOOP 1\n", 1),),
        "deferred shadow record": (g["cx_defshadow_cs"],),
        "pair debt": (g["cx_pairdebt"],),
        "volumetric fog light": (g["cx_vfog_cs"],),
        "volumetric fog integrate": (g["cx_vfogsum_cs"],),
        "shading rate": (g["cx_vrs_cs"],),
        "gui": (g["cx_gui_v"], g["cx_gui_f"]),
        "trigger highlight": (g["cx_fullscreen_v"], g["cx_hilitetrig_f"]),
        "oit resolve": (g["cx_fullscreen_v"], g["cx_oitresolve_f"]),
        "light beams (scene database)": (g["cx_beamdb_v"], g["cx_beam_f"]),
        "lightmap bake": (g["cx_bake_v"], g["cx_bake_f"]),
        "lightmap encoder": (g["cx_bakeenc_c"],),
        "weather map scatter": (g["cx_wx_scat_c"],),
        "weather map integrate": (g["cx_wx_int_c"],),
        "weather shelter cache": (g["cx_wx_shel_c"],),
        "sky visibility bake": (g["cx_skybake"],),
        # The two sky wrappers at their STUBS -- what the retry compiles when a stage or a declared pass is
        # not spliced. The real ones go through native_programs() with the fixture's native stages hosted.
        "sky (stub)": (g["cx_own_vs"], g["cx_sky_fs"]),
        "pass wrapper (stub)": (g["cx_pass_cs"],),
        "weather noise tile": (g["cx_wx_noise_c"],),
        "weather ripple tile": (g["cx_wx_ripl_c"],),
    }


# Stage count -> the file extensions glslangValidator infers each stage from.
EXTS = {1: ("comp",), 2: ("vert", "frag"), 4: ("vert", "tesc", "tese", "frag")}


SIDE_EFFECTS = re.compile(r"\b(atomic\w*|imageStore|imageAtomic\w*|gl_FragDepth)\b|\blayout\([^)]*\)\s*(?!readonly)(?:coherent\s+|volatile\s+|restrict\s+)*buffer\b")


def check_early_z(progs):
    """A fragment-stage side effect (SSBO/image store or atomic, gl_FragDepth) costs the program its early-Z,
    so every stage an opaque draw uses must preprocess clean.
    CX-188's deferred twin is checked here too: it draws the OPAQUE and CUTOUT classes, so early-Z is the
    whole point of it, and its G-buffer outputs are plain fragment outputs, not side effects."""
    with tempfile.TemporaryDirectory() as tmp:
        def effects(name):
            src = progs[name][1]
            for tok, val in TOKENS.items():
                src = src.replace(tok, val)
            p = os.path.join(tmp, "ez.frag"); open(p, "w").write(src)
            r = subprocess.run(["glslangValidator", "-E", p], capture_output=True, text=True)
            return sorted({m.group(0).split("(")[0].strip() for m in SIDE_EFFECTS.finditer(r.stdout)})
        bad = 0
        for name, want in (("clustered lit", False), ("clustered lit (deferred twin)", False),
                           ("owned water", False), ("grass blades", False)):
            got = effects(name)
            ok = bool(got) == want
            print(f"early-z: {name}: {'ok' if ok else 'FAILED'} ({'side effects: ' + ', '.join(got) if got else 'no side effects'})")
            bad |= not ok
        return bad


def check_program(name, stages, tmp, validate):
    """One program as Vulkan GLSL: its stages compiled to a SPIR-V module each, checked against one another as
    one program, and every module validated. Compiling the stages together is what pairs a vertex stage's
    outputs with the fragment stage's inputs. Returns what was refused, or None."""
    d = os.path.join(tmp, re.sub(r"[^A-Za-z0-9_.()-]", "_", name))
    os.makedirs(d, exist_ok=True)
    exts = EXTS[len(stages)]
    for src, ext in zip(stages, exts):
        for tok, val in TOKENS.items():
            src = src.replace(tok, val)
        open(os.path.join(d, f"stage.{ext}"), "w").write(vk_text(src))
    # glslang names each module after its stage, so `stage.frag` compiles to `frag.spv` beside it.
    r = subprocess.run(["glslangValidator"] + VK_FLAGS + [f"stage.{e}" for e in exts],
                       cwd=d, capture_output=True, text=True)
    if r.returncode:
        return (r.stdout + r.stderr).strip()
    if not validate:
        return None
    for ext in exts:
        v = subprocess.run(["spirv-val", "--target-env", VK_ENV, os.path.join(d, ext + ".spv")],
                           capture_output=True, text=True)
        if v.returncode:
            return f"{ext}'s module is not valid:\n" + (v.stdout + v.stderr).strip()
    return None


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


# --- CX-303: content pairs -----------------------------------------------------------------------------------

# Extra trees of content .shd (a hak or NWSync extraction, a test override folder), colon-separated.
CONTENT_STAGES = [d for d in os.environ.get("CX_CONTENT_STAGES", "").split(":") if d]
CONTENT_MARK, CONTENT_VS_MARK = "// $VANILLA-CONTENT ", "// $VANILLA-CONTENT-VS "
FS_STUB = "bool CxHostSurface(int feat) { return false; }\n"
VS_STUB = "bool CxHostVertex(CxDraw d, int id, int bb) { return false; }\n"
COMMENTS_RE = re.compile(r"//[^\n]*|/\*.*?\*/", re.S)


def with_define(src, define):
    """Mirrors ShaderStages.WithDefine: one #define after the #version line."""
    return src.replace("#version 460 core\n", f"#version 460 core\n#define {define}\n", 1)


def bindless(fs):
    names = (("uTex", 0), ("uFlowTex", 6), ("uNrmTex", 1), ("uSpecTex", 2), ("uRoughTex", 3),
             ("uHeightTex", 4), ("uIllumTex", 5), ("uAoTex", 7))
    for n, _ in names:
        fs = re.sub(rf"uniform sampler2D {n};[^\n]*\n", "", fs)
    head = ("#version 460 core\n#define CX_BINDLESS 1\n#extension GL_ARB_bindless_texture : require\n"
            "layout(std430, binding = 3) readonly buffer CxTexH { uvec2 uTexH[]; };\n"
            + "".join(f"#define {n} sampler2D(uTexH[(vDraw << 3) + {k}])\n" for n, k in names))
    return fs.replace("#version 460 core\n", head, 1)


def vs_bindless(vs):
    """Mirrors ContentPrograms.VsBindless: a content vertex half samples by the record's handles."""
    return vs.replace("#version 460 core\n", "#version 460 core\n#define CX_BINDLESS 1\n#extension GL_ARB_bindless_texture : require\n", 1)


def content_pair(g, vs, fs, chain):
    """Mirrors ContentPrograms.Start: the lit stages with each non-stock half's host library replaced by a content
    region naming both halves, the hook region spliced first, then the halves translated -- the vertex half
    against the lit vertex stage's own text, the fragment half on top of the vertex half's lanes and varyings.
    A None half is stock: Alloy's own stage. Returns (vertex text, fragment text, vertex result, fragment result)."""
    vres = fres = None
    vtext = expand_includes("cx_ownlit_vs.glsl")
    if vs is not None:
        raw = expand_includes("cx_ownlit_vs.glsl", swap={"cx_host_v.glsl": f"{CONTENT_VS_MARK}{vs} {fs or ''}\n{VS_STUB}// $VANILLA-END\n"})
        at = raw.index(CONTENT_VS_MARK)
        end = raw.index("// $VANILLA-END", at)
        vres = content_stage.translate(vs, False, chain, g["cx_content_v"], "", "", g["cx_constd_v"], raw[:at], partner=fs or "")
        vtext = raw[:at] + (vres.host if vres.host else VS_STUB) + raw[end:]
    ftext = g["cx_ownlit_fs"]
    if fs is not None:
        raw = expand_includes("cx_ownlit_fs.glsl", swap={"cx_host_f.glsl": f"{CONTENT_MARK}{fs} {vs or ''}\n{FS_STUB}// $VANILLA-END\n"})
        done = set()
        src = splice(raw, {}, g, done)
        at = src.index(CONTENT_MARK)
        end = src.index("// $VANILLA-END", at)
        seen = {d.lower() for d in done}
        fres = content_stage.translate(fs, True, lambda n: "" if n.lower() in seen else chain(n), g["cx_content_f"],
                                       g["cx_vanenv_defs"], g["cx_vanenv_f"], g["cx_constd_f"], src[:at],
                                       vertex=vres if vres is not None and not vres.refused else None, partner=vs or "")
        ftext = src[:at] + (fres.host if fres.host else FS_STUB) + src[end:]
    return vtext, ftext, vres, fres


def pair_programs(name, vtext, ftext, fres, vres):
    """The programs ContentPrograms.Twins builds for a pair, by the same facts."""
    vb = vs_bindless(vtext) if vres is not None else vtext
    out = {f"{name}": (vtext, ftext), f"{name} (bindless)": (vb, bindless(ftext))}
    post = fres is not None and fres.post
    lights = fres is not None and fres.reads_lights
    if not post and not lights:
        out[f"{name} (deferred twin)"] = (vtext, with_define(with_define(ftext, "CX_GBUF_OUT 1"), "CX_DEFERRED 1"))
    if (fres is not None and fres.cuts) or vres is not None:
        depth = with_define(bindless(ftext), "CX_DEPTH_ONLY 1")
        cast = with_define(depth, "CX_CAST 1")
        out[f"{name} (depth twin)"] = (vb, depth)
        out[f"{name} (caster twin)"] = (with_define(vb, "CX_CAST 1"), cast)
        out[f"{name} (layered caster twin)"] = (with_define(vb, "CX_CAST_LAYER 1"), cast)
    return out


def is_vertex(text):
    code = COMMENTS_RE.sub(" ", text)
    return bool(re.search(r"^[ \t]*#[ \t]*define[ \t]+SHADER_TYPE[ \t]+1\b", code, re.M) or re.search(r"\bgl_Position\b", code))


def partner(resref, files, vertex):
    """The other half a content tree's naming pairs a stage with: x_vs/x_fs, vs_x/fs_x, a_vs_b/a_fs_b."""
    a, b = ("vs", "fs") if vertex else ("fs", "vs")
    for cand in (re.sub(rf"_{a}$", f"_{b}", resref), re.sub(rf"^{a}_", f"{b}_", resref), resref.replace(f"_{a}_", f"_{b}_")):
        if cand != resref and cand in files:
            return cand
    return None


def engine_builds(text, fragment):
    with tempfile.TemporaryDirectory() as tmp:
        p = os.path.join(tmp, "engine." + ("frag" if fragment else "vert"))
        open(p, "w").write(content_stage.engine_prologue(fragment, fragment) + text)
        return subprocess.run(["glslangValidator", p], capture_output=True).returncode == 0


def content_programs(g):
    """Every content stage found, built the engine's way (the oracle) and translated into its pair (the subject).
    Returns the programs to compile and whether the shared fixture held."""
    bad = 0
    for why in content_stage.selfcheck(g["cx_content_f"], g["cx_content_v"], g["cx_vanenv_defs"], g["cx_vanenv_f"],
                                       g["cx_constd_f"], g["cx_constd_v"], g["cx_ownlit_v"]):
        print(f"content pair: fixture FAILED: {why}")
        bad = 1
    progs = {}
    fix = content_stage.FIXTURE.get
    vt, ft, vr, fr = content_pair(g, "cx_fixvs", "cx_fixture", fix)
    progs.update(pair_programs("content pair cx_fixvs/cx_fixture", vt, ft, fr, vr))
    # CX-316: a pair that declares the engine-named set itself, so the stripped declarations and the CxEngine
    # block's reads are compiled -- the wind arrays no corpus stage happens to read.
    vt, ft, vr, fr = content_pair(g, "cx_fixengv", "cx_fixeng", fix)
    progs.update(pair_programs("content pair cx_fixengv/cx_fixeng", vt, ft, fr, vr))
    for f in ("cx_fixpost", "cx_fixwhole"):
        vt, ft, vr, fr = content_pair(g, None, f, fix)
        progs.update(pair_programs(f"content pair stock/{f}", vt, ft, fr, vr))
    stock = content_stage.stock_shaders(paths.get("NWN_DIR") or "")
    if not stock:
        print("content pair: no game keys under NWN_DIR -- the engine-side oracle is off, fixtures only")
        return progs, bad
    checked = vanilla_only = 0
    for d in [CONTENT_SHD] + CONTENT_STAGES:
        chain = content_stage.Chain([d], stock)
        files = set(chain.files)
        for resref in sorted(files):
            if resref.startswith("inc_") or resref.startswith("cx_"):
                continue
            own = chain(resref) or ""
            whole = content_stage.engine_text(resref, chain)
            if whole is None or not content_stage.MAIN_FN.search(whole):
                continue
            vertex = is_vertex(own)
            if not vertex and "gl_FragColor" not in whole:
                continue
            # a MATERIAL stage: one that runs the stock material skeleton -- its entry points, or (a monolithic fork)
            # its own copy of them. The engine's own pipeline stages (particles, shadow volumes, the framebuffer
            # chain) are drawn by the engine's passes, and no .mtr in any corpus names one.
            body = COMMENTS_RE.sub(" ", whole.split("void main", 1)[-1] if not vertex else whole)
            if not re.search(r"\b(?:ApplyStandardShader|SetupStandardShaderInputs" + (r"|TransformMainCoordinates" if vertex else "")
                             + r")\s*\(", body):
                continue
            if content_stage.stock_shaped(own, not vertex):
                continue
            if not engine_builds(whole, not vertex):
                vanilla_only += 1
                print(f"content pair: {resref}: the engine's own build fails too -- not expected to run")
                continue
            other = partner(resref, files, vertex)
            if other is not None and content_stage.stock_shaped(chain(other) or "", vertex):
                other = None
            vs, fs = (resref, other) if vertex else (other, resref)
            if not vertex and other is not None:
                continue        # the vertex half's turn builds the pair
            vt, ft, vr, fr = content_pair(g, vs, fs, chain)
            refused = (vr.refused if vr is not None else None) or (fr.refused if fr is not None else None)
            if refused:
                print(f"content pair: {vs or 'stock'}/{fs or 'stock'}: REFUSED though the engine builds it -- {refused}")
                bad = 1
                continue
            checked += 1
            progs.update(pair_programs(f"content pair {vs or 'stock'}/{fs or 'stock'}", vt, ft, fr, vr))
            for n in (vr.notes if vr is not None else []) + (fr.notes if fr is not None else []):
                print(f"content pair: {vs or 'stock'}/{fs or 'stock'}: {n}")
    print(f"content pair: {checked} pair(s) the engine builds, translated; {vanilla_only} stage(s) the engine cannot build")
    return progs, bad


def post_programs(raw, g):
    """The hosted post stage once per corpus of CX_CONTENT_STAGES, over the game's own set: the fsfbpostpr that chain
    resolves -- a corpus's own, telegraphs and all, or stock's over the corpus's inc_postpr -- run whole."""
    stock = content_stage.stock_shaders(paths.get("NWN_DIR") or "")
    out, texts = {}, {g["cx_fbpost_fs"]}
    for d in CONTENT_STAGES if stock else []:
        fs = splice(raw["cx_fbpost_fs"], {}, raw, read=content_stage.Chain([d], stock))
        if fs not in texts:
            texts.add(fs)
            where = os.path.join(*os.path.normpath(d).split(os.sep)[-2:])
            out[f"post stack fbpost ({where}'s chain, fsfbpostpr whole)"] = (g["cx_post_vs"], fs)
    return out


# --- native stages, images and passes (docs/shader-overrides.md, "Native stages, images and passes") ---------
# Wherever Alloy hosts a content stage X -- a sky's fragment stage, either half of a material pair -- the chain's
# cx_X runs in its place under CX_NATIVE, and may declare GPU images and compute passes on #pragma lines of its own.
# This mirrors CX.Alloy.StageResources' parser, so a declaration the extender would refuse fails here instead of at a
# player's first frame; every token is an identifier or a decimal integer, because a GLSL preprocessor tokenises the
# line and refuses `128x128` as an integer with a type suffix.
NATIVE_FIXTURE = os.path.join(ROOT, "tools/native/fixture")
NATIVE_CS = os.path.join(MANAGED, "Alloy/Pass/StageResources.cs")
_NATIVE_SRC = open(NATIVE_CS).read()
# The budgets, read off the C# declaration so the two can only agree.
NATIVE_MAX_IMAGES, NATIVE_MAX_PASSES, NATIVE_MAX_2D, NATIVE_MAX_3D, NATIVE_MAX_VIEWDIV = map(int, re.search(
    r"MaxImages = (\d+), MaxPasses = (\d+), Max2dSide = (\d+), Max3dSide = (\d+), MaxViewDiv = (\d+);", _NATIVE_SRC).groups())
NATIVE_MAX_BYTES = (lambda m: int(m.group(1)) << int(m.group(2)))(re.search(r"MaxImageBytes = (\d+)L << (\d+);", _NATIVE_SRC))
NATIVE_MAX_NAME = int(re.search(r"MaxNameChars = (\d+);", _NATIVE_SRC).group(1))
# name -> (the sized-format enum StageResources maps it to, bytes per texel). StageResources.SelfCheck.cs
# asserts the same enums and check_native_grammar() reads them out of it, so neither table can drift.
NATIVE_FORMAT_ENUM = {"rgba8": (0x8058, 4), "rgba16f": (0x881A, 8), "rgba32f": (0x8814, 16),   # not-an-offset: format enums
                      "rg16f": (0x822F, 4), "rg8": (0x822B, 2), "r16f": (0x822D, 2),   # not-an-offset: format enums
                      "r32f": (0x822E, 4), "r8": (0x8229, 1), "r11f_g11f_b10f": (0x8C3A, 4)}   # not-an-offset: format enums
NATIVE_FORMATS = set(NATIVE_FORMAT_ENUM)
NATIVE_FLAGS = {"history", "mips", "clamp"}
NATIVE_TOKEN = re.compile(r"^(?:[A-Za-z_][A-Za-z0-9_]*|[0-9]+)$")
NATIVE_NAME = re.compile(r"^[A-Za-z_][A-Za-z0-9_]{0,%d}$" % (NATIVE_MAX_NAME - 1))
# An image or sampler uniform as a stage declares it: the format layout qualifier is what a declared image's
# own format has to agree with, and the 2D/3D-ness with its shape.
NATIVE_IMG = re.compile(r"(?:layout\s*\(([^)]*)\)\s*)?uniform\s+"
                        r"(?:(?:readonly|writeonly|coherent|volatile|restrict)\s+)*(image2D|image3D)\s+(\w+)\s*;")
NATIVE_SMP = re.compile(r"uniform\s+(sampler2D|sampler3D)\s+(\w+)\s*;")


def native_side(s, lo, hi, what):
    if not s.isdigit() or not lo <= int(s) <= hi:
        raise ValueError(f"{what} `{s}` is not a decimal {lo}..{hi}")
    return int(s)


def native_image(tok, images):
    if len(tok) < 5:
        raise ValueError(f"cx_image needs a name, a shape and a format: `{' '.join(tok)}`")
    name = tok[2]
    if not NATIVE_NAME.match(name) or name.startswith("gl_"):
        raise ValueError(f"`{name}` is not a GLSL identifier of at most {NATIVE_MAX_NAME} characters")
    if any(i["name"] == name for i in images):
        raise ValueError(f"two images are named `{name}`")
    im = {"name": name, "history": False, "mips": False, "clamp": False}
    if tok[3] == "tex2d":
        if len(tok) < 7:
            raise ValueError(f"`tex2d` takes <W> <H>: `{' '.join(tok)}`")
        im["shape"], im["w"], im["h"], im["d"] = "tex2d", native_side(tok[4], 1, NATIVE_MAX_2D, "a 2d width"), \
            native_side(tok[5], 1, NATIVE_MAX_2D, "a 2d height"), 1
        at = 6
    elif tok[3] == "tex3d":
        if len(tok) < 8:
            raise ValueError(f"`tex3d` takes <W> <H> <D>: `{' '.join(tok)}`")
        im["shape"] = "tex3d"
        im["w"], im["h"], im["d"] = (native_side(tok[4 + k], 1, NATIVE_MAX_3D, "a 3d side") for k in range(3))
        at = 7
    elif tok[3] == "view":
        im["shape"], im["div"], im["d"], im["clamp"] = "view", \
            native_side(tok[4], 1, NATIVE_MAX_VIEWDIV, "a view divisor"), 1, True
        at = 5
    else:
        raise ValueError(f"`{tok[3]}` is not tex2d, tex3d or view")
    if at >= len(tok) or tok[at] not in NATIVE_FORMATS:
        raise ValueError(f"`{tok[at] if at < len(tok) else ''}` is not one of {', '.join(sorted(NATIVE_FORMATS))}")
    im["format"] = tok[at]
    for f in tok[at + 1:]:
        if f not in NATIVE_FLAGS:
            raise ValueError(f"`{f}` is not one of {', '.join(sorted(NATIVE_FLAGS))}")
        im[f] = True
    return im


def native_pass(tok):
    if len(tok) != 5:
        raise ValueError(f"cx_pass needs once|frame, a resref and an image: `{' '.join(tok)}`")
    if tok[2] not in ("once", "frame"):
        raise ValueError(f"`{tok[2]}` is neither once nor frame")
    if not 0 < len(tok[3]) <= 16:
        raise ValueError(f"`{tok[3]}` is not a resref of at most 16 characters")
    return {"once": tok[2] == "once", "ref": tok[3], "image": tok[4]}


def native_fog(tok, images):
    """A `cx_fog <image> <x> <y>` line: a texel of a declared tex2d image, inside it."""
    line = " ".join(tok)
    if len(tok) != 5 or not NATIVE_NAME.match(tok[2]) or not tok[3].isdigit() or not tok[4].isdigit():
        raise ValueError(f"cx_fog takes an image name and a texel's <x> <y>: `{line}`")
    im = next((i for i in images if i["name"] == tok[2]), None)
    if im is None:
        raise ValueError(f"`{line}` names `{tok[2]}`, which no cx_image declares")
    if im["shape"] != "tex2d":
        raise ValueError(f"`{line}` names `{tok[2]}`, which is not a tex2d image")
    x, y = int(tok[3]), int(tok[4])
    if x >= im["w"] or y >= im["h"]:
        raise ValueError(f"`{line}` is outside `{tok[2]}`, which is {im['w']}x{im['h']}")
    return {"image": tok[2], "x": x, "y": y}


def native_overlay(tok, images):
    """A `cx_overlay <image>` line: a declared 2d image (tex2d or view), which the resolve adds over the frame."""
    line = " ".join(tok)
    if len(tok) != 3 or not NATIVE_NAME.match(tok[2]):
        raise ValueError(f"cx_overlay takes one image name: `{line}`")
    im = next((i for i in images if i["name"] == tok[2]), None)
    if im is None:
        raise ValueError(f"`{line}` names `{tok[2]}`, which no cx_image declares")
    if im["shape"] == "tex3d":
        raise ValueError(f"`{line}` names `{tok[2]}`, a volume: the frame takes a 2d image")
    return tok[2]


def native_decls(text):
    """The images, passes, fog texel and overlay image (None if undeclared) a native stage's OWN top-level text
    declares -- its includes are libraries, not a place to declare resources. Raises ValueError on anything the
    extender would refuse."""
    images, passes, fogs, overlays = [], [], [], []
    for raw in text.split("\n"):
        line = raw.strip()
        if not line.startswith("#pragma"):
            continue
        tok = line.split()
        if len(tok) < 2 or not tok[1].startswith("cx_"):
            continue
        for t in tok[2:]:
            if not NATIVE_TOKEN.match(t):
                raise ValueError(f"`{t}` on `{line}` is neither an identifier nor a decimal integer, and a "
                                 "GLSL preprocessor would refuse the line")
        if tok[1] == "cx_image":
            images.append(native_image(tok, images))
        elif tok[1] == "cx_pass":
            passes.append(native_pass(tok))
        elif tok[1] == "cx_fog":
            if fogs:
                raise ValueError(f"a second cx_fog, `{line}`: a stage names one fog colour")
            fogs.append(tok)
        elif tok[1] == "cx_overlay":
            if overlays:
                raise ValueError(f"a second cx_overlay, `{line}`: a frame has one overlay")
            overlays.append(tok)
        else:
            raise ValueError(f"unknown declaration `{tok[1]}` on `{line}`")
    if len(images) > NATIVE_MAX_IMAGES:
        raise ValueError(f"{len(images)} images declared, past the budget of {NATIVE_MAX_IMAGES}")
    if len(passes) > NATIVE_MAX_PASSES:
        raise ValueError(f"{len(passes)} passes declared, past the budget of {NATIVE_MAX_PASSES}")
    names = {i["name"] for i in images}
    for p in passes:
        if p["image"] not in names:
            raise ValueError(f"pass {p['ref']} is dispatched over `{p['image']}`, which no cx_image declares")
    fog = native_fog(fogs[0], images) if fogs else None
    overlay = native_overlay(overlays[0], images) if overlays else None
    # The memory budget, as far as the declarations themselves say it: every FIXED-size image exactly, mips
    # and history included. A view-sized one is measured at runtime and only the extender can take it.
    fixed = sum(native_image_bytes(i) for i in images if i["shape"] != "view")
    if fixed > NATIVE_MAX_BYTES:
        raise ValueError(f"its fixed-size images come to {fixed / (1 << 20):.1f} MiB, past the budget of "
                         f"{NATIVE_MAX_BYTES >> 20} MiB")
    return images, passes, fog, overlay


def native_image_bytes(im):
    """One image's storage at its declared extent: the mip chain summed, doubled for a history pair."""
    w, h, d = im["w"], im["h"], im["d"]
    bpp = NATIVE_FORMAT_ENUM[im["format"]][1]
    levels = 1
    if im["mips"]:
        side = max(w, h, d if im["shape"] == "tex3d" else 1)
        while side > 1:
            side >>= 1
            levels += 1
    total = sum(max(w >> l, 1) * max(h >> l, 1) * (max(d >> l, 1) if im["shape"] == "tex3d" else 1) * bpp
                for l in range(levels))
    return total * (2 if im["history"] else 1)


def native_stages(g):
    """(tree, chain, native resref, stage resref) for the fixture and for every native stage a content tree ships. A
    `cx_*.shd` that overrides one of Alloy's OWN stages or libraries is not a native stage, and neither is one whose
    plain stage the tree does not ship and which declares nothing (a library, say)."""
    stock = content_stage.stock_shaders(paths.get("NWN_DIR") or "")
    out = []
    for d in [NATIVE_FIXTURE, CONTENT_SHD] + CONTENT_STAGES:
        if not os.path.isdir(d):
            continue
        chain = content_stage.Chain([d], stock)
        for resref in sorted(chain.files):
            if not resref.startswith("cx_") or resref in g or len(resref) > 16:
                continue
            text = chain(resref) or ""
            stage = resref[3:]
            if "#pragma cx_" not in text and stage not in chain.files:
                continue
            out.append((d, chain, resref, stage))
    return out


def native_bindings(text):
    """{uniform name: (GLSL type, format layout qualifier or None)} for every image and 2D/3D sampler a
    program declares -- what the backend will bind, as against what the #pragma lines declared."""
    out = {}
    for m in NATIVE_IMG.finditer(text):
        quals = [q.strip() for q in (m.group(1) or "").split(",")]
        out[m.group(3)] = (m.group(2), next((q for q in quals if q in NATIVE_FORMATS), None))
    for m in NATIVE_SMP.finditer(text):
        out.setdefault(m.group(2), (m.group(1), None))
    return out


def native_check_bindings(images, text):
    """Reconcile one program's own declarations with the stage's: the GLSL type against the image's shape, and
    an image uniform's format layout qualifier against the declared format. A format mismatch is undefined
    loads and stores -- it compiles, links and draws the wrong pixels -- so nothing else would catch it."""
    byname = {i["name"]: i for i in images}
    errs, written = [], set()
    for name, (kind, fmt) in sorted(native_bindings(text).items()):
        prev = name.endswith("_prev")
        im = byname.get(name[:-5] if prev else name)
        if im is None:
            continue
        if prev and not im["history"]:
            errs.append(f"`{name}` is read but `{im['name']}` is not declared `history`")
        if (kind in ("image3D", "sampler3D")) != (im["shape"] == "tex3d"):
            errs.append(f"`{name}` is declared {kind} but its cx_image is {im['shape']}")
        if not kind.startswith("image"):
            continue
        if not prev:
            written.add(im["name"])
        if fmt is None:
            errs.append(f"`{name}` is declared {kind} with no format layout qualifier")
        elif fmt != im["format"]:
            errs.append(f"`{name}` is declared layout({fmt}) but its cx_image says {im['format']}")
    return errs, written


# Mirrors FbPostStage: the engine's post program the wrapper's whole-stage region names.
POST_STAGE, POST_REGION = "fsfbpostpr", "// $VANILLA-MAIN "


def native_flat(native, chain):
    """A native stage's text with its includes in place, the engine's `inc_standard` left out: what its host and half
    are read off, since a SHIM (`#include "<stage>"` and one replaced function) runs the stage's skeleton from there."""
    text = chain(native) or ""
    return content_stage.resolve(text, chain, "", [native.lower()], 0, [0], optional=True)[0] or text


def native_host(native, text):
    """Which host runs a native stage: the post chain's, by name; a MATERIAL half, one that runs the stock material
    skeleton; else the sky, whose fragment stage runs whole. `text` is the stage with its includes in place."""
    if native[3:] == POST_STAGE:
        return "post"
    body = COMMENTS_RE.sub(" ", text)
    if re.search(r"\b(?:ApplyStandardShader|SetupStandardShaderInputs|TransformMainCoordinates)\s*\(", body):
        return "material"
    return "sky"


def native_programs(raw, g):
    """Every native stage, built the way its host builds it -- a sky stage in cx_sky_fs under CX_NATIVE with its
    fallback stage beside it, a material half as its content pair with the other half by name or stock, the post chain
    in cx_fbpost_fs's retargeted region -- and each pass it declares hosted in cx_pass_cs, by the same substitution and
    the same splice, refused on the same grounds."""
    out, bad = {}, 0
    found = native_stages(g)
    for d, chain, native, stage in found:
        where = os.path.basename(os.path.normpath(d))
        text = chain(native) or ""
        try:
            images, passes, _, overlay = native_decls(text)
        except ValueError as e:
            print(f"native stage: {native}.shd ({where}): FAILED -- {e}")
            bad = 1
            continue
        # A pass the chain cannot answer for is what the extender refuses the stage over; the wrapper's STUB
        # would otherwise compile here and report a pass that dispatches nothing as checked.
        gone = [p["ref"] for p in passes if chain(p["ref"]) is None]
        if gone:
            print(f"native stage: {native}.shd ({where}): FAILED -- pass stage(s) NOT FOUND in the chain: "
                  + ", ".join(sorted(gone)))
            bad = 1
            continue
        flat = native_flat(native, chain)
        host = native_host(native, flat)
        print(f"native stage: {native}.shd ({where}): {host}, {len(images)} image(s), {len(passes)} pass(es)")
        texts, written = {}, set()
        if host == "post":
            fs = raw["cx_fbpost_fs"].replace(f"{POST_REGION}{POST_STAGE}\n", f"{POST_REGION}{native}\n")
            texts[f"native post chain {native} ({where})"] = (g["cx_post_vs"], splice(define(fs, "CX_NATIVE 1"), {}, raw, read=chain))
        elif host == "material":
            vertex = is_vertex(flat)
            files = set(chain.files)
            other = partner(stage, files, vertex)
            if other is not None and "cx_" + other in files:
                other = "cx_" + other
            vs, fs = (native, other) if vertex else (other, native)
            vt, ft, vr, fr = content_pair(g, vs, fs, chain)
            refused = (vr.refused if vr is not None else None) or (fr.refused if fr is not None else None)
            if refused:
                print(f"native stage: {native}.shd ({where}): FAILED -- the content pair {vs or 'stock'}/{fs or 'stock'} "
                      f"is refused: {refused}")
                bad = 1
                continue
            # the two halves link into one program: an image either native half declares binds in both
            if other is not None and other.startswith("cx_"):
                try:
                    images = images + [i for i in native_decls(chain(other) or "")[0] if i["name"] not in {j["name"] for j in images}]
                except ValueError:
                    pass        # the other half's own entry reports it
            for half, name, out_text in ((vs, "vertex", vt), (fs, "fragment", ft)):
                own = native_bindings(chain(half) or "") if half else {}
                for im in images:
                    for n in (im["name"], im["name"] + "_prev"):
                        # the translator keeps an image's uniform as written; a macro in its place is the material's
                        # texture 0 read where the image should be -- a wrong picture with no log line
                        if n in own and re.search(rf"^\s*#\s*define\s+{n}\b", out_text, re.M):
                            print(f"native stage: {native}.shd ({where}): FAILED -- the {name} half's `{n}` was "
                                  "translated away from the image it names")
                            bad = 1
            if vs is not None and vs.startswith("cx_"):
                vt = define(vt, "CX_NATIVE 1")
            if fs is not None and fs.startswith("cx_"):
                ft = define(ft, "CX_NATIVE 1")
            texts.update(pair_programs(f"native content pair {vs or 'stock'}/{fs or 'stock'} ({where})", vt, ft, fr, vr))
        else:
            fs = define(raw["cx_sky_fs"].replace("$SKYFS$", native), "CX_NATIVE 1")
            texts[f"native sky {native} ({where})"] = (g["cx_own_vs"], splice(fs, {}, raw, read=chain))
            if stage in chain.files:
                texts[f"sky stage {stage} ({where}, the native stage's fallback)"] = (
                    g["cx_own_vs"], splice(raw["cx_sky_fs"].replace("$SKYFS$", stage), {}, raw, read=chain))
        for p in passes:
            cs = splice(raw["cx_pass_cs"].replace("$PASSCS$", p["ref"]), {}, raw, read=chain)
            texts[f"pass {p['ref']} ({native}, {where})"] = (cs,)
            errs, wrote = native_check_bindings(images, chain(p["ref"]) or "")
            if not p["once"]:
                written |= wrote
            for e in errs:
                print(f"pass: {p['ref']}.shd ({where}): FAILED -- {e}")
                bad = 1
        for e in native_check_bindings(images, text)[0]:
            print(f"native stage: {native}.shd ({where}): FAILED -- {e}")
            bad = 1
        # A `history` image no FRAME pass rewrites has no history to keep: its two halves would alternate
        # between the baked texture and a zeroed one. The extender refuses the flag; so does this.
        for im in images:
            if im["history"] and im["name"] not in written:
                print(f"native stage: {native}.shd ({where}): FAILED -- `{im['name']}` is declared `history` but no "
                      "frame pass writes it as an image")
                bad = 1
        if overlay is not None and overlay not in written:
            print(f"native stage: {native}.shd ({where}): FAILED -- the overlay image `{overlay}` is not written as an "
                  "image by any frame pass")
            bad = 1
        out.update(texts)
    hosts = {native_host(n, native_flat(n, chain)) for d, chain, n, _ in found if d == NATIVE_FIXTURE}
    if hosts != {"sky", "material", "post"}:
        print(f"native stage: the fixture under {os.path.relpath(NATIVE_FIXTURE, ROOT)} was not found whole (a sky, a "
              "material half and a post chain) -- the grammar and the wrappers are UNCHECKED")
        bad = 1
    elif all(d == NATIVE_FIXTURE for d, _, _, _ in found):
        print("native stage: no content tree ships one -- the fixture is what is checked")
    return out, bad


# The C#<->Python pin. CX.Alloy.StageResources.SelfCheck asserts its grammar on literal declaration lines and the
# enum each format maps to; those literals are read back out of that file here and run through THIS parser,
# which has to reach the same verdicts. Two parsers for one grammar stay one grammar only if something says so.
NATIVE_SELFCHECK_CS = os.path.join(MANAGED, "Alloy/Pass/StageResources.SelfCheck.cs")
NATIVE_CS_GOOD = re.compile(r'const string good = """\n(.*?)\n[ \t]*""";', re.S)
NATIVE_CS_BAD = re.compile(r'Bad\(check,\s*"((?:[^"\\]|\\.)*)"')
NATIVE_CS_FMT = re.compile(r'Fmt\("(\w+)"\)\s*==\s*(0x[0-9A-Fa-f]+)')


def check_native_grammar():
    """The accept/refuse lines and the format enums StageResources.SelfCheck.cs asserts, run through this parser."""
    cs = open(NATIVE_SELFCHECK_CS).read()
    bad = 0
    good = NATIVE_CS_GOOD.search(cs)
    if good is None:
        print(f"native grammar: FAILED -- no `const string good` block in {os.path.basename(NATIVE_SELFCHECK_CS)}")
        return 1
    try:
        images, passes, fog, overlay = native_decls(good.group(1))
    except ValueError as e:
        print(f"native grammar: FAILED -- C# accepts its own good block, this parser does not: {e}")
        return 1
    # ...and that block is the FIXTURE's own declarations, so the hand-written literals are tied to the file
    # the gate compiles rather than to nothing.
    fix = os.path.join(NATIVE_FIXTURE, "cx_cxdemo_sky.shd")
    if os.path.exists(fix):
        if native_decls(open(fix, encoding="latin1").read()) != (images, passes, fog, overlay):
            print("native grammar: FAILED -- StageResources.SelfCheck.cs's good block is not the fixture's declarations")
            bad = 1
    refused = NATIVE_CS_BAD.findall(cs)
    if not refused:
        print(f"native grammar: FAILED -- no Bad(check, ...) lines in {os.path.basename(NATIVE_SELFCHECK_CS)}")
        return 1
    for esc in refused:
        line = esc.replace("\\n", "\n")
        try:
            native_decls(line)
        except ValueError:
            continue
        print(f"native grammar: FAILED -- C# refuses `{line}` and this parser accepts it")
        bad = 1
    enums = {n: int(v, 16) for n, v in NATIVE_CS_FMT.findall(cs)}
    mine = {n: e for n, (e, _) in NATIVE_FORMAT_ENUM.items()}
    if enums != mine:
        print(f"native grammar: FAILED -- the format enums differ: C# says {enums}, this says {mine}")
        bad = 1
    print(f"native grammar: {'ok' if not bad else 'FAILED'} ({len(images)} image(s) and {len(passes)} pass(es) "
          f"accepted, {len(refused)} line(s) refused, {len(enums)} format enum(s) pinned)")
    return bad


# CX-338: the world's depth is reversed (near 1, far 0) and its projection folded to match, so a world depth read IS
# the NDC z. GL's decode of it (`d * 2.0 - 1.0`) or GL's far plane (`d >= 1.0`) is a missed site: the frame goes
# wrong without a compile error, so the gate reads the text. Shadow and cover maps keep GL's depth and are not named.
WORLD_DEPTH = r"\b(?:uDepth|uSceneDepth|uGbDepth|uFbDepth|uTrigDepth)\b"
WORLD_READ = re.compile(r"\b(?:texture|textureLod|texelFetch)\s*\(\s*" + WORLD_DEPTH)
GL_DECODE = r"\s*\*\s*2\.0\s*-\s*1\.0"
GL_FAR = r"\s*(?:>=\s*1\.0|<\s*1\.0)"


def world_depth_misses(name, text):
    """Lines of `text` that read world depth the GL way: decoded `* 2.0 - 1.0`, or tested against 1.0 as the far plane."""
    out = []
    held = set()
    for i, line in enumerate(text.splitlines(), 1):
        code = line.split("//")[0]
        m = re.search(r"\b(?:float\s+)?(\w+)\s*=\s*" + WORLD_READ.pattern, code)
        if m:
            held.add(m.group(1))
        names = "|".join(sorted(held))
        if WORLD_READ.search(code) and re.search(r"\)\s*\.\s*[rx]" + GL_DECODE, code):
            out.append(f"{name}.glsl:{i}: a world depth read decoded as GL's (* 2.0 - 1.0)")
        elif WORLD_READ.search(code) and re.search(r"\)\s*\.\s*[rx]" + GL_FAR, code):
            out.append(f"{name}.glsl:{i}: a world depth read tested against GL's far plane (1.0)")
        elif names and re.search(r"\b(?:" + names + r")" + GL_DECODE, code):
            out.append(f"{name}.glsl:{i}: a world depth decoded as GL's (* 2.0 - 1.0)")
        elif names and re.search(r"\b(?:" + names + r")" + GL_FAR, code):
            out.append(f"{name}.glsl:{i}: a world depth tested against GL's far plane (1.0)")
    return out


def check_world_depth():
    """Every embedded .glsl file decodes world depth as reversed."""
    bad = []
    for f in sorted(os.listdir(SHADERS)):
        if f.endswith(".glsl"):
            bad += world_depth_misses(f[:-5], open(os.path.join(SHADERS, f)).read())
    for b in bad:
        print(f"world depth: {b}")
    probe = world_depth_misses("probe", "float d = texture(uDepth, uv).r;\nfloat z = d * 2.0 - 1.0;\n"
                                        "if (texelFetch(uSceneDepth, p, 0).r >= 1.0) return;\n")
    if len(probe) != 2:
        print(f"world depth: the gate's own probe found {len(probe)} of its 2 planted misses"); return 1
    print("world depth: " + (f"{len(bad)} GL decode(s) of the reversed world depth" if bad else "every read is reversed -- ok"))
    return 1 if bad else 0


def main():
    raw = shaders()
    g = spliced(raw)
    try:
        progs = programs(g)
    except KeyError as e:
        sys.exit(f"could not find shader file {e} -- did a stage get renamed?")
    content, bad_content = content_programs(g)
    progs.update(content)
    progs.update(post_programs(raw, g))
    native, bad_native = native_programs(raw, g)
    progs.update(native)

    bad = 0
    validate = shutil.which("spirv-val") is not None
    if not validate:
        print("spirv-val: SKIPPED -- not installed (Arch: spirv-tools); modules are compiled, not validated")
    with tempfile.TemporaryDirectory() as tmp:
        for name, stages in progs.items():
            err = check_program(name, stages, tmp, validate)
            print(f"{name}: {'ok' if err is None else 'FAILED'}")
            if err is not None:
                bad = 1
                print(err)
    print(f"{len(progs)} programs from {len(g)} embedded .glsl: "
          + ("FAILED" if bad else f"every stage compiles to a {VK_ENV} module"
             + (" and validates" if validate else ", unvalidated")))
    bad |= bad_content
    bad |= bad_native
    bad |= check_native_grammar()
    bad |= check_ssbo(progs)
    bad |= check_early_z(progs)
    bad |= check_material(g)
    bad |= audit(g)
    bad |= check_world_depth()
    return bad


if __name__ == "__main__":
    sys.exit(main())
