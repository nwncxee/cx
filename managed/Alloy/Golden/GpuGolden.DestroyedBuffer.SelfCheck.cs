using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// A pass may grow a buffer after an earlier pass recorded a use of the old one in the same list. The list still has to
/// run, or the whole frame is refused and the world stays grey. Oracle: the refusal count before and after the submit.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>Holds that a list naming a buffer destroyed after it was recorded still submits, and that destroying
    /// it twice is harmless; turns red when the id dies before the list has run.</summary>
    private static void DestroyedBufferSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        BufferId b = vk.CreateBuffer(new BufferDesc(64, BufferUse.Storage, MemoryKind.Upload, "golden grown buffer"));
        check.Expect(!b.IsNull, "the buffer was refused (logged above)");
        if (b.IsNull) return;
        int faults = vk.Faults;
        var cl = new CommandList("golden destroyed mid-recording", 1 << 10, log);
        cl.UpdateBuffer(b, 0, new byte[16]);
        vk.Destroy(b);
        vk.Destroy(b);
        check.Expect(vk.Submit(cl), $"a list naming a buffer destroyed after it was recorded was refused: \"{vk.LastFault}\"");
        check.Expect(vk.Faults == faults, $"the submit logged {vk.Faults - faults} refusals, not 0");
    }
}
