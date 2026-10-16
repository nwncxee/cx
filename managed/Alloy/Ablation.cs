using CX.Framework;
using CX.Graphics;

using static CX.Alloy.AlloyRenderer;

namespace CX.Alloy;

/// <summary>
/// The ABLATION LADDER behind <c>/alloy bench</c>: a pass's own stage rebuilt with <c>#define CX_ABLATE n</c>, so
/// the shader stops after rung n. Two rungs' GPU terms differ by exactly the stage between them, which prices a
/// pass whose total is known and whose shape is not. This is the one indirection the ladder reaches a pass
/// through -- a pass exposes the program it binds and holds no state for this.
/// </summary>
internal static unsafe class Ablation
{
    /// <summary>The swappable program sites: the three compute passes, the lit program the blended
    /// accumulation draws with, and the fringe pass (whose one rung draws nothing).</summary>
    internal const int Lights = 0, Shadows = 1, Debt = 2, Lit = 3, Fringe = 4;
    /// <summary>The longest compute ladder (the deferred walk's eight rungs, its probes at 9..12) and the
    /// blended one's. Rung 0 is the shipped program, which no variant stands for.</summary>
    internal const int MaxRung = 12, MaxLitRung = 6;
    private const int Sites = 3;

    /// <summary>The issue loop stops after this many batches that actually draw, 0 = all of them. Read once per
    /// batch there, and non-zero only inside <see cref="Blended"/>; no field on this class has an initialiser,
    /// so the class takes no static constructor and that read is a plain static load.</summary>
    internal static int DrawCap;

    /// <summary>The fringe pass's one rung: open and close its GPU term with no draw in it.</summary>
    private sealed class FringeRungState { public bool Skip; }
    private static readonly FringeRungState _fringe = new();
    /// <summary>The fringe pass draws nothing this step (rung 1 of its ladder).</summary>
    internal static bool FringeSkip => _fringe.Skip;

    /// <summary>Per site and rung: the variant program (0 = not built) and the latch that stops a refused rung
    /// recompiling on every step. Lazy -- nothing is built at boot or on a frame no bench is driving.</summary>
    private static uint[]? _progs;
    private static bool[]? _failed;
    /// <summary>Per site: the program the pass had before the ladder pointed it elsewhere, 0 = it is on its own.
    /// Read off the pass, so whichever of its own programs was bound is what comes back.</summary>
    private static uint[]? _shipped;

    /// <summary>Put <paramref name="site"/> on rung <paramref name="rung"/>, or back on its own program at 0.
    /// False = that rung did not link, which refuses THE STEP: the pass keeps its program and the feature with
    /// it. <c>check-shaders.py</c> driver-links every rung, so that is a failed gate before it is a bench
    /// surprise.</summary>
    internal static bool Rung(int site, int rung, ExtenderLog? log)
    {
        if (site == Lit) return LitRung(rung, log);
        if (site == Fringe) { _fringe.Skip = rung == 1; return rung <= 1; }
        uint shipped = _shipped == null ? 0u : _shipped[site];
        if (rung <= 0)
        {
            if (shipped != 0) { Bind(site, shipped); _shipped![site] = 0; }
            return true;
        }
        if (shipped == 0)
        {
            shipped = Bound(site);
            if (shipped == 0) return false;   // the pass never linked: there is nothing to ablate
            (_shipped ??= new uint[Sites])[site] = shipped;
        }
        uint p = Variant(site, rung, log);
        if (p == 0) { Bind(site, shipped); _shipped![site] = 0; return false; }
        Bind(site, p);
        return true;
    }

    /// <summary>Every site back on its own program: each step boundary, and finish.</summary>
    internal static void Restore(ExtenderLog? log)
    {
        for (int s = 0; s < Sites; s++) Rung(s, 0, log);
        LitRung(0, log);
        _fringe.Skip = false;
    }

    /// <summary>The variants are gone: the context that held them was lost, or <paramref name="delete"/> -- the
    /// module changed under the spliced programs, whose names are still live. A rung that would not link gets
    /// another try on what replaces it.</summary>
    internal static void Forget(bool delete)
    {
        if (delete)
        {
            Restore(null);
            if (_progs != null) foreach (uint p in _progs) if (p != 0) Gl.DeleteProgram(p);
            if (_litProgs != null) foreach (uint p in _litProgs) if (p != 0) Gl.DeleteProgram(p);
        }
        _progs = null; _failed = null; _shipped = null;
        _litProgs = null; _litLocs = null;
        _litProg = 0; _litList = 0; _litCap = 0; _litSkip = false;
        _fringe.Skip = false;
    }

    /// <summary>One rung's program, built on the first step that asks for it.</summary>
    private static uint Variant(int site, int rung, ExtenderLog? log)
    {
        if (rung < 1 || rung > MaxRung) return 0;
        _progs ??= new uint[Sites * (MaxRung + 1)];
        _failed ??= new bool[Sites * (MaxRung + 1)];
        int i = site * (MaxRung + 1) + rung;
        if (_progs[i] != 0) return _progs[i];
        if (_failed[i]) return 0;
        string resref = Resref(site);
        // The override's text where there is one, so a rung measures the stage the session actually runs.
        string src = ShaderResource.Override(resref, log) ?? ShaderStages.Source(resref);
        uint p = GlProgram.Compute(ShaderStages.WithDefine(src, $"CX_ABLATE {rung}"), log, $"{Name(site)} ablate {rung}");
        if (p == 0) { _failed[i] = true; return 0; }
        _progs[i] = p;
        return p;
    }

