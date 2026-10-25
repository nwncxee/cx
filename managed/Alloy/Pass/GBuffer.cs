using System;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The deferred G-buffer (ADR 0029) -- two render-size RGBA32UI textures and an R32UI the colour pass's
/// <c>CX_DEFERRED</c> twin writes on draw buffers 4..6 and <see cref="DeferredLights"/> reads,
/// in <c>cx_gbuf.glsl</c>'s lane format. It also owns the colour pass's whole draw-buffer list, because that
/// list is one piece of framebuffer state and <see cref="Ssr"/>'s record pair at 2..3 lives in it too.</summary>
internal static unsafe class GBuffer
{
    /// <summary>Draw-buffer index of G0; G1 and G2 follow it. 0..3 are the colour, the vis-ID and Ssr's record pair.</summary>
    internal const int FirstAttachment = 4;
    /// <summary>Targets, G0..G2.</summary>
    internal const int Targets = 3;
    /// <summary>Bytes a pixel the three targets and the light record cost together, for the allocation line.</summary>
    private const int BytesPerPixel = 2 * 16 + 4 + 16;

    private static ExtenderLog? _log;
    private static readonly StandDown _down = new("GBuffer");

    /// <summary>Everything the GL context owns, in one object: context loss replaces it, so no texture name,
    /// size or attachment latch from the dead context survives as a field nobody remembered to zero. The two
    /// per-frame flags ride here because no frame spans a context loss.</summary>
    private sealed class GBufferState
    {
        /// <summary>The three texture names, 0 until <see cref="Ensure"/> has allocated them.</summary>
        public uint Tex0, Tex1, Tex2;
        /// <summary>The light record (cx_gbuf's <c>CxLightRecPack</c>): written by <see cref="DeferredLights"/>
        /// over the whole rect, debited by <see cref="PairDebt"/>, read by the blended class's lit fragments.</summary>
        public uint TexLight;
        /// <summary>This frame's deferred pass wrote <see cref="TexLight"/>. Reset by <see cref="BeginFrame"/>.</summary>
        public bool LightLive;
        /// <summary>A twin draw wrote this frame: the deferred pass has work. Reset by <see cref="BeginFrame"/>.</summary>
        public bool Written;
        public int W, H;
        /// <summary>Attached to the frame FBO right now, so <see cref="ApplyDrawBuffers"/> routes 4..5 at it.</summary>
        public bool Armed;
        /// <summary>The frame FBO is carrying a draw-buffer list of ours and has not been handed its single
        /// default back. Without it a pass that arms nothing would still issue the restore on whatever FBO is
        /// bound -- the probe capture's, during a capture.</summary>
        public bool Wrote;
        public bool Checked;
    }

    private static GBufferState _gl = new();
    private static int _clears;

    /// <inheritdoc cref="GBufferState.TexLight"/>
    internal static uint TexLight => _gl.TexLight;
    /// <summary>Lane <paramref name="i"/>'s texture (0..2), 0 before <see cref="Ensure"/>: the Vulkan A/B reads them back.</summary>
    internal static uint Tex(int i) => i switch { 0 => _gl.Tex0, 1 => _gl.Tex1, _ => _gl.Tex2 };
    /// <inheritdoc cref="GBufferState.LightLive"/>
    internal static bool LightLive { get => _gl.LightLive; set => _gl.LightLive = value; }

    /// <summary>Allocated at the current render size.</summary>
    internal static bool Ready => _gl.Tex0 != 0 && !_down.Failed;

    /// <summary>Bind texture <paramref name="tex"/> on <paramref name="unit"/>, leaving it the active unit.</summary>
    internal static void BindUnit(uint unit, uint tex)
    {
        Gl.ActiveTexture(Gl.TEXTURE0 + unit);
        Gl.BindTexture(Gl.TEXTURE_2D, tex);
    }

