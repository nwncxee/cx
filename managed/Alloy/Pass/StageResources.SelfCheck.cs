using System.Text;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

internal static unsafe partial class StageResources
{
    /// <summary>Off-device: the declaration grammar, the dispatch and gap arithmetic, the resref rules and the two
    /// wrapper texts. Oracle: the declaration lines and GL enum literals written out below by hand, and the embedded
    /// GLSL as a second artifact. <c>check-shaders.py</c> reads these same literals out of this file and runs its own
    /// parser over them, so the two implementations cannot drift.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "StageResources.SelfCheck");
        Grammar(check);
        Budgets(check);
        Wrappers(check);
        Swaps(check);
        Overlay(check);
        // 129 texels over a group of 8 is 17 groups: the last one runs for a single column. Hand-written.
        check.Expect(Groups(129, 8) == 17 && Groups(128, 8) == 16 && Groups(1, 64) == 1 && Groups(32, 32) == 1,
          "a dispatch rounds its extent UP to whole work groups");
        // "cx_" + a 13-character stage is exactly a 16-byte resref; one character more cannot be a file.
        check.Expect(Fits("cxdemo_sky") && Fits("thirteenchars") && !Fits("fourteenchars_"),
          "a native stage's resref is cx_ + the stage, and it has to fit a resref");
        // A stage called sky_fs would name cx_sky_fs -- Alloy's OWN sky wrapper, so an override of it and not a native
        // stage. Same for a library name.
        check.Expect(Reserved(SkyStage.ShdFs) && Reserved(ShdPassCs) && Reserved("cx_stageenv")
                  && !Reserved("cx_cxdemo_sky") && !Reserved("cx_fst_sky") && !Reserved("cx_rambo"),
          "an Alloy stage or library resref is refused as a native stage, and a content one is not");
        // A frame that does not follow the last one is a gap, including the first ever.
        check.Expect(Gap(int.MinValue, 0) && Gap(7, 20) && !Gap(7, 8),
          "history is one frame old only when the last frame was the one before");
        log.Info(check.Ok ? "StageResources.SelfCheck: passed" : "StageResources.SelfCheck: FAILED");
    }

    /// <summary>The good lines are the fixture's (<c>tools/native/fixture/cx_cxdemo_sky.shd</c>), written out
    /// here by hand; every bad one is a line a module could ship and must be told about by name.</summary>
    private static void Grammar(Checker check)
    {
        const string good = """
            #version 460 core
            #pragma cx_image cxVolume tex3d 32 32 32 rgba8 mips
            #pragma cx_image cxHist view 2 rgba16f history
            #pragma cx_image cxBand tex2d 64 32 rgba16f clamp
            #pragma cx_pass once cxdemo_vol cxVolume
            #pragma cx_pass frame cxdemo_hist cxHist
            #pragma cx_pass frame cxdemo_band cxBand
            #pragma cx_fog cxBand 3 0
            #pragma cx_overlay cxHist
            void main() { }
            """;
        var s = new Stage { Ref = "cx_cxdemo_sky" };
        check.Expect(ParseDecls(s, good, out string why), $"the fixture's declarations parse -- {why}");
        check.Expect(s.Images.Length == 3 && s.Passes.Length == 3,
          $"the fixture declares 3 images and 3 passes; the parser read {s.Images.Length} and {s.Passes.Length}");
        if (s.Images.Length == 3 && s.Passes.Length == 3)
        {
            Img vol = s.Images[0], hist = s.Images[1], band = s.Images[2];
            // GL_RGBA8 and GL_RGBA16F, from the GL spec's enum values.
            check.Expect(vol.Shape == Shape.Tex3d && vol.W == 32 && vol.H == 32 && vol.D == 32
                      && vol.Format == 0x8058 && vol.Bpp == 4 && vol.Mips && !vol.History && !vol.Clamp,
              "`cxVolume tex3d 32 32 32 rgba8 mips` is a 32-cube RGBA8 volume with a mip chain and no history");
            check.Expect(hist.Shape == Shape.View && hist.Div == 2 && hist.Format == 0x881A && hist.Bpp == 8
                      && hist.History && hist.Clamp && !hist.Mips,
              "`cxHist view 2 rgba16f history` is a half-render-size RGBA16F pair, clamped");
            check.Expect(band.Shape == Shape.Tex2d && band.W == 64 && band.H == 32 && band.Format == 0x881A
                      && band.Clamp && !band.History,
              "`cxBand tex2d 64 32 rgba16f clamp` is a 64x32 RGBA16F, clamped");
            check.Expect(s.Passes[0].Once && s.Passes[0].Ref == "cxdemo_vol" && s.Passes[0].Image == 0,
              "the first pass is `once cxdemo_vol` over image 0");
            check.Expect(!s.Passes[1].Once && s.Passes[1].Image == 1 && !s.Passes[2].Once && s.Passes[2].Image == 2,
              "the other two are frame passes over images 1 and 2, in declaration order");
            check.Expect(s.FogImage == 2 && s.FogX == 3 && s.FogY == 0,
              "`cx_fog cxBand 3 0` reads the fog's colour from texel (3, 0) of image 2");
            check.Expect(s.OverlayImage == 1 && s.OverlayRef == "cxHist", "`cx_overlay cxHist` adds image 1 over the frame");
        }
        var plain = new Stage { Ref = "cx_cxdemo_sky" };
        check.Expect(ParseDecls(plain, "#pragma cx_image cxA tex2d 8 8 r8", out _) && plain.FogImage == -1
                  && plain.OverlayImage == -1,
          "a stage that declares no cx_fog leaves the fog its author's colour, and one with no cx_overlay adds nothing");
        var bare = new Stage { Ref = "cx_rambo" };
        check.Expect(ParseDecls(bare, "#version 460 core\n#pragma optimize(on)\nvoid main() { }", out _)
                  && bare.Images.Length == 0 && bare.Passes.Length == 0,
          "a native stage that declares nothing parses, and a pragma that is not a cx_ one is not a declaration");
        // The rest of the format table, against the spec's values.
        check.Expect(Fmt("rgba8") == 0x8058 && Fmt("rgba16f") == 0x881A && Fmt("rgba32f") == 0x8814
                  && Fmt("rg16f") == 0x822F && Fmt("rg8") == 0x822B && Fmt("r16f") == 0x822D
                  && Fmt("r32f") == 0x822E && Fmt("r8") == 0x8229 && Fmt("r11f_g11f_b10f") == 0x8C3A,
          "every declared format maps to its GL sized internal format");
        check.Expect(Fmt("rgba12f") == 0 && Fmt("RGBA8") == 0 && Fmt("") == 0,
          "an unknown format, and a known one in the wrong case, are refused");

        Bad(check, "#pragma cx_image cxA 3d:32x32x32 rgba8", "a shape written as one token");
        Bad(check, "#pragma cx_image cxA tex2d 64x32 rgba16f", "an extent written as one token");
        Bad(check, "#pragma cx_image cxA tex2d 8192 8192 rgba8", "a 2d side past the budget");
        Bad(check, "#pragma cx_image cxA tex3d 512 512 512 rgba8", "a 3d side past the budget");
        Bad(check, "#pragma cx_image cxA view 9 rgba8", "a view divisor past the budget");
        Bad(check, "#pragma cx_image cxA view 0 rgba8", "a view divisor of zero");
        Bad(check, "#pragma cx_image cxA tex2d 64 32 rgba12f", "an unknown format");
        Bad(check, "#pragma cx_image cxA tex2d 64 32 rgba8 wrap", "an unknown flag");
        Bad(check, "#pragma cx_image cxA tex2d 64 32", "a declaration with no format");
        Bad(check, "#pragma cx_image gl_A tex2d 64 32 rgba8", "a name in the reserved gl_ space");
        Bad(check, "#pragma cx_image cxA cube 64 32 rgba8", "an unknown shape");
        Bad(check, "#pragma cx_image cxA tex2d 64 32 rgba8\n#pragma cx_image cxA tex2d 8 8 r8", "two images of one name");
        Bad(check, "#pragma cx_volume cxA tex2d 64 32 rgba8", "an unknown cx_ verb");
        Bad(check, "#pragma cx_sky_image cxA tex2d 64 32 rgba8", "a verb of the old sky-only grammar");
        Bad(check, "#pragma cx_image cxA tex2d 64 32 rgba8\n#pragma cx_pass sometime cxdemo_vol cxA",
            "a pass that is neither once nor frame");
        Bad(check, "#pragma cx_image cxA tex2d 64 32 rgba8\n#pragma cx_pass once cxdemo_vol cxB",
            "a pass over an image nothing declares");
        Bad(check, "#pragma cx_image cxA tex2d 64 32 rgba8\n#pragma cx_pass once cxdemo_vol", "a pass with no image");
        Bad(check, "#pragma cx_image cxA tex2d 64 32 rgba8\n#pragma cx_fog cxB 0 0", "a fog texel in an image nothing declares");
        Bad(check, "#pragma cx_image cxA tex2d 64 32 rgba8\n#pragma cx_fog cxA 64 0", "a fog texel past the image's width");
        Bad(check, "#pragma cx_image cxA tex2d 64 32 rgba8\n#pragma cx_fog cxA 0 32", "a fog texel past the image's height");
        Bad(check, "#pragma cx_image cxA view 2 rgba16f\n#pragma cx_fog cxA 0 0", "a fog texel in a view-sized image");
        Bad(check, "#pragma cx_image cxA tex3d 8 8 8 rgba16f\n#pragma cx_fog cxA 0 0", "a fog texel in a volume");
        Bad(check, "#pragma cx_image cxA tex2d 64 32 rgba8\n#pragma cx_fog cxA 0", "a fog texel with one coordinate");
        Bad(check, "#pragma cx_image cxA tex2d 64 32 rgba8\n#pragma cx_fog cxA 0 0\n#pragma cx_fog cxA 1 0", "two fog texels");
        Bad(check, "#pragma cx_image cxA tex2d 64 32 rgba8\n#pragma cx_overlay cxB", "an overlay nothing declares");
        Bad(check, "#pragma cx_image cxA tex3d 8 8 8 rgba16f\n#pragma cx_overlay cxA", "an overlay that is a volume");
        Bad(check, "#pragma cx_image cxA tex2d 64 32 rgba8\n#pragma cx_overlay", "an overlay with no image");
        Bad(check, "#pragma cx_image cxA tex2d 64 32 rgba8\n#pragma cx_overlay cxA 0 0", "an overlay with a texel");
        Bad(check, "#pragma cx_image cxA view 2 rgba16f\n#pragma cx_overlay cxA\n#pragma cx_overlay cxA", "two overlays");
    }

    /// <summary>Which images swap halves per frame. Oracle: a hand-built stage -- a history image and a plain one a
    /// frame pass writes, a volume a once pass writes -- and the expected flags written out by hand.</summary>
    private static void Swaps(Checker check)
    {
        Img hist = new() { Name = "cxHist", History = true }, band = new() { Name = "cxBand" }, vol = new() { Name = "cxVol" };
        var s = new Stage
        {
            Ref = "cx_selfcheck",
            Images = [hist, band, vol],
            Passes =
            [
                new Pass { Once = true, Binds = [new ImageBind { Image = 2 }] },
                new Pass { Binds = [new ImageBind { Image = 0 }, new ImageBind { Image = 0, Prev = true }] },
                new Pass { Binds = [new ImageBind { Image = 1 }] },
            ],
        };
        check.Expect(MarkSwaps(s, out string why), $"a history image a frame pass writes is accepted -- {why}");
        check.Expect(hist.Swap, "the history image a frame pass writes swaps halves");
        check.Expect(!band.Swap, "a plain image a frame pass writes does not swap: it has no second half");
        check.Expect(!vol.Swap, "an image only a once pass writes does not swap");
        var idle = new Stage
        {
            Ref = "cx_selfcheck",
            Images = [new Img { Name = "cxHist", History = true }],
            Passes = [new Pass { Binds = [new ImageBind { Image = 0, Prev = true }] }],
        };
        check.Expect(!MarkSwaps(idle, out _), "a history image no frame pass writes as an image is refused");
    }

    /// <summary>An overlay has to be an image a FRAME pass writes. Oracle: hand-built stages -- one per way of touching
    /// the overlay image -- and the verdicts written out by hand.</summary>
    private static void Overlay(Checker check)
    {
        static Stage With(bool once, bool written) => new()
        {
            Ref = "cx_selfcheck",
            OverlayImage = 0,
            OverlayRef = "cxOver",
            Images = [new Img { Name = "cxOver", Shape = Shape.View }],
            Passes = [new Pass { Once = once, Binds = written ? [new ImageBind { Image = 0 }] : [] }],
        };
        check.Expect(OverlayWritten(With(once: false, written: true), out string why), $"an overlay a frame pass writes is accepted -- {why}");
        check.Expect(!OverlayWritten(With(once: true, written: true), out _), "an overlay only a once pass writes is refused");
        check.Expect(!OverlayWritten(With(once: false, written: false), out _), "an overlay a frame pass only samples is refused");
        check.Expect(OverlayWritten(new Stage { Ref = "cx_selfcheck" }, out _), "a stage with no overlay has nothing to check");
    }

    /// <summary>The two declaration budgets, each taken one line past its edge and one line inside it.</summary>
    private static void Budgets(Checker check)
    {
        check.Expect(ParseOk(Images(MaxImages)), $"{MaxImages} images are within the budget");
        check.Expect(!ParseOk(Images(MaxImages + 1)), $"{MaxImages + 1} images are past the budget");
        check.Expect(ParseOk(Images(1) + Passes(MaxPasses)), $"{MaxPasses} passes are within the budget");
        check.Expect(!ParseOk(Images(1) + Passes(MaxPasses + 1)), $"{MaxPasses + 1} passes are past the budget");
    }

    private static string Images(int n)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.Append("#pragma cx_image cxA").Append(i).Append(" tex2d 8 8 r8\n");
        return sb.ToString();
    }

    private static string Passes(int n)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.Append("#pragma cx_pass frame cxdemo_p").Append(i).Append(" cxA0\n");
        return sb.ToString();
    }

    private static bool ParseOk(string text) => ParseDecls(new Stage { Ref = "cx_selfcheck" }, text, out _);

    private static void Bad(Checker check, string line, string what)
        => check.Expect(!ParseOk(line), $"a native stage declaring {what} is refused: `{line.Replace('\n', '|')}`");

    private static uint Fmt(string s) => ParseFormat(s, out uint fmt, out _) ? fmt : 0u;

    /// <summary>The two wrapper stages: each carries its region and the entry name the splice renames to, the compute
    /// one carries the whole-stage environment and no frame uniform of its own, and the sky's still has the
    /// linear-HDR branch a native stage is built with.</summary>
    private static void Wrappers(Checker check)
    {
        string cs = ShaderStages.Source(ShdPassCs);
        check.Expect(cs.Contains("// $VANILLA-MAIN " + CsToken + "\n"),
          $"{ShdPassCs} carries the whole-stage region and its {CsToken} token");
        check.Expect(cs.Contains("void " + VanillaShader.MainEntry + "()"),
          $"{ShdPassCs} has a stub under the entry name the splice renames main() to");
        check.Expect(cs.Contains("#define worldtimerTimeOfDay uTimeOfDay") && cs.Contains("#define moduleYear uModYear"),
          $"{ShdPassCs} carries the whole-stage environment, so a pass reads the engine's names a stage does");
        // The frame uniforms belong to the stage that reads them: an environment that declared one would collide with
        // the stage's own declaration, which is what lets one stage compile under the engine and under Alloy alike.
        check.Expect(!cs.Contains("cxFrame") && !cs.Contains("cxInvVp") && !ShaderStages.Source(SkyStage.ShdFs).Contains("cxMain"),
          "neither wrapper declares a cx frame uniform");
        check.Expect(cs.Contains("layout(local_size_x"),
          $"{ShdPassCs}'s stub declares a work-group size, without which it is not a compute stage");
        string fs = ShaderStages.Source(SkyStage.ShdFs);
        check.Expect(fs.Contains("#ifdef CX_NATIVE"), $"{SkyStage.ShdFs} still has the linear-HDR branch a native stage is built with");
        check.Expect(ShaderStages.WithDefine(fs, NativeDefine).Contains("#define " + NativeDefine + "\n"),
          "the native build splices its define in");
    }
}
