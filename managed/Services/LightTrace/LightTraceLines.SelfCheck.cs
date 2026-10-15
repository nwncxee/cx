using System.Numerics;
using CX.Framework;

namespace CX.Services.LightTrace;

internal static partial class LightTraceLines
{
    /// <summary>Off-device: every trace line and the sampler's decision. Oracle: lines written out by hand
    /// from the trace's documented shape, and slot / state / minor names read off the engine's own branch
    /// tables (the four colour slots against the area's night and day palette offsets, the time-of-day
    /// states against the <c>SetAs*</c> calls each one makes, the minors against the message switch).</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "LightTraceLines.SelfCheck");

        // The slot numbers: 0/1 write the area's NIGHT ambient/diffuse pair, 2/3 its DAY pair.
        check.Expect(SlotName(0) == "moon-ambient", $"slot 0 is the moon ambient ({SlotName(0)})");
        check.Expect(SlotName(1) == "moon-diffuse", $"slot 1 is the moon diffuse ({SlotName(1)})");
        check.Expect(SlotName(2) == "sun-ambient", $"slot 2 is the sun ambient ({SlotName(2)})");
        check.Expect(SlotName(3) == "sun-diffuse", $"slot 3 is the sun diffuse ({SlotName(3)})");
        check.Expect(SlotName(7) == "slot7", $"a slot the engine ignores still names itself ({SlotName(7)})");
        check.Expect(DirSlotName(0) == "moon" && DirSlotName(1) == "sun",
                     $"direction slot 0 is the moon's and 1 the sun's ({DirSlotName(0)}/{DirSlotName(1)})");

        // The time-of-day states, by which SetAs* pair each one runs.
        check.Expect(StateName(1) == "day" && StateName(2) == "night" && StateName(3) == "dawn"
                     && StateName(4) == "dusk", "the four time-of-day states are day, night, dawn, dusk");
        check.Expect(StateName(9) == "state9", $"an unknown state still names itself ({StateName(9)})");

        // The area minors that can move the light, and one that cannot.
        check.Expect(MinorName(0x01) == "area-entry", $"minor 0x01 is the area-entry packet ({MinorName(0x01)})");
        check.Expect(MinorName(0x05) == "static-lights", $"minor 0x05 is the static-light update ({MinorName(0x05)})");
        check.Expect(MinorName(0x06) == "change-day-night", $"minor 0x06 is Area_ChangeDayNight ({MinorName(0x06)})");
        check.Expect(MinorName(0x08) == "fog-colour" && MinorName(0x09) == "fog-amount",
                     "minors 0x08 and 0x09 are the two fog packets");
        check.Expect(MinorName(0x0e) == "light-colour" && MinorName(0x0f) == "light-colour"
                     && MinorName(0x11) == "light-colour" && MinorName(0x12) == "light-colour",
                     "the four light-colour minors are 0x0e, 0x0f, 0x11 and 0x12");
        check.Expect(MinorName(0x10) == "light-direction" && MinorName(0x13) == "light-direction",
                     "the two light-direction minors are 0x10 and 0x13");
        check.Expect(MinorName(0x14) == null, "a minor that cannot move the light is not traced");

