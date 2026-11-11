using System;
using CX.Framework;

namespace CX.Graphics;

internal static partial class ShaderCache
{
    /// <summary>Off-device: which pipeline cache blobs the folder retires beside the one the driver running now
    /// reads. Oracle: a hand-written table of leaf names, ages and the verdict each one is owed -- this driver's
    /// own blob kept however old it is, the same GPU under a retired driver identity dropped, another GPU's and a
    /// blob younger than the window left alone.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "ShaderCache.SelfCheck");
        const string mine = "vk-pipelines-10de-2488-0F671E8BAC2E21CA6BC2FAE2BE177ECE.bin";
        (string Leaf, int Days, bool Gone)[] table =
        [
            (mine, 1, false),
            (mine, 20, false),
            (mine, 400, false),                                                       // a holiday is not a reason to lose it
            ("vk-pipelines-10de-2488-0F671E8BAC2E21CA6F9C57EEAF41849B.bin", 20, true),   // this GPU, the driver before
            ("vk-pipelines-10de-2488-0F671E8BAC2E21CA6F9C57EEAF41849B.bin", 400, true),
            ("vk-pipelines-10de-2488-0F671E8BAC2E21CA6F9C57EEAF41849B.bin", 1, false),   // ...written today: two live ICDs
            ("vk-pipelines-10de-2488-0F671E8BAC2E21CA6F9C57EEAF41849B.bin", 6, false),
            ("vk-pipelines-1002-73df-9A7E11C4D2B6F0381C5D4EE7A2904B6F.bin", 400, false), // another GPU: the age sweep's
            ("vk-pipelines-10de-1f82-0F671E8BAC2E21CA6BC2FAE2BE177ECE.bin", 400, false), // same vendor, another device
            ("vk-1a2b3c4d5e6f7a8b.spv", 400, false),                                     // a module is not a blob
        ];
        foreach ((string leaf, int days, bool gone) in table)
            check.Expect(Superseded(leaf, mine, TimeSpan.FromDays(days)) == gone,
                $"{leaf} at {days} days beside {mine} should be {(gone ? "dropped" : "kept")}");
        log.Info(check.Ok ? $"ShaderCache.SelfCheck: passed ({table.Length} retirement verdicts)"
                          : "ShaderCache.SelfCheck: FAILED");
    }
}
