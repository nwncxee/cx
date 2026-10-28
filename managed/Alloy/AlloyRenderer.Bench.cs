using System;
using System.Diagnostics;
using CX.Framework;

namespace CX.Alloy;

/// <summary><c>/alloy bench</c>: the GPU-cost A/B. With the camera held still it steps a fixed list of settings,
/// settles <see cref="BenchSettle"/> frames, measures <see cref="BenchMeasure"/> more (frame time, the colour
/// bracket's GPU span, every GPU term, the shadow bake), restores every knob it touched and logs one
/// <c>[bench]</c> line per step against the first.</summary>
public sealed unsafe partial class AlloyRenderer
{
    private const int BenchSettle = 45, BenchMeasure = 150;
    private static Knob[]? _benchKnobs;
    private static string[]? _benchWas;
    /// <summary>Per step: its label and the (knob index, value) pairs it sets over the restored baseline.</summary>
    private static (string Label, (int K, string V)[] Set)[]? _benchSteps;
    private static int _benchStep = -1, _benchFrame;
    private static long _benchT0, _benchStart;
    /// <summary>The hot-path census at the window's start: what the scene's movers did to the step, apart from the knob.</summary>
    private static long _benchHot0, _benchSph0, _benchEng0, _benchMov0;
    private static double _benchBaseFrame;
    /// <summary>Each step's WALL frame-time delta against the baseline, for the finish notice's three biggest
    /// savings. Wall time, not a GPU figure: on a capped display a saving becomes headroom and reads as zero
    /// here, so the [vkgpu] line is what answers "did it cost less GPU".</summary>
    private static double[]? _benchSaved;

    /// <summary>Total steps this run.</summary>
    private static int BenchTotal => _benchSteps?.Length ?? 0;
    private static string BenchLabel(int step) => _benchSteps![step].Label;

