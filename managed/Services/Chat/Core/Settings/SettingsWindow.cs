using System.Collections.Generic;
using CX.Framework;
using CX.Ui;
using Row = CX.Ui.KnobPanel.Row;

namespace CX.Services.Chat.Core;

/// <summary>
/// The chat settings window: the chat-side consumer of the shared <see cref="KnobPanel"/>. It owns the content
/// only -- the category list, which rows each category holds, and what a press does to the
/// <see cref="ChatWindow"/>; the panel owns placement, drag, layout, hit-test-to-row and the draw. A pick on a
/// shared knob or a font is raised to the shell: this pure core never sets a live knob nor touches the text rig.
/// </summary>
public sealed partial class SettingsWindow
{
    public const string WindowCategory = "Window settings", AddTabCategory = "Add new tab...";
    /// <summary>The default typeface / text size entry: the window follows <c>ui.typeface</c> / <c>ui.font</c>.</summary>
    public const string FollowUi = "UI default";

    /// <summary>The shared panel this window's rows are drawn and hit-tested by (the renderer calls
    /// <see cref="KnobPanel.Emit"/> on it): two columns, modal, its rows from <see cref="Layout"/>.</summary>
    public readonly KnobPanel Panel;

    // The model the panel's callbacks edit. Stashed at each public entry point because the shell passes the live
    // window per call; a null window means "no model" and the callbacks no-op (the shell's focused window can be
    // gone by the time a key commits an edit).
    private ChatWindow? _win;

    /// <summary>Tab index per category entry: -1 for <see cref="WindowCategory"/> and <see cref="AddTabCategory"/>.</summary>
    private int[] _catTab = System.Array.Empty<int>();

