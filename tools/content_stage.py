"""CX-303 content pairs, offline: the translation ContentStage.Translate makes and the engine's own assembly.

check-shaders.py compiles every content stage it can find twice -- once the way GLRender builds it (the engine
prologue, its includes resolved from the content tree and the game's own base_shaders.bif), and once translated
into an Alloy lit program pair -- and fails when a stage the engine can build is one Alloy cannot. The engine
build is the independent oracle: it never passes through this file's translation.

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
MAX_LANES = 12
PRE_FN, POST_FN, VERTEX_FN = "CxContentMain", "CxContentPost", "CxContentVertexMain"
MAIN_FN = re.compile(r"\bvoid\s+main\s*\(\s*(?:void)?\s*\)")
DIRECTIVE = re.compile(r"^[ \t]*#[ \t]*(?:version|extension)\b[^\n]*", re.M)
COMMENTS = re.compile(r"//[^\n]*|/\*.*?\*/", re.S)
OWN_DEFINE = re.compile(r"^[ \t]*#[ \t]*define[ \t]+(\w+)", re.M)
PLT_DECL = re.compile(r"^[ \t]*uniform\b[^;\n]*\bPLTscheme\b[^;\n]*;[^\n]*\n?", re.M)
PLT_OPEN = re.compile(r"\bPLTscheme\s*\[")
BONES_OPEN = re.compile(r"\bm_bones\s*\[")
BONES_DECL = re.compile(r"^[ \t]*uniform\b[^;\n]*\bm_bones\b[^;\n]*;[^\n]*\n?", re.M)
PREC = r"(?:(?:lowp|mediump|highp)\s+)?"
LAYOUT = r"(?:layout\s*\([^)]*\)\s*)?"
INTERP_Q = r"(?:(?:flat|smooth|noperspective|centroid|invariant)\s+)*"
INTERP = re.compile(r"^\s*" + LAYOUT + INTERP_Q)
UNIFORM_DECL = re.compile(r"^\s*" + LAYOUT + r"uniform\s+" + PREC + r"(\w+)\s+(.+?)\s*;\s*$")
INPUT_DECL = re.compile(r"^\s*" + LAYOUT + INTERP_Q + r"(in|varying|attribute)\s+" + PREC + r"(\w+)\s+(.+?)\s*;\s*$")
OUTPUT_DECL = re.compile(r"^\s*" + LAYOUT + INTERP_Q + r"(out)\s+" + PREC + r"(\w+)\s+(.+?)\s*;\s*$")
DECLARATOR = re.compile(r"^\s*(\w+)\s*(\[[^\]]*\])?\s*(?:=\s*(.+))?$", re.S)
GLSL_TYPE = r"(?:void|float|double|int|uint|bool|[iubd]?vec[234]|d?mat[234](?:x[234])?)"
DECL_TAIL = r"\s+(\w+\s*(?:\[[^\]]*\])?\s*(?:(?:=|,)[^;\n]*)?);"
GLOBAL_DECL = re.compile(r"^[ \t]*()(?:(?:const|uniform|lowp|mediump|highp)\s+)*(" + GLSL_TYPE + r"|sampler\w*)" + DECL_TAIL, re.M)
ENV_DECL = re.compile(r"^(?:(?:const|uniform|lowp|mediump|highp)\s+)*(" + GLSL_TYPE + r"|sampler\w*)" + DECL_TAIL, re.M)
FN_DEF = re.compile(r"^[ \t]*(?:(?:lowp|mediump|highp|const)\s+)*" + GLSL_TYPE + r"\s+(\w+)\s*\(", re.M)
GUARD = re.compile(r"^[ \t]*#[ \t]*ifndef[ \t]+(\w+)[ \t]*\r?\n[ \t]*#[ \t]*define[ \t]+\1\b", re.M)
OWN_COVERAGE = re.compile(r"^[ \t]*#[ \t]*define[ \t]+(?:ENVIRONMENT_MAP|NO_TEXTURE)[ \t]+1\b", re.M)
KEYHOLE_DEFINE = re.compile(r"^[ \t]*#[ \t]*define[ \t]+KEYHOLING[ \t]+(\d+)", re.M)
ALPHA_WRITE = re.compile(r"\b(?:FragmentColor|gl_FragColor)\s*=\s*vec4\s*\(")
OPENER_RHS = re.compile(r"\s*1(?:\.0*)?\s*\)")
SWIZZLE_ALPHA = re.compile(r"\b(?:FragmentColor|gl_FragColor)\s*\.\s*[rgbaxyzw]*[aw][rgbaxyzw]*\s*[-+*/]?=(?!=)")
LIGHT_ARRAYS = re.compile(r"\b(?:numLights|lightPosition|lightColor|lightDiffuse|lightAmbient|lightQuadraticAtten)\b")
FED_BY_NAME = re.compile(r"^scriptable(?:Float|Int|Vec)\d+$")
OWN_ALPHA = re.compile(r"^\s*(?:(?:FragmentColor|gl_FragColor)\s*\.\s*[aw]|1(?:\.0*)?)\s*$")
APPLY_CALL = re.compile(r"\bApplyStandardShader\s*\(\s*\)\s*;")
APPLY_ANY = re.compile(r"\bApplyStandardShader\s*\(\s*\)")
LOCAL_DECL = re.compile(r"((?:(?:const|lowp|mediump|highp)\s+)*)([A-Za-z_]\w*)\s+([A-Za-z_]\w*\s*(?:\[[^\]]*\])?\s*(?:=[^;]*|,[^;]*)?)\s*;", re.S)
KEYWORD = re.compile(r"^(?:return|else|if|for|while|do|switch|case|discard|break|continue)$")


class Result:
    def __init__(self):
        self.host, self.params, self.notes, self.refused = "", [], [], None
        self.cuts = self.reads_plt = self.reads_lights = self.post = self.no_keyhole = False
        self.outputs = set()


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
    for t in (env, vanilla_defs, vanilla_env_fs if vanilla_defs else "", ahead):
        s.update(OWN_DEFINE.findall(t))
        for m in ENV_DECL.finditer(COMMENTS.sub(" ", t)):
            s.update(n for n, _, _ in items(m.group(2)))
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
    have = [p for p in ps if p[0] == name]
    if have:
        lane = have[0][2]
    elif len(ps) >= MAX_LANES:
        r.notes.append(f"declares more than {MAX_LANES} parameters -- {name} is held at its default")
        return f"#define {name} {fallback}\n"
    else:
        lane = len(ps)
        ps.append((name, kind, lane))
    value = {"Float": f"CxCp({lane}).x", "Int": f"int(CxCp({lane}).x)",
             "Bool": f"(CxCp({lane}).x != 0.0)", "Vec4": f"CxCp({lane})"}[kind]
    return f"#define {name} (CxCpOn({lane}) ? {value} : {fallback})\n"


def rewrite(code, line, fragment, supplied, vs_out, ps, macros, zeroed, r):
    m = UNIFORM_DECL.match(code)
    if m:
        return "".join(uniform(m.group(1), n, a, i, supplied, ps, macros, r) for n, a, i in items(m.group(2)))
    m = INPUT_DECL.match(code)
    inp = bool(m) and (fragment or m.group(1) != "varying")
    if not inp and not m:
        m = OUTPUT_DECL.match(code)
    out = not inp and bool(m)
    if not inp and not out:
        g = GLOBAL_DECL.match(code)
        return "" if g and any(n in supplied for n, _, _ in items(g.group(3))) else None
    typ = m.group(2)
    decls = list(items(m.group(3)))
    if out and fragment:
        if typ != "vec4" or len(decls) != 1:
            r.refused = f"declares a {typ} fragment output ({m.group(3).strip()})"
            return None
        macros.append(decls[0][0])
        return f"#define {decls[0][0]} FragmentColor"
    if out:
        keep = [d for d in decls if d[0] not in supplied]
        r.outputs.update(d[0] for d in keep)
        if len(keep) == len(decls):
            return re.sub(r"\bvarying\b", "out", line)
        return "".join(f"{INTERP.match(code).group(0)}out {typ} {n}{a or ''};\n" for n, a, _ in keep)
    res = []
    for n, a, _ in decls:
        if vs_out is not None and n in vs_out:
            res.append(f"{INTERP.match(code).group(0)}in {typ} {n}{a or ''};\n")
            continue
        if n in supplied:
            continue
        zeroed.append(n)
        res.append(f"{typ} {n}{a};\n" if a else f"{typ} {n} = {typ}(0);\n")
    return "".join(res)


def declarations(text, fragment, supplied, vs_out, ps, macros, r):
    out, zeroed, depth, block = [], [], 0, [False]
    for line in text.split("\n"):
        code = strip_line(line, block)
        braces = code.count("{") - code.count("}")
        if depth == 0 and not code.lstrip().startswith("#"):
            new = rewrite(code, line, fragment, supplied, vs_out, ps, macros, zeroed, r)
            if r.refused:
                return text
            if new is not None:
                out.append(new)
                depth += braces
                continue
        out.append(line)
        depth = max(depth + braces, 0)
    if zeroed:
        r.notes.append(f"reads {', '.join(zeroed)}, which its vertex half does not write -- held at zero" if fragment
                       else f"reads the attribute(s) {', '.join(zeroed)}, which no arena stream carries -- held at zero")
    return "\n".join(out) + "\n"


def indexed(text, use, call):
    out, pos = [], 0
    while True:
        m = use.search(text, pos)
        if not m:
            break
        depth, i = 1, m.end()
        while i < len(text) and depth > 0:
            depth += 1 if text[i] == "[" else -1 if text[i] == "]" else 0
            i += 1
        if depth != 0:
            break
        out.append(text[pos:m.start()] + call + text[m.end():i - 1] + ")")
        pos = i
    out.append(text[pos:])
    return "".join(out)


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


def blank(text):
    """Comments blanked to spaces, newlines kept: same length, same offsets (ContentStage.Blank)."""
    return COMMENTS.sub(lambda m: re.sub(r"[^\n]", " ", m.group(0)), text)


def matching(b, open_at):
    depth = 0
    for i in range(open_at, len(b)):
        if b[i] == "{":
            depth += 1
        elif b[i] == "}":
            depth -= 1
            if depth == 0:
                return i
    return -1


def skip(b, s, e):
    while s < e:
        while s < e and b[s].isspace():
            s += 1
        if s >= e or b[s] != "#":
            return s
        while s < e and b[s] != "\n":
            s += 1
    return s


def statements(b, frm, to):
    out, depth, paren, start = [], 0, 0, frm
    for i in range(frm, to):
        c = b[i]
        if c == "(":
            paren += 1
        elif c == ")":
            paren -= 1
        elif c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0 and paren == 0:
                out.append((skip(b, start, i + 1), i + 1)); start = i + 1
        elif c == ";" and depth == 0 and paren == 0:
            out.append((skip(b, start, i + 1), i + 1)); start = i + 1
    return out


def edits_after(b, frm, to):
    tail = re.sub(r"\bFragmentColor\s*=\s*FragmentColor\s*;", "", b[frm:to])
    tail = re.sub(r"^[ \t]*#[^\n]*", "", tail, flags=re.M)
    return len(tail.strip(" \t\r\n;{}")) > 0


def split_main(text, r):
    """Mirrors ContentStage.SplitMain."""
    head = re.search(r"\bvoid\s+" + PRE_FN + r"\s*\(\s*\)", text)
    if not head:
        return text
    b = blank(text)
    op = b.find("{", head.end())
    close = matching(b, op) if op >= 0 else -1
    if close < 0:
        r.refused = "its main() has no closing brace"
        return text
    b0 = op + 1
    stmts = statements(b, b0, close)
    apply = apply_end = -1
    for s, e in stmts:
        m = APPLY_CALL.match(b, s, e)
        if m and len(b[s:e].rstrip()) == m.end() - m.start():
            apply, apply_end = s, e
            break
    calls = bool(APPLY_ANY.search(b, b0, close))
    if apply < 0 and calls:
        if edits_after(b, APPLY_ANY.search(b, b0, close).end(), close):
            r.notes.append("edits its colour after an ApplyStandardShader() it makes inside a block, which under Alloy "
                           "lands on the surface colour before the lighting")
        return text
    if apply >= 0 and not edits_after(b, apply_end, close):
        return text
    post_from = apply_end if apply >= 0 else b0
    pre_to = apply if apply >= 0 else b0
    r.post = True
    if apply < 0:
        r.notes.append("never calls ApplyStandardShader(), so it lights itself: its colour is drawn as it leaves it")
    hoist, used = [], set(re.findall(r"(?<![.\w])[A-Za-z_]\w*", b[post_from:close]))
    pre, at = [], b0
    for s, e in stmts:
        if e > pre_to:
            break
        d = LOCAL_DECL.match(b, s, e)
        if not d or KEYWORD.match(d.group(2)) or len(b[s:e].rstrip()) != d.end() - d.start():
            continue
        decls = list(items(d.group(3)))
        if not any(n in used for n, _, _ in decls):
            continue
        pre.append(text[at:d.start()])
        keep = []
        for n, a, init in decls:
            if n not in used:
                keep.append(n + (a or "") + (" = " + init if init is not None else ""))
                continue
            hoist.append((d.group(2), n, a))
            if init is not None:
                pre.append(f"cxHoist_{n} = {init}; ")
        if keep:
            pre.append(d.group(1) + d.group(2) + " " + ", ".join(keep) + ";")
        at = d.end()
    pre.append(text[at:pre_to])
    pre_text, post = "".join(pre), text[post_from:close]
    for _, n, _ in hoist:
        word = re.compile(r"(?<![.\w])" + re.escape(n) + r"\b")
        pre_text = word.sub("cxHoist_" + n, pre_text)
        post = word.sub("cxHoist_" + n, post)
    return (text[:head.start()] + "".join(f"{ty} cxHoist_{n}{a or ''};\n" for ty, n, a in hoist)
            + f"void {PRE_FN}()\n{{" + pre_text + "\n}\n" + f"void {POST_FN}()\n{{" + post + "\n}" + text[close + 1:])


def stock_shaped(text, fragment):
    """Mirrors ContentStage.StockShaped."""
    code = COMMENTS.sub(" ", text)
    for n in re.findall(r'^[ \t]*#[ \t]*include[ \t]+"([^"]+)"', code, re.M):
        n = n.lower()
        if n not in ("inc_standard", "inc_scriptable") and n not in DROPPED:
            return False
    code = re.sub(r"^[ \t]*#[^\n]*", " ", code, flags=re.M)
    m = re.match(r"^\s*void\s+main\s*\(\s*(?:void)?\s*\)\s*\{(.*)\}\s*$", code, re.S)
    if not m:
        return False
    for s in m.group(1).split(";"):
        st = re.sub(r"\s+", "", s)
        if not st or st == "ApplyStandardShader()":
            continue
        if not fragment:
            return False
        if st in ("SetupStandardShaderInputs()", "FragmentColor=vec4(1.0)", "FragmentColor=vec4(1)",
                  "FragmentColor=COLOR_WHITE", "gl_FragColor=FragmentColor"):
            continue
        return False
    return True


def fragment_wrapper(r):
    w = ("void CxContentBody()\n{\n    cxConUV = CxHostUV;\n    cxConRow = CxContentRow(vDraw);\n"
         + ("    CxConFillLights();\n" if r.reads_lights else "")
         + "    fEnvMapLevel = 0.0;\n    fOcclusion = 1.0;\n"
         + f"    {PRE_FN}();\n    fSpecularity = MATERIAL_SPECULARITY_VALUE;\n"
         + "    fRoughness = MATERIAL_ROUGHNESS_VALUE;\n    fMetallicness = MATERIAL_METALLICNESS_VALUE;\n"
         + "    SpecularColor = MATERIAL_SPECULAR_COLOR_VALUE;\n}\n")
    if r.post:
        w += ("vec4 CxHostPost(vec4 c)\n{\n    FragmentColor = vec4(RevertColorSpace(c.rgb), c.a);\n"
              f"    {POST_FN}();\n    return vec4(ApplyColorSpace(FragmentColor.rgb), FragmentColor.a);\n}}\n")
    return w


VERTEX_WRAPPER = ("bool CxHostVertex(CxDraw d, int id, int bb)\n{\n"
                  "    cxConD = d;\n    cxConDraw = id;\n    cxConBone = bb;\n    cxConRow = CxContentRow(id);\n"
                  "    cxConMv = d.model;\n    cxConMv[3].xyz -= uEyePos;\n"
                  "    cxConMvp = cxVP * d.model;\n"
                  "    cxConProj = cxVP;\n    cxConProj[3] += cxVP * vec4(uEyePos, 0.0);\n"
                  "    vPosView = vWorld - uEyePos;\n    vPosWorld = vWorld;\n"
                  "    vVertexNormal = vNrm;\n    vVertexTangent = cxTanW;\n    fTextureHandedness = aTangent.w;\n"
                  "    vVertexTexCoords = vUV;\n"
                  f"    {VERTEX_FN}();\n"
                  "    vWorld = vPosView + uEyePos;\n"
                  "    vNrm = vVertexNormal;\n"
                  "    vec3 b = cross(vVertexNormal, vVertexTangent) * fTextureHandedness;\n"
                  "    vTan = vec4(d.rot.x * vVertexTangent - d.rot.y * b, fTextureHandedness);\n"
                  "    vUV = vVertexTexCoords;\n"
                  "    vDepth = gl_Position.w;\n"
                  "    return true;\n}\n")


def translate(resref, fragment, chain, env, vanilla_defs, vanilla_env_fs, std_text, ahead="", build=(8193, 37), vertex=None):
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
    r.reads_lights = fragment and bool(LIGHT_ARRAYS.search(flat))
    if fragment:
        r.cuts = bool(re.search(r"\bdiscard\b|\bAlphaDiscard\s*\(", flat)) or writes_alpha(flat) or bool(OWN_COVERAGE.search(flat))
        kh = KEYHOLE_DEFINE.findall(flat)
        r.no_keyhole = bool(kh) and int(kh[-1]) == 0
    supplied = supplied_names(env, vanilla_defs, vanilla_env_fs, ahead)
    undef = []
    if vertex is not None:
        for o in sorted(vertex.outputs):
            if o in supplied:
                supplied.discard(o)
                undef.append(o)
    text = MAIN_FN.sub(f"void {PRE_FN if fragment else VERTEX_FN}()", text)
    if fragment:
        text = re.sub(r"\bgl_FragColor\b", "FragmentColor", text)
    text = PLT_DECL.sub("", text)
    text = indexed(text, PLT_OPEN, "CxPltScheme(vDraw, " if fragment else "CxPltScheme(cxConDraw, ")
    text = BONES_DECL.sub("", text)
    text = indexed(text, BONES_OPEN, "CxConBoneAt(")
    ps = list(vertex.params) if vertex is not None else []
    macros = []
    text = declarations(text, fragment, supplied, vertex.outputs if vertex is not None else None, ps, macros, r)
    if r.refused:
        return r
    r.params = ps
    if fragment:
        text = split_main(text, r)
        if r.refused:
            return r
    guards = set(GUARD.findall(text))
    own = [n for n in dict.fromkeys(OWN_DEFINE.findall(text)) if n not in guards]
    fns = list(dict.fromkeys(FN_DEF.findall(COMMENTS.sub(" ", text))))
    env_defs = list(dict.fromkeys(OWN_DEFINE.findall(env)))
    und = lambda names: "".join(f"#undef {n}\n" for n in names)
    host = ((f"// ---- content stage {resref}\n" if fragment else f"// ---- content vertex stage {resref}\n")
            + f"#define BUILD_VERSION {build[0]}\n#define BUILD_REVISION {build[1]}\n"
            + "".join(f"#define CX_CON_HAS_{f}\n" for f in fns)
            + env + ("" if env.endswith("\n") else "\n")
            + und(own + undef) + text + "\n"
            + (fragment_wrapper(r) if fragment else VERTEX_WRAPPER)
            + und(own) + und(env_defs + macros + [f"CX_CON_HAS_{f}" for f in fns])
            + "#undef BUILD_VERSION\n#undef BUILD_REVISION\n")
    if fragment:
        host += (vanilla_defs + ("" if vanilla_defs.endswith("\n") else "\n")
                 + "#define CX_CONTENT_PROGRAM 1\n" + ("#define CX_CONTENT_POST 1\n" if r.post else "")
                 + "bool CxHostSurface(int feat)\n{\n    if (feat < CX_FEAT_CONTENT0) return false;\n"
                 + "    CxContentBody();\n    return true;\n}\n")
    r.host = host
    return r


# --- the shared fixture (ContentStage.SelfCheck holds the same facts) ---------------------------------------
FIXTURE = {
    "cx_fixture": ('#define SHADER_TYPE 2\n#define LIGHTING 1\n#include "inc_standard"\n#include "cx_fixinc"\n'
                   "uniform float fScale = 2.0;\nuniform int iMode;\nuniform vec4 vTint;\nuniform vec3 vNever;\n"
                   "uniform sampler2D texUnit3;\nuniform sampler2D texMask;\nvarying float fVertexHeight;\n"
                   "in vec3 vOwnVarying;\nin vec2 vNobody;\nfloat FixHelper(float x) { return x * fScale; }\n"
                   "void main ()\n{\n    FragmentColor = vec4(1.0);\n    SetupStandardShaderInputs();\n"
                   "    FragmentColor.rgb *= vTint.rgb * FixHelper(fVertexHeight) + vOwnVarying + vNobody.x;\n"
                   "    ApplyStandardShader();\n    gl_FragColor = vec4(FragmentColor.rgb, texture2D(texUnit3, vTexCoords).r);\n}\n"),
    "cx_fixinc": "#ifndef FIX_INC\n#define FIX_INC\nfloat FixShared() { return 1.0; }\n#endif\n",
    "cx_fixvs": ('#define SHADER_TYPE 1\n#include "inc_standard"\nuniform float fSway;\nuniform float fScale = 2.0;\n'
                 "uniform highp mat4 m_bones[MAX_NUM_BONES];\n"
                 "out float fVertexHeight;\nvarying vec3 vOwnVarying;\nvarying vec3 vPosView;\n"
                 "void main()\n{\n    ApplyStandardShader();\n    fVertexHeight = (m_m * vPos).z;\n"
                 "    vOwnVarying = (m_bones[int(vIndex.x)] * vPos).xyz * fSway;\n}\n"),
    "cx_fixpost": ('#define KEYHOLING 0\n#include "inc_standard"\nvoid main()\n{\n    FragmentColor = vec4(1.0);\n'
                   "    SetupStandardShaderInputs();\n    float glow = 0.5, other = 1.0;\n    FragmentColor.rgb *= other;\n"
                   "    ApplyStandardShader();\n    FragmentColor.rgb += vec3(glow) * float(numLights);\n"
                   "    gl_FragColor = FragmentColor;\n}\n"),
    "cx_fixwhole": "void main()\n{\n    gl_FragColor = vec4(1.0, 0.0, 0.0, 1.0);\n}\n",
}
STOCK_VS = ('// header\n#define SHADER_TYPE 1\n#define NORMAL_MAP 1\n#include "inc_standard"\n'
            "void main ()\n{\n    ApplyStandardShader();\n}\n")
STOCK_FS = ('#define SHADER_TYPE 2\n#define NORMAL_MAP 1\n#include "inc_standard"\nvoid main ()\n{\n'
            "    FragmentColor = vec4(1.0);\n    SetupStandardShaderInputs();\n    ApplyStandardShader();\n"
            "    gl_FragColor = FragmentColor;\n}\n")


def selfcheck(env_fs, env_vs, defs, vanfs, std_fs, std_vs, lit_vs):
    """The fixture's facts, hand-written: the oracle is this text, not the translation."""
    bad = []
    vs = translate("cx_fixvs", False, FIXTURE.get, env_vs, "", "", std_vs, lit_vs)
    r = translate("cx_fixture", True, FIXTURE.get, env_fs, defs, vanfs, std_fs, "", vertex=vs)
    if vs.refused or r.refused:
        return [f"a fixture was refused: {vs.refused or r.refused}"]
    if vs.params != [("fSway", "Float", 0), ("fScale", "Float", 1)]:
        bad.append(f"vertex lanes {vs.params}")
    if r.params != [("fSway", "Float", 0), ("fScale", "Float", 1), ("iMode", "Int", 2), ("vTint", "Vec4", 3)]:
        bad.append(f"pair lanes {r.params}")
    if "#define fScale (CxCpOn(1) ? CxCp(1).x : (2.0))" not in r.host:
        bad.append("fScale does not read its vertex half's lane")
    if vs.outputs != {"fVertexHeight", "vOwnVarying"}:
        bad.append(f"vertex outputs {vs.outputs}")
    if "out float fVertexHeight;" not in vs.host or "out vec3 vOwnVarying;" not in vs.host or "out vec3 vPosView" in vs.host:
        bad.append("a vertex half's own varying is not an output, or a stock one kept its declaration")
    if "CxConBoneAt(int(vIndex.x))" not in vs.host or "mat4 m_bones" in vs.host \
       or "CxConBoneAt(MAX_NUM_BONES)" in vs.host or "void CxContentVertexMain()" not in vs.host:
        bad.append("m_bones is not rewritten, or main is not renamed")
    if "#undef fVertexHeight" not in r.host or "in float fVertexHeight;" not in r.host \
       or "in vec3 vOwnVarying;" not in r.host or "vec2 vNobody = vec2(0);" not in r.host:
        bad.append("varyings do not cross the pair as themselves")
    if "#define vNever vec3(0)" not in r.host or "#define texMask uTex" not in r.host or "uniform sampler2D texUnit3" in r.host:
        bad.append("a vec3 or sampler declaration is not mapped")
    if not r.cuts or not r.post or "void CxContentPost()" not in r.host or "#define CX_CONTENT_POST 1" not in r.host \
       or "gl_FragColor" in r.host:
        bad.append("the fixture does not cut, or does not post")
    if "#define CX_CON_HAS_FixHelper" not in r.host or "float FixShared()" not in r.host:
        bad.append("a defined function is not guarded, or the include did not resolve")
    miss = translate("cx_fixture", True, lambda n: None if n == "cx_fixinc" else FIXTURE.get(n),
                     env_fs, defs, vanfs, std_fs, "")
    if not miss.refused or "cx_fixinc" not in miss.refused:
        bad.append("a missing include does not refuse the stage")
    post = translate("cx_fixpost", True, FIXTURE.get, env_fs, defs, vanfs, std_fs, "")
    if post.refused or not post.post or post.cuts or not post.no_keyhole or not post.reads_lights:
        bad.append("the post fixture's facts")
    for s in ("float cxHoist_glow;", "cxHoist_glow = 0.5;", "float other = 1.0;",
              "FragmentColor.rgb += vec3(cxHoist_glow)", "CxConFillLights();"):
        if s not in post.host:
            bad.append(f"the post fixture lacks {s!r}")
    whole = translate("cx_fixwhole", True, FIXTURE.get, env_fs, defs, vanfs, std_fs, "")
    if whole.refused or not whole.post or whole.cuts or "void CxContentMain()\n{\n}" not in whole.host:
        bad.append("a whole stage is not all post body")
    if not stock_shaped(STOCK_VS, False) or not stock_shaped(STOCK_FS, True) or stock_shaped(FIXTURE["cx_fixvs"], False) \
       or stock_shaped(STOCK_FS.replace('#include "inc_standard"', '#include "my_inc"'), True) \
       or stock_shaped(STOCK_FS.replace("ApplyStandardShader();", "ApplyStandardShader();\n    FragmentColor.r = 1.0;"), True):
        bad.append("stock shape is misread")
    return bad
