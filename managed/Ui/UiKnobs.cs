using CX.Framework;

namespace CX.Ui;

/// <summary>The <c>ui</c> group: the knobs every CX window shares. <see cref="Font"/> is the text size in design px
/// before GUI scale; <see cref="UiHostService"/> bakes the one atlas at <c>font × GUI scale</c>, sets
/// <see cref="Metrics.Scale"/> from it on a set and on the GUI-scale edge, and every window re-lays out from the
/// atlas epoch. <see cref="Typeface"/> picks the face <c>default</c> is bound to, from <see cref="FontRegistry.Names"/>.</summary>
internal static class UiKnobs
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<int> Font, GridPx;
    internal static readonly Knob<float> ConsoleScale;
    /// <summary>The face <c>default</c> binds to; with <see cref="Font"/> it is the base every element's own
    /// <see cref="TextStyle"/> rows follow until set.</summary>
    internal static readonly Knob<string> Typeface;
    /// <summary><see cref="HoverFocus"/> is read by <see cref="UiHost"/>; the two snapping toggles and
    /// <see cref="GridPx"/> are applied to <c>ChatWindow</c>'s snap statics by the quickbar tick.</summary>
    internal static readonly Knob<bool> HoverFocus, Snap, EdgeSnap, TextEdit, Streaming;
    /// <summary>Every CX tooltip's dwell and fade times, read live by <see cref="Tooltip"/>.</summary>
    internal static readonly Knob<float> TipDelay, TipFadeIn, TipFadeOut;

    static UiKnobs()
    {
        G = KnobGroup.Declare("ui", "Interface", "Windows & text", order: 200);
        int[] ladder = new int[TextRig.MaxFontPx - TextRig.MinFontPx + 1];
        for (int i = 0; i < ladder.Length; i++) ladder[i] = TextRig.MinFontPx + i;
        Font = G.Dropdown("font", "Text size", ladder, (int)Metrics.DesignFontPx, layout: true,
                          help: "Size of the text in every CX window and label. Each element may set its own in its Text rows; a chat window on its gear.",
                          changed: UiHostService.ApplyFont);
        Typeface = G.Dropdown("typeface", "Typeface", FontRegistry.Names, FontRegistry.DefaultName, layout: true,
                              help: "The font every CX window draws with: the built-in DejaVu Sans, a font.<name> from cx.cfg, or one installed on this machine. Each element may pick its own in its Text rows. Type in the list to search.",
                              changed: UiHostService.ApplyTypeface);
        ConsoleScale = G.Slider("consolescale", "Console text size", 1f, 3f, 0.25f, 1.5f, "x",
                                help: "Size of the ~ console and the other text the game prints on screen, over the game's own 14px grid. Needs the Alloy renderer.");
        HoverFocus = G.Toggle("hoverfocus", "Focus a window by hovering", false, Tier.Advanced,
                              help: "A CX window takes the keyboard when the pointer rests on it, without a click.");
        Snap = G.Toggle("snap", "Snap windows to a grid", false, Tier.Advanced,
                        help: "Dragged windows land on grid points.");
        GridPx = G.Dropdown("gridpx", "Grid size", new[] { 25, 50, 75, 100, 150, 200 }, 50, tier: Tier.Advanced,
                            help: "Spacing of the grid points, in screen px.");
        EdgeSnap = G.Toggle("edgesnap", "Snap to screen edges", true,
                            help: "Dragged windows stick to the screen edge and to each other.");
        TextEdit = G.Toggle("textedit", "Modern editing in game text boxes", true,
                            help: "The game's own text boxes get selection (mouse drag, double-click, Shift+arrows, Ctrl+A), Ctrl+C/X/V, and Ctrl+arrow/Backspace/Delete by word. Needs the Alloy renderer.");
        Streaming = G.Toggle("streaming", "Show streaming progress", true,
                             help: "A thin bar at the top of the screen while the area is still arriving after the load screen: textures, geometry and light charts. You can play meanwhile; it hides once everything has landed. Needs the Alloy renderer.");
        G.Section("Tooltips");
        TipDelay = G.Slider("tipdelay", "Tooltip delay", 0f, 2f, 0.05f, Tooltip.DelaySec, "s",
                            help: "How long the pointer rests on something before its tooltip shows, in every CX window and on the CX quickbar. Moving to a neighbour while one shows skips the wait.");
        TipFadeIn = G.Slider("tipfadein", "Tooltip fade in", 0f, 1f, 0.01f, Tooltip.FadeInSec, "s", Tier.Advanced, help: "How long a tooltip takes to fade in. 0 shows it at once.");
        TipFadeOut = G.Slider("tipfadeout", "Tooltip fade out", 0f, 1f, 0.01f, Tooltip.FadeOutSec, "s", Tier.Advanced, help: "How long a tooltip takes to fade out after the pointer leaves. 0 hides it at once.");
    }
}

