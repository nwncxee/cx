using System;
using CX.Framework;

namespace CX.Services.Anim;

/// <summary>The floor under a whole sole: <see cref="FootFloor.Combine"/> and <see cref="FootFloor.Disagree"/>. Oracle:
/// sample sets worked by hand against the rule (the sample most others lie within 0.06 of wins, the higher on a tie, a
/// miss casts no vote).</summary>
internal static class FootFloorCheck
{
    private const float Tol = 0.06f, Miss = float.NaN;

    /// <summary>Runs the combining cases; each logs a FAIL line naming its case when it no longer holds.</summary>
    internal static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "FootFloor.SelfCheck");
        Case(check, "a crack under one of four", 0f, true, -0.4f, 0f, 0f, 0f);
        Case(check, "a crack under the third of four", 0f, true, 0f, 0f, -0.4f, 0f);
        Case(check, "a sliver above three", 0f, true, 0f, 0.3f, 0f, 0f);
        Case(check, "a sliver above two of three", 0f, true, 0f, 0f, 0.3f);
        Case(check, "a sole half on a 20 cm step, step first", 0.2f, true, 0.2f, 0.2f, 0f, 0f);
        Case(check, "a sole half on a 20 cm step, step last", 0.2f, true, 0f, 0f, 0.2f, 0.2f);
        Case(check, "two samples on different floors take the higher", 0.2f, true, 0f, 0.2f);
        Case(check, "one of four on the step is the odd one out", 0f, true, 0f, 0f, 0f, 0.2f);
        Case(check, "three of four on the step", 0.2f, true, 0f, 0.2f, 0.2f, 0.2f);
        Case(check, "a ramp across the sole is one floor, the middle", 0f, true, -0.05f, 0f, 0.05f);
        Case(check, "all different: the highest", 0.3f, true, 0f, 0.15f, 0.3f);
        Case(check, "misses cast no vote", 0.1f, true, Miss, Miss, 0.1f);
        Case(check, "a miss does not outvote a hit", 0.1f, true, Miss, 0.1f, Miss, Miss);
        Case(check, "a miss at a hit's height gives that hit no support", 0f, true, 0f, 0f, Miss, 0.1f);
        Case(check, "all missed", 0f, false, Miss, Miss, Miss);
        Case(check, "one sample, a hit", -0.3f, true, -0.3f);
        Case(check, "one sample, a miss", 0f, false, Miss);

        FootFloor none = default;
        check.Expect(!none.Combine(Tol).Hit, "FootFloor Combine: no samples is no floor");

        var a = new FloorSample(true, 0f);
        check.Expect(!FootFloor.Disagree(a, new FloorSample(true, 0.05f), Tol), "Disagree: 5 cm apart is one floor");
        check.Expect(FootFloor.Disagree(a, new FloorSample(true, 0.07f), Tol), "Disagree: 7 cm apart is two");
        check.Expect(FootFloor.Disagree(a, new FloorSample(true, -0.4f), Tol), "Disagree: a crack below is two");
        check.Expect(FootFloor.Disagree(a, new FloorSample(false, 0f), Tol) && FootFloor.Disagree(new FloorSample(false, 0f), a, Tol),
                     "Disagree: a hit and a miss are two floors, either way round");
        check.Expect(!FootFloor.Disagree(new FloorSample(false, 0f), new FloorSample(false, 0.5f), Tol), "Disagree: two misses are one answer");

        LegFrame f = default;
        f.Knobs.FootIk = true;
        f.Scale = 1f;
        for (int i = 0; i < 3; i++) f.FloorR.Add(new FloorSample(false, 0f));
        for (int i = 0; i < 3; i++) f.FloorL.Add(new FloorSample(true, 0.1f));
        Legs.Targets(f, default, out bool hr, out float tr, out bool hl, out float tl);
        check.Expect(!hr && hl && MathF.Abs(tr - 0.1f) < 1e-6f && MathF.Abs(tl - 0.1f) < 1e-6f,
                     $"Targets: a sole with no hit borrows the other foot's combined floor: right {tr:0.000} (hit {hr}), left {tl:0.000}");

        f.Scale = 2f;
        f.FloorR = default;
        f.FloorR.Add(new FloorSample(true, 0f));
        f.FloorR.Add(new FloorSample(true, 0f));
        f.FloorR.Add(new FloorSample(true, 0.09f));
        Legs.Targets(f, default, out hr, out tr, out _, out _);
        check.Expect(hr && MathF.Abs(tr - 0.09f) < 1e-6f, $"Targets: at scale 2 a 9 cm difference is one floor (tolerance scales), the higher: {tr:0.000}");

        f.Scale = 1f;
        f.FloorR = default;
        f.FloorR.Add(new FloorSample(true, -0.4f));
        f.FloorR.Add(new FloorSample(true, 0f));
        f.FloorR.Add(new FloorSample(true, 0f));
        Legs.Targets(f, default, out hr, out tr, out _, out _);
        check.Expect(hr && MathF.Abs(tr) < 1e-6f, $"Targets: a crack under one sample of three is not the foot's floor: {tr:0.000}");
        log.Info(check.Ok ? "FootFloor.SelfCheck: passed" : "FootFloor.SelfCheck: FAILED");
    }

    /// <summary>The samples <paramref name="offs"/> (NaN a miss) combine to <paramref name="want"/> with <paramref name="hit"/>.</summary>
    private static void Case(Checker check, string name, float want, bool hit, params float[] offs)
    {
        FootFloor floor = default;
        foreach (float o in offs) floor.Add(float.IsNaN(o) ? new FloorSample(false, 0.1f) : new FloorSample(true, o));
        FloorSample got = floor.Combine(Tol);
        check.Expect(got.Hit == hit && (!hit || MathF.Abs(got.Off - want) < 1e-6f),
                     $"FootFloor Combine: {name}: wants {(hit ? want.ToString("0.00") : "no floor")}, got {(got.Hit ? got.Off.ToString("0.00") : "no floor")}");
    }
}
