using System;

namespace CX.Services.Panels;

/// <summary>The animation of a <c>pulse</c> text block: its text fades between a floor and full opacity along a wave.</summary>
internal static class PanelPulse
{
    /// <summary>The share of its own opacity the text has at <paramref name="ms"/>: <paramref name="min"/> plus the wave over
    /// <paramref name="periodMs"/>. A sine and a triangle start half way up and rise first; a square holds full for the first half of the period, then the floor.</summary>
    public static float Alpha(long ms, float min, float periodMs, PulseShape shape = PulseShape.Sine)
    {
        float phase = (ms % (long)periodMs) / periodMs;
        float wave = shape switch
        {
            PulseShape.Triangle => phase < 0.25f ? 0.5f + 2f * phase : phase < 0.75f ? 1.5f - 2f * phase : 2f * phase - 1.5f,
            PulseShape.Square => phase < 0.5f ? 1f : 0f,
            _ => 0.5f + 0.5f * MathF.Sin(2f * MathF.PI * phase),
        };
        return min + (1f - min) * wave;
    }
}
