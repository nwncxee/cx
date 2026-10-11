using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The deferred point-light pass (ADR 0029) -- one compute dispatch over the render target after the
/// opaque and cutout passes, reading <see cref="GBuffer"/> and adding every point light's diffuse, specular,
/// film highlight and hot-light debt into the HDR target. The arithmetic lives in <c>cx_deferred_cs</c>, which
/// composes the SAME libraries the forward loop does, so the two cannot differ except by an input.</summary>
internal static unsafe class DeferredLights
{
    /// <summary>The overridable compute stage's resref (hak-limit length).</summary>
    internal const string ShdCs = "cx_deferred_cs";

    /// <summary>Invocations per group per axis, matching the stage's own <c>local_size</c>: one tile, and the
    /// dispatch covers the viewport in ceil(size / this) groups.</summary>
    private const int Group = 16;

    private static ExtenderLog? _log;
    private static readonly StandDown _down = new("DeferredLights");

    /// <summary>The deferred pass's stats buffer: [0] tiles whose list overflowed, [1] the lights they dropped,
    /// [2] pixels that found no usable shadow record, [3] lights <see cref="DeferredShadows"/> left unshadowed
    /// past <c>shadow.perpixel</c>'s budget. A shared point, bound before each dispatch;
    /// the shadow pass binds the same buffer, so one harvest covers both dispatches.</summary>
    internal const uint BindStats = SsboSlots.DeferredStats;
    private const int StatsWords = 8;
    /// <summary>Frames between stats harvests. The counters accumulate GPU-side and are never reset, so a
    /// sampled harvest loses nothing -- the delta since the last one is what the period line adds up.</summary>
    private const int HarvestEvery = 64;

    /// <summary>One program's uniform locations: the walk's (whichever program is bound) and the film
    /// pass's, taken once at each one's link.</summary>
    private struct Locs
    {
        internal int Rect, InvVp, Proj, View, Eye, Cubes, Material, LightOcc, LmMovers, Tiles, LightCount, ReflK, ShSize, ShRes;
    }

    /// <summary>Everything the GL context owns, in one object: context loss replaces it, so no program name,
    /// uniform location or harvest baseline from the dead context survives as a field nobody remembered to
    /// zero -- and the in-loop variant's build verdict goes with it, because a link the dead context refused
    /// may succeed on the next.</summary>
    private sealed class DeferredLightsState
    {
        /// <summary><see cref="Prog"/> is the program the pass BINDS; <see cref="ProgWalk"/> and
        /// <see cref="ProgInLoop"/> are the two it links for itself and <c>SelectEscape</c> picks between.</summary>
        public uint Prog, ProgWalk, BufStats;
        /// <summary>The <c>CX_SH_INLOOP</c> variant -- the walk's own hardware tap per shadowing light past
        /// the record, for the escapes that mean "every light": <c>shadow.cap 0</c>, <c>shadow.perpixel 0</c>,
        /// or no record this frame. The shipped program has no tap in its loop (a missed record is filled
        /// before the walk) because the tap's presence alone cost the pass 2.7 ms.</summary>
        public uint ProgInLoop;
        public bool InLoopFailed, InLoopOn;
        public Locs L, LFilm;
        /// <summary>The FILM pass -- this stage compiled with <c>CX_FILM_PASS</c>, a second dispatch that adds
        /// the water film's static highlights and nothing else. The walk program carries no film: with the
        /// film's light loop in it the pass ran 2.1 ms slower, the same cliff the in-loop fallback tap
        /// caused.</summary>
        public uint ProgFilm;
        /// <summary>The GPU-side totals this period's deltas are taken against, and the readback they arrive
        /// through.</summary>
        public uint LastTiles, LastLights, LastMiss, LastDrop, LastWalk, LastTaps, LastPix, LastEnt;
        public GpuReadback StatsRead;
        public int StatsSeq, StatsFrame;
    }

    private static DeferredLightsState _gl = new();

