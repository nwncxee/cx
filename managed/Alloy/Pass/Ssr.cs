namespace CX.Alloy;

/// <summary>
/// What both halves of the screen-space reflection share: the ray budget the trace reads, and the period line's
/// clause. The pass itself is <c>AlloyRenderer.VkSsr</c>.
/// </summary>
internal static class Ssr
{
    /// <summary>Cell visits per ray at each finest level (full, half, quarter): a coarser confirm needs fewer. A ray
    /// grazing the ground it left never skips a coarse cell (the ground's near edge is in it), so it pays ~2 visits a
    /// pixel; 40 ran dry a few hundred pixels short of a building at low camera pitch and left the probe's sky.</summary>
    internal static ReadOnlySpan<float> Steps => [160f, 96f, 48f];

    /// <summary>The period line's clause: whether the reflections are asked for at all.</summary>
    internal static string Stats(int frames) => $"ssr {(Lighting.Ssr.V ? "on" : "off")}";
}
