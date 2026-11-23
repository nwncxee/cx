using System;
using System.Collections.Generic;
using CX.Ui;

namespace CX.Services.Chat.Core;

/// <summary>Per-window persisted state: rect, render toggles, input side, send channel, look settings
/// (animation, outline, timestamps, background shape), and an ordered tab list. A null look field means "not
/// set": the window keeps its hardcoded default rather than a stale zero.</summary>
public sealed class WindowConfig
{
    public float X, Y, W, H;
    public uint Toggles = ChatToggle.Timestamp | ChatToggle.MessageBody | ChatToggle.Portrait;
    public bool InputOnTop;
    public int SendChannel;

    /// <summary>Per-window look settings. Null = the window's hardcoded default.</summary>
    public bool? AnimEnabled;
    public float? HoldSec, FadeSec, TauMs;
    public int? Outline;
    /// <summary>Per-window font: absent = follow the UI knobs.</summary>
    public int? FontPx;
    public string? Typeface;
    /// <summary>Per-window Default weight and Default style: absent = follow the UI rows.</summary>
    public int? Weight, Style;
    public int? TimeFormat, TimeSource;
    public int? Align;
    public int? BgMode;
    /// <summary>Gradient direction (deg) and stops (0..1); null = the window defaults.</summary>
    public float? BgAngle, BgStart, BgEnd;
    public bool? AutoHideFrame;
    public bool? AutoHideHeader;
    public bool? Locked;
    public bool? TellsHere;
    /// <summary>The pointer goes through the window; never set on Main (window 0).</summary>
    public bool ClickThrough;
    /// <summary>The window draws above NUI; off unless the player switched it on.</summary>
    public bool AboveNui;

    /// <summary>Persisted tabs: (label, channel mask as hex, pinned player name, kept). Empty = use the hardcoded defaults.</summary>
    public readonly List<(string Label, uint Mask, string Pin, bool Kept)> Tabs = new();

    /// <summary>The window's persisted fields, tabs aside: the caller adds the tabs it keeps.</summary>
    public static WindowConfig Capture(ChatWindow win)
    {
        var wc = new WindowConfig { X = win.X, Y = win.Y, W = win.W, H = win.H };
        wc.Toggles = win.Toggles; wc.InputOnTop = win.InputOnTop;
        wc.SendChannel = (int)win.SendChannel;
        wc.AnimEnabled = win.AnimEnabled; wc.HoldSec = win.HoldSec;
        wc.FadeSec = win.FadeSec; wc.TauMs = win.TauMs;
        wc.Outline = win.Outline;
        wc.FontPx = win.FontPx; wc.Typeface = win.Typeface; wc.Weight = win.Weight; wc.Style = win.Style;
        wc.TimeFormat = (int)win.TimeFormat; wc.TimeSource = (int)win.TimeSource;
        wc.Align = (int)win.Align;
        wc.BgMode = (int)win.BgMode; wc.BgAngle = win.BgAngle; wc.BgStart = win.BgStart; wc.BgEnd = win.BgEnd;
        wc.AutoHideHeader = win.AutoHideHeader; wc.AutoHideFrame = win.AutoHideFrame;
        wc.Locked = win.Locked;
        wc.TellsHere = win.TellsHere;
        wc.ClickThrough = win.ClickThrough;
        wc.AboveNui = win.AboveNui;
        return wc;
    }

    /// <summary>Put the persisted look and behaviour back on <paramref name="win"/>; a null field leaves its default.</summary>
    public void ApplyTo(ChatWindow win)
    {
        win.Toggles = Toggles;
        win.InputOnTop = InputOnTop;
        if (SendChannel >= 0 && SendChannel <= 5) win.SendChannel = (Channel)SendChannel;
        if (AnimEnabled is bool ae) win.AnimEnabled = ae;
        if (HoldSec is float hs) win.HoldSec = hs;
        if (FadeSec is float fs) win.FadeSec = fs;
        if (TauMs is float tm) win.TauMs = tm;
        if (Outline is int ol) win.Outline = ol;
        if (FontPx is int fp && fp >= TextRig.MinFontPx && fp <= TextRig.MaxFontPx) win.FontPx = fp;
        if (Typeface is { Length: > 0 } tfn) win.Typeface = tfn;
        if (Weight is int fw && (fw == 0 || Array.IndexOf(TextStyle.Weights, fw) >= 0)) win.Weight = fw;
        if (Style is int fst && fst is >= FontRegistry.StyleAsUi and <= FontRegistry.StyleItalic) win.Style = fst;
        if (TimeFormat is int tf) win.TimeFormat = (TsFormat)tf;
        if (TimeSource is int ts) win.TimeSource = (TsSource)ts;
        if (Align is int al && al >= 0 && al <= 3) win.Align = (TextAlign)al;
        if (BgMode is int bgm && bgm >= 0 && bgm <= 3) win.BgMode = (BgMode)bgm;
        if (BgAngle is float ba) win.BgAngle = Math.Clamp(ba, 0f, 360f);
        if (BgStart is float bs) win.BgStart = Math.Clamp(bs, 0f, 1f);
        if (BgEnd is float be) win.BgEnd = Math.Clamp(be, 0f, 1f);
        if (AutoHideHeader is bool ahh) win.AutoHideHeader = ahh;
        if (AutoHideFrame is bool ahf) win.AutoHideFrame = ahf;
        if (Locked is bool lk) win.Locked = lk;
        if (TellsHere is bool th) win.TellsHere = th;
        win.ClickThrough = ClickThrough;
        win.AboveNui = AboveNui;
    }
}
