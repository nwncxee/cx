"""CX-303 content stages, offline: the translation ContentStage.Translate makes and the engine's own assembly.

check-shaders.py compiles every content fragment stage it can find twice -- once the way GLRender builds it
(the engine prologue, its includes resolved from the content tree and the game's own base_shaders.bif), and
once translated into an Alloy lit program -- and fails when a stage the engine can build is one Alloy cannot.
The engine build is the independent oracle: it never passes through this file's translation.

`translate` mirrors managed/Alloy/Core/ContentStage.cs. The two are kept in step by the shared fixture in
ContentStage.SelfCheck (C#) and `selfcheck()` here: the same hand-written stage, the same expected facts.
"""
import os
import re
import struct

# --- the game's own stock shaders, out of data/nwn_base.key -> base_shaders.bif -----------------------------
RES_SHD = 2069   # ShaderResource.ResTypeShd: CResHelper<CResSHD, 2069>


def stock_shaders(nwn_dir):
    """{resref: text} for every .shd the install's KEY indexes. Empty when the install is not found."""
    key = os.path.join(nwn_dir or "", "data", "nwn_base.key")
    if not os.path.isfile(key):
        return {}
    b = open(key, "rb").read()
    _, _, nbif, nkey, off_files, off_keys = struct.unpack_from("<4s4sIIII", b, 0)
    bifs = []
    for i in range(nbif):
        _, name_off, name_len = struct.unpack_from("<IIH", b, off_files + i * 12)
        bifs.append(b[name_off:name_off + name_len].split(b"\0")[0].decode().replace("\\", "/"))
    wanted = {}
    for i in range(nkey):
        o = off_keys + i * 22
        resref = b[o:o + 16].split(b"\0")[0].decode("latin1").lower()
        restype, resid = struct.unpack_from("<HI", b, o + 16)
        if restype == RES_SHD:
            wanted.setdefault(bifs[resid >> 20], []).append((resref, resid & 0xFFFFF))   # not-an-offset: BIF resource-id bit split
    out = {}
    for bif, items in wanted.items():
        data = open(os.path.join(nwn_dir, bif), "rb").read()
        _, _, nvar, _, off_var = struct.unpack_from("<4s4sIII", data, 0)
        table = {}
        for i in range(nvar):
            rid, at, size, _ = struct.unpack_from("<IIII", data, off_var + i * 16)
            table[rid & 0xFFFFF] = (at, size)   # not-an-offset: BIF resource-id bit split
        for resref, idx in items:
            at, size = table[idx]
            out[resref] = data[at:at + size].decode("latin1")
    return out


class Chain:
    """The resource chain as check-shaders can see it: a content tree first (a hak beats the base game), then the
    game's own stock shaders, then `extra` -- the newer generation's headers, which content forks of the lighting
    files include and the installed base does not carry. Case-blind, like the engine's resman."""
    def __init__(self, dirs, stock, extra=None):
        self.files = {}
        for d in dirs:
            if not os.path.isdir(d):
                continue
            for root, _, names in os.walk(d):
                for n in names:
                    if n.lower().endswith(".shd"):
                        self.files.setdefault(n[:-4].lower(), os.path.join(root, n))
        self.stock = stock
        self.extra = extra

    def __call__(self, resref):
        p = self.files.get(resref.lower())
        if p:
            return open(p, encoding="latin1").read()
        hit = self.stock.get(resref.lower())
        return hit if hit is not None or self.extra is None else self.extra(resref)


