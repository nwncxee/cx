using System.Reflection;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Anti-aliasing as a post-stack stage (ADR 0020). The client's anti-aliasing option sizes the
/// ENGINE's subframe renderbuffers (<c>Camera::RefreshRenderSubframeResources</c> @0x5a6610), which the Alloy
/// frame never lands in, so under Alloy the option was inert. Alloy answers it with SMAA 1x on the resolved
/// display-space frame, and the option's sample count picks the preset: 2 MEDIUM, 4 HIGH, 8 and 16 ULTRA.</summary>
internal static unsafe partial class SmaaStage
{
    private static ExtenderLog? _log;
    /// <summary><c>g_nMSAA</c>, the option's live value.</summary>
    private static int* _msaa;
    /// <summary>The preset compiled in (0 none, 1 MEDIUM, 2 HIGH, 3 ULTRA): the <c>$AA$</c> token
    /// <see cref="ShaderResource.Tokens"/> expands, so an override reads the same preset as the embedded text.</summary>
    internal static int Level { get; private set; }
    /// <summary>Everything the GL context owns, in one object: context loss replaces it, so no name or
    /// uniform location from the dead context survives as a field nobody remembered to zero.</summary>
    private sealed class SmaaState
    {
        public uint Fbo, Edges, Blend, Area, Search, ProgEdge, ProgWeight, StageProg;
        /// <summary>LINEAR/clamp sampler for uSrc in the aa passes: the stack's ping-pong textures are
        /// NEAREST, and SMAA's searches and final blend both lean on bilinear fetches at half-texel
        /// offsets.</summary>
        public uint Sampler;
        public int LocEdgeSrc, LocEdgeRt, LocWgtRt, LocWgtEdges, LocWgtArea, LocWgtSearch, LocStageRt, LocStageBlend;
        public int W, H, BuiltLevel = -1;
        public bool Failed;
    }

    private static SmaaState _gl = new();

    /// <summary>The three passes' resrefs (override points), same rules as every post-chain stage.</summary>
    internal const string ShdEdgeFs = "cx_smaaedge_fs", ShdWeightFs = "cx_smaawgt_fs", ShdBlendFs = "cx_smaablend_fs";
    /// <summary>The reference tables, RG8 160x560 and R8 64x16, as the repo ships them (Textures/*.h upstream).</summary>
    internal const int AreaW = 160, AreaH = 560, SearchW = 64, SearchH = 16;