    private static uint Bound(int site)
        => site == Lights ? DeferredLights.Program : site == Shadows ? DeferredShadows.Program : PairDebt.Program;

    private static void Bind(int site, uint prog)
    {
        if (site == Lights) DeferredLights.Program = prog;
        else if (site == Shadows) DeferredShadows.Program = prog;
        else PairDebt.Program = prog;
    }

    private static string Resref(int site)
        => site == Lights ? DeferredLights.ShdCs : site == Shadows ? DeferredShadows.ShdCs : PairDebt.ShdCs;

    private static string Name(int site)
        => site == Lights ? "DeferredLights" : site == Shadows ? "DeferredShadows" : "PairDebt";

    // ---- the blended accumulation's ladder: the lit program, not a compute pass ----------------------------

    /// <summary>Per rung: the accumulation's variant at <c>rung * 2</c> and its OIT-lists twin at
    /// <c>rung * 2 + 1</c> (0 = none), each with the locations it took at its own link.</summary>
    private static uint[]? _litProgs;
    private static LitLoc[]? _litLocs;
    private static uint _litProg, _litList;
    private static LitLoc _litLoc, _litListLoc;
    /// <summary>The two draw-side rungs: draw nothing at all, or stop after the first batch that draws -- a
    /// one-time wait costs the same as every group, a per-draw cost almost nothing.</summary>
    private static bool _litSkip;
    private static int _litCap;

    /// <summary>What each rung adds to the lit program: rungs 1 and 2 are unshaded, then no shading at all, the
    /// walk stopped after its list and after its light bodies, and the whole walk with no hot branch.
    /// <c>check-shaders.py</c> splices the identical set.</summary>
    private static string LitDefines(int rung) => rung switch
    {
        2 or 3 => "CX_BLEND_FLAT 1",
        4 => "CX_NO_HOT 1\n#define CX_ABLATE 2",
        5 => "CX_NO_HOT 1\n#define CX_ABLATE 4",
        6 => "CX_NO_HOT 1",
        _ => "",
    };

    private static bool LitRung(int rung, ExtenderLog? log)
    {
        _litProg = 0; _litList = 0;
        _litSkip = rung == 1;
        _litCap = rung == 2 ? 1 : 0;
        if (rung <= 1) return true;
        if (Programs._litFsSrc == null || Programs._litVsSrc == null || rung > MaxLitRung) return false;
        _litProgs ??= new uint[(MaxLitRung + 1) * 2];
        _litLocs ??= new LitLoc[(MaxLitRung + 1) * 2];
        int k = rung * 2;
        if (_litProgs[k] == 0)
        {
            string fs = ShaderStages.WithDefine(Programs._litFsSrc, LitDefines(rung));
            _litProgs[k] = GlProgram.Graphics(Programs._litVsSrc, fs, log, $"AlloyRenderer lit program (blended ablate {rung})", host: false);
            if (_litProgs[k] == 0) return false;
            _litLocs[k] = Programs.LitLocs(_litProgs[k]);
            if (Programs._progLitList != 0)
            {
                _litProgs[k + 1] = GlProgram.Graphics(Programs._litVsSrc, OitPass.WithLists(fs), log,
                                                      $"AlloyRenderer lit program (blended ablate {rung}, OIT lists)", host: false);
                if (_litProgs[k + 1] == 0) return false;
                _litLocs[k + 1] = Programs.LitLocs(_litProgs[k + 1]);
            }
        }
        _litProg = _litProgs[k]; _litLoc = _litLocs[k];
        _litList = _litProgs[k + 1]; _litListLoc = _litLocs[k + 1];
        return true;
    }

    /// <summary>The blended accumulation's scope: the lit slots point at this step's variant for the length of
    /// its draws and are put back when it closes, so nothing else in the frame draws with one.</summary>
    internal ref struct Blended
    {
        private uint _lit, _list;
        private LitLoc _litL, _listL;
        /// <summary>This rung draws nothing: the bracket opens and closes with no draw in it.</summary>
        internal bool Skip;

        internal void Begin()
        {
            _lit = Programs._progLit46; _list = Programs._progLitList; _litL = Programs._lit; _listL = Programs._litL;
            Skip = _litSkip;
            DrawCap = _litCap;
            if (_litProg != 0) { Programs._progLit46 = _litProg; Programs._lit = _litLoc; }
            if (_litList != 0) { Programs._progLitList = _litList; Programs._litL = _litListLoc; }
        }

        internal void Dispose()
        {
            DrawCap = 0;
            Programs._progLit46 = _lit; Programs._lit = _litL;
            Programs._progLitList = _list; Programs._litL = _listL;
        }
    }

    /// <summary>Open the accumulation's scope: <c>using (var step = Ablation.BlendedStep())</c>.</summary>
    internal static Blended BlendedStep()
    {
        Blended b = default;
        b.Begin();
        return b;
    }
}
