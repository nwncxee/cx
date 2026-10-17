#!/usr/bin/env python3
"""Render a content sky offline, the way Alloy composes it, into PNGs.

    tools/sky/preview.py --scene 'month=3 day=20 hour=17.9 cover=0.3 yaw=268 pitch=6' -o dusk.png
    tools/sky/preview.py --sheet tools/sky/sheet.txt -o out/              # one PNG per line + a contact sheet
    tools/sky/preview.py --compare --sheet tools/sky/sheet.txt -o out/    # the stage beside its cx_ twin
    tools/sky/preview.py --bench 30 --size 2560x1440 --sheet ... -o out/  # median GPU time per pass
    tools/sky/preview.py --coverage skyPano --scene 'hour=12'              # cloud share overhead per cover

The sky is drawn from the real pieces: the dome mesh out of the material's ASCII .mdl, its textures, the embedded
cx_sky_fs wrapper with the content stage spliced into its region (check-shaders.py's own splice), and -- for an
Alloy-native twin (cx_<stage>) -- the images and compute passes its `#pragma cx_sky_*` lines declare, run through
cx_skypass_cs. Frame passes run --frames times per scene with the camera held still, so temporal accumulation has
converged the way it would in a held view. The frame is then presented the way the resolve presents a sky pixel:
its own value plus the bloom bleed, encoded, no exposure and no tonemap.

Needs the NVIDIA EGL device (headless), numpy and Pillow. Content resolves from --content (default: the
CONTENT_SHADERS path, tools/paths.py) and then from the game's own shaders.
"""
import argparse
import ctypes as C
import importlib.util
import math
import os
import re
import sys

import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(ROOT, "tools"))
import paths  # noqa: E402


def load_check_shaders(content):
    os.environ["CX_CONTENT_SHD"] = content
    spec = importlib.util.spec_from_file_location("check_shaders", os.path.join(ROOT, "check-shaders.py"))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


# --- the declaration grammar (docs/shader-overrides.md, "Sky twins and sky passes") --------------------------------
# One grammar, one parser: check-shaders.py's, which is itself pinned to CX.Alloy.SkyTwin's. This only turns
# what it returns into the shape this tool indexes by.
KINDS = {"tex2d": "2d", "tex3d": "3d", "view": "view"}


def sky_decls(cs, text):
    """({name: {kind, size, fmt, flags}}, [(once|frame, resref, image)]) for a twin's declarations."""
    images, passes = cs.sky_decls(text)
    out = {}
    for i in images:
        kind = KINDS[i["shape"]]
        size = [i["div"]] if kind == "view" else ([i["w"], i["h"], i["d"]] if kind == "3d" else [i["w"], i["h"]])
        out[i["name"]] = dict(kind=kind, size=size, fmt=i["format"],
                              flags={f for f in ("history", "mips", "clamp") if i[f]})
    return out, [("once" if p["once"] else "frame", p["ref"], p["image"]) for p in passes]


