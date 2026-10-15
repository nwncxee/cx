using System;

namespace CX.Engine;

public readonly unsafe partial struct CExoKeyTable
{
    /// <summary>The <c>CExoResFile*</c> for BIF <paramref name="bif"/>; the file array is sized to the table's
    /// BIF count, which the key file bounds.</summary>
    public IntPtr FileAt(uint bif) => ((IntPtr*)Files)[bif];
}

public readonly unsafe partial struct CAurBehavior
{
    /// <summary>The dependency pointers, as many as <see cref="DepCount"/> says now; each points at a
    /// <c>SafePointer</c> whose first word is the dependency.</summary>
    public Span<IntPtr> DepList => new((void*)Deps, DepCount);
}
