using System;
using System.Numerics;
using CX.Framework;

namespace CX.Services.Camera;

/// <summary>The offset camera with no spring arm: the rig and the collision flag over a box world, and the arm's four
/// knobs gone from cx.cfg, chat and the reports. Oracle: the hand positions worked beside each fixture, the flag words,
/// keys and replies written here as literals. The fade's verdict enters as a bool: Alloy off and walls.fade off are the
/// same false to the rig.</summary>
internal static class SpringArmGoneApprovedExamples
{
    /// <summary>The follow focus, and the rig the engine hands back for yaw 0, pitch 90, 5 m: due south of it.</summary>
    private static readonly Vector3 Focus = new(0f, 0f, 1f), Engine = new(0f, -5f, 1f);
    private const float Dt = 1f / 60f;
    private const int Frames = 30;

    /// <summary>A trunk 2.8 to 3.2 m south of the focus, x -0.4 to 1.6: the centre ray to (1.5, -5, 1) meets it at
    /// x 0.84, the one to (0, -5, 1.5) at x 0.</summary>
    private static readonly (Vector3 Lo, Vector3 Hi) Tree = (new(-0.4f, -3.2f, 0f), new(1.6f, -2.8f, 6f));
    /// <summary>A wall 3 to 3.2 m south of the focus, 10 m wide and tall: every ray from the focus southward meets it.</summary>
    private static readonly (Vector3 Lo, Vector3 Hi) Wall = (new(-5f, -3.2f, -5f), new(5f, -3f, 5f));

    /// <summary>The four keys the arm had.</summary>
    private static readonly string[] ArmKeys = ["walls.margin", "walls.armin", "walls.armout", "walls.armradius"];
    /// <summary>A key no real setting claims, fed beside the old ones to show the fed file was read at all.</summary>
    private const string Probe = "cx-selfcheck-springarm.keep";
    /// <summary>The fade's own radius, the one walls key that stays.</summary>
    private const string RadiusKey = "walls.radius";
    /// <summary>A walls knob name no declaration has: the reply to it is the reply to a key that is gone.</summary>
    private const string NoSuchKnob = "cxselfchecknoknob";

    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        _ = Walls.G;
        Quaternion q = CameraService.Pose(0f, 90f);

        OffsetPastATree(check, q);
        NoOffsetFade(check, q);
        OffsetNoFade(check, q);
        NoOffsetNoFade(check, q);
        ServerFlag(check, q);
        OldKeysLeave(check);
        NoCommand(check);
        NoListing(check);
        NoArmText(check, q);