    /// <summary>The button: arm the run (cold). A second press while one runs aborts it and restores.</summary>
    internal static void BenchArm()
    {
        if (_benchStep >= 0) { BenchFinish("aborted"); return; }
        _benchKnobs = new Knob[] { Shadows.Soft, Shadows.CubeDist, Lighting.BinCap, Lighting.BinReach, Lightmap.Movers,
                                   Weather.Enabled, Lighting.Ssr, Lighting.SsrPlanar, Renderer.Scale,
                                   Shadows.Taps, Shadows.Cap, Lighting.Relevance, Lighting.LightRec, Lighting.HiddenLights,
                                   Weather.EdgeTaps, Weather.Splash, Shadows.Dist, Shadows.SunFull, Lighting.Probe, Ao.On,
                                   Weather.FilmLights, Weather.Cover, Weather.WetStrength, Weather.RunOff, Weather.Map,
                                   Weather.SnowDepth, Weather.SnowPile, Weather.Impacts, Weather.ShelterCache, Lightmap.MoverTaps,
                                   Lighting.Deferred, Lighting.DeferredTiles, Shadows.DeferredRes,
                                   Lightmap.MoverLights, Shadows.PerPixel, Lighting.Floor, Lighting.FullModel, Renderer.Oit };
        _benchWas = Array.ConvertAll(_benchKnobs, k => k.FormatStored());
        _benchSteps = new (string, (int, string)[])[]
        {
            ("baseline", Array.Empty<(int, string)>()),
            ("shadow.soft 0 (no contact-hardening search, no disc)", new[] { (0, "0") }),
            ("shadow.taps 0 (no cube soft filter)", new[] { (9, "0") }),
            // On the deferred path this also stands the per-pixel visibility record down: the step reads
            // as "no record, every light one hardware tap" there.
            ("shadow.cap 0 (every light one hard tap, no deferred shadow record)", new[] { (10, "0") }),
            ("shadow.deferredres full (the per-pixel shadow record at full resolution)", new[] { (32, "1") }),
            // The per-pixel shadow budget: 0 taps every shadowing light, 1 is the floor, one shadow a pixel.
            ("shadow.perpixel 1 (one shadowed light a pixel)", new[] { (34, "1") }),
            ("shadow.perpixel 0 (no per-pixel shadow budget: every light tapped)", new[] { (34, "0") }),
            ("shadow.cubedist 5 (cube shadows ~off)", new[] { (1, "5") }),
            ("shadow.cubedist 15", new[] { (1, "15") }),
            ("light.bincap 8", new[] { (2, "8") }),
            ("light.binreach 0.5", new[] { (3, "0.5") }),
            // The per-light reach cut: 0 is the authored radius, and the coarse step is the one that
            // actually shortens an ordinary lamp.
            ("light.floor 0 (no perceptual reach cut: every light to its authored radius)", new[] { (35, "0") }),
            ("light.floor 0.002 (a coarse, just-visible reach cut)", new[] { (35, "0.002") }),
            ("light.relevance 0", new[] { (11, "0") }),
            // budget: the lighting LOD, off by default. The pair is the trade: what the refinements cost
            // on the faint tail, and what the frame looks like without them.
            ("light.fullmodel 0.05 (plain falloff under 5% of the brightest light)", new[] { (36, "0.05") }),
            ("light.fullmodel 0.25 (plain falloff under a quarter of the brightest)", new[] { (36, "0.25") }),
            ("light.hidden 0 (no mirror walk of baked statics)", new[] { (13, "0") }),
            // the whole move, read against the lit colour terms -- off puts the point-light loop back
            // inside the lit stage and the deferred dispatch does nothing.
            ("light.deferred 0 (forward point-light loop in the lit stage)", new[] { (30, "0") }),
            ("light.lightrec 0 (blended fragments on a deferred surface walk the lights again)", new[] { (12, "0") }),
            ("light.deferredtiles 1 (deferred pass on its own per-tile cull, not the froxel lists)", new[] { (31, "1") }),
            ("lightmap.movertaps 0 (hot lights: ray test only, no light body or shadow reads)", new[] { (29, "0") }),
            // The pair tiles and the screen-space debt they feed. 0 is the whole feature off --
            // the deferred frame then owes the baked receivers nothing and every static light keeps its page;
            // 4 is the per-mover budget's ceiling, which is what the atlas and the debt walk cost at worst.
            ("lightmap.moverlights 0 (deferred: no creature shadows from baked lights)", new[] { (33, "0") }),
            ("lightmap.moverlights 4 (deferred: four pair tiles per creature)", new[] { (33, "4") }),
            // The whole hot/mover path. On a deferred frame this knob removes THREE things --
            // the pair-tile bakes (the shadow-bake meter on every line below), the pair-debt dispatch (its own
            // GPU term, split further by the pairdebt ladder), and the cluster build's hot gate, which shortens
            // every froxel's live run. The step above removes the first two alone, so the difference between
            // the two steps is the build's.
            ("lightmap.movers 0 (no hot static lights: no pair bakes, no debt dispatch, no hot gate)", new[] { (4, "0") }),
            ("weather.edgetaps 1", new[] { (14, "1") }),
            ("weather.splash 0 (no ripples)", new[] { (15, "0") }),
            ("weather.filmlights 0 (no static highlights on puddles)", new[] { (20, "0") }),
            ("weather.shelcache 0 (shelter disk per pixel everywhere)", new[] { (28, "0") }),
            ("weather.cover 0 (no shelter: no edge disk, no exposure)", new[] { (21, "0") }),
            ("weather.runoff 0 (no wall streaks / foot band)", new[] { (23, "0") }),
            ("weather.map 0 (no wetness map: estimate puddles)", new[] { (24, "0") }),
            ("weather.impacts 0 (no splash sprites)", new[] { (27, "0") }),
            ("weather.snowdepth 0 (snow only: no relief march)", new[] { (25, "0") }),
            ("weather.snowpile 0 (snow only: no wall drift)", new[] { (26, "0") }),
            ("weather.wet 0 (rain only: precipitation, dry surfaces)", new[] { (22, "0") }),
            ("weather.enabled 0 (dry ground)", new[] { (5, "0") }),
            ("shadow.dist 120", new[] { (16, "120") }),
            ("shadow.sunfull 2", new[] { (17, "2") }),
            ("light.probe 0", new[] { (18, "0") }),
            ("ao.on 0", new[] { (19, "0") }),
            ("light.ssrplanar 0", new[] { (7, "0") }),
            ("light.ssr 0 + ssrplanar 0", new[] { (6, "0"), (7, "0") }),
            // oit 2 tests depth in the shader (no early-Z), so every blended fragment behind a wall is shaded
            ("alloy.oit 1 (weighted OIT: hardware depth test, no lists)", new[] { (37, "1") }),
            ("alloy.oit 0 (sorted blending, no OIT)", new[] { (37, "0") }),
            ("alloy.scale 0.5 (GPU unbound: frame = CPU)", new[] { (8, "0.5") }),
            ("baseline again (drift)", Array.Empty<(int, string)>()),
        };
        foreach (Knob k in _benchKnobs)
            if (k.Enforced) { _log?.Always($"AlloyRenderer: [bench] refused -- {k.Key} is held by the server"); return; }
        _benchSaved = new double[BenchTotal];
        _benchStart = Stopwatch.GetTimestamp();
        _log?.Always($"AlloyRenderer: [bench] {BenchTotal} knob steps x ({BenchSettle} settle + {BenchMeasure} measured) "
                   + "frames -- hold the camera still");
        Notice.Show(_log, $"Alloy bench started: {BenchTotal} steps, roughly {BenchTotal * (BenchSettle + BenchMeasure) / 3600.0:F0} min "
                        + "at 60 fps -- hold the camera still and do not move");
        _benchStep = 0;
        BenchApply();
    }