    /// <summary>The stats buffer <see cref="DeferredShadows"/> counts into as well. Minted by
    /// <see cref="Ready"/>, which <see cref="Active"/> calls before any pass of the frame runs.</summary>
    internal static uint StatsBuf => _gl.BufStats;
    /// <summary>Dispatches this period, for the <c>[alloy]</c> line.</summary>
    private static int _runs;
    /// <summary>Tiles that overflowed their light list and lights they dropped, since the last period line, plus
    /// the GPU-side totals the deltas are taken against.</summary>
    private static uint _overTiles, _overLights;
    /// <summary>Since the last period line: pixels that found no usable visibility record and lights the
    /// per-pixel budget left unshadowed, plus the GPU-side totals the deltas are taken against. The two differ:
    /// a record MISS falls back to one hardware tap per light -- a harder shadow, never a missing one -- while
    /// a light past the BUDGET is deliberate and is the number <c>shadow.perpixel</c> is spending.</summary>
    private static uint _shMiss, _shDrop;
    /// <summary>Since the last period line: the walk's own census -- lights walked and hardware shadow taps it
    /// still paid, BOTH in the shader's own <c>CX_WALK_UNIT</c> sixteens, and the deferred pixels both are
    /// divided by. The two numbers the budget is about: a lights-per-pixel that no longer grows with the area's
    /// lamp count, and a taps-per-pixel the per-pixel shadow budget holds near zero.</summary>
    private static ulong _walked, _taps, _pixels;
    /// <summary>For the bench ladder: list ENTRIES the walk touched, in the same <c>CX_WALK_UNIT</c> sixteens.
    /// Entries minus lights lit is what the reach window, the relevance cut and the facing test rejected --
    /// work the walk paid for and got no light back from, which is the hypothesis the ladder tests.</summary>
    private static ulong _entries;

    /// <summary>The colour pass may draw opaque and cutout batches with the <c>CX_DEFERRED</c> twin this frame:
    /// <c>light.deferred</c> on, this pass's program linked, the G-buffer allocated, a main-view perspective
    /// pass, and <see cref="DeferredShadows"/> armed -- without its record every point-light shadow would be a
    /// single hardware tap. Every <c>/alloy viz</c> mode draws forward (ADR 0029) but the forward class's light-source view, which reads this pass.</summary>
    internal static bool Active => Lighting.Deferred.V && (AlloyRenderer._lightViz == 0 || AlloyRenderer._lightViz == AlloyRenderer.VizLightFrom)
                                   && GBuffer.Ready && !GBuffer.Failed && Programs._progLitDef != 0
                                   && Ready() && DeferredShadows.Ready();

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>The pass. <paramref name="vp"/> is the JITTERED view-projection the prepass rasterised with
    /// (world positions are reconstructed from its inverse); <paramref name="viewport"/> the render viewport.</summary>
    internal static void Run(Matrix4x4* vp, Matrix4x4* view, Matrix4x4* proj, int* viewport, float near, float far)
    {
        if (!Active) return;
        // The HDR target IS the image this pass stores into and its depth attachment the surface it
        // reconstructs: with neither armed there is no deferred frame to correct.
        if (!PostChain.Armed || PostChain.HdrTex == 0 || PostChain.DepthTex == 0)
        { _down.Refuse(_log, "the owned HDR target is not armed", "no deferred point lights this frame"); return; }
        if (viewport[2] <= 0 || viewport[3] <= 0) return;
        if (!Matrix4x4.Invert(*vp, out Matrix4x4 invVp)) return;
        // The same expression AlloyRenderer derives _eyePos with, from the same matrix: V has to be the lit
        // stage's own normalize(uEyePos - vWorld), not an approximation of it.
        if (!Matrix4x4.Invert(*view, out Matrix4x4 camToWorld)) return;
        Vector3 eye = camToWorld.Translation;

        if (Own) SelectEscape(AlloyLights.ShadowCap <= 0f || AlloyLights.ShadowPerPixel <= 0f || DeferredShadows.Res <= 0);
        Gl.UseProgram(_gl.Prog);
        // The light SSBO, the cluster table and the mover spheres, exactly as a lit draw gets them.
        AlloyLights.BindForDraw();
        // ...and this pass's own CxPass block, the cascade and cube arrays, and uLocalCubes. Filled from the same
        // frame state and the same viewport as the colour pass's binds, so the block compares equal and no upload
        // happens -- but it is FILLED rather than assumed: a pass that borrowed another view's cluster table
        // leaves the block saying so (uBounds), and this walk reads that.
        SetUniforms(in _gl.L, viewport, near, far, &invVp, view, proj, &eye);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindStats, _gl.BufStats);

