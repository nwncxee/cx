using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Deferred ground decals: ahead of the opaque pass every ground decal over-blends its albedo into a
/// screen layer, and the prepassed surface under it folds that layer into its own albedo, so ground and decal are
/// lit once, as one surface. A decal fragment that does not lie on that surface stays in the blended pass:
/// <c>cx_ownlit_head</c>'s <c>CxDecalOn</c> splits every fragment between the two on the same inputs.</summary>
internal static unsafe class DecalLayer
{
    /// <summary>Image units the lit stage reads the layer and its surface depth on (<c>cx_ownlit_head</c>). Above
    /// the three every compute pass binds, so no dispatch between the opaque and blended passes rebinds them.</summary>
    internal const uint ImgLayer = 6, ImgSurf = 7;

    /// <summary>What <see cref="Issue"/> does with a lit program's <c>uDecal</c> right now.</summary>
    internal const int ModeNone = 0, ModeFold = 1, ModeSkip = 2, ModeWrite = 3;

    /// <summary>Everything the GL context owns, plus the frame's latches and the period's counters, in one
    /// object: context loss replaces it, keeping only the log.</summary>
    private sealed class DecalState
    {
        public ExtenderLog? Log;
        public uint Fbo, Layer, Surf;
        public int W, H;
        public bool Checked, Failed;
        /// <summary>The layer holds this frame's decals, bound for reading: the opaque pass folds it and the
        /// blended pass drops what it took.</summary>
        public bool Live;
        public int Mode;
        /// <summary>The projection's M33 and M43, which turn a window depth back into a view depth.</summary>
        public float Z0, Z1;
        public long Frames, Groups;
    }

    private static DecalState _s = new();

    internal static int Mode => _s.Mode;
    internal static float Z0 => _s.Z0;
    internal static float Z1 => _s.Z1;

    internal static void Attach(ExtenderLog log) => _s.Log = log;

    /// <summary>Context loss: the names went with it.</summary>
    internal static void Forget() => _s = new DecalState { Log = _s.Log };

