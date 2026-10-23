using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The HOT-LIGHT DEBT pass (ADR 0029): one compute dispatch over the deferred frame that takes a
/// creature's shadow back out of the baked term, once per pixel -- each 16x16 tile culls this frame's
/// <see cref="PairShadows"/> pairs against its own view-space box and subtracts the spec's own term. A second
/// dispatch, not a branch in the light pass: a tile with no pair exits at the first barrier, G-buffer unread.</summary>
internal static unsafe class PairDebt
{
    /// <summary>The overridable compute stage's resref (hak-limit length).</summary>
    internal const string ShdCs = "cx_pairdebt";

    /// <summary>Invocations per group per axis, matching the stage's own <c>local_size</c>.</summary>
    private const int Group = 16;

    /// <summary>The pass's own stats buffer: [0] tiles whose pair list overflowed, [1] the pairs they dropped,
    /// [2] tiles that held a pair, [3] the ablation ladder's sink, which the shipped program never writes and
    /// nothing reads. A shared point, bound before the dispatch.</summary>
    private const uint BindStats = SsboSlots.PairStats;
    private const int StatsWords = 4;
    /// <summary>Frames between stats harvests. The counters accumulate GPU-side and are never reset, so a
    /// sampled harvest loses nothing -- the delta since the last one is what the period line adds up.</summary>
    private const int HarvestEvery = 64;

    private static ExtenderLog? _log;
    private static readonly StandDown _down = new("PairDebt");
    /// <summary>Everything the GL context owns, in one object: context loss replaces it, so neither a name
    /// nor a uniform location from the dead context survives as a field nobody remembered to zero. The
    /// harvest baselines ride here because the GPU counters they are deltas against go with the buffer.</summary>
    private sealed class PairDebtState
    {
        public uint Prog, BufStats;
        public int LocRect, LocInvVp, LocProj, LocView, LocCubes, LocLmMovers, LocLightOcc, LocDiag;
        /// <summary>The last harvest's absolute counters; the period line reports the delta.</summary>
        public uint LastTiles, LastPairs, LastCov;
        public GpuReadback StatsRead;
        public int StatsSeq, StatsFrame;
    }

    private static PairDebtState _gl = new();
    private static int _runs;
    private static uint _overTiles, _overPairs;
    private static uint _covTiles;

    /// <summary>The debt pass failed for the session: <see cref="PairShadows.On"/> reads it so the pairing
    /// stands down with it and the hot lights keep their dyn pages.</summary>
    internal static bool StoodDown => _down.Failed;

    /// <summary>Is there a debt to take this frame? The pairs exist (which already requires
    /// <see cref="DeferredLights.Active"/>, so a forward frame never reaches here), the atlas is allocated
    /// and this program linked.</summary>
    internal static bool Active => PairShadows.On && PairShadows.Count > 0 && PairShadows.Tex != 0
                                   && GBuffer.Ready && !GBuffer.Failed && Ready();

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>The pass, immediately after <see cref="DeferredLights"/>. <paramref name="vp"/> is the JITTERED
    /// view-projection the prepass rasterised with (world positions come off its inverse).</summary>
    internal static void Run(Matrix4x4* vp, Matrix4x4* view, Matrix4x4* proj, int* viewport, float near, float far)
    {
        if (!Active) return;
        if (!PostChain.Armed || PostChain.HdrTex == 0 || PostChain.DepthTex == 0)
        { _down.Refuse(_log, "the owned HDR target is not armed", "no hot-light debt this frame"); return; }
        if (viewport[2] <= 0 || viewport[3] <= 0) return;
        if (!Matrix4x4.Invert(*vp, out Matrix4x4 invVp)) return;

        Gl.UseProgram(_gl.Prog);
        // the light SSBO, the cluster table, the mover spheres -- and PairShadows' own table and atlas
        AlloyLights.BindForDraw();
        // ...and this pass's CxPass block, the cascade and cube arrays, and uLocalCubes, from the same frame
        // state and the same viewport the colour pass bound: CxLocalBakedVis reads the static cube through it.
        AlloyRenderer.BindPass(_gl.LocCubes, viewport, near, far);
        Gl.Uniform1i(_gl.LocLmMovers, LightmapBake.MoverMode);
        Gl.Uniform1i(_gl.LocDiag, AlloyRenderer._lightViz == AlloyRenderer.VizLightFrom ? 1 : 0);
        // unreached here (no glossy mirror walk in this stage), bound so the program is never left reading an
        // unset uniform that a later edit might make reachable
        Gl.Uniform2f(_gl.LocLightOcc, Lighting.HiddenLights.V ? 1f : 0f, AlloyRenderer.GlossyRough);
        Gl.Uniform4iv(_gl.LocRect, 1, viewport);
        Gl.UniformMatrix4fv(_gl.LocInvVp, 1, 0, (float*)&invVp);
        Gl.UniformMatrix4fv(_gl.LocProj, 1, 0, (float*)proj);
        Gl.UniformMatrix4fv(_gl.LocView, 1, 0, (float*)view);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindStats, _gl.BufStats);