        // The lines. Each expected string is written out by hand, so a format drift is a failure.
        string colour = Colour(1, new Vector3(0.18f, 0.196f, 0.251f), 2f, 0x8000002au);
        check.Expect(colour == "[lighttrace] packet colour slot=moon-diffuse rgb=(0.180,0.196,0.251) fade=2.00s area=0x8000002a",
                     $"the colour-packet line ({colour})");
        string dir = Direction(1, new Vector3(0f, -0.70711f, -0.70711f), 0f, 0x2au);
        check.Expect(dir == "[lighttrace] packet direction slot=sun dir=(0.000,-0.707,-0.707) fade=0.00s area=0x0000002a",
                     $"the direction-packet line ({dir})");
        string fog = FogColour(new Vector3(0.5f, 0.5f, 0.6f), new Vector3(0.1f, 0.1f, 0.2f), 8f, 0x2au);
        check.Expect(fog == "[lighttrace] packet fog-colour sun=(0.500,0.500,0.600) moon=(0.100,0.100,0.200) fade=8.00s area=0x0000002a",
                     $"the fog-colour line ({fog})");
        string amount = FogAmount(25, 0x2au);
        check.Expect(amount == "[lighttrace] packet fog-amount n=25 area=0x0000002a", $"the fog-amount line ({amount})");
        string tod = TimeOfDay(4, 12000, 1);
        check.Expect(tod == "[lighttrace] time-of-day state=4 dusk elapsed=12000ms force=1", $"the time-of-day line ({tod})");
        string minor = Minor(0x06, "change-day-night");
        check.Expect(minor == "[lighttrace] packet minor=0x06 change-day-night", $"the arriving-packet line ({minor})");
        string setAs = SetAs("dusk", 0x2au, 1, 0);
        check.Expect(setAs == "[lighttrace] set-as dusk area=0x0000002a cycle=1 night=0", $"the set-as line ({setAs})");
        string start = TransitionStart("dusk", 1200f);
        check.Expect(start == "[lighttrace] transition dusk start len=1200.0s", $"the transition-start line ({start})");
        string end = TransitionEnd("dusk", 1201.3f);
        check.Expect(end == "[lighttrace] transition dusk end after=1201.3s", $"the transition-end line ({end})");

        var t = new LightTriple(new Vector3(0.18f, 0.196f, 0.251f), new Vector3(1f, 0.95f, 0.85f),
                                new Vector3(0f, 0f, 1f));
        string applied = Applied(in t);
        check.Expect(applied == "[lighttrace] applied amb=(0.180,0.196,0.251) dif=(1.000,0.950,0.850) dir=(0.000,0.000,1.000)",
                     $"the applied-to-gidy_sun line ({applied})");
        string alloy = Alloy(in t);
        check.Expect(alloy == "[lighttrace] alloy amb=(0.180,0.196,0.251) dif=(1.000,0.950,0.850) sun=(0.000,0.000,1.000)",
                     $"the what-Alloy-rendered line ({alloy})");

        // The sampler: at most one line a second, and only when the values moved.
        var same = t;
        var moved = new LightTriple(new Vector3(0.18f, 0.196f, 0.251f), new Vector3(1f, 0.95f, 0.85f),
                                    new Vector3(0f, 0.1f, 0.995f));
        check.Expect(Due(0L, -1L, in same, in same), "the first sample of a session always goes out");
        check.Expect(!Due(500L, 0L, in same, in moved), "a change inside the second waits");
        check.Expect(!Due(1000L, 0L, in same, in same), "a second with nothing moving says nothing");
        check.Expect(Due(1000L, 0L, in same, in moved), "a second later, a change goes out");
        check.Expect(Due(90000L, 0L, in same, in moved), "...and so does a change long after");

        // What counts as a change: colour to within half an 8-bit step, direction much finer.
        var nudged = new LightTriple(new Vector3(0.18f + 0.0005f, 0.196f, 0.251f), t.Diffuse, t.Dir);
        check.Expect(!nudged.Differs(in t), "a colour nudge under half an 8-bit step is the same colour");
        var stepped = new LightTriple(new Vector3(0.18f + 1f / 255f, 0.196f, 0.251f), t.Diffuse, t.Dir);
        check.Expect(stepped.Differs(in t), "a whole 8-bit colour step is a change");
        var turned = new LightTriple(t.Ambient, t.Diffuse, new Vector3(0f, 0.002f, 1f));
        check.Expect(turned.Differs(in t), "a sun step of a quarter of a degree is a change");
        var still = new LightTriple(t.Ambient, t.Diffuse, new Vector3(0f, 0.0001f, 1f));
        check.Expect(!still.Differs(in t), "...and float noise in the direction is not");

        log.Info(check.Ok ? "LightTraceLines.SelfCheck: passed"
                          : "LightTraceLines.SelfCheck: FAILED -- the lighting trace would read wrongly");
        return check.Ok;
    }
}
