using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;
using static CX.Alloy.TexLoadRead;

namespace CX.Alloy.Db;

/// <summary>The <b>master palette</b> -- one texture holding every PLT colour, built as <c>AurSetPalette</c>
/// @0x485190 builds it: one buffer of <c>(slot+1)*256</c> rows of <see cref="Width"/> RGBA entries, each palette
/// TGA flipped vertically and copied in at <c>slot &lt;&lt; 18</c>, so <b>a slot owns 256 rows whatever the
/// file's height</b>. A scheme entry is a GLOBAL row, <c>slot*256 + colour</c>, which the game layer computed.</summary>
internal static unsafe class PltPalettes
{
    /// <summary>Palette slots the engine declares, and the rows each one owns -- <c>slot &lt;&lt; 18</c>
    /// bytes at 256 entries of 4 bytes per row.</summary>
    internal const int Slots = 7, RowsPerSlot = 256, Width = 256, Bpp = 4;
    /// <summary>Rows of the whole buffer: <see cref="Slots"/> x <see cref="RowsPerSlot"/>, which is the 1792
    /// <c>ReplaceTexturePLT</c> divides by.</summary>
    internal const int Height = Slots * RowsPerSlot;
    internal const int RowBytes = Width * Bpp;
    /// <summary>The engine's own seven, in slot order. Only the names: whether a layer uses slot 2 or 3 is the
    /// game layer's business, and it tells us by the row it sets.</summary>
    internal static readonly string[] Default =
        { "pal_skin01", "pal_hair01", "pal_armor01", "pal_armor02", "pal_cloth01", "pal_leath01", "pal_tattoo01" };

    /// <summary>The master buffer, RGBA8, row-major, <see cref="Height"/> x <see cref="Width"/>. Zeroed, so
    /// the rows a palette file does not fill (176..255 of each slot, which the engine leaves as whatever
    /// <c>operator new[]</c> gave it) read black here instead of uninitialised heap.</summary>
    private static byte[] _master = Array.Empty<byte>();
    private static readonly bool[] _set = new bool[Slots];
    private static uint _tex;
    private static bool _dirty;
    private static ExtenderLog? _log;
    private static readonly Handles _handles = new();
    private static long _loads, _uploads, _refused;
    private static bool _loudNoRes;

    /// <summary>Texture unit the lit stage reads the master palette on. Past the 0..15 budget for
    /// <c>LocalShadowRaw</c>'s reason -- every unit below is spent in the lit bracket -- and declared with
    /// <c>layout(binding)</c> in <c>cx_plt.glsl</c>, so no bind-time <c>glUniform1i</c> can miss it.</summary>
    internal const uint Unit = TextureSlots.PltPalette;

