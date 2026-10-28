using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>What one <c>GLRender::DirectCreateSubTexture</c> upload means to GL: the external format and
/// type the selector pair picks, and the host row layout its unpack alignment implies. Pure arithmetic over
/// the call's own arguments, so it is checkable off-device (<see cref="MovieService.SelfCheck"/>).</summary>
internal readonly struct MovieRows
{
    /// <summary>The external format and type <c>glTexSubImage2D</c> is handed, and the bytes one pixel takes
    /// in the host rows. <see cref="Bpp"/> 0 is the refusal: a shape the movie funnel does not pass.</summary>
    internal readonly uint Format, Type;
    /// <inheritdoc cref="Format"/>
    internal readonly int Bpp;
    /// <summary>The sub-rectangle's size and the call's unpack alignment, kept so the upload and the plate
    /// read the same numbers the decode handed over.</summary>
    internal readonly int Width, Height, Align;

    private MovieRows(uint format, uint type, int bpp, int width, int height, int align)
    { Format = format; Type = type; Bpp = bpp; Width = width; Height = height; Align = align; }

    /// <summary>A shape this port reads. False hands the frame to the engine's own quad.</summary>
    internal bool Ok => Bpp > 0;

    /// <summary>Bytes from the start of one host row to the next: GL rounds a row up to the unpack
    /// alignment, which is what the pointer MEANS to the upload the engine makes from it.</summary>
    internal int Pitch => Ok ? (Width * Bpp + Align - 1) / Align * Align : 0;

    /// <summary>Bytes GL actually reads: whole rows but the last, which it reads only to its width. The last
    /// row's alignment padding need not exist, so a copy must not reach for it.</summary>
    internal long Bytes => Ok ? (long)Pitch * (Height - 1) + (long)Width * Bpp : 0;

    /// <summary>The host rows <paramref name="src"/> (this shape's pitch) as tight RGBA8 in <paramref name="o"/>, alpha
    /// opaque: 3- and 4-byte pixels copied, 565 expanded to the full byte range.</summary>
    internal void Widen(ReadOnlySpan<byte> src, Span<byte> o)
    {
        for (int y = 0; y < Height; y++)
        {
            ReadOnlySpan<byte> row = src.Slice(y * Pitch, Width * Bpp);
            int d = y * Width * 4;
            for (int x = 0; x < Width; x++, d += 4)
            {
                if (Bpp == 4) { o[d] = row[x * 4]; o[d + 1] = row[x * 4 + 1]; o[d + 2] = row[x * 4 + 2]; }
                else if (Bpp == 3) { o[d] = row[x * 3]; o[d + 1] = row[x * 3 + 1]; o[d + 2] = row[x * 3 + 2]; }
                else
                {
                    int v = row[x * 2] | row[x * 2 + 1] << 8;
                    o[d] = (byte)((v >> 11) * 255 / 31); o[d + 1] = (byte)((v >> 5 & 63) * 255 / 63); o[d + 2] = (byte)((v & 31) * 255 / 31);
                }
                o[d + 3] = 255;
            }
        }
    }

    /// <summary>Selector values that pick a depth or float pair instead of the byte path. Declined rather
    /// than transcribed: the movie funnel passes 0, 1 or 9, and nothing else reaches it.</summary>
    private static bool NotMovie(int selector) => selector is 10 or 11 or 16 or 17;

    /// <summary>The engine's own translation, transcribed from <c>GLRender::DirectCreateSubTexture</c>
    /// @0x4c9ef0 and the first two entries of <c>textureFormatTranslationTable</c> @0x12f5080: selector 9 is
    /// the 16-bit 565 pair, every other selector takes bytes with the format at <c>extFormat</c>.</summary>
    internal static MovieRows Of(int selector, int extFormat, int width, int height, int align)
    {
        if (width <= 0 || height <= 0) return default;
        if (align != 1 && align != 2 && align != 4 && align != 8) return default;
        if (selector == Rgb565Selector) return new MovieRows(Gl.RGB, Gl.UNSIGNED_SHORT_5_6_5, 2, width, height, align);
        if (NotMovie(selector)) return default;
        return extFormat switch
        {
            0 => new MovieRows(Gl.RGBA, Gl.UNSIGNED_BYTE, 4, width, height, align),
            1 => new MovieRows(Gl.RGB, Gl.UNSIGNED_BYTE, 3, width, height, align),
            _ => default,
        };
    }

    /// <summary>The one selector that names its own type instead of taking bytes: the movie's 16-bit
    /// colour-depth branch, where <c>RenderInterface::GetColorDepth</c> @0x12ec1d0 did not answer 32.</summary>
    private const int Rgb565Selector = 9;
}

