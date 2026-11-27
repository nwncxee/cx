using System;

namespace CX.Engine;

/// <summary>The engine's own 2DA cache, asked by name.</summary>
public readonly unsafe partial struct CTwoDimArrays
{
    /// <summary>The cached 2DA <paramref name="name"/> names, or zero when it loads none or the calls are unresolved; the
    /// name goes in as the <c>CExoString</c> the engine takes by value, which the Win64 callee destroys itself.
    /// The cache may evict the table later, so read it at once.</summary>
    public IntPtr Cached(ResRef name)
    {
        if (Ptr == IntPtr.Zero || EngineFn.TwoDimGetCached2DA == null || EngineFn.ExoStringInit == null || EngineFn.ExoStringDtor == null)
            return IntPtr.Zero;
        ExoStringRec exo = default;
        EngineFn.ExoStringInit((IntPtr)(&exo), (byte*)&name, uint.MaxValue);
        IntPtr table = EngineFn.TwoDimGetCached2DA(Ptr, (IntPtr)(&exo), 0);
#if WINDOWS
        exo = default;   // the Win64 callee destroyed its by-value CExoString
#endif
        EngineFn.ExoStringDtor((IntPtr)(&exo));
        return table;
    }
}
