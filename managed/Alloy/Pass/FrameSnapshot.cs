using System;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// The Alloy frame's ONE framebuffer copy, and the water stage that reads it (ADR 0011): colour and depth of
/// everything drawn up to <see cref="PassId.FbCopy"/>, snapshotted into this class's own texture pair so the
/// water stage can march them. The engine takes TWO such copies per frame because its bucket order is fixed,
/// not because two are needed; ours holds LINEAR HDR, so no engine draw could have read it anyway.
/// </summary>
internal static unsafe class FrameSnapshot
{
    /// <summary>Texture units the copy binds on -- above the probe's cube, and clear of the grass push map
    /// and the resolve even though those are other programs. <see cref="TextureSlots"/> is the ladder
    /// and the only place it is written down; do not paraphrase it here.</summary>
    internal const uint ColorUnit = TextureSlots.SnapshotColor, DepthUnit = TextureSlots.SnapshotDepth;

    /// <summary>The unit the engine's own noise texture binds on. The vanilla wave field IS that
    /// texture at four scales, so the stage cannot draw without it.</summary>
    internal const uint NoiseUnit = TextureSlots.WaterNoise;

    /// <summary>The resref vanilla's water shader samples as <c>texUnitNoise</c>. Not a guess and not a
    /// string-table hunch: <c>Scene::Render</c> @0x5762d0 loads exactly this name into <c>Scene+0x1f8</c>
    /// and binds it with <c>SetActiveTextureAndBind(0xf, 4, tid)</c> -- unit 15, which is the unit
    /// <see cref="TextureSlots.WaterNoise"/> independently landed on.</summary>
    private const string NoiseResref = "solid_noise";

    /// <summary>The engine's noise texture as the resman has it now, re-resolved per ask for <see cref="Bind"/>'s
    /// reason: a <c>CAurTexture*</c> is borrowed and an area transition frees it.</summary>
    internal static int NoiseTid => CX.Framework.AurTex.Tid(CX.Framework.AurTex.FindByName(NoiseResref));

    /// <summary>The unit the SCENE's environment map binds on -- the reflection vanilla's water
    /// carries.</summary>
    internal const uint EnvUnit = TextureSlots.WaterEnv;

    /// <summary><c>water.enabled</c> off stands the class down; on brings it back at the next pass. Armed
    /// and idling rather than un-installed, so the toggle is live either way.</summary>
    internal static bool On => Water.Enabled.V;

    /// <summary>The class can still work in this context: switched on. Read BEFORE the batch build, because
    /// staging has to decide whether to enroll the water class a pass phase before the copy is taken.</summary>
    internal static bool Usable => On;

    internal static void Attach(ExtenderLog log) => _log = log;
    private static ExtenderLog? _log;

    /// <summary>CX's own <c>solid_noise</c>: for the frames the engine's has no id in, and a content stage's
    /// <c>texUnitNoise</c> (owned, so it has a bindless handle). CX's own loader answers rather than the engine, which
    /// loads that resref only in <c>Scene::Render</c> and would leave a <c>CAurTexture</c> queued on its
    /// <c>RefreshTextures</c> list. Acquired once per session.</summary>
    internal static int OwnNoise()
    {
        if (!_ownNoiseAsked) { _ownNoiseAsked = true; _ownNoise = Db.TexRegistry.Acquire(NoiseResref); }
        return Db.TexRegistry.TryResident(_ownNoise, out uint name, out _) ? (int)name : 0;
    }

    /// <inheritdoc cref="OwnNoise"/>
    private static Db.TexId _ownNoise;
    /// <inheritdoc cref="OwnNoise"/>
    private static bool _ownNoiseAsked;
    internal static string Describe()
        // #456: the noise texture DESCRIBED, not just named. A bound-but-incomplete texture (a mipmapped
        // min filter with no mips) samples as a constant, and a constant wave field is a flat sea -- which
        // is indistinguishable from "bound and fine" if all the line prints is the id.
        => $"water {(On ? "on" : "off")} (vanilla fswater)  noise tid {NoiseTid} on unit {NoiseUnit} "
         + $"{(NoiseTid == 0 ? "" : TexProbe.TexFormat(NoiseTid) + " " + TexProbe.TexTopMip(NoiseTid) + " ")}"
         + $"rect {(Rect ? 1 : 0)}";

    /// <summary>Copy only the rows and columns the water can cover (<c>/water rect 0</c> = whole frame).</summary>
    internal static bool Rect => Water.Rect.V;
    /// <summary>Margin around the water's screen rectangle, as a fraction of the viewport height.</summary>
    internal const float RectMargin = 0.05f;


    /// <summary>The periodic line. <c>tod</c> is the engine clock the whole wave field's ANIMATION runs on
    /// (<c>fTick = worldtimerTimeOfDay * 0.001</c>), printed with its delta over the census period: a delta
    /// of ZERO means the phase never advances and the sea is a still photograph of a wave field, however
    /// correct that field is in space. Cheap, and it is the one thing a screenshot cannot show.</summary>
    internal static string Stats(int frames)
    {
        int tod = AlloyRenderer.TimeOfDay;
        int delta = tod - _todLast;
        _todLast = tod;
        return $"water: {(On ? "on" : "off")}" + (frames > 0 ? $" over {frames} frames" : "")
             + $", tod {tod} ({(delta >= 0 ? "+" : "")}{delta} since last)"
             + $", noise {NoiseTid}{(NoiseTid == 0 ? " -- NO WAVE FIELD" : "")}";
    }

    private static int _todLast;

