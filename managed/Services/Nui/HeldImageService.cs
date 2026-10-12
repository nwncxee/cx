using System;
using CX.Engine;
using CX.Framework;
using CX.Services.Radial;
using CX.Ui;

namespace CX.Services.Nui;

/// <summary>
/// The held image: while the server's <see cref="Key"/> policy line names an image, it follows the pointer over every
/// NUI window and the engine cursor swaps to <see cref="HeldCursor"/>. The rules live in <see cref="HeldImageModel"/>;
/// this draws what it decides, on the game thread, as one non-interactive above-NUI window.
/// </summary>
public sealed unsafe class HeldImageService : IExtenderService
{
    /// <summary>The policy key. An empty value releases it.</summary>
    internal const string Key = "held.image";
    /// <summary>The policy key for how long a pick waits for the server's answer, in milliseconds.</summary>
    internal const string ConfirmKey = "held.confirm";
    internal const int ConfirmDefaultMs = 1000, ConfirmMinMs = 50, ConfirmMaxMs = 10_000;
    /// <summary>The engine cursor shown while something is held unless <c>held.cursor</c> names another or <c>none</c>: the <c>Use</c> row
    /// of <c>cursors.2da</c>, whose <c>CursorID</c> column is 25.</summary>
    internal const int DefaultCursorId = 25;
    /// <summary>The look keys: the image's size a cell, the cursor, and how long a hold outlives a NUI that draws nothing.</summary>
    private const string SizeKey = "held.size", CursorKey = "held.cursor", GraceKey = "held.grace";
    private const int MinSizePx = 8, MaxSizePx = 128, MaxGraceMs = 60_000;
    /// <summary>The probe waits this long after boot, past the loading screens and menus, which draw NUI too.</summary>
    private const int ProbeAfterMs = 50_000;
    private const float DefaultSizePx = 32f, Alpha = 0.9f, PendingAlpha = 0.55f;

    private sealed class HeldState
    {
        public readonly HeldImageModel Model = new();
        /// <summary>The throwaway item behind a held <c>@look:</c>; ours until the hold ends.</summary>
        public IntPtr Minted;
        public bool Ready, ProbeDone;
        /// <summary>Set each frame to whether a hold is up; the input thread reads it.</summary>
        public volatile bool Cursor;
        public long SeenDrawn;
        public readonly long Born = Environment.TickCount64;
        public int Notices, LookEpoch = -1, CursorId = DefaultCursorId;
        public float SizePx = DefaultSizePx;
        /// <summary>A pick the input thread saw, for the next frame to show.</summary>
        public volatile string? PickReq;
        public ExtenderLog? Log;
    }

    private static HeldState _s = new();

