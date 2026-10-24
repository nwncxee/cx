using System.Numerics;
using System;
using CX.Framework;

namespace CX.Alloy;

public sealed unsafe partial class MovieService
{
    /// <summary>An independent transcription of <c>ShowFrame</c> @0x1165e30's own ortho, GUI-scale terms and
    /// all, so <see cref="MoviePlate.Fit"/> is checked against the engine rather than against itself. The
    /// engine's fixed [-1,1] quad inside <c>SetOrthoTransform(-right, right, -top, top, -1, 1)</c> has
    /// clip-space half-extent <c>1/right</c> by <c>1/top</c>.</summary>
    private static void EngineOrtho(int w, int h, int screenW, int screenH, float guiScale, out float right, out float top)
    {
        float sw = screenW, sh = screenH;
        float m = MathF.Max(w / sw * guiScale, h / sh * guiScale);
        top = sh / guiScale * m / h;
        right = m * (sw / guiScale) / w;
    }

    /// <summary>Off-device: the letterbox fit and the texture window. The fit is the whole of the ported
    /// arithmetic -- everything else in the draw is GL calls -- and a wrong one is a stretched movie.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        const float Eps = 1e-4f;

        // 1. Against the engine's own expression, at three GUI scales: the scale cancels, which is why it is
        //    not a term in Fit (and why Fit needs no engine call at all).
        (int w, int h, int sw, int sh)[] cases =
        [
            (1920, 1080, 1920, 1080),   // exact fit
            (640, 480, 1920, 1080),     // 4:3 in 16:9 -> pillarbox
            (854, 480, 1280, 1024),     // 16:9 in 5:4 -> letterbox
            (1280, 720, 800, 600),      // downscaled, still letterboxed
        ];
        foreach (var c in cases)
            foreach (float scale in new[] { 1f, 1.5f, 2.5f })
            {
                EngineOrtho(c.w, c.h, c.sw, c.sh, scale, out float right, out float top);
                MoviePlate p = MoviePlate.Fit(c.w, c.h, c.w, c.h, c.sw, c.sh);
                check.Expect(MathF.Abs(1f / right - p.Sx) < Eps && MathF.Abs(1f / top - p.Sy) < Eps,
                             $"the {c.w}x{c.h} plate in {c.sw}x{c.sh} matches the engine's ortho at GUI scale {scale} "
                           + $"(engine {1f / right:F4}x{1f / top:F4}, ours {p.Sx:F4}x{p.Sy:F4})");
            }

        // 2. The invariants the shape has to have whatever the numbers are.
        foreach (var c in cases)
        {
            MoviePlate p = MoviePlate.Fit(c.w, c.h, c.w, c.h, c.sw, c.sh);
            check.Expect(p.Sx > 0f && p.Sx <= 1f && p.Sy > 0f && p.Sy <= 1f, $"the {c.w}x{c.h} plate stays inside the screen");
            check.Expect(MathF.Abs(MathF.Max(p.Sx, p.Sy) - 1f) < Eps, $"the {c.w}x{c.h} plate touches the screen on its long axis");
            float drawn = p.Sx * c.sw / (p.Sy * c.sh);
            check.Expect(MathF.Abs(drawn - (float)c.w / c.h) < 1e-3f,
                         $"the {c.w}x{c.h} plate keeps its aspect on a {c.sw}x{c.sh} screen (drawn {drawn:F4})");
        }

        // 3. Which way round the bars go: 4:3 in 16:9 is pillarboxed (narrower), 16:9 in 5:4 letterboxed.
        MoviePlate pillar = MoviePlate.Fit(640, 480, 640, 480, 1920, 1080);
        check.Expect(pillar.Sx < 1f && MathF.Abs(pillar.Sy - 1f) < Eps, "a 4:3 movie on a 16:9 screen gets bars at the SIDES");
        MoviePlate letter = MoviePlate.Fit(854, 480, 854, 480, 1280, 1024);
        check.Expect(letter.Sy < 1f && MathF.Abs(letter.Sx - 1f) < Eps, "a 16:9 movie on a 5:4 screen gets bars top and bottom");

        // 4. The texture window: the decoded frame is the top-left corner of a padded texture, inset half a
        //    texel so the LINEAR fetch this port uses never reaches the pad (the engine's NEAREST could).
        MoviePlate pad = MoviePlate.Fit(854, 480, 1024, 512, 1920, 1080);
        check.Expect(MathF.Abs(pad.U0 - 0.5f / 1024f) < 1e-6f && MathF.Abs(pad.V0 - 0.5f / 512f) < 1e-6f,
                     "the texture window starts half a texel in");
        check.Expect(pad.U1 < 854f / 1024f && pad.U1 > 853f / 1024f && pad.V1 < 480f / 512f && pad.V1 > 479f / 512f,
                     "...and stops half a texel short of the decoded frame's edge, inside the pow2 pad");
        // ...and the window the LIVE path asks for, where Alloy's texture is the frame and there is no pad.
        MoviePlate whole = MoviePlate.Fit(854, 480, 854, 480, 1920, 1080);
        check.Expect(MathF.Abs(whole.U0 - 0.5f / 854f) < 1e-6f && MathF.Abs(whole.U1 - 853.5f / 854f) < 1e-6f
                     && MathF.Abs(whole.V0 - 0.5f / 480f) < 1e-6f && MathF.Abs(whole.V1 - 479.5f / 480f) < 1e-6f,
                     "an unpadded texture is sampled from half a texel in to half a texel short of its far edge");

