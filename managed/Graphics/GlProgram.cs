using System;
using System.Diagnostics;
using CX.Framework;
using CX.Alloy;   // ShaderResource: override lookup + $TOKEN$ expansion, both content/render concerns

namespace CX.Graphics;

/// <summary>Shader-object plumbing shared by the Alloy pass's programs: compile, link, uniform lookup.
/// Alloc-free apart from the log strings on the failure paths, which are cold by construction -- a program
/// either builds once at first use or is disabled for the session.</summary>
internal static unsafe class GlProgram
{
    private const int LogChars = 1024;

    /// <summary>Link a vertex+fragment program. Returns 0 on failure (already logged). A library the resource
    /// chain overrides (<see cref="ShaderSource.Content"/>) is swapped in first, and a build it breaks rebuilds
    /// from the embedded text; <paramref name="content"/> false is that embedded build outright -- the audit
    /// fall backs, which must not re-take the override they refused.</summary>
    internal static uint Graphics(string vs, string fs, ExtenderLog? log, string what, bool host = true, bool content = true)
    {
        LastStubbed = false;
        string cv = content ? Lib(vs, log, what) : vs, cf = content ? Lib(fs, log, what) : fs;
        uint p = Pair(cv, cf, log, what, host);
        if (p != 0 || (ReferenceEquals(cv, vs) && ReferenceEquals(cf, fs))) return p;
        log?.Info($"{what}: a library override .shd broke the build -- rebuilding from the embedded libraries");
        return Pair(vs, fs, log, what, host);
    }

    /// <summary>Did the last two-stage <see cref="Graphics(string, string, ExtenderLog?, string, bool, bool)"/>
    /// link only after a vanilla region fell back to its stub? For a caller whose stub is a picture rather
    /// than a feature switched off (the sky's black dome), which must refuse instead.</summary>
    internal static bool LastStubbed;

    /// <summary><see cref="ShaderSource.Content"/>, refused for the embedded libraries when the swap leaves the
    /// stage reading the pass terms outside the <c>CxPass</c> block (<see cref="ShaderContract.PassSafe"/>).</summary>
    private static string Lib(string src, ExtenderLog? log, string what)
    {
        string c = ShaderSource.Content(src, log);
        if (ReferenceEquals(c, src) || ShaderContract.PassSafe(c)) return c;
        log?.Info($"{what}: a library override .shd reads the pass terms outside the CxPass block (predates CX-43) -- using the embedded libraries");
        return src;
    }

    private static uint Pair(string vs, string fs, ExtenderLog? log, string what, bool host)
    {
        string tv = Text(vs, log, what, host), tf = Text(fs, log, what, host);
        ulong key = PairKey(tv, tf);
        uint p = ProgramBinary.Load(key, log, what);
        if (p != 0) { Labels[p] = what; return p; }
        if ((p = Claim(key, log, what)) != 0) return p;
        LightmapBake.CompileNotice();
        long t0 = Stopwatch.GetTimestamp();
        uint v = Compile(Gl.VERTEX_SHADER, vs, tv, log, what, host, out bool cv), f = Compile(Gl.FRAGMENT_SHADER, fs, tf, log, what, host, out bool cf);
        if (v == 0 || f == 0) { if (v != 0) Gl.DeleteShader(v); if (f != 0) Gl.DeleteShader(f); return 0; }
        return Cache(cv && cf ? key : 0, Link(log, what, v, f), t0, log, what);
    }

    /// <summary>Link a vertex+geometry+fragment program. Returns 0 on failure (already logged). Library
    /// overrides as in <see cref="Graphics(string, string, ExtenderLog?, string, bool, bool)"/>.</summary>
    internal static uint Graphics(string vs, string gs, string fs, ExtenderLog? log, string what)
    {
        string cv = Lib(vs, log, what), cg = Lib(gs, log, what), cf = Lib(fs, log, what);
        uint p = Triple(cv, cg, cf, log, what);
        if (p != 0 || (ReferenceEquals(cv, vs) && ReferenceEquals(cg, gs) && ReferenceEquals(cf, fs))) return p;
        log?.Info($"{what}: a library override .shd broke the build -- rebuilding from the embedded libraries");
        return Triple(vs, gs, fs, log, what);
    }

