using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// The CONTENT stages: every <c>customShaderFS</c> a <c>.mtr</c> pairs with a vertex stage, that Alloy neither reproduces
/// nor hosts by name. Each takes an id of its own and a lit program pair (forward, deferred twin) spliced from the lit
/// stage's text with the stage translated into its host region (<see cref="ContentStage"/>). Ids come on any thread;
/// programs build on the render thread the frame after a batch first asks, and until then the parts draw plain.
/// </summary>
internal static unsafe class ContentPrograms
{
    /// <summary>The issue loop's program number for a content stage's programs: <c>ProgBase + 2 * index</c> forward,
    /// one more for the deferred twin. Above every number the named lit programs use.</summary>
    internal const int ProgBase = 100;

    /// <summary>The fragment stages <c>base_shaders.bif</c> carries. Alloy's own stage IS their shading, so a
    /// material naming one as its custom stage loses nothing and none is hosted. Oracle: the bif's own listing.</summary>
    internal static readonly string[] StockFs =
    [
        "fs", "fsc", "fsc_ls", "fsc_sm", "fsfbdof", "fsfbgam", "fsfblvls", "fsfbpostpr", "fsfbshrp", "fsfbssao",
        "fsfbvib", "fsglu", "fsgrass", "fslit", "fslit_nm", "fslit_sm", "fslit_sm_nm", "fsparticle", "fst", "fstc",
        "fstc_sm", "fst_sm", "fswater", "fs_beamvol", "fs_cg_btn_col", "fs_invalid", "fs_pltgen", "fs_shadowplane",
        "fs_shadowvol",
    ];

    /// <summary>One content stage: its name, its programs in this context and what its translation said.</summary>
    private sealed class Stage
    {
        public string Resref = "";
        public uint Fwd, Def;
        public AlloyRenderer.LitLoc FwdLoc, DefLoc;
        public int FwdNoise = -1, DefNoise = -1;
        /// <summary>A batch asked for the stage and its programs are not built: the next frame builds them.</summary>
        public bool Wanted;
        /// <summary>The stage could not translate or build against this module. A fact about the source, kept until
        /// the module changes.</summary>
        public bool Failed;
        public bool Cuts, ReadsPlt;
        public ContentStage.Param[] Params = [];
        /// <summary>Bumped at every build: a side-table row filled against an older layout fills again.</summary>
        public int Epoch;
    }

    /// <summary>The registry: ids live for the session (a recipe holds one), programs for the context and module.</summary>
    private sealed class RegistryState
    {
        public readonly Stage?[] ById = new Stage?[ShaderFeatures.MaxContent];
        public readonly Dictionary<string, int> ByName = new(StringComparer.OrdinalIgnoreCase);
        public int Count;
        public long Spilled, Builds, Failures;
        public ExtenderLog? Log;
        /// <summary>What <see cref="Loc"/> hands back for a number no stage owns. Never uploaded through: no
        /// program answers that number, so the issue loop never binds one.</summary>
        public AlloyRenderer.LitLoc None;
    }

    private static RegistryState _reg = new();
    /// <summary>Over the name table: recipes are interned on the scene feed's workers.</summary>
    private static readonly object _lock = new();

    internal static void Attach(ExtenderLog log) => _reg.Log = log;

    /// <summary>The feature id <paramref name="mtr"/>'s content stage runs under, or 0: no stage, a stage vanilla
    /// itself would not build (it builds a content program only when the material names both halves), a stock one,
    /// or one Alloy hosts or reproduces by name. The caller has already found no reproduced vertex feature.</summary>
    internal static int Register(Db.MtrDef? mtr)
    {
        if (mtr == null || mtr.ShaderVs.Length == 0 || mtr.ShaderFs.Length == 0) return 0;
        string fs = mtr.ShaderFs;
        if (Named(fs)) return 0;
        lock (_lock)
        {
            RegistryState s = _reg;
            if (s.ByName.TryGetValue(fs, out int have)) return have;
            if (s.Count >= ShaderFeatures.MaxContent)
            {
                if (s.Spilled++ == 0)
                    s.Log?.Always($"ContentPrograms: {ShaderFeatures.MaxContent} content stages is the session's whole id range -- "
                                + $"\"{fs}\" and any later one draw the plain lit path");
                return 0;
            }
            int feat = (int)ShaderFeature.Content0 + s.Count;
            s.ById[s.Count++] = new Stage { Resref = fs };
            s.ByName[fs] = feat;
            return feat;
        }
    }

