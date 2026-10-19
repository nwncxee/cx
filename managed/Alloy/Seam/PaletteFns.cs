using System;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>The object-free <c>Aur*</c> palette setters, routed onto CX's own palette store
/// (<c>docs/alloy-db-texture-mode.md</c>). They take no engine object, so there is nothing to facade -- the
/// seam answers them with CX's state instead of the engine's GL texture.</summary>
public sealed unsafe class PaletteFns : ISeamProvider
{
    private static long _setFailures;

    public void Register(SeamRegistry r)
    {
        r.ReplaceEngine(SymbolMap.AurSetPalette, (void*)(delegate* unmanaged<int, nint, int>)&AurSetPalette);
        r.ReplaceEngine(SymbolMap.AurClearPalette, (void*)(delegate* unmanaged<void>)&AurClearPalette);
        r.ReplaceEngine(SymbolMap.AurCreatePaletteTexture, (void*)(delegate* unmanaged<void>)&AurCreatePaletteTexture);
    }

    /// <summary><c>AurSetPalette(int slot, char const* resref)</c> @0x485190 -- returns int. Synchronous on the
    /// game thread: the palette rows go into a CPU buffer and the texture is built later, in the bracket.</summary>
    [UnmanagedCallersOnly]
    private static int AurSetPalette(int slot, nint resref)
    {
        string name = resref == 0 ? "" : Marshal.PtrToStringUTF8(resref) ?? "";
        if (PltPalettes.SetPalette(slot, name)) return 1;
        _setFailures++;
        if ((_setFailures & (_setFailures - 1)) == 0)
            Facade.Log?.Always($"seam: AurSetPalette({slot}, \"{name}\") could not be honoured ({_setFailures} so far)");
        return 0;
    }

    /// <summary><c>AurClearPalette()</c> @0x485090: drop the palette texture. The CPU rows survive, and the
    /// next <c>SetPalette</c> rebuilds it -- which is what the engine's own pairing does.</summary>
    [UnmanagedCallersOnly]
    private static void AurClearPalette() => PltPalettes.ContextLost();

    /// <summary><c>AurCreatePaletteTexture()</c> @0x4874d0: the engine built a GL texture inline. CX's build
    /// needs Alloy's bracket, where <c>PltPalettes.Create</c> already runs every frame the palette is dirty,
    /// so the call has nothing left to do here.</summary>
    [UnmanagedCallersOnly]
    private static void AurCreatePaletteTexture() { }
}
