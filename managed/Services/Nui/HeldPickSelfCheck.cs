using System;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// The optimistic pick: which declared image a press shows, and when the server's answer or its silence ends the wait. Oracle: ids,
/// images and times typed into the checks, and a hand-driven clock (confirm window 1000 ms, the 500 ms no-NUI grace kept out of the way by
/// a draw counter that moves).
/// </summary>
internal static class HeldPickSelfCheck
{
    public static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "HeldPickSelfCheck");
        Picks(check);
        Pending(check);
        Look(check);
        ModelRefs(check);
        log.Info(check.Ok ? "HeldPickSelfCheck: passed" : "HeldPickSelfCheck: FAILED");
    }

    /// <summary>Oracle: hand-typed keys and hash; the names must differ per key, hold per key, and fit a resref.</summary>
    private static void ModelRefs(Checker check)
    {
        string a = NuiItemIcons.ModelRef(0x1234abcdu, "p1"), b = NuiItemIcons.ModelRef(0x1234abcdu, "p2");
        check.Expect(a != b, "two model views with one picture and size get different image names");
        check.Expect(a == NuiItemIcons.ModelRef(0x1234abcdu, "p1"), "one view keeps its image name");
        check.Expect(a.Length == 10 && a.StartsWith("cx"), "the name is cx and eight hex digits, a resref");
    }

    private static void Picks(Checker check)
    {
        HeldPicks.Reset();
        ServerPolicy.Apply("held.pick.cell*=iit_a\nheld.pick.cell_x*=iit_b\nheld.pick.slot_1=iit_c");
        check.Expect(HeldPicks.Count == 3 && HeldPicks.Find("slot_1") == "iit_c" && HeldPicks.Find("SLOT_1") == "iit_c", "an id key answers, without regard to case");
        check.Expect(HeldPicks.Find("cell_9") == "iit_a" && HeldPicks.Find("cell_x7") == "iit_b", "a prefix answers, the longest one first");
        check.Expect(HeldPicks.Find("slot_12") == null && HeldPicks.Find("") == null && HeldPicks.Find("other") == null, "an id key is not a prefix; nothing declared means no pick");
        ServerPolicy.Apply("held.pick.cell_x*=");
        check.Expect(HeldPicks.Find("cell_x7") == "iit_a", "releasing a prefix leaves the shorter one");
        ServerPolicy.Apply("held.pick.*=");
        check.Expect(HeldPicks.Count == 0 && HeldPicks.Find("cell_9") == null, "held.pick.*= releases every pick");
        HeldPicks.Reset();
    }

    private static void Pending(Checker check)
    {
        var m = new HeldImageModel();
        long draw = 0;
        check.Expect(m.Pick("iit_a", 1000, 1000, 5, draw) && m.Active && m.Pending && m.Name == "iit_a", "a pick shows its image at once, pending");
        check.Expect(!m.Update(1, true, "", ++draw, true, 1200, 5) && m.Pending && m.Active, "another key's change, the key unmoved, leaves the ghost");
        check.Expect(!m.Update(1, true, null, ++draw, true, 1999, 5) && m.Pending, "one ms before the window ends the ghost stays");
        check.Expect(m.Update(1, true, null, ++draw, true, 2000, 5) && !m.Pending && !m.Active, "with no answer in the window the ghost is dropped");

        m.Pick("iit_a", 3000, 1000, 5, draw);
        check.Expect(!m.Update(2, true, "iit_a", ++draw, true, 3100, 6) && !m.Pending && m.Active && m.Name == "iit_a", "the server's held.image confirms: solid, no change to what shows");
        check.Expect(!m.Update(2, true, null, ++draw, true, 9000, 6) && m.Active, "a confirmed hold is not dropped by the pick's window");

        m.Pick("iit_a", 4000, 1000, 6, draw);
        check.Expect(m.Update(3, true, "iit_b", ++draw, true, 4100, 7) && !m.Pending && m.Name == "iit_b", "the server may confirm a different image: it replaces the ghost");

        m.Pick("iit_a", 5000, 1000, 7, draw);
        check.Expect(m.Update(4, true, "", ++draw, true, 5100, 8) && !m.Pending && !m.Active, "an empty held.image clears the ghost");

        m.Pick("iit_a", 6000, 250, 8, draw);
        check.Expect(!m.Update(5, true, null, ++draw, true, 6249, 8) && m.Active && m.Update(5, true, null, ++draw, true, 6250, 8) && !m.Active, "held.confirm sets the window");

        m.Pick("iit_a", 7000, 1000, 8, draw);
        check.Expect(m.Update(6, false, "", ++draw, true, 7100, 8) && !m.Active && !m.Pending, "the knob off drops the ghost");
        check.Expect(!m.Pick("", 8000, 1000, 8, draw) && !m.Pending, "a pick of nothing holds nothing");

        m.Pick("iit_a", 10000, 1000, 9, draw);
        check.Expect(!m.Update(7, true, null, draw, true, 10600, 9) && m.Active && m.Pending, "a quiet NUI past the 500 ms grace leaves a pending pick to its window");
        check.Expect(m.Update(7, true, null, draw, true, 11000, 9) && !m.Active, "the window ends the quiet pick");
        check.Expect(!m.Update(7, true, null, draw, true, 11001, 9), "a pick is released once");
    }

    /// <summary>Oracle: the defaults and ranges written in the contract (0.9 and 0.55, 0-1, offsets of 256 px at most), as literals.</summary>
    private static void Look(Checker check)
    {
        var s = new HeldImageService.HeldState();
        ServerPolicy.Clear();
        HeldImageService.ReadLook(s, 1);
        check.Expect(s.Opacity == 0.9f && s.PickOpacity == 0.55f && s.Dx == 0f && s.Dy == 0f, "a server that sets nothing sees 0.9, 0.55 and a centred image");
        ServerPolicy.Apply("held.opacity=0.4\nheld.pickopacity=0.2\nheld.dx=-12\nheld.dy=8");
        HeldImageService.ReadLook(s, 2);
        check.Expect(s.Opacity == 0.4f && s.PickOpacity == 0.2f && s.Dx == -12f && s.Dy == 8f, "opacity and offset are read");
        ServerPolicy.Apply("held.opacity=7\nheld.pickopacity=-1\nheld.dx=900\nheld.dy=nope");
        HeldImageService.ReadLook(s, 3);
        check.Expect(s.Opacity == 1f && s.PickOpacity == 0f && s.Dx == 256f && s.Dy == 0f, "opacity clamps into 0-1, an offset into 256 px, and a word is the default");
        ServerPolicy.Clear();
        ServerPolicy.Apply("held.cursor=293");
        HeldImageService.ReadLook(s, 4);
        check.Expect(s.CursorId == 293 && PolicyRefusals.All().Length == 0, "293, the engine's last cursor id, is taken");
        ServerPolicy.Apply("held.cursor=294");
        HeldImageService.ReadLook(s, 5);
        check.Expect(s.CursorId == 25 && PolicyRefusals.All() is [{ Kind: RefusalKind.Held, Key: "held.cursor" }], "294 is past the engine's table: the default shows and /policy lists it");
        ServerPolicy.Apply("held.cursor=none");
        HeldImageService.ReadLook(s, 6);
        check.Expect(s.CursorId == -1 && PolicyRefusals.All().Length == 0, "none hides the cursor swap and a good value clears the listing");
        ServerPolicy.Clear();
        PolicyRefusals.ForgetPrefix("");
        var over = new HeldImageModel();
        over.Update(1, true, "abcdefghijklmnopq", 1, true, 1000);
        HeldImageService.List(over);
        check.Expect(over.Kind == HeldKind.Other && PolicyRefusals.All() is [{ Kind: RefusalKind.Held, Key: "held.image", Reason: "a resref is at most 16 characters" }],
            "a 17-character resref draws nothing and /policy says why");
        over.Update(2, true, "abcdefghijklmnop", 1, true, 1001);
        check.Expect(over.Kind == HeldKind.Resref, "sixteen characters is a resref");
        PolicyRefusals.ForgetPrefix("");
    }
}
