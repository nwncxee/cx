using System;
using CX.Framework;

namespace CX.Graphics;

internal static partial class ShaderCache
{
    /// <summary>Off-device: which files the folder's sweep drops, by name and by age. Oracle: a hand-written
    /// table of leaf names, ages and the verdict each one is owed -- a module kept for a month, a pipeline cache
    /// blob kept for a week, and the window that tells them apart.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "ShaderCache.SelfCheck");
        (string Leaf, int Days, bool Swept)[] table =
        [
            ("vk-1a2b3c4d5e6f7a8b.spv", 1, false),
            ("vk-1a2b3c4d5e6f7a8b.spv", 20, false),
            ("vk-1a2b3c4d5e6f7a8b.spv", 29, false),
            ("vk-1a2b3c4d5e6f7a8b.spv", 31, true),
            ("vk-pipelines-10de-2488-0F671E8BAC2E21CA6BC2FAE2BE177ECE.bin", 1, false),
            ("vk-pipelines-10de-2488-0F671E8BAC2E21CA6BC2FAE2BE177ECE.bin", 6, false),
            ("vk-pipelines-10de-2488-0F671E8BAC2E21CA6BC2FAE2BE177ECE.bin", 20, true),
            ("vk-pipelines-10de-2488-0F671E8BAC2E21CA6BC2FAE2BE177ECE.bin", 400, true),
        ];
        foreach ((string leaf, int days, bool swept) in table)
            check.Expect(Sweeps(leaf, TimeSpan.FromDays(days)) == swept,
                $"{leaf} at {days} days should be {(swept ? "swept" : "kept")}");
        log.Info(check.Ok ? $"ShaderCache.SelfCheck: passed ({table.Length} sweep verdicts)"
                          : "ShaderCache.SelfCheck: FAILED");
    }
}