# --- the engine's own build of a content stage (GLRender::LoadAndCompileSingleShader @0x4d34b0) --------------
def engine_prologue(fragment, frag_color, build=(8193, 37)):
    """The engine's prologue text, with the values a gamma-correcting, fragment-lit, high-quality client sets."""
    return ("#version 330 core\n#define mediump\n#define lowp\n#define highp\n#define MAX_NUM_LIGHTS 32\n"
            "#define MAX_NUM_BONES 64\n#define GAMMA_CORRECTION 1\n#define FRAGMENT_LIGHTING 1\n"
            "#define SHADER_QUALITY_MODE 2\n#define KEYHOLING_ENABLED 1\n#define SHADER_DEBUG_MODE 0\n"
            f"#define BUILD_VERSION {build[0]}\n#define BUILD_REVISION {build[1]}\n#define NO_DISCARD 0\n"
            f"#define POSTPROCESSING_TYPES_ENABLED 0\n#define varying {'in' if fragment else 'out'}\n"
            "#define attribute in\n#define texture2D texture\n#define textureCube texture\n"
            + ("#define gl_FragColor compat_glFragColor\nout vec4 compat_glFragColor;" if frag_color else "") + "\n")


INCLUDE = re.compile(r'^\s*#\s*include\s+"([^"]+)"')


def engine_text(resref, chain, depth=0):
    """The stage with every include followed and nothing deduped (the files' guards do that), as the engine's
    loader resolves it; None when an include does not resolve, which fails the stage there too."""
    text = chain(resref)
    if text is None or depth > 8:
        return None
    out = []
    for line in text.split("\n"):
        m = INCLUDE.match(line)
        if not m:
            out.append(line)
            continue
        inner = engine_text(m.group(1)[:16], chain, depth + 1)
        if inner is None:
            return None
        out.append(inner)
    return "\n".join(out)


# --- the translation (mirrors ContentStage.Translate) -------------------------------------------------------
DROPPED = {"inc_common", "inc_uniforms", "inc_target", "inc_lighting", "inc_material", "inc_keyhole", "inc_fog",
           "inc_transform", "inc_envmap", "inc_random", "inc_tonemap", "inc_debug", "inc_displacement",
           "inc_framebuffer"}
TINT_PLACEHOLDERS = ["fAnisotropy", "fSpecularIntensity", "fIllumColor", "fAlphaMipScale"]
MAX_LANES = 12
ENV_RESREF = "cx_content_f"
MAIN_FN = re.compile(r"\bvoid\s+main\s*\(\s*(?:void)?\s*\)")
DIRECTIVE = re.compile(r"^[ \t]*#[ \t]*(?:version|extension)\b[^\n]*", re.M)
COMMENTS = re.compile(r"//[^\n]*|/\*.*?\*/", re.S)
OWN_DEFINE = re.compile(r"^[ \t]*#[ \t]*define[ \t]+(\w+)", re.M)
PLT_DECL = re.compile(r"^[ \t]*uniform\b[^;\n]*\bPLTscheme\b[^;\n]*;[^\n]*\n?", re.M)
PLT_USE = re.compile(r"\bPLTscheme\s*\[([^\]\[]*)\]")
PREC = r"(?:(?:lowp|mediump|highp)\s+)?"
LAYOUT = r"(?:layout\s*\([^)]*\)\s*)?"
UNIFORM_DECL = re.compile(r"^\s*" + LAYOUT + r"uniform\s+" + PREC + r"(\w+)\s+(.+?)\s*;\s*$")
INPUT_DECL = re.compile(r"^\s*" + LAYOUT + r"(?:(?:flat|smooth|noperspective|centroid)\s+)*(?:in|varying)\s+" + PREC + r"(\w+)\s+(.+?)\s*;\s*$")
OUTPUT_DECL = re.compile(r"^\s*" + LAYOUT + r"out\s+" + PREC + r"(\w+)\s+(\w+)\s*;\s*$")
DECLARATOR = re.compile(r"^\s*(\w+)\s*(\[[^\]]*\])?\s*(?:=\s*(.+))?$", re.S)
GLSL_TYPE = r"(?:void|float|double|int|uint|bool|[iubd]?vec[234]|d?mat[234](?:x[234])?)"
DECL_TAIL = r"\s+(\w+\s*(?:\[[^\]]*\])?\s*(?:(?:=|,)[^;\n]*)?);"
GLOBAL_DECL = re.compile(r"^[ \t]*(?:(?:const|uniform|lowp|mediump|highp)\s+)*(" + GLSL_TYPE + r"|sampler\w*)" + DECL_TAIL, re.M)
ENV_DECL = re.compile(r"^(?:(?:const|uniform|lowp|mediump|highp)\s+)*(" + GLSL_TYPE + r"|sampler\w*)" + DECL_TAIL, re.M)
FN_DEF = re.compile(r"^[ \t]*(?:(?:lowp|mediump|highp|const)\s+)*" + GLSL_TYPE + r"\s+(\w+)\s*\(", re.M)
GUARD = re.compile(r"^[ \t]*#[ \t]*ifndef[ \t]+(\w+)[ \t]*\r?\n[ \t]*#[ \t]*define[ \t]+\1\b", re.M)
OWN_COVERAGE = re.compile(r"^[ \t]*#[ \t]*define[ \t]+(?:ENVIRONMENT_MAP|NO_TEXTURE)[ \t]+1\b", re.M)
ALPHA_WRITE = re.compile(r"\b(?:FragmentColor|gl_FragColor)\s*=\s*vec4\s*\(")
OPENER_RHS = re.compile(r"\s*1(?:\.0*)?\s*\)")
SWIZZLE_ALPHA = re.compile(r"\b(?:FragmentColor|gl_FragColor)\s*\.\s*[rgbaxyzw]*[aw][rgbaxyzw]*\s*[-+*/]?=(?!=)")


