using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The FSR sky golden's fixture: a summed fog volume of the golden's own, bound where a stage reads <c>uVFogVol</c>.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Bind <paramref name="total"/> as this frame's built fog volume on <paramref name="vk"/>; the null id
    /// starts the fog's device state over.</summary>
    internal static void VkFogFixture(VkBackend vk, ImageId total)
        => _vkFog = total.IsNull ? new VkFogState() : new VkFogState { Dev = vk, Total = total, Built = true };

    /// <summary>The colour pass's device state for a <paramref name="w"/> x <paramref name="h"/> frame, its stand-in
    /// images among it, as a frame makes it; false when refused (logged).</summary>
    internal static bool VkFixtureEnsure(VkBackend vk, int w, int h, ExtenderLog log) => VkEnsure(vk, w, h, log);

    /// <summary>The colour frame's lead-in on <paramref name="vk"/> as a frame runs it: the uniform arena rewound and
    /// every draw record uploaded into <paramref name="cl"/>; false when an upload was refused.</summary>
    internal static bool VkFixtureFrame(VkBackend vk, CommandList cl)
    {
        VkArenaRewind();
        _vkc.DirtyAll = true;
        return VkUploads(vk, cl);
    }

    /// <summary>Own slot <paramref name="i"/> as an engine part of VDO slot <paramref name="vao"/>, item <paramref name="item"/>,
    /// placed at <paramref name="model"/>; false when the slot is taken.</summary>
    internal static bool PlaceFixture(int i, int vao, int item, System.Numerics.Matrix4x4 model)
    {
        if ((uint)i >= (uint)MaxOwned || _oTri[i] != System.IntPtr.Zero || DbSlot(i)) return false;
        _oTri[i] = FixtureTri;
        _oVao[i] = vao; _oItem[i] = item;
        _dData[i].Model = model;
        return true;
    }

    /// <summary>Give back a slot <see cref="PlaceFixture"/> took.</summary>
    internal static void ClearFixture(int i)
    {
        if ((uint)i >= (uint)MaxOwned || _oTri[i] != FixtureTri) return;
        _oTri[i] = System.IntPtr.Zero;
        _oVao[i] = 0; _oItem[i] = 0;
        _dData[i] = default;
    }

    /// <summary>The tri a fixture slot holds: never dereferenced, it only marks the slot owned.</summary>
    private static readonly System.IntPtr FixtureTri = new(1);
}
