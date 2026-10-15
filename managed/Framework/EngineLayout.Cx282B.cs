namespace CX.Framework;

/// <summary>Rows of <see cref="EngineLayout"/> for the seam's reads of the engine's own structs.</summary>
public static unsafe partial class EngineLayout
{
    /// <summary>LerpFloat -&gt; the end value an animated float settles on; <c>Gob::Gob</c> seeds it with the start.</summary>
    public static readonly int LerpTarget = Field<float>("LerpFloat", "target");
}
