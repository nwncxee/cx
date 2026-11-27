using System;
using CX.Framework;
using CX.Services.Camera;
using CX.Services.Interact;

namespace CX.Ui;

/// <summary>Two detail rows the panel shows only while their switch is on: the Interact feedback sound under Play a
/// sound, and each camera mode's Cursor key under its Action camera; both in the main list, and each submenu still
/// within the cap. Oracle: the rows' knobs, the switch states and the cap of 14 written here.</summary>
public sealed partial class KnobPanel
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool ShownWhileOn(ExtenderLog log)
    {
        var check = new Checker(log, "ShownWhileOn.SelfCheck");
        var body = new GlyphAtlas { LineHeight = 24, Ascent = 19, SpaceAdvance = 6f };
        var small = new GlyphAtlas { LineHeight = 19, Ascent = 15, SpaceAdvance = 5f };
        for (int cp = 0x20; cp < 0x180; cp++) { body.SetTestGlyph(cp, cp == ' ' ? 6f : 11f); small.SetTestGlyph(cp, cp == ' ' ? 5f : 8f); }

        KnobPanel Laid(KnobGroup g, bool advOpen)
        {
            var p = new KnobPanel { Category = Array.IndexOf(KnobCategories, "Controls") };
            p.OpenAt(1280f, 720f, FacePx, body, small);
            p._subs = new[] { new SubMenu(g) }; p.Sub = 0; p.AdvOpen = advOpen; p.LayoutRows();
            return p;
        }

        Knob[] touched = { InteractKnobs.FeedbackSound, CameraModes.Chase.Action, CameraModes.TopDown.Action, CameraModes.Driving.Action };
        string[] were = Array.ConvertAll(touched, k => k.FormatStored());
        try
        {
            {
                InteractKnobs.FeedbackSound.Set(false);
                Row? sound = Find(Laid(InteractKnobs.G, advOpen: true), InteractKnobs.Sound);
                check.Expect(sound == null, $"with Play a sound off, the Sound row is in neither list (Advanced open): {(sound == null ? "absent" : sound.Adv ? "Advanced" : "main")}");
            }
            {
                InteractKnobs.FeedbackSound.Set(true);
                Row? sound = Find(Laid(InteractKnobs.G, advOpen: false), InteractKnobs.Sound);
                check.Expect(sound != null && !sound.Adv, $"with Play a sound on, the Sound row is in the main list (Advanced shut): {(sound == null ? "absent" : sound.Adv ? "Advanced" : "main")}");
            }
            foreach (CameraModeKnobs m in CameraModes.ByMode)
            {
                m.Action.Set(false);
                Row? off = Find(Laid(m.G, advOpen: true), m.CursorKey);
                check.Expect(off == null, $"{m.G.Submenu}: with the action camera off, the Cursor key row is in neither list (Advanced open): {(off == null ? "absent" : off.Adv ? "Advanced" : "main")}");
                m.Action.Set(true);
                Row? on = Find(Laid(m.G, advOpen: false), m.CursorKey);
                check.Expect(on != null && !on.Adv, $"{m.G.Submenu}: with the action camera on, the Cursor key row is in the main list (Advanced shut): {(on == null ? "absent" : on.Adv ? "Advanced" : "main")}");
            }
            foreach (KnobGroup g in new[] { InteractKnobs.G, CameraModes.Chase.G, CameraModes.TopDown.G, CameraModes.Driving.G })
            {
                int rows = 0;
                foreach (KnobGroup sg in KnobGroup.All)
                    if (sg.Category == g.Category && sg.Submenu == g.Submenu)
                        foreach (Knob k in sg.Knobs)
                            if (k.Tier == Tier.User && k.Kind != KnobKind.Link && k.ShowIf == null) rows++;
                check.Expect(rows <= 14, $"{g.Category} » {g.Submenu} keeps its always-shown User rows within the cap: {rows} of 14");
            }
        }
        finally
        {
            for (int i = 0; i < touched.Length; i++) if (touched[i].FormatStored() != were[i]) touched[i].Parse(were[i]);
            KnobPanelService.Close();
        }
        log.Info(check.Ok ? "ShownWhileOn.SelfCheck: passed" : "ShownWhileOn.SelfCheck: FAILED");
        return check.Ok;
    }
}
