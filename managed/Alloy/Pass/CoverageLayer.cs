using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// The alpha-to-coverage layer -- the one multisampled target in the Alloy frame, for the cutout parts whose
/// material asks for coverage instead of a hard cut (<c>parameter int NWN_ALPHA_MODE 2</c> on any
/// <c>.mtr</c>). <see cref="Begin"/> seeds every sample from the finished lit frame, the parts draw forward
/// with coverage from their alpha, and <see cref="End"/> resolves colour and depth back.
/// </summary>
internal static unsafe class CoverageLayer
{
    /// <summary>The alpha below which a fragment is discarded outright: a coverage of nothing at any sample count.</summary>
    internal const float Cut = 0.01f;

    /// <summary>Does a coverage part with this shader feature take its coverage from the DIFFUSE alpha? The
    /// one place a hosted program's own source plugs in, asked by the colour draw and by the shadow caster so
    /// the two cannot answer differently. ponytail: no hosted source is read yet; the upgrade is a hosted
    /// sample-mask variant naming its own channel, and the answer here turning true for it.</summary>
    internal static bool FromDiffuse(int feat) => !ShaderFeatures.Hosted(feat);

    private static readonly StandDown _down = new("CoverageLayer");
    private static ExtenderLog? _log;
    /// <summary>Everything the GL context owns, in one object: context loss replaces it, so no name from the
    /// dead context survives as a field nobody remembered to zero. The run's rectangle and the two restore
    /// latches ride here too -- they are read by the <see cref="End"/> of the same frame that set them, and no
    /// frame spans a context loss.</summary>
    private sealed class CoverageState
    {
        public uint Fbo, ResolveFbo, Col, Dep, ProgSeed, ProgDepth;
        public int W, H, Samples;
        /// <summary>The driver's sample ceiling, queried once per context.</summary>
        public int MaxSamples;
        public int USeedHdr = -1, USeedDepth = -1, UDepthMs = -1, UDepthN = -1;
        /// <summary>The run's viewport-space rectangle: seeded, scissored and resolved over.</summary>
        public int Rx, Ry, Rw, Rh;
        /// <summary>Frames with nothing for the layer, counting towards <see cref="IdleFrames"/>.</summary>
        public int Idling;
        /// <summary>Multisampling and face culling as the caller had them, restored at <see cref="End"/>.</summary>
        public bool MsWas, CullWas;
    }

    private static CoverageState _gl = new();
    private static long _runs, _pixels, _frameArea, _released;
    /// <summary>Frames with nothing for the layer before its targets are freed: they are full-frame and
    /// multisampled (177 MB at 4x and 2560x1440), so an area without coverage content should not keep them.</summary>
    private const int IdleFrames = 600;

    /// <summary>Will the layer try to open this frame? False hands the coverage tail back to the cutout pass.</summary>
    internal static bool Armed => Renderer.A2c.V >= 2 && !_down.Failed;
    /// <summary>The armed layer's sample count, for the mask variant's <c>uA2cSamples</c>.</summary>
    internal static int Samples => _gl.Samples;
    /// <summary>The layer shapes its own sample mask this run (<c>alloy.a2cmask</c>, and the variant built).</summary>
    internal static bool Masked { get; private set; }

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Context loss: every name went with the context.</summary>
    internal static void Forget()
    {
        _gl = new CoverageState();
        _down.Forget();
    }