class Result:
    def __init__(self):
        self.host, self.params, self.cuts, self.reads_plt, self.notes, self.refused = "", [], False, False, [], None


def resolve(text, chain, std_text, stack, depth, count, optional=False):
    if "#include" not in text:
        return text, None
    out = []
    for line in text.split("\n"):
        m = INCLUDE.match(line)
        if not m:
            out.append(line)
            continue
        name = m.group(1)[:16]
        key = name.lower()
        if key in DROPPED:
            continue
        body = std_text if key == "inc_standard" else chain(name)
        if body is None and optional:
            continue
        if body is None:
            return None, f'includes "{name}", which nothing in the resource chain supplies'
        count[0] += 1
        if depth + 1 >= 8 or key in stack or count[0] > 64:
            return None, f'its includes nest past 8 deep, repeat, or number over 64 ("{name}")'
        stack.append(key)
        inner, missing = resolve(body, chain, std_text, stack, depth + 1, count, optional or key == "inc_standard")
        stack.pop()
        if inner is None:
            return None, missing
        out.append(inner)
    return "\n".join(out) + "\n", None


def items(lst):
    depth, start, parts = 0, 0, []
    for i, c in enumerate(lst):
        if c in "([":
            depth += 1
        elif c in ")]":
            depth -= 1
        elif c == "," and depth == 0:
            parts.append(lst[start:i]); start = i + 1
    parts.append(lst[start:])
    for p in parts:
        m = DECLARATOR.match(p)
        if m:
            yield m.group(1), m.group(2), (m.group(3).strip() if m.group(3) else None)


def supplied_names(env, vanilla_defs, vanilla_env_fs, ahead):
    s = set()
    for t in (env, vanilla_defs, vanilla_env_fs, ahead):
        s.update(OWN_DEFINE.findall(t))
        for m in ENV_DECL.finditer(COMMENTS.sub(" ", t)):
            s.update(n for n, _, _ in items(m.group(2)))
    s.difference_update(TINT_PLACEHOLDERS)
    return {n for n in s if not n.startswith("CX_CON_HAS_")}


def strip_line(line, block):
    out, i = [], 0
    while i < len(line):
        if block[0]:
            if line[i] == "*" and i + 1 < len(line) and line[i + 1] == "/":
                block[0] = False; i += 1
        elif line[i] == "/" and i + 1 < len(line) and line[i + 1] == "/":
            break
        elif line[i] == "/" and i + 1 < len(line) and line[i + 1] == "*":
            block[0] = True; i += 1
        else:
            out.append(line[i])
        i += 1
    return "".join(out)