    /// <summary>From <see cref="PostStack.Attach"/>: resolve the option's global.</summary>
    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _msaa = (int*)Native.Sym(SymbolMap.MsaaSamples);
        if (_msaa == null) log.Info("SmaaStage: g_nMSAA is unresolved -- the anti-aliasing option stays inert");
    }

    /// <summary>The option's sample count -> preset level; pure, so the self-check can assert it.</summary>
    internal static int LevelFor(int samples) => samples >= 8 ? 3 : samples >= 4 ? 2 : samples >= 2 ? 1 : 0;
    /// <summary>The option as set right now, in MSAA samples; 0 when the global is unresolved.</summary>
    internal static int Samples => _msaa == null ? 0 : *_msaa;
    /// <summary>The stage's per-frame gate: the option is on, nothing of ours failed to build, and TAA is
    /// off -- the two are exclusive, because TAA smooths what SMAA smooths and more.</summary>
    internal static bool Active => !_gl.Failed && LevelFor(Samples) > 0 && !PostChain.TaaOn;
    internal static string PresetName(int level) => level switch { 3 => "ULTRA", 2 => "HIGH", 1 => "MEDIUM", _ => "off" };
    /// <summary>The /post and /knobs term: option, preset, and whether the stage is dropped.</summary>
    internal static string Describe()
        => $"(aa option {Samples}x -> SMAA {PresetName(LevelFor(Samples))}{(_gl.Failed ? ", FAILED" : PostChain.TaaOn ? ", replaced by TAA" : "")})";

    /// <summary>Context loss: forget every GL name; the next frame rebuilds.</summary>
    internal static void Forget()
    {
        _gl = new SmaaState();
    }

    /// <summary>Asked by <see cref="PostStack.Begin"/>: targets at this size and programs at this preset,
    /// (re)built when either moved. False drops the stage for the frame (and, on a build failure, for the
    /// context). Cold after the first frame at a size and level.</summary>
    internal static bool Ready(int w, int h)
    {
        if (_gl.Failed) return false;
        if (Gl.GenSamplers == null || Gl.ClearBufferfv == null)
        {
            _gl.Failed = true;
            _log?.Info("SmaaStage: no sampler objects or glClearBufferfv on this context -- the aa stage is DROPPED");
            return false;
        }
        int level = LevelFor(Samples);
        if (level != _gl.BuiltLevel && !Build(level)) return false;
        return w == _gl.W && h == _gl.H || Size(w, h);
    }

    private static bool Build(int level)
    {
        if (_gl.ProgEdge != 0) { Gl.DeleteProgram(_gl.ProgEdge); Gl.DeleteProgram(_gl.ProgWeight); }
        Level = level;
        string vs = ShaderStages.Source(PostChain.ShdPostVs);
        _gl.ProgEdge = GlProgram.Graphics(PostChain.ShdPostVs, vs, ShdEdgeFs, ShaderStages.Source(ShdEdgeFs), _log, "SMAA edges");
        _gl.ProgWeight = GlProgram.Graphics(PostChain.ShdPostVs, vs, ShdWeightFs, ShaderStages.Source(ShdWeightFs), _log, "SMAA weights");
        if (_gl.ProgEdge == 0 || _gl.ProgWeight == 0)
        {
            _gl.Failed = true;
            _log?.Info("SmaaStage: a pass did not build -- the aa stage is DROPPED on this context; the frame "
                     + "resolves without anti-aliasing");
            return false;
        }
        _gl.LocEdgeSrc = GlProgram.Loc(_gl.ProgEdge, "uSrc"); _gl.LocEdgeRt = GlProgram.Loc(_gl.ProgEdge, "uRt");
        _gl.LocWgtRt = GlProgram.Loc(_gl.ProgWeight, "uRt"); _gl.LocWgtEdges = GlProgram.Loc(_gl.ProgWeight, "uEdges");
        _gl.LocWgtArea = GlProgram.Loc(_gl.ProgWeight, "uArea"); _gl.LocWgtSearch = GlProgram.Loc(_gl.ProgWeight, "uSearch");
        _gl.BuiltLevel = level;
        _log?.Info($"SmaaStage: built at SMAA {PresetName(level)} (option {Samples}x)");
        return true;
    }

    private static bool Size(int w, int h)
    {
        if (w <= 0 || h <= 0) return false;
        if (_gl.Area == 0)
        {
            uint* t = stackalloc uint[2];
            Gl.CreateTextures(Gl.TEXTURE_2D, 2, t);
            _gl.Area = t[0]; _gl.Search = t[1];
            uint f; Gl.GenFramebuffers(1, &f); _gl.Fbo = f;
            _gl.Sampler = Gl.LinearClampSampler();
            if (!Table(_gl.Area, "SmaaArea", Gl.RG8, Gl.RG, AreaW, AreaH, Gl.LINEAR)
                || !Table(_gl.Search, "SmaaSearch", Gl.R8, Gl.RED, SearchW, SearchH, Gl.NEAREST))
            {
                _gl.Failed = true;
                _log?.Info("SmaaStage: the area/search tables are missing from the build -- the aa stage is DROPPED");
                return false;
            }
        }
        RenderTarget.AllocColor(ref _gl.Edges, false, w, h, Gl.LINEAR);
        RenderTarget.AllocColor(ref _gl.Blend, false, w, h, Gl.LINEAR);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.Fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _gl.Edges, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            _gl.Failed = true;
            _log?.Info($"SmaaStage: the edge/weight target is incomplete (0x{status:x}) -- the aa stage is DROPPED");
            return false;
        }
        _gl.W = w; _gl.H = h;
        _log?.Info($"SmaaStage: edge + weight targets {w}x{h}");
        return true;
    }

    /// <summary>Upload one embedded table. The one allocation this class makes, once per context.</summary>
    private static bool Table(uint tex, string resource, uint internalFormat, uint format, int w, int h, int filter)
    {
        byte[]? bytes = TableBytes(resource, w * h * (format == Gl.RG ? 2 : 1));
        if (bytes == null) return false;
        Gl.TextureStorage2D(tex, 1, internalFormat, w, h);
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 1);
        fixed (byte* p = bytes) Gl.TextureSubImage2D(tex, 0, 0, 0, w, h, format, Gl.UNSIGNED_BYTE, p);
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 4);
        RenderTarget.Clamp(tex, filter);
        return true;
    }

    /// <summary>Embedded table <paramref name="resource"/>'s bytes, or null when it is missing or not <paramref name="len"/> long.</summary>
    private static byte[]? TableBytes(string resource, int len)
    {
        using Stream? s = typeof(SmaaStage).Assembly.GetManifestResourceStream(resource);
        if (s == null || s.Length != len) return null;
        byte[] bytes = new byte[len];
        s.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>The two passes ahead of the blend stage, from <see cref="PostStack.Run"/>: edges of
    /// <paramref name="src"/>, then weights. Leaves the weights on <see cref="TextureSlots.AaBlend"/>, the
    /// linear sampler on uSrc's unit, and the stage program's own uniforms set; the caller rebinds its
    /// target and viewport after. Hot: allocates nothing.</summary>
    internal static void Prepare(uint src, uint stageProg, int* viewport)
    {
        float* zero = stackalloc float[4];
        float w = _gl.W, h = _gl.H;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.Fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _gl.Edges, 0);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        // the edge pass DISCARDS non-edges (the reference's stencil hook), so last frame's edges must go
        Gl.ClearBufferfv(Gl.COLOR, 0, zero);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.PostHdr);
        Gl.BindTexture(Gl.TEXTURE_2D, src);
        Gl.BindSampler(TextureSlots.PostHdr, _gl.Sampler);
        Gl.UseProgram(_gl.ProgEdge);
        Gl.Uniform1i(_gl.LocEdgeSrc, (int)TextureSlots.PostHdr);
        Gl.Uniform4f(_gl.LocEdgeRt, 1f / w, 1f / h, w, h);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);

        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _gl.Blend, 0);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.AaEdges); Gl.BindTexture(Gl.TEXTURE_2D, _gl.Edges);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.AaArea); Gl.BindTexture(Gl.TEXTURE_2D, _gl.Area);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.AaSearch); Gl.BindTexture(Gl.TEXTURE_2D, _gl.Search);
        Gl.UseProgram(_gl.ProgWeight);
        Gl.Uniform1i(_gl.LocWgtEdges, (int)TextureSlots.AaEdges);
        Gl.Uniform1i(_gl.LocWgtArea, (int)TextureSlots.AaArea);
        Gl.Uniform1i(_gl.LocWgtSearch, (int)TextureSlots.AaSearch);
        Gl.Uniform4f(_gl.LocWgtRt, 1f / w, 1f / h, w, h);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);

        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.AaBlend); Gl.BindTexture(Gl.TEXTURE_2D, _gl.Blend);
        if (stageProg != _gl.StageProg)
        {
            _gl.StageProg = stageProg;
            _gl.LocStageRt = GlProgram.Loc(stageProg, "uRt"); _gl.LocStageBlend = GlProgram.Loc(stageProg, "uBlend");
        }
        Gl.UseProgram(stageProg);
        Gl.Uniform1i(_gl.LocStageBlend, (int)TextureSlots.AaBlend);
        Gl.Uniform4f(_gl.LocStageRt, 1f / w, 1f / h, w, h);
    }

    /// <summary>Pass 2: blend weights from the edges, through the area and search tables.</summary>
    internal static string WeightFs => ShaderSource.Get("cx_smaawgt_f.glsl");

    /// <summary>Pass 3, the stack stage: the neighbourhood blend of uSrc by the weights. Display space in
    /// and out, like every stage; the incoming <c>c</c> is re-fetched bilinearly by the reference. Carries
    /// the shared defs for the library's sake only -- no preset macro reaches this pass, which is why a
    /// live preset change rebuilds the other two programs and not this one.</summary>
    internal static string BlendStageFs => ShaderSource.Get("cx_smaablend_f.glsl");

    /// <summary>Off-device: the option maps to the presets, the three passes compose on the port, and the
    /// tables are in the build at their reference sizes.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(LevelFor(0) == 0 && LevelFor(2) == 1 && LevelFor(4) == 2 && LevelFor(8) == 3 && LevelFor(16) == 3,
          "0/2/4/8/16 samples -> off/MEDIUM/HIGH/ULTRA/ULTRA");
        foreach (string r in new[] { ShdEdgeFs, ShdWeightFs, ShdBlendFs })
            check.Expect(ShaderStages.Source(r).Contains("#define SMAA_GLSL_4 1") && ShaderStages.Source(r).Contains("float2 SMAALumaEdgeDetectionPS("),
              $"{r} carries the port + library");
        foreach (string r in new[] { ShdEdgeFs, ShdWeightFs })
            check.Expect(ShaderResource.Tokens(ShaderStages.Source(r)).Contains($"#define CX_AA_LEVEL {Level}\n"), $"{r} takes the preset from $AA$");
        check.Expect(ShaderContract.SlotSafe(ShaderStages.Source(ShdWeightFs), "uEdges", "uArea", "uSearch"), "the weight pass binds its three tables");
        using (Stream? a = typeof(SmaaStage).Assembly.GetManifestResourceStream("SmaaArea"))
        using (Stream? s = typeof(SmaaStage).Assembly.GetManifestResourceStream("SmaaSearch"))
            check.Expect(a?.Length == AreaW * AreaH * 2 && s?.Length == SearchW * SearchH, "the area and search tables are embedded at 160x560 RG8 and 64x16 R8");
        log.Info(check.Ok ? "SmaaStage.SelfCheck: passed" : "SmaaStage.SelfCheck: FAILED");
        return check.Ok;
    }
}