    /// <summary>Is <paramref name="fs"/> a stage Alloy never translates: a stock one, or one it hosts or reproduces by
    /// name? Whole-name and case-blind, as the engine's own cache compares them.</summary>
    internal static bool Named(string fs)
    {
        foreach (string n in StockFs) if (fs.Equals(n, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string n in ShaderFeatures.FsNames) if (n.Length > 0 && fs.Equals(n, StringComparison.OrdinalIgnoreCase)) return true;
        return ShaderFeatures.CoversFromNormalAlpha(fs);
    }

    private static Stage? Of(int feat)
    {
        if (!ShaderFeatures.IsContent(feat)) return null;
        int i = feat - (int)ShaderFeature.Content0;
        return i < _reg.Count ? _reg.ById[i] : null;
    }

    /// <summary>Is this content stage's program built and its body running? Render thread. Asking is what queues
    /// the build: a stage nothing draws never compiles.</summary>
    internal static bool Ready(int feat)
    {
        Stage? s = Of(feat);
        if (s == null || s.Failed) return false;
        if (s.Fwd != 0) return true;
        s.Wanted = true;
        return false;
    }

    /// <summary>Does this content stage make its own coverage (its own discard, a written alpha, or an alpha the
    /// stock setup does not cut on)? Only once it runs: before that its parts take Alloy's cut like any other.</summary>
    internal static bool Cuts(int feat) => Of(feat) is { Fwd: not 0, Cuts: true };

    /// <summary>Does the stage read the engine's PLT scheme feed? Its records then carry the wearer's palette rows.</summary>
    internal static bool ReadsPlt(int feat) => Of(feat) is { ReadsPlt: true };

    /// <summary>Has this content stage a deferred twin to draw its opaque and cutout batches through?</summary>
    internal static bool HasDeferred(int feat) => Of(feat) is { Fwd: not 0, Def: not 0 };

    /// <summary>The stage's parameter lanes and the layout generation they belong to, for the side-table fill.</summary>
    internal static ContentStage.Param[] Params(int feat) => Of(feat)?.Params ?? [];
    internal static int Epoch(int feat) => Of(feat)?.Epoch ?? 0;

    /// <summary>The issue loop's number for content stage <paramref name="feat"/>'s forward program or deferred twin.</summary>
    internal static int ProgId(int feat, bool deferred) => ProgBase + 2 * (feat - (int)ShaderFeature.Content0) + (deferred ? 1 : 0);

    internal static bool IsProg(int which) => which >= ProgBase;
    internal static bool IsDeferred(int which) => which >= ProgBase && ((which - ProgBase) & 1) == 1;

    private static Stage? OfProg(int which)
        => which >= ProgBase ? Of((int)ShaderFeature.Content0 + (which - ProgBase) / 2) : null;

    /// <summary>The GL program behind issue-loop number <paramref name="which"/>.</summary>
    internal static uint Program(int which)
        => OfProg(which) is { } s ? (IsDeferred(which) ? s.Def : s.Fwd) : 0;

    /// <summary>The uniform locations of issue-loop number <paramref name="which"/>.</summary>
    internal static ref AlloyRenderer.LitLoc Loc(int which)
    {
        Stage? s = OfProg(which);
        if (s == null) return ref _reg.None;
        return ref IsDeferred(which) ? ref s.DefLoc : ref s.FwdLoc;
    }

    /// <summary>What a content program reads beyond the lit stage's own terms: the engine's <c>solid_noise</c>, as a
    /// handle (<c>texUnitNoise</c>). Called at the program's bind.</summary>
    internal static void BindExtras(int which)
    {
        Stage? s = OfProg(which);
        if (s == null) return;
        int loc = IsDeferred(which) ? s.DefNoise : s.FwdNoise;
        if (loc < 0 || Gl.Uniform2ui == null) return;
        ulong h = AlloyRenderer.Bindless ? AlloyRenderer.HandleFor(FrameSnapshot.ContentNoiseTid()) : 0ul;
        Gl.Uniform2ui(loc, (uint)h, (uint)(h >> 32));
    }

    /// <summary>Build every stage a batch asked for since the last frame. Render thread, in the bracket, ahead of
    /// every batch: the lit program must exist, because a content program is spliced from its text.</summary>
    internal static void EnsureWanted()
    {
        if (Programs._litFsSrc == null || Programs._litVsSrc == null) return;
        RegistryState r = _reg;
        for (int i = 0; i < r.Count; i++)
            if (r.ById[i] is { Wanted: true } s) Build(s);
    }

    private static void Build(Stage s)
    {
        s.Wanted = false;
        string what = $"Alloy content stage {s.Resref}";
        string? fs = WithRegion(Programs._litFsSrc!, s.Resref);
        if (fs == null)
        {
            Fail(s, "the lit stage in use carries no cx_host_f region to host it in (a content override of cx_ownlit_fs "
                  + "that predates hosting)");
            return;
        }
        string vs = Programs._litVsSrc!;
        string? def = Programs._progLitDef != 0 ? Programs.WithDeferred(fs) : null;
        int prev;
        Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &prev);
        using (GlProgram.Prefetch([(vs, fs, true), (vs, def, true)], _reg.Log))
        {
            uint fwd = GlProgram.Graphics(vs, fs, _reg.Log, what, host: true);
            bool stubbed = GlProgram.LastStubbed;
            ContentStage.Result? t = ContentStage.Last(s.Resref);
            if (fwd == 0 || stubbed || t == null || t.Refused != null)
            {
                if (fwd != 0) Gl.DeleteProgram(fwd);
                Fail(s, t?.Refused ?? (fwd == 0 ? "its program did not compile or link (the lines above say why)"
                                                : "it compiled only with its body stubbed (the lines above say why)"));
                Gl.UseProgram((uint)prev);
                return;
            }
            uint d = def != null ? GlProgram.Graphics(vs, def, _reg.Log, what + " (deferred twin)", host: true) : 0;
            if (d != 0 && GlProgram.LastStubbed) { Gl.DeleteProgram(d); d = 0; }
            s.Fwd = fwd; s.FwdLoc = Programs.LitLocs(fwd); s.FwdNoise = GlProgram.Loc(fwd, "uConNoise");
            s.Def = d;
            if (d != 0) { s.DefLoc = Programs.LitLocs(d); s.DefNoise = GlProgram.Loc(d, "uConNoise"); }
            s.Cuts = t.Cuts; s.ReadsPlt = t.ReadsPlt; s.Params = t.Params; s.Epoch++;
            _reg.Builds++;
            _reg.Log?.Always($"{what}: runs translated -- {t.Params.Length} parameter lane(s)"
                           + (t.Cuts ? ", its own coverage (out of the depth prepass)" : "")
                           + (d == 0 ? ", forward only (no deferred twin)" : "")
                           + (t.Notes.Count == 0 ? "" : "; it " + string.Join("; it ", t.Notes)));
        }
        Gl.UseProgram((uint)prev);
    }