FED_BY_NAME = re.compile(r"^scriptable(?:Float|Int|Vec)\d+$")


def uniform(typ, name, arr, init, supplied, ps, macros, r):
    if name in supplied:
        return ""
    if FED_BY_NAME.match(name):
        return f"uniform {typ} {name}{arr or ''}" + (f" = {init}" if init is not None else "") + ";\n"
    if arr is not None:
        r.notes.append(f"declares the uniform array {name}, which no .mtr can feed -- held at zero")
        return f"{typ} {name}{arr};\n"
    if typ.startswith("sampler"):
        to = "uTex" if typ == "sampler2D" else "uProbe" if typ == "samplerCube" else ""
        if not to:
            r.refused = f"declares a {typ} ({name}), which no material unit carries"
            return ""
        macros.append(name)
        return f"#define {name} {to}\n"
    kind = {"float": "Float", "int": "Int", "bool": "Bool", "vec4": "Vec4"}.get(typ)
    fallback = f"({init})" if init is not None else f"{typ}(0)"
    macros.append(name)
    if kind is None:
        r.notes.append(f"declares the {typ} {name}, which the engine's .mtr upload (1 or 4 components) never fits -- held at its default")
        return f"#define {name} {fallback}\n"
    if len(ps) >= MAX_LANES:
        r.notes.append(f"declares more than {MAX_LANES} parameters -- {name} is held at its default")
        return f"#define {name} {fallback}\n"
    lane = len(ps)
    ps.append((name, kind, lane))
    value = {"Float": f"CxCp({lane}).x", "Int": f"int(CxCp({lane}).x)",
             "Bool": f"(CxCp({lane}).x != 0.0)", "Vec4": f"CxCp({lane})"}[kind]
    return f"#define {name} (CxCpOn({lane}) ? {value} : {fallback})\n"


def rewrite(code, supplied, ps, macros, zeroed, r):
    m = UNIFORM_DECL.match(code)
    if m:
        return "".join(uniform(m.group(1), n, a, i, supplied, ps, macros, r) for n, a, i in items(m.group(2)))
    m = INPUT_DECL.match(code)
    if m:
        out = []
        for n, a, _ in items(m.group(2)):
            if n in supplied:
                continue
            zeroed.append(n)
            out.append(f"{m.group(1)} {n}{a};\n" if a else f"{m.group(1)} {n} = {m.group(1)}(0);\n")
        return "".join(out)
    m = OUTPUT_DECL.match(code)
    if m:
        if m.group(1) != "vec4":
            r.refused = f"declares a {m.group(1)} fragment output ({m.group(2)})"
            return None
        macros.append(m.group(2))
        return f"#define {m.group(2)} FragmentColor"
    m = GLOBAL_DECL.match(code)
    if m and any(n in supplied for n, _, _ in items(m.group(2))):
        return ""
    return None


def declarations(text, supplied, ps, macros, r):
    out, zeroed, depth, block = [], [], 0, [False]
    for line in text.split("\n"):
        code = strip_line(line, block)
        braces = code.count("{") - code.count("}")
        if depth == 0 and not code.lstrip().startswith("#"):
            new = rewrite(code, supplied, ps, macros, zeroed, r)
            if r.refused:
                return text
            if new is not None:
                out.append(new)
                depth += braces
                continue
        out.append(line)
        depth = max(depth + braces, 0)
    if zeroed:
        r.notes.append(f"reads {', '.join(zeroed)}, which its vertex stage writes and Alloy runs none of -- held at zero")
    return "\n".join(out) + "\n"


OWN_ALPHA = re.compile(r"^\s*(?:(?:FragmentColor|gl_FragColor)\s*\.\s*[aw]|1(?:\.0*)?)\s*$")


def keeps_alpha(flat, at):
    depth, last, i = 1, -1, at
    while i < len(flat) and depth > 0:
        c = flat[i]
        if c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
        elif c == "," and depth == 1:
            last = i
        i += 1
    return last >= 0 and bool(OWN_ALPHA.match(flat[last + 1:i - 1]))


