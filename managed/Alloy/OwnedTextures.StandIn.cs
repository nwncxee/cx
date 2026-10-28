using System;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The two one-texel stand-ins an owned draw falls back on: the white texel a diffuse with no texture
/// id draws as vanilla draws it, and the transparent-black texel an empty bindless map slot points at.</summary>
internal static unsafe partial class OwnedTextures
{
    /// <summary>The one opaque white texel a part draws on when its diffuse resolves to texture id 0. Vanilla
    /// parity: <c>AurTextureGetTID</c> @0x48e080 returns 0 for the engine's shared <c>m_pNullTexture</c> and
    /// the draw is still issued, so the part renders blank white where Alloy's own skip made it invisible.
    /// Minted lazily at the frame's own point and registered OWNED; 0 out means the caller keeps its skip.</summary>
    internal static int WhiteTid()
    {
        if (_standIn.White != 0) return (int)_standIn.White;
        uint white = 0xFFFFFFFF;
        uint t = VkTextures.NewName();
        if (t == 0) return 0;
        MirrorSolid(t, 1, 1, white, NearestSampler);
        if (!VkTextures.Has(t)) return 0;
        _owned.Add(t);
        _standIn.White = t;
        _log?.Info($"OwnedTextures: minted the white null-texture stand-in (tid {t}) -- a diffuse with no texture id now draws as vanilla draws it");
        return (int)t;
    }

    /// <summary>The one transparent-black texel every empty map slot's bindless handle points at: what NVIDIA
    /// and Mesa return for a null handle, which AMD's driver faults on instead. Minted lazily at the frame's
    /// own point and registered OWNED; 0 out means the caller uploads the zero handle as before.</summary>
    internal static int NullTid()
    {
        if (_standIn.Black != 0) return (int)_standIn.Black;
        uint t = VkTextures.NewName();
        if (t == 0) return 0;
        MirrorSolid(t, 1, 1, 0, NearestSampler);
        if (!VkTextures.Has(t)) return 0;
        _owned.Add(t);
        _standIn.Black = t;
        return (int)t;
    }

    /// <summary>The <see cref="WhiteTid"/> and <see cref="NullTid"/> names, 0 until minted. Dropped (never
    /// deleted) on <see cref="ContextLost"/> with every other name, and re-minted on the next draw that needs
    /// one.</summary>
    private sealed class StandInState { public uint White, Black; }
    private static StandInState _standIn = new();

    /// <summary>The white stand-in's name, 0 until minted; a read, never a mint (stage workers call it).</summary>
    internal static int WhiteName => (int)_standIn.White;
}
