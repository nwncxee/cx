using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;
using NuiModelDoll = CX.Services.Nui.NuiModelDoll;

namespace CX.Alloy;

internal static unsafe partial class GuiPassVk
{
    /// <summary>What the recorded run has bound since its render pass began, so a draw re-records only what changed.</summary>
    private struct Bound
    {
        public PipelineId Pipe;
        public BufferId Vtx, Idx;
        public DepthState Depth;
        public RasterState Raster;
        public bool StateSet;
    }

    /// <summary>The render pass being recorded: which image pair it writes and at what size, and whether a draw has
    /// gone into it (a pass with none is dropped rather than submitted empty).</summary>
    private struct Pass
    {
        public bool Open;
        public ImageId Colour, Depth;
        public int W, H;
    }

    /// <summary>The GUI's buffers sized and filled for this flush: the records and bone palettes in one storage
    /// buffer, the stream vertices and indices in their own. False (logged) when the device refused one.</summary>
    private static bool Stage(VkBackend vk, ExtenderLog log)
    {
        GuiVkState s = _s;
        long rec = (long)GuiCapture.DrawN * GuiCapture.RecFloats * sizeof(float);
        long bones = (long)GuiCapture.SkinN * GuiCapture.BoneFloats * sizeof(float);
        // the whole-word lengths the uploads below write, not the staged counts: a recorded update is words
        return Grow(vk, ref s.Recs, ref s.RecCap, Math.Max(rec + bones, 64), BufferUse.Storage, "vk gui records", log)
            && Grow(vk, ref s.Vtx, ref s.VtxCap, Math.Max(VtxBytes, 64), BufferUse.Vertex, "vk gui stream vertices", log)
            && Grow(vk, ref s.Idx, ref s.IdxCap, Math.Max(IdxBytes, 64), BufferUse.Index, "vk gui stream indices", log);
    }

    /// <summary>The stream vertex and index bytes this flush uploads, each rounded up to a whole word.</summary>
    private static int VtxBytes => (GuiCapture.VtxN + 3) & ~3;
    private static int IdxBytes => ((GuiCapture.IdxN + 1) & ~1) * sizeof(ushort);

    /// <summary>This flush's staged bytes into the buffers, recorded ahead of every pass that reads them.</summary>
    private static void Uploads(CommandList cl)
    {
        GuiVkState s = _s;
        int rec = GuiCapture.DrawN * GuiCapture.RecFloats * sizeof(float);
        int bones = GuiCapture.SkinN * GuiCapture.BoneFloats * sizeof(float);
        if (rec > 0) cl.UpdateBuffer(s.Recs, 0, MemoryMarshal.AsBytes(GuiCapture.Records.AsSpan(0, GuiCapture.DrawN)));
        if (bones > 0) cl.UpdateBuffer(s.Recs, rec, MemoryMarshal.AsBytes(GuiCapture.Bones.AsSpan(0, GuiCapture.SkinN)));
        if (GuiCapture.VtxN > 0) cl.UpdateBuffer(s.Vtx, 0, GuiCapture.Vtx.AsSpan(0, VtxBytes));
        if (GuiCapture.IdxN > 0) cl.UpdateBuffer(s.Idx, 0, MemoryMarshal.AsBytes(GuiCapture.Idx.AsSpan(0, IdxBytes / sizeof(ushort))));
    }

    private static bool Grow(VkBackend vk, ref BufferId id, ref long cap, long bytes, BufferUse use, string label, ExtenderLog log)
    {
        if (!id.IsNull && bytes <= cap) return true;
        vk.Destroy(id);
        cap = Math.Max(bytes, cap * 2);
        id = vk.CreateBuffer(new BufferDesc(cap, use, MemoryKind.Device, label));
        if (!id.IsNull) return true;
        cap = 0;
        log.Always($"GuiPassVk: {label} at {bytes} bytes was REFUSED (logged above) -- this frame's GUI is NOT DRAWN");
        return false;
    }

