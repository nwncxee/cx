using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using CX.Graphics;

namespace CX.Ui;

public static unsafe partial class TextRig
{
    /// <summary>Every GL object the replay draws through, made in the context it draws in: VAOs are not shared, so
    /// the engine's context and Alloy's each hold one set. Alloy's is replaced when its context goes.</summary>
    private sealed class RigGlState
    {
        public bool Tried, Ok;
        public uint Vao, Vbo, Atlas, Sampler;
        public int AtlasEpoch = -1;
        public readonly Prog[] Progs = new Prog[KindCount];
    }

    /// <summary>One linked program and its uniform locations (-1 where its stage has no such uniform).</summary>
    private sealed class Prog { public uint Id; public int Proj, Tex, Alpha, Offset, Pal, Rows, Crop; }

    private const int KindCount = (int)UiDrawKind.IconPlt + 1;

    private static RigGlState _engGl = new();
    /// <summary>Volatile: replaced by <see cref="ContextLost"/> while a render thread may be replaying.</summary>
    private static volatile RigGlState _alloyGl = new();

    /// <summary>Alloy's context is gone and every name it held with it.</summary>
    internal static void ContextLost() => _alloyGl = new RigGlState();

    /// <summary>Issue <paramref name="l"/> into the bound framebuffer, in whichever context is current. Sets the
    /// state it draws with and leaves blend on, depth test and cull off, no sampler and unit 0 active. Hot, zero-alloc.</summary>
    internal static void Replay(UiDrawList l)
    {
        if (l.CmdN == 0 || _log == null || !Gl.Init(_log) || l.ViewW <= 0 || l.ViewH <= 0) return;
        RigGlState g = GlContextService.Current46 ? _alloyGl : _engGl;
        if (!Build(g)) return;
        if (g.AtlasEpoch != AtlasEpoch) UploadAtlas(g);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.Disable(Gl.DEPTH_TEST); Gl.Disable(Gl.CULL_FACE);
        Gl.Enable(Gl.BLEND); Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
        Gl.BindVertexArray(g.Vao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, g.Vbo);
        fixed (float* p = l.Verts) Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)(l.VertN * sizeof(float)), p, Gl.DYNAMIC_DRAW);
        Matrix4x4 m = Ortho(l.ViewW, l.ViewH);
        Prog? cur = null;
        uint tex = uint.MaxValue, samp = uint.MaxValue, pal = 0;
        int clip = -1;
        for (int i = 0; i < l.CmdN; i++)
        {
            ref UiCmd c = ref l.Cmds[i];
            Prog p = g.Progs[(int)c.Kind];
            if (p != cur)
            {
                Gl.UseProgram(p.Id);
                Gl.UniformMatrix4fv(p.Proj, 1, (byte)Gl.FALSE, (float*)&m);
                Gl.Uniform1i(p.Tex, 0);
                Gl.Uniform1i(p.Pal, 1);
                cur = p;
            }
            Gl.Uniform1f(p.Alpha, c.Alpha);
            Gl.Uniform2f(p.Offset, c.OffX, c.OffY);
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
            Gl.DrawArrays(Gl.TRIANGLES, c.First, c.Count);
        }
        // A sampler left on unit 0 overrides the next texture's own filter: the glyph atlas's, or the engine's.
        if (samp != 0 && Gl.BindSampler != null) Gl.BindSampler(0, 0);
        if (pal != 0) { Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_2D, 0); Gl.ActiveTexture(Gl.TEXTURE0); }
        Gl.Disable(Gl.SCISSOR_TEST);
        Gl.BindVertexArray(0);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, 0);
    }

    /// <summary>The master-palette rows a PLT icon command carries, one per layer.</summary>
    internal const int PltRows = 10;

    /// <summary>Build <paramref name="g"/>'s programs, VAO and sampler on its first replay; false (said once) when a
    /// program refused, and that context's UI draws nothing for the session.</summary>
    private static bool Build(RigGlState g)
    {
        if (g.Tried) return g.Ok;
        g.Tried = true;
        string ctx = GlContextService.Current46 ? "Alloy's 4.6 context" : "the engine's context";
        for (int k = 0; k < KindCount; k++)
        {
            Prog? p = Link(FragmentSource((UiDrawKind)k), (UiDrawKind)k);
            if (p == null) { _log?.Always($"TextRig: the {(UiDrawKind)k} program did not build in {ctx} -- CX's own UI is NOT DRAWN there"); return false; }
            g.Progs[k] = p;
        }
        g.Vao = MakeVao(out g.Vbo);
        g.Sampler = Gl.BindSampler != null ? Gl.LinearClampSampler() : 0u;
        g.Ok = true;
        _log?.Info($"TextRig: UI replay ready in {ctx} ({KindCount} programs, one VAO)");
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

    private static Prog? Link(string fsrc, UiDrawKind k)
    {
        uint vs = Compile(Gl.VERTEX_SHADER, VSrc), fs = Compile(Gl.FRAGMENT_SHADER, fsrc);
        if (vs == 0 || fs == 0) return null;
        uint id = Gl.CreateProgram();
        Gl.AttachShader(id, vs); Gl.AttachShader(id, fs); Gl.LinkProgram(id);
        int ok; Gl.GetProgramiv(id, Gl.LINK_STATUS, &ok);
        Gl.DeleteShader(vs); Gl.DeleteShader(fs);
        if (ok == 0) { _log?.Info($"TextRig: {k} program link failed"); return null; }
        return new Prog
        {
            Id = id, Proj = GlProgram.Loc(id, "uProj"), Tex = GlProgram.Loc(id, "uTex"), Alpha = GlProgram.Loc(id, "uAlpha"),
            Offset = GlProgram.Loc(id, "uOffset"), Pal = GlProgram.Loc(id, "uPal"), Rows = GlProgram.Loc(id, "uRows[0]"),
            Crop = GlProgram.Loc(id, "uCrop"),
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

    /// <summary>The current sheet into <paramref name="g"/>'s atlas texture, every level, at unpack alignment 1 (R8 rows
    /// under 4 texels) and back to the GL default 4 -- in the engine's context <c>GLRender::CreateTexture</c> @0x4ca2b0
    /// makes no pixel-store call of its own and assumes it. Cold: once per re-bake.</summary>
    private static void UploadAtlas(RigGlState g)
    {
        int epoch = AtlasEpoch;   // before the sheet: a re-bake between the two reads then re-uploads next replay
        AtlasSheet? a = _sheet;   // snapshot: a cold re-bake swap must not pair one sheet's Pixels with another's side
        if (a == null) return;
        if (g.Atlas == 0) { uint t; Gl.GenTextures(1, &t); g.Atlas = t; }
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, g.Atlas);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR_MIPMAP_LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, a.Mips.Length);   // must equal the uploaded chain, or the texture is incomplete and samples black
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 1);
        fixed (byte* px = a.Pixels)
            Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.R8, a.Side, a.Side, 0, Gl.RED, Gl.UNSIGNED_BYTE, px);
        for (int l = 1; l < AtlasSheet.MipLevels && l - 1 < a.Mips.Length; l++)
            fixed (byte* px = a.Mips[l - 1])
                Gl.TexImage2D(Gl.TEXTURE_2D, l, (int)Gl.R8, a.Side >> l, a.Side >> l, 0, Gl.RED, Gl.UNSIGNED_BYTE, px);
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 4);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        g.AtlasEpoch = epoch;
        _log?.Info($"TextRig: atlas uploaded as GL tex {g.Atlas} ({a.Side}², {a.FaceCount} face(s), {AtlasSheet.MipLevels} levels, {a.Bytes / 1024} KB R8)");
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

    private const string VSrc =
        "#version 330 core\nlayout(location=0) in vec2 aPos;\nlayout(location=1) in vec2 aUV;\n" +
        "layout(location=2) in vec3 aCol;\nlayout(location=3) in float aA;\nuniform mat4 uProj;\nuniform vec2 uOffset;\n" +
        "out vec2 vUV;\nout vec3 vCol;\nout float vA;\n" +
        "void main(){ gl_Position = uProj*vec4(aPos + uOffset,0.0,1.0); vUV = aUV; vCol = aCol; vA = aA; }\n";
    private const string FSrc =
        "#version 330 core\nin vec2 vUV;\nin vec3 vCol;\nin float vA;\nuniform sampler2D uTex;\nuniform float uAlpha;\nout vec4 o;\n" +
        "void main(){ float a = texture(uTex, vUV).r * vA * uAlpha; o = vec4(vCol, a); }\n";
    private const string FSrcRgb =
        "#version 330 core\nin vec2 vUV;\nin vec3 vCol;\nin float vA;\nuniform sampler2D uTex;\nout vec4 o;\n" +
        "void main(){ o = vec4(texture(uTex, vUV).rgb, vA); }\n";
    /// <summary>The portrait program discards texture alpha and icons carry theirs.</summary>
    private const string FSrcIcon =
        "#version 330 core\nin vec2 vUV;\nin vec3 vCol;\nin float vA;\nuniform sampler2D uTex;\nout vec4 o;\n" +
        "void main(){ o = texture(uTex, vUV) * vec4(1.0, 1.0, 1.0, vA); }\n";
    /// <summary>The icon program drops the vertex colour, so it cannot tint; this one multiplies both, and the batch alpha.</summary>
    private const string FSrcSkin =
        "#version 330 core\nin vec2 vUV;\nin vec3 vCol;\nin float vA;\nuniform sampler2D uTex;\nuniform float uAlpha;\nout vec4 o;\n" +
        "void main(){ o = texture(uTex, vUV) * vec4(vCol, vA * uAlpha); }\n";

    /// <summary>A PLT layer: a PAIR texture (intensity R, layer G) resolved to <c>master[rows[layer]][intensity]</c>, the
    /// engine's composite lookup. texelFetch, because the mean of two layer indices is a third layer; bilinear runs over
    /// the RESOLVED colours, premultiplied, and taps clamp to the art <c>uCrop</c> (u1, v0) leaves, so the sheet's
    /// transparent padding never bleeds a dark fringe into the edge.</summary>
    private static readonly string FSrcIconPlt =
        "#version 330 core\nin vec2 vUV;\nin vec3 vCol;\nin float vA;\n" +
        "uniform sampler2D uTex;\nuniform sampler2D uPal;\n" + $"uniform int uRows[{PltRows}];" + "\nuniform vec2 uCrop;\nout vec4 o;\n" +
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
        "  o = vec4(c.rgb / max(c.a, 0.0001), c.a * vA);\n}\n";
}
