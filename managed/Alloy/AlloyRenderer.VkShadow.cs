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
    /// the sun cascades and the light cubes, each with its moving-caster mirror, and the four the blocker search
    /// reads unfiltered -- one left on the stand-in reads depth 1.0, finds no blocker and takes the hard tap where
    /// GL spreads a disc. The deferred walk binds the same images through <c>CasterPass.Vk*Map</c>.</summary>
    private static ImageId VkShadowMap(string name) => name switch
    {
        "uSunShadow" or "uSunShadowRaw" => CasterPass.VkCascadeMap,
        "uSunDynShadow" or "uSunDynShadowRaw" => CasterPass.VkSunDynMap,
        "uLocalShadow" or "uLocalShadowRaw" => CasterPass.VkCubeMap,
        "uLocalDynShadow" or "uLocalDynShadowRaw" => CasterPass.VkCubeDynMap,
        _ => default,
    };

    /// <summary>The caster cull's two sphere buffers into <paramref name="cl"/>. Called at the head of the shadow
    /// bracket, which runs ahead of the colour build that publishes this frame's bounds -- so the bake culls against
    /// the previous frame's, as the GL bake does, and every dispatch recorded after this names a live buffer.</summary>
    internal static void VkRecordCasterBounds(VkBackend vk, CommandList cl)
    {
        fixed (Sphere* cull = _dSph) fixed (Sphere* cast = _dCastSph)
            CasterGatherVk.RecordSpheres(vk, cl, cull, cast, _recOwnCap + MaxSkyDraws);
    }

    /// <summary>The bake the shadow bracket recorded aside, between the frame's uploads and its first colour pass: it
    /// reads the records, bones and handles <see cref="VkUploads"/> just staged, so the maps are baked from what the
    /// colour bracket will draw with and sampled inside the same submit.</summary>
    private static bool VkRecordShadow(CommandList cl)
    {
        if (CasterPass.VkBracket is { } bake) cl.Append(bake);
        return CasterPass.VkFlush(cl);
    }
}