    /// <summary>Bind the three G-buffer targets, the frame's depth and the lightmap record buffer the slot lane
    /// indexes, for a compute pass to read. The depth is read while attached: a dispatch touches no framebuffer,
    /// so there is no feedback loop, and the lit stage samples it at the same time (<c>SsaoPass.BindForDraw</c>).</summary>
    internal static void BindForCompute()
    {
        BindUnit(TextureSlots.Gbuf0, _gl.Tex0);
        BindUnit(TextureSlots.Gbuf1, _gl.Tex1);
        BindUnit(TextureSlots.Gbuf2, _gl.Tex2);
        BindUnit(TextureSlots.GbufDepth, PostChain.DepthTex);
        LightmapBake.BindChartBuffer();
    }

    /// <summary>Unbind the textures <see cref="BindForCompute"/> bound; the chart buffer is a shared point its next reader rebinds.</summary>
    internal static void UnbindCompute()
    {
        BindUnit(TextureSlots.Gbuf0, 0);
        BindUnit(TextureSlots.Gbuf1, 0);
        BindUnit(TextureSlots.Gbuf2, 0);
        BindUnit(TextureSlots.GbufDepth, 0);
    }

    /// <summary>Attached to the frame FBO right now -- the opaque and cutout passes. Read by <see cref="Issue"/>,
    /// which masks the G-buffer's draw buffers per program while it is true.</summary>
    internal static bool Armed => _gl.Armed;

    /// <summary>Latched: the G-buffer cannot work on this context, so there is no deferred pass this session.
    /// Read by <see cref="DeferredLights"/>, which must then leave <c>Active</c> false.</summary>
    internal static bool Failed => _down.Failed;

    /// <inheritdoc cref="GBufferState.Written"/>
    internal static bool Written { get => _gl.Written; set => _gl.Written = value; }

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>(Re)allocate at the render size. False = allocation failed (said loudly, once).</summary>
    internal static bool Ensure(int w, int h)
    {
        if (_down.Failed) return false;
        var g = _gl;
        if (g.Tex0 != 0 && g.W == w && g.H == h) return true;
        if (w <= 0 || h <= 0) return false;
        if (Gl.DrawBuffers == null || Gl.ClearTexImage == null || Gl.ColorMaski == null)
            return _down.Fail(_log, "glDrawBuffers, glClearTexImage or glColorMaski is unresolved",
                              "deferred point lights are off for the session -- the forward loop walks every light");
        // immutable storage, one level: a new size is a new name, and an integer texture under LINEAR is
        // incomplete and samples zero
        RenderTarget.Create(ref g.Tex0, Gl.RGBA32UI, w, h, Gl.NEAREST);
        RenderTarget.Create(ref g.Tex1, Gl.RGBA32UI, w, h, Gl.NEAREST);
        RenderTarget.Create(ref g.Tex2, Gl.R32UI, w, h, Gl.NEAREST);
        RenderTarget.Create(ref g.TexLight, Gl.RGBA32UI, w, h, Gl.NEAREST);
        g.W = w; g.H = h; g.Checked = false;
        _log?.Info($"GBuffer: {w}x{h}, G0..G1 RGBA32UI + G2 R32UI + the light record RGBA32UI ({(long)w * h * BytesPerPixel >> 20} MiB)");
        return true;
    }

    /// <summary>Before the opaque pass: clear G0 to zero (flags 0 = "not mine") and reset <see cref="Written"/>.
    /// G1 and G2 are never cleared -- G0's flag byte gates every read of them.</summary>
    internal static void BeginFrame()
    {
        _gl.Written = false; _gl.LightLive = false;
        if (!Ready) return;
        Gl.ClearTexImage(_gl.Tex0, 0, Gl.RGBA_INTEGER, Gl.UNSIGNED_INT, null);
        _clears++;
    }