    /// <summary>Layers <paramref name="from"/>..<paramref name="to"/> recorded into <paramref name="cl"/>, in capture
    /// order: one render pass per run of layers sharing a target, split again where a layer clears the depth. The
    /// model-view solve draws its run twice into scratch images and resolves. False = the flush refuses.</summary>
    private static bool Record(VkBackend vk, CommandList cl, int from, int to, ExtenderLog log)
    {
        GuiVkState s = _s;
        ImageId screen = s.Into.IsNull ? vk.CompositeFront : s.Into;
        if (screen.IsNull || s.Depth.IsNull) return false;
        var pass = new Pass();
        var b = new Bound();
        bool gamma = !PostChain.GammaOff();
        if (s.ClearOwed)
        {
            // the engine's swallowed frame clear, in its own clear colour and unscissored, as GL's FrameClear does it
            s.ClearOwed = false;
            float[] c = ScreenOwner.ClearRgba;
            Vector4 clear = s.Black ? Zero : new Vector4(c[0], c[1], c[2], c[3]);
            Open(cl, ref pass, ref b, screen, s.Depth, PresentTarget.Width, PresentTarget.Height, true, true, clear);
            Close(cl, ref pass);
        }
        // No target yet, which 0 does NOT mean here: 0 is the screen, so the first layer must open its pass. GL
        // binds the base framebuffer before the loop instead, which is why its own latch starts at 0.
        uint curTarget = uint.MaxValue;
        bool released = false;
        int li = from, solve = 0, runStart = 0, runEnd = 0;
        while (li < to || solve != 0)
        {
            if (solve != 0 && li == runEnd)
            {
                ref GuiCapture.Layer last = ref GuiCapture.Layers[runStart];
                View v = ViewOf(vk, curTarget, last.W, last.H, log);
                if (solve == 1)
                {
                    solve = 2; li = runStart;
                    Open(cl, ref pass, ref b, v.White, v.Depth, last.W, last.H, true, true, One);
                    v.Solved = true;
                }
                else
                {
                    solve = 0;
                    Open(cl, ref pass, ref b, v.Colour, v.Depth, last.W, last.H, true, true, Background(curTarget));
                    v.Drawn = true;
                    Resolve(cl, ref pass, ref b, v, last.W, last.H, curTarget, log);
                    curTarget = uint.MaxValue;
                    if (li >= to) break;
                }
                continue;
            }
            ref GuiCapture.Layer l = ref GuiCapture.Layers[li++];
            bool reopen = l.Clear;
            if (l.Target != curTarget)
            {
                curTarget = l.Target;
                NuiModelDoll.ModelSlot? owner = NuiModelDoll.OfTexture(curTarget);
                // A released view's texture may be a reused GL name: draw nothing into it.
                released = curTarget != 0 && owner == null;
                if (released) continue;
                if (curTarget != 0)
                {
                    View v = ViewOf(vk, curTarget, l.W, l.H, log);
                    if (v.Colour.IsNull) { released = true; continue; }
                    bool wanted = s.Solve != null && GuiModelSolve.Wanted(li - 1, to, owner, false, out runEnd);
                    if (wanted)
                    {
                        solve = 1; runStart = li - 1;
                        Open(cl, ref pass, ref b, v.Black, v.Depth, l.W, l.H, true, true, Zero);
                    }
                    else
                    {
                        Open(cl, ref pass, ref b, v.Colour, v.Depth, l.W, l.H, true, true, Background(curTarget));
                        v.Drawn = true;
                    }
                }
                // A screen pass clears the depth only where the layer asks, as GL's loop does; a model view's own
                // depth is cleared with its colour above.
                else Open(cl, ref pass, ref b, screen, s.Depth, PresentTarget.Width, PresentTarget.Height, l.Clear, false);
                reopen = false;
            }
            else if (released) continue;
            // The run's depth clear rides on its FIRST view, drawn or not: skipped with an empty layer, it left last
            // flush's text quads (0.001 nearer) in the buffer, and everything behind them failed there.
            if (reopen) Open(cl, ref pass, ref b, pass.Colour, pass.Depth, pass.W, pass.H, true, false);
            if (l.Count == 0 || l.W <= 0 || l.H <= 0) continue;
            CommandList side = s.Side!;
            side.SetViewport(l.X, l.Y, l.W, l.H);
            side.SetScissor(l.SX, l.SY, l.SW, l.SH);
            side.SetDepthRange(l.Near, l.Far);
            bool cw = curTarget != 0;
            if (!Draws(vk, side, ref b, in l, cw, gamma, log)) return false;
        }
        Close(cl, ref pass);
        return true;
    }