    // Dropdown choice tables, in enum order (the picked index IS the enum value). Static: a row points at
    // them, never copies.
    private static readonly string[] TellOpenNames = { "Off", "New tab", "New window" };
    private static readonly string[] AlignNames = { "Left", "Right", "Center", "Justify" };
    private static readonly string[] FormatNames = { "HH:mm", "HH:mm:ss", "h:mm tt" };
    private static readonly string[] SourceNames = { "Local", "Game" };
    private static readonly string[] BgModeNames = { "Solid", "Linear", "Radial", "Conic" };
    /// <summary>The text-size ladder: <see cref="FollowUi"/>, then every point from <see cref="TextRig.MinFontPx"/> to
    /// <see cref="TextRig.MaxFontPx"/> (a layout-affecting knob is a dropdown, never a slider -- ADR 0025).</summary>
    public static readonly string[] FontSizes = BuildFontSizes();
    private static string[] BuildFontSizes()
    {
        int lo = TextRig.MinFontPx, hi = TextRig.MaxFontPx;
        var a = new string[hi - lo + 2];
        a[0] = FollowUi;
        for (int i = 1; i < a.Length; i++) a[i] = (lo + i - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return a;
    }
    /// <summary>The typeface choices: <see cref="FollowUi"/>, then the registry's names past <c>default</c>. Set by the
    /// shell once the registry loaded; the self-check supplies its own.</summary>
    public static string[] Typefaces = { FollowUi };

    public SettingsWindow()
    {
        Panel = new KnobPanel
        {
            Title = "Chat window",
            HeaderAction = "Copy this window",
            HeaderActionPressed = () => CopyWindowRequested = true,
            WFrac = 0.52f, HFrac = 0.66f, CatFrac = 0.30f, SubFrac = 0f,
            Modal = true,
            Source = () => { if (_win != null) Layout(_win); },
            CategoryPicked = OnCategoryPicked,
            RowPressed = OnRowPressed,
            ApplyValue = OnApplyValue,
            CommitLabel = OnCommitLabel,
        };
    }

    public float X => Panel.X;
    public float Y => Panel.Y;
    public float W => Panel.W;
    public float H => Panel.H;
    public bool Open => Panel.Open;
    public List<Row> Rows => Panel.Rows;

    /// <summary>Number of chat windows (snapshot), used to show "Copy from Window N" rows. Set by the shell
    /// before <see cref="Layout"/> (the panel is modal, so window count can't change while it's open).</summary>
    public int WindowCount;
    /// <summary>Index of the window this panel is editing (0 = Main). Set by the shell on open.</summary>
    public int WindowIndex;

    /// <summary>Set by the header's "Copy this window": the shell opens a new window carrying this one's settings.</summary>
    public bool CopyWindowRequested;

    /// <summary>Set by the second press of "Close this window" (the first only arms it); the shell closes the window.</summary>
    public bool CloseWindowRequested;
    /// <summary>True between the first press of "Close this window" and the confirming second; any other press disarms.</summary>
    public bool CloseArmed;

    /// <summary>Index of the source window for a pending "Copy from" action, or -1. The shell reads + clears
    /// this after MouseDown to perform the actual copy (it owns the window list, which Layout doesn't).</summary>
    public int CopyRequested = -1;

    /// <summary>A "Tell auto-open" pick (the <see cref="TellAutoOpen"/> value), or -1. The shell reads + clears it
    /// after MouseDown and sets <c>chat.tellopen</c>; the core stays pure and the SelfCheck sets nothing live.</summary>
    public int TellOpenPicked = -1;

    /// <summary>Set true when the window's typeface or text size was picked. The shell reads + clears it to bake the
    /// face (cold, on the input thread) and relay the window.</summary>
    public bool FontChanged;

    /// <summary>Set true when the user just turned TellsHere on. The shell reads + clears this to enforce
    /// exclusivity (clear TellsHere on all other windows — the panel only sees one window).</summary>
    public bool TellsHereChanged;

    /// <summary>The tab the chosen category edits, or -1 for the window / add entries.</summary>
    public int CategoryTab => (uint)Panel.Category < (uint)_catTab.Length ? _catTab[Panel.Category] : -1;

    /// <summary>Rebuild the category list and the chosen category's rows from the live window + config,
    /// publishing a fresh list. Input-thread only (open + each settings interaction); the ingest-thread Rebuild
    /// never calls it, so the list it builds is single-writer and only ever read cross-thread as a whole snapshot.</summary>
    public void Layout(ChatWindow win)
    {
        _win = win;
        var cats = new List<string> { WindowCategory };
        var catTab = new List<int> { -1 };
        for (int ti = 0; ti < win.Tabs.Count; ti++)
        {
            if (win.Tabs[ti].Ephemeral) continue;
            cats.Add(win.Tabs[ti].Label); catTab.Add(ti);
        }
        cats.Add(AddTabCategory); catTab.Add(-1);
        Panel.Categories = cats.ToArray(); _catTab = catTab.ToArray();
        if ((uint)Panel.Category >= (uint)cats.Count - 1) Panel.Category = 0;   // a removed tab, or the add entry: back to the window

        var rows = new List<Row>();
        int tab = CategoryTab;
        if (tab >= 0) LayoutTab(rows, win, tab); else LayoutWindow(rows, win);
        Panel.Publish(rows);
    }

    private void LayoutWindow(List<Row> rows, ChatWindow win)
    {
        AddSection(rows, "Window");
        AddToggle(rows, SetAct.InputTop, "Input on top", win.InputOnTop, 0,
                  "The input line pops out above the window instead of below it.");
        AddToggle(rows, SetAct.AutoHideHdr, "Auto-hide header", win.AutoHideHeader, 0,
                  "The tab bar hides until the pointer is over the window.");
        AddToggle(rows, SetAct.AutoHideFrame, "Auto-hide outline", win.AutoHideFrame, 0,
                  "The frame and its shadow hide until the pointer is over the window.");
        AddToggle(rows, SetAct.LockWin, "Lock window", win.Locked, 0,
                  "No moving, resizing or tab dragging on this window.");
        AddToggle(rows, SetAct.TellsHere, "Tells here", win.TellsHere, 0,
                  "Tells land in this window. Only one window can hold this.");
        // A pending pick shows at once: the shell sets the knob only after the press returns.
        AddDropdown(rows, SetAct.CycleTellOpen, "Tell auto-open", TellOpenNames, TellOpenPicked >= 0 ? TellOpenPicked : ChatKnobs.TellOpen.V,
                    "Where a tell from someone new lands: nowhere special, a new tab, or its own window.");

        AddSection(rows, "Lines");
        foreach (var (bit, label) in ChatWindow.Options)
            AddToggle(rows, SetAct.ToggleBit, label, (win.Toggles & bit) != 0, bit);
        AddDropdown(rows, SetAct.CycleAlign, "Alignment", AlignNames, (int)win.Align, "");
        AddDropdown(rows, SetAct.CycleFmt, "Time format", FormatNames, (int)win.TimeFormat, "");
        AddDropdown(rows, SetAct.CycleSrc, "Time source", SourceNames, (int)win.TimeSource,
                    "Local: your clock. Game: the in-game world clock.");

        AddSection(rows, "Font");
        AddDropdown(rows, SetAct.Typeface, "Typeface", Typefaces, TypefaceIndex(win.Typeface),
                    "The font this window draws its text with; UI default follows the Typeface setting under UI.");
        AddDropdown(rows, SetAct.SliderFont, "Text size", FontSizes, FontIndex(win.FontPx),
                    "Text size in this window; UI default follows the Text size setting under UI.");
        AddDropdown(rows, SetAct.OutlineOn, "Outline", TextStyle.OutlineNames, win.Outline,
                    "A dark halo around the messages for readability over bright scenes: thin or thick.");

        AddSection(rows, "Animation");
        AddToggle(rows, SetAct.AnimOn, "Animations", win.AnimEnabled, 0,
                  "Lines fade in and out; the window eases when scrolled.");
        if (win.AnimEnabled)
        {
            AddSlider(rows, SetAct.SliderHold, "Hold (s)", win.HoldSec, 0f, 300f);
            AddSlider(rows, SetAct.SliderFade, "Fade (s)", win.FadeSec, 0f, 30f);
            AddSlider(rows, SetAct.SliderTau, "Ease (ms)", win.TauMs, 10f, 500f);
        }

        // The colours themselves are the shared look (ui.look / ui.palette under UI); this window only shapes
        // how the background is filled with them.
        AddSection(rows, "Background");
        AddDropdown(rows, SetAct.CycleBgMode, "Bg style", BgModeNames, (int)win.BgMode,
                    "Solid, or a gradient across the look's two background colours: Linear along a direction, Radial from the centre, Conic around it. The native look draws the game's own window art instead.");
        if (win.BgMode != BgMode.Solid)
        {
            if (win.BgMode != BgMode.Radial)
                AddSlider(rows, SetAct.SliderBgAngle, "Direction (°)", win.BgAngle, 0f, 360f,
                          "0 runs left to right, 90 top to bottom.");
            AddSlider(rows, SetAct.SliderBgStart, "Start (%)", win.BgStart * 100f, 0f, 100f,
                      "The first colour holds up to here.");
            AddSlider(rows, SetAct.SliderBgEnd, "End (%)", win.BgEnd * 100f, 0f, 100f,
                      "The second holds from here; at or before Start makes a hard edge.");
        }

        // Copy from another window (hidden when only one window exists).
        if (WindowCount > 1) AddSection(rows, "Other windows");
        for (int wi = 0; wi < WindowCount; wi++)
        {
            if (wi == WindowIndex) continue;
            rows.Add(new Row { Ctl = KnobKind.Button, Act = SetAct.CopyFrom, Text = $"Copy from Window {wi + 1}",
                               Key = wi.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        // Closing lives here, not on the tab bar; Main is undeletable, so it gets no row.
        if (win.Closeable)
        {
            AddSection(rows, "Close");
            rows.Add(new Row { Ctl = KnobKind.Button, Act = SetAct.CloseWindow,
                               Text = CloseArmed ? "Click again to confirm" : "Close this window",
                               HelpText = "Closes this chat window. Its tabs are gone; press twice to confirm." });
        }
    }

    /// <summary>The tab editor: label, the channel filter, remove. The tell pin is the tab bar's own affordance, not a setting.</summary>
    private static void LayoutTab(List<Row> rows, ChatWindow win, int ti)
    {
        Tab tab = win.Tabs[ti];
        string tk = ti.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AddSection(rows, "Tab");
        rows.Add(new Row { Ctl = KnobKind.Text, Act = SetAct.TabLabel, Text = "Label", Value = tab.Label, Key = tk,
                           HelpText = "The name on the tab." });
        AddSection(rows, "Channels");
        foreach (var (bit, chLabel) in FilterChannel.Editable())
            rows.Add(new Row { Ctl = KnobKind.Toggle, Act = SetAct.TabChannel, Text = chLabel,
                               On = (tab.ChannelMask & bit) != 0, Bit = bit, Key = tk });
        if (win.Tabs.Count > 1)
            rows.Add(new Row { Ctl = KnobKind.Button, Act = SetAct.TabRemove, Text = "Remove tab", Key = tk });
    }

    private static void AddSection(List<Row> rows, string label)
        => rows.Add(new Row { Kind = KnobPanel.RowKind.Section, Act = SetAct.None, Text = label.ToUpperInvariant() });

    private static void AddToggle(List<Row> rows, SetAct act, string label, bool on, uint bit, string help = "")
    { rows.Add(new Row { Ctl = KnobKind.Toggle, Act = act, Text = label, On = on, Bit = bit, HelpText = help }); }

    private static void AddDropdown(List<Row> rows, SetAct act, string label, string[] choices, int selected, string help)
    { rows.Add(new Row { Ctl = KnobKind.Dropdown, Act = act, Text = label, Choices = choices, Selected = selected, HelpText = help }); }

    /// <summary>Index of a window font px in <see cref="FontSizes"/>: 0 follows the UI, else the point, clamped to the ladder.</summary>
    private static int FontIndex(int px)
    {
        if (px <= 0) return 0;
        int i = px - TextRig.MinFontPx + 1;
        return i < 1 ? 1 : i >= FontSizes.Length ? FontSizes.Length - 1 : i;
    }

    /// <summary>Index of a window typeface in <see cref="Typefaces"/>: 0 (follow the UI) when empty or unknown.</summary>
    private static int TypefaceIndex(string name)
    {
        if (name.Length == 0) return 0;
        for (int i = 1; i < Typefaces.Length; i++)
            if (string.Equals(Typefaces[i], name, System.StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    private static void AddSlider(List<Row> rows, SetAct act, string label, float value, float min, float max, string help = "")
    {
        float norm = max > min ? (value - min) / (max - min) : 0f;
        rows.Add(new Row { Ctl = KnobKind.Slider, Act = act, Text = label, Norm = Clamp01(norm), Min = min, Max = max, HelpText = help });
    }

    /// <summary>Open the panel centred in a <paramref name="vpW"/>×<paramref name="vpH"/> viewport on the faces the
    /// shell baked at <paramref name="em"/> (null faces lay out on estimates and draw nothing). Opens on the
    /// window category. <paramref name="winIndex"/> and <paramref name="winCount"/> drive the "Copy from" rows.</summary>
    public void OpenAt(ChatWindow win, float vpW, float vpH, float em = KnobPanel.FacePx,
                       GlyphAtlas? body = null, GlyphAtlas? small = null, int winIndex = 0, int winCount = 1)
    {
        _win = win;
        WindowIndex = winIndex; WindowCount = winCount;
        Panel.Category = 0; Panel.Cats.Reset(); Panel.List.Reset();
        Panel.OpenAt(vpW, vpH, em, body, small);   // lays out through Source
    }

    /// <summary>Swap in fresh faces (the sheet re-baked while open: same px, so the layout stands; the next
    /// interaction re-lays out). Safe off the input thread -- two field writes.</summary>
    public void SetFaces(GlyphAtlas? body, GlyphAtlas? small) { Panel.Body = body; Panel.Small = small; }

    internal bool Contains(float mx, float my) => Panel.Contains(mx, my);

    /// <summary>Pointer press while open. Always consumed (the panel is modal). Mutates the window/config per
    /// the hit row; a slider hit starts a drag (see <see cref="MouseMove"/>). Close button or a click-off closes.</summary>
    public bool MouseDown(float mx, float my, ChatWindow win)
    {
        _win = win;
        return Panel.MouseDown(mx, my);
    }

    /// <summary>A category press: a tab or the window entry selects it; <see cref="AddTabCategory"/> adds an
    /// All-channel tab and selects its new entry.</summary>
    private void OnCategoryPicked(int c)
    {
        ChatWindow? win = _win;
        if (win != null && c == Panel.Categories.Length - 1)
        {
            win.AddTab(new Tab("All", 0xffffffff));
            Panel.Category = c;   // the new tab's entry takes this slot; the add entry moves one down
            return;
        }
        Panel.Category = c;
    }

    /// <summary>A press on a non-slider row: mutate the window/config. Slider and text rows never reach here --
    /// the panel owns the track drag, the value box and the label edit. A dropdown row arrives after a pick,
    /// with <see cref="Row.Selected"/> already the chosen index (= the enum value).</summary>
    private void OnRowPressed(Row row)
    {
        ChatWindow? win = _win;
        if (win == null) return;
        bool armed = CloseArmed;
        CloseArmed = false;
        switch (row.Act)
        {
            case SetAct.CloseWindow:
                if (armed) CloseWindowRequested = true; else CloseArmed = true;
                break;
            case SetAct.ToggleBit: win.Toggles ^= row.Bit; break;
            case SetAct.InputTop: win.InputOnTop = !win.InputOnTop; break;
            case SetAct.AutoHideHdr: win.AutoHideHeader = !win.AutoHideHeader; break;
            case SetAct.AutoHideFrame: win.AutoHideFrame = !win.AutoHideFrame; break;
            case SetAct.LockWin: win.Locked = !win.Locked; break;
            case SetAct.TellsHere: win.TellsHere = !win.TellsHere; TellsHereChanged = win.TellsHere; break;
            case SetAct.CycleTellOpen: TellOpenPicked = row.Selected; break;
            case SetAct.AnimOn: win.AnimEnabled = !win.AnimEnabled; break;
            case SetAct.OutlineOn: win.Outline = row.Selected; break;
            case SetAct.CycleFmt: win.TimeFormat = (TsFormat)row.Selected; break;
            case SetAct.CycleSrc: win.TimeSource = (TsSource)row.Selected; break;
            case SetAct.CycleAlign: win.Align = (TextAlign)row.Selected; break;
            case SetAct.CycleBgMode: win.BgMode = (BgMode)row.Selected; break;
            case SetAct.SliderFont: win.FontPx = row.Selected <= 0 ? 0 : TextRig.MinFontPx + row.Selected - 1; FontChanged = true; break;
            case SetAct.Typeface: win.Typeface = row.Selected <= 0 || row.Selected >= Typefaces.Length ? "" : Typefaces[row.Selected]; FontChanged = true; break;
            case SetAct.TabRemove:
            {
                int ti = ParseIndex(row.Key);
                if (win.RemoveTab(ti)) Panel.Category = 0;
                break;
            }
            case SetAct.TabChannel:
            {
                int ti = ParseIndex(row.Key);
                if ((uint)ti < (uint)win.Tabs.Count)
                {
                    win.Tabs[ti].ChannelMask ^= row.Bit;
                    if (ti == win.ActiveTab) win.ClampSendChannel();
                }
                break;
            }
            case SetAct.CopyFrom:
                CopyRequested = ParseIndex(row.Key);
                break;
        }
    }

    /// <summary>Pointer motion while a slider or the header is grabbed, or a hover moved.</summary>
    public bool MouseMove(float mx, float my, ChatWindow win)
    {
        _win = win;
        return Panel.MouseMove(mx, my);
    }

    /// <summary>Write a raw value onto the window for a slider/channel row and mirror it back onto the row's
    /// draw state so the knob/value box track the pointer.</summary>
    private void OnApplyValue(Row r, float v)
    {
        ChatWindow? win = _win;
        if (win == null) return;
        float cl = v < r.Min ? r.Min : v > r.Max ? r.Max : v;
        r.Norm = r.Max > r.Min ? (cl - r.Min) / (r.Max - r.Min) : 0f;
        switch (r.Act)
        {
            case SetAct.SliderHold: win.HoldSec = cl; break;
            case SetAct.SliderFade: win.FadeSec = cl; break;
            case SetAct.SliderTau: win.TauMs = cl; break;
            case SetAct.SliderBgAngle: win.BgAngle = cl; break;
            case SetAct.SliderBgStart: win.BgStart = cl / 100f; break;
            case SetAct.SliderBgEnd: win.BgEnd = cl / 100f; break;
        }
    }

    // ---- inline text entry ---------------------------------------------------------------------

    /// <summary>True while the panel wants typed text: a value box or label edit, or a searching list.</summary>
    public bool Editing => Panel.Editing;
    /// <summary>The in-progress edit buffer (renderer draws it in the active value box).</summary>
    public string EditText => Panel.EditText;

    /// <summary>A row's <see cref="Row.Key"/> as an index (a tab, or a "Copy from" source window); -1 when it is none.</summary>
    private static int ParseIndex(string key)
        => int.TryParse(key, System.Globalization.NumberStyles.None,
                         System.Globalization.CultureInfo.InvariantCulture, out int i) ? i : -1;

    /// <summary>Append typed characters (a searching list takes them as its filter).</summary>
    public void EditInsert(string s) => Panel.EditInsert(s);

    public void EditBackspace() => Panel.EditBackspace();

    /// <summary>Commit the typed value; the panel routes a label here and parses a value box itself.</summary>
    public void CommitEdit(ChatWindow? win)
    {
        _win = win;
        Panel.CommitEdit();
    }

    /// <summary>Commit a tab label: the trimmed text goes on the tab (empty keeps the old one) and its category
    /// entry is renamed.</summary>
    private void OnCommitLabel(Row r, string t)
    {
        ChatWindow? win = _win;
        if (win == null) return;
        int ti = ParseIndex(r.Key);
        string trimmed = t.Trim();
        if ((uint)ti >= (uint)win.Tabs.Count || trimmed.Length == 0) return;
        win.Tabs[ti].Label = trimmed;
        r.Value = trimmed;
        Layout(win);   // the category entry carries the label
    }

    public void CancelEdit() => Panel.CancelEdit();

    /// <summary>Pointer release. Consumed iff we owned the press. Ends any slide drag.</summary>
    public bool MouseUp() => Panel.MouseUp();

    /// <summary>Wheel notch while open: the open dropdown list, else the column under the pointer.</summary>
    public bool MouseWheel(int dy) => Panel.MouseWheel(dy);

    /// <summary>Force-close at a session boundary, clearing any half-owned press, drag, or field edit.</summary>
    public void Reset() { Panel.Close(); TellsHereChanged = false; FontChanged = false; CopyWindowRequested = false; CloseWindowRequested = false; CloseArmed = false; }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
