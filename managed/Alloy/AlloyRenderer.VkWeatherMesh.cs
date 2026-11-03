using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>
/// Falling rain and snow through task and mesh stages where the device offers them: one draw culls, lights and emits
/// every kept drop, with no record buffer and no indirect command between.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The path the weather pass takes on <paramref name="vk"/> once its programs are built, and the line it says
    /// once about it; <paramref name="again"/> is whether a second ask would say it again.</summary>
    internal static Weather.WxPath VkWxPath(VkBackend vk, ExtenderLog log, out string line, out bool again)
    {
        line = "";
        again = true;
        return Weather.WxPath.Compute;
    }

    /// <summary>A fixed field of <paramref name="kind"/> (1 rain, 2 snow, 3 drips) drawn through <paramref name="path"/>
    /// with every fragment counting one into a small float target: where the drops stand, read back. Null when refused.</summary>
    internal static float[]? VkWxCoverage(VkBackend vk, ExtenderLog log, Weather.WxPath path, int kind) => null;
}
