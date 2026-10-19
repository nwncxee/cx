using System;
using CX.Engine;

namespace CX.Alloy;

/// <summary>A <c>.mtr</c> texture slot by its declared index, whichever side of the engine owns it. Every
/// answer is a BORROWED <c>CAurTexture*</c> that dies at the next area transition and at module unload:
/// re-fetch it per frame, compare it, never cache or dereference it past the call.</summary>
internal static class MatTextures
{
    /// <summary>Slot <paramref name="n"/> of <paramref name="mat"/>: the engine's own (<c>texture0..10</c>,
    /// through <see cref="Material.TextureAt"/>) or an extender-owned one (11+). Null-safe on the receiver;
    /// zero for a slot nothing fills.</summary>
    internal static IntPtr Declared(IntPtr mat, int n)
    {
        if (mat == IntPtr.Zero) return IntPtr.Zero;
        var view = new Material(mat);
        return n < MaterialExtraSlots.First
             ? view.TextureAt(n).Ptr
             : MaterialExtraSlots.Texture(view.Shared.Ptr, n);
    }
}