    /// <summary>The clear colours a model-view pass takes: the solve's black and white passes, and the server's
    /// background for a single-pass view.</summary>
    private static readonly Vector4 Zero = new(0f, 0f, 0f, 1f), One = new(1f, 1f, 1f, 1f);

    /// <summary>The background colour the slot drawing into <paramref name="tex"/> names, opaque black for none.</summary>
    private static Vector4 Background(uint tex)
    {
        NuiModelDoll.ModelSlot? slot = NuiModelDoll.OfTexture(tex);
        if (slot == null) return Zero;
        float* c = stackalloc float[4];
        NuiModelDoll.Background(slot, c);
        return new Vector4(c[0], c[1], c[2], c[3]);
    }

    /// <summary>Close whatever pass is open and begin one over <paramref name="colour"/> and
    /// <paramref name="depth"/>. The draws recorded aside go in when it closes, so the loose arena they read is
    /// uploaded ahead of them.</summary>
    private static void Open(CommandList cl, ref Pass pass, ref Bound b, ImageId colour, ImageId depth, int w, int h,
                             bool clearDepth, bool clearColour, Vector4 clear = default)
    {
        Close(cl, ref pass);
        GuiVkState s = _s;
        pass = new Pass { Open = true, Colour = colour, Depth = depth, W = w, H = h };
        s.Side!.Reset();
        s.Arena0 = s.ArenaUsed;
        b = new Bound();
        s.ClearDepth = clearDepth;
        s.ClearColour = clearColour;
        s.Clear = clear;
    }

    /// <summary>Every image this flush has drawn into that the pass about to begin may sample -- a model view's own
    /// texture, and the solve's two scratches -- handed over before it, where the backend can place the barrier. The
    /// panel that shows a doll need not be the pass's first draw.</summary>
    private static void Readable(CommandList cl, GuiVkState s, ImageId colour)
    {
        Span<ImageId> read = stackalloc ImageId[NuiModelDoll.MaxViews * 3];
        int n = 0;
        foreach (View v in s.Views)
        {
            if (v.Drawn && v.Colour != colour && !v.Colour.IsNull) read[n++] = v.Colour;
            if (!v.Solved) continue;
            if (v.Black != colour && !v.Black.IsNull) read[n++] = v.Black;
            if (v.White != colour && !v.White.IsNull) read[n++] = v.White;
        }
        if (n > 0) cl.ReadImages(read[..n]);
    }

    /// <summary>The open pass: the arena bytes its binds gathered, uploaded ahead of it, then the pass and its draws.
    /// A pass whose attachments only wanted clearing still begins, so the clears happen.</summary>
    private static void Close(CommandList cl, ref Pass pass)
    {
        if (!pass.Open) return;
        GuiVkState s = _s;
        int len = (s.ArenaUsed - s.Arena0 + 3) & ~3;
        if (len > 0) cl.UpdateBuffer(s.Loose, s.Arena0, s.Arena.AsSpan(s.Arena0, Math.Min(len, LooseCap - s.Arena0)));
        Span<ColorAttachment> colors = stackalloc ColorAttachment[1];
        Vector4 clear = s.ClearColour ? s.Clear : default;
        colors[0] = new ColorAttachment(pass.Colour, 0, 0, s.ClearColour ? LoadOp.Clear : LoadOp.Load, StoreOp.Store, clear);
        var d = new DepthAttachment(pass.Depth, 0, 0, s.ClearDepth ? LoadOp.Clear : LoadOp.Load, StoreOp.Store, 1f);
        Readable(cl, s, pass.Colour);
        cl.BeginRenderPass(new RenderPassDesc(colors, d));
        cl.Append(s.Side!);
        cl.EndRenderPass();
        pass.Open = false;
    }