def writes_alpha(flat):
    for m in ALPHA_WRITE.finditer(flat):
        if not OPENER_RHS.match(flat, m.end()) and not keeps_alpha(flat, m.end()):
            return True
    return bool(SWIZZLE_ALPHA.search(flat))


def edits_after_apply(flat):
    main = MAIN_FN.search(flat)
    if not main:
        return False
    apply = flat.find("ApplyStandardShader", main.start())
    if apply < 0:
        return False
    tail = re.sub(r"\bgl_FragColor\s*=\s*FragmentColor\s*;", "", flat[apply:])
    return bool(re.search(r"\b(?:FragmentColor|gl_FragColor)\b(?:\s*\.\s*\w+)?\s*[-+*/]?=(?!=)", tail))


def translate(resref, chain, env, vanilla_defs, vanilla_env_fs, std_text, ahead="", build=(8193, 37)):
    r = Result()
    stage = chain(resref)
    if stage is None:
        r.refused = f"{resref}.shd is not in the resource chain"
        return r
    text, missing = resolve(stage, chain, std_text, [resref.lower()], 0, [0])
    if text is None:
        r.refused = missing
        return r
    text = DIRECTIVE.sub("", text)
    if not MAIN_FN.search(text):
        r.refused = f"{resref}.shd has no void main() to run"
        return r
    own_text, _ = resolve(stage, chain, "", [resref.lower()], 0, [0])
    flat = COMMENTS.sub(" ", own_text or text)
    r.reads_plt = bool(re.search(r"\bPLTscheme\b", flat))
    r.cuts = bool(re.search(r"\bdiscard\b|\bAlphaDiscard\s*\(", flat)) or writes_alpha(flat) or bool(OWN_COVERAGE.search(flat))
    if not re.search(r"\bSetupStandardShaderInputs\s*\(", flat):
        r.notes.append("never runs SetupStandardShaderInputs, so what it writes to FragmentColor is shaded as the surface colour")
    if edits_after_apply(flat):
        r.notes.append("edits its colour after ApplyStandardShader, which under Alloy lands on the surface colour before the lighting")
    supplied = supplied_names(env, vanilla_defs, vanilla_env_fs, ahead)
    text = MAIN_FN.sub("void CxContentMain()", text)
    text = re.sub(r"\bgl_FragColor\b", "FragmentColor", text)
    text = PLT_DECL.sub("", text)
    text = PLT_USE.sub(r"CxPltScheme(vDraw, \1)", text)
    macros = []
    text = declarations(text, supplied, r.params, macros, r)
    if r.refused:
        return r
    guards = set(GUARD.findall(text))
    own = [n for n in dict.fromkeys(OWN_DEFINE.findall(text)) if n not in guards]
    fns = list(dict.fromkeys(FN_DEF.findall(COMMENTS.sub(" ", text))))
    env_defs = list(dict.fromkeys(OWN_DEFINE.findall(env)))
    undef = lambda names: "".join(f"#undef {n}\n" for n in names)
    r.host = (f"// ---- content stage {resref}\n" + undef(TINT_PLACEHOLDERS)
              + f"#define BUILD_VERSION {build[0]}\n#define BUILD_REVISION {build[1]}\n"
              + "".join(f"#define CX_CON_HAS_{f}\n" for f in fns)
              + env + ("" if env.endswith("\n") else "\n")
              + undef(own) + text + "\n"
              + "void CxContentBody()\n{\n    cxConUV = CxHostUV;\n    cxConRow = CxContentRow(vDraw);\n"
              + "    fEnvMapLevel = 0.0;\n    fOcclusion = 1.0;\n"
              + "    CxContentMain();\n    fSpecularity = MATERIAL_SPECULARITY_VALUE;\n"
              + "    fRoughness = MATERIAL_ROUGHNESS_VALUE;\n    fMetallicness = MATERIAL_METALLICNESS_VALUE;\n"
              + "    SpecularColor = MATERIAL_SPECULAR_COLOR_VALUE;\n}\n"
              + undef(own) + undef(env_defs + macros + [f"CX_CON_HAS_{f}" for f in fns])
              + "#undef BUILD_VERSION\n#undef BUILD_REVISION\n"
              + vanilla_defs + ("" if vanilla_defs.endswith("\n") else "\n")
              + "#define CX_CONTENT_PROGRAM 1\n"
              + "bool CxHostSurface(int feat)\n{\n    if (feat < CX_FEAT_CONTENT0) return false;\n"
              + "    CxContentBody();\n    return true;\n}\n")
    return r


