namespace CX.Framework;

/// <summary>Rows of <see cref="EngineLayout"/> for reads the perf and tooling services make of engine records.</summary>
public static unsafe partial class EngineLayout
{
    /// <summary>VertexDataObject element item -&gt; the element count a whole-stream draw (<c>size</c> = -1) covers.</summary>
    public static readonly int VdoElemItemCount = Field<int>("VdoElemItem", "count");
    /// <inheritdoc cref="VdoElemItemCount"/>
    public static readonly int VdoElemItemStride = Field("VdoElemItem", "stride");
}