    private static uint Triple(string vs, string gs, string fs, ExtenderLog? log, string what)
    {
        string tv = Text(vs, log, what, true), tg = Text(gs, log, what, true), tf = Text(fs, log, what, true);
        ulong key = ProgramBinary.Key(ProgramBinary.Key(ProgramBinary.Key(ProgramBinary.Seed,
                        Gl.VERTEX_SHADER, tv), Gl.GEOMETRY_SHADER, tg), Gl.FRAGMENT_SHADER, tf);
        uint p = ProgramBinary.Load(key, log, what);
        if (p != 0) { Labels[p] = what; return p; }
        LightmapBake.CompileNotice();
        long t0 = Stopwatch.GetTimestamp();
        uint v = Compile(Gl.VERTEX_SHADER, vs, tv, log, what, true, out bool cv),
             g = Compile(Gl.GEOMETRY_SHADER, gs, tg, log, what, true, out bool cg),
             f = Compile(Gl.FRAGMENT_SHADER, fs, tf, log, what, true, out bool cf);
        if (v == 0 || g == 0 || f == 0)
        { if (v != 0) Gl.DeleteShader(v); if (g != 0) Gl.DeleteShader(g); if (f != 0) Gl.DeleteShader(f); return 0; }
        return Cache(cv && cg && cf ? key : 0, Link(log, what, v, g, f), t0, log, what);
    }

    /// <summary>Link a vertex+fragment program whose stages a content author may override as
    /// <c>&lt;resref&gt;.shd</c>. An override that fails to compile or link rebuilds from the embedded default,
    /// so a malformed .shd costs a log line, not the frame; 0 on failure. <paramref name="vsSource"/> and
    /// <paramref name="fsSource"/> hand back what actually compiled, so contracts check what RAN, not what shipped.</summary>
    internal static uint Graphics(string vsRef, string vs, string fsRef, string fs, ExtenderLog? log, string what,
                                  out string vsSource, out string fsSource, bool host = true)
    {
        byte[]? bvs = ShaderResource.OverrideSpirv(vsRef, log), bfs = ShaderResource.OverrideSpirv(fsRef, log);
        if (bvs != null || bfs != null)
        {
            // ponytail: the contracts see the EMBEDDED text, because a module cannot be read for them --
            // the alloy.spirv=1 gate is the audit. Mixed GLSL+SPIR-V stages fail to link per GL 4.6 and land on
            // the same fallback below, so nothing special-cases them.
            vsSource = vs; fsSource = fs;
            uint v = bvs != null ? Binary(Gl.VERTEX_SHADER, bvs, log, what) : Compile(Gl.VERTEX_SHADER, vs, log, what, host);
            uint f = bfs != null ? Binary(Gl.FRAGMENT_SHADER, bfs, log, what) : Compile(Gl.FRAGMENT_SHADER, fs, log, what, host);
            if (v != 0 && f != 0) { uint p = Link(log, what, v, f); if (p != 0) return p; }
            else { if (v != 0) Gl.DeleteShader(v); if (f != 0) Gl.DeleteShader(f); }
            log?.Info($"{what}: SPIR-V override did not build -- falling back to the embedded default");
            return Graphics(vs, fs, log, what, host);
        }
        Texts(vsRef, vs, fsRef, fs, log, what, out vsSource, out fsSource);
        uint prog = Pair(vsSource, fsSource, log, what, host);
        if (prog != 0 || (ReferenceEquals(vsSource, vs) && ReferenceEquals(fsSource, fs))) return prog;
        log?.Info($"{what}: override .shd failed to build -- falling back to the embedded default");
        vsSource = vs; fsSource = fs;
        return Pair(vs, fs, log, what, host);
    }

    /// <summary>The texts the overridable <see cref="Graphics(string, string, string, string, ExtenderLog?, string)"/>
    /// builds first: the stage override, then the libraries inside whichever text won, back to the embedded
    /// stages when either reads the pass terms outside the <c>CxPass</c> block.</summary>
    internal static void Texts(string vsRef, string vs, string fsRef, string fs, ExtenderLog? log, string what,
                               out string vsSource, out string fsSource)
    {
        // the stage override first, then the libraries inside whichever text won: a dumped stage keeps its
        // brackets, so a library override still reaches it
        string? ovs = ShaderResource.Override(vsRef, log), ofs = ShaderResource.Override(fsRef, log);
        vsSource = ShaderSource.Content(ovs ?? vs, log); fsSource = ShaderSource.Content(ofs ?? fs, log);
        if (ShaderContract.PassSafe(vsSource) && ShaderContract.PassSafe(fsSource)) return;
        log?.Info($"{what}: an override .shd reads the pass terms outside the CxPass block (predates CX-43) -- building the embedded default");
        vsSource = vs; fsSource = fs;
    }