/// <summary>The movie frame as the DECODER hands it over, not as the engine's texture. One hook, on the
/// upload funnel, stages the rows to host memory; <see cref="Upload"/> puts them in an Alloy texture inside
/// Alloy's own context. The engine's upload still runs, so the hand-back quad keeps its picture.</summary>
internal static unsafe class MovieFrame
{
    /// <summary>Everything one session's movie frames own: the staging buffer (kept at one frame's rows for
    /// the session -- no notice of a movie ending reaches here), Alloy's texture, and the refusal latches.
    /// Never replaced: a new object would leak <c>Rows</c> and forget a refusal, so the one edge there is
    /// (<see cref="ContextLost"/>) drops the texture name alone.</summary>
    private sealed class MovieFrameState
    {
        internal ExtenderLog? Log;
        internal GlDirectCreateSubTextureFn Orig;
        internal byte* Rows;
        internal nuint Cap;
        internal MovieRows Staged;
        internal uint Tex;
        internal int TexW, TexH;
        internal bool Refused, SaidLive;
        /// <summary>The staged frame widened to RGBA8 for Vulkan, which takes no 3-byte or 565 image to sample.</summary>
        internal byte[] Rgba = [];
        internal bool SaidLiveVk;
    }

    private static MovieFrameState _s = new();

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _s.Log = log;
        _s.Orig = hooks.InstallGlDirectCreateSubTexture(&OnSubTexture);
        if (_s.Orig == null)
            log.Always("MovieFrame: GLRender::DirectCreateSubTexture did not hook -- the decoded frame never "
                     + "reaches Alloy and the engine's own movie quad draws every frame");
    }

    /// <summary>The decode's one upload. Stage first, then the engine's own call: its texture is the picture
    /// the hand-back path draws, so it is filled whether or not Alloy takes this frame.</summary>
    [UnmanagedCallersOnly]
    private static void OnSubTexture(int level, int selector, int width, int height, int extFormat,
                                     void* rows, int x, int y, int align)
    {
        Stage(level, selector, width, height, extFormat, rows, x, y, align);
        GlDirectCreateSubTextureFn orig = _s.Orig;
        if (orig != null) orig(level, selector, width, height, extFormat, rows, x, y, align);
    }

    /// <summary>Copy the rows GL would read out of the engine's buffer. Allocates only when the frame size
    /// grows, which is once per movie: the copy itself is a <c>memcpy</c> into a buffer already there.</summary>
    private static void Stage(int level, int selector, int width, int height, int extFormat,
                             void* rows, int x, int y, int align)
    {
        MovieFrameState s = _s;
        s.Staged = default;
        if (s.Refused || rows == null || !Renderer.Movie.V) return;
        if (level != 0 || x != 0 || y != 0)
        { Refuse($"the upload is level {level} at ({x},{y}) and not a whole frame"); return; }
        MovieRows r = MovieRows.Of(selector, extFormat, width, height, align);
        if (!r.Ok)
        {
            Refuse($"selector {selector} / external format {extFormat} at {width}x{height}, unpack alignment "
                + $"{align}, is not a frame shape this port reads");
            return;
        }
        nuint bytes = (nuint)r.Bytes;
        if (s.Rows == null || s.Cap < bytes)
        {
            byte* next;
            try { next = (byte*)NativeMemory.Realloc(s.Rows, bytes); }
            catch (OutOfMemoryException) { next = null; }
            if (next == null)
            { Refuse($"{bytes} bytes of staging for one {width}x{height} frame could not be allocated"); return; }
            s.Rows = next;
            s.Cap = bytes;
        }
        Buffer.MemoryCopy(rows, s.Rows, s.Cap, bytes);
        s.Staged = r;
    }

    /// <summary>The staged rows as tight RGBA8 for a Vulkan upload, the same refusals as <see cref="Upload"/>; empty
    /// means the engine's own quad draws this frame and the reason was said once. Allocates only when the frame grows.</summary>
    internal static ReadOnlySpan<byte> RgbaVk(int frameW, int frameH)
    {
        MovieFrameState s = _s;
        if (s.Refused || s.Log == null) return default;
        if (s.Orig == null) { Refuse("GLRender::DirectCreateSubTexture is not hooked"); return default; }
        MovieRows r = s.Staged;
        if (!r.Ok) return default;
        if (r.Width != frameW || r.Height != frameH)
        { Refuse($"the staged frame is {r.Width}x{r.Height} and ShowFrame asked for {frameW}x{frameH}"); return default; }
        int n = r.Width * r.Height;
        if (s.Rgba.Length < n * 4) s.Rgba = new byte[n * 4];
        byte[] o = s.Rgba;
        r.Widen(new ReadOnlySpan<byte>(s.Rows, (int)r.Bytes), o);
        if (!s.SaidLiveVk)
        {
            s.SaidLiveVk = true;
            s.Log.Info($"MovieFrame: LIVE on Vulkan -- the movie frame comes from the decode, {r.Width}x{r.Height} rows at pitch "
                     + $"{r.Pitch}, {r.Bpp} byte(s) a pixel widened to RGBA8 and uploaded into a Vulkan image");
        }
        return new ReadOnlySpan<byte>(o, 0, n * 4);
    }

    /// <summary>Our own mechanism failing is loud and hands the movie to the engine's own quad -- never a
    /// quiet fallback. Once, and for the session: a refusal re-failing twenty-four times a second behind one
    /// stale log line is not a diagnosis.</summary>
    private static uint Refuse(string why)
    {
        MovieFrameState s = _s;
        if (s.Refused) return 0;
        s.Refused = true;
        s.Staged = default;
        s.Log?.Always($"MovieFrame: the decoded frame does not reach an Alloy texture -- {why}. The engine's own "
                    + "movie quad is drawing the movie (set alloy.movie 0 to make that the deliberate choice).");
        return 0;
    }

}
