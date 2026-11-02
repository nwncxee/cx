using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>
/// Regression goldens for the deferred walk (they hold on main 65203c28, so they carry no approved-spec mark): each
/// scene's HDR target against the one main 65203c28 drew, recorded on this card and embedded gzipped. Oracle: that
/// recorded image, golden data. A scene with no recording writes one under <c>.scratch/</c> and fails.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>A pixel channel matches when within two half-float steps of the recording, plus a floor for zeros.</summary>
    private const float DwRel = 1e-3f, DwAbs = 1e-4f;

    private static void DeferredWalkSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        // Given a stone wall lit by 40 overlapping lamps of mixed colours and reach, 8 of them casting shadows
        // When the deferred lights draw it
        // Then every pixel matches what the previous build draws
        DwMatch(vk, check, log, "wall", DwWall(), "E3 stone wall under 40 lamps");
        DeferredRigScene waved = DwWall();
        waved.Wave = true;
        DwMatch(vk, check, log, "wall", waved, "E3 stone wall under 40 lamps, the walk with wave lockstep");

        // Given plain, glossy, clear-coated, sheen, skin and iridescent-film surfaces side by side under the same lamps
        // When the deferred lights draw them
        // Then every pixel matches what the previous build draws
        DwMatch(vk, check, log, "materials", DwMaterials(), "E4 six materials");

        // Given a hand with skin meeting a coated metal ring inside a few pixels on screen
        // When the deferred lights draw it
        // Then both surfaces match what the previous build draws, with no seam between them
        DwMatch(vk, check, log, "ring", DwRingScene(), "E5 skin against a coated ring");

        // Given a lamp in a doorway whose reach covers both the near door frame and the far room wall seen past it
        // When the deferred lights draw the view
        // Then the frame and the far wall are both lit as the previous build lights them
        DwMatch(vk, check, log, "doorway", DwDoorway(), "E6 near frame and far wall");

        // Given 130 lamps all reaching one patch of floor
        // When the deferred lights draw it
        // Then no lamp that the previous build lights there goes dark
        DwMatch(vk, check, log, "crowd", DwCrowd(), "E7 130 lamps on one patch", brighter: true);

        // Given a hak shipping cx_deferred_cs.shd written for the previous build
        // When an area with lamps draws
        // Then the area lights with the hak's shader, or, if it no longer fits, cx.log names it as refused and the stock walk lights the area
        DeferredRigScene hak = DwWall();
        hak.WalkOverride = DwResource("golden/DeferredWalkHak") is { } text
            ? ShaderSource.Expand(System.Text.Encoding.UTF8.GetString(text), "cx_deferred_cs") : null;
        check.Expect(hak.WalkOverride != null, "E9: the hak fixture (main 65203c28's cx_deferred_cs) is embedded");
        if (hak.WalkOverride == null) return;
        DwMatch(vk, check, log, "wall", hak, "E9 a hak's walk from main 65203c28");
        check.Expect(hak.WalkFromContent, "E9: the hak's own walk drew the frame, not the shipped stage it falls back to");
    }

    /// <summary>The rig's frame for <paramref name="sc"/>, or null with the check failed.</summary>
    private static float[]? DwFrame(VkBackend vk, Checker check, ExtenderLog log, DeferredRigScene sc, string what)
    {
        float[]? got = AlloyRenderer.DeferredRig(vk, sc, log);
        check.Expect(got != null, $"{what}: the rig frame was refused (logged above)");
        return got;
    }

    /// <summary>Every channel of the frame against the recording for <paramref name="name"/>: within the tolerance
    /// either way, or with <paramref name="brighter"/> no darker than it less the tolerance.</summary>
    private static void DwMatch(VkBackend vk, Checker check, ExtenderLog log, string name, DeferredRigScene sc, string what,
                                bool brighter = false)
    {
        if (DwFrame(vk, check, log, sc, what) is not { } got || DwRecorded(check, log, name, got, what) is not { } want) return;
        int bad = 0, first = -1;
        float worst = 0f;
        for (int i = 0; i < got.Length; i++)
        {
            if ((i & 3) == 3) continue;
            float tol = DwRel * MathF.Max(MathF.Abs(got[i]), MathF.Abs(want[i])) + DwAbs;
            if (brighter ? got[i] >= want[i] - tol : MathF.Abs(got[i] - want[i]) <= tol) continue;
            if (bad++ == 0) first = i;
            worst = MathF.Max(worst, MathF.Abs(got[i] - want[i]));
        }
        string how = brighter ? "darker than" : "off";
        log.Info($"GpuGolden: deferred walk {what}, {bad} channel(s) {how} the recording, worst {worst:G4}");
        check.Expect(bad == 0, $"{what}: {bad} channel(s) {how} main 65203c28's frame, first at pixel "
                             + $"({first / 4 % DwW}, {first / 4 / DwW}) {(first >= 0 ? got[first] : 0f):G6} against {(first >= 0 ? want[first] : 0f):G6}, worst {worst:G4}");
    }

    /// <summary>The recorded frame for <paramref name="name"/>; when none is embedded, <paramref name="got"/> is
    /// written to <c>.scratch/deferred-walk/</c> for a reviewer to adopt and the check fails.</summary>
    private static float[]? DwRecorded(Checker check, ExtenderLog log, string name, float[] got, string what)
    {
        if (DwResource($"golden/DeferredWalk.{name}.bin") is { } bytes)
        {
            var want = new float[bytes.Length / 2];
            ReadOnlySpan<Half> h = MemoryMarshal.Cast<byte, Half>(bytes);
            for (int i = 0; i < want.Length; i++) want[i] = (float)h[i];
            check.Expect(want.Length == got.Length, $"{what}: the recording holds {want.Length} channels, the frame {got.Length}");
            return want.Length == got.Length ? want : null;
        }
        string dir = Path.Combine(Environment.CurrentDirectory, ".scratch", "deferred-walk");
        Directory.CreateDirectory(dir);
        var halves = new Half[got.Length];
        for (int i = 0; i < got.Length; i++) halves[i] = (Half)got[i];
        string path = Path.Combine(dir, $"DeferredWalk.{name}.bin.gz");
        using (var gz = new GZipStream(File.Create(path), CompressionLevel.SmallestSize))
            gz.Write(MemoryMarshal.AsBytes(halves.AsSpan()));
        log.Info($"GpuGolden: deferred walk {what} has no recording; this frame was written to {path}");
        check.Expect(false, $"{what}: no recorded frame is embedded (this one is at {path})");
        return null;
    }

    /// <summary>An embedded fixture's bytes, gunzipped when its name says so; null when it is not embedded.</summary>
    private static byte[]? DwResource(string name)
    {
        using Stream? s = typeof(GpuGolden).Assembly.GetManifestResourceStream(name);
        if (s == null) return null;
        using var ms = new MemoryStream();
        if (name.EndsWith(".bin", StringComparison.Ordinal))
        {
            using var gz = new GZipStream(s, CompressionMode.Decompress);
            gz.CopyTo(ms);
        }
        else s.CopyTo(ms);
        return ms.ToArray();
    }
}