    /// <summary>The <see cref="ProgramBinary"/> key of a vertex+fragment pair's first-try texts: what the cache
    /// and <see cref="Prefetch"/>'s claims both look a pair up by.</summary>
    private static ulong PairKey(string tv, string tf) =>
        ProgramBinary.Key(ProgramBinary.Key(ProgramBinary.Seed, Gl.VERTEX_SHADER, tv), Gl.FRAGMENT_SHADER, tf);

    /// <summary>What <see cref="Prefetch"/> issued that no build has claimed yet, by <see cref="PairKey"/>.
    /// Lives inside one <see cref="PrefetchScope"/>.</summary>
    private sealed class PrefetchState
    {
        internal readonly System.Collections.Generic.Dictionary<ulong, (uint Prog, uint V, uint F, long T0)> Inflight = new();
    }

    private static PrefetchState _prefetch = new();

    /// <summary>Start every vertex+fragment program in <paramref name="set"/> -- compile and link, no status read --
    /// so the driver's compiler threads build them side by side, and the two-stage <c>Graphics</c> call for the
    /// same texts claims one instead of compiling, until the returned scope ends. A null fragment text is skipped;
    /// with no <see cref="GlContextService.ParallelCompile"/> nothing is issued and every build compiles in turn.</summary>
    internal static PrefetchScope Prefetch(ReadOnlySpan<(string Vs, string? Fs, bool Host)> set, ExtenderLog? log)
    {
        if (!GlContextService.ParallelCompile) return new PrefetchScope(log);
        if (ProgramBinary.Seed == 0)
        {
            log?.Info("GlProgram: the program binary cache is off, so a parallel build has no key to be claimed by -- compiling in turn");
            return new PrefetchScope(log);
        }
        foreach (var (vs, fs, host) in set)
        {
            if (fs == null) continue;
            // ponytail: every entry is keyed through Lib, which the overridable builds skip; they differ only when an
            // unsafe stage override meets a safe library override, and that one program compiles in turn
            string tv = Text(Lib(vs, null, ""), null, "", host), tf = Text(Lib(fs, null, ""), null, "", host);
            ulong key = PairKey(tv, tf);
            if (key == 0 || _prefetch.Inflight.ContainsKey(key) || ProgramBinary.Has(key)) continue;
            LightmapBake.CompileNotice();
            uint v = Issue(Gl.VERTEX_SHADER, tv), f = Issue(Gl.FRAGMENT_SHADER, tf);
            _prefetch.Inflight[key] = (StartLink(v, f), v, f, Stopwatch.GetTimestamp());
        }
        return new PrefetchScope(log);
    }

    /// <summary>The span of one <see cref="Prefetch"/>: disposing it drops whatever no build claimed.</summary>
    internal readonly ref struct PrefetchScope
    {
        private readonly ExtenderLog? _log;
        internal PrefetchScope(ExtenderLog? log) => _log = log;
        public void Dispose() => DropPrefetched(_log);
    }

    /// <summary>What <see cref="Start"/> issued that no build has claimed yet: unlike a prefetch it outlives the frame, so
    /// a program can compile behind several frames of drawing and be claimed once the driver says it is done.</summary>
    private sealed class BackgroundState
    {
        internal readonly System.Collections.Generic.Dictionary<ulong, (uint Prog, uint V, uint F, long T0)> Inflight = new();
    }

    private static BackgroundState _background = new();

