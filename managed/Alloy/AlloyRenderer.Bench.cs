using System;
using System.Diagnostics;
using CX.Framework;

namespace CX.Alloy;

/// <summary><c>/alloy bench</c>: the GPU-cost A/B. With the camera held still it steps a fixed list of settings,
/// settles <see cref="BenchSettle"/> frames, measures <see cref="BenchMeasure"/> more (frame time, the colour
/// bracket's GPU span, every GPU term, the shadow bake), restores every knob it touched and logs one
/// <c>[bench]</c> line per step against the first; then the <see cref="_benchAbl"/> ablation ladder.</summary>
public sealed unsafe partial class AlloyRenderer
{
    private const int BenchSettle = 45, BenchMeasure = 150;
    private static Knob[]? _benchKnobs;
    private static string[]? _benchWas;
    /// <summary>Per step: its label and the (knob index, value) pairs it sets over the restored baseline.</summary>
    private static (string Label, (int K, string V)[] Set)[]? _benchSteps;
    private static int _benchStep = -1, _benchFrame;
    private static long _benchT0, _benchStart;
    private static double _benchSpan0; private static int _benchSpanN0;
    private static readonly ulong[] _benchSum0 = new ulong[(int)GpuTerm.Count];
    private static readonly int[] _benchN0 = new int[(int)GpuTerm.Count];
    /// <summary>The shadow BAKE's own meter at the window's start -- the one GPU cost that is not a
    /// <see cref="GpuTerm"/>, and the one <c>lightmap.movers</c> moves through the pair tiles.</summary>
    private static ulong _benchBake0; private static int _benchBakeN0;
    /// <summary>The hot-path census at the window's start: what the scene's movers did to the step, apart from the knob.</summary>
    private static long _benchHot0, _benchSph0, _benchEng0, _benchMov0;
    private static double _benchBaseBlend, _benchBaseFrame, _benchBaseSpan, _benchBaseLit, _benchBaseAll, _benchBaseBake;
    /// <summary>Each step's GPU-span delta against the baseline, for the finish notice's three biggest savings.</summary>
    private static double[]? _benchSaved;

    // ---- the ablation ladder ---------------------------------------------------------------------
    /// <summary>Which pass an ablation step rebuilds. The GPU term each one is read off is
    /// <see cref="BenchAblTerm"/>.</summary>
    private const int AblLights = 0, AblShadows = 1, AblDebt = 2, AblBlended = 3, AblProbe = 4;
    /// <summary>The ladder, appended after the knob steps: (label, pass, rung), rung 0 = the shipped program,
    /// which closes each pass's ladder so its top is measured under the same drift as its rungs. The rungs of
    /// one pass must stay contiguous and in ascending order -- <see cref="BenchAblSummary"/> reads the array
    /// that way to difference them.</summary>
    private static readonly (string Label, int Pass, int Rung)[] _benchAbl =
    {
        ("deferred ablate 1 (G-buffer read + record ownership test + world position; no light)", AblLights, 1),
        ("deferred ablate 2 (+ the froxel list source: lengths read, every entry indexed)", AblLights, 2),
        ("deferred ablate 3 (+ the relevance / full-model pre-pass)", AblLights, 3),
        ("deferred ablate 4 (+ the light bodies: curve, N.L, lobe, leak -- no shadow at all)", AblLights, 4),
        ("deferred ablate 5 (+ the shadow record fetch; no hardware tap, no mirror tap, no film)", AblLights, 5),
        ("deferred ablate 6 (+ the transfer-weight combine: the G-buffer's weights held across the walk)", AblLights, 6),
        ("deferred ablate 7 (+ the in-place record fill on a pixel the record missed)", AblLights, 7),
        ("deferred ablate 8 (+ the mirror tap: a glossy pixel's strongest baked light)", AblLights, 8),
        ("deferred full (the shipped walk + the film pass, its own dispatch)", AblLights, 0),
        ("defshadow ablate 1 (G-buffer read + the record's gate; nothing ranked, nothing tapped)", AblShadows, 1),
        ("defshadow ablate 2 (+ the candidate ranking and the budget's dissolve; no cube tap)", AblShadows, 2),
        ("defshadow full (the shipped program: + the four filtered taps)", AblShadows, 0),
        ("pairdebt ablate 1 (the tile depth slab off the depth buffer; no pair cull, no G-buffer read)", AblDebt, 1),
        ("pairdebt ablate 2 (+ the per-tile pair cull and sort; no pair tile tapped)", AblDebt, 2),
        ("pairdebt full (the shipped program: + a pair tile's G-buffer read, the pair taps and the subtraction)", AblDebt, 0),
        ("blended ablate 1 (OIT accumulation: the bracket opened and closed, not one draw)", AblBlended, 1),
        ("blended ablate 2 (+ the first group that DRAWS, alone, no shading: a one-time wait or a per-draw cost?)", AblBlended, 2),
        ("blended ablate 3 (+ every draw group: vertex stage, raster and OIT write; no shading at all)", AblBlended, 3),
        ("blended ablate 4 (+ the lit stage, its point-light walk stopped at the list source, no hot branch)", AblBlended, 4),
        ("blended ablate 5 (+ the light bodies; no shadow, no hot branch)", AblBlended, 5),
        ("blended ablate 6 (+ the capped soft shadows and the mirror tap; no hot branch)", AblBlended, 6),
        ("blended full (the shipped lit program: + the hot-light branch)", AblBlended, 0),
        ("deferred probe 1 (deferred ablate 6 again: no cube tap anywhere in the program)", AblProbe, 6),
        ("deferred probe 2 (+ ONE hardware cube tap, taken by a single invocation)", AblProbe, 9),
        ("deferred probe 3 (probe 1 + one fetch of the STATIC cube array alone, by a single invocation)", AblProbe, 10),
        ("deferred probe 4 (probe 1 + one fetch of the DYNAMIC cube array alone, by a single invocation)", AblProbe, 11),
        ("deferred probe 5 (probe 1 + one fetch of the PAIR atlas alone, by a single invocation)", AblProbe, 12),
    };
    /// <summary>What each rung of a pass's ladder ADDS, in the ladder's own order, for the summary line. One
    /// name per rung of that pass in <see cref="_benchAbl"/>, the shipped-program rung last.</summary>
    private static readonly string[][] _benchAblNames =
    {
        new[] { "gbuf+worldpos", "list source", "relevance pre-pass", "light bodies", "record fetch", "weights combine",
                  "miss fill", "mirror tap", "film pass" },
        new[] { "gbuf+gate", "candidate ranking", "filtered taps" },
        new[] { "depth slab", "pair cull", "gbuf+pair taps+subtract" },
        new[] { "bracket, no draws", "first group", "every group, no shading", "shading+list source", "light bodies", "shadows+mirror", "hot branch" },
        new[] { "walk with no tap", "one tap at one pixel", "static cube fetch vs tap", "dyn cube fetch vs static", "pair fetch vs dyn" },
    };
    /// <summary>Each ablation step's measured GPU term for its own pass, in ms; NaN = the rung was refused.</summary>
    private static double[]? _benchAblMs;