    /// <summary>One layer's draws, in capture order: a stream draw out of the GUI's own buffers and a database part
    /// out of the mesh arena's, each through Alloy's GUI program or the content program its material names.</summary>
    private static bool Draws(VkBackend vk, CommandList side, ref Bound b, in GuiCapture.Layer l, bool cw, bool gamma, ExtenderLog log)
    {
        GuiVkState s = _s;
        for (int di = l.First; di < l.First + l.Count; di++)
        {
            ref GuiCapture.Draw d = ref GuiCapture.Draws[di];
            VertexLayoutId layout;
            BufferId vtx, idx;
            if (d.RawVao != 0)
            {
                // CX-120: a DB draw's geometry is the mesh arena's, so Vulkan binds that layout's buffers where GL
                // binds its vertex array. Nothing else changes: the record, the storage buffer and the per-rect
                // viewport are the captured path's.
                int fmt = d.ArenaFmt;
                if ((uint)fmt >= MeshArena.Formats) { s.NoLayout++; continue; }
                layout = s.ArenaLayout[fmt];
                vtx = MeshArena.VkVbo((MeshArena.Fmt)fmt);
                idx = MeshArena.VkEbo;
            }
            else if (d.Stream) { layout = StreamLayout(vk, d.Format, d.Stride, log); vtx = s.Vtx; idx = s.Idx; }
            else { s.NoLayout++; continue; }
            if (layout.IsNull || vtx.IsNull || (d.Indexed && idx.IsNull)) { s.NoLayout++; continue; }
            if (Topo(d.Prim, log) is not { } topo) { s.Skipped++; continue; }
            if (Blend(in d, log) is not { } blend) { s.Skipped++; continue; }
            GuiContent.VkPair? content = d.Content == 0 ? null : ContentProgram(vk, d.Content, log);
            GuiVkProg prog = content?.Program ?? s.Prog!;
            PipelineId pipe = Pipeline(prog, layout, blend, topo, log);
            if (pipe.IsNull) { s.Skipped++; continue; }
            int loose = content == null ? GuiBlock(prog, gamma, log) : ContentBlock(content, di, l.W, l.H, gamma, log);
            if (loose < 0) return false;
            if (pipe != b.Pipe) { side.SetPipeline(pipe); b.Pipe = pipe; b.StateSet = false; }
            var depth = new DepthState(d.DepthTest, d.DepthWrite, CompareOp.LessEqual);
            var raster = new RasterState(d.Cull ? CullMode.Back : CullMode.None, cw);
            if (!b.StateSet || depth != b.Depth) { side.SetDepthState(depth); b.Depth = depth; }
            if (!b.StateSet || raster != b.Raster) { side.SetRasterState(raster); b.Raster = raster; }
            b.StateSet = true;
            if (vtx != b.Vtx) { side.BindVertexBuffer(vtx); b.Vtx = vtx; }
            if (d.Indexed && idx != b.Idx) { side.BindIndexBuffer(idx, IndexType.U16, 0); b.Idx = idx; }
            BindSet(side, prog, loose, d.Tex, d.EnvTex);
            if (d.Indexed) side.DrawIndexed(d.Count, 1, (int)(d.Offset / sizeof(ushort)), d.BaseVertex, di);
            else side.Draw(d.Count, 1, (int)d.Offset, di);
            s.Drawn++;
        }
        return true;
    }

