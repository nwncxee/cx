using System;
using System.Numerics;
using CX.Framework;

namespace CX.Rhi;

/// <summary>The seam's off-device proofs. Oracle: hand-written literals -- every value recorded is a literal
/// the check reads back and compares against the same literal, never against the encoder's own output.</summary>
public static class RhiSelfCheck
{
    public static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "Rhi.SelfCheck");
        Handles(check);
        RoundTrip(check);
        DrawRoundTrip(check);
        Growth(check);
        FormatTraits(check);
        Blends(check);
        log.Info(check.Ok ? "Rhi.SelfCheck: passed" : "Rhi.SelfCheck: FAILED");
    }

    /// <summary>A freed handle goes stale, its slot comes back under a new generation, and null never resolves.</summary>
    private static void Handles(Checker check)
    {
        var t = new HandleTable<string>();
        uint a = t.Add("a"), b = t.Add("b");
        check.Expect(a != 0 && b != 0 && a != b, $"two live handles are distinct and non-null ({a}, {b})");
        check.Expect(t.TryGet(b, out string? got) && got == "b", "a live handle resolves to its entry");
        check.Expect(t.Remove(a, out _) && !t.TryGet(a, out _), "a removed handle no longer resolves");
        uint c = t.Add("c");
        check.Expect((c & 0xFFFFF) == (a & 0xFFFFF) && c != a, "the freed slot is reused under a new generation");
        check.Expect(!t.TryGet(a, out _) && t.TryGet(c, out got) && got == "c", "the stale copy stays refused after reuse");
        check.Expect(!t.TryGet(0, out _) && !t.Remove(a, out _), "null and stale handles are refused by Remove too");
        check.Expect(t.Count == 2, $"two entries live, not {t.Count}");
    }

    /// <summary>Every command reads back in record order with the literals it was recorded with.</summary>
    private static void RoundTrip(Checker check)
    {
        var list = new CommandList("selfcheck");
        Span<ColorAttachment> colors =
        [
            new(new ImageId(7), 1, 2, LoadOp.Clear, StoreOp.Store, new Vector4(0.25f, 0.5f, 0.75f, 1f)),
            new(new ImageId(8), 0, -1, LoadOp.Load, StoreOp.DontCare, default),
        ];
        list.BeginRenderPass(new RenderPassDesc(colors, new DepthAttachment(new ImageId(9), 0, 0, LoadOp.Clear, StoreOp.Store, 1f)));
        list.SetPipeline(new PipelineId(42));
        list.SetViewport(1, 2, 640, 360);
        Span<BufferBinding> ubo = [new(2, new BufferId(5), 256, 64)];
        Span<ImageBinding> tex = [new(3, new ImageId(7), new SamplerId(11)), new(4, new ImageId(8))];
        list.BindSet(1, new BindSet(ubo, default, tex, default));
        list.Draw(3);
        list.UpdateBuffer(new BufferId(5), 16, [1, 2, 3]);
        list.Dispatch(8, 4, 2);
        list.EndRenderPass();

        CommandReader r = list.TakePending();
        check.Expect(r.Next(out Op op) && op == Op.BeginRenderPass, $"first command is BeginRenderPass, not {op}");
        RenderPassCmd rp = r.Read<RenderPassCmd>();
        ReadOnlySpan<ColorAttachment> rc = r.Span<ColorAttachment>(rp.Colors);
        check.Expect(rp.Colors == 2 && rp.HasDepth && rp.Depth.Image.Value == 9 && rp.Depth.Depth == 1f, $"render pass header {rp}");
        check.Expect(rc.Length == 2 && rc[0].Image.Value == 7 && rc[0].Mip == 1 && rc[0].Layer == 2
                     && rc[0].Clear == new Vector4(0.25f, 0.5f, 0.75f, 1f) && rc[1].Layer == -1 && rc[1].Store == StoreOp.DontCare,
                     "the colour attachments read back field for field");
        check.Expect(r.Next(out op) && op == Op.SetPipeline && r.Read<PipelineId>().Value == 42, "SetPipeline 42");
        check.Expect(r.Next(out op) && op == Op.SetViewport && r.Read<Rect>() == new Rect(1, 2, 640, 360), "SetViewport 1,2 640x360");
        check.Expect(r.Next(out op) && op == Op.BindSet, $"BindSet, not {op}");
        BindSetCmd bs = r.Read<BindSetCmd>();
        ReadOnlySpan<BufferBinding> ru = r.Span<BufferBinding>(bs.Uniforms);
        r.Span<BufferBinding>(bs.Storage);
        ReadOnlySpan<ImageBinding> rt = r.Span<ImageBinding>(bs.Sampled);
        r.Span<ImageBinding>(bs.StorageImages);
        check.Expect(bs == new BindSetCmd(1, 1, 0, 2, 0), $"BindSet header {bs}");
        check.Expect(ru.Length == 1 && ru[0] == new BufferBinding(2, new BufferId(5), 256, 64), "the uniform binding reads back");
        check.Expect(rt.Length == 2 && rt[0].Sampler.Value == 11 && rt[1].Slot == 4 && rt[1].Sampler.IsNull, "the sampled bindings read back");
        check.Expect(r.Next(out op) && op == Op.Draw && r.Read<DrawCmd>() == new DrawCmd(3, 1, 0, 0), "Draw(3) with its defaults");
        check.Expect(r.Next(out op) && op == Op.UpdateBuffer, $"UpdateBuffer, not {op}");
        UpdateBufferCmd ub = r.Read<UpdateBufferCmd>();
        ReadOnlySpan<byte> bytes = r.Span<byte>(ub.Bytes);
        check.Expect(ub.Offset == 16 && bytes.Length == 3 && bytes[0] == 1 && bytes[2] == 3, "the update's bytes are copied in");
        check.Expect(r.Next(out op) && op == Op.Dispatch && r.Read<DispatchCmd>() == new DispatchCmd(8, 4, 2), "Dispatch 8x4x2");
        check.Expect(r.Next(out op) && op == Op.EndRenderPass, $"EndRenderPass, not {op}");
        check.Expect(!r.Next(out _), "nothing past the last command");
        check.Expect(!list.TakePending().Next(out _), "a command is handed out once");
    }

    /// <summary>The batch pass's commands read back with their literals; a push past the block and a ragged multi-draw
    /// are refused and fault the list, recording nothing.</summary>
    private static void DrawRoundTrip(Checker check)
    {
        var list = new CommandList("selfcheck-draw");
        list.PushConstants(4, [1, 0, 0, 0, 2, 0, 0, 0]);
        list.BindVertexBuffer(new BufferId(3), 96);
        list.SetDepthState(new DepthState(true, false, CompareOp.Equal));
        list.SetRasterState(new RasterState(CullMode.Back, FrontCw: true, DepthBias: true));
        list.DrawIndexedMulti([30, 6], [0, 30], [0, 12]);
        list.DrawIndexedIndirect(new BufferId(4), 40, 7, new BufferId(5), 8);
        CommandReader r = list.TakePending();
        check.Expect(r.Next(out Op op) && op == Op.PushConstants, $"PushConstants, not {op}");
        PushConstantsCmd pc = r.Read<PushConstantsCmd>();
        ReadOnlySpan<byte> pb = r.Span<byte>(pc.Bytes);
        check.Expect(pc == new PushConstantsCmd(4, 8) && pb[0] == 1 && pb[4] == 2, $"the push reads back at offset 4, 8 bytes ({pc})");
        check.Expect(r.Next(out op) && op == Op.BindVertexBuffer && r.Read<VertexBufferCmd>() == new VertexBufferCmd(new BufferId(3), 96), "vertex buffer 3 at 96");
        check.Expect(r.Next(out op) && op == Op.SetDepthState && r.Read<DepthState>() == new DepthState(true, false, CompareOp.Equal), "depth: test, no write, EQUAL");
        check.Expect(r.Next(out op) && op == Op.SetRasterState && r.Read<RasterState>() == new RasterState(CullMode.Back, true, true), "raster: back, clockwise, biased");
        check.Expect(r.Next(out op) && op == Op.DrawIndexedMulti, $"DrawIndexedMulti, not {op}");
        int n = r.Read<MultiDrawCmd>().Draws;
        ReadOnlySpan<int> cnt = r.Span<int>(n), first = r.Span<int>(n), bas = r.Span<int>(n);
        check.Expect(n == 2 && cnt[0] == 30 && cnt[1] == 6 && first[1] == 30 && bas[1] == 12, "the multi-draw's three spans read back in order");
        check.Expect(r.Next(out op) && op == Op.DrawIndexedIndirect
                     && r.Read<IndirectCmd>() == new IndirectCmd(new BufferId(4), 40, 7, new BufferId(5), 8), "indirect: commands 4 at 40, 7 at most, count 5 at 8");
        check.Expect(!r.Next(out _), "nothing past the indirect draw");

        var bad = new CommandList("selfcheck-refusals");
        bad.PushConstants(124, [1, 2, 3, 4, 5, 6, 7, 8]);
        check.Expect(bad.Faulted && bad.Length == 0, "a push past the 128-byte block faults and records nothing");
        bad.Reset();
        bad.DrawIndexedMulti([1, 2], [0], [0, 0]);
        check.Expect(bad.Faulted && bad.Length == 0, "a multi-draw with ragged spans faults and records nothing");
    }

    /// <summary>An arena too small grows once and keeps what it held; a warm arena records with no allocation.</summary>
    private static void Growth(Checker check)
    {
        var list = new CommandList("selfcheck-growth", 64);
        for (int i = 0; i < 8; i++) list.Draw(i + 1);
        check.Expect(list.Growths >= 1, "recording past the arena grows it");
        CommandReader r = list.TakePending();
        int n = 0;
        bool inOrder = true;
        while (r.Next(out Op op)) inOrder &= op == Op.Draw && r.Read<DrawCmd>().Verts == ++n;
        check.Expect(inOrder && n == 8, $"the eight draws survive the growth in order ({n} read)");

        int grew = list.Growths;
        list.Reset();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 8; i++) list.Draw(i + 1);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        check.Expect(allocated == 0 && list.Growths == grew, $"a warm arena records with no allocation ({allocated} bytes)");
    }

    /// <summary>A blend nobody constructed -- a fresh <see cref="BlendSet"/>'s element -- writes every channel, and a
    /// masked channel is the only one held. Oracle: the channel literals named by hand.</summary>
    private static void Blends(Checker check)
    {
        var set = new BlendSet();
        check.Expect(set[0].Writes(ColorMask.R) && set[0].Writes(ColorMask.All) && !set[0].Enable,
                     "an unset attachment blend writes every channel, unblended");
        var a = new AttachmentBlend(Masked: ColorMask.A);
        check.Expect(a.Writes(ColorMask.R | ColorMask.G | ColorMask.B) && !a.Writes(ColorMask.A) && !a.Writes(ColorMask.All),
                     "masking alpha holds alpha alone");
    }

    /// <summary>Oracle: the formats' definitions written out by hand -- which carry depth, stencil, sRGB, integers.</summary>
    private static void FormatTraits(Checker check)
    {
        check.Expect(Format.D24S8.IsDepth() && Format.D24S8.HasStencil() && !Format.D32F.HasStencil(), "depth and stencil traits");
        check.Expect(!Format.Rgba16F.IsDepth() && !Format.R32Ui.IsDepth(), "colour formats are not depth");
        check.Expect(Format.Rgba8Srgb.IsSrgb() && !Format.Rgba8.IsSrgb(), "only the sRGB format encodes");
        check.Expect(Format.Rgba32Ui.IsInteger() && Format.R32Ui.IsInteger() && !Format.R32F.IsInteger(), "integer formats");
        check.Expect(!Format.R11G11B10F.IsDepth() && Format.D16.IsDepth(), "R11G11B10F is colour, D16 depth");
    }
}
