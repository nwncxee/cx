using System;
using System.Runtime.InteropServices;
using CX.Alloy;
using CX.Alloy.Db;
using CX.Framework;
using CX.Ui;
using static CX.Alloy.TexLoadRead;

namespace CX.Services.NuiItems;

/// <summary>The engine half: the skin and its default font read from the game's resources, a server's own over NWSync
/// first, and the font declared to the text rig under <see cref="FaceName"/>. Game thread, once per session.</summary>
internal static unsafe class NuiSkinFontLoad
{
    /// <summary>The name the skin's default font is declared under.</summary>
    internal const string FaceName = "nuiskin";
    /// <summary>The skin's resref, and the restypes of a <c>.tml</c> and a <c>.ttf</c>, read from <c>nwn_base.key</c>.</summary>
    private const string SkinResRef = "nui_skin";
    private const ushort TmlResType = 2076, TtfResType = 2074;

    private sealed class LoadState
    {
        public bool Tried;
        public float Px, Spacing;
        public bool Snap;
    }

    private static readonly LoadState _s = new();
    private static readonly Handles _handles = new();

    /// <summary>The skin's default font's extra px between glyphs at UI scale 1, once <see cref="Ensure"/> has read it.</summary>
    internal static float Spacing => _s.Spacing;
    /// <summary>Whether the skin draws its default font on whole pixels, once <see cref="Ensure"/> has read it.</summary>
    internal static bool Snap => _s.Snap;

    /// <summary>The skin's default font size in px at UI scale 1, with the font declared under <see cref="FaceName"/>; 0 when
    /// either could not be read, which <paramref name="log"/> says once.</summary>
    internal static float Ensure(ExtenderLog? log)
    {
        if (_s.Tried) return _s.Px;
        _s.Tried = true;
        if (!Read(SkinResRef, TmlResType, out byte[]? skin, out string? why))
        {
            log?.Always($"NuiSkinFont: the NUI skin could not be read ({why}) -- the item menu keeps the UI font");
            return 0f;
        }
        string text = System.Text.Encoding.UTF8.GetString(skin!);
        NuiSkinFont.Text(text, out _s.Spacing, out _s.Snap);
        if (!NuiSkinFont.Parse(text, out string resref, out float px))
        {
            log?.Always("NuiSkinFont: the NUI skin names no default font with a size -- the item menu keeps the UI font");
            return 0f;
        }
        if (!Read(resref, TtfResType, out byte[]? ttf, out why))
        {
            log?.Always($"NuiSkinFont: the skin's default font {resref} could not be read ({why}) -- the item menu keeps the UI font");
            return 0f;
        }
        FontRegistry.Declare(FaceName, ttf!, $"{resref}.ttf, the NUI skin's default font");
        _s.Px = px;
        return px;
    }

    /// <summary>One resource's bytes, from the file that holds it or from the NWSync store.</summary>
    private static bool Read(string resref, ushort resType, out byte[]? bytes, out string? why)
    {
        bytes = null;
        if (!ResLocate.Ready && !ResLocate.Attach(out why)) { why ??= "the resource manager is not attached"; return false; }
        Span<byte> name = stackalloc byte[ShaderResource.ResRefBytes + 1];
        TexRegistry.ResRef(resref, name);
        uint* resId = stackalloc uint[ResLocate.ResIdWords];
        TexLocation loc;
        fixed (byte* p = name)
        {
            if (!ResLocate.Find(p, resType, out IntPtr table, out IntPtr entry)) { why = "no such resource"; return false; }
            if (!ResLocate.Locate(table, entry, resId, out loc, out why, out ResLocate.Source src)) { why ??= $"the resource is {src}"; return false; }
        }
        if (loc.Kind != KindNwSync)
        {
            if (!_handles.Read(loc, out ReadOnlySpan<byte> raw, out why)) return false;
            bytes = raw.ToArray();
            return true;
        }
        Span<byte> key = stackalloc byte[loc.Path.Length];
        System.Text.Encoding.ASCII.GetBytes(loc.Path, key);
        if (!NwSyncReader.TryRead(key, loc.ResType, out byte* data, out int size)) { why = "NWSync has no row for it"; return false; }
        try
        {
            bytes = new byte[size];
            Marshal.Copy((IntPtr)data, bytes, 0, size);
        }
        finally { NativeMemory.Free(data); }
        why = null;
        return true;
    }
}