    /// <summary>Start the vertex+fragment program <paramref name="vs"/>/<paramref name="fs"/> in the BACKGROUND -- compile
    /// and link, no status read -- and hand back its key for <see cref="Done"/>. A program already cached or in flight is
    /// not issued again. With no parallel compile (or no binary cache to key a claim by) nothing is issued and the key is
    /// 0, which reads done: the build that follows compiles in turn.</summary>
    internal static ulong Start(string vs, string fs, bool host)
    {
        if (!GlContextService.ParallelCompile || ProgramBinary.Seed == 0) return 0;
        string tv = Text(Lib(vs, null, ""), null, "", host), tf = Text(Lib(fs, null, ""), null, "", host);
        ulong key = PairKey(tv, tf);
        if (key == 0 || ProgramBinary.Has(key)) return 0;
        if (_background.Inflight.ContainsKey(key) || _prefetch.Inflight.ContainsKey(key)) return key;
        LightmapBake.CompileNotice();
        uint v = Issue(Gl.VERTEX_SHADER, tv), f = Issue(Gl.FRAGMENT_SHADER, tf);
        _background.Inflight[key] = (StartLink(v, f), v, f, Stopwatch.GetTimestamp());
        return key;
    }

    /// <summary>Has the driver finished every program <see cref="Start"/> handed back <paramref name="keys"/> for? A key
    /// nothing is in flight under (0, claimed, dropped) reads done.</summary>
    internal static bool Done(ReadOnlySpan<ulong> keys)
    {
        foreach (ulong k in keys)
        {
            if (k == 0 || !_background.Inflight.TryGetValue(k, out var f)) continue;
            int ok = 0;
            Gl.GetProgramiv(f.Prog, Gl.COMPLETION_STATUS, &ok);
            if (ok == 0) return false;
        }
        return true;
    }

    /// <summary>Delete what <see cref="Start"/> issued under <paramref name="keys"/> that no build claimed: a build that
    /// failed before it reached them leaves them in flight otherwise, holding their GL names for the session.</summary>
    internal static void Drop(ReadOnlySpan<ulong> keys)
    {
        foreach (ulong k in keys)
            if (k != 0 && _background.Inflight.Remove(k, out var f))
            { Gl.DeleteProgram(f.Prog); Gl.DeleteShader(f.V); Gl.DeleteShader(f.F); }
    }

    /// <summary>The context took every name: whatever was in flight is gone with it.</summary>
    internal static void ForgetBackground() => _background = new BackgroundState();

    /// <summary>The program <see cref="Prefetch"/> or <see cref="Start"/> issued under <paramref name="key"/>, waited for;
    /// 0 when none was issued or it did not link, and the caller's own build then repeats it in turn, retries and log
    /// lines included.</summary>
    private static uint Claim(ulong key, ExtenderLog? log, string what)
    {
        if (!_prefetch.Inflight.Remove(key, out var f) && !_background.Inflight.Remove(key, out f)) return 0;
        int ok; Gl.GetProgramiv(f.Prog, Gl.LINK_STATUS, &ok);
        if (ok != 0) { Notes(f.V, true, log, what); Notes(f.F, true, log, what); Notes(f.Prog, false, log, what); }
        Gl.DeleteShader(f.V); Gl.DeleteShader(f.F);
        if (ok != 0) { Labels[f.Prog] = what; return Cache(key, f.Prog, f.T0, log, what, "compiled in parallel, ready after"); }
        Gl.DeleteProgram(f.Prog);
        log?.Info($"{what}: WARN the parallel build did not link -- building it again in turn, with the retries");
        return 0;
    }

    /// <summary>Delete what <see cref="Prefetch"/> issued and no build claimed -- a guess at texts the builds did
    /// not take, after an override fell back or a contract refused -- and say so, so a miss is not silent.</summary>
    private static void DropPrefetched(ExtenderLog? log)
    {
        if (_prefetch.Inflight.Count == 0) return;
        foreach (var f in _prefetch.Inflight.Values) { Gl.DeleteProgram(f.Prog); Gl.DeleteShader(f.V); Gl.DeleteShader(f.F); }
        log?.Info($"GlProgram: WARN {_prefetch.Inflight.Count} program(s) compiled in parallel went unclaimed -- "
                + "the builds took other texts than the ones issued; dropped");
        _prefetch = new PrefetchState();
    }

    /// <summary>The overridable link for callers that do not care where the stages came from.</summary>
    internal static uint Graphics(string vsRef, string vs, string fsRef, string fs, ExtenderLog? log, string what)
        => Graphics(vsRef, vs, fsRef, fs, log, what, out _, out _);

