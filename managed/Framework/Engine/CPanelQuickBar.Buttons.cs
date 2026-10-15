using System;
using CX.Framework;

namespace CX.Engine;

public readonly partial struct CPanelQuickBar
{
    /// <summary>Button <paramref name="index"/> (set x 12 + column), laid inline from the first, one stride apart.</summary>
    public IntPtr Button(int index) => Ptr + EngineLayout.QuickbarButtons + index * EngineLayout.QuickbarButtonStride;
}
