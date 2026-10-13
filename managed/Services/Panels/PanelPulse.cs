using System;

namespace CX.Services.Panels;

/// <summary>The animation of a <c>pulse</c> text block: its text fades between a floor and full opacity on a smooth wave.</summary>
internal static class PanelPulse
{
    /// <summary>The share of its own opacity the text has at <paramref name="ms"/>: <paramref name="min"/> plus a raised sine over <paramref name="periodMs"/>, starting half way up.</summary>
    public static float Alpha(long ms, float min, float periodMs)
        => min + (1f - min) * (0.5f + 0.5f * MathF.Sin(2f * MathF.PI * (ms % (long)periodMs) / periodMs));
}
