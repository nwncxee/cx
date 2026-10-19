using System;
using CX.Framework;

namespace CX.Engine;

/// <summary>A material's texture slots are an inline array of <c>SafePointer</c> records (the pointer, then a
/// refcount int), <see cref="EngineLayout.MatSlotStride"/> apart -- an INDEX into an inline array, which no
/// field row describes, so the accessor is a concept on the view.</summary>
public readonly unsafe ref partial struct Material
{
    /// <summary>Slot <paramref name="i"/> of the Material's OWN table, which starts at the object itself (the
    /// constructor writes 0x00/0x10/../0x60). Unbounded, so it is banned: the one legal reader is
    /// <see cref="TextureAt"/>, which carries the bound the engine itself lacks.</summary>
    public CAurTexture Slot(int i) => new(*(IntPtr*)(Ptr + i * EngineLayout.MatSlotStride));

    /// <summary>Slots a plain <c>Material</c> owns itself (<c>BindAllStandardTextures</c> @0x4c6e30 stops at
    /// i &lt; 3); anything higher is real only in a SharedMaterial's table.</summary>
    private const int OwnSlots = 3;

    /// <summary>Texture slot <paramref name="slot"/>, as <c>Material::GetTexture</c> @0x4c5810 is the two
    /// loads it is: the SharedMaterial's table where it holds one, else this Material's own slot. BOUNDED
    /// where the engine is not -- past <see cref="OwnSlots"/> the engine hands back whatever field sits
    /// there, which is no texture. Null-safe, and the answer is BORROWED: re-fetch it, never cache it.</summary>
    public CAurTexture TextureAt(int slot)
    {
        if (IsNull) return default;
        SharedMaterial shared = Shared;
#pragma warning disable RS0030 // Material.TextureAt is the one bounded reader of the raw slot tables
        if (!shared.IsNull)
        {
            CAurTexture over = shared.Slot(slot);
            if (!over.IsNull) return over;
        }
        return slot >= OwnSlots ? default : Slot(slot);
#pragma warning restore RS0030
    }
}

/// <inheritdoc cref="Material"/>
public readonly unsafe ref partial struct SharedMaterial
{
    /// <summary>Slot <paramref name="i"/> of the SharedMaterial's own eleven, which begin at
    /// <see cref="EngineLayout.SharedSlot0"/> and win over the Material's three. Banned like the Material's
    /// own table, and for the same reason: <see cref="Material.TextureAt"/> is the reader.</summary>
    public CAurTexture Slot(int i) => new(*(IntPtr*)(Ptr + EngineLayout.SharedSlot0 + i * EngineLayout.MatSlotStride));
}