        BindUnit(TextureSlots.ShadowVis, DeferredShadows.TexVis);
        BindUnit(TextureSlots.ShadowKey, DeferredShadows.TexKey);
        BindUnit(TextureSlots.Gbuf0, GBuffer.Tex0);
        BindUnit(TextureSlots.Gbuf1, GBuffer.Tex1);
        BindUnit(TextureSlots.Gbuf2, GBuffer.Tex2);
        BindUnit(TextureSlots.Gbuf3, GBuffer.Tex3);
        // The frame's own depth attachment, read while it is attached: a compute dispatch touches no
        // framebuffer, so there is no feedback loop to have. The lit stage samples the same texture at the same
        // time for the same reason (SsaoPass.BindForDraw's uSceneDepth).
        BindUnit(TextureSlots.GbufDepth, PostChain.DepthTex);
        Gl.BindImageTexture(0, PostChain.HdrTex, 0, 0, 0, Gl.READ_WRITE, Gl.RGBA16F);
        Gl.BindImageTexture(1, GBuffer.TexLight, 0, 0, 0, Gl.WRITE_ONLY, Gl.RGBA32UI);

        Gl.DispatchCompute((uint)(viewport[2] + Group - 1) / Group, (uint)(viewport[3] + Group - 1) / Group, 1);
        GBuffer.LightLive = true;
        // The store is read back three ways before the frame ends: as an ATTACHMENT (grass's root blend, the
        // blended class), as a texture FETCH (Ssr's colour copy, the resolve) and as an image again.
        Gl.MemoryBarrier(Gl.FRAMEBUFFER_BARRIER_BIT | Gl.TEXTURE_FETCH_BARRIER_BIT | Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT);
        // The film's highlights, added on top: a linear term of the same pixel (g.specT x film), so a second
        // read-modify-write of the HDR target is exact. Not while something else owns the slot, which wants the
        // stage it asked for and nothing added to it.
        if (Own && Weather.ReflK > 0)
        {
            Gl.UseProgram(_gl.ProgFilm);
            SetUniforms(in _gl.LFilm, viewport, near, far, &invVp, view, proj, &eye);
            Gl.DispatchCompute((uint)(viewport[2] + Group - 1) / Group, (uint)(viewport[3] + Group - 1) / Group, 1);
            Gl.MemoryBarrier(Gl.FRAMEBUFFER_BARRIER_BIT | Gl.TEXTURE_FETCH_BARRIER_BIT | Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT);
        }