    /// <summary>The <c>water</c> knobs, off-device: the master stands the class down, and reset re-arms it.</summary>
    private static void CheckTuning(Checker check)
    {
        bool on = Water.Enabled.V;
        try
        {
            Water.Enabled.V = false;
            check.Expect(!On && !Usable, "water.enabled off stands the class down");
            check.Expect(Water.Enabled.Default, "reset re-arms the class");
        }
        finally
        {
            Water.Enabled.V = on;
        }
    }

    /// <summary>Off-device (run-demo.sh): the stage's own contracts, the ones a GPU would only report as a
    /// wrong-looking sea. Two are structural -- water reads the copy the pass list takes immediately before
    /// it, and the probe is what the reflection falls back to -- and both are text checks on the source that
    /// is actually compiled, which is the only thing an override cannot silently change out from under.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        CheckTuning(check);

        string fs = Fs;
        check.Expect(fs.Contains("uniform sampler2D uFbColor") && fs.Contains("uniform sampler2D uFbDepth"),
          "the stage declares the copy pair it marches");
        // #339's own criterion, as a text check: the shading is stock fswater's, not a look of ours. The
        // march is vanilla's GetRayIntersectCoords, the wave field reads the ENGINE's noise texture, and the
        // reflection's fallback is the shaded surface -- a probe here would be the custom look coming back.
        check.Expect(fs.Contains("CxRayIntersect("), "the stage marches with stock's own ray intersection");
        check.Expect(fs.Contains("texture(uNoise,"), "the wave field reads the engine's noise texture");
        check.Expect(!fs.Contains("samplerCube uProbe"), "the probe is NOT the reflection's fallback -- vanilla falls back to the surface");
        // Colour space: since #132 the whole Alloy frame is linear, so the copy is linear and a decode on
        // read would be a second linearisation. Sampling it in the wrong space is a whole-image error that
        // reads as "the reflection is washed out" (or "far too dark") rather than as a bug, so it is
        // asserted rather than left to the next reader of the shader text.
        check.Expect(fs.Contains("return texture(uFbColor, uv).rgb;") && !fs.Contains("CxLinear(texture(uFbColor"),
          "the copy is sampled as LINEAR, with no decode");
        // Ordering: FbCopy immediately precedes WaterOwned, and both sit between the blended class and
        // additive. That IS the ticket's "one copy, vanilla's placement" -- it is not an implementation
        // detail of the pass list, it is the design.
        int copy = -1, water = -1, blend = -1, add = -1;
        for (int i = 0; i < FrameGraph.Passes.Length; i++)
        {
            PassId id = FrameGraph.Passes[i].Id;
            if (id == PassId.FbCopy) copy = i;
            if (id == PassId.WaterOwned) water = i;
            if (id == PassId.BlendedOwned) blend = i;
            if (id == PassId.AdditiveOwned) add = i;
        }
        check.Expect(copy >= 0 && water == copy + 1, "the water pass reads a copy taken immediately before it");
        check.Expect(blend >= 0 && blend < copy, "the blended class (fade included) is IN the copy");
        check.Expect(add >= 0 && add > water, "additive is NOT in the copy, and composites over the water");
        check.Expect(ColorUnit > EnvProbe.ProbeUnit && DepthUnit > ColorUnit,
          "the copy's units sit above the probe cube's -- two sampler types on one unit drops the draw");
        // #339: every view the stage implements is REACHABLE. /alloy viz clamps to AlloyRenderer.LightVizMax,
        // silently -- a view above it paints the highest one instead, which looks like a working instrument
        // answering the wrong question. Read off the stage text so adding a view and forgetting the ceiling
        // fails here rather than in a screenshot.
        int highest = 0;
        for (int i = fs.IndexOf("uViz == "); i >= 0; i = fs.IndexOf("uViz == ", i + 1))
        {
            int j = i + 8, n = 0;
            while (j < fs.Length && fs[j] >= '0' && fs[j] <= '9') { n = n * 10 + (fs[j] - '0'); j++; }
            if (n > highest) highest = n;
        }
        check.Expect(highest <= AlloyRenderer.LightVizMax,
          $"the stage's highest view ({highest}) is above /alloy viz's ceiling ({AlloyRenderer.LightVizMax})");
        check.Expect(NoiseUnit != ColorUnit && NoiseUnit != DepthUnit && NoiseUnit != TextureSlots.Diffuse,
          "the noise texture has a unit of its own");

        log.Info(check.Ok ? "FrameSnapshot.SelfCheck: passed" : "FrameSnapshot.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>The whole fragment stage, composed by <see cref="ShaderStages"/> -- the one place any owned
    /// stage's composition is written down (ADR 0012).</summary>
    internal static string Fs => ShaderStages.Source(AlloyRenderer.ShdWaterFs);

    /// <summary>The water vertex stage: the lit pass's clip expression and nothing else. Deliberately not the
    /// lit stage itself -- water is never skinned and never a wind-feature part -- and not a tessellating one
    /// either: vanilla water displaces in the FRAGMENT stage (fswater's parallax search over
    /// GetMainWaveOffset), so there is nothing for a tessellator to do.</summary>
    internal static string Vs => ShaderStages.Source(AlloyRenderer.ShdWaterVs);

    /// <summary>The water fragment stage, part one: the declarations, and the stock constants they carry.
    /// Every number is <c>inc_water.shd</c>'s own <c>#define</c> at the quality mode this client runs
    /// (<c>SHADER_QUALITY_MODE 2</c>). Only that branch is ported: the two lower tiers are different shaders,
    /// and reproducing a look nobody on this client sees is the kind of ladder that rots.</summary>
    internal static string FsHead => ShaderSource.Get("cx_water_fhead.glsl");
}