        log.Always(check.Ok ? "SpringArmGoneApprovedExamples.SelfCheck: passed" : "SpringArmGoneApprovedExamples.SelfCheck: FAILED");
    }

    /// <summary>One run of the layer's rig and flag settle over <see cref="Frames"/> frames: the worst distance from
    /// <paramref name="full"/>, whether the flag word ever lost the bit or changed, and whether the world ever
    /// stood between the camera and the focus.</summary>
    private static (float Off, bool BitLost, bool WordMoved, bool Hidden) Run(Quaternion q, (Vector3, Vector3) box,
        float offX, float offY, bool fade, Vector3 full, ref int flags, ref bool weSet)
    {
        SpringArm.Rest();
        var world = new BoxWorld { Boxes = [box] };
        int start = flags;
        float off = 0f;
        bool lost = false, moved = false, hidden = true;
        for (int i = 0; i < Frames; i++)
        {
            Vector3 at = CameraService.Rig(ref world, Focus, Engine, q, offX, offY, Dt, out bool on);
            CameraService.SettleFlag(ref flags, on, fade, ref weSet);
            off = MathF.Max(off, Vector3.Distance(at, full));
            lost |= (flags & EngineLayout.CameraFlagDisableCollision) == 0;
            moved |= flags != start;
            hidden &= world.Cast(at, Focus, out _);
        }
        return (off, lost, moved, hidden);
    }

    // Given Alloy is on and walls.fade is on, with an offset of 1.5 in chase, or a height offset of 0.5 alone
    // When a tree passes between the camera and the character
    // Then the engine's pull-in stays off and the camera keeps its full distance
    private static void OffsetPastATree(Checker check, Quaternion q)
    {
        foreach ((float x, float y, Vector3 full, string what) in new[]
                 { (1.5f, 0f, new Vector3(1.5f, -5f, 1f), "an offset of 1.5"), (0f, 0.5f, new Vector3(0f, -5f, 1.5f), "a height offset of 0.5 alone") })
        {
            int flags = 0;
            bool weSet = false;
            var r = Run(q, Tree, x, y, fade: true, full, ref flags, ref weSet);
            check.Expect(!r.BitLost, $"S1 with {what} past a tree, the engine's pull-in stays off");
            check.Expect(r.Off < 1e-3f, $"S1 with {what} past a tree, the camera keeps its full distance (it came {r.Off:0.###} m off its rig)");
        }
    }

    // Given an offset of 0 and walls.fade on
    // When a wall passes between the camera and the character
    // Then the engine's pull-in stays off, as it does today
    private static void NoOffsetFade(Checker check, Quaternion q)
    {
        int flags = 0;
        bool weSet = false;
        var r = Run(q, Wall, 0f, 0f, fade: true, Engine, ref flags, ref weSet);
        check.Expect(!r.BitLost, "S2 with no offset and the fade on, the engine's pull-in stays off");
    }

    // Given walls.fade off, or Alloy off, with an offset of 1.5 in chase
    // When a wall passes between the camera and the character
    // Then the engine's pull-in stays off, the camera keeps its full distance, and the wall can hide the character
    private static void OffsetNoFade(Checker check, Quaternion q)
    {
        int flags = 0;
        bool weSet = false;
        var r = Run(q, Wall, 1.5f, 0f, fade: false, new Vector3(1.5f, -5f, 1f), ref flags, ref weSet);
        check.Expect(!r.BitLost, "S3 with an offset and no fade, the engine's pull-in stays off");
        check.Expect(r.Off < 1e-3f, $"S3 with an offset and no fade, the camera keeps its full distance (it came {r.Off:0.###} m off its rig)");
        check.Expect(r.Hidden, "S3 with an offset and no fade, the wall stands between the camera and the character");
    }

    // Given walls.fade off, or Alloy off, with an offset of 0
    // When a wall passes between the camera and the character
    // Then the engine's pull-in pulls the camera in, as the stock camera does
    private static void NoOffsetNoFade(Checker check, Quaternion q)
    {
        int flags = 0;
        bool weSet = false;
        var r = Run(q, Wall, 0f, 0f, fade: false, Engine, ref flags, ref weSet);
        check.Expect(!r.WordMoved && flags == 0, $"S4 with no offset and no fade, the flag stays clear and the engine's pull-in runs (word {flags})");
        check.Expect(r.Off < 1e-3f, "S4 with no offset, the camera sits where the engine's own solve put it");
    }

    // Given the server has set the camera's no-collision flag itself
    // When the offset goes from 1.5 to 0 and back, with walls.fade off
    // Then the flag stays set
    private static void ServerFlag(Checker check, Quaternion q)
    {
        const int OtherServerBits = 1 | 16;
        int server = EngineLayout.CameraFlagDisableCollision | OtherServerBits;
        int flags = server;
        bool weSet = false;
        bool moved = false;
        foreach (float x in new[] { 1.5f, 0f, 1.5f })
            moved |= Run(q, Wall, x, 0f, fade: false, Engine + new Vector3(x, 0f, 0f), ref flags, ref weSet).WordMoved;
        check.Expect(!moved && flags == server, $"S5 the server's no-collision flag stays set through 1.5, 0, 1.5 (word {flags}, want {server})");
    }

    // Given a cx.cfg that holds walls.margin, walls.armin, walls.armout and walls.armradius, one of them in mixed case
    // When the client reads it and writes it back
    // Then the four keys are gone and walls.radius is kept
    private static void OldKeysLeave(Checker check)
    {
        string?[] were = new string?[ArmKeys.Length];
        for (int i = 0; i < ArmKeys.Length; i++) were[i] = Settings.Raw(ArmKeys[i]);
        string? radiusWas = Settings.Raw(RadiusKey);

        Settings.Read($"walls.margin=1.5\nWalls.ArmIn=5\nwalls.armout=9\nwalls.armradius=1\n{RadiusKey}=2\n{Probe}=1\n");
        string written = KnobFile.Compose();
        bool[] held = Array.ConvertAll(ArmKeys, k => Settings.Raw(k) != null);
        string? radius = Settings.Raw(RadiusKey);

        foreach (string k in ArmKeys) Settings.Forget(k);
        Settings.Forget(RadiusKey);
        Settings.Forget(Probe);
        for (int i = 0; i < ArmKeys.Length; i++) if (were[i] != null) Settings.Read($"{ArmKeys[i]}={were[i]}\n");
        if (radiusWas != null) Settings.Read($"{RadiusKey}={radiusWas}\n");

        check.Expect(written.Contains($"\n{Probe}=1\n", StringComparison.Ordinal), "S6 the fed cx.cfg was read: its other key is written back");
        for (int i = 0; i < ArmKeys.Length; i++)
        {
            check.Expect(!held[i], $"S6 {ArmKeys[i]} is dropped on read");
            check.Expect(!written.Contains(ArmKeys[i], StringComparison.OrdinalIgnoreCase), $"S6 cx.cfg is written again without {ArmKeys[i]}");
        }
        check.Expect(radius == "2", $"S6 {RadiusKey} is kept (read back {radius ?? "nothing"})");
    }

    // Given chat commands are on
    // When the player types /walls margin 1
    // Then CX answers that walls has no knob called margin, and the same for armin, armout and armradius
    private static void NoCommand(Checker check)
    {
        check.Expect(KnobFile.Holding, "S7 runs inside the boot hold, so a set it makes and puts back arms no cx.cfg write");
        if (!KnobFile.Holding) return;
        bool was = General.Commands.V;
        General.Commands.Set(true);
        CommandRegistry.Global.TryDispatch($"/walls {NoSuchKnob} 1", out string? unknown);
        foreach (string key in ArmKeys)
        {
            string name = key["walls.".Length..];
            Knob? k = KnobGroup.Find(key);
            string? stored = k?.FormatStored();
            bool ran = CommandRegistry.Global.TryDispatch($"/walls {name} 1", out string? reply);
            if (k != null && stored != null) k.Parse(stored);
            check.Expect(k == null, $"S7 walls declares no knob {key}");
            string? want = unknown?.Replace(NoSuchKnob, name, StringComparison.Ordinal);
            check.Expect(ran && reply != null && reply == want,
                $"S7 /walls {name} 1 answers as /walls {NoSuchKnob} 1 does, not: {reply}");
        }
        General.Commands.Set(was);
    }

    // Given any cx.cfg
    // When the player lists /knobs or copies their settings for a bug report
    // Then none of the four keys appears, and walls.radius does
    private static void NoListing(Checker check)
    {
        bool was = General.Commands.V;
        General.Commands.Set(true);
        bool ran = CommandRegistry.Global.TryDispatch("/knobs", out string? listed);
        General.Commands.Set(was);
        string copied = KnobSurface.Report();
        check.Expect(ran && listed != null, "S8 /knobs answers");
        foreach ((string what, string text) in new[] { ("/knobs", listed ?? ""), ("the copied settings", copied) })
        {
            check.Expect(text.Contains(RadiusKey + "=", StringComparison.Ordinal), $"S8 {what} lists {RadiusKey}");
            foreach (string k in ArmKeys)
                check.Expect(!text.Contains(k, StringComparison.OrdinalIgnoreCase), $"S8 {what} has no {k}");
        }
    }

    /// <summary>The one field of the <c>[camera]</c> line the offset itself writes, on and off.</summary>
    private const string OffsetOnField = ", offset on; ", OffsetOffField = ", offset off; ";

    // Given an offset of 1.5
    // When the player reads /knobs and cx.log
    // Then no line mentions the arm, its margin or its pull-ins
    private static void NoArmText(Checker check, Quaternion q)
    {
        Knob<float> offset = CameraModes.Chase.OffsetX;
        float was = offset.V;
        offset.Set(1.5f);
        string noteOn = CameraService.Report(0, offsetOn: true), noteOff = CameraService.Report(0, offsetOn: false);
        check.Expect(noteOn == noteOff, $"S9 the camera's /knobs note reads the same with the offset on as off: on \"{noteOn}\", off \"{noteOff}\"");

        CameraService.PeriodLine(offsetOn: false);
        int flags = 0;
        bool weSet = false;
        Run(q, Wall, 1.5f, 0f, fade: false, new Vector3(1.5f, -5f, 1f), ref flags, ref weSet);
        string lineOn = CameraService.PeriodLine(offsetOn: true);
        flags = 0; weSet = false;
        Run(q, Wall, 0f, 0f, fade: false, Engine, ref flags, ref weSet);
        string lineOff = CameraService.PeriodLine(offsetOn: false);
        offset.Set(was);
        check.Expect(lineOn.Contains(OffsetOnField, StringComparison.Ordinal) && lineOff.Contains(OffsetOffField, StringComparison.Ordinal),
            $"S9 the [camera] line says whether the offset was on: on \"{lineOn}\", off \"{lineOff}\"");
        string onAsOff = lineOn.Replace(OffsetOnField, OffsetOffField, StringComparison.Ordinal);
        check.Expect(onAsOff == lineOff,
            $"S9 a period with the offset on into a wall logs the [camera] line a period with it off does, but for the offset: on \"{lineOn}\", off \"{lineOff}\"");
    }
}