    /// <summary>Attach G0..G2 at COLOR4..6 of the bound frame FBO for the opaque and cutout passes.</summary>
    internal static void AttachColourPass()
    {
        if (!Ready || _gl.Armed) return;
        for (uint i = 0; i < Targets; i++)
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT4 + i, Gl.TEXTURE_2D, Tex(i), 0);
        _gl.Armed = true;
        ApplyDrawBuffers();
        // once per allocation, not per frame: driver-side validation is a CPU cost inside the Opaque term
        if (_gl.Checked) return;
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            Detach();
            _down.Fail(_log, $"the frame target is incomplete with the G-buffer attached (0x{status:x})",
                       "deferred point lights are off for the session -- the forward loop walks every light");
            return;
        }
        _gl.Checked = true;
    }

    /// <summary>After the cutout pass: detach, so no later pass writes into the G-buffer. Every frame that
    /// reached <see cref="AttachColourPass"/> must come here -- the draw-buffer state is the FBO's own and
    /// outlives the frame.</summary>
    internal static void Detach()
    {
        if (!_gl.Armed) return;
        _gl.Armed = false;
        ApplyDrawBuffers();
        MaskColour(true);   // the masks are FBO-independent GL state: hand them back write-enabled
        for (uint i = 0; i < Targets; i++)
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT4 + i, Gl.TEXTURE_2D, 0, 0);
    }

    /// <summary>Write-enable the G-buffer draw buffers for the twin, mask them OFF for every other
    /// program drawing in the same passes. Only the twin declares outputs there, and a draw buffer no output
    /// is written to is left UNDEFINED rather than left alone -- masked, a forward draw over a twin's pixel
    /// leaves the twin's record standing, which the reader's own depth test is there to reject.</summary>
    internal static void MaskColour(bool twin)
    {
        byte b = twin ? (byte)1 : (byte)0;
        for (uint i = 0; i < Targets; i++) Gl.ColorMaski((uint)FirstAttachment + i, b, b, b, b);
    }

    /// <summary>The frame FBO's draw-buffer list, composed from both armed states: the colour at 0, the OIT
    /// reveal slot at 1 (NONE in these passes), Ssr's records at 2..3 and the G-buffer at 4..6.
    /// <paramref name="suspend"/> is the sky, which writes neither -- its pixels keep G0 = 0 and no record.</summary>
    internal static void ApplyDrawBuffers(bool suspend = false)
    {
        bool ssr = Ssr.RecordsArmed && !suspend, gb = _gl.Armed && !suspend;
        if (!ssr && !gb && !_gl.Wrote) return;
        if (Gl.DrawBuffers == null) return;
        uint* bufs = stackalloc uint[FirstAttachment + Targets];
        bufs[0] = Gl.COLOR_ATTACHMENT0;
        int n = 1;
        if (ssr || gb)
        {
            bufs[1] = Gl.NONE;
            bufs[2] = ssr ? Gl.COLOR_ATTACHMENT2 : Gl.NONE;
            bufs[3] = ssr ? Gl.COLOR_ATTACHMENT3 : Gl.NONE;
            n = FirstAttachment;
            if (gb)
            {
                for (uint i = 0; i < Targets; i++) bufs[FirstAttachment + i] = Gl.COLOR_ATTACHMENT4 + i;
                n = FirstAttachment + Targets;
            }
        }
        Gl.DrawBuffers(n, bufs);
        _gl.Wrote = n > 1 || Ssr.RecordsArmed || _gl.Armed;
    }

    /// <summary>Context loss: the names are gone.</summary>
    internal static void Forget()
    {
        _gl = new GBufferState();
        _down.Forget();
    }

    internal static string Stats(int frames)
    {
        int n = _clears; _clears = 0;
        return _down.Failed ? "gbuf STOOD DOWN" : !Lighting.Deferred.V ? "gbuf off" : n == 0 ? "gbuf idle"
             : $"gbuf {_gl.W}x{_gl.H} ({(float)n / MathF.Max(frames, 1):F2} clears/frame, "
             + $"{(long)_gl.W * _gl.H * BytesPerPixel >> 20} MiB)";
    }

    private static uint Tex(uint i) => i == 0 ? _gl.Tex0 : i == 1 ? _gl.Tex1 : _gl.Tex2;
}