    /// <summary>The solved model view into the pass open over the view's own texture: the same full-screen draw GL
    /// makes, reading the two scratch images.</summary>
    private static void Resolve(CommandList cl, ref Pass pass, ref Bound b, View v, int w, int h, uint tex, ExtenderLog log)
    {
        GuiVkState s = _s;
        if (s.Solve == null) return;
        if (!s.SolveSaid)
        {
            s.SolveSaid = true;
            log.Always($"GuiPassVk: a model view with additive draws is solved over black and white ({w}x{h})");
        }
        Vector4 bg = Background(tex);
        int loose = Block(s.Solve, () => VkUniformShim.Set(s.SolveBg, [bg.X, bg.Y, bg.Z, bg.W]), log);
        if (loose < 0) return;
        PipelineId pipe = Pipeline(s.Solve, default, 0, Topology.Triangles, log);
        if (pipe.IsNull) return;
        CommandList side = s.Side!;
        side.SetPipeline(pipe);
        side.SetViewport(0, 0, w, h);
        side.SetScissor(0, 0, w, h);
        side.SetDepthRange(0f, 1f);
        side.SetDepthState(default);
        side.SetRasterState(default);
        ReadOnlySpan<BufferBinding> ubo = [new(UboSlots.Loose, s.Loose, loose, s.Solve.Block.Length)];
        ReadOnlySpan<ImageBinding> tex2 = [new(0, v.Black, s.Linear), new(1, v.White, s.Linear)];
        side.BindSet(0, new BindSet(ubo, default, tex2, default));
        side.Draw(3);
        b = new Bound();
    }

    /// <summary>Model view slot <paramref name="tex"/>'s Vulkan targets at <paramref name="w"/> x
    /// <paramref name="h"/>, remade when the view or its size changed. The colour image stands in for the view's GL
    /// texture on both sides: Vulkan draws into it and a later panel samples it, and GL never reads it under this
    /// backend.</summary>
    private static View ViewOf(VkBackend vk, uint tex, int w, int h, ExtenderLog log)
    {
        GuiVkState s = _s;
        int k = NuiModelDoll.OfTexture(tex)?.Index ?? 0;
        View v = s.Views[(uint)k < (uint)s.Views.Length ? k : 0];
        if (v.Tex == tex && v.W == w && v.H == h && !v.Colour.IsNull) return v;
        vk.Destroy(v.Colour); vk.Destroy(v.Depth); vk.Destroy(v.Black); vk.Destroy(v.White);
        v.Tex = tex; v.W = w; v.H = h;
        v.Colour = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, ViewFormat, w, h, 1, 1, 1, 1,
                                                ImageUse.ColorTarget | ImageUse.Sampled, $"vk gui model view {k}"));
        v.Depth = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, DepthFormat, w, h, 1, 1, 1, 1, ImageUse.DepthTarget, $"vk gui model view {k} depth"));
        v.Black = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, ViewFormat, w, h, 1, 1, 1, 1,
                                               ImageUse.ColorTarget | ImageUse.Sampled, $"vk gui model view {k} over black"));
        v.White = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, ViewFormat, w, h, 1, 1, 1, 1,
                                               ImageUse.ColorTarget | ImageUse.Sampled, $"vk gui model view {k} over white"));
        if (v.Colour.IsNull || v.Depth.IsNull)
            log.Always($"GuiPassVk: the Vulkan model view target {w}x{h} for GL texture {tex} was REFUSED (logged above) -- that view is NOT DRAWN");
        else log.Info($"GuiPassVk: model view target {w}x{h} on texture {tex}: a Vulkan image of its own");
        return v;
    }

    /// <summary>The Vulkan image standing in for model view texture <paramref name="tex"/>, null id for any other
    /// name.</summary>
    private static ImageId ViewImage(int tex)
    {
        foreach (View v in _s.Views) if (v.Tex == (uint)tex && v.Tex != 0) return v.Colour;
        return default;
    }
}