    private static void Fail(Stage s, string why)
    {
        s.Failed = true;
        _reg.Failures++;
        _reg.Log?.Always($"Alloy content stage {s.Resref}: NOT RUN -- {why}. Its parts draw the plain lit path");
    }

    /// <summary>The lit stage's text with its hosted-stage library replaced by <paramref name="resref"/>'s content
    /// region, whose stub is the dispatcher that hosts nothing. Null when the text carries no such library.</summary>
    internal static string? WithRegion(string lit, string resref)
    {
        string open = ShaderSource.LibMark + HostLib + "\n", close = ShaderSource.EndMark + HostLib + "\n";
        int a = lit.IndexOf(open, StringComparison.Ordinal);
        int b = a < 0 ? -1 : lit.IndexOf(close, a, StringComparison.Ordinal);
        if (b < 0) return null;
        return string.Concat(lit.AsSpan(0, a), VanillaShader.ContentMark + resref + "\n" + RegionStub + "// $VANILLA-END\n",
                             lit.AsSpan(b + close.Length));
    }

    /// <summary>The library a content region replaces: the named hosted stages' dispatcher.</summary>
    private const string HostLib = "cx_host_f";

    /// <summary>What a content region compiles to when the stage is refused: no body for any feature.</summary>
    internal const string RegionStub = "bool CxHostSurface(int feat) { return false; }\n";