    /// <summary>Is a hold up with a cursor of its own, so the pointer shows <see cref="HeldCursor"/> wherever it is? Needs the cursor fold.</summary>
    internal static bool HoldsCursor => _s.Cursor && _s.CursorId >= 0;
    internal static int HeldCursor => _s.CursorId;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _s.Log = log;
        HeldPicks.Init();
        HeldImageModel.SelfCheck(log);
        HeldPickSelfCheck.Run(log);
        if (!RadialIcons.Init(log) || EngineFn.SdlGetMouseState == null)
        {
            log.Always("HeldImage: the item icons or the pointer read did not resolve -- no held image");
            return;
        }
        new Window(0, 0, 0, 0) { Focusable = false, Layer = UiLayer.AboveNui, DrawContent = Frame };
        _s.Ready = true;
        Capabilities.Register("heldpick", 1, static () => _s.Ready && NuiKnobs.Enabled.V && NuiKnobs.HeldImage.V && NuiHitTest.Armed);
        Capabilities.Register("heldimage", 1, static () => _s.Ready && NuiKnobs.Enabled.V && NuiKnobs.HeldImage.V);
        NuiKnobs.Enabled.Changed += Capabilities.Refresh;
        NuiKnobs.HeldImage.Changed += Capabilities.Refresh;
    }

    /// <summary>Per frame, from the window roll-call: read the key when it moved, end a hold NUI no longer backs, draw.</summary>
    private static void Frame()
    {
        HeldState s = _s;
        long now = Environment.TickCount64;
        TryProbe(s);
        bool on = NuiKnobs.Enabled.V && NuiKnobs.HeldImage.V;
        int epoch = ServerPolicy.Epoch;
        if (epoch != s.LookEpoch) ReadLook(s, epoch);
        string? value = s.Model.Stale(epoch, on) ? ServerPolicy.Str(Key, "") : null;
        var activity = NuiService.Activity;
        int stamp = s.Model.Pending ? ServerPolicy.Stamp(Key) : 0;
        if (s.Model.Update(epoch, on, value, activity.Drawn, activity.Counting, now, stamp)) Changed(s);
        if (s.Model.Active && NuiHitTest.TakeWindowGone() && s.Model.Release()) Changed(s);
        string? pick = s.PickReq;
        if (pick != null)
        {
            s.PickReq = null;
            int confirm = Math.Clamp(ServerPolicy.Int(ConfirmKey, ConfirmDefaultMs), ConfirmMinMs, ConfirmMaxMs);
            if (on && s.Model.Pick(pick, now, confirm, ServerPolicy.Stamp(Key), activity.Drawn)) Changed(s);
        }
        s.Cursor = s.Model.Active;
        if (!s.Model.Active) return;

        int mx = 0, my = 0;
        EngineFn.SdlGetMouseState(&mx, &my);
        float px = s.SizePx * Metrics.Scale;
        float alpha = s.Model.Pending ? PendingAlpha : Alpha;
        switch (s.Model.Kind)
        {
            case HeldKind.Resref:
                TextRig.DrawIcon(s.Model.Name, mx - px / 2, my - px / 2, px, px, alpha);
                break;
            case HeldKind.Item:
                DrawItem(RadialIcons.Item((IntPtr)(s.Model.Oid | NuiItemIcons.ServerIdBit)), mx, my, px, alpha);
                break;
            case HeldKind.Look:
                DrawItem(s.Minted, mx, my, px, alpha);
                break;
        }
    }

    /// <summary>
    /// A left press landed on NUI (called from the input hook). When the element under the pointer declared a <c>held.pick</c>, the next
    /// frame shows its image as a pending hold. Reads the last published hit, which is the element the pointer was on a frame ago.
    /// </summary>
    internal static void OnPress()
    {
        HeldState s = _s;
        if (!s.Ready || HeldPicks.Count == 0 || !NuiKnobs.Enabled.V || !NuiKnobs.HeldImage.V) return;
        if (!NuiHitTest.TryGet(out NuiHit hit)) return;
        Span<char> id = stackalloc char[NuiHit.TokenCap];
        int n = System.Text.Encoding.UTF8.GetChars(hit.Token(), id);
        string? image = HeldPicks.Find(id[..n]);
        if (image != null) s.PickReq = image;
    }

    /// <summary>A hold started, changed or ended: free the old minted item and make the new one outside any draw batch.</summary>
    private static void Changed(HeldState s)
    {
        if (s.Minted != IntPtr.Zero) { NuiItemIcons.Destroy(s.Minted); s.Minted = IntPtr.Zero; }
        HeldImageModel m = s.Model;
        if (m.Active) NuiHitTest.WatchTopWindow(); else NuiHitTest.StopWatching();
        s.Log?.Info($"HeldImage: {(m.Active ? "holding " + m.Kind + " " + m.Name : "released")}");
        if (m.Kind == HeldKind.Look)
        {
            s.Minted = NuiItemIcons.MintLook(m.Name);
            if (s.Minted == IntPtr.Zero) Notice(s, "an @look: image made no item -- nothing follows the pointer");
        }
        else if (m.Kind == HeldKind.Other) Notice(s, $"'{m.Name}' is not an image this client draws -- only the cursor changes");
    }

    /// <summary>The server's look for a hold, read when the policy moves: size, cursor and grace, each back to its default when released.</summary>
    private static void ReadLook(HeldState s, int epoch)
    {
        s.LookEpoch = epoch;
        s.SizePx = Math.Clamp(ServerPolicy.Int(SizeKey, (int)DefaultSizePx), MinSizePx, MaxSizePx);
        string cursor = ServerPolicy.Str(CursorKey, "");
        s.CursorId = cursor == "none" ? -1 : int.TryParse(cursor, out int id) && id >= 0 ? id : DefaultCursorId;
        s.Model.GraceMs = Math.Clamp(ServerPolicy.Int(GraceKey, HeldImageModel.DefaultGraceMs), 0, MaxGraceMs);
    }

    private static void Notice(HeldState s, string text)
    {
        if (s.Notices++ < 5) s.Log?.Always("HeldImage: " + text);
    }

    /// <summary>The item's icon centred on the pointer, as many cells as its art's own frame fills, or its base item's cells
    /// when the frame is unknown.</summary>
    private static void DrawItem(IntPtr item, int mx, int my, float px, float alpha)
    {
        if (item == IntPtr.Zero) return;
        IntPtr icon = new CNWCItem(item).GuiIcon;
        if (icon == IntPtr.Zero) return;
        NuiItemIcons.Cells(item, out int cw, out int ch);
        IconFrames.Frame f = IconFrames.Of(icon);
        float w = f.Known ? px * f.Cw / IconFrames.CellPx : px * cw, h = f.Known ? px * f.Ch / IconFrames.CellPx : px * ch;
        RadialIcons.DrawGuiIcon(icon, mx - w / 2, my - h / 2, w, h, alpha, cw, ch);
    }

    /// <summary>The live harness has no server: <c>nui.heldprobe</c> sends this client the line, once, at the first NUI draw 50 s after boot.</summary>
    private static void TryProbe(HeldState s)
    {
        long drawn = NuiService.Activity.Drawn;
        bool drawing = drawn != s.SeenDrawn;
        s.SeenDrawn = drawn;
        if (s.ProbeDone || !drawing || Environment.TickCount64 - s.Born < ProbeAfterMs) return;
        s.ProbeDone = true;
        string probe = NuiKnobs.HeldProbe.V;
        if (probe.Length > 0) ServerPolicy.Set(Key, probe);
    }
}