    internal static bool Ready => _tex != 0;
    /// <summary>The master-palette texture's GL name, 0 until <see cref="Create"/> has made one. A UI draw
    /// that resolves a PLT itself binds it on a unit of its own rather than through <see cref="Bind"/>.</summary>
    internal static uint Texture => _tex;
    internal static ReadOnlySpan<byte> Master => _master;
    /// <summary>Has any slot been read? False means every PLT in the scene would resolve black, which is the
    /// one state this class must not reach quietly.</summary>
    internal static bool Loaded => _loaded > 0;
    private static int _loaded;
    private static bool _loudEmpty;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        if (_master.Length == 0) _master = new byte[Height * RowBytes];
    }

    /// <summary>The game's <c>AurSetPalette</c>: slot <paramref name="slot"/> takes the palette
    /// <paramref name="resref"/>. Game thread; reads the resource synchronously, as the engine does (seven
    /// 180 KiB TGAs at boot). False means the resource is missing, which is loud and counted -- a missing
    /// palette would otherwise be a silently black creature.</summary>
    internal static bool SetPalette(int slot, string resref)
    {
        if ((uint)slot >= Slots)
        {
            _refused++;
            _log?.Always($"PltPalettes: refuses slot {slot} (\"{resref}\") -- the engine declares {Slots}");
            return false;
        }
        if (_master.Length == 0) _master = new byte[Height * RowBytes];
        if (!ReadTga(resref, out byte[]? rgba, out int w, out int h, out string? why))
        {
            _refused++;
            _log?.Always($"PltPalettes: \"{resref}\" did not load into slot {slot} -- {why}. "
                       + "Every PLT layer whose rows fall in that slot resolves BLACK");
            return false;
        }
        Compose(_master, slot, rgba!, w, h);
        if (!_set[slot]) _loaded++;
        _set[slot] = true;
        _loads++;
        _dirty = true;
        return true;
    }

    /// <summary>Copy a decoded palette image into <paramref name="master"/> at <paramref name="slot"/>, flipped
    /// vertically -- <c>AurSetPalette</c>'s <c>ImageFlipY</c> then <c>ImageCopy</c> at <c>slot &lt;&lt; 18</c>.
    /// The shipped files are stored bottom-up (TGA descriptor 0x08), so after the flip master row 0 of a slot
    /// is the TOP row of the image. Pure: the off-device check calls it.</summary>
    internal static void Compose(Span<byte> master, int slot, ReadOnlySpan<byte> rgba, int w, int h)
    {
        int baseOff = slot * RowsPerSlot * RowBytes;
        Span<byte> dstSlot = master.Slice(baseOff, RowsPerSlot * RowBytes);
        // The whole slot first: a re-SetPalette with a shorter or narrower file must not leave the previous
        // one's rows behind. (Vanilla does -- ImageCopy is a flat memcpy into a buffer operator new[] never
        // zeroed, and the grow path copies the old contents forward.)
        dstSlot.Clear();
        // ImageCopy is a flat memcpy of w*h*bpp, not a row-by-row blit, so the destination stride IS the
        // source width. Only a 256-wide palette lands on the master's row boundaries, which every shipped
        // one is; a narrower file would shear in the engine exactly as it shears here.
        int rows = Math.Min(h, RowsPerSlot * RowBytes / Math.Max(w * Bpp, 1));
        int rowSrc = w * Bpp;
        for (int y = 0; y < rows; y++)
        {
            int src = (h - 1 - y) * rowSrc;                 // ImageFlipY: master row 0 is the LAST stored row
            int dst = y * rowSrc;
            if (dst + rowSrc > dstSlot.Length) break;
            rgba.Slice(src, rowSrc).CopyTo(dstSlot.Slice(dst, rowSrc));
        }
    }

    /// <summary>The engine's <c>AurCreatePaletteTexture</c>: one texture out of the buffer. Called again
    /// whenever a slot changed; the old name is dropped, because a bindless-handled texture is immutable and
    /// Alloy's samplers read this one by unit, not by handle.</summary>
    internal static void Create()
    {
        if (!_dirty && _tex != 0) return;
        if (_master.Length == 0) return;
        if (_loaded == 0)
        {
            if (!_loudEmpty)
            {
                _loudEmpty = true;
                _log?.Always("PltPalettes: asked to upload a master palette with NO slot loaded -- every PLT "
                           + "would resolve black. Refusing; PltPalettes.LoadDefaults has not run or every "
                           + "palette resource was missing (its own lines say which)");
            }
            return;
        }
        if (Gl.CreateTextures == null || Gl.TextureStorage2D == null) return;
        if (_tex != 0 && Gl.DeleteTextures != null) { uint old = _tex; Gl.DeleteTextures(1, &old); _tex = 0; }
        uint t; Gl.CreateTextures(Gl.TEXTURE_2D, 1, &t);
        if (t == 0) { _log?.Always("PltPalettes: glCreateTextures gave no name -- every PLT resolves BLACK"); return; }
        int svUnpack; Gl.GetIntegerv(Gl.PIXEL_UNPACK_BUFFER_BINDING, &svUnpack);
        if (svUnpack != 0) Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, 0);
        Gl.DrainErrors(8);
        Gl.TextureStorage2D(t, 1, Gl.RGBA8, Width, Height);
        Gl.TextureParameteri(t, Gl.TEXTURE_MAX_LEVEL, 0);
        // Fetched with texelFetch by (intensity, row): filtering across rows would mix two colour choices and
        // filtering across columns is what cx_plt.glsl's own taps do, over resolved colours.
        Gl.TextureParameteri(t, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
        Gl.TextureParameteri(t, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        fixed (byte* p = _master)
            Gl.TextureSubImage2D(t, 0, 0, 0, Width, Height, Gl.RGBA_, Gl.UNSIGNED_BYTE, p);
        uint err = Gl.GetError();
        if (svUnpack != 0) Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, (uint)svUnpack);
        if (err != 0)
        {
            if (Gl.DeleteTextures != null) Gl.DeleteTextures(1, &t);
            _log?.Always($"PltPalettes: the driver refused a {Width}x{Height} RGBA8 upload (0x{err:x}) -- every PLT resolves BLACK");
            return;
        }
        _tex = t;
        _dirty = false;
        _uploads++;
    }

    /// <summary>Bind the master palette for a pass that resolves PLTs. Nothing else uses the unit.</summary>
    internal static void Bind()
    {
        if (_tex != 0 && Gl.BindTextureUnit != null) Gl.BindTextureUnit(Unit, _tex);
    }

    /// <summary>Load the engine's seven defaults. Alloy mode calls this where the engine's own init does.
    /// Resource reads only -- the upload is <see cref="Create"/>'s, in Alloy's bracket, because a GL call
    /// from the game thread's context is not this class's to make.</summary>
    internal static void LoadDefaults()
    {
        for (int s = 0; s < Slots; s++) SetPalette(s, Default[s]);
        if (_loaded == 0 && !_loudEmpty)
        {
            _loudEmpty = true;
            _log?.Always("PltPalettes: not one of the seven palettes loaded -- every PLT resolves BLACK. The "
                       + "per-slot lines above say why");
        }
    }

    internal static void ContextLost() { _tex = 0; _dirty = true; }

    /// <summary>One palette TGA, decoded to RGBA8 in memory order (the engine's own TGA read swaps R and B,
    /// which <see cref="RasterReader.PrepareTga"/> reproduces). Game thread.</summary>
    private static bool ReadTga(string resref, out byte[]? rgba, out int w, out int h, out string? why)
    {
        rgba = null; w = h = 0;
        if (!ResLocate.Ready && !ResLocate.Attach(out why))
        {
            why ??= "the resource manager is not attached";
            if (!_loudNoRes)
            {
                _loudNoRes = true;
                _log?.Always($"PltPalettes: the resource manager is not attached ({why}) -- no palette can be "
                           + "read, so every PLT layer resolves BLACK until it is");
            }
            return false;
        }
        Span<byte> name = stackalloc byte[ShaderResource.ResRefBytes + 1];
        TexRegistry.ResRef(resref, name);
        uint* resId = stackalloc uint[ResLocate.ResIdWords];
        TexLocation loc;
        fixed (byte* p = name)
        {
            if (!ResLocate.Find(p, PaletteResType, out IntPtr table, out IntPtr entry))
            { why = "no TGA of that name in the resource tables"; return false; }
            if (!ResLocate.Locate(table, entry, resId, out loc, out why, out ResLocate.Source src))
            { why ??= $"the resource is {src}"; return false; }
        }
        if (!_handles.Read(loc, out ReadOnlySpan<byte> raw, out why)) return false;
        byte* px = RasterReader.PrepareTga(raw, out RasterReader.Raster r, out why);
        if (px == null) return false;
        try
        {
            if (r.Px != Bpp) { why = $"{r.Px} bytes/pixel, not {Bpp} -- a palette must carry alpha"; return false; }
            w = r.W; h = r.H;
            rgba = new byte[r.Bytes];
            new ReadOnlySpan<byte>(px, r.Bytes).CopyTo(rgba);
            return true;
        }
        finally { NativeMemory.Free(px); }
    }

    private const ushort PaletteResType = 3;   // not-an-offset: restype id (TGA)

    internal static string Note()
    {
        int set = 0;
        for (int s = 0; s < Slots; s++) if (_set[s]) set++;
        return $"pltpal: {set}/{Slots} slots ({_loads} loads, {_uploads} uploads"
             + (_refused == 0 ? "" : $", {_refused} REFUSED") + $"), master {Width}x{Height}"
             + (_tex == 0 ? " NOT UPLOADED" : "");
    }
}
