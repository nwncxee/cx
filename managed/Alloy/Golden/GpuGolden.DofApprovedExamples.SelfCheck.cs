using System;
using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>
/// Depth of field's approved examples on a real device (approved-spec). Oracles: the fixture's own checker and lamp,
/// the projection's depth worked out by hand in the harness, and literal limits a person can see past: 0.2 of a
/// checker's contrast is blurred away, and a display-space frame cannot pass 1.
/// </summary>
internal static partial class GpuGolden
{
    private const int DofSplit = DofSide / 2;

    static partial void DofApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        DofSharpExample(vk, check, log);
        DofLampExample(vk, check, log);
        DofHaloExample(vk, check, log);
        DofAaExample(vk, check, log);
        DofFsrExample(vk, check, log);
        DofMouseExample(vk, check, log);
        DofConversationExample(vk, check, log);
        DofSettingsExample(vk, check, log);
        DofNeverBlurExample(vk, check, log);
    }

    private static void DofSharpExample(VkBackend vk, Checker check, ExtenderLog log)
    {
        // Given depth of field is switched on in CX's settings and the camera sits low behind the character
        // When the character stands in a town with buildings far behind them
        // Then the character stays sharp and the far buildings are blurred
        DofPx[]? o = DofRun(vk, log, static (x, y) => (DofChecker(x, y), 0f, 0f), static (x, y) => x < DofSplit ? 8f : 45f, DofCamera(8f, 70f));
        check.Expect(o != null, "Sharp character, blurred distance: the frame ran");
        if (o == null) return;
        float character = DofContrast(o, 64, 448, 64, 960), buildings = DofContrast(o, 576, 960, 64, 960);
        check.Expect(character > 0.99f, $"Sharp character, blurred distance: the character keeps its checker (contrast {character:F3})");
        check.Expect(buildings < 0.2f, $"Sharp character, blurred distance: the far buildings lose theirs (contrast {buildings:F3})");
    }

    private static void DofLampExample(VkBackend vk, Checker check, ExtenderLog log)
    {
        // Given a bright lamp far out of focus at night
        // When depth of field is on
        // Then the lamp spreads into a soft bright disc, brighter than the same lamp on the stock client
        const int lampX = 700, lampY = 512;
        const float lamp = 64f;
        DofPx[]? o = DofRun(vk, log, static (x, y) => (Math.Abs(x - lampX) <= 1 && Math.Abs(y - lampY) <= 1 ? lamp : 0f, 0f, 0f), static (x, y) => 45f, DofCamera(8f, 70f));
        check.Expect(o != null, "Bright lamps: the frame ran");
        if (o == null) return;
        int bright = 0;
        float peak = 0f, energy = 0f;
        for (int y = lampY - 64; y < lampY + 64; y++)
            for (int x = lampX - 64; x < lampX + 64; x++)
            {
                float v = DofAt(o, x, y);
                if (v > 1f) bright++;
                peak = MathF.Max(peak, v);
                energy += v;
            }
        check.Expect(peak < lamp * 0.5f, $"Bright lamps: the lamp spreads (peak {peak:F1} of {lamp})");
        check.Expect(bright >= 24, $"Bright lamps: a disc of pixels stays brighter than any display-space blur can be (got {bright})");
        check.Expect(energy > 9f * lamp * 0.7f && energy < 9f * lamp * 1.3f, $"Bright lamps: the disc keeps the lamp's light ({energy:F0} of {9f * lamp:F0})");
    }

    private static void DofHaloExample(VkBackend vk, Checker check, ExtenderLog log)
    {
        // Given a sharp character standing in front of blurred ground
        // When depth of field is on
        // Then the character's colours do not smear onto the ground around them, as they do on the stock client
        DofPx[]? o = DofRun(vk, log, static (x, y) => x < DofSplit ? (1f, 0f, 0f) : (0f, DofChecker(x, y), 0f), static (x, y) => x < DofSplit ? 8f : 45f, DofCamera(8f, 70f));
        check.Expect(o != null, "No halo: the frame ran");
        if (o == null) return;
        float smear = 0f, ground = DofContrast(o, 600, 900, 64, 960, 1);
        for (int y = 64; y < 960; y++)
            for (int x = DofSplit + 2; x < DofSplit + 40; x++) smear = MathF.Max(smear, DofAt(o, x, y));
        check.Expect(ground < 0.2f, $"No halo: the ground is blurred (contrast {ground:F3})");
        check.Expect(smear < 0.05f, $"No halo: the character's red stays off the ground beside them (got {smear:F3})");
    }

    private static void DofAaExample(VkBackend vk, Checker check, ExtenderLog log)
    {
        // Given depth of field is on and FSR is off
        // When anti-aliasing is set to TAA, then SMAA, then off
        // Then the blur looks the same in all three, with no grain in the blurred areas
        string aa = Post.Aa.V;
        float scale = Post.Scale.V;
        DofPx[]?[] runs = new DofPx[]?[3];
        try
        {
            // TAA is what draws when FSR is picked at a render scale above 1
            (string Aa, float Scale)[] modes = [(Post.AaFsr, 1.5f), (Post.AaSmaa, 1f), (Post.AaOff, 1f)];
            for (int i = 0; i < 3; i++)
            {
                Post.Aa.V = modes[i].Aa; Post.Scale.V = modes[i].Scale;
                runs[i] = DofRun(vk, log, static (x, y) => ((x / 96 + y / 96) % 2 == 0 ? 0f : 1f, 0f, 0f), static (x, y) => 45f, DofCamera(8f, 70f));
            }
        }
        finally { Post.Aa.V = aa; Post.Scale.V = scale; }
        check.Expect(runs[0] != null && runs[1] != null && runs[2] != null, "Every AA choice: the frames ran");
        if (runs[0] == null || runs[1] == null || runs[2] == null) return;
        DofPx[] a = runs[0]!, b = runs[1]!, c = runs[2]!;
        bool same = true;
        for (int i = 0; i < a.Length && same; i++) same = a[i] == b[i] && a[i] == c[i];
        check.Expect(same, "Every AA choice: TAA, SMAA and no AA leave the blur the same");
        // along a blurred edge's row the checker does not change, so each step down the column is grain
        float grain = 0f, across = 0f;
        for (int x = 566; x < 586; x++)
        {
            across = MathF.Max(across, MathF.Abs(DofAt(a, x + 1, 528) - DofAt(a, x, 528)));
            for (int y = 512; y < 544; y++) grain = MathF.Max(grain, MathF.Abs(DofAt(a, x, y + 1) - DofAt(a, x, y)));
        }
        check.Expect(across < 0.5f, $"Every AA choice: the edge is blurred (largest step {across:F3})");
        check.Expect(grain < 0.02f, $"Every AA choice: no grain in the blurred area (largest step {grain:F3})");
    }

    private static void DofFsrExample(VkBackend vk, Checker check, ExtenderLog log)
    {
        // Given depth of field is on and FSR is on at a render scale of 0.5
        // When the same scene draws
        // Then the same areas are sharp and blurred as with FSR off, with no bright or dark fringe where focus changes
        // the far field on the left: a coordinate read past the smaller depth's middle then finds the sharp side and shows
        static (float, float, float) Colour(int x, int y) => (x >= DofSplit ? 4f * DofChecker(x, y) + 0.2f : 0.2f + DofChecker(x, y), 0f, 0f);
        static float Z(int x, int y) => x >= DofSplit ? 8f : 45f;
        DofPx[]? off = DofRun(vk, log, Colour, Z, DofCamera(8f, 70f));
        DofPx[]? on = DofRun(vk, log, Colour, Z, DofCamera(8f, 70f), depthSide: DofSide / 2);
        check.Expect(off != null && on != null, "With FSR: the frames ran");
        if (off == null || on == null) return;
        float blurredOff = DofContrast(off, 64, 448, 64, 960), blurredOn = DofContrast(on, 64, 448, 64, 960);
        check.Expect(blurredOff < 0.2f && blurredOn < 0.2f, $"With FSR: the far side is blurred either way (contrast {blurredOff:F3} off, {blurredOn:F3} on)");
        int differ = 0;
        float lo = float.MaxValue, hi = 0f;
        for (int y = 0; y < DofSide; y++)
            for (int x = 0; x < DofSide; x++)
            {
                float v = DofAt(on, x, y);
                if (MathF.Abs(v - DofAt(off, x, y)) > 0.25f) differ++;
                lo = MathF.Min(lo, v);
                hi = MathF.Max(hi, v);
            }
        check.Expect(differ < DofSide * DofSide / 100, $"With FSR: the same areas are sharp and blurred ({differ} pixels differ)");
        check.Expect(lo > 0.19f && hi < 4.3f, $"With FSR: no bright or dark fringe (the frame spans {lo:F3} to {hi:F3} of 0.2 to 4.2)");
    }

    private static void DofMouseExample(VkBackend vk, Checker check, ExtenderLog log)
    {
        // Given the depth of field focus type is set to Mouse
        // When the mouse moves from the character to a distant wall
        // Then the focus moves to the wall smoothly, with no jump
        var mouse = new DofSet(Focus: Dof.FocusMouse);
        float onCharacter = DofWith(in mouse, () => DofProbe(vk, log, 300, 512)), onWall = DofWith(in mouse, () => DofProbe(vk, log, 900, 512));
        check.Expect(MathF.Abs(onCharacter - 8f) < 0.1f, $"Focus choices: the depth under the mouse on the character reads 8 m (got {onCharacter:F2})");
        check.Expect(MathF.Abs(onWall - 30f) < 0.5f, $"Focus choices: the depth under the mouse on the wall reads 30 m (got {onWall:F2})");
    }

    private static void DofConversationExample(VkBackend vk, Checker check, ExtenderLog log)
    {
        // Given a conversation has moved the camera in close
        // When depth of field is on
        // Then the character the camera is on stays sharp
        DofPx[]? o = DofRun(vk, log, static (x, y) => (DofChecker(x, y), 0f, 0f), static (x, y) => x < DofSplit ? 3f : 45f,
                            DofCamera(3f, 80f, dialog: true, mouseDepth: 30f), new DofSet(Focus: Dof.FocusMouse, NeverBlur: false));
        check.Expect(o != null, "Conversation: the frame ran");
        if (o == null) return;
        float character = DofContrast(o, 64, 448, 64, 960), behind = DofContrast(o, 576, 960, 64, 960);
        check.Expect(character > 0.99f, $"Conversation: the character stays sharp (contrast {character:F3})");
        check.Expect(behind < 0.2f, $"Conversation: what is behind them blurs (contrast {behind:F3})");
    }

    private static void DofSettingsExample(VkBackend vk, Checker check, ExtenderLog log)
    {
        // Given depth of field is on
        // When the Amount setting is raised, or the Dead zone setting is widened
        // Then the blur gets stronger, or the sharp band gets deeper
        const int lampX = 512, lampY = 512;
        static (float, float, float) Lamp(int x, int y) => (Math.Abs(x - lampX) <= 1 && Math.Abs(y - lampY) <= 1 ? 64f : 0f, 0f, 0f);
        static int Spread(DofPx[] o)
        {
            int n = 0;
            for (int y = lampY - 64; y < lampY + 64; y++)
                for (int x = lampX - 64; x < lampX + 64; x++)
                    if (DofAt(o, x, y) > 0.1f) n++;
            return n;
        }
        DofPx[]? weak = DofRun(vk, log, Lamp, static (x, y) => 20f, DofCamera(15f, 0f), new DofSet(Focus: Dof.FocusPlayer, NeverBlur: false));
        DofPx[]? strong = DofRun(vk, log, Lamp, static (x, y) => 20f, DofCamera(15f, 0f), new DofSet(Focus: Dof.FocusPlayer, NeverBlur: false, Amount: 3f));
        check.Expect(weak != null && strong != null, "Settings work: the amount frames ran");
        if (weak != null && strong != null)
            check.Expect(Spread(strong) > Spread(weak) * 3 / 2, $"Settings work: Amount 3 spreads a lamp further than 1 ({Spread(strong)} pixels, {Spread(weak)})");
        DofPx[]? thin = DofRun(vk, log, static (x, y) => (DofChecker(x, y), 0f, 0f), static (x, y) => 16.6f, DofCamera(15f, 0f), new DofSet(Focus: Dof.FocusPlayer, NeverBlur: false, Amount: 5f));
        DofPx[]? deep = DofRun(vk, log, static (x, y) => (DofChecker(x, y), 0f, 0f), static (x, y) => 16.6f, DofCamera(15f, 0f), new DofSet(Focus: Dof.FocusPlayer, NeverBlur: false, Amount: 5f, DeadZone: 10f));
        check.Expect(thin != null && deep != null, "Settings work: the dead zone frames ran");
        if (thin == null || deep == null) return;
        float t = DofContrast(thin, 400, 600, 400, 600), d = DofContrast(deep, 400, 600, 400, 600);
        check.Expect(t < 0.2f, $"Settings work: 16.6 m is past the band at Dead zone 3 (contrast {t:F3})");
        check.Expect(d > 0.99f, $"Settings work: Dead zone 10 takes 16.6 m into the band (contrast {d:F3})");
    }

    private static void DofNeverBlurExample(VkBackend vk, Checker check, ExtenderLog log)
    {
        // Given the Never blur the player setting is off and the focus type is Mouse
        // When the mouse is on a distant wall
        // Then the character near the camera blurs, and with the setting on they stay sharp
        static (float, float, float) Colour(int x, int y) => (DofChecker(x, y), 0f, 0f);
        static float Z(int x, int y) => x < DofSplit ? 8f : 30f;
        DofPx[]? off = DofRun(vk, log, Colour, Z, DofCamera(8f, 45f, mouseDepth: 30f), new DofSet(Focus: Dof.FocusMouse, NeverBlur: false));
        DofPx[]? on = DofRun(vk, log, Colour, Z, DofCamera(8f, 45f, mouseDepth: 30f), new DofSet(Focus: Dof.FocusMouse, NeverBlur: true));
        check.Expect(off != null && on != null, "Never blur the player: the frames ran");
        if (off == null || on == null) return;
        float blurred = DofContrast(off, 64, 448, 64, 960), kept = DofContrast(on, 64, 448, 64, 960);
        check.Expect(blurred < 0.2f, $"Never blur the player: off, the character blurs (contrast {blurred:F3})");
        check.Expect(kept > 0.99f, $"Never blur the player: on, the character stays sharp (contrast {kept:F3})");
    }
}
