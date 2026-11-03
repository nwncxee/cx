using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>
/// The shading-parity goldens' rig scene: one plain wall facing the camera head-on, one light two metres in front of
/// its centre, and every pixel's record set by hand so a single term of the walk reaches the frame.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The light every parity scene hangs in front of the wall's centre: position and reach, white.</summary>
    private static readonly Vector4 ParityLamp = new(0f, 0f, 2f, 20f);

    /// <summary>A parity scene: the wall's pixels face <paramref name="n"/> and read slot 3, opaque unless
    /// <paramref name="alpha"/> says otherwise; the lamp's slot word is <paramref name="word"/> (-1 casts no shadow,
    /// -2 the ambient class, 0 cube slot 0). <paramref name="edit"/> sets each pixel's weights.</summary>
    private static DeferredRigScene ParityScene(Vector3 n, float word, float alpha, Func<RigPixel, RigPixel> edit)
    {
        DeferredRigScene sc = DwCamera(new Vector3(0f, 0f, 10f), Vector3.Zero);
        DwSurfaces(sc, [new(Vector3.Zero, Vector3.UnitZ, (_, _) => DwStone)]);
        for (int i = 0; i < sc.Pixels.Length; i++)
        {
            if (sc.Pixels[i].Flags == 0f) continue;
            RigPixel p = sc.Pixels[i];
            p.N = n; p.Slot = 3; p.SpecT = 0f; p.DiffT = Vector3.Zero; p.AmbT = Vector3.Zero;
            p.ShSlots = new Vector4(0f, 1f, 2f, 3f); p.ShVis = Vector4.Zero;
            sc.Pixels[i] = edit(p);
        }
        sc.Lights = [ParityLamp.X, ParityLamp.Y, ParityLamp.Z, ParityLamp.W, 1f, 1f, 1f, word];
        sc.Cubes = word >= 0f ? [ParityLamp] : [];
        sc.PartAlpha = [1f, 1f, 1f, alpha];
        sc.Plain = true;
        return sc;
    }

    /// <summary>The wall's centre pixel of <paramref name="sc"/>'s frame, or null with the check failed.</summary>
    private static Vector3? ParityCentre(VkBackend vk, Checker check, ExtenderLog log, DeferredRigScene sc, string what)
    {
        if (DwFrame(vk, check, log, sc, what) is not { } px) return null;
        int at = ((DwH / 2) * DwW + DwW / 2) * 4;
        return new Vector3(px[at], px[at + 1], px[at + 2]);
    }
}
