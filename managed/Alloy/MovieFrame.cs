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
    /// Never replaced: a new object would leak <c>Rows</c> and forget a stand-down, so the one edge there is
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
        internal bool Stood, Said, SaidNoContext;
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
        if (s.Stood || rows == null || !Renderer.Movie.V) return;
        if (level != 0 || x != 0 || y != 0)
        { Stand($"the upload is level {level} at ({x},{y}) and not a whole frame"); return; }
        MovieRows r = MovieRows.Of(selector, extFormat, width, height, align);
        if (!r.Ok)
        {
            Stand($"selector {selector} / external format {extFormat} at {width}x{height}, unpack alignment "
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
            { Stand($"{bytes} bytes of staging for one {width}x{height} frame could not be allocated"); return; }
            s.Rows = next;
            s.Cap = bytes;
        }
        Buffer.MemoryCopy(rows, s.Rows, s.Cap, bytes);
        s.Staged = r;
    }

    /// <summary>The staged rows in an Alloy texture, uploaded in ALLOY's context -- the hook fires in the
    /// engine's. Returns the texture name, or 0, which means the engine's own quad draws this frame and the
    /// reason was said once.</summary>
    internal static uint Upload(int frameW, int frameH)
    {
        MovieFrameState s = _s;
        if (s.Stood || s.Log == null) return 0;
        if (s.Orig == null) return Stand("GLRender::DirectCreateSubTexture is not hooked");
        MovieRows r = s.Staged;
        if (!r.Ok) return 0;
        if (r.Width != frameW || r.Height != frameH)
            return Stand($"the staged frame is {r.Width}x{r.Height} and ShowFrame asked for {frameW}x{frameH}");
        if (!Gl.Init(s.Log)) return Stand("the GL entry points did not resolve");
        if (Gl.CreateTextures == null || Gl.TextureStorage2D == null || Gl.TextureSubImage2D == null
            || Gl.TextureParameteri == null || Gl.PixelStorei == null || Gl.DeleteTextures == null)
            return Stand("a direct-state-access texture entry point did not resolve");
        if (!GlContextService.Enter()) return NoContext();

        uint err;
        try
        {
            // Clear the 4.6 queue first, or an error some other pass left there would stand this one down.
            Gl.DrainErrors();
            if (!Ensure(s, frameW, frameH)) return Stand("the movie texture did not build");
            if (r.Align != UnpackDefault) Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, r.Align);
            Gl.TextureSubImage2D(s.Tex, 0, 0, 0, r.Width, r.Height, r.Format, r.Type, s.Rows);
            if (r.Align != UnpackDefault) Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, UnpackDefault);
            err = Gl.GetError != null ? Gl.GetError() : 0;
        }
        finally { GlContextService.Leave(); }
        if (err != 0) return Stand($"the upload raised GL error 0x{err:x4}");

        if (!s.Said)
        {
            s.Said = true;
            s.Log.Info($"MovieFrame: LIVE -- the movie frame comes from the decode, {r.Width}x{r.Height} rows at "
                     + $"pitch {r.Pitch} (format 0x{r.Format:x4}, type 0x{r.Type:x4}), staged and uploaded into an "
                     + "Alloy texture in Alloy's context; the engine's own movie texture is no longer sampled");
        }
        return s.Tex;
    }

    /// <summary>GL's default unpack alignment, and the one the engine restores after an upload that changed
    /// it: a call that asks for 4 needs no <c>glPixelStorei</c> at all.</summary>
    private const int UnpackDefault = 4;

    /// <summary>Alloy's movie texture at the decoded frame's exact size -- no power-of-two pad, which is why
    /// the plate's texture window is the whole texture. Immutable storage, so a size change recreates.</summary>
    private static bool Ensure(MovieFrameState s, int width, int height)
    {
        if (s.Tex != 0 && s.TexW == width && s.TexH == height) return true;
        if (s.Tex != 0) { uint dead = s.Tex; Gl.DeleteTextures(1, &dead); s.Tex = 0; }
        uint tex;
        Gl.CreateTextures(Gl.TEXTURE_2D, 1, &tex);
        if (tex == 0) return false;
        Gl.TextureStorage2D(tex, 1, Gl.RGBA8, width, height);
        // One level, so the default NEAREST_MIPMAP_LINEAR minification would leave the texture incomplete.
        Gl.TextureParameteri(tex, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
        Gl.TextureParameteri(tex, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.TextureParameteri(tex, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TextureParameteri(tex, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        s.Tex = tex; s.TexW = width; s.TexH = height;
        return true;
    }

    /// <summary>Our own mechanism failing is loud and hands the movie to the engine's own quad -- never a
    /// quiet fallback. Once, and for the session: a refusal re-failing twenty-four times a second behind one
    /// stale log line is not a diagnosis.</summary>
    private static uint Stand(string why)
    {
        MovieFrameState s = _s;
        if (s.Stood) return 0;
        s.Stood = true;
        s.Staged = default;
        s.Log?.Always($"MovieFrame: the decoded frame does not reach an Alloy texture -- {why}. The engine's own "
                    + "movie quad is drawing the movie (set alloy.movie 0 to make that the deliberate choice).");
        return 0;
    }

    /// <summary>Alloy's context is not the game thread's to enter -- it has not come up, it failed for the
    /// session, or the render thread holds it. Not this mechanism failing, so it is said once and not latched.</summary>
    private static uint NoContext()
    {
        MovieFrameState s = _s;
        if (s.SaidNoContext) return 0;
        s.SaidNoContext = true;
        s.Log?.Always("MovieFrame: Alloy's GL 4.6 context cannot be entered from the game thread -- the engine's "
                    + "own movie quad draws the movie until it can");
        return 0;
    }

    /// <summary>The 4.6 context went and the texture went with it: the name is dropped, not deleted, so the
    /// next frame rebuilds instead of drawing through a dead one.</summary>
    internal static void ContextLost() { _s.Tex = 0; _s.TexW = 0; _s.TexH = 0; }
}
