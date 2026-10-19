namespace CX.Framework;

/// <summary>Engine resource-type ids, as the <c>CResHelper&lt;T, n&gt;</c> instantiations and the key table's
/// restype column name them. One declaration, so a layer asking the resource chain for a file names the type
/// from here instead of repeating the number.</summary>
public static class ResTypes
{
    public const ushort Tga = 3, Plt = 6, Mdl = 2002, Dds = 2033;
}
