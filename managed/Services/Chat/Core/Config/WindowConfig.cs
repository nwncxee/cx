using System.Collections.Generic;

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
    public int? TimeFormat, TimeSource;
    public int? Align;
    public int? BgMode;
    /// <summary>Gradient direction (deg) and stops (0..1); null = the window defaults.</summary>
    public float? BgAngle, BgStart, BgEnd;
    public bool? AutoHideFrame;
    public bool? AutoHideHeader;
    public bool? Locked;
    public bool? TellsHere;

    /// <summary>Persisted tabs: (label, channel mask as hex, pinned player name, kept). Empty = use the hardcoded defaults.</summary>
    public readonly List<(string Label, uint Mask, string Pin, bool Kept)> Tabs = new();
}
