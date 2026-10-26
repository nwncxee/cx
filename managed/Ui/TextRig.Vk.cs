using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Ui;

public static partial class TextRig
{
    /// <summary>The glyph sheet's Vulkan copy on one device, and the epoch it was uploaded from.</summary>
    private sealed class RigVkState
    {
        public VkBackend? Dev;
        public ImageId Atlas;
        public SamplerId Sampler;
        public int Epoch = -1;
    }

    private static volatile RigVkState _vk = new();

    /// <summary>The replay's vertex stage, for the Vulkan replay to translate as GL compiles it.</summary>
    internal static string VertexText => VSrc;

    /// <summary>Kind <paramref name="k"/>'s fragment stage, as <see cref="VertexText"/>.</summary>
    internal static string FragmentText(UiDrawKind k) => FragmentSource(k);

    /// <summary>The occlusion opacity of a command anchored to nothing: the scene test returns 1.</summary>
    internal const float NoSceneTest = NoTest;

    /// <summary>The glyph sheet on <paramref name="vk"/>: every level as RG8 (coverage, distance field), re-uploaded
    /// after a re-bake, sampled trilinear and clamped as GL's atlas texture is. Null id before a sheet exists or when
    /// the device refused the image (logged by it).</summary>
    internal static ImageId VkAtlas(VkBackend vk, out SamplerId sampler)
    {
        if (_vk.Dev != vk) _vk = new RigVkState { Dev = vk };
        RigVkState s = _vk;
        int epoch = AtlasEpoch;
        AtlasSheet? a = _sheet;
        if (s.Sampler.IsNull) s.Sampler = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.Linear, Wrap.Clamp));
        sampler = s.Sampler;
        if (a == null || s.Epoch == epoch) return s.Atlas;
        vk.Destroy(s.Atlas);
        int levels = System.Math.Min(AtlasSheet.MipLevels, a.Mips.Length + 1);
        s.Atlas = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rg8, a.Side, a.Side, 1, levels, 1, 1,
                                               ImageUse.Sampled | ImageUse.CopyDst, "vk ui atlas"));
        s.Epoch = epoch;
        if (s.Atlas.IsNull) return s.Atlas;
        for (int l = 0; l < levels; l++)
            vk.Upload(s.Atlas, l, 0, Interleave(l == 0 ? a.Pixels : a.Mips[l - 1], l == 0 ? a.Field : a.FieldMips[l - 1]));
        _log?.Info($"TextRig: atlas uploaded to Vulkan ({a.Side}², {levels} levels RG8)");
        return s.Atlas;
    }

    /// <summary>The device went with the context, and the atlas copy with it.</summary>
    internal static void VkContextLost() => _vk = new RigVkState();
}