    /// <summary>Link a compute program. Returns 0 on failure (already logged). Library overrides as in
    /// <see cref="Graphics(string, string, ExtenderLog?, string, bool, bool)"/>.</summary>
    internal static uint Compute(string cs, ExtenderLog? log, string what)
    {
        string cc = Lib(cs, log, what);
        uint p = Single(cc, log, what);
        if (p != 0 || ReferenceEquals(cc, cs)) return p;
        log?.Info($"{what}: a library override .shd broke the build -- rebuilding from the embedded libraries");
        return Single(cs, log, what);
    }

    private static uint Single(string cs, ExtenderLog? log, string what)
    {
        string tc = Text(cs, log, what, true);
        ulong key = ProgramBinary.Key(ProgramBinary.Seed, Gl.COMPUTE_SHADER, tc);
        uint p = ProgramBinary.Load(key, log, what);
        if (p != 0) { Labels[p] = what; return p; }
        LightmapBake.CompileNotice();
        long t0 = Stopwatch.GetTimestamp();
        uint c = Compile(Gl.COMPUTE_SHADER, cs, tc, log, what, true, out bool clean);
        return c == 0 ? 0 : Cache(clean ? key : 0, Link(log, what, c), t0, log, what);
    }

    /// <summary>Save a program that linked from its first-try texts, and log what the miss cost; a stubbed
    /// retry is not cached, so its WARN lines repeat every launch instead of going quiet behind a blob.</summary>
    private static uint Cache(ulong key, uint prog, long t0, ExtenderLog? log, string what, string done = "compiled in")
    {
        if (prog == 0) return 0;
        bool saved = ProgramBinary.Save(key, prog, log, what);
        log?.Info($"{what}: {done} {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms"
                + (saved ? " -- saved to cx-shaders/" : key == 0 ? " -- not cached" : ""));
        return prog;
    }

    /// <summary>The compute counterpart of the overridable <see cref="Graphics(string, string, string, string,
    /// ExtenderLog?, string)"/>: same lookup, same fall back to the embedded default on a build failure.</summary>
    internal static uint Compute(string csRef, string cs, ExtenderLog? log, string what)
    {
        byte[]? bcs = ShaderResource.OverrideSpirv(csRef, log);
        if (bcs != null)
        {
            uint c = Binary(Gl.COMPUTE_SHADER, bcs, log, what);
            if (c != 0) { uint p = Link(log, what, c); if (p != 0) return p; }
            log?.Info($"{what}: SPIR-V override did not build -- falling back to the embedded default");
            return Compute(cs, log, what);
        }
        string? ocs = ShaderResource.Override(csRef, log);
        string src = ShaderSource.Content(ocs ?? cs, log);
        uint prog = Single(src, log, what);
        if (prog != 0 || ReferenceEquals(src, cs)) return prog;
        log?.Info($"{what}: override .shd failed to build -- falling back to the embedded default");
        return Single(cs, log, what);
    }

    /// <summary>Program name -> the <c>what</c> it was linked under, so an alloy.gldebug report can say which
    /// pass drew. Written at link (cold); names recycle after a context loss, which only misnames a debug line.</summary>
    internal static readonly System.Collections.Generic.Dictionary<uint, string> Labels = new();

    /// <summary>Link a content vertex+fragment pair from finished texts, binding each named attribute to its
    /// location before the link -- the engine's own <c>ConnectShaderData</c> order. No token expansion, no splice,
    /// no binary cache. Returns 0 on failure (already logged).</summary>
    internal static uint Content(string vs, string fs, (string Name, uint Loc)[] attribs, ExtenderLog? log, string what)
    {
        uint v = CompileText(Gl.VERTEX_SHADER, vs, log, what), f = CompileText(Gl.FRAGMENT_SHADER, fs, log, what);
        if (v != 0 && f != 0) return Link(log, what, v, f, attribs: attribs);
        if (v != 0) Gl.DeleteShader(v);
        if (f != 0) Gl.DeleteShader(f);
        return 0;
    }

