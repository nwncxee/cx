using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using CX.Graphics;
using static System.FormattableString;

namespace CX.Ui;

public static unsafe partial class TextRig
{
    /// <summary>Every GL object the replay draws through, made in the context it draws in: VAOs are not shared, so
    /// the engine's context and Alloy's each hold one set. Alloy's is replaced when its context goes.</summary>
    private sealed class RigGlState
    {
        public bool Tried, Ok, BatchTried, HeldSaid;
        public uint Vao, Vbo, Atlas, Sampler, RecBuf;
        /// <summary>The atlas's bindless handle, resident until a re-bake replaces the texture; 0 = none minted.</summary>
        public ulong AtlasHandle;
        public int AtlasEpoch = -1;
        public readonly Prog[] Progs = new Prog[KindCount];
        /// <summary>The one program a bindless run draws through: Alloy's context with bindless on, else null.</summary>
        public Prog? Batch;
        /// <summary>Per command of the list being replayed: its record, its handle (0 = drawn bound) and a run's ranges.</summary>
        public UiRec[] Recs = new UiRec[512];
        public ulong[] Handles = new ulong[512];
        public int[] Firsts = new int[512], Counts = new int[512];
    }

    /// <summary>One linked program and its uniform locations (-1 where its stages have no such uniform).</summary>
    private sealed class Prog { public uint Id; public int Proj, Tex, Alpha, Offset, Pal, Rows, Crop, Occl, Anchor, Vp, DepthK, View, Scene, Depth, Base; }

    /// <summary>One command as <see cref="BatchVs"/> reads it from <c>CxUiCmdBuf</c>, in std430 layout: 80 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct UiRec
    {
        public ulong Tex;
        public uint Kind, Flags;
        public float OffX, OffY, Alpha, Pad0;
        public Vector4 Clip, Occl;
        public float Ax, Ay, Pad1, Pad2;
    }

    private const int KindCount = (int)UiDrawKind.IconPlt + 1;
    /// <summary><see cref="UiRec.Flags"/>: sample bilinear with clamped taps (the sampler object's filter); clip to the rect.</summary>
    private const uint FlagBilinear = 1, FlagClip = 2;
    /// <summary>An occlusion opacity past 1: the command is anchored to nothing and the scene test returns 1.</summary>
    private const float NoTest = 2f;
    /// <summary>The unit the world frame's depth binds on for the scene test: the last programs of the frame read it there.</summary>
    private const uint DepthUnit = CX.Alloy.TextureSlots.HiliteDepth;

    private static RigGlState _engGl = new();
    /// <summary>Volatile: replaced by <see cref="ContextLost"/> while a render thread may be replaying.</summary>
    private static volatile RigGlState _alloyGl = new();

    /// <summary>Alloy's context is gone and every name it held with it.</summary>
    internal static void ContextLost() => _alloyGl = new RigGlState();

