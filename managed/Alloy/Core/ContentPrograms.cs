using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// The CONTENT pairs: every <c>customShaderVS</c>/<c>customShaderFS</c> pair a <c>.mtr</c> names, keyed as the engine's
/// cache keys them, each with programs spliced from the lit stages with its halves translated (<see cref="ContentStage"/>):
/// forward, deferred, and -- for a pair placing or cutting its own depth -- depth and caster twins. They compile in the
/// background from the frame a batch first asks; until then the parts draw plain.
/// </summary>
internal static unsafe class ContentPrograms
{
    /// <summary>The issue loop's program number for a pair's programs: <c>ProgBase + 2 * index</c> forward, one more for
    /// the deferred twin. Above every number the named lit programs use.</summary>
    internal const int ProgBase = 100;

    /// <summary>One pair's programs and uniform locations in this context, and what its translation said.</summary>
    private sealed class ProgramSet
    {
        public uint Fwd, Def, Depth, Cast, CastLayer;
        public AlloyRenderer.LitLoc FwdLoc, DefLoc, DepthLoc, CastLoc, CastLayerLoc;
        public int FwdNoise = -1, DefNoise = -1, DepthNoise = -1, CastNoise = -1, CastLayerNoise = -1;
        /// <summary>What each program was attached as (<see cref="StageResources.Attach"/>): -1 is nothing per bind.</summary>
        public int FwdRec = -1, DefRec = -1, DepthRec = -1, CastRec = -1, CastLayerRec = -1;
        /// <summary>The depth twin's <c>uHlColor</c>: the outline-highlight mask draws through it.</summary>
        public int DepthHl = -1;
    }

    /// <summary>One content pair: its names, its programs, and the facts its build read off the translation.</summary>
    private sealed class Stage
    {
        public string Vs = "", Fs = "";
        /// <summary>What each half runs as: the native stage (<c>cx_</c> + the half, <see cref="StageResources"/>) where
        /// the chain has one, else the half itself -- and the native stage's id, -1 for a half hosted plainly.</summary>
        public string HostVs = "", HostFs = "";
        public int NatVs = -1, NatFs = -1;
        public ProgramSet P = new();
        /// <summary>A batch asked for the pair and its programs are not built: the next frame starts them.</summary>
        public bool Wanted;
        /// <summary>The background compile's keys while it runs (<see cref="GlProgram.Start"/>), null otherwise.</summary>
        public ulong[]? Keys;
        public string? VsSrc, FsSrc;
        /// <summary>The pair could not translate or build against this module. A fact about the source, kept until the
        /// module changes.</summary>
        public bool Failed;
        /// <summary>Both halves add nothing to their stock counterparts: the pair IS the plain lit path, and no program
        /// is built for it.</summary>
        public bool Plain;
        public bool Cuts, Moves, ReadsPlt, Post, ReadsLights, NoKeyhole;
        /// <summary>Is the vertex half known to add nothing to stock -- off the feed's text at registration, then the
        /// chain's at the build? A pair not known so may move its vertices, so its parts never frustum-cull.</summary>
        public bool VsStock;
        /// <summary>...and the fragment half, at the build: a stock half is Alloy's own stage, with no region.</summary>
        public bool FsStock;
        public ContentStage.Param[] Params = [];
        /// <summary>Bumped at every build: a content-table row filled against an older layout fills again.</summary>
        public int Epoch;
    }

    /// <summary>The registry: ids live for the session (a recipe holds one), programs for the context and module.</summary>
    private sealed class RegistryState
    {
        public readonly Stage?[] ById = new Stage?[ShaderFeatures.MaxContent];
        public readonly Dictionary<string, int> ByName = new(StringComparer.OrdinalIgnoreCase);
        public int Count;
        public long Spilled, Builds, Failures, StockPairs;
        public ExtenderLog? Log;
        /// <summary>What <see cref="Loc"/> hands back for a number no pair owns. Never uploaded through: no program
        /// answers that number, so the issue loop never binds one.</summary>
        public AlloyRenderer.LitLoc None;
    }

