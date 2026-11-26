using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Every air stage the frame builds compiles on this device, the dust, the motes, the sky-lit motes and the
    /// wisps, as each is built the first frame its layer shows. Oracle: the device's own compiler.</summary>
    internal static void VkAirStagesSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        (string Resref, string? Define, string Label)[] stages =
        [
            (AirScene.ShdCs, null, "golden air dust"),
            (AirScene.MoteCs, null, "golden air motes"),
            (AirScene.MoteCs, VkAirSky, "golden air pollen"),
            (AirScene.WispCs, null, "golden air wisps"),
        ];
        foreach ((string resref, string? define, string label) in stages)
        {
            VkDefStage? st = VkDefBuild(vk, resref, define, label, log, null, out _);
            check.Expect(st != null, $"the {label} stage did not build (logged above)");
            if (st != null) vk.Destroy(st.Pipe);
        }
    }
}
