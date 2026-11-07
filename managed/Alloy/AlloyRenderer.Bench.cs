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

    /// <summary>One bench run: its knob list, the values it restores, its steps and where it has got to. A run
    /// ends by dropping the object, so no figure from a finished run can be read as a live one.</summary>
    private sealed class BenchState
    {
        public Knob[] Knobs = [];
        public string[] Was = [];
        /// <summary>Per step: its label and the (knob index, value) pairs it sets over the restored baseline.</summary>
        public (string Label, (int K, string V)[] Set)[] Steps = [];
        public int Step, Frame;
        public long T0, Start;
        /// <summary>The hot-path census at the window's start: what the scene's movers did to the step, apart from the knob.</summary>
        public long Hot0, Sph0, Eng0, Mov0;
        public double BaseFrame;
        /// <summary>Each step's WALL frame-time delta against the baseline, for the finish notice's three biggest
        /// savings. Wall time, not a GPU figure: on a capped display a saving becomes headroom and reads as zero
        /// here, so the [vkgpu] line is what answers "did it cost less GPU".</summary>
        public double[] Saved = [];

        /// <summary>Total steps this run.</summary>
        public int Total => Steps.Length;
        public string Label(int step) => Steps[step].Label;
    }

    /// <summary>The run in flight, or null when none is.</summary>
    private static BenchState? _bench;

    /// <summary>The button: arm the run (cold). A second press while one runs aborts it and restores.</summary>
    internal static void BenchArm()
    {
        if (_bench != null) { BenchFinish("aborted"); return; }
        var b = new BenchState
        {
            Knobs = [Shadows.Soft, Shadows.CubeDist, Lighting.BinCap, Lighting.BinReach, Lightmap.Movers,
                     Weather.Enabled, Lighting.Ssr, Lighting.SsrPlanar, Post.Scale,
                     Shadows.Taps, Shadows.Cap, Lighting.Relevance, Lighting.LightRec, Lighting.HiddenLights,
                     Weather.EdgeTaps, Weather.Splash, Shadows.Dist, Shadows.SunFull, Lighting.Probe, Ao.On,
                     Weather.FilmLights, Weather.Cover, Weather.WetStrength, Weather.RunOff, Weather.Map,
                     Weather.SnowDepth, Weather.SnowPile, Weather.Impacts, Weather.ShelterCache, Lightmap.MoverTaps,
                     Lighting.Deferred, Lighting.DeferredTiles, Shadows.DeferredRes,
                     Shadows.PerPixel, Lighting.Floor, Lighting.FullModel, Renderer.Oit],
            Steps =
            [
                ("baseline", []),
                ("shadow.soft 0 (no contact-hardening search, no disc)", [(0, "0")]),
                ("shadow.taps 0 (no cube soft filter)", [(9, "0")]),
                // On the deferred path this also stands the per-pixel visibility record down: the step reads
                // as "no record, every light one hardware tap" there.
                ("shadow.cap 0 (every light one hard tap, no deferred shadow record)", [(10, "0")]),
                ("shadow.deferredres full (the per-pixel shadow record at full resolution)", [(32, "1")]),
                // The per-pixel shadow budget: 0 taps every shadowing light, 1 is the floor, one shadow a pixel.
                ("shadow.perpixel 1 (one shadowed light a pixel)", [(33, "1")]),
                ("shadow.perpixel 0 (no per-pixel shadow budget: every light tapped)", [(33, "0")]),
                ("shadow.cubedist 5 (cube shadows ~off)", [(1, "5")]),
                ("shadow.cubedist 15", [(1, "15")]),
                ("light.bincap 8", [(2, "8")]),
                ("light.binreach 0.5", [(3, "0.5")]),
                // The per-light reach cut: 0 is the authored radius, and the coarse step is the one that
                // actually shortens an ordinary lamp.
                ("light.floor 0 (no perceptual reach cut: every light to its authored radius)", [(34, "0")]),
                ("light.floor 0.002 (a coarse, just-visible reach cut)", [(34, "0.002")]),
                ("light.relevance 0", [(11, "0")]),
                // budget: the lighting LOD, off by default. The pair is the trade: what the refinements cost
                // on the faint tail, and what the frame looks like without them.
                ("light.fullmodel 0.05 (plain falloff under 5% of the brightest light)", [(35, "0.05")]),
                ("light.fullmodel 0.25 (plain falloff under a quarter of the brightest)", [(35, "0.25")]),
                ("light.hidden 0 (no mirror walk of baked statics)", [(13, "0")]),
                // the whole move, read against the lit colour terms -- off puts the point-light loop back
                // inside the lit stage and the deferred dispatch does nothing.
                ("light.deferred 0 (forward point-light loop in the lit stage)", [(30, "0")]),
                ("light.lightrec 0 (blended fragments on a deferred surface walk the lights again)", [(12, "0")]),
                ("light.deferredtiles 0 (each deferred pixel walks its own froxel list, not its tile's)", [(31, "0")]),
                ("lightmap.movertaps 0 (hot lights: ray test only, no light body or shadow reads)", [(29, "0")]),
                // The whole hot/mover path. On a deferred frame this knob removes the mover-debt dispatches and
                // the cluster build's hot gate, which shortens every froxel's live run.
                ("lightmap.movers 0 (no hot static lights: no mover debt, no hot gate)", [(4, "0")]),
                ("weather.edgetaps 1", [(14, "1")]),
                ("weather.splash 0 (no ripples)", [(15, "0")]),
                ("weather.filmlights 0 (no static highlights on puddles)", [(20, "0")]),
                ("weather.shelcache 0 (shelter disk per pixel everywhere)", [(28, "0")]),
                ("weather.cover 0 (no shelter: no edge disk, no exposure)", [(21, "0")]),
                ("weather.runoff 0 (no wall streaks / foot band)", [(23, "0")]),
                ("weather.map 0 (no wetness map: estimate puddles)", [(24, "0")]),
                ("weather.impacts 0 (no splash sprites)", [(27, "0")]),
                ("weather.snowdepth 0 (snow only: no relief march)", [(25, "0")]),
                ("weather.snowpile 0 (snow only: no wall drift)", [(26, "0")]),
                ("weather.wet 0 (rain only: precipitation, dry surfaces)", [(22, "0")]),
                ("weather.enabled 0 (dry ground)", [(5, "0")]),
                ("shadow.dist 120", [(16, "120")]),
                ("shadow.sunfull 2", [(17, "2")]),
                ("light.probe 0", [(18, "0")]),
                ("ao.on 0", [(19, "0")]),
                ("light.ssrplanar 0", [(7, "0")]),
                ("light.ssr 0 + ssrplanar 0", [(6, "0"), (7, "0")]),
                // oit 2 tests depth in the shader (no early-Z), so every blended fragment behind a wall is shaded
                ("alloy.oit 1 (weighted OIT: hardware depth test, no lists)", [(36, "1")]),
                ("alloy.oit 0 (sorted blending, no OIT)", [(36, "0")]),
                ("post.scale 0.5 (GPU unbound: frame = CPU)", [(8, "0.5")]),
                ("baseline again (drift)", []),
            ],
        };
        b.Was = Array.ConvertAll(b.Knobs, k => k.FormatStored());
        foreach (Knob k in b.Knobs)
            if (k.Enforced) { _log?.Always($"AlloyRenderer: [bench] refused -- {k.Key} is held by the server"); return; }
        b.Saved = new double[b.Total];
        b.Start = Stopwatch.GetTimestamp();
        _log?.Always($"AlloyRenderer: [bench] {b.Total} knob steps x ({BenchSettle} settle + {BenchMeasure} measured) "
                   + "frames -- hold the camera still");
        Notice.Show(_log, $"Alloy bench started: {b.Total} steps, roughly {b.Total * (BenchSettle + BenchMeasure) / 3600.0:F0} min "
                        + "at 60 fps -- hold the camera still and do not move");
        _bench = b;
        BenchApply(b);
    }

    /// <summary>Every knob back to what it was, then this step's values over it.</summary>
    private static void BenchApply(BenchState b)
    {
        for (int i = 0; i < b.Knobs.Length; i++) b.Knobs[i].Parse(b.Was[i]);
        foreach ((int k, string v) in b.Steps[b.Step].Set) b.Knobs[k].Parse(v);
        b.Frame = 0;
    }

    /// <summary>Once per present. Idle: one compare.</summary>
    private static void BenchTick()
    {
        if (_bench is not BenchState b) return;
        b.Frame++;
        if (b.Frame == BenchSettle)
        {
            b.T0 = Stopwatch.GetTimestamp();
            b.Hot0 = CasterPass.HotSlots; b.Sph0 = CasterPass.HotSpheres; b.Eng0 = CasterPass.HotEngulf; b.Mov0 = CasterPass.MoverFrames;
            return;
        }
        if (b.Frame < BenchSettle + BenchMeasure) return;
        double frame = (Stopwatch.GetTimestamp() - b.T0) * 1000.0 / Stopwatch.Frequency / BenchMeasure;
        if (b.Step == 0) b.BaseFrame = frame;
        b.Saved[b.Step] = frame - b.BaseFrame;
        _log?.Always($"AlloyRenderer: [bench] {b.Label(b.Step)}: frame {frame:F2} ms ({1000.0 / frame:F0} fps, {frame - b.BaseFrame:+0.00;-0.00})"
                   + $"; scene: hot lights {(CasterPass.HotSlots - b.Hot0) / (double)BenchMeasure:F1}/f, "
                   + $"mover spheres {(CasterPass.HotSpheres - b.Sph0) / (double)BenchMeasure:F1}/f ({(CasterPass.HotEngulf - b.Eng0) / (double)BenchMeasure:F1} round their light), "
                   + $"moving casters {(CasterPass.MoverFrames - b.Mov0) / (double)BenchMeasure:F1}/f");
        if (++b.Step >= b.Total) { BenchFinish("done"); return; }
        BenchApply(b);
    }

    private static void BenchFinish(string why)
    {
        if (_bench is not BenchState b) return;
        for (int i = 0; i < b.Knobs.Length; i++) b.Knobs[i].Parse(b.Was[i]);
        _bench = null;
        double mins = (Stopwatch.GetTimestamp() - b.Start) / (double)Stopwatch.Frequency / 60.0;
        _log?.Always($"AlloyRenderer: [bench] {why} -- every knob restored");
        // The player's own end-of-run line. The bench runs for minutes with the camera held still, and a run
        // that ended with nothing said is a run nobody knows to stop holding still for -- aborted and refused
        // runs included, which is why this sits in Finish and not after the last step. Notice's sink is the CX
        // chatbox's own LOCAL line (ChatService.EchoLocal: the local store, nothing on the wire, no opcode);
        // with no sink -- chatbox stood down, main menu -- Notice logs it and drops it, which is the fallback
        // and already this mechanism's own documented ceiling.
        Notice.Show(_log, $"Alloy bench {why} ({b.Step} of {b.Total} steps, {mins:F1} min) -- full results in cx.log [bench]"
                        + BenchTopSavings(b));
    }

    /// <summary>The three steps that cut the frame time most, for the finish notice -- the knob name alone, which
    /// is the half of a step's label that fits a chat line.</summary>
    private static string BenchTopSavings(BenchState b)
    {
        string s = "";
        for (int rank = 0; rank < 3; rank++)
        {
            int best = -1;
            for (int i = 1; i < b.Saved.Length; i++)
            {
                if (b.Saved[i] >= -0.05) continue;   // not a saving worth naming
                if (best >= 0 && b.Saved[i] >= b.Saved[best]) continue;
                best = i;
            }
            if (best < 0) break;
            string label = b.Label(best);
            int cut = label.IndexOf(" (", StringComparison.Ordinal);
            s += (s.Length > 0 ? ", " : ". Biggest frame-time savings (wall; a capped display hides a GPU saving): ") + $"{(cut > 0 ? label[..cut] : label)} {b.Saved[best]:0.00} ms";
            b.Saved[best] = 0.0;   // taken: the next rank finds the next one down
        }
        return s;
    }
}
