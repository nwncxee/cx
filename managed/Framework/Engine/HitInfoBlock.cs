using System.Runtime.InteropServices;

namespace CX.Engine;

/// <summary>A <c>CHitInfo</c> and the one-slot ignore list laid right after it, allocated together.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct HitInfoBlock
{
    /// <summary>The record the engine fills.</summary>
    public fixed byte Info[CX.Framework.EngineLayout.SizeOfHitInfo];
    /// <summary>The ignore list's single entry.</summary>
    public nint IgnoreSlot;
}