# --- GL through the headless EGL context check-shaders.py makes ----------------------------------------------------
class GL:
    def __init__(self, fn):
        u, i, f, p, vp = C.c_uint, C.c_int, C.c_float, C.c_char_p, C.c_void_p
        sig = {
            "glCreateShader": (u, u), "glShaderSource": (None, u, i, C.POINTER(p), vp), "glCompileShader": (None, u),
            "glGetShaderiv": (None, u, u, C.POINTER(i)), "glGetShaderInfoLog": (None, u, i, vp, p),
            "glCreateProgram": (u,), "glAttachShader": (None, u, u), "glLinkProgram": (None, u),
            "glGetProgramiv": (None, u, u, C.POINTER(i)), "glGetProgramInfoLog": (None, u, i, vp, p),
            "glUseProgram": (None, u), "glGetUniformLocation": (i, u, p),
            "glUniform1i": (None, i, i), "glUniform1f": (None, i, f), "glUniform2i": (None, i, i, i),
            "glUniform2f": (None, i, f, f), "glUniform3f": (None, i, f, f, f), "glUniform4f": (None, i, f, f, f, f),
            "glUniformMatrix4fv": (None, i, i, C.c_ubyte, C.POINTER(f)),
            "glGetProgramResourceIndex": (u, u, u, p),
            "glGetProgramResourceiv": (None, u, u, u, i, C.POINTER(u), i, C.POINTER(i), C.POINTER(i)),
            "glCreateTextures": (None, u, i, C.POINTER(u)), "glTextureStorage2D": (None, u, i, u, i, i),
            "glTextureStorage3D": (None, u, i, u, i, i, i), "glTextureSubImage2D": (None, u, i, i, i, i, i, u, u, vp),
            "glTextureParameteri": (None, u, u, i), "glGenerateTextureMipmap": (None, u),
            "glBindTextureUnit": (None, u, u), "glBindImageTexture": (None, u, u, i, C.c_ubyte, i, u, u),
            "glGetTextureImage": (None, u, i, u, u, i, vp), "glDeleteTextures": (None, i, C.POINTER(u)),
            "glCreateFramebuffers": (None, i, C.POINTER(u)), "glNamedFramebufferTexture": (None, u, u, u, i),
            "glBindFramebuffer": (None, u, u), "glViewport": (None, i, i, i, i),
            "glClearColor": (None, f, f, f, f), "glClear": (None, u), "glEnable": (None, u), "glDisable": (None, u),
            "glCreateVertexArrays": (None, i, C.POINTER(u)), "glCreateBuffers": (None, i, C.POINTER(u)),
            "glNamedBufferData": (None, u, C.c_ssize_t, vp, u),
            "glVertexArrayVertexBuffer": (None, u, u, u, C.c_ssize_t, i),
            "glVertexArrayAttribFormat": (None, u, u, i, u, C.c_ubyte, u),
            "glVertexArrayAttribBinding": (None, u, u, u), "glEnableVertexArrayAttrib": (None, u, u),
            "glBindVertexArray": (None, u), "glDrawArrays": (None, u, i, i),
            "glDispatchCompute": (None, u, u, u), "glMemoryBarrier": (None, u), "glFinish": (None,),
            "glGetError": (u,), "glGenQueries": (None, i, C.POINTER(u)), "glBeginQuery": (None, u, u),
            "glEndQuery": (None, u), "glGetQueryObjectui64v": (None, u, u, C.POINTER(C.c_uint64)),
        }
        for name, (res, *args) in sig.items():
            h = fn(name, res, *args)
            if h is None:
                sys.exit(f"no {name} on this context")
            setattr(self, name[2:], h)

    def gen(self, call, *a):
        x = C.c_uint(0)
        call(*a, 1, C.byref(x))
        return x.value

    def program(self, stages, what):
        prog = self.CreateProgram()
        for kind, src in stages:
            s = self.CreateShader(kind)
            buf = C.c_char_p(src.encode("latin1"))
            self.ShaderSource(s, 1, C.byref(buf), None)
            self.CompileShader(s)
            ok = C.c_int(0)
            self.GetShaderiv(s, 0x8B81, C.byref(ok))   # not-an-offset: GL_COMPILE_STATUS
            if not ok.value:
                log = C.create_string_buffer(16384)
                self.GetShaderInfoLog(s, 16384, None, log)
                dump = os.path.join(os.environ.get("TMPDIR", "/tmp"), re.sub(r"\W", "_", what) + ".glsl")
                open(dump, "w", encoding="latin1").write(src)
                sys.exit(f"{what}: compile failed (source in {dump})\n" + log.value.decode(errors="replace")[:4000])
            self.AttachShader(prog, s)
        self.LinkProgram(prog)
        ok = C.c_int(0)
        self.GetProgramiv(prog, 0x8B82, C.byref(ok))   # not-an-offset: GL_LINK_STATUS
        if not ok.value:
            log = C.create_string_buffer(16384)
            self.GetProgramInfoLog(prog, 16384, None, log)
            sys.exit(f"{what}: link failed\n" + log.value.decode(errors="replace")[:4000])
        return prog

    def loc(self, prog, name):
        return self.GetUniformLocation(prog, name.encode())

    def utype(self, prog, name):
        """The GL type of a uniform, or None when the program does not use it."""
        idx = self.GetProgramResourceIndex(prog, 0x92E1, name.encode())   # not-an-offset: GL_UNIFORM
        if idx == 0xFFFFFFFF:
            return None
        prop, out = C.c_uint(0x92FA), C.c_int(0)   # not-an-offset: GL_TYPE
        self.GetProgramResourceiv(prog, 0x92E1, idx, 1, C.byref(prop), 1, None, C.byref(out))   # not-an-offset: GL_UNIFORM
        return out.value


TEX2D, TEX3D = 0x0DE1, 0x806F   # not-an-offset: GL enums
LINEAR, LMIPL, REPEAT, CLAMP = 0x2601, 0x2703, 0x2901, 0x812F   # not-an-offset: GL enums
MIN, MAG, WS, WT, WR = 0x2801, 0x2800, 0x2802, 0x2803, 0x8072   # not-an-offset: GL enums


def is_image_type(t):
    return 0x904C <= t <= 0x906B   # not-an-offset: the GL_IMAGE_* / GL_INT_IMAGE_* / GL_UNSIGNED_INT_IMAGE_* type range


# --- scene inputs ---------------------------------------------------------------------------------------------------
# The scene vocabulary. year..hour, yaw/pitch/fov, eyez (the eye's height over the dome's centre, metres), t (the
# client clock, seconds) and main (0 = a probe face) are the engine's and the camera's. cover, storm, tint, phase,
# moon, armed, flash, flashaz, comet, cometseed and noir are the scriptable slots the REFERENCE content's sky reads
# (Shadows over Westgate: slots 4, 9, 10, 11, 12, 16 -- see uniforms() below), and aim=sun|moon points the camera
# along its inc_skymath model. Another sky sets its own uniforms raw: u.<name>=x[,y[,z[,w]]] for a float/vec,
# i.<name>=n for an int (e.g. u.scriptableVec3=0,0,1,0).
DEFAULTS = dict(year=1372, month=6, day=15, hour=12.0, cover=0.0, storm=0.0, phase=0.5, tint=(0, 0, 0),
                flash=0.0, flashaz=0.0, comet=-1.0, cometseed=0.37, noir=0, yaw=180.0, pitch=15.0, fov=70.0,
                t=3600.0, moon=1, armed=1, aim="", main=1, eyez=8.0)