        Gl.BindImageTexture(0, 0, 0, 0, 0, Gl.READ_WRITE, Gl.RGBA16F);
        Gl.BindImageTexture(1, 0, 0, 0, 0, Gl.WRITE_ONLY, Gl.RGBA32UI);
        BindUnit(TextureSlots.Gbuf0, 0); BindUnit(TextureSlots.Gbuf1, 0);
        BindUnit(TextureSlots.Gbuf2, 0); BindUnit(TextureSlots.Gbuf3, 0);
        BindUnit(TextureSlots.GbufDepth, 0);
        BindUnit(TextureSlots.ShadowVis, 0); BindUnit(TextureSlots.ShadowKey, 0);
        Gl.ActiveTexture(Gl.TEXTURE0);   // the unit every pass leaves current: the colour loop rebinds diffuse there
        Gl.UseProgram(0);
        _runs++;
        Harvest();
    }

    /// <summary>Copy the tile-list overflow counters out, sampled: they accumulate GPU-side and are never reset,
    /// so a harvest every <see cref="HarvestEvery"/> frames loses nothing -- the delta since the last one is what
    /// the period line adds up. Through <see cref="GpuReadback"/>, never <c>glGetBufferSubData</c>, which on
    /// NVIDIA waits out the whole queued frame.</summary>
    private static void Harvest()
    {
        if (--_gl.StatsFrame > 0) return;
        _gl.StatsFrame = HarvestEvery;
        if (!GpuReadback.EntryPoints || !_gl.StatsRead.Ensure(StatsWords * sizeof(uint))) return;
        // BUFFER_UPDATE: the copy reads a SHADER-written buffer, which SHADER_STORAGE alone does not order
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT | Gl.BUFFER_UPDATE_BARRIER_BIT);
        int w = _gl.StatsSeq % GpuReadback.Slots, r = (_gl.StatsSeq + 1) % GpuReadback.Slots;
        _gl.StatsSeq++;
        _gl.StatsRead.Copy(_gl.BufStats, 0, w, 0, StatsWords * sizeof(uint));
        _gl.StatsRead.Fence(w);
        uint* v = (uint*)_gl.StatsRead.Ready(r);
        if (v == null) return;
        _overTiles += v[0] - _gl.LastTiles; _overLights += v[1] - _gl.LastLights;
        _shMiss += v[2] - _gl.LastMiss; _shDrop += v[3] - _gl.LastDrop;
        _walked += v[4] - _gl.LastWalk; _taps += v[5] - _gl.LastTaps; _pixels += v[6] - _gl.LastPix;
        _entries += v[7] - _gl.LastEnt;
        _gl.LastTiles = v[0]; _gl.LastLights = v[1]; _gl.LastMiss = v[2]; _gl.LastDrop = v[3];
        _gl.LastWalk = v[4]; _gl.LastTaps = v[5]; _gl.LastPix = v[6]; _gl.LastEnt = v[7];
    }

    private static void BindUnit(uint unit, uint tex)
    {
        Gl.ActiveTexture(Gl.TEXTURE0 + unit);
        Gl.BindTexture(Gl.TEXTURE_2D, tex);
    }

    /// <summary>Every uniform the walk and the film pass both take, through <paramref name="l"/>, the bound
    /// program's own locations.</summary>
    private static void SetUniforms(in Locs l, int* viewport, float near, float far, Matrix4x4* invVp,
                                    Matrix4x4* view, Matrix4x4* proj, Vector3* eye)
    {
        AlloyRenderer.BindPass(l.Cubes, viewport, near, far);
        AlloyRenderer.UploadMaterialModel(l.Material);
        Gl.Uniform2f(l.LightOcc, Lighting.HiddenLights.V ? 1f : 0f, AlloyRenderer.GlossyRough);
        Gl.Uniform1i(l.LmMovers, LightmapBake.MoverMode);
        Gl.Uniform4iv(l.Rect, 1, viewport);
        Gl.UniformMatrix4fv(l.InvVp, 1, 0, (float*)invVp);
        Gl.UniformMatrix4fv(l.Proj, 1, 0, (float*)proj);
        Gl.UniformMatrix4fv(l.View, 1, 0, (float*)view);
        Gl.Uniform3fv(l.Eye, 1, (float*)eye);
        // light.deferredtiles: the per-tile cull, or each pixel's own froxel list (the parity A/B). The tile
        // walk culls uLights itself, so it needs the record count and R4's pick budget -- the same two the
        // cluster build is handed.
        Gl.Uniform1i(l.Tiles, Lighting.DeferredTiles.V ? 1 : 0);
        Gl.Uniform1i(l.LightCount, AlloyLights.UploadedLights);
        Gl.Uniform1i(l.ReflK, Weather.ReflK);
        // CX-188 step 3: the visibility record the shadow dispatch just wrote, and the divisor the walk
        // upsamples it by. 0 = no record this frame (said loudly there), and every light takes the walk's own
        // hardware tap.
        Gl.Uniform2i(l.ShSize, DeferredShadows.Width, DeferredShadows.Height);
        Gl.Uniform1i(l.ShRes, DeferredShadows.Res);
    }

    /// <summary>Build the program on the first frame that wants it, once. A link failure is LOUD and latched: the
    /// alternative is opaque surfaces with no point light on them and nothing in the log.</summary>
    private static bool Ready()
    {
        if (_gl.Prog != 0) return true;
        if (_down.Failed) return false;
        if (Gl.DispatchCompute == null || Gl.MemoryBarrier == null || Gl.BindImageTexture == null || Gl.BindBufferBase == null)
            return _down.Fail(_log, "compute, image stores or SSBO entry points are missing", "deferred point lights are off for the session");
        uint prog = GlProgram.Compute(ShdCs, ShaderStages.Source(ShdCs), _log, "DeferredLights");
        if (prog == 0)
        {
            _log?.Always("DeferredLights: the deferred point-light program did not build (the driver's own log is "
                       + "above) -- light.deferred is off for the session and the forward light loop draws the frame");
            return _down.Fail(_log, "the compute program did not link", "deferred point lights are off for the session");
        }
        uint film = GlProgram.Compute(ShaderStages.WithDefine(ShaderStages.Source(ShdCs), "CX_FILM_PASS 1"), _log, "DeferredLights film");
        if (film == 0)
        {
            _log?.Always("DeferredLights: the film pass did not build (the driver's own log is above) -- light.deferred is "
                       + "off for the session and the forward light loop draws the frame");
            return _down.Fail(_log, "the film pass did not link", "deferred point lights are off for the session");
        }
        _gl.ProgFilm = film;
        _gl.LFilm = Take(film);
        uint b;
        Gl.CreateBuffers(1, &b); _gl.BufStats = b;
        uint* zero = stackalloc uint[StatsWords] { 0, 0, 0, 0, 0, 0, 0, 0 };
        Gl.NamedBufferData(_gl.BufStats, (IntPtr)(StatsWords * sizeof(uint)), zero, Gl.DYNAMIC_DRAW);
        _gl.LastTiles = _gl.LastLights = _gl.LastMiss = _gl.LastDrop = 0;
        _gl.LastWalk = _gl.LastTaps = _gl.LastPix = _gl.LastEnt = 0; _gl.StatsFrame = 1;
        _gl.ProgWalk = prog;
        Select(prog);
        _log?.Info("DeferredLights: ready -- one 16x16 compute dispatch over the G-buffer");
        return true;
    }

    /// <summary>The program this pass binds. Assigning points the pass at another build of its own stage, for
    /// a diagnostic that measures one; the locations come with it, since a build is its own program object.</summary>
    internal static uint Program { get => _gl.Prog; set => Select(value); }

    /// <summary>The bound program is one of the two this pass links for itself. False while something else owns
    /// the slot: the escape stops re-picking and the film pass stays down, so the stage asked for is what runs.</summary>
    private static bool Own => _gl.Prog == _gl.ProgWalk || _gl.Prog == _gl.ProgInLoop;

    /// <summary>Bind <paramref name="prog"/> as the pass's program and take its uniform locations -- at link,
    /// and again on each change of <see cref="Program"/>.</summary>
    private static void Select(uint prog)
    {
        _gl.Prog = prog;
        _gl.L = Take(prog);
    }

    /// <summary>A program's locations, and its two cube arrays' units -- which carry no layout(binding), the lib
    /// being shared with programs that bind them on other units, so they take theirs here at link.</summary>
    private static Locs Take(uint prog)
    {
        Locs l = default;
        l.Rect = GlProgram.Loc(prog, "uRect");
        l.InvVp = GlProgram.Loc(prog, "uInvVp");
        l.Proj = GlProgram.Loc(prog, "uProj");
        l.View = GlProgram.Loc(prog, "uView");
        l.Eye = GlProgram.Loc(prog, "uEyePos");
        l.Cubes = GlProgram.Loc(prog, "uLocalCubes");
        l.Material = GlProgram.Loc(prog, "uMaterial");
        l.LightOcc = GlProgram.Loc(prog, "uLightOcc");
        l.LmMovers = GlProgram.Loc(prog, "uLmMovers");
        l.Tiles = GlProgram.Loc(prog, "uTiles");
        l.LightCount = GlProgram.Loc(prog, "uLightCount");
        l.ReflK = GlProgram.Loc(prog, "uReflK");
        l.ShSize = GlProgram.Loc(prog, "uShSize");
        l.ShRes = GlProgram.Loc(prog, "uShRes");
        Gl.UseProgram(prog);
        Gl.Uniform1i(GlProgram.Loc(prog, "uLocalShadow"), (int)TextureSlots.LocalShadow);
        Gl.Uniform1i(GlProgram.Loc(prog, "uLocalDynShadow"), (int)TextureSlots.LocalShadowDyn);
        Gl.UseProgram(0);
        return l;
    }

    /// <summary>Bind the in-loop-tap variant while <paramref name="want"/> (an "every light" escape is on) and the
    /// shipped program otherwise -- on a CHANGE only, since <see cref="Select"/> re-reads every location.</summary>
    private static void SelectEscape(bool want)
    {
        if (want == _gl.InLoopOn) return;
        if (want && _gl.ProgInLoop == 0 && !_gl.InLoopFailed)
        {
            _gl.ProgInLoop = GlProgram.Compute(ShaderStages.WithDefine(ShaderStages.Source(ShdCs), "CX_SH_INLOOP 1"),
                                            _log, "DeferredLights in-loop taps");
            if (_gl.ProgInLoop == 0)
            {
                _gl.InLoopFailed = true;
                _log?.Always("DeferredLights: the in-loop-tap variant did not link (the driver's own log is above) -- "
                           + "shadow.cap 0 / shadow.perpixel 0 keep the record's four-light budget instead of tapping every light");
            }
        }
        if (want && _gl.ProgInLoop == 0) return;
        Select(want ? _gl.ProgInLoop : _gl.ProgWalk);
        _gl.InLoopOn = want;
    }

    /// <summary>Context loss: the program and any per-size objects are gone.</summary>
    internal static void Forget()
    {
        _gl = new DeferredLightsState();
        _down.Forget();
    }

    internal static string Stats(int frames)
    {
        string s = $"deferred lights {(!Lighting.Deferred.V ? "off" : Lighting.DeferredTiles.V ? "on, tiled" : "on, froxel lists")}"
                 + $" ({_runs} dispatches"
                 // the list cap is a budget WITH a counter: a tile that dropped a light is a visibly unlit lamp,
                 // and a silent one is the VAO drip's bug again
                 + (_overTiles > 0 ? $", TILE LIGHT LIST OVERFLOW {_overTiles} tiles / {_overLights} lights dropped (cap {AlloyLights.ClusterCap})" : "")
                 // CX-188 budget: what the per-pixel shadow budget spent, and the pixels that fell out of it.
                 // The first is the saving; the second takes the walk's own hardware tap per light, and a number
                 // that climbs there is the record not reaching the pixels it is for.
                 + (_shDrop > 0 ? $", {_shDrop} lights unshadowed past the budget of {Shadows.PerPixel.V}" : "")
                 + (_shMiss > 0 ? $", {_shMiss} pixels with no usable shadow record ({(_gl.InLoopOn ? "hard taps in the walk" : "hard taps of the nearest texel's lights")})" : "")
                 // CX-188 budget: what a pixel actually spent, sampled over the harvested frames. "lights lit"
                 // and not "walked": a list entry the reach window, the relevance cut or the facing test
                 // dropped is not in it, and the list's own length is the cluster build's tally. Both counters
                 // come up in the shader's CX_WALK_UNIT sixteens, so each resolves to about 1/16 per pixel --
                 // an average, not an audit, and near-zero taps is the answer the budget is for.
                 + (_pixels > 0 ? $", {16.0 * _walked / _pixels:F1} lights lit/px, {16.0 * _taps / _pixels:F2} hard taps/px" : "")
                 // CX-188 ablation: and the LIST LENGTH the walk read to find them. "entries" is every list
                 // slot the loop touched; the rejected half is what the reach window, the relevance cut and
                 // the facing test threw away after paying to index it -- the cost the ablation ladder prices.
                 + (_pixels > 0 ? $", {16.0 * _entries / _pixels:F1} entries/px ({16.0 * (_entries - Math.Min(_entries, _walked)) / _pixels:F1} rejected)" : "")
                 + (_down.Reason.Length > 0 ? ", last refusal: " + _down.Reason : "") + ")";
        _runs = 0; _overTiles = 0; _overLights = 0; _shMiss = 0; _shDrop = 0;
        _walked = 0; _taps = 0; _pixels = 0; _entries = 0;
        return s;
    }
}
