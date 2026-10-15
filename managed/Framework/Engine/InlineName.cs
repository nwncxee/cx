using System.Runtime.CompilerServices;
using CX.Framework;

namespace CX.Engine;

/// <summary>An inline <c>char[]</c> name reads as a C string: the row says where it starts, not how long the
/// array is, so a <c>byte*</c> -- not a <c>Span</c> -- is what a comparison walks.</summary>
public readonly unsafe partial struct Animation
{
    /// <inheritdoc cref="EngineLayout.AnimationName"/>
    public byte* Chars => (byte*)Unsafe.AsPointer(ref Name);
}

/// <inheritdoc cref="Animation"/>
public readonly unsafe ref partial struct Model
{
    /// <inheritdoc cref="EngineLayout.ModelName"/>
    public byte* Chars => (byte*)Unsafe.AsPointer(ref Name);
}

/// <inheritdoc cref="Animation"/>
public readonly unsafe ref partial struct MdlNode
{
    /// <inheritdoc cref="EngineLayout.MdlNodeName"/>
    public byte* Chars => (byte*)Unsafe.AsPointer(ref Name);
}