    private static RegistryState _reg = new();
    /// <summary>Over the name table: recipes are interned on the scene feed's workers.</summary>
    private static readonly object _lock = new();

    internal static void Attach(ExtenderLog log) => _reg.Log = log;

    /// <summary>The feature id <paramref name="mtr"/>'s content pair runs under, or 0: no pair (the engine builds a content
    /// program only when the material names both halves), or a pair whose halves both add nothing to stock -- read off
    /// the texts the material feed already holds; a pair whose texts it does not hold yet takes an id, and its build
    /// finds out.</summary>
    internal static int Register(Db.MtrDef? mtr)
    {
        if (mtr == null || mtr.ShaderVs.Length == 0 || mtr.ShaderFs.Length == 0) return 0;
        string vs = mtr.ShaderVs, fs = mtr.ShaderFs;
        string key = vs + "\n" + fs;
        lock (_lock)
        {
            RegistryState s = _reg;
            if (s.ByName.TryGetValue(key, out int have)) return have;
            bool vsStock = Stock(MtrFeed.ShaderText(vs), false);
            // A half with a native stage beside it is never stock: the feed reads the native text first, so by the
            // time both plain halves are known, so is whether either has one. Not gated on alloy.native: the id outlives
            // the knob, and with it off the build finds both halves stock and draws the plain lit path, as here.
            bool native = MtrFeed.ShaderText(StageResources.Prefix + vs) != null || MtrFeed.ShaderText(StageResources.Prefix + fs) != null;
            if (vsStock && !native && Stock(MtrFeed.ShaderText(fs), true))
            {
                s.ByName[key] = 0;
                s.StockPairs++;
                return 0;
            }
            if (s.Count >= ShaderFeatures.MaxContent)
            {
                if (s.Spilled++ == 0)
                    s.Log?.Always($"ContentPrograms: {ShaderFeatures.MaxContent} content pairs is the session's whole id range -- "
                                + $"\"{vs}\"/\"{fs}\" and any later one draw the plain lit path");
                return 0;
            }
            int feat = (int)ShaderFeature.Content0 + s.Count;
            s.ById[s.Count++] = new Stage { Vs = vs, Fs = fs, VsStock = vsStock };
            s.ByName[key] = feat;
            return feat;
        }
    }

    private static bool Stock(string? text, bool fragment) => text != null && ContentStage.StockShaped(text, fragment);

    private static Stage? Of(int feat)
    {
        if (!ShaderFeatures.IsContent(feat)) return null;
        int i = feat - (int)ShaderFeature.Content0;
        return i < _reg.Count ? _reg.ById[i] : null;
    }

    /// <summary>Is this pair's forward program built and its body running? Render thread. Asking is what queues the
    /// build: a pair nothing draws never compiles.</summary>
    internal static bool AskReady(int feat)
    {
        Stage? s = Of(feat);
        if (s == null || s.Failed || s.Plain) return false;
        // Staged ahead of the frame's head, so a native half's passes run before its parts draw.
        StageResources.Use(s.NatVs);
        StageResources.Use(s.NatFs);
        if ((s.NatVs >= 0 && !StageResources.Live(s.NatVs)) || (s.NatFs >= 0 && !StageResources.Live(s.NatFs)))
            Renew(s, "a native half stood down");
        if (s.P.Fwd != 0) return true;
        s.Wanted = true;
        return false;
    }

    /// <summary>Does this pair's fragment half make its own coverage (its own discard, a written alpha, or an alpha the
    /// stock setup does not cut on)? Only once it runs: before that its parts take Alloy's cut like any other.</summary>
    internal static bool Cuts(int feat) => Of(feat) is { P.Fwd: not 0, Cuts: true };