    private static uint Link(ExtenderLog? log, string what, uint a, uint b = 0, uint c = 0, uint d = 0,
                             (string Name, uint Loc)[]? attribs = null)
    {
        if (GlContextService.Debug) log?.Always($"GlProgram: [gldebug] linking {what}");
        uint prog = StartLink(a, b, c, d, attribs);
        int ok; Gl.GetProgramiv(prog, Gl.LINK_STATUS, &ok);
        Gl.DeleteShader(a);
        if (b != 0) Gl.DeleteShader(b);
        if (c != 0) Gl.DeleteShader(c);
        if (d != 0) Gl.DeleteShader(d);
        if (ok != 0) { Notes(prog, false, log, what); Labels[prog] = what; return prog; }
        log?.Info($"{what}: link failed -- {InfoLog(prog, false)}");
        Gl.DeleteProgram(prog);
        return 0;
    }

    /// <summary>Create a program from compiled stages, bind <paramref name="attribs"/> and start the link. Reads no
    /// status, so under <see cref="GlContextService.ParallelCompile"/> the driver may still be linking on return.</summary>
    private static uint StartLink(uint a, uint b, uint c = 0, uint d = 0, (string Name, uint Loc)[]? attribs = null)
    {
        uint prog = Gl.CreateProgram();
        if (Gl.ProgramParameteri != null) Gl.ProgramParameteri(prog, Gl.PROGRAM_BINARY_RETRIEVABLE_HINT, 1);
        Gl.AttachShader(prog, a);
        if (b != 0) Gl.AttachShader(prog, b);
        if (c != 0) Gl.AttachShader(prog, c);
        if (d != 0) Gl.AttachShader(prog, d);
        if (attribs != null)
            foreach (var (name, loc) in attribs)
            {
                Span<byte> n = stackalloc byte[name.Length + 1];
                Gl.AsciiZ(name, n);
                fixed (byte* np = n) Gl.BindAttribLocation(prog, loc, np);
            }
        Gl.LinkProgram(prog);
        return prog;
    }

    /// <summary>Compile one stage. The source's sizing tokens are expanded here and nowhere else, so an
    /// embedded default and a content override read identically (see <see cref="ShaderResource.Tokens"/>),
    /// and <see cref="VanillaShader.Splice"/> runs at the same seam for the same reason.</summary>
    private static uint Compile(uint type, string rawSrc, ExtenderLog? log, string what, bool host = true)
        => Compile(type, rawSrc, Text(rawSrc, log, what, host), log, what, host, out _);

    /// <summary>A stage's first-try text: the vanilla bridge first, sizing tokens second -- spliced stock text is
    /// subject to the same token expansion as everything else. What <see cref="ProgramBinary"/> keys on.</summary>
    private static string Text(string rawSrc, ExtenderLog? log, string what, bool host) =>
        ShaderResource.Tokens(VanillaShader.Splice(rawSrc, log, what, true, host));

    /// <summary><see cref="Compile(uint, string, ExtenderLog?, string, bool)"/> from a precomputed first-try
    /// <paramref name="text"/>; <paramref name="clean"/> is whether that text compiled without a retry.</summary>
    private static uint Compile(uint type, string rawSrc, string text, ExtenderLog? log, string what, bool host, out bool clean)
    {
        uint sh = CompileText(type, text, log, what);
        clean = sh != 0;
        if (sh != 0 || !VanillaShader.HasSplice(rawSrc)) return sh;
        // a content .shd that renamed a function the stage calls would otherwise take the WHOLE
        // program down -- and for the lit stage that means the world reverts to engine drawing over one
        // edit in a hak. The stub is what that retry is for: the feature off, the world still rendering.
        log?.Info($"{what}: WARN the spliced vanilla text did not compile -- retrying with the fallback stub, "
                + "so the program links and the feature it carried is off");
        LastStubbed = true;
        return CompileText(type, ShaderResource.Tokens(VanillaShader.Splice(rawSrc, log, what, false)), log, what);
    }

