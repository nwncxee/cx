using System;

namespace CX.Services.Panels;

/// <summary>The animation of a <c>pulse</c> text block: its text fades between <see cref="Min"/> and full opacity on a smooth wave.</summary>
internal static class PanelPulse
{
    /// <summary>The faintest the text gets, as a share of its own opacity.</summary>
    public const float Min = 0.35f;
    /// <summary>One full fade out and back, in milliseconds.</summary>
    public const long PeriodMs = 1200;

    /// <summary>The share of its own opacity the text has at <paramref name="ms"/>: <see cref="Min"/> plus a raised sine, starting half way up.</summary>
    public static float Alpha(long ms)
        => Min + (1f - Min) * (0.5f + 0.5f * MathF.Sin(2f * MathF.PI * (ms % PeriodMs) / PeriodMs));
}
