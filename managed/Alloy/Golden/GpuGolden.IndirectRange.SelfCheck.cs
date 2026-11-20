using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// An indirect draw whose commands or count lie past the end of their buffer makes the GPU read memory nothing owns,
/// which hangs or resets the device. The backend refuses it at recording instead. Oracle: hand-counted command sizes
/// (five words indexed, four plain) against a 100-byte command buffer and an 8-byte count buffer.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The command buffer's bytes: five indexed commands, or six plain ones with 4 bytes over.</summary>
    private const int IndirectFixtureBytes = 100;
    private const string IndirectCmdLabel = "golden indirect commands", IndirectCntLabel = "golden indirect counts";
    /// <summary>The refusal an in-range draw recorded outside a pass meets: the range test let it through.</summary>
    private const string OutsidePass = "a draw outside a render pass";

    /// <summary>Holds that each indirect draw reaching past its buffer is refused naming that buffer, and that one
    /// ending exactly at the end gets past the range test.</summary>
    private static void IndirectRangeSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        const BufferUse Use = BufferUse.Storage | BufferUse.Indirect;
        BufferId cmds = vk.CreateBuffer(new BufferDesc(IndirectFixtureBytes, Use, MemoryKind.Device, IndirectCmdLabel));
        BufferId cnt = vk.CreateBuffer(new BufferDesc(8, Use, MemoryKind.Device, IndirectCntLabel));
        check.Expect(!cmds.IsNull && !cnt.IsNull, "the indirect fixture's buffers were refused (logged above)");
        if (cmds.IsNull || cnt.IsNull) { vk.Destroy(cmds); vk.Destroy(cnt); return; }
        var cl = new CommandList("golden indirect range", 1 << 10, log);
        string Refusal(Action<CommandList> record)
        {
            cl.Reset();
            record(cl);
            return vk.Submit(cl) ? "(submitted)" : vk.LastFault;
        }
        string r = Refusal(c => c.DrawIndexedIndirect(cmds, 0, 5));
        check.Expect(r.Contains(OutsidePass, StringComparison.Ordinal), $"five indexed commands fill 100 bytes exactly: \"{r}\"");
        r = Refusal(c => c.DrawIndexedIndirect(cmds, 20, 5));
        check.Expect(r.Contains(IndirectCmdLabel, StringComparison.Ordinal),
            $"five indexed commands from byte 20 end at 120, past 100, and the refusal names the buffer: \"{r}\"");
        r = Refusal(c => c.DrawIndexedIndirect(cmds, -20, 1));
        check.Expect(r.Contains(IndirectCmdLabel, StringComparison.Ordinal),
            $"a command starting 20 bytes before the buffer is refused, and the refusal names it: \"{r}\"");
        r = Refusal(c => c.DrawIndexedIndirect(cmds, 0, 5, cnt, 4));
        check.Expect(r.Contains(OutsidePass, StringComparison.Ordinal), $"a count at byte 4 of 8 is inside: \"{r}\"");
        r = Refusal(c => c.DrawIndexedIndirect(cmds, 0, 5, cnt, 8));
        check.Expect(r.Contains(IndirectCntLabel, StringComparison.Ordinal),
            $"a count at byte 8 of 8 is past the end, and the refusal names the count buffer: \"{r}\"");
        r = Refusal(c => c.DrawIndirect(cmds, 0, 6));
        check.Expect(r.Contains(OutsidePass, StringComparison.Ordinal), $"six plain commands take 96 of 100 bytes: \"{r}\"");
        r = Refusal(c => c.DrawIndirect(cmds, 0, 7));
        check.Expect(r.Contains(IndirectCmdLabel, StringComparison.Ordinal),
            $"seven plain commands take 112 of 100 bytes, and the refusal names the buffer: \"{r}\"");
        vk.Destroy(cmds);
        vk.Destroy(cnt);
    }
}
