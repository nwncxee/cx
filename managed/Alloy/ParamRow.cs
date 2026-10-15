using System.Runtime.InteropServices;

namespace CX.Alloy;

/// <summary>A material parameter the engine's <c>SetMaterialParam</c> or a <c>.mtr</c> can set on a draw record.</summary>
internal enum ParamName : byte
{
    None, Specularity, Roughness, Metallicness, CustomSpecularColor,
    CxMetallic, CxRoughness, CxClearcoat, CxClearcoatRoughness, CxSheenColor, CxSheenRoughness, CxEmissiveStrength, CxOrm,
}

/// <summary>The material terms a recipe stages verbatim into its draw record: the two stock PBR vec4s, then
/// ADR 0026's three extension vec4s. A zero in the stock terms means "not declared".</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ParamRow
{
    /// <summary>The stock PBR terms.</summary>
    public float Specularity, Roughness, Metallicness;
    /// <summary>The slot mask's lane, written per frame elsewhere and never here.</summary>
    public float Spare;
    /// <summary><c>CustomSpecularColor</c>, then whether it was declared (black is the engine's "none").</summary>
    public float CustomR, CustomG, CustomB, CustomDeclared;
    /// <summary>(CxMetallic, CxRoughness, CxClearcoat, CxClearcoatRoughness), (CxSheenColor.rgb, CxSheenRoughness),
    /// (CxEmissiveStrength, CxOrm, _, _).</summary>
    public MaterialExt Ext;

    /// <summary>Set the first component of <paramref name="name"/>, as the engine's own setter does.</summary>
    public void Set(ParamName name, float v)
    {
        switch (name)
        {
            case ParamName.Specularity: Specularity = v; break;
            case ParamName.Roughness: Roughness = v; break;
            case ParamName.Metallicness: Metallicness = v; break;
            case ParamName.CustomSpecularColor: CustomR = v; break;
            case ParamName.CxMetallic: Ext.Ext0.X = v; break;
            case ParamName.CxRoughness: Ext.Ext0.Y = v; break;
            case ParamName.CxClearcoat: Ext.Ext0.Z = v; break;
            case ParamName.CxClearcoatRoughness: Ext.Ext0.W = v; break;
            case ParamName.CxSheenColor: Ext.Ext1.X = v; break;
            case ParamName.CxSheenRoughness: Ext.Ext1.W = v; break;
            case ParamName.CxEmissiveStrength: Ext.Ext2.X = v; break;
            case ParamName.CxOrm: Ext.Ext2.Y = v; break;
        }
    }
}
