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

        // 5. V0 is the TOP of the screen: the decoder uploads row 0 first and the engine's quad puts it there.
        Span<Vector4> v = stackalloc Vector4[4];
        pad.Write(v);
        check.Expect(v[0].Y > 0f && v[0].W == pad.V0 && v[1].Y < 0f && v[1].W == pad.V1,
                     "the strip's first vertex is top-left at V0, so the frame is not drawn upside down");
        check.Expect(v[0].X == -pad.Sx && v[2].X == pad.Sx && v[3].X == pad.Sx && v[3].Y == -pad.Sy,
                     "...and the four vertices are a triangle strip, not a fan");

        log.Info(check.Ok
            ? "MovieService.SelfCheck: OK -- the letterbox fit matches ShowFrame's ortho at every GUI scale"
            : "MovieService.SelfCheck: FAILED (see above)");
    }
}
