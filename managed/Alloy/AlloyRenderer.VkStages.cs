using System;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The frame's uniform arena as <see cref="StageResources"/>' Vulkan passes take it: a block staged and
/// recorded at once, for a compute pass outside every render pass.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary><paramref name="bytes"/> into the arena and into <paramref name="cl"/> now: its offset in
    /// <paramref name="arena"/> and its length, or -1 when the arena is full (said by the arena).</summary>
    internal static int VkStageUniforms(CommandList cl, ReadOnlySpan<byte> bytes, out BufferId arena, out int len)
    {
        VkColourState s = _vkc;
        arena = s.Arena;
        len = bytes.Length;
        if (len == 0) return -1;
        int at = VkArena(bytes);
        if (at >= 0) cl.UpdateBuffer(s.Arena, at, s.ArenaBytes.AsSpan(at, (len + 3) & ~3));
        return at;
    }

    /// <summary>This frame's <c>CxEngine</c> block, staged and recorded into <paramref name="cl"/> now when this is its
    /// first reader; -1 when the arena is full.</summary>
    internal static int VkStageEngine(CommandList cl, out BufferId arena, out int len)
    {
        VkColourState s = _vkc;
        arena = s.Arena;
        len = sizeof(EngineBlock);
        bool fresh = s.EngineAt == -1;
        int at = VkEngine();
        if (at >= 0 && fresh) cl.UpdateBuffer(s.Arena, at, s.ArenaBytes.AsSpan(at, (len + 3) & ~3));
        return at;
    }

    /// <summary>The frame uniforms and stage images of program <paramref name="p"/> when it is built from a native
    /// stage: the terms into the block being captured. Hot.</summary>
    private static void VkStageFrame(VkProg p)
    {
        if (p.StageA >= 0 || p.StageB >= 0) StageResources.VkUploadFrame(p.StageFrame, p.StageA, p.StageB, !_capturing);
    }

    /// <summary>Program <paramref name="p"/>'s attachment to native stages <paramref name="a"/> and <paramref name="b"/>:
    /// which of its declarations are their images, and where its frame uniforms are. Cold: once per program.</summary>
    private static void VkStageAttach(VkProg p, int a, int b)
    {
        p.StageA = a;
        p.StageB = b;
        p.StageBinds = StageResources.VkBinds(p.Opaques, a, b, draw: true);
        VkUniformShim.Resolve(p.Names, () => p.StageFrame = FrameLoc.Locate(p.ShimName));
    }
}