    /// <summary>Does this pair's vertex half run -- move the vertices, in general? Only once it runs.</summary>
    internal static bool Moves(int feat) => Of(feat) is { P.Fwd: not 0, Moves: true };

    /// <summary>May this pair move its vertices -- a vertex half not known to be stock? Asked when a record's cull sphere
    /// is written, which may be before the pair builds: such a part is never frustum-culled, as a posed skin is not.</summary>
    internal static bool MayMove(int feat) => Of(feat) is { VsStock: false };

    /// <summary>Is the depth this pair's parts write its OWN -- its vertex half places them, or its fragment half cuts
    /// them -- so the plain prepass and casters cannot stand in for it?</summary>
    internal static bool OwnDepth(int feat) => Of(feat) is { P.Fwd: not 0 } s && (s.Cuts || s.Moves);

    /// <summary>Does the pair read the engine's PLT scheme feed? Its records then carry the wearer's palette rows.</summary>
    internal static bool ReadsPlt(int feat) => Of(feat) is { ReadsPlt: true };

    /// <summary>Does the pair's fragment half switch keyholing off? Its parts then never dissolve, as in the engine.</summary>
    internal static bool NoKeyhole(int feat) => Of(feat) is { P.Fwd: not 0, NoKeyhole: true };

    /// <summary>Has this pair a deferred twin to draw its opaque and cutout batches through?</summary>
    internal static bool HasDeferred(int feat) => Of(feat) is { P: { Fwd: not 0, Def: not 0 } };

    /// <summary>The pair's parameter lanes and the layout generation they belong to, for the content-table fill.</summary>
    internal static ContentStage.Param[] Params(int feat) => Of(feat)?.Params ?? [];
    internal static int Epoch(int feat) => Of(feat)?.Epoch ?? 0;

    /// <summary>The issue loop's number for pair <paramref name="feat"/>'s forward program or deferred twin.</summary>
    internal static int ProgId(int feat, bool deferred) => ProgBase + 2 * (feat - (int)ShaderFeature.Content0) + (deferred ? 1 : 0);

    internal static bool IsProg(int which) => which >= ProgBase;
    internal static bool IsDeferred(int which) => which >= ProgBase && ((which - ProgBase) & 1) == 1;

    private static Stage? OfProg(int which)
        => which >= ProgBase ? Of((int)ShaderFeature.Content0 + (which - ProgBase) / 2) : null;

    /// <summary>The GL program behind issue-loop number <paramref name="which"/>.</summary>
    internal static uint Program(int which)
        => OfProg(which) is { } s ? (IsDeferred(which) ? s.P.Def : s.P.Fwd) : 0;

    /// <summary>The uniform locations of issue-loop number <paramref name="which"/>.</summary>
    internal static ref AlloyRenderer.LitLoc Loc(int which)
    {
        Stage? s = OfProg(which);
        if (s == null) return ref _reg.None;
        return ref IsDeferred(which) ? ref s.P.DefLoc : ref s.P.FwdLoc;
    }

    /// <summary>The pair's depth twin (the prepass's program for it), or 0.</summary>
    internal static uint DepthProgram(int feat) => Of(feat)?.P.Depth ?? 0;

    /// <summary>Where the depth twin takes the outline-highlight mask's colour, or -1.</summary>
    internal static int DepthHlColor(int feat) => Of(feat)?.P.DepthHl ?? -1;

    /// <summary>The depth twin's uniform locations.</summary>
    internal static ref AlloyRenderer.LitLoc DepthLoc(int feat)
    {
        Stage? s = Of(feat);
        if (s == null) return ref _reg.None;
        return ref s.P.DepthLoc;
    }

    /// <summary>The pair's caster twin -- the layered one for <paramref name="layered"/> -- or 0.</summary>
    internal static uint CastProgram(int feat, bool layered) => Of(feat) is { } s ? (layered ? s.P.CastLayer : s.P.Cast) : 0;