/// <summary>Every UI group declares here, from every UI service's Initialize, so its rows exist whatever initializes
/// first and whether or not the feature arms. The panel's order is each group's <c>order:</c> (ADR 0030), not this
/// list; what this fixes is the declaration order, which decides <c>/knobs</c> and settles an <c>order</c> tie. A
/// class of its own with no static state: a static method on a group class would run that group's constructor first.</summary>
internal static class UiCategory
{
    internal static void Declare()
    {
        _ = CX.Services.Chat.Core.ChatKnobs.G; _ = CX.Services.Chat.Core.SnippetKnobs.G; _ = CX.Services.Dialog.DialogKnobs.G; _ = CX.Services.Nameplate.NameplateKnobs.G;
        _ = CX.Services.CombatText.CombatTextKnobs.G; _ = CX.Services.Inventory.InventoryKnobs.G; _ = CX.Services.Quickbar.QuickbarKnobs.G;
        _ = CX.Services.Radial.RadialKnobs.G; _ = CX.Services.Perf.FpsKnobs.G; _ = CX.Services.Nui.NuiKnobs.G;
        _ = UiKnobs.G;
    }

    /// <summary>Off-device, read-only (a set would arm the writer against the user's real file): the eleven groups
    /// declare in the fixed order above, the typeface row offers the registry's names, the font ladder is 6-32, and
    /// every re-keyed row binds under its current name with its stated default.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "UiCategory.SelfCheck");
        Declare();
        string[] order = { "chat", "snippets", "dialog", "nameplate", "combattext", "inventory", "quickbar", "radial", "fps", "nui", "ui" };
        int at = -1;
        var all = KnobGroup.All;
        foreach (string g in order)
        {
            int i = -1;
            for (int k = 0; k < all.Count; k++) if (all[k].Name == g) i = k;
            check.Expect(i > at, $"group {g} declares after the one before it");
            at = i;
        }
        check.Expect(!UiKnobs.Typeface.Disabled && UiKnobs.Typeface.Tier == Tier.User && UiKnobs.Typeface.Layout
          && UiKnobs.Typeface.Values.Length >= 1 && UiKnobs.Typeface.Values[0] == FontRegistry.DefaultName, "ui.typeface is a live layout row over the registry's names, default first");
        check.Expect(UiKnobs.Font.Values[0] == TextRig.MinFontPx && UiKnobs.Font.Values[^1] == TextRig.MaxFontPx && UiKnobs.Font.Layout, "ui.font is the 6-32 layout ladder");
        check.Expect(KnobGroup.Find("ui.hoverfocus") == UiKnobs.HoverFocus && !UiKnobs.HoverFocus.Default && UiKnobs.GridPx.Default == 50 && UiKnobs.EdgeSnap.Default,
          "hoverfocus / gridpx / edgesnap carry the cx-ui / cx-quickbar defaults");
        check.Expect(KnobGroup.Find("fps.enabled") is Knob<bool> fe && fe.Persisted && !fe.Default, "fps.enabled persists and ships off");
        check.Expect(KnobGroup.Find("nameplate.probe") is KnobButton { Tier: Tier.ChatOnly } && KnobGroup.Find("nameplate.mode") == null, "nameplate.probe is the Chat-only button; mode is gone");
        check.Expect(KnobGroup.Find("dialog.placed") is Knob<bool> { Tier: Tier.Hidden } && KnobGroup.Find("dialog.x") is Knob<float> { Tier: Tier.Hidden }, "the dialog rect is Hidden state");
        check.Expect(KnobGroup.Find("quickbar.compass") is Knob<bool> { Default: true } && KnobGroup.Find("quickbar.align") is Knob<int> { Default: 1 }, "quickbar.compass shows by default; align defaults to centre");
        check.Expect(KnobGroup.Find("combattext.enabled") is Knob<bool> { Default: true } && KnobGroup.Find("combattext.bannersize") is Knob<float> { Default: 1.3f }, "combattext rows bind");
        var live = CommandRegistry.Global;
        foreach (string dead in new[] { "/np", "/ct", "/qbar", "/compass" }) check.Expect(!live.Knows(dead), $"{dead} is gone");
        log.Info(check.Ok ? "UiCategory.SelfCheck: passed" : "UiCategory.SelfCheck: FAILED");
    }
}
