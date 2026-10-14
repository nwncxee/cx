using System;
using System.IO;
using CX.Engine;
using CX.Framework;
using CX.Services.Nui;
using CX.Services.Radial;
using CX.Ui;
using CX.Services.Panels;

namespace CX.Services.Cards;

/// <summary>
/// Draws the cards a server declares for a NUI element (<c>docs/ui-policy-contract.md</c>). One window on the above-NUI
/// layer takes no input; each frame it asks the NUI hit test what the pointer rests on, and a <see cref="CardPresenter"/> decides
/// what shows. The <c>card</c> capability is advertised only while the hit test is armed and <c>nui.cards</c> is on.
/// </summary>
public sealed class CardService : IExtenderService
{
    private sealed class CardServiceState
    {
        public readonly CardPresenter Presenter = new();
        public bool Ready;
        public NuiHit Forced;
        public string ForcedKey = "";
        public int FileGen = -1;
        public long LoadedAt;
        public bool LateDone;
        public ExtenderLog? Log;
    }

    private static CardServiceState _s = new();

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        UiCategory.Declare();
        var s = _s;
        s.Log = log;
        CardRegistry.Init(log);
        CardSelfCheck.Run(log);
        s.Presenter.SubjectOf = Subject;
        Capabilities.Register("card", 7, static () => _s.Ready && NuiKnobs.Enabled.V && NuiKnobs.Cards.V && NuiHitTest.Armed);
        NuiKnobs.Enabled.Changed += Capabilities.Refresh;
        NuiKnobs.Cards.Changed += Capabilities.Refresh;
        TextRig.FontChanged += PanelFonts.WantAll;
        new Window(0, 0, 0, 0) { Focusable = false, Layer = UiLayer.AboveNui, DrawContent = Draw };
        s.Ready = true;
    }

    /// <summary><c>nui.cardfile</c>: policy lines read as if a server had sent them, for building and testing cards. Read again
    /// whenever the registry is empty and has changed since, because a login clears it.</summary>
    private static void LoadTestFile(CardServiceState s)
    {
        string path = NuiKnobs.CardFile.V;
        if (path.Length == 0 || CardRegistry.Count > 0 || CardRegistry.Gen == s.FileGen) return;
        if (ReadFile(s, "nui.cardfile", path) is { } text) s.Log?.Always($"Cards: nui.cardfile applied {ServerPolicy.Apply(text)} line(s)");
        s.FileGen = CardRegistry.Gen;
        s.LoadedAt = Environment.TickCount64;
        s.LateDone = false;
    }

    /// <summary><c>nui.cardlate</c> (<c>seconds:file</c>): the lines of that file, applied once the seconds have passed since the card file was read.</summary>
    private static void ApplyLate(CardServiceState s)
    {
        string spec = NuiKnobs.CardLate.V;
        int colon = spec.IndexOf(':');
        if (s.LateDone || s.LoadedAt == 0 || colon <= 0 || !float.TryParse(spec.AsSpan(0, colon), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sec)) return;
        if (Environment.TickCount64 - s.LoadedAt < (long)(sec * 1000f)) return;
        s.LateDone = true;
        string path = spec[(colon + 1)..].Trim();
        if (path.Length == 0) { s.Log?.Always("Cards: nui.cardlate names no file after the colon"); return; }
        if (ReadFile(s, "nui.cardlate", path) is { } text) s.Log?.Always($"Cards: nui.cardlate applied {ServerPolicy.Apply(text)} line(s)");
    }

    /// <summary>The text of the file a test knob names, or null with the reason logged. Nothing a bad path throws leaves here.</summary>
    private static string? ReadFile(CardServiceState s, string knob, string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            s.Log?.Always($"Cards: {knob} could not be read: {e.Message}");
            return null;
        }
    }

    /// <summary>The name and stack size the client knows for an item, by the server's object id.</summary>
    private static PanelSubject Subject(uint serverId)
    {
        uint id = serverId | ImageNames.ServerIdBit;
        IntPtr item = RadialIcons.Item((IntPtr)id);
        if (item == IntPtr.Zero) return default;
        int stack = new CNWCItem(item).StackSize;
        return new PanelSubject(RadialIcons.ObjectName(id), stack.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The per-frame draw: read the hit, update the card, draw each card's background, icons, then text.</summary>
    private static void Draw()
    {
        var s = _s;
        if (!NuiKnobs.Enabled.V || !NuiKnobs.Cards.V)
        {
            if (s.Presenter.Alpha > 0f) s.Presenter.Cut();
            return;
        }
        LoadTestFile(s);
        ApplyLate(s);
        CardLooks.Sync(s.Log);
        bool has;
        NuiHit hit;
        string force = NuiKnobs.CardForce.V;
        if (force.Length > 0) { has = true; hit = Forced(s, force); }
        else has = NuiHitTest.TryGet(out hit);
        s.Presenter.Update(has, in hit);
        if (!s.Presenter.Prepare(TextRig.ViewportW, TextRig.ViewportH)) return;
        float a = s.Presenter.Alpha;
        PanelView[] views = s.Presenter.Views!;
        for (int i = 0; i < views.Length; i++) DrawView(views[i], s.Presenter.X(i), s.Presenter.Y(i), a);
    }

    private static void DrawView(PanelView v, float x, float y, float a)
    {
        TextRig.DrawBatch(v.Back.AsSpan(), v.Back.VertexCount, a, x, y);
        for (int i = 0; i < v.Icons.Count; i++)
        {
            IconDraw ic = v.Icons[i];
            if (ic.Look != null) CardLooks.Draw(ic.Look, x + ic.X, y + ic.Y, ic.W, ic.H, a * ic.Alpha);
            else if (ic.Model != null) DrawModel(ic.Model, x + ic.X, y + ic.Y, ic.W, ic.H, a * ic.Alpha);
            else if (ic.Item != 0) RadialIcons.Draw((IntPtr)(ic.Item | ImageNames.ServerIdBit), x + ic.X, y + ic.Y, ic.W, ic.H, a * ic.Alpha);
            else DrawResref(ic.Src, x + ic.X, y + ic.Y, ic.W, ic.H, a * ic.Alpha);
        }
        TextRig.DrawBatch(v.Text.AsSpan(), v.Text.VertexCount, a, x, y);
        if (v.Pulse != null) TextRig.DrawBatch(v.Pulse.AsSpan(), v.Pulse.VertexCount, a * PanelPulse.Alpha(Environment.TickCount64, v.PulseMin, v.PulsePeriodMs), x, y);
    }

    /// <summary>A model view stretched to the box. Nothing until the doll has been built and drawn once.</summary>
    private static void DrawModel(string key, float x, float y, float w, float h, float a)
    {
        uint tex = NuiModelView.Show(key, out _, out _);
        if (tex != 0) TextRig.DrawIconTid((int)tex, x, y, w, h, a, flipV: true);
    }

    /// <summary>A resref icon at its art's own proportions, centred in the box.</summary>
    private static void DrawResref(string resref, float x, float y, float w, float h, float a)
    {
        if (string.IsNullOrEmpty(resref) || !AurTex.Ready) return;
        Span<byte> name = stackalloc byte[RadialIcons.ResRefLen];
        name.Clear();
        for (int i = 0; i < resref.Length && i < name.Length; i++) name[i] = (byte)resref[i];
        IconFrames.Fit(IconFrames.Read(name, false), ref x, ref y, ref w, ref h, out float u1, out float v0);
        TextRig.DrawIconTid(TextRig.ResolveTid(resref), x, y, w, h, a, u1, v0);
    }

    /// <summary>The pointer-free hit <c>nui.cardforce</c> stands in: an element with that id at a fixed place.</summary>
    private static NuiHit Forced(CardServiceState s, string key)
    {
        if (s.ForcedKey != key)
        {
            s.ForcedKey = key;
            s.Forced = new NuiHit { Element = (IntPtr)1, Row = -1, X = 160f, Y = 200f, W = 64f, H = 64f };
            if (System.Text.Encoding.UTF8.TryGetBytes(key, s.Forced.TokenBuffer(), out int n)) s.Forced.TokenLen = n;
            else s.Log?.Always($"Cards: nui.cardforce is over {NuiHit.TokenCap} bytes, longer than any element id -- it matches nothing");
        }
        return s.Forced;
    }
}