        GBuffer.BindForCompute();
        Gl.BindImageTexture(0, PostChain.HdrTex, 0, 0, 0, Gl.READ_WRITE, Gl.RGBA16F);
        Gl.BindImageTexture(1, GBuffer.TexLight, 0, 0, 0, Gl.READ_WRITE, Gl.RGBA32UI);

        Gl.DispatchCompute((uint)(viewport[2] + Group - 1) / Group, (uint)(viewport[3] + Group - 1) / Group, 1);
        // the store is read back as an attachment (grass's root blend), as a texture fetch (Ssr's copy) and as
        // an image again -- the same three ways DeferredLights' own store is
        Gl.MemoryBarrier(Gl.FRAMEBUFFER_BARRIER_BIT | Gl.TEXTURE_FETCH_BARRIER_BIT | Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT);

        Gl.BindImageTexture(0, 0, 0, 0, 0, Gl.READ_WRITE, Gl.RGBA16F);
        Gl.BindImageTexture(1, 0, 0, 0, 0, Gl.READ_WRITE, Gl.RGBA32UI);
        GBuffer.UnbindCompute();
        Gl.ActiveTexture(Gl.TEXTURE0);   // the unit every pass leaves current
        Gl.UseProgram(0);
        _runs++;
        Harvest();
    }

    /// <summary>Copy the per-tile counters out, sampled, through <see cref="GpuReadback"/> -- never
    /// <c>glGetBufferSubData</c>, which on NVIDIA waits out the whole queued frame.</summary>
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
        _overTiles += v[0] - _gl.LastTiles; _overPairs += v[1] - _gl.LastPairs; _covTiles += v[2] - _gl.LastCov;
        _gl.LastTiles = v[0]; _gl.LastPairs = v[1]; _gl.LastCov = v[2];
    }

    /// <summary>Build the program on the first frame that wants it, once. A link failure is LOUD and latched:
    /// a deferred frame whose hot branch is compiled out and whose debt pass is missing shows every creature's
    /// shadow gone from the baked ground, and nothing in the log.</summary>
    private static bool Ready()
    {
        if (_gl.Prog != 0) return true;
        if (_down.Failed) return false;
        if (Gl.DispatchCompute == null || Gl.MemoryBarrier == null || Gl.BindImageTexture == null || Gl.BindBufferBase == null)
            return _down.Fail(_log, "compute, image stores or SSBO entry points are missing",
                              "the hot-light debt is off for the session and baked surfaces take no creature shadow on the deferred path");
        string src = ShaderStages.Source(ShdCs);
        if (!ShaderContract.RecordSafe(src, "CxChart", LightmapBake.RecFloats))
            return _down.Fail(_log, "the program's CxChart record disagrees with the extender's (an override older than it)",
                              "the hot-light debt is off for the session and baked surfaces take no creature shadow on the deferred path");
        uint prog = GlProgram.Compute(ShdCs, src, _log, "PairDebt");
        if (prog == 0)
        {
            _log?.Always("PairDebt: the hot-light debt program did not build (the driver's own log is above) -- "
                       + "baked surfaces take no creature shadow from static lights on the deferred path; "
                       + "light.deferred 0 is the forward path, which still does");
            return _down.Fail(_log, "the compute program did not link",
                              "the hot-light debt is off for the session");
        }
        uint b;
        Gl.CreateBuffers(1, &b); _gl.BufStats = b;
        uint* zero = stackalloc uint[StatsWords] { 0, 0, 0, 0 };
        Gl.NamedBufferData(_gl.BufStats, (IntPtr)(StatsWords * sizeof(uint)), zero, Gl.DYNAMIC_DRAW);
        _gl.LastTiles = _gl.LastPairs = _gl.LastCov = 0; _gl.StatsFrame = 1;
        Select(prog);
        _log?.Info("PairDebt: ready -- one 16x16 compute dispatch takes the creature shadows back out of the baked term");
        return true;
    }

    /// <summary>The program this pass binds. Assigning points the pass at another build of its own stage, for
    /// a diagnostic that measures one; the locations come with it, since a build is its own program object.</summary>
    internal static uint Program { get => _gl.Prog; set => Select(value); }

    /// <summary>Bind <paramref name="prog"/> and take its uniform locations -- at link, and again on each
    /// change of <see cref="Program"/>.</summary>
    private static void Select(uint prog)
    {
        _gl.Prog = prog;
        _gl.LocRect = GlProgram.Loc(prog, "uRect");
        _gl.LocInvVp = GlProgram.Loc(prog, "uInvVp");
        _gl.LocProj = GlProgram.Loc(prog, "uProj");
        _gl.LocView = GlProgram.Loc(prog, "uView");
        _gl.LocCubes = GlProgram.Loc(prog, "uLocalCubes");
        _gl.LocLmMovers = GlProgram.Loc(prog, "uLmMovers");
        _gl.LocDiag = GlProgram.Loc(prog, "uDiag");
        _gl.LocLightOcc = GlProgram.Loc(prog, "uLightOcc");
        // the two cube arrays carry no layout(binding) -- the lib is shared with programs that bind them on
        // other units -- so they take theirs here, as every lit program does at its own link
        Gl.UseProgram(prog);
        Gl.Uniform1i(GlProgram.Loc(prog, "uLocalShadow"), (int)TextureSlots.LocalShadow);
        Gl.Uniform1i(GlProgram.Loc(prog, "uLocalDynShadow"), (int)TextureSlots.LocalShadowDyn);
        Gl.UseProgram(0);
    }

    /// <summary>Context loss: the program and the stats buffer are gone.</summary>
    internal static void Forget()
    {
        _gl = new PairDebtState();
        _down.Forget();
    }

    internal static string Stats(int frames)
    {
        float n = MathF.Max(frames, 1);
        string s = _down.Failed ? "pair debt STOOD DOWN: " + _down.Reason
                 : $"pair debt {_runs} dispatches, {_covTiles / n:F0} 16x16 tiles owed/frame"
                 // the cap is a budget WITH a counter: a tile that dropped a pair is a creature shadow with a
                 // missing bite out of it, and a silent one is the VAO drip's bug again
                 + (_overTiles > 0 ? $", TILE PAIR LIST OVERFLOW {_overTiles} tiles / {_overPairs} pairs dropped" : "")
                 + (_down.Reason.Length > 0 && !_down.Failed ? ", last refusal: " + _down.Reason : "");
        _runs = 0; _overTiles = 0; _overPairs = 0; _covTiles = 0;
        return s;
    }
}
