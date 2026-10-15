namespace CX.Framework;

/// <summary>Rows of <see cref="EngineLayout"/> for the seam's reads of the engine's own structs.</summary>
public static unsafe partial class EngineLayout
{
    /// <summary>LerpFloat -&gt; the value the lerp starts from (<c>WriteGFF</c> rva 0x2ea250 writes it as ValueFrom);
    /// <see cref="LerpValue"/> is where it ends. <c>Gob::Gob</c> seeds both with the same start.</summary>
    public static readonly int LerpFrom = Field<float>("LerpFloat", "from");
}
