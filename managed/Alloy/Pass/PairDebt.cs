namespace CX.Alloy;

/// <summary>The HOT-LIGHT DEBT pass (ADR 0029): one compute dispatch over the deferred frame that takes a
/// creature's shadow back out of the baked term, once per pixel -- each 16x16 tile culls this frame's
/// <see cref="PairShadows"/> pairs against its own view-space box and subtracts the spec's own term. Declared
/// here as the content's override point; the dispatch itself belongs to the backend that draws the frame.</summary>
internal static class PairDebt
{
    /// <summary>The overridable compute stage's resref (hak-limit length).</summary>
    internal const string ShdCs = "cx_pairdebt";
}