        // 5. V0 is the TOP of the screen: the decoder uploads row 0 first and the engine's quad puts it there.
        Span<Vector4> v = stackalloc Vector4[4];
        pad.Write(v);
        check.Expect(v[0].Y > 0f && v[0].W == pad.V0 && v[1].Y < 0f && v[1].W == pad.V1,
                     "the strip's first vertex is top-left at V0, so the frame is not drawn upside down");
        check.Expect(v[0].X == -pad.Sx && v[2].X == pad.Sx && v[3].X == pad.Sx && v[3].Y == -pad.Sy,
                     "...and the four vertices are a triangle strip, not a fan");

        RowsCheck(check);

        log.Info(check.Ok
            ? "MovieService.SelfCheck: OK -- the letterbox fit matches ShowFrame's ortho at every GUI scale, and "
            + "one upload's format pair and row pitch match the engine's own translation"
            : "MovieService.SelfCheck: FAILED (see above)");
    }

    /// <summary>Off-device: what one <c>DirectCreateSubTexture</c> call means. The expected values are
    /// hand-written literals read out of <c>GLRender::DirectCreateSubTexture</c> @0x4c9ef0 and
    /// <c>textureFormatTranslationTable</c> @0x12f5080, so <see cref="MovieRows"/> is checked against the
    /// engine's numbers and not against its own.</summary>
    private static void RowsCheck(Checker check)
    {
        // 1. The pairs the movie's upload passes: RGBA (additional-data track), RGB (main), 565 (16-bit).
        (int Sel, int Ext, uint Format, uint Type, int Bpp, string What)[] pairs =
        [
            (0, 0, 0x1908, 0x1401, 4, "selector 0 / format 0 is GL_RGBA + GL_UNSIGNED_BYTE at 4 bytes"),
            (1, 1, 0x1907, 0x1401, 3, "selector 1 / format 1 is GL_RGB + GL_UNSIGNED_BYTE at 3 bytes"),
            (9, 0, 0x1907, 0x8363, 2, "selector 9 is GL_RGB + GL_UNSIGNED_SHORT_5_6_5 at 2 bytes, whatever the format index"),
        ];
        foreach (var p in pairs)
        {
            MovieRows r = MovieRows.Of(p.Sel, p.Ext, 320, 240, 4);
            check.Expect(r.Ok && r.Format == p.Format && r.Type == p.Type && r.Bpp == p.Bpp,
                         $"{p.What} (got ok={r.Ok} format=0x{r.Format:x4} type=0x{r.Type:x4} bpp={r.Bpp})");
        }

        // 2. The shapes that are refused rather than transcribed, and the arguments GL itself would reject.
        (int Sel, int Ext, int W, int H, int Align, string What)[] refused =
        [
            (10, 0, 320, 240, 4, "the depth/stencil selector"),
            (11, 0, 320, 240, 4, "the depth selector"),
            (16, 0, 320, 240, 4, "the RGB-float selector"),
            (17, 0, 320, 240, 4, "the RED-float selector"),
            (0, 2, 320, 240, 4, "a format index past the two the movie passes"),
            (0, -1, 320, 240, 4, "a negative format index"),
            (0, 0, 0, 240, 4, "a zero-width frame"),
            (0, 0, 320, -1, 4, "a negative height"),
            (0, 0, 320, 240, 3, "an unpack alignment GL has no rule for"),
            (0, 0, 320, 240, 0, "a zero unpack alignment"),
        ];
        foreach (var f in refused)
            check.Expect(!MovieRows.Of(f.Sel, f.Ext, f.W, f.H, f.Align).Ok, $"{f.What} is refused");

        // 3. GL rounds each row up to the alignment and reads the LAST row only to its width -- where a copy overruns.
        (int Sel, int Ext, int W, int H, int Align, int Pitch, long Bytes, string What)[] rows =
        [
            (0, 0, 640, 480, 4, 2560, 2560L * 479 + 2560, "RGBA at 640 wide needs no padding"),
            (1, 1, 640, 480, 4, 1920, 1920L * 479 + 1920, "RGB at 640 wide is already 4-aligned"),
            (1, 1, 854, 480, 4, 2564, 2564L * 479 + 2562, "RGB at 854 wide pads each row by 2 bytes, bar the last"),
            (1, 1, 853, 2, 4, 2560, 2560L + 2559, "RGB at 853 wide pads by 1 byte, and two rows read pitch + width"),
            (1, 1, 853, 1, 4, 2560, 2559, "...and a single row reads its width alone"),
            (1, 1, 11, 4, 1, 33, 33L * 4, "alignment 1 pads nothing"),
            (9, 0, 854, 480, 4, 1708, 1708L * 479 + 1708, "565 at 854 wide is 2 bytes a pixel, already aligned"),
        ];
        foreach (var r in rows)
        {
            MovieRows m = MovieRows.Of(r.Sel, r.Ext, r.W, r.H, r.Align);
            check.Expect(m.Pitch == r.Pitch && m.Bytes == r.Bytes,
                         $"{r.What} (expected pitch {r.Pitch} bytes {r.Bytes}, got {m.Pitch}/{m.Bytes})");
        }

        // 4. A refused shape measures nothing: a pitch off a Bpp of 0 would divide the frame into no rows.
        MovieRows no = MovieRows.Of(10, 0, 320, 240, 4);
        check.Expect(no.Pitch == 0 && no.Bytes == 0, "a refused upload has no pitch and no size");
    }
}