    /// <summary>A caster twin's uniform locations.</summary>
    internal static ref AlloyRenderer.LitLoc CastLoc(int feat, bool layered)
    {
        Stage? s = Of(feat);
        if (s == null) return ref _reg.None;
        return ref layered ? ref s.P.CastLayerLoc : ref s.P.CastLoc;
    }

    /// <summary>What a content program reads beyond the lit stage's own terms: the engine's <c>solid_noise</c>, as a
    /// handle (<c>texUnitNoise</c>). Called at the program's bind.</summary>
    internal static void BindExtras(int which)
    {
        Stage? s = OfProg(which);
        if (s == null) return;
        bool def = IsDeferred(which);
        BindNoise(def ? s.P.DefNoise : s.P.FwdNoise);
        StageResources.Bind(def ? s.P.DefRec : s.P.FwdRec);
    }

    /// <summary><see cref="BindExtras"/> for a pair's depth twin (<paramref name="cast"/> false) or caster twin, the
    /// layered one for <paramref name="layered"/>.</summary>
    internal static void BindTwinExtras(int feat, bool cast, bool layered)
    {
        Stage? s = Of(feat);
        if (s == null) return;
        BindNoise(!cast ? s.P.DepthNoise : layered ? s.P.CastLayerNoise : s.P.CastNoise);
        StageResources.Bind(!cast ? s.P.DepthRec : layered ? s.P.CastLayerRec : s.P.CastRec);
    }

    private static void BindNoise(int loc)
    {
        if (loc < 0 || Gl.Uniform2ui == null) return;
        ulong h = AlloyRenderer.Bindless ? AlloyRenderer.HandleFor(FrameSnapshot.OwnNoise()) : 0ul;
        Gl.Uniform2ui(loc, (uint)h, (uint)(h >> 32));
    }

    /// <summary>Start every pair a batch asked for since the last frame, and finish every one whose background compile
    /// is done. Render thread, in the bracket, ahead of every batch: the lit programs must exist, because a pair's
    /// programs are spliced from their text.</summary>
    internal static void EnsureWanted()
    {
        if (CX.Alloy.Programs._litFsSrc == null || CX.Alloy.Programs._litVsSrc == null) return;
        RegistryState r = _reg;
        for (int i = 0; i < r.Count; i++)
        {
            if (r.ById[i] is not { } s) continue;
            if (s.Keys != null) { if (GlProgram.Done(s.Keys)) Finish(s); }
            else if (s.Wanted) Start(s);
        }
    }

    /// <summary>Translate the pair and issue its programs to the driver's compiler threads; <see cref="Finish"/> claims
    /// them once they are done. A pair whose halves both add nothing to stock is the plain lit path and builds nothing.</summary>
    private static void Start(Stage s)
    {
        s.Wanted = false;
        s.HostVs = Host(s.Vs, out s.NatVs);
        s.HostFs = Host(s.Fs, out s.NatFs);
        string? vsText = VanillaShader.ChainText(s.HostVs), fsText = VanillaShader.ChainText(s.HostFs);
        if (vsText == null || fsText == null)
        {
            Fail(s, $"{(vsText == null ? s.HostVs : s.HostFs)}.shd is not in the resource chain");
            return;
        }
        bool vsStock = ContentStage.StockShaped(vsText, false), fsStock = ContentStage.StockShaped(fsText, true);
        s.VsStock = vsStock; s.FsStock = fsStock;
        if (vsStock && fsStock)
        {
            s.Plain = true;
            _reg.Log?.Info($"{What(s)}: adds nothing to the stock pair -- Alloy's own lit stage is its shading");
            return;
        }
        string? vs = vsStock ? CX.Alloy.Programs._litVsSrc : WithRegion(CX.Alloy.Programs._litVsSrc!, VsLib, VanillaShader.ContentVsMark, s.HostVs, s.HostFs, VsStub);
        string? fs = fsStock ? CX.Alloy.Programs._litFsSrc : WithRegion(CX.Alloy.Programs._litFsSrc!, FsLib, VanillaShader.ContentMark, s.HostFs, s.HostVs, RegionStub);
        if (vs != null && !vsStock && AlloyRenderer.Bindless) vs = ShaderStages.AfterVersion(vs, VsBindless);
        if (vs != null && s.NatVs >= 0) vs = ShaderStages.WithDefine(vs, StageResources.NativeDefine);
        if (fs != null && s.NatFs >= 0) fs = ShaderStages.WithDefine(fs, StageResources.NativeDefine);
        if (vs == null || fs == null)
        {
            Fail(s, $"the lit stage in use carries no {(vs == null ? VsLib : FsLib)} region to host it in (a content override "
                  + "of the lit stage that predates hosting)");
            return;
        }
        s.VsSrc = vs; s.FsSrc = fs;
        // the forward pair first: its splice translates the halves, and the facts say which twins the pair needs
        var keys = new List<ulong> { GlProgram.Start(vs, fs, true) };
        if (Facts(s, out _)) foreach (var t in Twins(s)) keys.Add(GlProgram.Start(t.Vs, t.Fs, true));
        s.Keys = keys.ToArray();
    }

