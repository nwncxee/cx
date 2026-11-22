using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Ui;

namespace CX.Alloy;

/// <summary>Approved examples of the VRAM pressure ladder, fed device figures in the stub host. Oracles: hand-written
/// budgets and use, the page and shadow sizes they work out to by hand, and the log lines they must give.</summary>
internal static class VramLadderApprovedExamples
{
    /// <summary>The budget every ladder example's device reports, MiB; the bake's ladder examples read the same figures.</summary>
    internal const long Budget = 8000;
    /// <summary>92% of <see cref="Budget"/>: past the 90% the ladder gives way at.</summary>
    internal const long Past = 7360;
    /// <summary>85% of <see cref="Budget"/>: inside the band the ladder holds in.</summary>
    internal const long Band = 6800;
    /// <summary>70% of <see cref="Budget"/>: under the 80% the ladder takes back at.</summary>
    internal const long Under = 5600;
    /// <summary>The <c>lightmap.vram</c> figure the pool examples size from: 10 pages of 96 MiB when nothing gave way.</summary>
    private const int PoolVram = 960;
    private const long PoolMib = 960;

    /// <summary>Every CPU example, each on a freshly armed ladder; the ladder is disarmed after.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        int cube0 = CasterPass.CubeFaceSize, dyn0 = CasterPass.CubeDynFaceSize;
        try
        {
            TexturesFirst(check, cube0, dyn0);
            PoolFromLiveBudget(check, cube0, dyn0);
            ShadowsHalveOnce(check, cube0, dyn0);
            TakenBack(check, cube0, dyn0);
            NeverPast(check, cube0, dyn0);
            SmallCubeKept(check, dyn0);
            BandHolds(check, cube0, dyn0);
            OncePerLowering(check, cube0, dyn0);
            PanelSaysLowered(check, cube0, dyn0);
        }
        finally
        {
            VramLadder.AreaChanged(0, Budget, 0);
            VramLadder.Reset(null, cube0, dyn0);
        }
        check.Expect(CasterPass.CubeFaceSize == cube0 && CasterPass.CubeDynFaceSize == dyn0,
                     $"the ladder left the shadow arrays at {CasterPass.CubeFaceSize}/{CasterPass.CubeDynFaceSize}, not {cube0}/{dyn0}");
        log.Info(check.Ok ? "VramLadderApprovedExamples.SelfCheck: passed" : "VramLadderApprovedExamples.SelfCheck: FAILED");
    }

    private static ExtenderLog Ear(List<string> heard) => new() { Tap = heard.Add };

    /// <summary>Lines in <paramref name="heard"/> holding <paramref name="tag"/>.</summary>
    private static int Count(List<string> heard, string tag) => GpuGolden.Count(heard, tag);

    /// <summary>Textures lines in <paramref name="heard"/> that say how many MiB the eviction freed.</summary>
    private static int TextureLines(List<string> heard)
    {
        int n = 0;
        foreach (string l in heard)
            if (l.Contains(VramLadder.TexturesTag, StringComparison.Ordinal) && AlloyRenderer.ClauseMiB(l, VramLadder.FreedKey) != null) n++;
        return n;
    }

    /// <summary>Climb to the shadow rung from nothing given: two periods, an area change, a period, all past 90%.</summary>
    private static VramRung Climb()
    {
        VramLadder.Period(Past, Budget);
        VramLadder.Period(Past, Budget);
        VramLadder.AreaChanged(Past, Budget, PoolMib);
        return VramLadder.Period(Past, Budget);
    }

    // Given VRAM use passes 90% of the budget
    // When the next period starts
    // Then idle textures are evicted first, and cx.log says once per area how many MiB that freed
    /// <summary>E1. Oracle: the rung named first by hand, and one texture line per area counted off the tapped log.</summary>
    private static void TexturesFirst(Checker check, int cube, int dyn)
    {
        var heard = new List<string>();
        VramLadder.Reset(Ear(heard), cube, dyn);
        VramRung first = VramLadder.Period(Past, Budget);
        check.Expect(first == VramRung.Textures, $"E1: the first period past 90% gave {first}, not the textures");
        VramLadder.Period(Past, Budget);
        VramLadder.Period(Past, Budget);
        check.Expect(TextureLines(heard) == 1, $"E1: three periods past 90% in one area logged {TextureLines(heard)} texture lines, not 1");
        VramLadder.AreaChanged(Past, Budget, PoolMib);
        VramLadder.Period(Past, Budget);
        VramLadder.Period(Past, Budget);
        check.Expect(TextureLines(heard) == 2, $"E1: a second area past 90% brought the texture lines to {TextureLines(heard)}, not 2");
    }

    // Given an area change while use is past 90%
    // When the player changes area
    // Then it is sized from the live budget, in whole 96 MiB pages, and shrinks to what fits
    /// <summary>E3. Oracle: pages worked by hand -- 7500 of 8000 MiB with a 960 MiB pool leaves 660 MiB under 90%,
    /// 6 whole pages; 7300 leaves 860 MiB, 8 pages.</summary>
    private static void PoolFromLiveBudget(Checker check, int cube, int dyn)
    {
        VramLadder.Reset(null, cube, dyn);
        VramLadder.Period(Past, Budget);
        VramLadder.Period(Past, Budget);
        VramLadder.AreaChanged(7500, Budget, PoolMib);
        check.Expect(VramLadder.Given == VramRung.Pool, $"E3: an area change past 90% left the ladder at {VramLadder.Given}, not the pool");
        int six = VramLadder.PoolPages(PoolVram);
        check.Expect(six == 6, $"E3: 7500 of 8000 MiB with a 960 MiB pool sized the pool at {six} pages, not 6");
        VramLadder.Reset(null, cube, dyn);
        VramLadder.Period(Past, Budget);
        VramLadder.Period(Past, Budget);
        VramLadder.AreaChanged(7300, Budget, PoolMib);
        int eight = VramLadder.PoolPages(PoolVram);
        check.Expect(eight == 8, $"E3: 7300 of 8000 MiB with a 960 MiB pool sized the pool at {eight} pages, not 8");
    }

    // Given use is past 90% after every step above
    // When the next period starts
    // Then local light shadow cubes and moving-object shadows each halve once (2048 to 1024, 1024 to 512, 512 to 256), and cx.log says that shadow quality was lowered to fit VRAM
    /// <summary>E4. Oracle: the halvings written by hand, the arrays' sizes from before the climb, and one line
    /// counted off the tapped log.</summary>
    private static void ShadowsHalveOnce(Checker check, int cube, int dyn)
    {
        var heard = new List<string>();
        VramLadder.Reset(Ear(heard), cube, dyn);
        VramRung r = Climb();
        check.Expect(r == VramRung.Shadows, $"E4: the period after the pool gave {r}, not the shadows");
        check.Expect(VramLadder.Draws(2048) == 1024 && VramLadder.Draws(1024) == 512 && VramLadder.Draws(512) == 256,
                     $"E4: lowered, 2048/1024/512 draw at {VramLadder.Draws(2048)}/{VramLadder.Draws(1024)}/{VramLadder.Draws(512)}, not 1024/512/256");
        check.Expect(CasterPass.CubeFaceSize * 2 == cube, $"E4: the light cubes draw at {CasterPass.CubeFaceSize}, not half of {cube}");
        check.Expect(CasterPass.CubeDynFaceSize * 2 == dyn, $"E4: the moving-object cubes draw at {CasterPass.CubeDynFaceSize}, not half of {dyn}");
        VramLadder.Period(Past, Budget);
        VramLadder.Period(Past, Budget);
        check.Expect(VramLadder.Draws(512) == 256 && CasterPass.CubeFaceSize * 2 == cube,
                     $"E4: two more periods past 90% halved again: 512 draws at {VramLadder.Draws(512)}, the cubes at {CasterPass.CubeFaceSize}");
        check.Expect(Count(heard, VramLadder.ShadowsTag) == 1, $"E4: cx.log said shadow quality was lowered {Count(heard, VramLadder.ShadowsTag)} times, not once");
    }

    // Given use falls under 80% of the budget
    // When the player next changes area
    // Then the shadows return to the sizes their settings name, and the pool returns to the size it takes when nothing gave way
    /// <summary>E5. Oracle: the arrays' sizes from before the climb, and the 10 pages 960 MiB is when nothing gave way.</summary>
    private static void TakenBack(Checker check, int cube, int dyn)
    {
        VramLadder.Reset(null, cube, dyn);
        Climb();
        check.Expect(VramLadder.ShadowsLowered, "E5: the climb did not lower the shadows, so nothing is there to take back");
        VramLadder.Period(Under, Budget);
        VramLadder.AreaChanged(Under, Budget, 576);
        check.Expect(!VramLadder.ShadowsLowered && CasterPass.CubeFaceSize == cube && CasterPass.CubeDynFaceSize == dyn,
                     $"E5: under 80% at an area change the cubes draw at {CasterPass.CubeFaceSize}/{CasterPass.CubeDynFaceSize}, not {cube}/{dyn}");
        int pages = VramLadder.PoolPages(PoolVram);
        check.Expect(pages == 10 && VramLadder.Given == VramRung.None, $"E5: taken back, the pool takes {pages} pages at {VramLadder.Given}, not 10 with nothing given");
    }

    // Given use stays under 90% all session
    // When the session runs
    // Then nothing above happens: full scratch, the full pool, full shadows
    /// <summary>E6. Oracle: 89% of the budget by hand, the 10 pages of a 960 MiB pool, the named sizes; a 91% period
    /// after is the control that the ladder is listening at all.</summary>
    private static void NeverPast(Checker check, int cube, int dyn)
    {
        var heard = new List<string>();
        VramLadder.Reset(Ear(heard), cube, dyn);
        for (int i = 0; i < 5; i++) VramLadder.Period(7120, Budget);
        VramLadder.AreaChanged(7120, Budget, PoolMib);
        VramLadder.Period(7120, Budget);
        check.Expect(VramLadder.Given == VramRung.None && !VramLadder.HalfScratch && VramLadder.PoolPages(PoolVram) == 10
                     && VramLadder.Draws(512) == 512 && CasterPass.CubeFaceSize == cube && heard.Count == 0,
                     $"E6: under 90% all session the ladder gave {VramLadder.Given} and logged {heard.Count} lines");
        VramRung control = VramLadder.Period(7280, Budget);
        check.Expect(control == VramRung.Textures, $"E6: the control period at 91% gave {control}, not the textures");
    }

    // Given shadow.cubesize 256 or less, and use past 90% after the pool step
    // When the next period starts
    // Then the cube size is unchanged and cx.log has no shadow line
    /// <summary>E8. Oracle: a hand-named 256 cube size that must stay 256, and no shadow line on the tapped log.</summary>
    private static void SmallCubeKept(Checker check, int dyn)
    {
        var heard = new List<string>();
        int before = CasterPass.CubeFaceSize;
        VramLadder.Reset(Ear(heard), 256, dyn);
        VramLadder.Period(Past, Budget);
        VramLadder.Period(Past, Budget);
        VramLadder.AreaChanged(Past, Budget, PoolMib);
        check.Expect(VramLadder.Given == VramRung.Pool, $"E8: the ladder stands at {VramLadder.Given}, not after the pool step");
        VramLadder.Period(Past, Budget);
        check.Expect(VramLadder.Draws(256) == 256 && !VramLadder.ShadowsLowered && CasterPass.CubeFaceSize == before,
                     $"E8: a 256 cube size draws at {VramLadder.Draws(256)}, the cubes at {CasterPass.CubeFaceSize}");
        check.Expect(Count(heard, VramLadder.ShadowsTag) == 0, $"E8: cx.log has {Count(heard, VramLadder.ShadowsTag)} shadow lines, not none");
    }

    // Given the ladder gave way and use sits between 80% and 90%
    // When periods pass and the player changes area
    // Then nothing more is given and nothing taken back
    /// <summary>E12. Oracle: 85% of the budget by hand, and the rung and the 8 pages (7360 of 8000 MiB with a 960 MiB
    /// pool leaves 800 MiB) the climb stood at before the band.</summary>
    private static void BandHolds(Checker check, int cube, int dyn)
    {
        VramLadder.Reset(null, cube, dyn);
        Climb();
        check.Expect(VramLadder.Given == VramRung.Shadows && VramLadder.PoolPages(PoolVram) == 8,
                     $"E12: the climb stands at {VramLadder.Given} with {VramLadder.PoolPages(PoolVram)} pages, not the shadows with 8");
        for (int i = 0; i < 4; i++) VramLadder.Period(Band, Budget);
        VramLadder.AreaChanged(Band, Budget, 768);
        VramLadder.Period(Band, Budget);
        check.Expect(VramLadder.Given == VramRung.Shadows && VramLadder.ShadowsLowered && VramLadder.PoolPages(PoolVram) == 8
                     && VramLadder.Draws(512) == 256 && CasterPass.CubeFaceSize * 2 == cube,
                     $"E12: in the band the ladder moved: {VramLadder.Given}, {VramLadder.PoolPages(PoolVram)} pages, 512 draws at {VramLadder.Draws(512)}");
    }

    // Given shadows lowered, taken back, then lowered again
    // When each lowering happens
    // Then cx.log says so once per lowering
    /// <summary>E15. Oracle: shadow lines counted off the tapped log, one after the first lowering and two after the second.</summary>
    private static void OncePerLowering(Checker check, int cube, int dyn)
    {
        var heard = new List<string>();
        VramLadder.Reset(Ear(heard), cube, dyn);
        Climb();
        VramLadder.Period(Past, Budget);
        int one = Count(heard, VramLadder.ShadowsTag);
        VramLadder.AreaChanged(Under, Budget, 768);
        check.Expect(!VramLadder.ShadowsLowered, "E15: an area change under 80% did not take the shadows back");
        Climb();
        VramLadder.Period(Past, Budget);
        int two = Count(heard, VramLadder.ShadowsTag);
        check.Expect(one == 1 && two == 2, $"E15: cx.log said shadow quality was lowered {one} then {two} times, not 1 then 2");
    }

    // Given shadows lowered to fit VRAM
    // When /settings opens
    // Then the shadow.cubesize row still reads its value and says it is lowered to fit VRAM, and cx.cfg is unchanged
    /// <summary>E14. Oracle: the knob's stored text from before the lowering, and the note the ladder declares.</summary>
    private static void PanelSaysLowered(Checker check, int cube, int dyn)
    {
        string stored = Shadows.CubeSize.FormatStored(), shown = Shadows.CubeSize.Format();
        VramLadder.Reset(null, cube, dyn);
        Climb();
        check.Expect(VramLadder.ShadowsLowered, "E14: the climb did not lower the shadows");
        var body = new GlyphAtlas { LineHeight = 24, Ascent = 19, SpaceAdvance = 6f };
        var small = new GlyphAtlas { LineHeight = 19, Ascent = 15, SpaceAdvance = 5f };
        for (int cp = 0x20; cp < 0x2600; cp++) { body.SetTestGlyph(cp, cp == ' ' ? 6f : 11f); small.SetTestGlyph(cp, cp == ' ' ? 5f : 8f); }
        var p = new KnobPanel { Category = Array.IndexOf(KnobPanel.KnobCategories, "Graphics") };
        p.OpenAt(1920f, 1080f, KnobPanel.FacePx, body, small);
        KnobPanel.Row? row = null;
        for (int s = 0; s < p.Subs.Count && row == null; s++)
        {
            p.Sub = s; p.AdvOpen = true; p.LayoutRows();
            foreach (KnobPanel.Row r in p.Rows) if (ReferenceEquals(r.K, Shadows.CubeSize)) row = r;
        }
        check.Expect(row != null, "E14: /settings has no shadow.cubesize row");
        if (row == null) return;
        row.Refresh();
        string text = row.Text + " " + string.Join(" ", row.Label) + " " + string.Join(" ", row.Help) + " " + row.HelpText;
        check.Expect(row.Value == shown, $"E14: lowered, the shadow.cubesize row reads \"{row.Value}\", not \"{shown}\"");
        check.Expect(text.Contains(VramLadder.LoweredNote, StringComparison.Ordinal),
                     $"E14: the shadow.cubesize row does not say it is lowered to fit VRAM: \"{text}\"");
        check.Expect(Shadows.CubeSize.FormatStored() == stored, $"E14: cx.cfg's shadow.cubesize became \"{Shadows.CubeSize.FormatStored()}\", not \"{stored}\"");
    }
}