    /// <summary>Issue <paramref name="l"/> into the bound framebuffer, in whichever context is current. In Alloy's, with
    /// bindless on, every run of commands whose textures have handles is one multi-draw; the rest draw one by one.
    /// Leaves blend on, depth test and cull off, no sampler and unit 0 active. Hot, zero-alloc.</summary>
    internal static void Replay(UiDrawList l)
    {
        if (l.CmdN == 0 || _log == null || !Gl.Init(_log) || l.ViewW <= 0 || l.ViewH <= 0) return;
        bool alloy = GlContextService.Current46;
        RigGlState g = alloy ? _alloyGl : _engGl;
        if (!Build(g)) return;
        if (!alloy) UiHostService.Owe();
        if (alloy && !g.BatchTried && CX.Alloy.AlloyRenderer.Bindless) BuildBatch(g);
        if (g.AtlasEpoch != AtlasEpoch) UploadAtlas(g);
        Matrix4x4 vp = default; Vector2 k = default; uint depth = 0;
        bool scene = alloy && CX.Alloy.UiScene.Get(out vp, out k, out depth);
        Gl.Disable(Gl.DEPTH_TEST); Gl.Disable(Gl.CULL_FACE);
        Gl.Enable(Gl.BLEND); Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
        if (scene) { Gl.ActiveTexture(Gl.TEXTURE0 + DepthUnit); Gl.BindTexture(Gl.TEXTURE_2D, depth); }
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindVertexArray(g.Vao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, g.Vbo);
        fixed (float* p = l.Verts) Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)(l.VertN * sizeof(float)), p, Gl.DYNAMIC_DRAW);
        Matrix4x4 m = Ortho(l.ViewW, l.ViewH);
        for (int i = 0; i < KindCount; i++) Uniforms(g.Progs[i], &m, scene, &vp, k, l);
        if (g.Batch != null && GlContextService.HeldHere && !g.HeldSaid)
        {
            g.HeldSaid = true;   // the handle table mints on the game thread only
            _log?.Always("TextRig: the render thread replays the UI one command at a time -- bindless batching runs on the game thread only");
        }
        bool batched = g.Batch != null && CX.Alloy.AlloyRenderer.Bindless && !GlContextService.HeldHere && Stage(g, l) > 0;
        if (batched) Uniforms(g.Batch!, &m, scene, &vp, k, l);
        Prog? cur = null;
        uint tex = uint.MaxValue, samp = uint.MaxValue, pal = 0;
        int clip = -1;
        for (int i = 0; i < l.CmdN;)
        {
            if (batched && g.Handles[i] != 0)
            {
                int end = i + 1;
                while (end < l.CmdN && g.Handles[end] != 0) end++;
                if (clip != 0) { Gl.Disable(Gl.SCISSOR_TEST); clip = 0; }
                if (cur != g.Batch) { Gl.UseProgram(g.Batch!.Id); cur = g.Batch; }
                Gl.Uniform1i(g.Batch!.Base, i);
                for (int j = i; j < end; j++) { g.Firsts[j - i] = l.Cmds[j].First; g.Counts[j - i] = l.Cmds[j].Count; }
                fixed (int* f = g.Firsts) fixed (int* n = g.Counts) Gl.MultiDrawArrays(Gl.TRIANGLES, f, n, end - i);
                i = end;
                continue;
            }
            ref UiCmd c = ref l.Cmds[i++];
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
            Gl.DrawArrays(Gl.TRIANGLES, c.First, c.Count);
        }
        // A sampler left on unit 0 overrides the next texture's own filter: the glyph atlas's, or the engine's.
        if (samp != 0 && samp != uint.MaxValue && Gl.BindSampler != null) Gl.BindSampler(0, 0);
        if (pal != 0) { Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_2D, 0); Gl.ActiveTexture(Gl.TEXTURE0); }
        if (scene) { Gl.ActiveTexture(Gl.TEXTURE0 + DepthUnit); Gl.BindTexture(Gl.TEXTURE_2D, 0); Gl.ActiveTexture(Gl.TEXTURE0); }
        Gl.Disable(Gl.SCISSOR_TEST);
        Gl.BindVertexArray(0);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, 0);
    }

    /// <summary>The per-replay uniforms of <paramref name="p"/>: the ortho, its units, and the world frame's camera for
    /// the scene test (off without one).</summary>
    private static void Uniforms(Prog p, Matrix4x4* m, bool scene, Matrix4x4* vp, Vector2 k, UiDrawList l)
    {
        Gl.UseProgram(p.Id);
        Gl.UniformMatrix4fv(p.Proj, 1, (byte)Gl.FALSE, (float*)m);
        Gl.Uniform1i(p.Tex, 0);
        Gl.Uniform1i(p.Pal, 1);
        Gl.Uniform1i(p.Depth, (int)DepthUnit);
        Gl.Uniform1i(p.Scene, scene ? 1 : 0);
        if (!scene) return;
        Gl.UniformMatrix4fv(p.Vp, 1, (byte)Gl.FALSE, (float*)vp);
        Gl.Uniform2f(p.DepthK, k.X, k.Y);
        Gl.Uniform2f(p.View, l.ViewW, l.ViewH);
    }

    /// <summary>Fill <paramref name="g"/>'s record and handle per command and upload the records: a command takes a
    /// handle when its texture has one (Alloy's own, or the atlas's), a PLT icon never. Returns how many took one.</summary>
    private static int Stage(RigGlState g, UiDrawList l)
    {
        if (g.Recs.Length < l.CmdN)
        {
            int cap = Math.Max(g.Recs.Length * 2, l.CmdN);
            g.Recs = new UiRec[cap]; g.Handles = new ulong[cap]; g.Firsts = new int[cap]; g.Counts = new int[cap];
        }
        if (g.AtlasHandle == 0 && g.Atlas != 0 && (g.AtlasHandle = Gl.GetTextureHandleARB(g.Atlas)) != 0)
            Gl.MakeTextureHandleResidentARB(g.AtlasHandle);
        int n = 0;
        for (int i = 0; i < l.CmdN; i++)
        {
            ref UiCmd c = ref l.Cmds[i];
            ulong h = c.Kind switch
            {
                UiDrawKind.Text => g.AtlasHandle,
                UiDrawKind.IconPlt => 0,   // ponytail: two textures and ten rows a command; bindless when PLT icons show in the zones
                _ => CX.Alloy.OwnedTextures.Armed ? CX.Alloy.AlloyRenderer.HandleFor((int)c.Tex) : 0,   // a handle freezes an engine texture's own parameters
            };
            g.Handles[i] = h;
            if (h == 0) continue;
            n++;
            ref UiRec r = ref g.Recs[i];
            r.Tex = h; r.Kind = (uint)c.Kind;
            r.Flags = (c.Sampler ? FlagBilinear : 0u) | (c.Clip ? FlagClip : 0u);
            r.OffX = c.OffX; r.OffY = c.OffY; r.Alpha = c.Alpha;
            float y0 = l.ViewH - (c.Cy + c.Ch);
            r.Clip = new Vector4(c.Cx, y0, c.Cx + (c.Cw < 0 ? 0 : c.Cw), y0 + (c.Ch < 0 ? 0 : c.Ch));
            r.Occl = new Vector4(c.Wx, c.Wy, c.Wz, c.Occl ? c.Hidden : NoTest);
            r.Ax = c.Ax; r.Ay = c.Ay;
        }
        if (n == 0) return 0;
        fixed (UiRec* p = g.Recs) Gl.NamedBufferData(g.RecBuf, (IntPtr)(l.CmdN * sizeof(UiRec)), p, Gl.DYNAMIC_DRAW);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, CX.Alloy.SsboSlots.UiCmds, g.RecBuf);
        return n;
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
            Prog? p = Link(VSrc, FragmentSource((UiDrawKind)k), ((UiDrawKind)k).ToString());
            if (p == null) { _log?.Always($"TextRig: the {(UiDrawKind)k} program did not build in {ctx} -- CX's own UI is NOT DRAWN there"); return false; }
            g.Progs[k] = p;
        }
        g.Vao = MakeVao(out g.Vbo);
        g.Sampler = Gl.BindSampler != null ? Gl.LinearClampSampler() : 0u;
        g.Ok = true;
        _log?.Info($"TextRig: UI replay ready in {ctx} ({KindCount} programs, one VAO)");
        return true;
    }

    /// <summary>The bindless program and its record buffer, in Alloy's context once bindless is on. A refusal is said
    /// once and that context draws one command at a time, as it does with bindless off.</summary>
    private static void BuildBatch(RigGlState g)
    {
        g.BatchTried = true;
        if (Gl.MultiDrawArrays == null || Gl.CreateBuffers == null || Gl.NamedBufferData == null || Gl.GetTextureHandleARB == null)
        {
            _log?.Always("TextRig: glMultiDrawArrays or a bindless/DSA entry point is missing -- the UI draws one command at a time");
            return;
        }
        g.Batch = Link(BatchVs, BatchFs, "bindless batch");
        if (g.Batch == null) { _log?.Always("TextRig: the bindless UI program did not build -- the UI draws one command at a time"); return; }
        uint b; Gl.CreateBuffers(1, &b); g.RecBuf = b;
        _log?.Info("TextRig: bindless UI batching ready -- each run of handled commands is one multi-draw");
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
            Id = id, Proj = GlProgram.Loc(id, "uProj"), Tex = GlProgram.Loc(id, "uTex"), Alpha = GlProgram.Loc(id, "uAlpha"),
            Offset = GlProgram.Loc(id, "uOffset"), Pal = GlProgram.Loc(id, "uPal"), Rows = GlProgram.Loc(id, "uRows[0]"),
            Crop = GlProgram.Loc(id, "uCrop"), Occl = GlProgram.Loc(id, "uOccl"), Anchor = GlProgram.Loc(id, "uAnchor"),
            Vp = GlProgram.Loc(id, "uVp"), DepthK = GlProgram.Loc(id, "uDepthK"), View = GlProgram.Loc(id, "uView"),
            Scene = GlProgram.Loc(id, "uScene"), Depth = GlProgram.Loc(id, "uDepth"), Base = GlProgram.Loc(id, "uBase"),
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
    /// makes no pixel-store call of its own and assumes it. A handled atlas is immutable, so a re-bake makes a new one.</summary>
    private static void UploadAtlas(RigGlState g)
    {
        int epoch = AtlasEpoch;   // before the sheet: a re-bake between the two reads then re-uploads next replay
        AtlasSheet? a = _sheet;   // snapshot: a cold re-bake swap must not pair one sheet's Pixels with another's side
        if (a == null) return;
        if (g.AtlasHandle != 0)
        {
            Gl.MakeTextureHandleNonResidentARB(g.AtlasHandle);   // residency first: a resident handle outlives its texture
            uint old = g.Atlas; Gl.DeleteTextures(1, &old);
            g.Atlas = 0; g.AtlasHandle = 0;
        }
        if (g.Atlas == 0) { uint t; Gl.GenTextures(1, &t); g.Atlas = t; }
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, g.Atlas);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR_MIPMAP_LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, a.Mips.Length);   // must equal the uploaded chain, or the texture is incomplete and samples black
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 1);
        for (int l = 0; l < AtlasSheet.MipLevels && l - 1 < a.Mips.Length; l++)
        {
            byte[] rg = Interleave(l == 0 ? a.Pixels : a.Mips[l - 1], l == 0 ? a.Field : a.FieldMips[l - 1]);
            fixed (byte* px = rg)
                Gl.TexImage2D(Gl.TEXTURE_2D, l, (int)Gl.RG8, a.Side >> l, a.Side >> l, 0, Gl.RG, Gl.UNSIGNED_BYTE, px);
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

    /// <summary>One command record (<see cref="UiRec"/>), on <see cref="CX.Alloy.SsboSlots.UiCmds"/>.</summary>
    private static readonly string RecDecl =
        "struct UiRec { uvec2 tex; uint kind; uint flags; vec2 off; float alpha; float pad0; vec4 clip; vec4 occl; vec2 anchor; vec2 pad1; };\n" +
        $"layout(std430, binding = {CX.Alloy.SsboSlots.UiCmds}) readonly buffer CxUiCmdBuf {{ UiRec uRecs[]; }};\n";

    /// <summary>The bindless run's vertex stage: command <c>uBase + gl_DrawID</c>'s offset and scene test.</summary>
    internal static readonly string BatchVs =
        "#version 460 core\nlayout(location=0) in vec2 aPos;\nlayout(location=1) in vec2 aUV;\n" +
        "layout(location=2) in vec3 aCol;\nlayout(location=3) in float aA;\n" + RecDecl +
        "uniform mat4 uProj;\nuniform int uBase;\n" + OcclLib +
        "out vec2 vUV;\nout vec3 vCol;\nout float vA;\nflat out int vDraw;\nflat out float vFade;\n" +
        "void main(){ vDraw = uBase + gl_DrawID; UiRec r = uRecs[vDraw];\n" +
        "  gl_Position = uProj * vec4(aPos + r.off, 0.0, 1.0); vUV = aUV; vCol = aCol; vA = aA; vFade = CxOccl(r.occl, r.anchor); }\n";

    /// <summary>The bindless run's fragment stage: every kind but PLT, as the bound programs above draw it, sampling by the
    /// command's handle -- through clamped bilinear taps where the bound path binds the sampler object -- and clipping
    /// to its rect where the bound path scissors. The clip discards last, after every derivative is taken.</summary>
    internal static readonly string BatchFs =
        "#version 460 core\n#extension GL_ARB_bindless_texture : require\n" + RecDecl +
        "in vec2 vUV;\nin vec3 vCol;\nin float vA;\nflat in int vDraw;\nflat in float vFade;\nout vec4 o;\n" + SdfLib +
        "vec4 Bilinear(sampler2D s, vec2 uv){\n" +
        "  ivec2 d = max(textureSize(s, 0), ivec2(1)), m = d - 1; vec2 t = uv * vec2(d) - 0.5; ivec2 p = ivec2(floor(t)); vec2 f = t - vec2(p);\n" +
        "  vec4 a = texelFetch(s, clamp(p, ivec2(0), m), 0), b = texelFetch(s, clamp(p + ivec2(1, 0), ivec2(0), m), 0);\n" +
        "  vec4 c = texelFetch(s, clamp(p + ivec2(0, 1), ivec2(0), m), 0), e = texelFetch(s, clamp(p + ivec2(1, 1), ivec2(0), m), 0);\n" +
        "  return mix(mix(a, b, f.x), mix(c, e, f.x), f.y);\n}\n" +
        "void main(){\n" +
        "  UiRec r = uRecs[vDraw];\n" +
        $"  vec4 t = (r.flags & {FlagBilinear}u) != 0u ? Bilinear(sampler2D(r.tex), vUV) : texture(sampler2D(r.tex), vUV);\n" +
        $"  if (r.kind == {(int)UiDrawKind.Text}u) o = vec4(vCol, CxText(t.rg, vUV, vec2(textureSize(sampler2D(r.tex), 0))) * vA * r.alpha);\n" +
        $"  else if (r.kind == {(int)UiDrawKind.Portrait}u) o = vec4(t.rgb, vA);\n" +
        $"  else if (r.kind == {(int)UiDrawKind.Icon}u) o = t * vec4(1.0, 1.0, 1.0, vA);\n" +
        "  else o = t * vec4(vCol, vA * r.alpha);\n" +
        "  o.a *= vFade;\n" +
        $"  if ((r.flags & {FlagClip}u) != 0u && (any(lessThan(gl_FragCoord.xy, r.clip.xy)) || any(greaterThanEqual(gl_FragCoord.xy, r.clip.zw)))) discard;\n" +
        "}\n";
}
