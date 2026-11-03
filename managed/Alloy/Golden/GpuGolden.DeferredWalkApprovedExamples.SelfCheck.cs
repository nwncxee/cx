using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>
/// The deferred walk's approved examples (approved-spec, E8 and E11) on this device, through the goldens' rig. Oracles:
/// the recorded frame of the stone wall (E8), and the lamp count of a wall every lamp reaches (E11).
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The lamps hung in front of E11's wall, every one reaching every pixel of it.</summary>
    private const int DwEvenLamps = 12;

    private static void DeferredWalkApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        // Given a GPU whose Vulkan driver offers no subgroup ballot
        // When an area with lamps draws
        // Then the lamps light it as on the previous build, and cx.log says once that the deferred walk runs without wave lockstep
        DeferredRigScene flat = DwWall();
        flat.NoWave = true;
        DwMatch(vk, check, log, "wall", flat, "E8 the stone wall without wave operations");
        bool noted = false;
        string? first = AlloyRenderer.VkDefWaveNote(false, ref noted), again = AlloyRenderer.VkDefWaveNote(false, ref noted);
        check.Expect(first != null && first.Contains("without wave lockstep", StringComparison.Ordinal),
                     $"E8: cx.log says the deferred walk runs without wave lockstep, not '{first}'");
        check.Expect(again == null, "E8: ...and says it once");

        // Given an area with lamps, after a period of play
        // When the deferred lights report their period line in cx.log
        // Then the line says how many lights a deferred pixel walked, on average and at most
        DeferredRigScene even = DwEvenWall();
        if (DwFrame(vk, check, log, even, "E11 a wall every lamp reaches") == null) return;
        string line = DeferredLights.WalkedText(even.Walked.Pixels, even.Walked.Sum, even.Walked.Most);
        check.Expect(line.Contains($"walked {DwEvenLamps:F1} lights on average, {DwEvenLamps} at most", StringComparison.Ordinal),
                     $"E11: the period line says a pixel of the wall walked its {DwEvenLamps} lamps, not '{line}'");
    }

    /// <summary>E11: a plain stone wall filling the frame, <see cref="DwEvenLamps"/> wide lamps a metre or two in front of it.</summary>
    private static DeferredRigScene DwEvenWall()
    {
        DeferredRigScene sc = DwCamera(new Vector3(0f, 2f, 10f), new Vector3(0f, 1.5f, 0f));
        DwSurfaces(sc, [new(Vector3.Zero, Vector3.UnitZ, (_, _) => DwStone)]);
        DwLights(sc, DwEvenLamps, new Vector3(-1f, 1f, 1f), new Vector3(1f, 2f, 2f), 90f, 100f, 0, 0, 1112u);
        return sc;
    }
}