    /// <summary>Total steps this run: the knob list and then the ablation ladder.</summary>
    private static int BenchTotal => (_benchSteps?.Length ?? 0) + _benchAbl.Length;
    /// <summary>The GPU term an ablation step's pass is timed by.</summary>
    private static GpuTerm BenchAblTerm(int pass)
        => pass == AblLights ? GpuTerm.Deferred : pass == AblShadows ? GpuTerm.DeferredShadow
         : pass == AblDebt ? GpuTerm.PairDebt : pass == AblBlended ? GpuTerm.Blended : GpuTerm.Deferred;
    /// <summary>The ladder site a step's pass swaps its program at: the probe steps are the deferred walk's
    /// own program, higher up its ladder.</summary>
    private static int AblSite(int pass)
        => pass == AblShadows ? Ablation.Shadows : pass == AblDebt ? Ablation.Debt
         : pass == AblBlended ? Ablation.Lit : Ablation.Lights;
    private static string BenchLabel(int step)
        => step < _benchSteps!.Length ? _benchSteps[step].Label : _benchAbl[step - _benchSteps.Length].Label;

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
        _benchAblMs = new double[_benchAbl.Length];
        _benchStart = Stopwatch.GetTimestamp();
        _log?.Always($"AlloyRenderer: [bench] {BenchTotal} steps ({_benchSteps.Length} knob + {_benchAbl.Length} ablation) "
                   + $"x ({BenchSettle} settle + {BenchMeasure} measured) frames -- hold the camera still");
        Notice.Show(_log, $"Alloy bench started: {BenchTotal} steps, roughly {BenchTotal * (BenchSettle + BenchMeasure) / 3600.0:F0} min "
                        + "at 60 fps -- hold the camera still and do not move");
        _benchStep = 0;
        BenchApply();
    }

    /// <summary>Every knob back to what it was and every pass back to its shipped program, then this step's
    /// values over it. An ablation rung that will not link is logged and the step SKIPPED -- the ladder loses a
    /// rung, the run goes on, and nothing about the pass itself is stood down.</summary>
    private static void BenchApply()
    {
        while (_benchStep >= 0)
        {
            for (int i = 0; i < _benchKnobs!.Length; i++) _benchKnobs[i].Parse(_benchWas![i]);
            Ablation.Restore(_log);
            if (_benchStep < _benchSteps!.Length)
            {
                foreach ((int k, string v) in _benchSteps[_benchStep].Set) _benchKnobs[k].Parse(v);
                _benchFrame = 0;
                return;
            }
            int a = _benchStep - _benchSteps.Length;
            (string label, int pass, int rung) = _benchAbl[a];
            bool ok = Ablation.Rung(AblSite(pass), rung, _log);
            if (ok) { _benchFrame = 0; return; }
            _log?.Always($"AlloyRenderer: [bench] {label}: REFUSED -- that ablation variant did not link (the driver's "
                       + "own log is above); the pass kept its shipped program and the step is skipped");
            _benchAblMs![a] = double.NaN;
            if (++_benchStep >= BenchTotal) { BenchFinish("done"); return; }
        }
    }

    /// <summary>Once per present. Idle: one compare.</summary>
    private static void BenchTick()
    {
        if (_benchStep < 0) return;
        _benchFrame++;
        if (_benchStep == 0 && _benchFrame == BenchSettle) BlendProfile.Arm();
        if (_benchFrame == BenchSettle)
        {
            _benchT0 = Stopwatch.GetTimestamp();
            _benchSpan0 = _spanSum; _benchSpanN0 = _spanN;
            for (int t = 0; t < _qTerm.Length; t++) { _benchSum0[t] = _qTerm[t].Sum; _benchN0[t] = _qTerm[t].N; }
            _benchBake0 = CasterPass.ShadowGpuSum; _benchBakeN0 = CasterPass.ShadowGpuN;
            _benchHot0 = CasterPass.HotSlots; _benchSph0 = CasterPass.HotSpheres; _benchEng0 = CasterPass.HotEngulf; _benchMov0 = CasterPass.MoverFrames;
            return;
        }
        if (_benchFrame < BenchSettle + BenchMeasure) return;
        // a period line reset the meters mid-window: this step's deltas are garbage, so measure it again
        if (_spanN < _benchSpanN0 || _qTerm[(int)GpuTerm.Opaque].N < _benchN0[(int)GpuTerm.Opaque]
            || CasterPass.ShadowGpuN < _benchBakeN0) { _benchFrame = BenchSettle - 1; return; }
        double frame = (Stopwatch.GetTimestamp() - _benchT0) * 1000.0 / Stopwatch.Frequency / BenchMeasure;
        double span = _spanN > _benchSpanN0 ? (_spanSum - _benchSpan0) / (_spanN - _benchSpanN0) / 1e6 : 0.0;
        double lit = BenchTerm(GpuTerm.Opaque) + BenchTerm(GpuTerm.OpaqueKey) + BenchTerm(GpuTerm.Cutout) + BenchTerm(GpuTerm.CutoutKey);
        double blend = BenchTerm(GpuTerm.Blended);
        double all = 0.0;
        for (int t = 0; t < _qTerm.Length; t++) all += BenchTerm((GpuTerm)t);
        int bakeN = CasterPass.ShadowGpuN - _benchBakeN0;
        double bake = bakeN > 0 && CasterPass.ShadowGpuSum >= _benchBake0 ? (CasterPass.ShadowGpuSum - _benchBake0) / (double)bakeN / 1e6 : 0.0;
        if (_benchStep == 0) { _benchBaseBlend = blend; _benchBaseFrame = frame; _benchBaseSpan = span; _benchBaseLit = lit; _benchBaseAll = all; _benchBaseBake = bake; }
        _benchSaved![_benchStep] = span - _benchBaseSpan;
        // an ablation step's own pass term, and the cost of the stage this rung added over the last.
        string stage = "";
        if (_benchStep >= _benchSteps!.Length)
        {
            int a = _benchStep - _benchSteps.Length;
            (_, int pass, _) = _benchAbl[a];
            double ms = BenchTerm(BenchAblTerm(pass));
            _benchAblMs![a] = ms;
            int first = a; while (first > 0 && _benchAbl[first - 1].Pass == pass) first--;
            double prev = a > first && !double.IsNaN(_benchAblMs[a - 1]) ? _benchAblMs[a - 1] : 0.0;
            string[] names = _benchAblNames[pass];
            int rung = a - first;
            stage = $"; this pass {ms:F3} ms, stage \"{(rung < names.Length ? names[rung] : "?")}\" costs {ms - prev:+0.000;-0.000} ms";
        }
        _log?.Always($"AlloyRenderer: [bench] {BenchLabel(_benchStep)}: frame {frame:F2} ms ({1000.0 / frame:F0} fps, {frame - _benchBaseFrame:+0.00;-0.00}), "
                   + $"gpu span {span:F2} ({span - _benchBaseSpan:+0.00;-0.00}), lit colour {lit:F2} ({lit - _benchBaseLit:+0.00;-0.00}), "
                   + $"blended {blend:F2} ({blend - _benchBaseBlend:+0.00;-0.00}), "
                   + $"all gpu terms {all:F2} ({all - _benchBaseAll:+0.00;-0.00}), shadow bake {bake:F2} ({bake - _benchBaseBake:+0.00;-0.00})"
                   + stage + $"; scene: hot lights {(CasterPass.HotSlots - _benchHot0) / (double)BenchMeasure:F1}/f, "
                   + $"mover spheres {(CasterPass.HotSpheres - _benchSph0) / (double)BenchMeasure:F1}/f ({(CasterPass.HotEngulf - _benchEng0) / (double)BenchMeasure:F1} round their light), "
                   + $"moving casters {(CasterPass.MoverFrames - _benchMov0) / (double)BenchMeasure:F1}/f");
        if (++_benchStep >= BenchTotal) { BenchFinish("done"); return; }
        BenchApply();
    }

    /// <summary>One term's mean over the measured window, in ms; 0 when it was not measured.</summary>
    private static double BenchTerm(GpuTerm t)
    {
        int n = _qTerm[(int)t].N - _benchN0[(int)t];
        return n > 0 && _qTerm[(int)t].Sum >= _benchSum0[(int)t] ? (_qTerm[(int)t].Sum - _benchSum0[(int)t]) / (double)n / 1e6 : 0.0;
    }

    /// <summary>The ladder, differenced: one line per pass reading <c>total = stage + stage + ...</c>, so the
    /// reader is told where a pass's time sits rather than handed six numbers to subtract. A pass whose ladder
    /// never ran (no dispatch this session, or every rung refused) says so instead of printing zeros.</summary>
    private static void BenchAblSummary()
    {
        if (_benchAblMs == null) return;
        for (int a = 0; a < _benchAbl.Length;)
        {
            int pass = _benchAbl[a].Pass, end = a;
            while (end < _benchAbl.Length && _benchAbl[end].Pass == pass) end++;
            string[] names = _benchAblNames[pass];
            double top = double.IsNaN(_benchAblMs[end - 1]) ? 0.0 : _benchAblMs[end - 1];
            string s = "";
            double prev = 0.0;
            for (int i = a; i < end; i++)
            {
                // a name per rung, side by side in this file; a mismatch is a typo, not a crash (#FeatPrimKeys)
                string nm = i - a < names.Length ? names[i - a] : $"rung {i - a}";
                if (double.IsNaN(_benchAblMs[i])) { s += (s.Length > 0 ? " + " : "") + $"{nm} REFUSED"; continue; }
                s += (s.Length > 0 ? " + " : "") + $"{nm} {_benchAblMs[i] - prev:F3}";
                prev = _benchAblMs[i];
            }
            string who = pass == AblLights ? "cx_deferred_cs" : pass == AblShadows ? "cx_defshadow_cs"
                       : pass == AblDebt ? "cx_pairdebt" : pass == AblBlended ? "blended accumulation (lit program)"
                       : "cx_deferred_cs probe";
            _log?.Always(top > 0.0
                ? $"AlloyRenderer: [bench] ablation: {who} {top:F3} ms = {s} (ms, per stage, each measured with the stages above it compiled out)"
                : $"AlloyRenderer: [bench] ablation: {who} never dispatched in this run -- nothing to price");
            a = end;
        }
    }

    private static void BenchFinish(string why)
    {
        for (int i = 0; i < _benchKnobs!.Length; i++) _benchKnobs[i].Parse(_benchWas![i]);
        Ablation.Restore(_log);
        int done = _benchStep;
        _benchStep = -1;
        BenchAblSummary();
        double mins = (Stopwatch.GetTimestamp() - _benchStart) / (double)Stopwatch.Frequency / 60.0;
        _log?.Always($"AlloyRenderer: [bench] {why} -- every knob restored, every pass back on its shipped program");
        // The player's own end-of-run line. The bench runs for minutes with the camera held still, and a run
        // that ended with nothing said is a run nobody knows to stop holding still for -- aborted and refused
        // runs included, which is why this sits in Finish and not after the last step. Notice's sink is the CX
        // chatbox's own LOCAL line (ChatService.EchoLocal: the local store, nothing on the wire, no opcode);
        // with no sink -- chatbox stood down, main menu -- Notice logs it and drops it, which is the fallback
        // and already this mechanism's own documented ceiling.
        Notice.Show(_log, $"Alloy bench {why} ({done} of {BenchTotal} steps, {mins:F1} min) -- full results in cx.log [bench]"
                        + BenchTopSavings());
    }

    /// <summary>The three steps that cut the GPU span most, for the finish notice -- the knob name alone, which
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
            s += (s.Length > 0 ? ", " : ". Biggest GPU savings: ") + $"{(cut > 0 ? label[..cut] : label)} {_benchSaved[best]:0.00} ms";
            _benchSaved[best] = 0.0;   // taken: the next rank finds the next one down
        }
        return s;
    }
}
