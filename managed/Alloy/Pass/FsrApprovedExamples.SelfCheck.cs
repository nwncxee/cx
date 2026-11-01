using System;
using System.Collections.Generic;
using System.IO;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// FSR 3.1 upscaling's approved examples off the device (approved-spec), E1, E2 and E4 to E8. Oracles: sizes, phase
/// counts and mip biases worked by hand (0.67 x 2560 = 1715, int(8 x (2560 / 1715)^2) = 17, log2(1715 / 2560) - 1 =
/// -1.58), cx.log as written to disk, and the staged install's LICENSES.txt.
/// </summary>
internal static partial class Fsr
{
    static partial void Examples(Checker check, ExtenderLog log)
    {
        Post.Aa.V = Post.AaFsr; Post.Scale.V = 0.67f;
        check.Expect(On, "E1: FSR 3.1 picked at render scale 0.67, FSR draws the world");
        check.Expect(!PostChain.TaaOn && !SmaaStage.Active, "E1: ...and neither TAA nor SMAA runs");
        string e1 = Line(PostChain.RenderSize(2560), PostChain.RenderSize(1440), 2560, 1440);
        check.Expect(e1.Contains("1715x965 to 2560x1440", StringComparison.Ordinal),
            $"E1: cx.log says FSR drew it from 1715x965 up to 2560x1440 (got \"{e1}\")");

        Post.Scale.V = 1f;
        check.Expect(On && !PostChain.TaaOn, "E2: FSR 3.1 picked at render scale 1.0 still draws, and TAA does not run");
        string e2 = Line(PostChain.RenderSize(2560), PostChain.RenderSize(1440), 2560, 1440);
        check.Expect(e2.Contains("2560x1440 to 2560x1440", StringComparison.Ordinal),
            $"E2: cx.log says FSR drew it 2560x1440 to 2560x1440 (got \"{e2}\")");

        Post.Scale.V = 0.67f;
        int phases = PostChain.JitterPhases(2560);
        check.Expect(phases == 17, $"E4: FSR 3.1 picked at 0.67 on a 2560-wide window, the jitter repeats after 17 (got {phases})");
        var seen = new HashSet<(float, float)>();
        for (int f = 0; f < 17; f++)
        {
            PostChain.Jitter(f, 1715, 965, phases, out float jx, out float jy);
            seen.Add((jx, jy));
        }
        PostChain.Jitter(0, 1715, 965, phases, out float x0, out float y0);
        PostChain.Jitter(17, 1715, 965, phases, out float x17, out float y17);
        check.Expect(seen.Count == 17 && x0 == x17 && y0 == y17, $"E4: ...visiting 17 positions before it repeats (got {seen.Count})");

        check.Expect(MathF.Abs(PostChain.LodBias + 1.58f) < 0.01f, $"E5: FSR 3.1 picked at 0.67, textures sample with a mip bias of -1.58 (got {PostChain.LodBias:F3})");
        Post.Aa.V = Post.AaTaa;
        check.Expect(MathF.Abs(PostChain.LodBias + 0.58f) < 0.01f, $"E5: ...where TAA at the same scale uses -0.58 (got {PostChain.LodBias:F3})");

        RefusedExample(check, log);
        NewAreaExample(check);
        LicenceExample(check);
    }

    /// <summary>E6: a refusal is said once on cx.log, shown on the panel, and hands the frame back to TAA.</summary>
    private static void RefusedExample(Checker check, ExtenderLog log)
    {
        Post.Aa.V = Post.AaFsr;
        string why = $"the test GPU refused the upscaler ({Environment.TickCount64})";
        Refuse(why, log);
        Refuse(why, log);
        try
        {
            check.Expect(!On && PostChain.TaaOn, "E6: a refused FSR does not draw, and the frame draws with TAA");
            string panel = Post.G.PanelStatus?.Invoke() ?? "";
            check.Expect(panel.Contains("FSR", StringComparison.Ordinal) && panel.Contains(why, StringComparison.Ordinal),
                $"E6: the settings panel says FSR is off for the session, and why (got \"{panel}\")");
            ExtenderLog.Flush();
            string path = Path.Combine(HostEnv.Home, "cx.log");
            if (!OperatingSystem.IsLinux() || !File.Exists(path)) { check.Skip("E6's cx.log half: no cx.log under $HOME to read"); return; }
            int said = 0;
            foreach (string line in File.ReadLines(path)) if (line.Contains(why, StringComparison.Ordinal)) said++;
            check.Expect(said == 1, $"E6: cx.log says why once (said {said} times)");
        }
        finally { Forget(); }
    }

    /// <summary>E7: the world edge starts FSR's history over, once.</summary>
    private static void NewAreaExample(Checker check)
    {
        Post.Aa.V = Post.AaFsr;
        TakeReset();
        PassRegistry.Global.Revoke();
        bool first = TakeReset(), second = TakeReset();
        check.Expect(first && !second, $"E7: the first frame in a new area starts FSR's history over, and only that one (got {first}, {second})");
    }

    /// <summary>E8: the install's LICENSES.txt carries AMD's MIT notice, and the panel never says AMD FidelityFX.</summary>
    private static void LicenceExample(Checker check)
    {
        string label = Post.Aa.Label + " " + Post.Aa.Help;
        check.Expect(!label.Contains("FidelityFX", StringComparison.OrdinalIgnoreCase), $"E8: the panel never says AMD FidelityFX (\"{label}\")");
        string path = Path.Combine("cx-linux-x64", "LICENSES.txt");
        if (!File.Exists(path)) { check.Skip("E8's licence half: no staged cx-linux-x64/LICENSES.txt here (run-demo has one)"); return; }
        string text = File.ReadAllText(path);
        int at = text.IndexOf("FidelityFX", StringComparison.Ordinal);
        check.Expect(at >= 0 && text.IndexOf("Advanced Micro Devices", at, StringComparison.Ordinal) > at
                     && text.IndexOf("Permission is hereby granted, free of charge", at, StringComparison.Ordinal) > at,
            "E8: LICENSES.txt carries AMD's MIT notice for the FidelityFX SDK");
    }
}
