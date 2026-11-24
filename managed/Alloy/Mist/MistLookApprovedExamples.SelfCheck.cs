using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>How area mist looks: about a metre deep in the docks, lit by the area's moon or sun rather than its fog colour,
/// and parted deep enough to see. Oracle, by hand: the docks quicksave's fog amount 3 and black night fog, a pale blue moon.</summary>
internal static unsafe class MistLookApprovedExamples
{
    /// <summary>The Waterdeep docks quicksave's fog amount, read off its area's night palette.</summary>
    private const float Docks = 3f;

    /// <summary>A pale blue moon, the shape of the docks' night diffuse.</summary>
    private static readonly Vector3 Moon = new(0.20f, 0.22f, 0.30f);

    /// <summary>A warm noon sun.</summary>
    private static readonly Vector3 Noon = new(0.90f, 0.80f, 0.60f);

    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MistLookApprovedExamples.SelfCheck");
        bool on = VolumetricFog.Enabled.V, mist = VolumetricFog.Mist.V;
        string quality = VolumetricFog.Quality.V;
        bool fogOn = FrameInputs.FogOn, night = FrameInputs.Night, interior = FrameInputs.Interior, blind = FrameInputs.Blind;
        bool hasDif = FrameInputs.HasAreaDiffuse;
        Vector3 fogCol = FrameInputs.FogColor, dif = FrameInputs.AreaDiffuse;
        try
        {
            VolumetricFog.Quality.V = "medium";
            VolumetricFog.Enabled.V = true;
            VolumetricFog.Mist.V = true;
            Deeper(check);
            Moonlit(check);
            VisibleTrail(check);
        }
        finally
        {
            VolumetricFog.Enabled.V = on;
            VolumetricFog.Mist.V = mist;
            VolumetricFog.Quality.V = quality;
            FrameInputs.FogOn = fogOn; FrameInputs.Night = night; FrameInputs.Interior = interior; FrameInputs.Blind = blind;
            FrameInputs.HasAreaDiffuse = hasDif; FrameInputs.FogColor = fogCol; FrameInputs.AreaDiffuse = dif;
            GroundMist.Reset();
        }
        log.Info(check.Ok ? "MistLookApprovedExamples.SelfCheck: passed" : "MistLookApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    // Given the Waterdeep docks (fog amount 3) with Volumetric fog and Ground mist on
    // When the player stands on open ground
    // Then mist covers the ground to about 1 m deep, where today it is 0.3 m.
    private static void Deeper(Checker check)
    {
        Lay(Flat(), Vector3.Zero, true, Docks);
        Run(2f, Vector2.Zero);
        float deep = Near(0f, 0f, 4f);
        check.Expect(deep > 0.8f && deep < 1.2f, $"E22 the docks lay {deep:F3} m of mist by the player (want about 1 m)");
    }

    /// <summary>The fog terms for a camera over the docks, under <paramref name="diffuse"/> at night or by day.</summary>
    private static FogVolume.Terms Under(Vector3 diffuse, bool atNight, bool indoors)
    {
        FrameInputs.FogOn = true; FrameInputs.FogColor = Vector3.Zero;
        FrameInputs.Night = atNight; FrameInputs.Interior = indoors; FrameInputs.Blind = false;
        FrameInputs.HasAreaDiffuse = true; FrameInputs.AreaDiffuse = diffuse;
        Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0f, -12f, 8f), Vector3.Zero, Vector3.UnitZ);
        Matrix4x4 vp = view * Matrix4x4.CreatePerspectiveFieldOfView(1f, 16f / 9f, 0.5f, 200f);
        int* viewport = stackalloc int[] { 0, 0, 1280, 720 };
        FogVolume.Prepare(view, vp, viewport, 200f, true, true, 0f, out FogVolume.Terms t);
        return t;
    }

    // Given the docks at night, where the area's fog colour is black
    // When the player looks at mist away from lamps
    // Then it shows pale in the moonlight's colour rather than black, lighter than with the area's fog colour alone.
    private static void Moonlit(Checker check)
    {
        FogVolume.Terms night = Under(Moon, true, false);
        float sky = night.MistSky.X + night.MistSky.Y + night.MistSky.Z, fog = night.Ambient.X + night.Ambient.Y + night.Ambient.Z;
        check.Expect(sky > fog && night.MistSky.Z > night.MistSky.X,
                     $"E25 at night the mist takes {night.MistSky} against the fog colour's {night.Ambient} (want lighter, and blue as the moon)");
        FogVolume.Terms day = Under(Noon, false, false);
        check.Expect(day.MistSky.X > day.MistSky.Z, $"E25 by day the mist takes {day.MistSky}, warm as the sun (want red over blue)");
    }

    // Given the docks at night with mist on the ground
    // When the player walks 5 m through it
    // Then a screenshot shows the path just walked as a visible gap in the mist, and the gap has closed ten seconds later.
    private static void VisibleTrail(Checker check)
    {
        Lay(Flat(), Vector3.Zero, true, Docks);
        Run(1f, Vector2.Zero);
        DbId world = MistPartingApprovedExamples.World(), me = MistPartingApprovedExamples.Seat(world, Vector3.Zero, true);
        MistPartingApprovedExamples.Hold(world, 0.1f);
        MistPartingApprovedExamples.Walk(world, me, Vector3.Zero, new Vector3(5f, 0f, 0f), true);
        float path = MistPartingApprovedExamples.Path(0f, 0f), beside = MistPartingApprovedExamples.Beside(0f, 0f);
        MistPartingApprovedExamples.Hold(world, 10f);
        float healed = MistPartingApprovedExamples.Path(0f, 0f), around = MistPartingApprovedExamples.Beside(0f, 0f);
        MistPartingApprovedExamples.Drop(world, me);
        check.Expect(beside - path >= 0.5f, $"E26 the walk leaves a gap {beside - path:F3} m deep ({path:F3} m on the path, {beside:F3} m beside it; want half a metre at least)");
        check.Expect(healed >= 0.9f * around, $"E26 ten seconds on the path holds {healed:F3} m beside {around:F3} m (want it closed)");
    }
}
