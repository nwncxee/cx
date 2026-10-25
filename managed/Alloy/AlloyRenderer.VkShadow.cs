using CX.Engine;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The per-draw records the caster bake's vertex stages read through <c>uDraw</c>, this frame's as the
    /// colour bracket's uploads left them.</summary>
    internal static BufferId VkDrawRecords => _vkc.Records;

    /// <summary>This frame's bone block, which the bake skins with.</summary>
    internal static BufferId VkBones => _vkc.Bones;

    /// <summary>The handle table a masked caster samples its own diffuse through.</summary>
    internal static BufferId VkTexH => _vkc.TexH;

    /// <summary>The stand-in blocks a declaration with nothing behind it yet reads as zeros.</summary>
    internal static BufferId VkZeroUbo => _vkc.ZeroUbo;
    internal static BufferId VkZeroSsbo => _vkc.ZeroSsbo;

    /// <summary>The transparent-black 2D stand-in, for a sampler whose texture did not resolve.</summary>
    internal static ImageId VkStandIn2D => _vkc.Tex2D;

    /// <summary>The caster map a lit stage's shadow sampler names, or the null id for a declaration that is not one:
    /// the sun cascades and the light cubes, each with its moving-caster mirror, and the two the sun's blocker search
    /// reads unfiltered. The depth agent binds the same images through <c>CasterPass.Vk*Map</c>.</summary>
    private static ImageId VkShadowMap(string name) => name switch
    {
        "uSunShadow" or "uSunShadowRaw" => CasterPass.VkCascadeMap,
        "uSunDynShadow" or "uSunDynShadowRaw" => CasterPass.VkSunDynMap,
        "uLocalShadow" => CasterPass.VkCubeMap,
        "uLocalDynShadow" => CasterPass.VkCubeDynMap,
        _ => default,
    };

    /// <summary>The shadow bracket into <paramref name="cl"/>, between the frame's uploads and its first colour pass:
    /// this frame's caster bounds, then the bake the shadow bracket recorded aside. Both read what
    /// <see cref="VkUploads"/> just staged, so the maps are baked from the records, bones and handles the colour
    /// bracket will draw with rather than from a frame-old copy.</summary>
    private static void VkRecordShadow(VkBackend vk, CommandList cl)
    {
        fixed (Sphere* cull = _dSph) fixed (Sphere* cast = _dCastSph)
            CasterGatherVk.RecordSpheres(vk, cl, cull, cast, _recOwnCap + MaxSkyDraws);
        if (CasterPass.VkBracket is { } bake) cl.Append(bake);
    }
}