    /// <summary>A module change: every content program goes, and a stage that failed against the old module may
    /// build against the new one. Ids stay -- the recipes holding them outlive the edge.</summary>
    internal static void DropSpliced()
    {
        RegistryState r = _reg;
        for (int i = 0; i < r.Count; i++)
        {
            if (r.ById[i] is not { } s) continue;
            if (s.Fwd != 0) Gl.DeleteProgram(s.Fwd);
            if (s.Def != 0) Gl.DeleteProgram(s.Def);
            s.Fwd = 0; s.Def = 0; s.Failed = false; s.Cuts = false; s.Wanted = false;
        }
        ContentStage.Forget();
    }

    /// <summary>The context took every program name: forget them, and rebuild on the next ask.</summary>
    internal static void ContextLost()
    {
        RegistryState r = _reg;
        for (int i = 0; i < r.Count; i++)
            if (r.ById[i] is { } s) { s.Fwd = 0; s.Def = 0; s.Wanted = false; }
    }

    /// <summary>The period line's clause: stages named, running, refused.</summary>
    internal static string Note()
    {
        RegistryState r = _reg;
        if (r.Count == 0) return "";
        int running = 0, failed = 0;
        for (int i = 0; i < r.Count; i++)
            if (r.ById[i] is { } s) { if (s.Fwd != 0) running++; if (s.Failed) failed++; }
        return $"content stages: {r.Count} named, {running} running, {failed} NOT RUN"
             + (r.Spilled == 0 ? "" : $", {r.Spilled} past the id range") + "; ";
    }

    /// <summary>Off-device. Oracle: hand-written <c>.mtr</c> texts and the engine's own rules as the contract
    /// research states them -- both halves named or no program, stock and named stages never translated, one id
    /// per stage whatever its case -- and the issue-loop numbering worked by hand.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        static Db.MtrDef Mtr(string text) => Db.MtrDef.Parse(System.Text.Encoding.ASCII.GetBytes(text), "selfcheck");
        RegistryState saved = _reg;
        _reg = new RegistryState { Log = saved.Log };
        try
        {
            check.Expect(Register(Mtr("customshaderfs cx_selfcheck_a\n")) == 0,
                         "a material naming only a fragment stage gets a content id, where the engine builds nothing");
            int a = Register(Mtr("customshadervs vslit\ncustomshaderfs cx_selfcheck_a\n"));
            int b = Register(Mtr("customShaderVS VSLIT_NM\ncustomShaderFS CX_SELFCHECK_A\n"));
            check.Expect(a == 13 && b == 13, $"the first content stage is id 13 for either case of its name (got {a}, {b})");
            check.Expect(Register(Mtr("customshadervs vslit\ncustomshaderfs cx_selfcheck_b\n")) == 14,
                         "a second stage does not take the next id");
            check.Expect(Register(Mtr("customshadervs vslit_nm\ncustomshaderfs fslit_nm\n")) == 0
                         && Register(Mtr("customshadervs td_ground_vs\ncustomshaderfs td_ground_fs\n")) == 0
                         && Register(Mtr("customshadervs vslit_sm_nm\ncustomshaderfs fs_plt_hair\n")) == 0,
                         "a stock, a hosted or a reproduced stage is translated");
            check.Expect(ProgId(13, false) == 100 && ProgId(13, true) == 101 && ProgId(14, false) == 102
                         && IsDeferred(101) && !IsDeferred(102) && !IsProg(9),
                         "the issue-loop numbering is not 100 + 2 * index, plus one for the twin");
            check.Expect(!Ready(13) && _reg.ById[0]!.Wanted && !Ready(12),
                         "an unbuilt stage reads ready, does not queue its build, or a named feature reads as content");
            string region = WithRegion("head\n//@cx-lib cx_host_f\nbody\n//@cx-end cx_host_f\ntail\n", "rambo") ?? "";
            check.Expect(region == "head\n// $VANILLA-CONTENT rambo\n" + RegionStub + "// $VANILLA-END\ntail\n",
                         "the hosted library is not replaced by the content region, brackets and all");
            check.Expect(WithRegion("no library here\n", "rambo") == null, "a lit stage with no host library takes a region");
        }
        finally { _reg = saved; }
        log.Info(check.Ok ? "ContentPrograms.SelfCheck: passed" : "ContentPrograms.SelfCheck: FAILED");
        return check.Ok;
    }
}
