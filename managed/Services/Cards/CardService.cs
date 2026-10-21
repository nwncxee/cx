using System;
using System.Collections.Generic;
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
        /// <summary>The game's GUI art for a card that wears the native look; one skin draws every card on screen.</summary>
        public readonly Skin Skin = NativeChrome.NewSkin();
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
        Capabilities.Register("card", 9, static () => _s.Ready && NuiKnobs.Enabled.V && NuiKnobs.Cards.V && NuiHitTest.Armed);
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

    /// <summary><c>nui.cardlatefile</c>: the lines of that file, applied once <c>nui.cardlatesec</c> seconds have passed since the card file was read.</summary>
    private static void ApplyLate(CardServiceState s)
    {
        string path = NuiKnobs.CardLateFile.V.Trim();
        if (s.LateDone || s.LoadedAt == 0 || path.Length == 0) return;
        if (Environment.TickCount64 - s.LoadedAt < NuiKnobs.CardLateSec.V * 1000L) return;
        s.LateDone = true;
        if (ReadFile(s, "nui.cardlatefile", path) is { } text) s.Log?.Always($"Cards: nui.cardlatefile applied {ServerPolicy.Apply(text)} line(s)");
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
        return new PanelSubject(Native.ObjectName(id), stack.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
        bool armed = s.Presenter.WantsNative(Theme.IsNative) && s.Skin.Arm(s.Log, "");
        if (!s.Presenter.Prepare(TextRig.ViewportW, TextRig.ViewportH, armed)) return;
        float a = s.Presenter.Alpha;
        PanelView[] views = s.Presenter.Views!;
        Art(s.Skin, views, s.Presenter, a);
        for (int i = 0; i < views.Length; i++) DrawView(views[i], s.Presenter.X(i), s.Presenter.Y(i), a);
    }

    /// <summary>Every native card's art, under all the flat geometry and text: one pass of the one skin, flushed before
    /// the first card draws. Nothing is emitted while no card wears it.</summary>
    private static void Art(Skin skin, PanelView[] views, CardPresenter p, float alpha)
    {
        int n = 0;
        for (int i = 0; i < views.Length; i++) n += views[i].Art.Count;
        if (n == 0) return;
        skin.Begin();
        for (int i = 0; i < views.Length; i++)
        {
            float x = p.X(i), y = p.Y(i);
            List<SkinRect> art = views[i].Art;
            for (int j = 0; j < art.Count; j++)
            {
                SkinRect r = art[j];
                skin.Draw(r.Part, x + r.X0, y + r.Y0, x + r.X1, y + r.Y1);
            }
        }
        skin.Flush(alpha);
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
            else if (ic.Crop.Set) DrawCropped(ic.Src, ic.Crop, x + ic.X, y + ic.Y, ic.W, ic.H, a * ic.Alpha);
            else DrawResref(ic.Src, x + ic.X, y + ic.Y, ic.W, ic.H, a * ic.Alpha);
        }
        TextRig.DrawBatch(v.Text.AsSpan(), v.Text.VertexCount, a, x, y);
        if (v.Pulse != null) TextRig.DrawBatch(v.Pulse.AsSpan(), v.Pulse.VertexCount, a * PanelPulse.Alpha(Environment.TickCount64, v.PulseMin, v.PulsePeriodMs, v.PulseWave), x, y);
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

    /// <summary>The part <paramref name="crop"/> of a resref's texture, stretched to the box. Textures are bottom-up, so the crop's top is
    /// the larger v.</summary>
    private static void DrawCropped(string resref, CropRect crop, float x, float y, float w, float h, float a)
    {
        if (string.IsNullOrEmpty(resref) || !AurTex.Ready) return;
        TextRig.DrawIconRegion(TextRig.ResolveTid(resref), x, y, w, h, a, crop.X, 1f - crop.Y, crop.X + crop.W, 1f - crop.Y - crop.H);
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