    /// <summary>Open the layer over the viewport-space rectangle (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="w"/>, <paramref name="h"/>): seed it from the frame and leave it bound for the coverage
    /// draws. False = the layer is off or stood down; the caller draws the parts as cutouts instead.</summary>
    internal static bool Begin(int* viewport, int x, int y, int w, int h)
    {
        int want = Renderer.A2c.V;
        if (want < 2 || _down.Failed || w <= 0 || h <= 0) return false;
        if (Gl.TextureStorage2DMultisample == null || Gl.BlitFramebuffer == null)
            return _down.Fail(_log, "glTextureStorage2DMultisample is missing", "NWN_ALPHA_MODE 2 parts draw as cutouts");
        if (_gl.MaxSamples == 0) { int m = 0; Gl.GetIntegerv(Gl.MAX_SAMPLES, &m); _gl.MaxSamples = m; }
        int samples = want;
        while (samples > _gl.MaxSamples && samples > 2) samples >>= 1;
        if (samples > _gl.MaxSamples)
            return _down.Fail(_log, "the driver has no multisampled targets", "NWN_ALPHA_MODE 2 parts draw as cutouts");
        int fw = viewport[0] + viewport[2], fh = viewport[1] + viewport[3];
        if (!EnsureTargets(fw, fh, samples) || !EnsurePrograms()) return false;
        Masked = Renderer.A2cMask.V && Programs.EnsureMaskProgram();

        _gl.Rx = x; _gl.Ry = y; _gl.Rw = w; _gl.Rh = h; _gl.Idling = 0;
        _gl.MsWas = Gl.IsEnabled(Gl.MULTISAMPLE) != 0; _gl.CullWas = Gl.IsEnabled(Gl.CULL_FACE) != 0;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.Fbo);
        Gl.Enable(Gl.SCISSOR_TEST);
        Gl.Scissor(x, y, w, h);
        Gl.UseProgram(_gl.ProgSeed);
        Gl.BindTextureUnit(TextureSlots.CoverSeed, PostChain.HdrTex);
        Gl.BindTextureUnit(TextureSlots.CoverSeedDepth, PostChain.DepthTex);
        Gl.Uniform1i(_gl.USeedHdr, (int)TextureSlots.CoverSeed);
        Gl.Uniform1i(_gl.USeedDepth, (int)TextureSlots.CoverSeedDepth);
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthFunc(Gl.ALWAYS); Gl.DepthMask(1);
        Gl.Disable(Gl.BLEND); Gl.Disable(Gl.CULL_FACE);
        Gl.BindVertexArray(PostChain.FullscreenVao);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.BindVertexArray(0);
        Gl.BindTextureUnit(TextureSlots.CoverSeed, 0);
        Gl.BindTextureUnit(TextureSlots.CoverSeedDepth, 0);
        Gl.DepthFunc(Gl.LEQUAL);
        Gl.Enable(Gl.MULTISAMPLE);
        if (!Masked) Gl.Enable(Gl.SAMPLE_ALPHA_TO_COVERAGE);
        _runs++; _pixels += (long)w * h; _frameArea += (long)viewport[2] * viewport[3];
        return true;
    }

    /// <summary>Close the layer: colour box-resolved into the HDR target, depth written back where the layer
    /// covered half a pixel or more, and the frame's framebuffer bound again with the depth test at LEQUAL.
    /// The G-buffer, light record and SSR record under such a pixel are still the BACKGROUND's; each reader
    /// drops a record whose view depth no longer matches the frame's.</summary>
    internal static void End(int* viewport)
    {
        Gl.Disable(Gl.SAMPLE_ALPHA_TO_COVERAGE);
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, _gl.Fbo);
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, _gl.ResolveFbo);
        // the HDR target is re-made on a resize, and this layer may not be
        Gl.FramebufferTexture2D(Gl.DRAW_FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, PostChain.HdrTex, 0);
        Gl.BlitFramebuffer(_gl.Rx, _gl.Ry, _gl.Rx + _gl.Rw, _gl.Ry + _gl.Rh, _gl.Rx, _gl.Ry, _gl.Rx + _gl.Rw, _gl.Ry + _gl.Rh, Gl.COLOR_BUFFER_BIT, Gl.NEAREST);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, PostChain.Fbo);
        Gl.UseProgram(_gl.ProgDepth);
        Gl.BindTextureUnit(TextureSlots.CoverSeedDepth, _gl.Dep);
        Gl.Uniform1i(_gl.UDepthMs, (int)TextureSlots.CoverSeedDepth);
        Gl.Uniform1i(_gl.UDepthN, _gl.Samples);
        Gl.ColorMask(0, 0, 0, 0);
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthFunc(Gl.LESS); Gl.DepthMask(1);
        Gl.Disable(Gl.CULL_FACE);
        Gl.BindVertexArray(PostChain.FullscreenVao);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.BindVertexArray(0);
        Gl.ColorMask(1, 1, 1, 1);
        Gl.DepthFunc(Gl.LEQUAL);
        Gl.Disable(Gl.SCISSOR_TEST);
        if (!_gl.MsWas) Gl.Disable(Gl.MULTISAMPLE);
        if (_gl.CullWas) Gl.Enable(Gl.CULL_FACE);
        Gl.BindTextureUnit(TextureSlots.CoverSeedDepth, 0);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
    }

    /// <summary>A frame with no coverage content: after <see cref="IdleFrames"/> of them the targets go.</summary>
    internal static void Idle()
    {
        if (_gl.Col == 0 || ++_gl.Idling < IdleFrames) return;
        Release();
        _released++;
    }

    private static void Release()
    {
        if (_gl.Col != 0) { uint c = _gl.Col; Gl.DeleteTextures(1, &c); }
        if (_gl.Dep != 0) { uint d = _gl.Dep; Gl.DeleteTextures(1, &d); }
        _gl.Col = 0; _gl.Dep = 0; _gl.W = 0; _gl.H = 0; _gl.Samples = 0; _gl.Idling = 0;
    }

    private static bool EnsureTargets(int w, int h, int samples)
    {
        if (_gl.Col != 0 && w == _gl.W && h == _gl.H && samples == _gl.Samples) return true;
        Release();
        if (_gl.Fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); _gl.Fbo = f; Gl.GenFramebuffers(1, &f); _gl.ResolveFbo = f; }
        uint t;
        Gl.CreateTextures(Gl.TEXTURE_2D_MULTISAMPLE, 1, &t); _gl.Col = t;
        Gl.TextureStorage2DMultisample(_gl.Col, samples, Gl.RGBA16F, w, h, 1);
        Gl.CreateTextures(Gl.TEXTURE_2D_MULTISAMPLE, 1, &t); _gl.Dep = t;
        // 32F holds the seeded D24 value exactly, so the write-back's GL_LESS fails on an untouched pixel
        Gl.TextureStorage2DMultisample(_gl.Dep, samples, Gl.DEPTH_COMPONENT32F, w, h, 1);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.Fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D_MULTISAMPLE, _gl.Col, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D_MULTISAMPLE, _gl.Dep, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.ResolveFbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, PostChain.HdrTex, 0);
        uint resolve = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, PostChain.Fbo);
        if (status != Gl.FRAMEBUFFER_COMPLETE || resolve != Gl.FRAMEBUFFER_COMPLETE)
        {
            _log?.Info($"CoverageLayer: {samples}x {w}x{h} layer 0x{status:x}, resolve 0x{resolve:x}");
            Release();
            return _down.Fail(_log, "the multisampled layer is incomplete", "NWN_ALPHA_MODE 2 parts draw as cutouts");
        }
        _gl.W = w; _gl.H = h; _gl.Samples = samples;
        _log?.Info($"CoverageLayer: {samples}x RGBA16F + D32F at {w}x{h} ({(long)w * h * samples * 12 / (1024 * 1024)} MB)");
        return true;
    }

    private static bool EnsurePrograms()
    {
        if (_gl.ProgSeed != 0) return true;
        _gl.ProgSeed = GlProgram.Graphics(PostChain.FullscreenVs, ShaderSource.Get("cx_a2cseed_f.glsl"), _log, "CoverageLayer seed");
        _gl.ProgDepth = GlProgram.Graphics(PostChain.FullscreenVs, ShaderSource.Get("cx_a2cdepth_f.glsl"), _log, "CoverageLayer depth");
        if (_gl.ProgSeed == 0 || _gl.ProgDepth == 0)
        {
            if (_gl.ProgSeed != 0) Gl.DeleteProgram(_gl.ProgSeed);
            if (_gl.ProgDepth != 0) Gl.DeleteProgram(_gl.ProgDepth);
            _gl.ProgSeed = 0; _gl.ProgDepth = 0;
            return _down.Fail(_log, "a layer program did not build", "NWN_ALPHA_MODE 2 parts draw as cutouts");
        }
        _gl.USeedHdr = GlProgram.Loc(_gl.ProgSeed, "uHdr"); _gl.USeedDepth = GlProgram.Loc(_gl.ProgSeed, "uDepth");
        _gl.UDepthMs = GlProgram.Loc(_gl.ProgDepth, "uDepthMs"); _gl.UDepthN = GlProgram.Loc(_gl.ProgDepth, "uSamples");
        return true;
    }

    internal static string Stats(int frames)
    {
        long n = _runs, px = _pixels, area = _frameArea, rel = _released;
        _runs = 0; _pixels = 0; _frameArea = 0; _released = 0;
        if (_down.Failed) return $"a2c STOOD DOWN ({_down.Reason})";
        if (n == 0) return Renderer.A2c.V < 2 ? "a2c off" : rel > 0 ? "a2c idle, targets released" : "a2c idle";
        return $"a2c {(float)n / System.Math.Max(frames, 1):F2} runs/frame, {_gl.Samples}x{(Masked ? " masked" : "")}, " +
               $"scissor {100.0 * px / System.Math.Max(area, 1):F1}% of the frame";
    }
}
