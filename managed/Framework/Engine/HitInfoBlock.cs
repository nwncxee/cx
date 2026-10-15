using System.Runtime.InteropServices;

namespace CX.Engine;

/// <summary>The room an engine <c>CHitInfo</c> fills, read through its <c>CHitInfo</c> view.</summary>
[StructLayout(LayoutKind.Sequential, Size = CX.Framework.EngineLayout.SizeOfHitInfo)]
public struct HitInfoSpace
{
}

/// <summary>A <c>CHitInfo</c> and the one-slot ignore list laid right after it, allocated together.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct HitInfoBlock
{
    /// <summary>The record the engine fills.</summary>
    public HitInfoSpace Info;
    /// <summary>The ignore list's single entry.</summary>
    public nint IgnoreSlot;
}