    /// <summary>Every knob back to what it was, then this step's values over it.</summary>
    private static void BenchApply()
    {
        if (_benchStep < 0) return;
        for (int i = 0; i < _benchKnobs!.Length; i++) _benchKnobs[i].Parse(_benchWas![i]);
        foreach ((int k, string v) in _benchSteps![_benchStep].Set) _benchKnobs[k].Parse(v);
        _benchFrame = 0;
    }

    /// <summary>Once per present. Idle: one compare.</summary>
    private static void BenchTick()
    {
        if (_benchStep < 0) return;
        _benchFrame++;
        if (_benchFrame == BenchSettle)
        {
            _benchT0 = Stopwatch.GetTimestamp();
            _benchHot0 = CasterPass.HotSlots; _benchSph0 = CasterPass.HotSpheres; _benchEng0 = CasterPass.HotEngulf; _benchMov0 = CasterPass.MoverFrames;
            return;
        }
        if (_benchFrame < BenchSettle + BenchMeasure) return;
        double frame = (Stopwatch.GetTimestamp() - _benchT0) * 1000.0 / Stopwatch.Frequency / BenchMeasure;
        if (_benchStep == 0) _benchBaseFrame = frame;
        _benchSaved![_benchStep] = frame - _benchBaseFrame;
        _log?.Always($"AlloyRenderer: [bench] {BenchLabel(_benchStep)}: frame {frame:F2} ms ({1000.0 / frame:F0} fps, {frame - _benchBaseFrame:+0.00;-0.00})"
                   + $"; scene: hot lights {(CasterPass.HotSlots - _benchHot0) / (double)BenchMeasure:F1}/f, "
                   + $"mover spheres {(CasterPass.HotSpheres - _benchSph0) / (double)BenchMeasure:F1}/f ({(CasterPass.HotEngulf - _benchEng0) / (double)BenchMeasure:F1} round their light), "
                   + $"moving casters {(CasterPass.MoverFrames - _benchMov0) / (double)BenchMeasure:F1}/f");
        if (++_benchStep >= BenchTotal) { BenchFinish("done"); return; }
        BenchApply();
    }

    private static void BenchFinish(string why)
    {
        for (int i = 0; i < _benchKnobs!.Length; i++) _benchKnobs[i].Parse(_benchWas![i]);
        int done = _benchStep;
        _benchStep = -1;
        double mins = (Stopwatch.GetTimestamp() - _benchStart) / (double)Stopwatch.Frequency / 60.0;
        _log?.Always($"AlloyRenderer: [bench] {why} -- every knob restored");
        // The player's own end-of-run line. The bench runs for minutes with the camera held still, and a run
        // that ended with nothing said is a run nobody knows to stop holding still for -- aborted and refused
        // runs included, which is why this sits in Finish and not after the last step. Notice's sink is the CX
        // chatbox's own LOCAL line (ChatService.EchoLocal: the local store, nothing on the wire, no opcode);
        // with no sink -- chatbox stood down, main menu -- Notice logs it and drops it, which is the fallback
        // and already this mechanism's own documented ceiling.
        Notice.Show(_log, $"Alloy bench {why} ({done} of {BenchTotal} steps, {mins:F1} min) -- full results in cx.log [bench]"
                        + BenchTopSavings());
    }

    /// <summary>The three steps that cut the frame time most, for the finish notice -- the knob name alone, which
    /// is the half of a step's label that fits a chat line.</summary>
    private static string BenchTopSavings()
    {
        if (_benchSaved == null) return "";
        string s = "";
        for (int rank = 0; rank < 3; rank++)
        {
            int best = -1;
            for (int i = 1; i < _benchSaved.Length; i++)
            {
                if (_benchSaved[i] >= -0.05) continue;   // not a saving worth naming
                if (best >= 0 && _benchSaved[i] >= _benchSaved[best]) continue;
                best = i;
            }
            if (best < 0) break;
            string label = BenchLabel(best);
            int cut = label.IndexOf(" (", StringComparison.Ordinal);
            s += (s.Length > 0 ? ", " : ". Biggest frame-time savings (wall; a capped display hides a GPU saving): ") + $"{(cut > 0 ? label[..cut] : label)} {_benchSaved[best]:0.00} ms";
            _benchSaved[best] = 0.0;   // taken: the next rank finds the next one down
        }
        return s;
    }
}
