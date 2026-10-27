using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Order-independent transparency for the blended class, two modes behind <c>/alloy oit</c>. Mode 1:
/// weighted blended (McGuire &amp; Bavoil 2013) -- one unsorted pass, depth-tested and writing no depth, into
/// an accumulation pair <see cref="End"/> composites in one full-screen draw; approximate, stacked layers
/// blur. Mode 2 (default): per-pixel linked lists, sorted and composited exactly, with the pair as its net.</summary>
internal static unsafe class OitPass
{
    private const uint R16F = 0x822D, RGBA32F = 0x8814;   // not-an-offset: GL internal-format enums
    /// <summary>SSBO binding points of the head table (count at 0, then one head per pixel) and the node pool.</summary>
    private const uint BindHeads = SsboSlots.OitHeads, BindNodes = SsboSlots.OitNodes;
    private const int NodeBytes = 16;
    /// <summary>Mode 2 frames the weighted pair stays written after an overflow reads back.
    /// ponytail: a fixed ~2 s at 60 fps, unmeasured; tie it to the overflow rate if bursts outlast it.</summary>
    private const long NetFrames = 120;
    /// <summary>Mode 2's object table for the frame: a creature's parts share one id, and the resolve composites
    /// the nearest node of an id once, so a ghosted creature is translucent as a whole rather than per part.
    /// ponytail: 255 ids a frame, first come; a 256th object gets 0 (per-part again). Linear scan, ~100 batches.</summary>
    private static readonly IntPtr[] _gobs = new IntPtr[255];
    internal static int GobId(IntPtr gob)
    {
        var g = _gl;
        if (gob == IntPtr.Zero || !g.Linked) { _gobZero++; return 0; }
        for (int i = 0; i < g.GobN; i++) if (_gobs[i] == gob) return i + 1;
        if (g.GobN == _gobs.Length) return 0;
        _gobs[g.GobN++] = gob;
        return g.GobN;
    }
    /// <summary>The head table: the node count, then one head per pixel.</summary>
    private static long HeadBytes(int w, int h) => 4L + 4L * w * h;
    /// <summary><c>oitlayers</c> in cx.cfg: node pool capacity as an average of nodes per pixel. 16 bytes a
    /// node, so 3 at 1080p is ~100 MB, allocated only once mode 2 is first used.</summary>
    private static readonly int Layers = Renderer.OitLayers.Boot;
    /// <summary><c>/alloy oit</c>: 0 = the sorted, depth-writing pass (A/B instrument), 1 = weighted blended, 2 = linked lists
    /// (default since the plaza A/B measured it at parity with 1; 1 is the fallback a refused pool lands on).</summary>
    internal static int Mode => Renderer.Oit.V;
    /// <summary>True while the lit twin that carries the append block (<see cref="WithLists"/>) exists. Without it
    /// mode 2 stands down to the weighted pair: a linked resolve over lists nothing appended to draws nothing.</summary>
    internal static bool ListsArmed { get => _gl.ListsArmed; set => _gl.ListsArmed = value; }
    /// <summary>True inside a bracket that is resolving through the lists: IssueColour binds the twin then.</summary>
    internal static bool Linked => _gl.Linked;
    /// <summary>The define that compiles the append block into a fragment stage, after its #version line.</summary>
    internal static string WithLists(string fs)
    {
        int v = fs.IndexOf("#version", StringComparison.Ordinal);
        if (v < 0) return fs;
        return fs.Insert(fs.IndexOf('\n', v) + 1, "#define CX_OIT_LISTS 1\n");
    }
    /// <summary>The count comes back through a GPU copy into a persistently mapped ring, read two frames later
    /// behind a fence: no GL call in the frame ever waits.</summary>
    private const int ReadSlots = 3;