    /// <summary>Draw batches [<paramref name="from"/>, <paramref name="to"/>) -- the frame's ground decals -- into
    /// the layer and bind it for the passes that read it. <paramref name="can"/> is the frame's own verdict (a
    /// perspective main view with prepass depth and an unshifted viewport); without it decals blend as before.</summary>
    internal static void Run(int from, int to, Matrix4x4* vp, in Matrix4x4 proj, int* viewport, float near, float far, bool can)
    {
        var s = _s;
        s.Live = false;
        if (!can || to <= from || s.Failed || Programs.ProgDecal == 0 || PostChain.DepthTex == 0) return;
        if (!Ensure(viewport[2], viewport[3])) return;
        s.Z0 = proj.M33; s.Z1 = proj.M43;
        int cur; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &cur);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, s.Fbo);
        uint* bufs = stackalloc uint[2] { Gl.COLOR_ATTACHMENT0, Gl.COLOR_ATTACHMENT1 };
        Gl.DrawBuffers(2, bufs);
        float* clear = stackalloc float[4] { 0f, 0f, 0f, 1f };
        Gl.ClearBufferfv(Gl.COLOR, 0, clear);
        clear[3] = 0f;
        Gl.ClearBufferfv(Gl.COLOR, 1, clear);
        // no depth attachment: the writer's own CxDecalOn against the prepass depth is the visibility test
        Gl.DepthMask(0);
        Gl.Enable(Gl.BLEND); AlloyRenderer._blendOn = true;
        Gl.BlendFuncSeparatei(0, Gl.ONE, Gl.ONE_MINUS_SRC_ALPHA, Gl.ZERO, Gl.ONE_MINUS_SRC_ALPHA);
        Gl.BlendFunci(1, Gl.ONE, Gl.ZERO);
        s.Mode = ModeWrite;
        Issue._holdProg = false;
        Issue.IssueColour(from, to, vp, viewport, near, far);
        s.Mode = ModeNone;
        Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
        Gl.Disable(Gl.BLEND); AlloyRenderer._blendOn = false;
        Gl.DepthMask(1);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)cur);
        Gl.MemoryBarrier(Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT | Gl.FRAMEBUFFER_BARRIER_BIT);
        Gl.BindImageTexture(ImgLayer, s.Layer, 0, 0, 0, Gl.READ_ONLY, Gl.RGBA16F);
        Gl.BindImageTexture(ImgSurf, s.Surf, 0, 0, 0, Gl.READ_ONLY, Gl.R32F);
        s.Live = true;
        s.Frames++; s.Groups += to - from;
    }

    /// <summary>Set the mode a pass draws in: <see cref="ModeFold"/> for the opaque pass, <see cref="ModeSkip"/>
    /// for the blended one, <see cref="ModeNone"/> after. Only takes while the layer is live.</summary>
    internal static void Use(int mode) => _s.Mode = _s.Live ? mode : ModeNone;

    /// <summary>After the blended pass: the layer's readers are done, so its image units are handed back.</summary>
    internal static void End()
    {
        var s = _s;
        s.Mode = ModeNone;
        if (!s.Live) return;
        s.Live = false;
        Gl.BindImageTexture(ImgLayer, 0, 0, 0, 0, Gl.READ_ONLY, Gl.RGBA16F);
        Gl.BindImageTexture(ImgSurf, 0, 0, 0, 0, Gl.READ_ONLY, Gl.R32F);
    }

    private static bool Ensure(int w, int h)
    {
        var s = _s;
        if (s.Fbo != 0 && s.W == w && s.H == h) return true;
        if (w <= 0 || h <= 0) return false;
        if (Gl.BlendFuncSeparatei == null || Gl.BlendFunci == null || Gl.BindImageTexture == null
            || Gl.MemoryBarrier == null || Gl.ClearBufferfv == null || Gl.DrawBuffers == null)
            return Fail("glBlendFuncSeparatei, glBindImageTexture or glMemoryBarrier is unresolved");
        RenderTarget.Create(ref s.Layer, Gl.RGBA16F, w, h, Gl.NEAREST);
        RenderTarget.Create(ref s.Surf, Gl.R32F, w, h, Gl.NEAREST);
        if (s.Fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); s.Fbo = f; }
        int cur; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &cur);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, s.Fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, s.Layer, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT1, Gl.TEXTURE_2D, s.Surf, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)cur);
        s.W = w; s.H = h;
        if (status != Gl.FRAMEBUFFER_COMPLETE) return Fail($"its framebuffer is incomplete (0x{status:x})");
        s.Log?.Info($"DecalLayer: {w}x{h}, RGBA16F albedo layer + R32F surface depth ({(long)w * h * 12 >> 20} MiB)");
        return true;
    }

    private static bool Fail(string why)
    {
        _s.Failed = true;
        _s.Log?.Info($"DecalLayer: STOOD DOWN -- {why}; ground decals blend over the ground the old way for the session");
        return false;
    }

    internal static string Stats(int frames)
    {
        var s = _s;
        long f = s.Frames, g = s.Groups;
        s.Frames = 0; s.Groups = 0;
        return s.Failed ? "decals STOOD DOWN" : f == 0 ? "decals idle"
             : $"decals {(float)g / f:F1} batches/frame folded on {(float)f / System.Math.Max(frames, 1) * 100f:F0}% of frames";
    }

    /// <summary>Off-device: the C# image units are the ones the lit stage declares, and the stage's writer and its
    /// blended-pass drop ask the same <c>CxDecalOn</c>. Oracle: the embedded GLSL text.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DecalLayer.SelfCheck");
        string head = AlloyRenderer.OwnedLitFsHead, synth = ShaderSource.Get("cx_ownlit_synth.glsl");
        check.Expect(head.Contains($"layout(rgba16f, binding = {ImgLayer}) readonly uniform image2D uDecalLayer;"),
          "the albedo layer's image unit is the one cx_ownlit_head declares");
        check.Expect(head.Contains($"layout(r32f, binding = {ImgSurf}) readonly uniform image2D uDecalSurf;"),
          "the surface depth's image unit is the one cx_ownlit_head declares");
        check.Expect(synth.Contains("!CxDecalOn(z, vDepth)) discard;")
          && synth.Contains("uDecal == 2 && CxDecalOn(imageLoad(uDecalSurf, ivec2(gl_FragCoord.xy)).r, vDepth)) discard;"),
          "the writer and the blended pass split decal fragments on the same test");
        check.Expect(synth.Contains($"uDecal == {ModeFold}") && synth.Contains($"uDecal == {ModeSkip}"),
          "the fold and skip modes are the values the stage tests");
        log.Info(check.Ok ? "DecalLayer.SelfCheck: passed" : "DecalLayer.SelfCheck: FAILED");
    }
}