def parse_scene(text):
    s = dict(DEFAULTS, raw=[])
    for tok in text.split():
        k, _, v = tok.partition("=")
        if k.startswith(("u.", "i.")):
            s["raw"].append((k[0], k[2:], [float(x) for x in v.split(",")]))
            continue
        if k not in s:
            sys.exit(f"unknown scene key {k} (have: {', '.join(s)})")
        s[k] = (tuple(float(x) for x in v.split(",")) if k == "tint" else v if k == "aim"
                else int(v) if isinstance(s[k], int) else float(v))
    return s


LAT, TILT = 0.6109, 0.4091   # inc_skymath's SUN_LATITUDE / SUN_TILT


def body_dir(s, which):
    """inc_skymath's SunDir / MoonDir, for aiming the preview camera at a body."""
    doy = (s["month"] - 1) * 30 + (s["day"] - 1)
    hour, phase = s["hour"], (s["phase"] % 1.0 if which == "moon" else 0.0)
    decl = -TILT * math.cos(6.28319 * (doy + phase * 360.0 + 10.0) / 360.0)
    if which == "moon":
        days = (s["year"] - 1358) * 360.0 + doy - 329.0
        decl += 0.35 * math.sin(6.28319 * (((days * 24.0 + hour) / 658.596) % 1.0))
    H = (hour - 12.0) / 12.0 * math.pi - phase * 6.2831853
    se = math.sin(LAT) * math.sin(decl) + math.cos(LAT) * math.cos(decl) * math.cos(H)
    d = np.array([-math.sin(H) * math.cos(decl), (math.sin(decl) - se * math.sin(LAT)) / math.cos(LAT), se])
    return d / np.linalg.norm(d)


def look(yaw, pitch):
    """World direction for a compass yaw (0 = north, 90 = east) and a pitch above the horizon, degrees."""
    y, p = math.radians(yaw), math.radians(pitch)
    return np.array([math.sin(y) * math.cos(p), math.cos(y) * math.cos(p), math.sin(p)])


def view_proj(eye, fwd, fov, aspect):
    f = fwd / np.linalg.norm(fwd)
    up = np.array([0.0, 0.0, 1.0]) if abs(f[2]) < 0.99 else np.array([0.0, 1.0, 0.0])
    r = np.cross(f, up); r /= np.linalg.norm(r)
    u = np.cross(r, f)
    view = np.identity(4)
    view[0, :3], view[1, :3], view[2, :3] = r, u, -f
    view[:3, 3] = -view[:3, :3] @ eye
    n, fa = 0.5, 5000.0
    t = 1.0 / math.tan(math.radians(fov) / 2)
    proj = np.zeros((4, 4))
    proj[0, 0], proj[1, 1] = t / aspect, t
    proj[2, 2], proj[2, 3], proj[3, 2] = (fa + n) / (n - fa), 2 * fa * n / (n - fa), -1.0
    return proj, proj @ view


def mat(m):
    """A numpy 4x4 (math convention, M @ v) as the column-major floats glUniformMatrix4fv reads untransposed."""
    return (C.c_float * 16)(*np.asarray(m, dtype=np.float64).T.flatten())


# --- the dome --------------------------------------------------------------------------------------------------------
def load_mdl(path):
    """The first trimesh of an ASCII .mdl: (triangle positions incl. the node's position, triangle uvs)."""
    lines = open(path, encoding="latin1").read().split("\n")
    pos, verts, tverts, faces = np.zeros(3), [], [], []
    i = 0
    while i < len(lines):
        t = lines[i].split()
        if t[:1] == ["position"]:
            pos = np.array([float(x) for x in t[1:4]])
        elif t[:1] in (["verts"], ["tverts"], ["faces"]):
            n, which = int(t[1]), t[0]
            for line in lines[i + 1:i + 1 + n]:
                v = line.split()
                if which == "verts":
                    verts.append([float(x) for x in v[:3]])
                elif which == "tverts":
                    tverts.append([float(x) for x in v[:2]])
                else:
                    faces.append([int(x) for x in v[:8]])
            i += n
        elif t[:1] == ["endnode"] and verts:
            break
        i += 1
    verts, tverts = np.array(verts) + pos, np.array(tverts)
    p = np.array([[verts[f[k]] for k in range(3)] for f in faces]).reshape(-1, 3)
    uv = np.array([[tverts[f[4 + k]] for k in range(3)] for f in faces]).reshape(-1, 2)
    return p.astype(np.float32), uv.astype(np.float32)