    /// <summary>Everything the GL context owns, in one object: context loss replaces it, so no name, mapped
    /// pointer or fence from the dead context survives as a field nobody remembered to zero. The bracket's
    /// per-frame latches ride here too -- a frame cannot span a context loss.</summary>
    private sealed class OitState
    {
        public uint Fbo, Accum, Reveal, Prog, Nodes;
        /// <summary>The head table, cleared on the GPU by <see cref="ProgClear"/> (a full-screen draw writing
        /// zeros) rather than by glClearBufferSubData: any CPU-issued write to a buffer the GPU still holds is
        /// an implicit sync, and one per frame serialised the CPU and GPU -- the plaza's frame rate halved.</summary>
        public uint Heads, ProgClear;
        public int UClearStride;
        public uint ReadBuf;
        public uint* ReadMap;
        public readonly IntPtr[] Fence = new IntPtr[ReadSlots];
        public long Frame, NetUntil;
        public int W, H, UAccum, UReveal, UViz, UMode, UDim, UNet, Cap;
        public bool Failed, ListsFailed, Linked, Net, EngagedLogged;
        /// <summary>The lit twin that carries the append block: a program name, so the context owns it.</summary>
        public bool ListsArmed;
        /// <summary>The frame's own draw framebuffer, read at <see cref="Begin"/> and restored by
        /// <see cref="End"/>.</summary>
        public int RestoreFbo;
        /// <summary>How many object ids this frame's table holds.</summary>
        public int GobN;
    }

    private static OitState _gl = new();
    private static long _skippedReads, _netRuns;
    /// <summary><c>/alloy oitviz</c>: 1 = revealage as grey, 2 = accumulated mean colour, 3 = accumulated weight/10,
    /// 4 = list length/16 (mode 2), 5 = the nearest node's object id as a colour (mode 2). Unblended.</summary>
    internal static int Viz => Renderer.OitViz.V;
    private static ExtenderLog? _log;
    private static long _runs, _linkedRuns, _overflowFrames, _nodesPeak, _nodesSum, _gobPeak, _gobZero;

    internal static void Init(ExtenderLog log) => _log = log;
    /// <summary>A program's <c>uOit</c> (0 outside the bracket, else the mode -- 1 when the lists refused)
    /// and <c>uOitDim</c> (head-table stride in pixels, node capacity), set on bind.</summary>
    internal static void Upload(int locOit, int locDim, bool inBracket)
    {
        var g = _gl;
        Gl.Uniform1i(locOit, !inBracket ? 0 : g.Linked ? 2 : 1);
        UploadDim(locDim);
    }
    private static void UploadDim(int loc)
    {
        var g = _gl;
        int* dim = stackalloc int[2] { g.W, g.Cap };
        Gl.Uniform2iv(loc, 1, dim);
    }

    /// <summary>Will <see cref="Begin"/> open the bracket this frame, as far as the route can tell before it? The route
    /// then orders the blended range by state, not distance. What draws that range outside the bracket draws it in that
    /// order: the env-probe capture, and a frame <see cref="Begin"/> refuses with no scene depth or an empty viewport.</summary>
    internal static bool OrderFree => !_gl.Failed && Mode != 0;

    /// <summary>Will the list resolve composite each object id once this frame? Never: Vulkan has no lists, so an
    /// object's overlapping parts thicken it as the weighted modes do.</summary>
    internal static bool Dedupes => false;