    /// <summary>The pair's facts off its halves' last translation; false (with <paramref name="refused"/>) when either was
    /// refused or has not been translated yet.</summary>
    private static bool Facts(Stage s, out string? refused)
    {
        refused = null;
        bool vsStock = s.VsStock, fsStock = s.FsStock;
        ContentStage.Result? v = vsStock ? null : ContentStage.Last(false, s.HostVs, s.HostFs);
        ContentStage.Result? f = fsStock ? null : ContentStage.Last(true, s.HostVs, s.HostFs);
        if ((!vsStock && v == null) || (!fsStock && f == null)) return false;
        refused = v?.Refused ?? f?.Refused;
        if (refused != null) return false;
        s.Moves = !vsStock;
        s.Cuts = f?.Cuts ?? false;
        s.ReadsPlt = (f?.ReadsPlt ?? false) || (v?.ReadsPlt ?? false);
        s.Post = f?.Post ?? false;
        s.ReadsLights = f?.ReadsLights ?? false;
        s.NoKeyhole = f?.NoKeyhole ?? false;
        s.Params = f?.Params ?? v!.Params;
        return true;
    }

    /// <summary>The twins the pair's facts call for, beside its forward program: the deferred one unless it edits the lit
    /// colour or lights itself (only the forward program has either), and the depth and caster ones when its parts' depth
    /// is its own -- the casters only with the handle table, the one way a caster stage reaches the pair's maps.</summary>
    private static List<(string What, string Vs, string Fs, int Slot)> Twins(Stage s)
    {
        var list = new List<(string, string, string, int)>();
        string vs = s.VsSrc!, fs = s.FsSrc!;
        if (!s.Post && !s.ReadsLights && CX.Alloy.Programs._progLitDef != 0)
            list.Add(("deferred twin", vs, CX.Alloy.Programs.WithDeferred(fs), 1));
        if (!s.Cuts && !s.Moves) return list;
        list.Add(("depth twin", vs, ShaderStages.WithDefine(fs, "CX_DEPTH_ONLY 1"), 2));
        if (!AlloyRenderer.Bindless) return list;
        string castFs = ShaderStages.WithDefine(ShaderStages.WithDefine(fs, "CX_DEPTH_ONLY 1"), "CX_CAST 1");
        list.Add(("caster twin", ShaderStages.WithDefine(vs, "CX_CAST 1"), castFs, 3));
        list.Add(("layered caster twin", ShaderStages.WithDefine(vs, "CX_CAST_LAYER 1"), castFs, 4));
        return list;
    }