# --- the shared fixture (ContentStage.SelfCheck holds the same facts) ---------------------------------------
FIXTURE = {
    "cx_fixture": ('#define SHADER_TYPE 2\n#define LIGHTING 1\n#include "inc_standard"\n#include "cx_fixinc"\n'
                   "uniform float fScale = 2.0;\nuniform int iMode;\nuniform vec4 vTint;\nuniform vec3 vNever;\n"
                   "uniform sampler2D texUnit3;\nuniform sampler2D texMask;\nvarying float fVertexHeight;\n"
                   "in vec3 vOwnVarying;\nfloat FixHelper(float x) { return x * fScale; }\n"
                   "void main ()\n{\n    FragmentColor = vec4(1.0);\n    SetupStandardShaderInputs();\n"
                   "    FragmentColor.rgb *= vTint.rgb * FixHelper(fVertexHeight) + vOwnVarying;\n"
                   "    ApplyStandardShader();\n    gl_FragColor = vec4(FragmentColor.rgb, texture2D(texUnit3, vTexCoords).r);\n}\n"),
    "cx_fixinc": "#ifndef FIX_INC\n#define FIX_INC\nfloat FixShared() { return 1.0; }\n#endif\n",
}


def selfcheck(env, vanilla_defs, vanilla_env_fs, std_text, ahead=""):
    """The fixture's facts, hand-written: the oracle is this text, not the translation."""
    r = translate("cx_fixture", FIXTURE.get, env, vanilla_defs, vanilla_env_fs, std_text, ahead)
    bad = []
    if r.refused:
        return [f"the fixture was refused: {r.refused}"]
    if r.params != [("fScale", "Float", 0), ("iMode", "Int", 1), ("vTint", "Vec4", 2)]:
        bad.append(f"lanes {r.params}, want fScale/Float/0, iMode/Int/1, vTint/Vec4/2")
    if "#define fScale (CxCpOn(0) ? CxCp(0).x : (2.0))" not in r.host:
        bad.append("fScale's default is not its own initialiser")
    if "#define vNever vec3(0)" not in r.host:
        bad.append("a vec3 is not held at its default")
    if "#define texMask uTex" not in r.host or "uniform sampler2D texUnit3" in r.host:
        bad.append("a custom sampler is not unit 0, or the supplied texUnit3 kept its declaration")
    if "vec3 vOwnVarying = vec3(0);" not in r.host or "float fVertexHeight" in r.host:
        bad.append("an unknown varying is not zeroed, or the supplied fVertexHeight kept its declaration")
    if not r.cuts:
        bad.append("an alpha write after the opener does not cut")
    if "void CxContentMain()" not in r.host or "gl_FragColor" in r.host:
        bad.append("main is not renamed or gl_FragColor survives")
    if "#define CX_CON_HAS_FixHelper" not in r.host or "float FixShared()" not in r.host:
        bad.append("a defined function is not guarded, or the include did not resolve")
    if not any("after ApplyStandardShader" in n for n in r.notes):
        bad.append("the post-apply edit is not noted")
    miss = translate("cx_fixture", lambda n: None if n == "cx_fixinc" else FIXTURE.get(n),
                     env, vanilla_defs, vanilla_env_fs, std_text, ahead)
    if not miss.refused or "cx_fixinc" not in miss.refused:
        bad.append("a missing include does not refuse the stage")
    return bad
