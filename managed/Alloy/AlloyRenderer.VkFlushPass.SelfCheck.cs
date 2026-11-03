using System;
using CX.Framework;
using CX.Rhi;

namespace CX.Alloy;

public sealed partial class AlloyRenderer
{
    /// <summary>Oracles: hand-written stale-or-not verdicts for four passes driven through <see cref="VkFlushPass"/>,
    /// which is where the snapshot's one staling rule lives. The lists only record; no device sees them.</summary>
    internal static void FlushPassSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.FlushPassSelfCheck");
        VkColourState keep = _vkc;
        try
        {
            var scene = new ImageId(11);
            var other = new ImageId(12);
            _vkc = new VkColourState { Depth = scene, DepthRead = new ImageId(13), W = 8, H = 8 };
            var cl = new CommandList("vk flush pass self-check", 4096);
            var side = new CommandList("vk flush pass self-check draws", 1024);
            ReadOnlySpan<ColorAttachment> colors = [new(new ImageId(14), 0, 0, LoadOp.Load, StoreOp.Store, default)];

            check.Expect(Staled(cl, side, new RenderPassDesc(colors, Over(scene)), true),
                         "a pass that attached the scene depth and wrote it left the snapshot speaking for the scene");
            check.Expect(!Staled(cl, side, new RenderPassDesc(colors, Over(scene)), false),
                         "a pass that declared none of its draws write depth staled the snapshot, so the next reader copies for nothing");
            check.Expect(!Staled(cl, side, new RenderPassDesc(colors, Over(other)), true),
                         "a pass that wrote a depth target of its own staled the scene depth's snapshot");
            check.Expect(!Staled(cl, side, new RenderPassDesc(colors), true),
                         "a pass with no depth attachment at all staled the snapshot");
        }
        finally { _vkc = keep; }
        log.Info(check.Ok ? "AlloyRenderer.FlushPassSelfCheck: passed" : "AlloyRenderer.FlushPassSelfCheck: FAILED");

        static DepthAttachment Over(ImageId image) => new(image, 0, 0, LoadOp.Load, StoreOp.Store, WorldDepth.Clear);

        // arena0 is the arena as it stands, so the flush uploads nothing and the pass only records
        static bool Staled(CommandList cl, CommandList side, in RenderPassDesc rp, bool writesDepth)
        {
            VkColourState s = _vkc;
            s.Snapshot.Take(cl, s.Depth, s.DepthRead, s.W, s.H);
            VkFlushPass(cl, side, s.ArenaUsed, in rp, writesDepth);
            return s.Snapshot.Stale;
        }
    }
}
