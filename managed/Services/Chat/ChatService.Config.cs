using System;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Services.Chat.Render;
using CX.Ui;

namespace CX.Services.Chat;

/// <summary>The persisted side of the chatbox: the per-window <c>cx.cfg</c> round trip (LoadConfig / SaveConfig via the
/// <c>chat</c> group's Hidden bag) and the server-synced game clock the timestamps read.</summary>
public sealed unsafe partial class ChatService
{
    // *g_pAppManager, for the game clock (below) and the Tell player resolver. The in-game gate that used to
    // walk this chain now lives in UiHostService; chat reads GameGate.InGame / GameGate.InModule instead.
    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);

    // In-game game clock (chat game-time timestamps). The server syncs ONLY the game HOUR to the client, into
    // CNWCModule+0x16c (verified: the free-running CWorldTimer is never anchored to module time, which is why
    // reading it showed a wrong hour). CClientExoApp::GetModule(app) fetches the module off the same
    // *g_pAppManager chain the gate walks. minute/second aren't on the wire, so we anchor real-time to each
    // hour edge (PollGameClock, per frame) and interpolate a sub-hour minute at ingest.

    private static int _gmHour = -1;    // last polled server game hour (0..23); -1 = none seen -> local fallback
    private static long _gmHourMs;      // TickCount64 when _gmHour last changed (sub-hour interpolation anchor)
    private static int _gmMph = 2;      // module MinutesPerHour (real minutes per game hour); 2 = engine default

    /// <summary>The client CNWCModule (server-synced state), or Zero off a module (main menu / run-demo). All
    /// guarded pointer reads off *g_pAppManager, then CClientExoApp::GetModule(app).</summary>
    private static IntPtr GameModule()
    {
        if (_appMgr == IntPtr.Zero || EngineFn.GetModule == null) return IntPtr.Zero;
        IntPtr mgr = *(IntPtr*)_appMgr; if (mgr == IntPtr.Zero) return IntPtr.Zero;
        IntPtr app = *(IntPtr*)mgr;     if (app == IntPtr.Zero) return IntPtr.Zero;
        return new CClientExoApp(app).GetModule().Ptr;
    }

    /// <summary>Track the server-synced game hour each frame so ingest can interpolate a sub-hour minute. Hot,
    /// zero-alloc: pointer reads + one call. Resets to "unseen" off a module. An out-of-range hour (e.g. a
    /// wrong offset, or the field still zero pre-first-update reading as a valid midnight) is treated as unseen.</summary>
    private static void PollGameClock()
    {
        IntPtr module = GameModule();
        if (module == IntPtr.Zero) { _gmHour = -1; return; }
        var mod = new CNWCModule(module);
        int hour = mod.Hour;
        if ((uint)hour > 23u) { _gmHour = -1; return; }   // guards a bad offset -> local fallback
        int mph = mod.MinutesPerHour;
        _gmMph = mph > 0 ? mph : 2;
        if (hour != _gmHour) { _gmHour = hour; _gmHourMs = Environment.TickCount64; }   // hour edge -> re-anchor
    }

    /// <summary>Current in-game time, or an invalid <see cref="GameStamp"/> (caller falls back to local) before
    /// any server hour is seen. The hour is server-synced; minute/second are extrapolated from real time since
    /// the hour edge and self-correct at each rollover. Cold (ingest). ponytail: the wire carries only the hour,
    /// so minute/second drift or pin under pause/timestop; fall back to HH-only if this reads wrong live.</summary>
    private static GameStamp GameNow()
    {
        int hour = _gmHour;
        if (hour < 0) return default;
        long hourLenMs = (long)_gmMph * 60000L;
        if (hourLenMs <= 0) return new GameStamp(hour, 0, 0);
        double frac = (Environment.TickCount64 - _gmHourMs) / (double)hourLenMs;
        if (frac < 0) frac = 0; else if (frac > 1) frac = 1;   // clamp into the hour (pause / just-past-rollover slop)
        int minute = (int)(frac * 60); if (minute > 59) minute = 59;
        int second = (int)(frac * 3600) % 60;
        return new GameStamp(hour, minute, second);
    }

    /// <summary>Restore the per-window state from the <c>chat</c> group's Hidden bag (bound from <c>cx.cfg</c>
    /// pre-main, so this runs at Initialize) and apply it to every window (rect/toggles/input side/send
    /// channel/tabs). Creates additional windows from the config up to the cap.</summary>
    private static void LoadConfig()
    {
        if (_layout == null) return;
        ChatConfig config = ChatConfig.From(ChatKnobs.Windows.Pairs);
        for (int i = 0; i < config.Windows.Count && i < ChatLayout.MaxWindows; i++)
        {
            var wc = config.Windows[i];
            ChatWindow win;
            if (i < _layout.Count)
            {
                win = _layout.Windows[i];
            }
            else
            {
                // Only create additional windows for config entries with a real rect (skip gap-filled defaults).
                if (wc.W < ChatWindow.MinW || wc.H < ChatWindow.MinH) continue;
                win = new ChatWindow(wc.X, wc.Y,
                    System.Math.Max(wc.W, ChatWindow.MinW), System.Math.Max(wc.H, ChatWindow.MinH),
                    new[] { new Tab("All", 0xffffffff) });
                if (_layout.AddWindow(win) == null) break;
            }
            if (wc.W >= ChatWindow.MinW && wc.H >= ChatWindow.MinH)
            { win.X = wc.X; win.Y = wc.Y; win.W = wc.W; win.H = wc.H; }
            win.Toggles = wc.Toggles;
            win.InputOnTop = wc.InputOnTop;
            if (wc.SendChannel >= 0 && wc.SendChannel <= 5) win.SendChannel = (Channel)wc.SendChannel;
            if (wc.AnimEnabled is bool ae) win.AnimEnabled = ae;
            if (wc.HoldSec is float hs) win.HoldSec = hs;
            if (wc.FadeSec is float fs) win.FadeSec = fs;
            if (wc.TauMs is float tm) win.TauMs = tm;
            if (wc.Outline is int ol) win.Outline = ol;
            if (wc.FontPx is int fp && fp >= TextRig.MinFontPx && fp <= TextRig.MaxFontPx) win.FontPx = fp;
            if (wc.Typeface is { Length: > 0 } tfn) win.Typeface = tfn;
            if (wc.TimeFormat is int tf) win.TimeFormat = (TsFormat)tf;
            if (wc.TimeSource is int ts) win.TimeSource = (TsSource)ts;
            if (wc.Align is int al && al >= 0 && al <= 3) win.Align = (TextAlign)al;
            if (wc.BgMode is int bgm && bgm >= 0 && bgm <= 3) win.BgMode = (BgMode)bgm;
            if (wc.BgAngle is float ba) win.BgAngle = Math.Clamp(ba, 0f, 360f);
            if (wc.BgStart is float bs) win.BgStart = Math.Clamp(bs, 0f, 1f);
            if (wc.BgEnd is float be) win.BgEnd = Math.Clamp(be, 0f, 1f);
            if (wc.AutoHideHeader is bool ahh) win.AutoHideHeader = ahh;
            if (wc.AutoHideFrame is bool ahf) win.AutoHideFrame = ahf;
            if (wc.Locked is bool lk) win.Locked = lk;
            if (wc.TellsHere is bool th) win.TellsHere = th;
            if (wc.Tabs.Count > 0)
            {
                var tabs = new Tab[wc.Tabs.Count];
                for (int j = 0; j < wc.Tabs.Count; j++)
                    tabs[j] = new Tab(wc.Tabs[j].Label, wc.Tabs[j].Mask) { PinnedName = wc.Tabs[j].Pin, Kept = wc.Tabs[j].Kept };
                win.SetTabs(tabs);
            }
        }
        if (config.RetiredColorKeys > 0)
            _log?.Info($"[chat] {config.RetiredColorKeys} retired per-window colour key(s) in cx.cfg ignored "
                     + "-- chat follows the shared look (ui.look / ui.palette); they leave the file on the next save");
    }

    /// <summary>Sync every window-owned field into the <c>chat</c> group's Hidden bag (a whole replace, so a
    /// closed window's keys leave the file) and arm the <c>cx.cfg</c> writer. AC4: ephemeral (auto-created,
    /// unkept) tabs are excluded; a non-Main window whose tabs are ALL ephemeral is dropped entirely.</summary>
    private static void SaveConfig()
    {
        if (_layout == null) return;
        var config = new ChatConfig();
        ChatWindow[] wins = _layout.Windows;
        for (int i = 0; i < wins.Length; i++)
        {
            ChatWindow win = wins[i];
            // AC4: skip non-Main windows where every tab is ephemeral (no kept content).
            if (i > 0)
            {
                bool allEph = true;
                var tabs = win.Tabs;
                for (int j = 0; j < tabs.Count; j++) if (!tabs[j].Ephemeral) { allEph = false; break; }
                if (allEph) continue;
            }
            var wc = new WindowConfig();
            wc.X = win.X; wc.Y = win.Y; wc.W = win.W; wc.H = win.H;
            wc.Toggles = win.Toggles; wc.InputOnTop = win.InputOnTop;
            wc.SendChannel = (int)win.SendChannel;
            wc.AnimEnabled = win.AnimEnabled; wc.HoldSec = win.HoldSec;
            wc.FadeSec = win.FadeSec; wc.TauMs = win.TauMs;
            wc.Outline = win.Outline;
            wc.FontPx = win.FontPx; wc.Typeface = win.Typeface;
            wc.TimeFormat = (int)win.TimeFormat; wc.TimeSource = (int)win.TimeSource;
            wc.Align = (int)win.Align;
            wc.BgMode = (int)win.BgMode; wc.BgAngle = win.BgAngle; wc.BgStart = win.BgStart; wc.BgEnd = win.BgEnd;
            wc.AutoHideHeader = win.AutoHideHeader; wc.AutoHideFrame = win.AutoHideFrame;
            wc.Locked = win.Locked;
            wc.TellsHere = win.TellsHere;
            for (int j = 0; j < win.Tabs.Count; j++)
            {
                if (win.Tabs[j].Ephemeral) continue; // AC4: unkept tabs excluded
                wc.Tabs.Add((win.Tabs[j].Label, win.Tabs[j].ChannelMask, win.Tabs[j].PinnedName, win.Tabs[j].Kept));
            }
            config.Windows.Add(wc);
        }
        ChatKnobs.Windows.Replace(config.Pairs());
    }
}