    /// <summary>Specialize one SPIR-V stage (GL 4.6 core). No token expansion, no vanilla splice and no
    /// contract audit reach a module -- see <see cref="ShaderResource.OverrideSpirv"/>. Entry point is
    /// <c>main</c>; specialization constants are not fed, so a module must bake its own sizes.</summary>
    private static uint Binary(uint type, byte[] spirv, ExtenderLog? log, string what)
    {
        if (Gl.ShaderBinary == null || Gl.SpecializeShader == null)
        {
            log?.Info($"{what}: driver exposes no glShaderBinary/glSpecializeShader -- SPIR-V stage refused");
            return 0;
        }
        uint sh = Gl.CreateShader(type);
        // glShaderBinary rejects an unsupported format with INVALID_ENUM and leaves the object uncompiled,
        // which the COMPILE_STATUS below reads as a plain build failure -- no separate capability query.
        fixed (byte* p = spirv) Gl.ShaderBinary(1, &sh, Gl.SHADER_BINARY_FORMAT_SPIR_V, p, spirv.Length);
        byte* entry = stackalloc byte[5];
        entry[0] = (byte)'m'; entry[1] = (byte)'a'; entry[2] = (byte)'i'; entry[3] = (byte)'n'; entry[4] = 0;
        Gl.SpecializeShader(sh, entry, 0, null, null);
        int ok; Gl.GetShaderiv(sh, Gl.COMPILE_STATUS, &ok);
        if (ok != 0)
        {
            Notes(sh, true, log, what);
            log?.Info($"{what}: SPIR-V module accepted ({spirv.Length} bytes, type=0x{type:x}) -- "
                    + "token expansion, vanilla splice and the stage contracts are all skipped for it");
            return sh;
        }
        log?.Info($"{what}: SPIR-V specialize failed (type=0x{type:x}) -- {InfoLog(sh, true)}");
        Gl.DeleteShader(sh);
        return 0;
    }

    private static uint CompileText(uint type, string src, ExtenderLog? log, string what)
    {
        if (GlContextService.Debug) log?.Always($"GlProgram: [gldebug] compiling {what} (type 0x{type:x}, {src.Length} chars)");
        uint sh = Issue(type, src);
        int ok; Gl.GetShaderiv(sh, Gl.COMPILE_STATUS, &ok);
        if (ok != 0) { Notes(sh, true, log, what); return sh; }
        log?.Info($"{what}: shader compile failed (type=0x{type:x}) -- {InfoLog(sh, true)}");
        Gl.DeleteShader(sh);
        return 0;
    }

    /// <summary>Create one stage from finished text and start its compile; reads no status.</summary>
    private static uint Issue(uint type, string src)
    {
        uint sh = Gl.CreateShader(type);
        Span<byte> b = stackalloc byte[src.Length + 1];
        for (int i = 0; i < src.Length; i++) b[i] = (byte)src[i];
        b[src.Length] = 0;
        fixed (byte* p = b) { byte* pp = p; Gl.ShaderSource(sh, 1, &pp, null); }
        Gl.CompileShader(sh);
        return sh;
    }

    /// <summary>A successful build's info log, when the driver left one. Mesa's compiler reports warnings
    /// there (unused varyings, implicit conversions, extension notes) that NVIDIA's never mentions, so it is
    /// the one channel a "compiles on both, differs on one" stage announces itself on. Cold: build time.</summary>
    private static void Notes(uint obj, bool shader, ExtenderLog? log, string what)
    {
        if (log == null) return;
        string s = InfoLog(obj, shader);
        if (s.Length == 0 || s == "(no info log)") return;
        log.Info($"{what}: driver notes on a successful build -- {s}");
    }

    /// <summary>Driver info log, truncated. Cold: build failures and <see cref="Notes"/>.</summary>
    private static string InfoLog(uint obj, bool shader)
    {
        Span<byte> buf = stackalloc byte[LogChars];
        int len = 0;
        fixed (byte* p = buf)
        {
            if (shader) Gl.GetShaderInfoLog(obj, LogChars, &len, p);
            else Gl.GetProgramInfoLog(obj, LogChars, &len, p);
        }
        if (len <= 0) return "(no info log)";
        if (len > LogChars) len = LogChars;
        Span<char> chars = stackalloc char[len];
        for (int i = 0; i < len; i++) chars[i] = (char)buf[i];
        return new string(chars).Replace('\n', ' ').Trim();
    }

    /// <summary>Uniform location by name; -1 when the program does not use it.</summary>
    internal static int Loc(uint prog, string name)
    {
        Span<byte> b = stackalloc byte[name.Length + 1];
        Gl.AsciiZ(name, b);
        fixed (byte* p = b) return Gl.GetUniformLocation(prog, p);
    }
}
