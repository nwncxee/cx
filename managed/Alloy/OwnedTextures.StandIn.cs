using System;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The two one-texel stand-ins an owned draw falls back on: the white texel a diffuse with no GL id
/// draws as vanilla draws it, and the transparent-black texel an empty bindless map slot points at.</summary>
internal static unsafe partial class OwnedTextures
{
    /// <summary>The one opaque white texel a part draws on when its diffuse resolves to GL id 0. Vanilla
    /// parity: <c>AurTextureGetTID</c> @0x48e080 returns 0 for the engine's shared <c>m_pNullTexture</c> and
    /// the draw is still issued, so the part renders blank white where Alloy's own skip made it invisible.
    /// Minted lazily inside the 4.6 bracket and registered OWNED; 0 out means the caller keeps its skip.</summary>
    internal static int WhiteTid()
    {
        if (_standIn.White != 0) return (int)_standIn.White;
        if (!GlContextService.Available) return 0;
        uint t;
        Gl.CreateTextures(Gl.TEXTURE_2D, 1, &t);
        if (t == 0) return 0;
        uint white = 0xFFFFFFFF;
        Gl.TextureStorage2D(t, 1, Gl.RGBA8, 1, 1);
        Gl.TextureSubImage2D(t, 0, 0, 0, 1, 1, Gl.RGBA, Gl.UNSIGNED_BYTE, &white);
        Gl.TextureParameteri(t, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
        Gl.TextureParameteri(t, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        _owned.Add(t);
        _standIn.White = t;
        MirrorSolid(t, 1, 1, white, NearestSampler);
        _log?.Info($"OwnedTextures: minted the white null-texture stand-in (tid {t}) -- a diffuse with no GL id now draws as vanilla draws it");
        return (int)t;
    }

    /// <summary>The one transparent-black texel every empty map slot's bindless handle points at: what NVIDIA
    /// and Mesa return for a null handle, which AMD's driver faults on instead. Minted lazily inside the 4.6
    /// bracket and registered OWNED; 0 out means the caller uploads the zero handle as before.</summary>
    internal static int NullTid()
    {
        if (_standIn.Black != 0) return (int)_standIn.Black;
        if (!GlContextService.Available) return 0;
        uint t;
        Gl.CreateTextures(Gl.TEXTURE_2D, 1, &t);
        if (t == 0) return 0;
        uint zero = 0;
        Gl.TextureStorage2D(t, 1, Gl.RGBA8, 1, 1);
        Gl.TextureSubImage2D(t, 0, 0, 0, 1, 1, Gl.RGBA, Gl.UNSIGNED_BYTE, &zero);
        Gl.TextureParameteri(t, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
        Gl.TextureParameteri(t, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        _owned.Add(t);
        _standIn.Black = t;
        MirrorSolid(t, 1, 1, zero, NearestSampler);
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