    /// <summary>Open the accumulation pair for the blended range. False = draw the old way this frame.</summary>
    internal static bool Begin(uint depthTex, int* viewport)
    {
        var g = _gl;
        if (!OrderFree || depthTex == 0) return false;
        int w = viewport[0] + viewport[2], h = viewport[1] + viewport[3];
        if (!EnsureTargets(w, h) || !EnsurePrograms()) return false;
        g.Linked = Mode == 2 && ListsArmed && EnsureLists(w, h);
        g.GobN = 0;
        if (g.Linked) { g.Frame++; if (ReadOverflow()) g.NetUntil = g.Frame + NetFrames; }
        g.Net = !g.Linked || g.Frame < g.NetUntil || Viz is >= 1 and <= 3;
        if (g.Linked && g.Net) _netRuns++;
        int cur; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &cur); g.RestoreFbo = cur;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, g.Fbo);
        // Both modes test against the scene depth in hardware, read-only. Nothing in the bracket may SAMPLE it: a
        // depth texture sampled while attached is decompressed by the driver, which taxed the next frame's opaque pass.
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, depthTex, 0);
        uint* bufs = stackalloc uint[2] { Gl.COLOR_ATTACHMENT0, Gl.COLOR_ATTACHMENT1 };
        if (!g.Net) { bufs[0] = Gl.NONE; bufs[1] = Gl.NONE; }
        Gl.DrawBuffers(2, bufs);
        if (g.Net)
        {
            float* zero = stackalloc float[4] { 0f, 0f, 0f, 0f };
            float* one = stackalloc float[4] { 1f, 1f, 1f, 1f };
            Gl.ClearBufferfv(Gl.COLOR, 0, zero);
            Gl.ClearBufferfv(Gl.COLOR, 1, one);
        }
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthFunc(Gl.LEQUAL); Gl.DepthMask(0);
        Gl.Enable(Gl.BLEND);
        Gl.BlendFunci(0, Gl.ONE, Gl.ONE);
        Gl.BlendFunci(1, Gl.ZERO, Gl.ONE_MINUS_SRC_COLOR);
        if (g.Linked)
        {
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindHeads, g.Heads);
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindNodes, g.Nodes);
            // last frame's resolve read the table; this frame's clear writes it, then the appends read it
            Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT);
            Gl.UseProgram(g.ProgClear);
            Gl.Uniform1i(g.UClearStride, g.W);
            Gl.ColorMask(0, 0, 0, 0); Gl.Disable(Gl.DEPTH_TEST);
            Gl.BindVertexArray(PostChain.FullscreenVao);
            Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
            Gl.BindVertexArray(0);
            Gl.ColorMask(1, 1, 1, 1); Gl.Enable(Gl.DEPTH_TEST);
            Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT);
            _linkedRuns++;
            if (!g.EngagedLogged) { g.EngagedLogged = true; _log?.Info($"OitPass: mode 2 engaged -- {g.W}x{g.H}, cap {g.Cap}, heads {g.Heads}, nodes {g.Nodes}"); }
        }
        _runs++;
        return true;
    }

    /// <summary>Read the node count End copied two frames ago, if its fence has passed. True = it overflowed.</summary>
    private static bool ReadOverflow()
    {
        var g = _gl;
        int slot = (int)((g.Frame + 1) % ReadSlots);
        IntPtr fence = g.Fence[slot];
        if (fence == IntPtr.Zero) return false;
        bool over = false;
        uint st = Gl.ClientWaitSync(fence, 0, 0);
        if (st == Gl.ALREADY_SIGNALED || st == Gl.CONDITION_SATISFIED)
        {
            uint count = g.ReadMap[slot];
            over = count > (uint)g.Cap;
            if (over) _overflowFrames++;
            if (count > _nodesPeak) _nodesPeak = count;
            _nodesSum += count;
        }
        else _skippedReads++;
        Gl.DeleteSync(fence); g.Fence[slot] = IntPtr.Zero;
        return over;
    }

    /// <summary>Composite the pair (or the lists) over the frame and hand the frame's framebuffer back. Leaves
    /// GL_BLEND on with the depth mask off, the state the passes after the blended class expect to find.</summary>
    internal static void End(int* viewport)
    {
        var g = _gl;
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, 0, 0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)g.RestoreFbo);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        if (g.Linked) Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT);
        Gl.UseProgram(g.Prog);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.OitAccum); Gl.BindTexture(Gl.TEXTURE_2D, g.Accum);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.OitReveal); Gl.BindTexture(Gl.TEXTURE_2D, g.Reveal);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.Uniform1i(g.UAccum, (int)TextureSlots.OitAccum); Gl.Uniform1i(g.UReveal, (int)TextureSlots.OitReveal);
        Gl.Uniform1i(g.UViz, Viz);
        Gl.Uniform1i(g.UMode, g.Linked ? 2 : 1);
        Gl.Uniform1i(g.UNet, g.Net ? 1 : 0);
        UploadDim(g.UDim);
        new PassState(depth: false, depthWrite: false, blend: Viz == 0,
                      blendSrc: g.Linked ? Gl.ONE : Gl.ONE_MINUS_SRC_ALPHA, blendDst: Gl.SRC_ALPHA).Apply();
        Gl.BindVertexArray(PostChain.FullscreenVao);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.BindVertexArray(0);
        // The draw loop tracks the depth test as ON and never re-enables it; the water and additive passes
        // after this one would otherwise draw through every wall.
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthFunc(Gl.LEQUAL);
        if (g.GobN > _gobPeak) _gobPeak = g.GobN;
        if (g.Linked)
        {
            int slot = (int)(g.Frame % ReadSlots);
            Gl.MemoryBarrier(Gl.BUFFER_UPDATE_BARRIER_BIT);
            Gl.CopyNamedBufferSubData(g.Heads, g.ReadBuf, IntPtr.Zero, (IntPtr)(slot * 4), (IntPtr)4);
            g.Fence[slot] = Gl.FenceSync(Gl.SYNC_GPU_COMMANDS_COMPLETE, 0);
        }
        g.Linked = false;
    }

    internal static string Stats(int frames)
    {
        var g = _gl;
        long n = _runs, l = _linkedRuns, nr = _netRuns, o = _overflowFrames, peak = _nodesPeak, sum = _nodesSum, gp = _gobPeak, gz = _gobZero, sk = _skippedReads;
        _runs = 0; _linkedRuns = 0; _netRuns = 0; _overflowFrames = 0; _nodesPeak = 0; _nodesSum = 0; _gobPeak = 0; _gobZero = 0; _skippedReads = 0;
        if (g.Failed) return "oit STOOD DOWN";
        if (n == 0) return "oit idle";
        string s = $"oit {(float)n / System.Math.Max(frames, 1):F2} runs/frame at {g.W}x{g.H}";
        if (l > 0) s += $" linked {l} nodes avg {sum / System.Math.Max(l - sk, 1)} peak {peak} ({sk} reads not ready) cap {g.Cap} overflow {o} frames, net {nr} frames, objects {gp} peak/frame, {gz} ungrouped batches" + (g.ListsFailed ? " (lists REFUSED)" : "");
        return s;
    }

    /// <summary>Context loss: the names, the mapped readback and the fences died with it, so the whole GL
    /// state goes at once -- the two stand-down latches with it, since a pool the dead context refused may be
    /// cut on the next one.</summary>
    internal static void ContextLost() => _gl = new OitState();

    private static bool EnsureTargets(int w, int h)
    {
        var g = _gl;
        if (g.Accum != 0 && w == g.W && h == g.H) return true;
        if (w <= 0 || h <= 0) return false;
        if (g.Fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); g.Fbo = f; }
        // 32-bit: the accumulation is HDR colour times a weight up to 3e3, and a few stacked emissive flame
        // layers overflow fp16 to inf -- which resolved as a white patch over the lamp and the torch.
        RenderTarget.Create(ref g.Accum, RGBA32F, w, h, Gl.NEAREST);
        RenderTarget.Create(ref g.Reveal, R16F, w, h, Gl.NEAREST);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, g.Fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, g.Accum, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT1, Gl.TEXTURE_2D, g.Reveal, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            g.Failed = true;
            _log?.Info($"OitPass: the accumulation target is incomplete (0x{status:x}) -- blended draws the sorted way on this context");
            return false;
        }
        g.W = w; g.H = h;
        g.Cap = 0;   // the lists are sized to the target; a resize re-cuts them on the next mode-2 frame
        _log?.Info($"OitPass: accumulation pair {w}x{h} RGBA32F + R16F ready -- blended class is order-independent");
        return true;
    }

    /// <summary>The head tables and node pool for mode 2, cut to the current target. Immutable GPU-only stores.</summary>
    private static bool EnsureLists(int w, int h)
    {
        var g = _gl;
        if (g.ListsFailed) return false;
        if (Gl.FenceSync == null)
        {
            g.ListsFailed = true;
            _log?.Info("OitPass: glFenceSync is unresolved -- mode 2 resolves the weighted way on this context");
            return false;
        }
        int cap = w * h * Layers;
        if (g.Nodes != 0 && cap == g.Cap) return true;
        if (g.Nodes != 0) { uint* old = stackalloc uint[2] { g.Nodes, g.Heads }; Gl.DeleteBuffers(2, old); }
        Gl.DrainErrors();   // drain: the refusal test below must see only this allocation
        uint* b = stackalloc uint[2]; Gl.CreateBuffers(2, b);
        g.Nodes = b[0]; g.Heads = b[1];
        Gl.NamedBufferStorage(g.Nodes, (IntPtr)((long)(cap + 1) * NodeBytes), null, 0);
        Gl.NamedBufferStorage(g.Heads, (IntPtr)HeadBytes(w, h), null, 0);
        if (g.ReadBuf == 0)
        {
            const uint flags = Gl.MAP_READ_BIT | Gl.MAP_PERSISTENT_BIT | Gl.MAP_COHERENT_BIT;
            uint r; Gl.CreateBuffers(1, &r); g.ReadBuf = r;
            Gl.NamedBufferStorage(g.ReadBuf, (IntPtr)(ReadSlots * 4), null, flags);
            g.ReadMap = (uint*)Gl.MapNamedBufferRange(g.ReadBuf, IntPtr.Zero, (IntPtr)(ReadSlots * 4), flags);
        }
        if (g.ProgClear == 0)
        {
            g.ProgClear = GlProgram.Graphics(PostChain.FullscreenVs, OitClearFs, _log, "OitPass clear");
            g.UClearStride = GlProgram.Loc(g.ProgClear, "uStride");
        }
        if (Gl.GetError() != 0 || g.ReadMap == null || g.ProgClear == 0)
        {
            g.ListsFailed = true;
            _log?.Info($"OitPass: the node pool ({(long)cap * NodeBytes >> 20} MB) or its readback refused -- mode 2 resolves the weighted way on this context");
            return false;
        }
        g.Cap = cap;
        Gl.ZeroBuffer(g.Heads, HeadBytes(w, h));   // once, before any GPU use: no sync to take
        _log?.Info($"OitPass: linked lists ready, {cap} nodes ({(long)cap * NodeBytes >> 20} MB, {Layers}/pixel) -- blended class sorts per pixel");
        return true;
    }

    private static bool EnsurePrograms()
    {
        var g = _gl;
        if (g.Prog != 0) return true;
        g.Prog = GlProgram.Graphics(PostChain.FullscreenVs, OitResolveFs, _log, "OitPass resolve");
        if (g.Prog == 0) { g.Failed = true; return false; }
        g.UAccum = GlProgram.Loc(g.Prog, "uAccum"); g.UReveal = GlProgram.Loc(g.Prog, "uReveal"); g.UViz = GlProgram.Loc(g.Prog, "uViz");
        g.UMode = GlProgram.Loc(g.Prog, "uMode"); g.UDim = GlProgram.Loc(g.Prog, "uOitDim");
        g.UNet = GlProgram.Loc(g.Prog, "uNet");
        return true;
    }

    /// <summary>Zero the head table and the count from the GPU, one texel per pixel. Colour writes are masked
    /// by the caller, so this is a buffer write with a rasteriser for an index generator.</summary>
    internal static string OitClearFs => ShaderSource.Get("cx_oitclear_f.glsl");

    /// <summary>Composite. Mode 1: colour = accum.rgb / accum.a (the weighted mean), alpha = revealage, under
    /// ONE_MINUS_SRC_ALPHA / SRC_ALPHA. Mode 2: the pixel's list gathered into a depth-sorted local array and
    /// composited front to back, premultiplied colour plus transmittance under ONE / SRC_ALPHA; a list longer
    /// than the array spills its farthest nodes into an unsorted tail behind the sorted ones.</summary>
    internal static string OitResolveFs => ShaderSource.Get("cx_oitresolve_f.glsl");
}