def read_mtr(path):
    out = {}
    for line in open(path, encoding="latin1"):
        t = line.split("//")[0].split()
        if len(t) >= 2:
            out[t[0].lower()] = t[1].lower()
    return out


VS = """#version 460 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUV;
uniform mat4 uVP;
uniform mat4 uModel;
out vec2 vUV;
flat out float vAlpha;
out vec3 vWorld;
void main()
{
    vec4 w = uModel * vec4(aPos, 1.0);
    gl_Position = uVP * w;
    vUV = aUV;
    vAlpha = 1.0;
    vWorld = w.xyz;
}
"""


class Sky:
    def __init__(self, gl, cs, content, stage, twin, size, assets):
        self.gl, self.cs, self.content, self.size = gl, cs, content, size
        self.g = cs.shaders()
        self.read = lambda name: self.find(name)
        self.stage = stage
        self.twin = twin
        self.images_decl, self.passes_decl = sky_decls(cs, self.read(stage) or "") if twin else ({}, [])
        fs = self.wrap("cx_sky_f", "$SKYFS$", stage, "CX_SKY_LINEAR 1" if twin else None)
        self.prog = gl.program([(0x8B31, VS), (0x8B30, fs)], stage)   # not-an-offset: GL_VERTEX_SHADER, GL_FRAGMENT_SHADER
        self.images = {}
        self.passes = []
        for when, ref, img in self.passes_decl:
            src = self.wrap("cx_skypass_cs", "$SKYCS$", ref, None)
            p = gl.program([(0x91B9, src)], ref)   # not-an-offset: GL_COMPUTE_SHADER
            grp = (C.c_int * 3)()
            gl.GetProgramiv(p, 0x8267, grp)   # not-an-offset: GL_COMPUTE_WORK_GROUP_SIZE
            self.passes.append((when, ref, img, p, tuple(grp)))
        self.assets = assets
        self.fresh = True
        self.once_done = False
        self.frame = 0
        self.prev_vp = None

    def find(self, name):
        for base in (self.content, self.cs.STOCK_SHD):
            for root, _, files in os.walk(base):
                if name + ".shd" in files:
                    return open(os.path.join(root, name + ".shd"), encoding="latin1").read()
        return None

    def wrap(self, wrapper, token, resref, define):
        src = self.g[wrapper].replace(token, resref)
        if define:
            at = src.index("\n", src.index("#version")) + 1
            src = src[:at] + f"#define {define}\n" + src[at:]
        m = self.cs.VANILLA.search(src)
        stock = self.read(resref)
        if stock is None:
            sys.exit(f"{resref}.shd not found under {self.content} or the game's shaders")
        body = self.cs.main_text(stock, {resref}, self.g, src[:m.start()], self.read)
        if body is None:
            sys.exit(f"{resref}.shd has no main()")
        src = src[:m.start()] + m.group(1) + f"// $VANILLA-MAIN {resref}\n" + body + src[m.end():]
        for tok, val in self.cs.DRIVER_TOKENS.items():
            src = src.replace(tok, val)
        return src

    def fmt(self, im):
        """The declared format's GL sized internal format, out of check-shaders' own table."""
        return self.cs.SKY_FORMAT_ENUM[im["fmt"]][0]

    def make_images(self):
        gl = self.gl
        for name, d in self.images_decl.items():
            if d["kind"] == "view":
                n = d["size"][0]
                dims = [-(-self.size[0] // n), -(-self.size[1] // n)]
            else:
                dims = d["size"]
            three = d["kind"] == "3d"
            levels = (int(math.log2(max(dims))) + 1) if "mips" in d["flags"] else 1
            names = []
            for _ in range(2 if "history" in d["flags"] else 1):
                t = gl.gen(gl.CreateTextures, TEX3D if three else TEX2D)
                if three:
                    gl.TextureStorage3D(t, levels, self.fmt(d), *dims)
                else:
                    gl.TextureStorage2D(t, levels, self.fmt(d), *dims)
                wrap = CLAMP if (d["kind"] == "view" or "clamp" in d["flags"]) else REPEAT
                for w in (WS, WT, WR):
                    gl.TextureParameteri(t, w, wrap)
                gl.TextureParameteri(t, MIN, LMIPL if levels > 1 else LINEAR)
                gl.TextureParameteri(t, MAG, LINEAR)
                names.append(t)
            self.images[name] = dict(d, dims=dims, tex=names, levels=levels)
        self.fresh = True

    def bind(self, prog):
        """Every declared image the program reads or writes, by name and by the uniform's own type."""
        gl = self.gl
        unit, iunit = 4, 0
        for name, im in self.images.items():
            for uname, tex in ((name, im["tex"][0]), (name + "_prev", im["tex"][-1])):
                if uname.endswith("_prev") and len(im["tex"]) < 2:
                    continue
                t = gl.utype(prog, uname)
                if t is None:
                    continue
                if is_image_type(t):
                    gl.BindImageTexture(iunit, tex, 0, 1 if im["kind"] == "3d" else 0, 0, 0x88BA, self.fmt(im))   # not-an-offset: GL_READ_WRITE
                    gl.Uniform1i(gl.loc(prog, uname), iunit)
                    iunit += 1
                else:
                    gl.BindTextureUnit(unit, tex)
                    gl.Uniform1i(gl.loc(prog, uname), unit)
                    unit += 1

    def uniforms(self, prog, s, eye, vp, inv_clean, proj, main):
        gl = self.gl
        L = lambda n: gl.loc(prog, n)
        hour = s["hour"]
        ihour = int(hour)
        def i1(n, v): gl.Uniform1i(L(n), int(v))
        def f1(n, v): gl.Uniform1f(L(n), float(v))
        def v4(n, *v): gl.Uniform4f(L(n), *[float(x) for x in v])
        def v3(n, *v): gl.Uniform3f(L(n), *[float(x) for x in v])
        i1("uModYear", s["year"]); i1("uModMonth", s["month"]); i1("uModDay", s["day"]); i1("uModHour", ihour)
        i1("uModDawn", 6); i1("uModDusk", 19)
        # the server's glide clock: pushed hour + rate * stopwatch, with the stopwatch at 0
        f1("uModTransition", 0.0); f1("uWeatherDensity", 0.0)
        i1("uTimeOfDay", int(s["t"] * 1000.0) % 86400000)
        v4("scriptableVec10", (s["phase"] if s["moon"] else -1.0), hour, 1.0, 1.0 if s["armed"] else 0.0)
        if not s["armed"]:
            v4("scriptableVec10", 0, 0, 0, 0)
        v4("scriptableVec11", 0, 0, 0, s["cover"] + 1.0)
        v4("scriptableVec12", *s["tint"], s["storm"])
        v4("scriptableVec15", 0.0, 0.0, 0, 0)
        v4("scriptableVec16", s["flash"], s["flashaz"], 0, 0)
        i1("scriptableInt9", 1 if s["comet"] >= 0 else 0)
        v4("scriptableVec9", max(s["comet"], 0.0), s["cometseed"], 0, 1.0)
        i1("scriptableInt4", s["noir"])
        gl.UniformMatrix4fv(L("uVP"), 1, 0, mat(vp))
        gl.UniformMatrix4fv(L("m_proj"), 1, 0, mat(proj))
        vi = np.identity(4); vi[:3, 3] = eye
        gl.UniformMatrix4fv(L("uViewInv"), 1, 0, mat(vi))
        v3("uEyePos", *eye); v3("uCamPos", *eye); v3("uPlayerPos", *(eye - [0, 0, 8])); v3("uCamFocus", *(eye - [0, 0, 8]))
        i1("uTex", 0); i1("texUnit1", 1); v3("uTint", 1, 1, 1); f1("uSkyFog", 0.0); v4("globalColor", 1, 1, 1, 1)
        i1("uOit", 0); f1("uLodBias", 0.0); v4("uFogParams", 0, 0, 0, 0)
        i1("cxSkyFrame", self.frame); i1("cxSkyMain", 1 if main else 0); i1("cxSkyFresh", 1 if self.fresh else 0)
        gl.UniformMatrix4fv(L("cxSkyInvVp"), 1, 0, mat(inv_clean))
        gl.UniformMatrix4fv(L("cxSkyPrevVp"), 1, 0, mat(self.prev_vp if self.prev_vp is not None else np.linalg.inv(inv_clean)))
        v3("cxSkyEye", *eye); gl.Uniform2i(L("cxSkyView"), *self.size)
        for kind, name, vals in s["raw"]:
            if kind == "i":
                i1(name, vals[0])
            elif len(vals) == 1:
                f1(name, vals[0])
            else:
                getattr(gl, f"Uniform{len(vals)}f")(L(name), *vals)

    def run(self, when, s, eye, vp, proj, timings=None):
        gl = self.gl
        inv = np.linalg.inv(vp)
        for w, ref, img, p, grp in self.passes:
            if w != when:
                continue
            if timings is not None:
                q = gl.gen(gl.GenQueries)
                gl.BeginQuery(0x88BF, q)   # not-an-offset: GL_TIME_ELAPSED
            gl.UseProgram(p)
            self.uniforms(p, s, eye, vp, inv, proj, True)
            self.bind(p)
            dims = list(self.images[img]["dims"]) + [1, 1]
            gl.DispatchCompute(*[-(-dims[k] // grp[k]) for k in range(3)])
            gl.MemoryBarrier(0xFFFFFFFF)   # not-an-offset: GL_ALL_BARRIER_BITS
            for name, im in self.images.items():
                if im["levels"] > 1 and gl.utype(p, name) is not None and is_image_type(gl.utype(p, name)):
                    gl.GenerateTextureMipmap(im["tex"][0])
            if timings is not None:
                gl.EndQuery(0x88BF)   # not-an-offset: GL_TIME_ELAPSED
                timings.append((ref, q))

    def swap_history(self):
        for im in self.images.values():
            if len(im["tex"]) == 2:
                im["tex"].reverse()


def load_texture(gl, path):
    im = Image.open(path).convert("RGBA")
    w, h = im.size
    t = gl.gen(gl.CreateTextures, TEX2D)
    levels = int(math.log2(max(w, h))) + 1
    gl.TextureStorage2D(t, levels, 0x8058, w, h)   # not-an-offset: GL_RGBA8
    data = im.tobytes()
    gl.TextureSubImage2D(t, 0, 0, 0, w, h, 0x1908, 0x1401, data)   # not-an-offset: GL_RGBA, GL_UNSIGNED_BYTE
    gl.GenerateTextureMipmap(t)
    gl.TextureParameteri(t, MIN, LMIPL); gl.TextureParameteri(t, MAG, LINEAR)
    gl.TextureParameteri(t, WS, REPEAT); gl.TextureParameteri(t, WT, CLAMP)
    gl.TextureParameteri(t, 0x84FE, 8)   # not-an-offset: GL_TEXTURE_MAX_ANISOTROPY
    return t


def find_asset(name, roots):
    for base in roots:
        for root, _, files in os.walk(base):
            for f in files:
                if f.lower().startswith(name + ".") and f.lower().rsplit(".", 1)[1] in ("dds", "tga", "png"):
                    return os.path.join(root, f)
    return None


# --- the resolve, for a sky pixel --------------------------------------------------------------------------------------
def present(hdr, bloom=0.06, thresh=1.0, knee=0.5, gamma=2.2):
    """CX's sky-pixel resolve: own value + bloom bleed, encoded, no exposure/tonemap (cx_resolve_f's uSkyRaw arm).
    The bloom is the usual soft-knee bright pass over a Gaussian pyramid, close enough to judge a glow by."""
    c = np.nan_to_num(hdr[..., :3].astype(np.float32))
    br = c.max(axis=2, keepdims=True)
    soft = np.clip(br - thresh + knee, 0, 2 * knee) ** 2 / (4 * knee)
    src = c * (np.maximum(soft, br - thresh) / np.maximum(br, 1e-6))
    h, w = c.shape[:2]
    acc = np.zeros_like(c)
    lvl = src
    for k in range(6):
        lh, lw = lvl.shape[:2]
        if lh < 8 or lw < 8:
            break
        lvl = np.stack([np.asarray(Image.fromarray(lvl[..., i], "F").resize((lw // 2, lh // 2), Image.BILINEAR))
                        for i in range(3)], axis=2)
        up = np.stack([np.asarray(Image.fromarray(lvl[..., i], "F").resize((w, h), Image.BICUBIC)) for i in range(3)], axis=2)
        acc += up
    out = np.clip(c + acc / 6.0 * bloom * 6.0, 0, 1) ** (1.0 / gamma)
    return (out * 255 + 0.5).astype(np.uint8)


def render(gl, sky, s, size, fbo, color, vao, count, model, frames, ms_out=None):
    eye = np.array([500.0, 500.0, s["eyez"]])
    fwd = body_dir(s, s["aim"]) if s["aim"] in ("sun", "moon") else look(s["yaw"], s["pitch"])
    proj, vp = view_proj(eye, fwd, s["fov"], size[0] / size[1])
    if not sky.images and sky.images_decl:
        sky.make_images()
    if not sky.once_done:
        once = []
        sky.run("once", s, eye, vp, proj, once)
        gl.Finish()
        for ref, tq in once:
            v = C.c_uint64(0)
            gl.GetQueryObjectui64v(tq, 0x8866, C.byref(v))   # not-an-offset: GL_QUERY_RESULT
            print(f"once: {ref} {v.value / 1e6:.1f} ms")
        sky.once_done = True
    q = sky.gl.gen(gl.GenQueries)
    timings = []
    for k in range(frames):
        sky.swap_history()
        sky.run("frame", s, eye, vp, proj, timings if k == frames - 1 else None)
        if k == frames - 1:
            gl.BeginQuery(0x88BF, q)   # not-an-offset: GL_TIME_ELAPSED
        sky.prev_vp = vp
        sky.fresh = False
        sky.frame += 1
        gl.BindFramebuffer(0x8D40, fbo)   # not-an-offset: GL_FRAMEBUFFER
        gl.Viewport(0, 0, *size)
        gl.ClearColor(0.035, 0.04, 0.03, 1.0)   # where the world would be
        gl.Clear(0x4000)   # not-an-offset: GL_COLOR_BUFFER_BIT
        gl.UseProgram(sky.prog)
        sky.uniforms(sky.prog, s, eye, vp, np.linalg.inv(vp), proj, s["main"] == 1)
        gl.UniformMatrix4fv(gl.loc(sky.prog, "uModel"), 1, 0, mat(model))
        gl.BindTextureUnit(0, sky.assets[0]); gl.BindTextureUnit(1, sky.assets[1])
        sky.bind(sky.prog)
        gl.BindVertexArray(vao)
        gl.DrawArrays(0x0004, 0, count)   # not-an-offset: GL_TRIANGLES
        if k == frames - 1:
            gl.EndQuery(0x88BF)   # not-an-offset: GL_TIME_ELAPSED
    gl.Finish()
    ns = C.c_uint64(0)
    gl.GetQueryObjectui64v(q, 0x8866, C.byref(ns))   # not-an-offset: GL_QUERY_RESULT
    if ms_out is not None:
        ms_out.append(ns.value / 1e6)
        for ref, tq in timings:
            v = C.c_uint64(0)
            gl.GetQueryObjectui64v(tq, 0x8866, C.byref(v))   # not-an-offset: GL_QUERY_RESULT
            ms_out.append((ref, v.value / 1e6))
    buf = (C.c_float * (size[0] * size[1] * 4))()
    gl.GetTextureImage(color, 0, 0x1908, 0x1406, C.sizeof(buf), buf)   # not-an-offset: GL_RGBA, GL_FLOAT
    img = np.frombuffer(buf, dtype=np.float32).reshape(size[1], size[0], 4)[::-1]
    return img


def label(img, text):
    from PIL import ImageDraw
    im = Image.fromarray(img)
    d = ImageDraw.Draw(im)
    d.rectangle([0, 0, 8 + 6 * len(text), 14], fill=(0, 0, 0))
    d.text((4, 2), text, fill=(255, 255, 255))
    return np.asarray(im)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--stage", default="fst_sky", help="the material's customShaderFS (default: the reference content's)")
    ap.add_argument("--mtr", default="ccs_01b", help="the sky material whose .mdl and textures to draw (default: likewise)")
    ap.add_argument("--content", default=paths.get("CONTENT_SHADERS") or "")
    ap.add_argument("--assets", default="", help="where the .mdl/.mtr/.dds live (default: <content>/../skyboxes)")
    ap.add_argument("--scene", default="", help="space-separated key=value overrides of " + ", ".join(DEFAULTS))
    ap.add_argument("--sheet", help="a file of scenes, one per line ('label: key=value ...')")
    ap.add_argument("--compare", action="store_true", help="the stage itself beside its cx_ twin")
    ap.add_argument("--vanilla", action="store_true", help="render the stage itself, not its twin")
    ap.add_argument("--size", default="960x540")
    ap.add_argument("--frames", type=int, default=24, help="frames of temporal accumulation per scene")
    ap.add_argument("--bench", type=int, default=0, help="time this many frames per scene and report medians")
    ap.add_argument("--coverage", metavar="IMAGE", help="measure the share of the sky overhead (20..90 degrees) a "
                    "cloud panorama IMAGE (u azimuth, v = sqrt of elevation from -6 degrees) holds as cloud, per cover")
    ap.add_argument("--raw", action="store_true", help="also save each frame's linear HDR as .npy")
    ap.add_argument("--nolabel", action="store_true", help="leave the scene/stage/time label off the PNGs")
    ap.add_argument("--dump", action="append", default=[], help="also save this declared image (npy + png slices)")
    ap.add_argument("-o", "--out", default="sky.png")
    a = ap.parse_args()
    size = tuple(int(x) for x in a.size.split("x"))
    cs = load_check_shaders(a.content)
    fn = cs.nv_context()
    if fn is None:
        sys.exit("no NVIDIA EGL device")
    gl = GL(fn)
    assets_dir = a.assets or os.path.join(os.path.dirname(a.content.rstrip("/")), "skyboxes")
    mtr = read_mtr(os.path.join(assets_dir, a.mtr + ".mtr"))
    pos, uv = load_mdl(os.path.join(assets_dir, a.mtr + ".mdl"))
    roots = [assets_dir]
    tex = [load_texture(gl, find_asset(mtr.get(k, "ds_alpha0"), roots)) for k in ("texture0", "texture1")]
    data = np.hstack([pos, uv]).astype(np.float32)
    vbo = gl.gen(gl.CreateBuffers)
    gl.NamedBufferData(vbo, data.nbytes, data.ctypes.data, 0x88E4)   # not-an-offset: GL_STATIC_DRAW
    vao = gl.gen(gl.CreateVertexArrays)
    gl.VertexArrayVertexBuffer(vao, 0, vbo, 0, 20)
    for at, n, off in ((0, 3, 0), (1, 2, 12)):
        gl.EnableVertexArrayAttrib(vao, at)
        gl.VertexArrayAttribFormat(vao, at, n, 0x1406, 0, off)   # not-an-offset: GL_FLOAT
        gl.VertexArrayAttribBinding(vao, at, 0)
    color = gl.gen(gl.CreateTextures, TEX2D)
    gl.TextureStorage2D(color, 1, 0x881A, *size)   # not-an-offset: GL_RGBA16F
    fbo = gl.gen(gl.CreateFramebuffers)
    gl.NamedFramebufferTexture(fbo, 0x8CE0, color, 0)   # not-an-offset: GL_COLOR_ATTACHMENT0
    focus = np.array([500.0, 500.0, 0.0])
    model = np.identity(4); model[:3, 3] = focus
    twin = "cx_" + a.stage
    variants = [(a.stage, False), (twin, True)] if a.compare else [((a.stage, False) if a.vanilla else (twin, True))]
    skies = [Sky(gl, cs, a.content, st, tw, size, tex) for st, tw in variants]
    scenes = []
    if a.sheet:
        for line in open(a.sheet):
            line = line.split("#")[0].strip()
            if line:
                name, _, rest = line.partition(":")
                scenes.append((name.strip(), parse_scene(a.scene + " " + rest)))
    else:
        scenes.append(("scene", parse_scene(a.scene)))
    if a.coverage:
        sky = skies[-1]
        for cover in (0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9):
            s = parse_scene(a.scene + f" cover={cover}")
            sky.fresh = True
            render(gl, sky, s, size, fbo, color, vao, len(pos), model, 1)
            im = sky.images[a.coverage]
            w, h = im["dims"][:2]
            buf = (C.c_float * (w * h * 4))()
            gl.GetTextureImage(im["tex"][0], 0, 0x1908, 0x1406, C.sizeof(buf), buf)   # not-an-offset: GL_RGBA, GL_FLOAT
            T = np.frombuffer(buf, dtype=np.float32).reshape(h, w, 4)[..., 3]
            v = (np.arange(h) + 0.5) / h
            low = -0.105
            el = low + v * v * (math.pi / 2 - low)
            wgt = np.cos(el) * 2.0 * v * (math.pi / 2 - low)
            sel = el > math.radians(20.0)
            cloud = (T < 0.5).astype(np.float64)
            frac = float((cloud[sel] * wgt[sel, None]).sum() / (wgt[sel].sum() * w))
            print(f"cover {cover:.1f}: cloud share overhead {frac:.3f}")
        return
    tiles = []
    if a.sheet:
        os.makedirs(a.out, exist_ok=True)
    for name, s in scenes:
        row = []
        for sky in skies:
            ms = []
            sky.fresh = True
            img = render(gl, sky, s, size, fbo, color, vao, len(pos), model, a.frames if sky.twin else 1, ms)
            if a.bench:
                runs = []
                for _ in range(a.bench):
                    m2 = []
                    render(gl, sky, s, size, fbo, color, vao, len(pos), model, 1, m2)
                    runs.append(m2)
                med = lambda xs: float(np.median(xs))
                parts = [("dome", med([r[0] for r in runs]))] + [
                    (ref, med([r[1 + j][1] for r in runs])) for j, (ref, _) in enumerate(runs[0][1:])]
                print(f"BENCH {name:18s} {sky.stage:11s} total {sum(t for _, t in parts):.3f} ms  "
                      + "  ".join(f"{r.replace('sowsky_', '')} {t:.3f}" for r, t in parts))
            if a.raw:
                np.save(os.path.join(a.out if a.sheet else os.path.dirname(a.out) or ".",
                                     re.sub(r"\W+", "_", name) + "_" + sky.stage + "_hdr.npy"), img)
            passes = ms[1:]
            total = ms[0] + sum(t for _, t in passes)
            png = present(img) if a.nolabel else label(present(img), f"{name} [{sky.stage}] {total:.2f} ms")
            row.append(png)
            detail = "  ".join(f"{r.replace('sowsky_', '')} {t:.2f}" for r, t in passes)
            print(f"{name:20s} {sky.stage:11s} total {total:.2f} ms (dome {ms[0]:.2f}; {detail})  max {np.nanmax(img[..., :3]):.2f}")
            for dname in a.dump:
                im = sky.images.get(dname)
                if im is None:
                    continue
                d3 = im["dims"] + [1] if len(im["dims"]) == 2 else im["dims"]
                n = d3[0] * d3[1] * d3[2] * 4
                buf = (C.c_float * n)()
                gl.GetTextureImage(im["tex"][0], 0, 0x1908, 0x1406, C.sizeof(buf), buf)   # not-an-offset: GL_RGBA, GL_FLOAT
                arr = np.frombuffer(buf, dtype=np.float32).reshape(d3[2], d3[1], d3[0], 4)
                base = os.path.join(a.out if a.sheet else os.path.dirname(a.out) or ".", re.sub(r"\W+", "_", name) + "_" + dname)
                np.save(base + ".npy", arr)
                sl = arr[0][::-1]
                rgb = sl[..., :3] / max(float(np.percentile(sl[..., :3], 99.5)), 1e-6)
                Image.fromarray((np.clip(rgb, 0, 1) * 255).astype(np.uint8)).save(base + "_rgb.png")
                Image.fromarray((np.clip(sl[..., 3], 0, 1) * 255).astype(np.uint8)).save(base + "_a.png")
        tiles.append(np.hstack(row))
        if a.sheet:
            os.makedirs(a.out, exist_ok=True)
            Image.fromarray(tiles[-1]).save(os.path.join(a.out, re.sub(r"\W+", "_", name) + ".png"))
    if a.sheet:
        Image.fromarray(np.vstack(tiles)).save(os.path.join(a.out, "sheet.png"))
    else:
        Image.fromarray(tiles[0]).save(a.out)


if __name__ == "__main__":
    main()
