using System;
using CX.Engine;

namespace CX.Alloy;

/// <summary>A material's numbered texture slot, as the engine resolves one. Every answer is a BORROWED
/// <c>CAurTexture*</c> that dies at the next area transition and at module unload: re-fetch it per frame,
/// compare it, never cache or dereference it past the call.</summary>
internal static class MatTextures
{
    /// <summary>Slots a plain <c>Material</c> owns itself (<c>BindAllStandardTextures</c> @0x4c6e30 stops at
    /// i &lt; 3); anything higher is real only in a SharedMaterial's table.</summary>
    private const int OwnSlots = 3;

    /// <summary>Slot <paramref name="slot"/>, live, as <c>Material::GetTexture</c> @0x4c5810 is the two loads
    /// it is -- the SharedMaterial's slot table when it holds one, else the Material's own slot. Null-safe on
    /// the receiver only, and BOUNDED: past its own three slots the engine hands back whatever field sits
    /// there (crashes 1789305736, 1789311470 dereferenced one as a texture), where this refuses.</summary>
    internal static IntPtr At(IntPtr mat, int slot)
    {
        if (mat == IntPtr.Zero) return IntPtr.Zero;
        var view = new Material(mat);
        SharedMaterial shared = view.Shared;
        if (!shared.IsNull)
        {
            IntPtr over = shared.Slot(slot).Ptr;
            if (over != IntPtr.Zero) return over;
        }
        return slot >= OwnSlots ? IntPtr.Zero : view.Slot(slot).Ptr;
    }

    /// <summary>A <c>.mtr</c> slot by its index, engine (<c>texture0..10</c>) or extender-owned (11+).</summary>
    internal static IntPtr Declared(IntPtr mat, int n)
    {
        if (n < MaterialExtraSlots.First) return At(mat, n);
        IntPtr shared = mat == IntPtr.Zero ? IntPtr.Zero : new Material(mat).Shared.Ptr;
        return MaterialExtraSlots.Texture(shared, n);
    }
}