    /// <summary>Claim the pair's programs -- compiled by now, or compiled here in turn without parallel compile -- and
    /// say what it became.</summary>
    private static void Finish(Stage s)
    {
        ulong[] issued = s.Keys!;
        s.Keys = null;
        string what = What(s);
        int prev;
        Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &prev);
        uint fwd = GlProgram.Graphics(s.VsSrc!, s.FsSrc!, _reg.Log, what, host: true);
        bool stubbed = GlProgram.LastStubbed, facts = Facts(s, out string? refused);
        if (fwd == 0 || stubbed || !facts)
        {
            if (fwd != 0) Gl.DeleteProgram(fwd);
            GlProgram.Drop(issued);
            Fail(s, refused ?? (fwd == 0 ? "its program did not compile or link (the lines above say why)"
                                         : "it compiled only with its body stubbed (the lines above say why)"));
            Gl.UseProgram((uint)prev);
            return;
        }
        var p = new ProgramSet { Fwd = fwd, FwdLoc = CX.Alloy.Programs.LitLocs(fwd), FwdNoise = GlProgram.Loc(fwd, "uConNoise") };
        p.FwdRec = StageResources.Attach(fwd, s.NatVs, s.NatFs, _reg.Log);
        if (p.FwdRec == -2)
        {
            Gl.DeleteProgram(fwd);
            GlProgram.Drop(issued);
            Gl.UseProgram((uint)prev);
            Renew(s, "its native half refused the program");
            return;
        }
        var built = new List<string>();
        foreach (var (twin, vs, fs, slot) in Twins(s))
        {
            uint t = GlProgram.Graphics(vs, fs, _reg.Log, $"{what} ({twin})", host: true);
            if (t != 0 && GlProgram.LastStubbed) { Gl.DeleteProgram(t); t = 0; }
            if (t == 0) continue;
            int rec = StageResources.Attach(t, s.NatVs, s.NatFs, _reg.Log);
            if (rec == -2) { Gl.DeleteProgram(t); continue; }
            built.Add(twin);
            var loc = CX.Alloy.Programs.LitLocs(t);
            int noise = GlProgram.Loc(t, "uConNoise");
            switch (slot)
            {
                case 1: p.Def = t; p.DefLoc = loc; p.DefNoise = noise; p.DefRec = rec; break;
                case 2: p.Depth = t; p.DepthLoc = loc; p.DepthNoise = noise; p.DepthHl = GlProgram.Loc(t, "uHlColor"); p.DepthRec = rec; break;
                case 3: p.Cast = t; p.CastLoc = loc; p.CastNoise = noise; p.CastRec = rec; break;
                default: p.CastLayer = t; p.CastLayerLoc = loc; p.CastLayerNoise = noise; p.CastLayerRec = rec; break;
            }
        }
        s.P = p;
        s.Epoch++;
        _reg.Builds++;
        ContentStage.Result? f = s.FsStock ? null : ContentStage.Last(true, s.HostVs, s.HostFs);
        ContentStage.Result? v = s.VsStock ? null : ContentStage.Last(false, s.HostVs, s.HostFs);
        var notes = new List<string>();
        if (v != null) notes.AddRange(v.Notes);
        if (f != null) notes.AddRange(f.Notes);
        _reg.Log?.Always($"{what}: runs translated -- {(s.VsStock ? "stock" : "its own")} vertex half, "
                       + $"{(s.FsStock ? "stock" : "its own")} fragment half, {s.Params.Length} parameter lane(s)"
                       + (s.Cuts ? ", its own coverage" : "") + (s.Moves ? ", its own vertex placement" : "")
                       + (s.Post ? ", edits on the lit colour (forward only)" : "") + (s.ReadsLights ? ", lights itself (forward only)" : "")
                       + (s.NoKeyhole ? ", never dissolves" : "")
                       + (built.Count == 0 ? "" : "; " + string.Join(", ", built))
                       + (s.Cuts || s.Moves ? (p.Depth == 0 ? "; NO depth twin, so its parts stay out of the prepass" : "")
                                              + (p.Cast == 0 ? "; NO caster twin, so its shadows cast off the diffuse alpha, unmoved" : "") : "")
                       + (notes.Count == 0 ? "" : "; it " + string.Join("; it ", notes)));
        Gl.UseProgram((uint)prev);
    }

    private static string What(Stage s) => $"Alloy content pair {(s.HostVs.Length > 0 ? s.HostVs : s.Vs)}/{(s.HostFs.Length > 0 ? s.HostFs : s.Fs)}";

    /// <summary>What a half runs as: its native stage, declared, where the chain has one that may run -- else the half
    /// itself, with <paramref name="id"/> -1. A native stage that stood down stays refused until the module changes.</summary>
    private static string Host(string half, out int id)
    {
        id = -1;
        string? native = StageResources.NativeOf(half);
        if (native == null) return half;
        int got = StageResources.Declare(native, _reg.Log);
        if (!StageResources.Live(got)) return half;
        id = got;
        return native;
    }

    /// <summary>A native half stood down (said by <see cref="StageResources.Fail"/>): the pair's programs go, and it
    /// rebuilds from its plain halves at the next frame that asks -- once, since the refusal is latched.</summary>
    private static void Renew(Stage s, string why)
    {
        Drop(s);
        s.Wanted = true;
        _reg.Log?.Info($"{What(s)}: {why} -- the pair rebuilds from {s.Vs}/{s.Fs}");
    }

    private static void Drop(Stage s)
    {
        foreach (uint p in new[] { s.P.Fwd, s.P.Def, s.P.Depth, s.P.Cast, s.P.CastLayer }) if (p != 0) Gl.DeleteProgram(p);
        if (s.Keys != null) GlProgram.Drop(s.Keys);
        s.P = new ProgramSet();
        s.Failed = false; s.Plain = false; s.Cuts = false; s.Moves = false; s.Wanted = false; s.Keys = null;
        s.NatVs = -1; s.NatFs = -1;
    }

    private static void Fail(Stage s, string why)
    {
        s.Failed = true;
        _reg.Failures++;
        _reg.Log?.Always($"{What(s)}: NOT RUN -- {why}. Its parts draw the plain lit path");
    }

    /// <summary>The lit stage's text with its <paramref name="lib"/> library replaced by a content region for the pair --
    /// <paramref name="mark"/> naming its own half then the other -- whose stub hosts nothing. Null when the text carries
    /// no such library.</summary>
    internal static string? WithRegion(string lit, string lib, string mark, string own, string other, string stub)
    {
        string open = ShaderSource.LibMark + lib + "\n", close = ShaderSource.EndMark + lib + "\n";
        int a = lit.IndexOf(open, StringComparison.Ordinal);
        int b = a < 0 ? -1 : lit.IndexOf(close, a, StringComparison.Ordinal);
        if (b < 0) return null;
        return string.Concat(lit.AsSpan(0, a), mark + own + " " + other + "\n" + stub + "// $VANILLA-END\n", lit.AsSpan(b + close.Length));
    }

    /// <summary>The libraries a content region replaces: the hosts that host nothing.</summary>
    internal const string FsLib = "cx_host_f", VsLib = "cx_host_v";

    /// <summary>A content vertex half samples the material through the record's handles when the fragment stage
    /// does: the lit vertex stage itself samples nothing, so only a content one is built with the extension.</summary>
    private const string VsBindless = "#define CX_BINDLESS 1\n#extension GL_ARB_bindless_texture : require\n";

    /// <summary>What a region compiles to when its half is refused: no body.</summary>
    internal const string RegionStub = "bool CxHostSurface(int feat) { return false; }\n";
    internal const string VsStub = "bool CxHostVertex(CxDraw d, int id, int bb) { return false; }\n";

    /// <summary>A module change: every content program goes, and a pair that failed against the old module may build
    /// against the new one. Ids stay -- the recipes holding them outlive the edge.</summary>
    internal static void DropSpliced()
    {
        RegistryState r = _reg;
        for (int i = 0; i < r.Count; i++)
        {
            if (r.ById[i] is not { } s) continue;
            Drop(s);
        }
        ContentStage.Forget();
    }

    /// <summary>The context took every program name: forget them, and rebuild on the next ask. A pair that failed is
    /// asked again too, since the context it failed against is gone.</summary>
    internal static void ContextLost()
    {
        RegistryState r = _reg;
        for (int i = 0; i < r.Count; i++)
            // the native stage ids too: the table they index is the dead context's, and a new one numbers afresh
            if (r.ById[i] is { } s) { s.P = new ProgramSet(); s.Wanted = false; s.Keys = null; s.Failed = false; s.NatVs = -1; s.NatFs = -1; }
        GlProgram.ForgetBackground();
    }

    /// <summary>The period line's clause: pairs named, running, compiling, refused.</summary>
    internal static string Note()
    {
        RegistryState r = _reg;
        if (r.Count == 0 && r.StockPairs == 0) return "";
        int running = 0, failed = 0, compiling = 0;
        for (int i = 0; i < r.Count; i++)
            if (r.ById[i] is { } s) { if (s.P.Fwd != 0) running++; if (s.Failed) failed++; if (s.Keys != null) compiling++; }
        return $"content pairs: {r.Count} named, {running} running, {compiling} compiling, {failed} NOT RUN, {r.StockPairs} stock"
             + (r.Spilled == 0 ? "" : $", {r.Spilled} past the id range") + "; ";
    }

    /// <summary>Off-device. Oracle: hand-written <c>.mtr</c> texts and the engine's own rule as the contract research
    /// states it -- both halves named or no program, one id per pair whatever its case, another pair for another half --
    /// the region text worked by hand, and the issue-loop numbering worked by hand.</summary>
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
            int a = Register(Mtr("customshadervs cx_selfcheck_v\ncustomshaderfs cx_selfcheck_a\n"));
            int b = Register(Mtr("customShaderVS CX_SELFCHECK_V\ncustomShaderFS CX_SELFCHECK_A\n"));
            check.Expect(a == 2 && b == 2, $"the first content pair is not id 2 for both cases of its names (got {a}, {b})");
            check.Expect(Register(Mtr("customshadervs cx_selfcheck_w\ncustomshaderfs cx_selfcheck_a\n")) == 3,
                         "the same fragment half under another vertex half does not take the next id");
            check.Expect(ProgId(2, false) == 100 && ProgId(2, true) == 101 && ProgId(3, false) == 102
                         && IsDeferred(101) && !IsDeferred(102) && !IsProg(9),
                         "the issue-loop numbering is not 100 + 2 * index, plus one for the twin");
            check.Expect(!AskReady(2) && _reg.ById[0]!.Wanted && !AskReady(1),
                         "an unbuilt pair reads ready, does not queue its build, or the skin class reads as content");
            string region = WithRegion("head\n//@cx-lib cx_host_f\nbody\n//@cx-end cx_host_f\ntail\n", FsLib,
                                       VanillaShader.ContentMark, "rambo", "vslit", RegionStub) ?? "";
            check.Expect(region == "head\n// $VANILLA-CONTENT rambo vslit\n" + RegionStub + "// $VANILLA-END\ntail\n",
                         "the host library is not replaced by the content region naming both halves, brackets and all");
            check.Expect(WithRegion("no library here\n", VsLib, VanillaShader.ContentVsMark, "a", "b", VsStub) == null,
                         "a lit stage with no host library takes a region");
        }
        finally { _reg = saved; }
        log.Info(check.Ok ? "ContentPrograms.SelfCheck: passed" : "ContentPrograms.SelfCheck: FAILED");
        return check.Ok;
    }
}
