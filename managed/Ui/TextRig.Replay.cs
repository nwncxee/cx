using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using CX.Graphics;
using static System.FormattableString;

namespace CX.Ui;

public static unsafe partial class TextRig
{
    /// <summary>Every GL object the replay draws through, made in the engine's context on its first replay.</summary>
    private sealed class RigGlState
    {
        public bool Tried, Ok;
        public uint Vao, Vbo, Atlas, Sampler;
        public int AtlasEpoch = -1;
        public readonly Prog[] Progs = new Prog[KindCount];
    }

    /// <summary>One linked program and its uniform locations (-1 where its stages have no such uniform).</summary>
    private sealed class Prog { public uint Id; public int Proj, Tex, Alpha, Offset, Pal, Rows, Crop, Occl, Anchor, Scene; }

    private const int KindCount = (int)UiDrawKind.IconPlt + 1;
    /// <summary>An occlusion opacity past 1: the command is anchored to nothing and the scene test returns 1.</summary>
    private const float NoTest = 2f;

    private static RigGlState _gl = new();

    /// <summary>Issue <paramref name="l"/> into the bound framebuffer of the engine's context, one command at a time.
    /// Leaves blend on, depth test and cull off, no sampler and unit 0 active. Under Vulkan the list goes over the
    /// composite through <c>GuiPassVk.ReplayNow</c> and no GL is issued. Hot, zero-alloc.</summary>
    internal static void Replay(UiDrawList l)
    {
        if (l.CmdN == 0 || _log == null || l.ViewW <= 0 || l.ViewH <= 0) return;
        if (CX.Alloy.Renderer.OnVulkan) { CX.Alloy.GuiPassVk.ReplayNow(l, _log); return; }
        if (!Gl.Init(_log)) return;
        RigGlState g = _gl;
        if (!Build(g)) return;
        UiHostService.Owe();
        if (g.AtlasEpoch != AtlasEpoch) UploadAtlas(g);
        Gl.Disable(Gl.DEPTH_TEST); Gl.Disable(Gl.CULL_FACE);
        Gl.Enable(Gl.BLEND); Gl.BlendFunc(EngineTokens.SRC_ALPHA, EngineTokens.ONE_MINUS_SRC_ALPHA);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindVertexArray(g.Vao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, g.Vbo);
        fixed (float* p = l.Verts) Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)(l.VertN * sizeof(float)), p, Gl.DYNAMIC_DRAW);
        Matrix4x4 m = Ortho(l.ViewW, l.ViewH);
        for (int i = 0; i < KindCount; i++) Uniforms(g.Progs[i], &m);
        Prog? cur = null;
        uint tex = uint.MaxValue, samp = uint.MaxValue, pal = 0;
        int clip = -1;
        for (int i = 0; i < l.CmdN; i++)
        {
            ref UiCmd c = ref l.Cmds[i];
            Prog p = g.Progs[(int)c.Kind];
            if (p != cur) { Gl.UseProgram(p.Id); cur = p; }
            Gl.Uniform1f(p.Alpha, c.Alpha);
            Gl.Uniform2f(p.Offset, c.OffX, c.OffY);
            Gl.Uniform4f(p.Occl, c.Wx, c.Wy, c.Wz, c.Occl ? c.Hidden : NoTest);
            Gl.Uniform2f(p.Anchor, c.Ax, c.Ay);
            if (c.Kind == UiDrawKind.IconPlt)
            {
                Gl.Uniform2f(p.Crop, c.U1, c.V0);
                fixed (int* r = l.Rows.AsSpan(c.RowsAt)) Gl.Uniform1iv(p.Rows, PltRows, r);
                if (c.Pal != pal) { Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_2D, c.Pal); Gl.ActiveTexture(Gl.TEXTURE0); pal = c.Pal; }
            }
            uint t = c.Kind == UiDrawKind.Text ? g.Atlas : c.Tex;
            if (t != tex) { Gl.BindTexture(Gl.TEXTURE_2D, t); tex = t; }
            uint s = c.Sampler ? g.Sampler : 0u;
            if (s != samp && Gl.BindSampler != null) { Gl.BindSampler(0, s); samp = s; }
            if (c.Clip)
            {
                if (clip != 1) { Gl.Enable(Gl.SCISSOR_TEST); clip = 1; }
                Gl.Scissor(c.Cx, l.ViewH - (c.Cy + c.Ch), c.Cw < 0 ? 0 : c.Cw, c.Ch < 0 ? 0 : c.Ch);
            }
            else if (clip != 0) { Gl.Disable(Gl.SCISSOR_TEST); clip = 0; }
            Gl.DrawArrays(EngineTokens.TRIANGLES, c.First, c.Count);
        }
        // A sampler left on unit 0 overrides the next texture's own filter: the glyph atlas's, or the engine's.
        if (samp != 0 && samp != uint.MaxValue && Gl.BindSampler != null) Gl.BindSampler(0, 0);
        if (pal != 0) { Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_2D, 0); Gl.ActiveTexture(Gl.TEXTURE0); }
        Gl.Disable(Gl.SCISSOR_TEST);
        Gl.BindVertexArray(0);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, 0);
    }

    /// <summary>The per-replay uniforms of <paramref name="p"/>: the ortho and its units. The scene test is off --
    /// only Alloy's frame has the depth to test against, and this replay is the engine's.</summary>
    private static void Uniforms(Prog p, Matrix4x4* m)
    {
        Gl.UseProgram(p.Id);
        Gl.UniformMatrix4fv(p.Proj, 1, (byte)Gl.FALSE, (float*)m);
        Gl.Uniform1i(p.Tex, 0);
        Gl.Uniform1i(p.Pal, 1);
        Gl.Uniform1i(p.Scene, 0);
    }

    /// <summary>The master-palette rows a PLT icon command carries, one per layer.</summary>
    internal const int PltRows = 10;

    /// <summary>Build <paramref name="g"/>'s programs, VAO and sampler on its first replay; false (said once) when a
    /// program refused, and CX's own UI draws nothing for the session.</summary>
    private static bool Build(RigGlState g)
    {
        if (g.Tried) return g.Ok;
        g.Tried = true;
        for (int k = 0; k < KindCount; k++)
        {
            Prog? p = Link(VSrc, FragmentSource((UiDrawKind)k), ((UiDrawKind)k).ToString());
            if (p == null) { _log?.Always($"TextRig: the {(UiDrawKind)k} program did not build in the engine's context -- CX's own UI is NOT DRAWN"); return false; }
            g.Progs[k] = p;
        }
        g.Vao = MakeVao(out g.Vbo);
        g.Sampler = Gl.BindSampler != null ? Gl.LinearClampSampler() : 0u;
        g.Ok = true;
        _log?.Info($"TextRig: UI replay ready in the engine's context ({KindCount} programs, one VAO)");
        return true;
    }

    private static string FragmentSource(UiDrawKind k) => k switch
    {
        UiDrawKind.Text => FSrc,
        UiDrawKind.Portrait => FSrcRgb,
        UiDrawKind.Icon => FSrcIcon,
        UiDrawKind.Skin => FSrcSkin,
        _ => FSrcIconPlt,
    };

    private static Prog? Link(string vsrc, string fsrc, string name)
    {
        uint vs = Compile(Gl.VERTEX_SHADER, vsrc), fs = Compile(Gl.FRAGMENT_SHADER, fsrc);
        if (vs == 0 || fs == 0) return null;
        uint id = Gl.CreateProgram();
        Gl.AttachShader(id, vs); Gl.AttachShader(id, fs); Gl.LinkProgram(id);
        int ok; Gl.GetProgramiv(id, Gl.LINK_STATUS, &ok);
        Gl.DeleteShader(vs); Gl.DeleteShader(fs);
        if (ok == 0) { _log?.Info($"TextRig: {name} program link failed"); return null; }
        return new Prog
        {
            Id = id, Proj = Gl.Loc(id, "uProj"), Tex = Gl.Loc(id, "uTex"), Alpha = Gl.Loc(id, "uAlpha"),
            Offset = Gl.Loc(id, "uOffset"), Pal = Gl.Loc(id, "uPal"), Rows = Gl.Loc(id, "uRows[0]"),
            Crop = Gl.Loc(id, "uCrop"), Occl = Gl.Loc(id, "uOccl"), Anchor = Gl.Loc(id, "uAnchor"),
            Scene = Gl.Loc(id, "uScene"),
        };
    }

    private static uint Compile(uint type, string src)
    {
        uint sh = Gl.CreateShader(type);
        byte[] bytes = Encoding.UTF8.GetBytes(src);
        fixed (byte* sp = bytes)
        {
            byte* pp = sp; int len = bytes.Length;
            Gl.ShaderSource(sh, 1, &pp, &len);
        }
        Gl.CompileShader(sh);
        int ok; Gl.GetShaderiv(sh, Gl.COMPILE_STATUS, &ok);
        if (ok == 0)
        {
            byte* buf = stackalloc byte[512]; int n = 0;
            Gl.GetShaderInfoLog(sh, 512, &n, buf);
            _log?.Info($"TextRig: shader compile failed: {Marshal.PtrToStringAnsi((IntPtr)buf, n)}");
            return 0;
        }
        return sh;
    }

    /// <summary>The current sheet into <paramref name="g"/>'s atlas texture, every level, both planes interleaved as RG8
    /// (R coverage, G distance field), at unpack alignment 1 (rows under 2 texels) and back to the GL default 4 -- in the engine's context <c>GLRender::CreateTexture</c> @0x4ca2b0
    /// makes no pixel-store call of its own and assumes it.</summary>
    private static void UploadAtlas(RigGlState g)
    {
        int epoch = AtlasEpoch;   // before the sheet: a re-bake between the two reads then re-uploads next replay
        AtlasSheet? a = _sheet;   // snapshot: a cold re-bake swap must not pair one sheet's Pixels with another's side
        if (a == null) return;
        if (g.Atlas == 0) { uint t; Gl.GenTextures(1, &t); g.Atlas = t; }
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, g.Atlas);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, EngineTokens.LINEAR_MIPMAP_LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, EngineTokens.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, EngineTokens.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, EngineTokens.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, a.Mips.Length);   // must equal the uploaded chain, or the texture is incomplete and samples black
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 1);
        for (int l = 0; l < AtlasSheet.MipLevels && l - 1 < a.Mips.Length; l++)
        {
            byte[] rg = Interleave(l == 0 ? a.Pixels : a.Mips[l - 1], l == 0 ? a.Field : a.FieldMips[l - 1]);
            fixed (byte* px = rg)
                Gl.TexImage2D(Gl.TEXTURE_2D, l, (int)EngineTokens.RG8, a.Side >> l, a.Side >> l, 0, EngineTokens.RG, EngineTokens.UNSIGNED_BYTE, px);
        }
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 4);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        g.AtlasEpoch = epoch;
        _log?.Info($"TextRig: atlas uploaded as GL tex {g.Atlas} ({a.Side}², {a.FaceCount} face(s), {AtlasSheet.MipLevels} levels, {a.Bytes / 1024} KB RG8: coverage + distance field)");
    }

    /// <summary>Two planes of one level as RG8 texels. Cold: once per level per re-bake.</summary>
    private static byte[] Interleave(byte[] r, byte[] g)
    {
        var rg = new byte[r.Length * 2];
        for (int i = 0; i < r.Length; i++) { rg[2 * i] = r[i]; rg[2 * i + 1] = g[i]; }
        return rg;
    }

    /// <summary>A VAO + its own VBO wired to the UI vertex layout (pos2, uv2, rgb3, alpha1).</summary>
    private static uint MakeVao(out uint vbo)
    {
        uint a; Gl.GenVertexArrays(1, &a);
        uint vb; Gl.GenBuffers(1, &vb); vbo = vb;
        Gl.BindVertexArray(a);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, vb);
        int stride = Floats * sizeof(float);
        Gl.VertexAttribPointer(0, 2, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)0);
        Gl.EnableVertexAttribArray(0);
        Gl.VertexAttribPointer(1, 2, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)(2 * sizeof(float)));
        Gl.EnableVertexAttribArray(1);
        Gl.VertexAttribPointer(2, 3, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)(4 * sizeof(float)));
        Gl.EnableVertexAttribArray(2);
        Gl.VertexAttribPointer(3, 1, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)(7 * sizeof(float)));
        Gl.EnableVertexAttribArray(3);
        Gl.BindVertexArray(0);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, 0);
        return a;
    }

    /// <summary>Column-major ortho for glOrtho(0,w,h,0,-1,1): top-left origin, y grows downward.</summary>
    internal static Matrix4x4 Ortho(float w, float h)
        => new(2f / w, 0f, 0f, 0f,
               0f, -2f / h, 0f, 0f,
               0f, 0f, -1f, 0f,
               -1f, 1f, 0f, 1f);

    /// <summary>World distance a scene surface may sit in front of an anchor's point and still not cover it: the body
    /// of the creature the anchor belongs to.</summary>
    private const float OcclBias = 1f;
    /// <summary>The scene test, in the vertex stage: the share of five depth taps around the anchor pixel that lie behind
    /// the world point, from <c>o.w</c> (all covered) to 1. The depth terms are the projection's (cx_scenedepth.glsl's
    /// reading); a draw anchored to nothing, a frame with no world or a point behind the eye is 1.</summary>
    private static readonly string OcclLib =
        "uniform mat4 uVp;\nuniform vec2 uDepthK;\nuniform vec2 uView;\nuniform int uScene;\nuniform sampler2D uDepth;\n" +
        "const ivec2 kTap[5] = ivec2[5](ivec2(0), ivec2(4, 0), ivec2(-4, 0), ivec2(0, 4), ivec2(0, -4));\n" +
        "float CxOccl(vec4 o, vec2 a){\n" +
        "  if (uScene == 0 || o.w > 1.0) return 1.0;\n" +
        "  vec4 c = uVp * vec4(o.xyz, 1.0);\n" +
        "  if (c.w <= 0.0) return 1.0;\n" +
        "  ivec2 sz = textureSize(uDepth, 0);\n" +
        "  ivec2 p = ivec2(vec2(a.x / uView.x, 1.0 - a.y / uView.y) * vec2(sz));\n" +
        "  float seen = 0.0;\n" +
        "  for (int i = 0; i < 5; i++) {\n" +
        "    float ndc = texelFetch(uDepth, clamp(p + kTap[i], ivec2(0), sz - 1), 0).r * 2.0 - 1.0;\n" +
        Invariant($"    seen += uDepthK.y / (ndc + uDepthK.x) + {OcclBias:F1} >= c.w ? 1.0 : 0.0;\n") +
        "  }\n" +
        "  return mix(o.w, 1.0, seen / 5.0);\n}\n";

    /// <summary>A glyph's coverage from the atlas texel <c>t</c> (R baked coverage, G distance field with
    /// <see cref="AtlasSheet.SdfEdge"/> on the outline). At or below its baked size it is the baked coverage, exactly;
    /// magnified past <see cref="SdfFrom"/>x it hands over to a one-screen-pixel smoothstep of the field, which stays sharp.</summary>
    private static readonly string SdfLib =
        "float CxText(vec2 t, vec2 uv, vec2 size){\n" +
        "  vec2 p = uv * size; float texels = max(length(dFdx(p)), length(dFdy(p)));\n" +
        "  float w = max(0.5 * length(vec2(dFdx(t.g), dFdy(t.g))), 0.002);\n" +
        Invariant($"  float sdf = smoothstep({AtlasSheet.SdfEdge / 255f:F5} - w, {AtlasSheet.SdfEdge / 255f:F5} + w, t.g);\n") +
        Invariant($"  return mix(t.r, sdf, clamp((1.0 / max(texels, 1e-4) - {SdfFrom:F2}) / {SdfBand:F2}, 0.0, 1.0));\n") + "}\n";

    /// <summary>The magnification text starts handing over to the distance field at, and the span it finishes over:
    /// the field's edge reads harsher than the baked coverage at 1:1, so only scaled-up text takes it.</summary>
    private const float SdfFrom = 1.25f, SdfBand = 0.75f;

    private static readonly string VSrc =
        "#version 330 core\nlayout(location=0) in vec2 aPos;\nlayout(location=1) in vec2 aUV;\n" +
        "layout(location=2) in vec3 aCol;\nlayout(location=3) in float aA;\nuniform mat4 uProj;\nuniform vec2 uOffset;\n" +
        "uniform vec4 uOccl;\nuniform vec2 uAnchor;\n" + OcclLib +
        "out vec2 vUV;\nout vec3 vCol;\nout float vA;\nflat out float vFade;\n" +
        "void main(){ gl_Position = uProj*vec4(aPos + uOffset,0.0,1.0); vUV = aUV; vCol = aCol; vA = aA; vFade = CxOccl(uOccl, uAnchor); }\n";
    private const string FsHead = "#version 330 core\nin vec2 vUV;\nin vec3 vCol;\nin float vA;\nflat in float vFade;\nuniform sampler2D uTex;\nout vec4 o;\n";
    private static readonly string FSrc =
        FsHead + "uniform float uAlpha;\n" + SdfLib +
        "void main(){ float a = CxText(texture(uTex, vUV).rg, vUV, vec2(textureSize(uTex, 0))) * vA * uAlpha * vFade; o = vec4(vCol, a); }\n";
    private const string FSrcRgb =
        FsHead + "void main(){ o = vec4(texture(uTex, vUV).rgb, vA * vFade); }\n";
    /// <summary>The portrait program discards texture alpha and icons carry theirs.</summary>
    private const string FSrcIcon =
        FsHead + "void main(){ o = texture(uTex, vUV) * vec4(1.0, 1.0, 1.0, vA * vFade); }\n";
    /// <summary>The icon program drops the vertex colour, so it cannot tint; this one multiplies both, and the batch alpha.</summary>
    private const string FSrcSkin =
        FsHead + "uniform float uAlpha;\n" +
        "void main(){ o = texture(uTex, vUV) * vec4(vCol, vA * uAlpha * vFade); }\n";

    /// <summary>A PLT layer: a PAIR texture (intensity R, layer G) resolved to <c>master[rows[layer]][intensity]</c>, the
    /// engine's composite lookup. texelFetch, because the mean of two layer indices is a third layer; bilinear runs over
    /// the RESOLVED colours, premultiplied, and taps clamp to the art <c>uCrop</c> (u1, v0) leaves, so the sheet's
    /// transparent padding never bleeds a dark fringe into the edge.</summary>
    private static readonly string FSrcIconPlt =
        FsHead + "uniform sampler2D uPal;\n" + $"uniform int uRows[{PltRows}];" + "\nuniform vec2 uCrop;\n" +
        "ivec2 lo, hi;\n" +
        "vec4 tap(ivec2 p){\n" +
        "  vec2 rg = texelFetch(uTex, clamp(p, lo, hi), 0).rg;\n" +
        $"  int l = int(rg.g * 255.0 + 0.5); l = l < {PltRows - 1} ? l : {PltRows - 1};\n" +
        "  vec4 c = texelFetch(uPal, ivec2(int(rg.r * 255.0 + 0.5), uRows[l]), 0);\n" +
        "  return vec4(c.rgb * c.a, c.a);\n}\n" +
        "void main(){\n" +
        "  ivec2 d = textureSize(uTex, 0); d = max(d, ivec2(1));\n" +
        "  lo = ivec2(0, int(floor(uCrop.y * float(d.y) + 0.5))); hi = ivec2(max(int(floor(uCrop.x * float(d.x) + 0.5)) - 1, 0), d.y - 1);\n" +
        "  vec2 t = vUV * vec2(d) - 0.5; vec2 f = fract(t); ivec2 p = ivec2(floor(t));\n" +
        "  vec4 c = mix(mix(tap(p), tap(p + ivec2(1,0)), f.x),\n" +
        "               mix(tap(p + ivec2(0,1)), tap(p + ivec2(1,1)), f.x), f.y);\n" +
        "  o = vec4(c.rgb / max(c.a, 0.0001), c.a * vA * vFade);\n}\n";
}
