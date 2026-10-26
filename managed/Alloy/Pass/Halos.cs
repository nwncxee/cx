using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Light flares: one view-aligned billboard per authored flare element, the chain vanilla lays along
/// the line from the screen centre through the light. Population and layout are vanilla's, re-decided each frame
/// at the enrol slot (<see cref="Flares"/>); an element draws its own texture, and where none is resident the
/// procedural halo profile a hak can override. Vanilla's near-plane quad becomes a billboard of world size.</summary>
internal static unsafe partial class Halos
{
    internal const string ShdHaloVs = "cx_halo_vs", ShdHaloFs = "cx_halo_fs";
    /// <summary>The occlusion probe, one invocation per halo. Not an overridable stage: it produces a
    /// number the simulation consumes, not a pixel.</summary>
    private const string OccCs = "cx_haloocc_c";

    /// <summary>SSBO binding points, both shared: the draw binds <see cref="BindHalos"/> and the occlusion
    /// probe it runs reads it there before binding <see cref="BindVis"/>.</summary>
    private const uint BindHalos = SsboSlots.Halos, BindVis = SsboSlots.HaloVis;
    private const uint GlTriangleStrip = 0x0005;   // not-an-offset: GL primitive-mode enum
    private const int InitialHalos = 64;
    /// <summary><c>Scene::RenderLensFlares</c> @0x471860's own integration terms: the occlusion fade travels at
    /// 3.0 per second on a delta floored at 0.02s, so a flare takes a third of a second to appear or vanish
    /// however fast the frame runs.</summary>
    private const float FlareRate = 3.0f, FlareDtFloor = 0.02f;
    /// <summary>The probe's fixed-point scale and its "no sample" slot value -- off screen or behind the
    /// camera, where the fade HOLDS rather than decaying toward an answer nobody measured.</summary>
    private const int VisScale = 1024, VisNone = 0;
    private const int Ring = GpuReadback.Slots;
    private const int OccGroup = 64;   // not-an-offset: local_size_x in cx_haloocc_c.glsl
    /// <summary><c>ManageSceneBSP</c> @0x47fa80 gathers the frame's light candidates around the scene focus out to
    /// this or the fog end, whichever is further, past each light's own adjusted radius.</summary>
    private const float MinFocusReach = 45f;
    /// <summary>The soft range, in metres, of the per-pixel cut and the probe: a surface this far in front of the
    /// light starts to hide it.</summary>
    private const float OcclusionSoftness = 0.6f;

    private static ExtenderLog? _log;

    /// <summary>What staging knows about an element beside its GPU record: the light's rig id, the texture it
    /// draws with and whether it leads its light. One record, so the sort moves them together.</summary>
    private struct Staged
    {
        /// <summary>The flare's light, as <c>DbLight.Id</c> names it -- the Lit row or part index, which
        /// unlike the engine's <c>PartLight*</c> is never recycled and is never zero. Elements of one light
        /// share it, so they share one fade; a ring sample is matched back through it.</summary>
        public IntPtr Key;
        /// <summary>The GL name this element draws with, 0 for the procedural profile. Also the batch key:
        /// the list is sorted on it, so one draw serves every element sharing a texture.</summary>
        public uint Tex;
        /// <summary>Is this the light's first staged element -- the one the occlusion probe measures? The
        /// others would step the same light's fade a second time.</summary>
        public bool Lead;
    }

    /// <summary>This frame's flare elements, grouped by texture, and the area's resolved flare textures.
    /// One owner: the area edge replaces it, which is what releases those textures.</summary>
    private sealed class HaloStageState
    {
        public HaloRec[] Rec = new HaloRec[InitialHalos];
        public Staged[] At = new Staged[InitialHalos];
        public int Count;
        /// <summary>Elements an authored chain named a texture for but no size, position or colour shift --
        /// vanilla reads past the end of the short array; nothing is drawn here, and this is the count.</summary>
        public int Short;
        /// <summary>One <c>TexRegistry</c> reference per flare resref the area named, so a frame costs a
        /// dictionary probe rather than an acquire.</summary>
        public readonly Dictionary<string, Db.TexId> Textures = new();
    }

    private static HaloStageState _st = new();

    /// <summary>Every GL name and location the context owns, in one object: context loss replaces it. The
    /// fences die with the context like the buffers: forgotten, never deleted.</summary>
    private sealed class HaloGlState
    {
        public uint Prog, Buf, Vao, OccProg;
        public int SsboCap, LocView, LocProj, LocDepth, LocGain, LocViz, LocFirst, LocFlare, LocTextured;
        public int OccLoView, OccLoProj, OccLoDepth, OccLoCount;
        /// <summary>The probe's output (one uint per halo, device-local) and the mapped ring it is copied into.</summary>
        public uint VisDev;
        public GpuReadback VisRead;
        public int VisSeq, VisCap;
    }

    private static HaloGlState _gl = new();
    private static bool _failed, _off;
    private static int _drawn, _groups, _bare;

    private static readonly int[] _visN = new int[Ring];
    private static IntPtr[] _visKey = new IntPtr[Ring * InitialHalos];
    private static uint[] _vis = new uint[InitialHalos];
    /// <summary>The last sample the probe read back and the keys it measured, for <see cref="Occlude"/>: read
    /// in Alloy's context by <see cref="Collect"/>, consumed on the engine's side with no GL call at all.</summary>
    private static IntPtr[] _readyKey = new IntPtr[InitialHalos];
    private static int _readyN;
    private static long _occLate;
    private static bool _occFailed;
    private static int _occFaded;

    /// <summary>The dials are <see cref="Effects"/>' <c>halos</c> (the brightness, and 0 is the pass off) /
    /// <c>halosize</c> / <c>haloviz</c>: one live knob each, so a chat set is what the next launch reads.</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        log.Info($"Halos: armed -- flare lights from the scene database drawn as their authored element chain (/effects; size {Effects.HaloSize.V:F2} gain {Effects.Halos.V:F2})");
    }

    /// <summary><c>RenderLensFlares</c> @0x471860 draws a flare, and integrates its fade, only while the camera
    /// stands within its <c>flareradius</c>.</summary>
    private static bool Reaches(Vector3 pos, float flareRadius)
        => !FrameInputs.HasCamera || (pos - FrameInputs.CameraPos).LengthSquared() <= flareRadius * flareRadius;

    /// <summary>Take the probe's last answer (read fenced, two frames late, in Alloy's context) and walk each
    /// flare's <c>PartLight+0xa0</c> toward it at the engine's own rate -- nothing engine-side integrates that
    /// field any more. No GL call: this runs in the ENGINE's context, and a sample whose light is no longer a
    /// halo is dropped. No sample HOLDS the fade; a probe that cannot build says so once.</summary>
    private static void Occlude()
    {
        if (_occFailed || _st.Count == 0) return;
        int have = _readyN;
        if (have <= 0) return;
        float dt = FrameDelta.Delta(FlareDtFloor);
        if (dt <= 0f) return;
        _occFaded = 0;
        for (int i = 0; i < have; i++)
        {
            uint s = _vis[i];
            if (s == VisNone) continue;
            IntPtr key = _readyKey[i];
            // zero is a trailing element of a light whose lead carried the sample: stepping it would double the rate
            if (key == IntPtr.Zero) continue;
            int live = -1;
            // ponytail: linear match, O(samples x elements) -- tens of flares per view; a key->index map if that grows
            for (int j = 0; j < _st.Count && live < 0; j++) if (_st.At[j].Key == key) live = j;
            if (live < 0) continue;
            float target = MathF.Min(1f, (s - 1) / (float)VisScale);
            float was = Fade(key), next = Step(was, target, dt);
            if (next != was) _occFaded++;
            SetFade(key, next);
        }
    }

    /// <summary>Each flare's occlusion fade, keyed by the light's rig id: there is no <c>PartLight+0xa0</c> to
    /// integrate it into.</summary>
    private static readonly Dictionary<IntPtr, float> _dbFade = new();

    /// <summary>A flare's integrated occlusion fade (a light not yet measured starts at 0 and fades in, as a fresh
    /// engine light does).</summary>
    private static float Fade(IntPtr key) => _dbFade.TryGetValue(key, out float f) ? f : 0f;

    private static void SetFade(IntPtr key, float v) => _dbFade[key] = v;

    /// <summary>The halo population: every light of <paramref name="scene"/> that <see cref="Flares"/> admits, read
    /// off the database the way <c>DbLights</c> reads the rig; <paramref name="radiusMult"/> is the rig's own.</summary>
    internal static void EnrollDb(Db.DbId scene, float radiusMult)
    {
        _st.Count = 0;
        if (_off || !Effects.HalosOn) return;
        int lits = Db.SceneDb.Lights.High, parts = Db.DbInputs.LightCount;
        Vector3 focus = Db.SceneDb.SceneFocus[scene.Index];
        float fogEnd = Db.SceneDb.SceneFogFar[scene.Index];
        for (int lit = 1; lit < lits; lit++)
            if (Db.DbInputs.LitLight(lit, out Db.DbLight l)) StageDb(in l, scene, radiusMult, focus, fogEnd);
        for (int k = 0; k < parts; k++)
            if (Db.DbInputs.Light(k, out Db.DbLight l) && l.Lit < 0) StageDb(in l, scene, radiusMult, focus, fogEnd);
        Occlude();
        for (int i = 0; i < _st.Count; i++)
        {
            float f = _occFailed ? 1f : Fade(_st.At[i].Key);
            f = f > 0f ? MathF.Min(f, 1f) : 0f;
            _st.Rec[i].Tint *= f;
        }
        Batch();
    }

    /// <summary>How many elements this light node actually authored: <c>RenderLensFlares</c> @0x471860 loops
    /// the resolved texture array and indexes the other three with the same subscript, reading past the end of
    /// a short one. Alloy draws what the model named -- the shortest of the four.</summary>
    internal static int Elements(LightDef def)
        => Math.Min(Math.Min(def.FlareTextures.Length, def.FlareSizes.Length),
                    Math.Min(def.FlarePositions.Length, def.FlareColors.Length));

    private static void StageDb(in Db.DbLight l, Db.DbId scene, float radiusMult, Vector3 focus, float fogEnd)
    {
        if (!l.Enabled || !Db.SceneDb.Objects.Valid(l.Obj) || Db.SceneDb.ObjScene[l.Obj.Index].Value != scene.Value) return;
        LightDef? def = Db.DbInputs.LightDefOf(in l);
        if (def == null || !Flares(def, l.Position, l.Colour, l.Radius, l.Multiplier, radiusMult, focus, fogEnd)) return;
        // RenderLensFlares @0x471860 skips a light standing in a tile still under fog of war
        int tile = Db.TileBuild.At(scene, l.Position);
        if (tile >= 0 && Db.SceneDb.TileFogged[tile]) return;
        if (!Reaches(l.Position, def.FlareRadius)) return;
        int n = Elements(def);
        _st.Short += def.FlareTextures.Length - n;
        bool lead = true;
        for (int i = 0; i < n; i++)
        {
            float size = def.FlareSizes[i];
            if (size <= 0f) continue;
            Reserve(_st.Count + 1);
            ref HaloRec d = ref _st.Rec[_st.Count];
            d.Pos = l.Position;
            d.Softness = OcclusionSoftness;
            d.Tint = Tint(l.Colour, def.FlareColors[i], 1f);
            d.FlareRadius = def.FlareRadius;
            d.Size = size * Effects.HaloSize.V;
            d.FlarePosition = def.FlarePositions[i];
            d.Probe = new Vector2(lead ? 1f : 0f, 0f);
            _st.At[_st.Count] = new Staged { Key = l.Id, Tex = TexName(def.FlareTextures[i]), Lead = lead };
            lead = false;
            _st.Count++;
        }
    }

    /// <summary>Room for <paramref name="want"/> staged elements. Growth doubles, so an area settles on its
    /// own size within a frame or two and the staging loop allocates nothing after that.</summary>
    private static void Reserve(int want)
    {
        if (want <= _st.Rec.Length) return;
        int cap = Math.Max(want, _st.Rec.Length * 2);
        Array.Resize(ref _st.Rec, cap);
        Array.Resize(ref _st.At, cap);
    }

    /// <summary>This element's flare texture, 0 until one is resident: a preview would draw the white
    /// stand-in as a flare, so an element waits on its own artwork behind the procedural profile.</summary>
    private static uint TexName(string resref)
    {
        if (resref.Length == 0) return 0;
        if (!_st.Textures.TryGetValue(resref, out Db.TexId id)) _st.Textures[resref] = id = Db.TexRegistry.Acquire(resref);
        return Db.TexRegistry.TryResident(id, out uint name, out _) ? name : 0u;
    }

    /// <summary>Sort the staged elements by texture so each GL name is bound once and drawn as one instanced
    /// range. Insertion sort: tens of elements, already nearly grouped, and it moves no memory on a frame
    /// whose lights have not changed.</summary>
    private static void Batch()
    {
        for (int i = 1; i < _st.Count; i++)
        {
            HaloRec r = _st.Rec[i];
            Staged a = _st.At[i];
            int j = i - 1;
            while (j >= 0 && _st.At[j].Tex > a.Tex) { _st.Rec[j + 1] = _st.Rec[j]; _st.At[j + 1] = _st.At[j]; j--; }
            _st.Rec[j + 1] = r; _st.At[j + 1] = a;
        }
    }

    /// <summary>Does vanilla flare this light? <c>GetLensFlareLights</c> @0x4c1d80 walks only the frame's candidates,
    /// which <c>SetupLightsForThisPositionAndRadius</c> @0x4c1df0 admits when isdynamic or affectdynamic, radius in
    /// (0, 1000), colour not black, multiplier above 0 and the scene focus within the adjusted radius plus
    /// max(45, fog end); of those it keeps lights with flare colour shifts, and the draw loops their textures.</summary>
    internal static bool Flares(LightDef def, Vector3 pos, Vector3 colour, float radius, float multiplier,
                                float radiusMult, Vector3 focus, float fogEnd)
    {
        if (def.FlareColors.Length <= 0 || def.FlareTextures.Length <= 0) return false;
        if (!def.Dynamic && !def.AffectDynamic) return false;
        if (!(radius > 0f && radius < EngineLayout.DirectionalRange) || !(multiplier > 0f)) return false;
        if (colour == Vector3.Zero) return false;
        Db.DbLights.Adjust(radius, multiplier, radiusMult, ref colour, out float adjR);
        float reach = adjR + MathF.Max(MinFocusReach, fogEnd);
        return (pos - focus).LengthSquared() < reach * reach;
    }

    /// <summary>One flare's occlusion fade after one step: it travels toward <paramref name="target"/> at
    /// <see cref="FlareRate"/> per second and never overshoots it. Vanilla's target was binary, so this
    /// reduces to <c>RenderLensFlares</c>' own <c>+/- dt * 3</c> for a target of 0 or 1, and interpolates for
    /// the partial coverage Alloy can measure and a ray cannot.</summary>
    internal static float Step(float fade, float target, float dt)
    {
        float step = FlareRate * dt;
        return fade < target ? MathF.Min(target, fade + step) : MathF.Max(target, fade - step);
    }

    /// <summary>A staged halo's colour: the RAW light colour plus the node's shift, as <c>RenderLensFlares</c>
    /// @0x471860 sums them, times the clamped fade -- display space and unclamped above, because vanilla clamps each
    /// channel only after the view weights the vertex stage applies. NaN stages as 0.</summary>
    internal static Vector3 Tint(Vector3 colour, Vector3 shift, float fade)
    {
        Vector3 v = colour + shift;
        float f = fade > 0f ? MathF.Min(fade, 1f) : 0f;
        return new Vector3(Finite(v.X), Finite(v.Y), Finite(v.Z)) * f;
    }

    /// <summary>A channel floored at 0 (no weight lifts a negative one past vanilla's clamp) and capped finite: NaN
    /// reads 0, and an Inf cannot reach the vertex stage's weights as Inf x 0.</summary>
    private static float Finite(float c) => c > 0f ? MathF.Min(c, 1e6f) : 0f;

    /// <summary>The flare pass: after the particles, where vanilla draws its flares (step 29). Additive
    /// (ONE, ONE), no depth test -- the engine's own <c>SetBlendFunc(2,2)</c> with <c>Disable(0xb)</c>, and
    /// visibility is the shader's own depth-disc read of the scene copy. One draw per texture group; hands
    /// back blend off, depth test on, depth mask on.</summary>
    internal static void Draw(Matrix4x4* view, Matrix4x4* proj, int* viewport)
    {
        int n = _st.Count;
        if (n == 0 || !EnsureProgram()) return;
        if (!SceneDepth.Take(viewport)) return;
        if (n > _gl.SsboCap)
        {
            _gl.SsboCap = Math.Max(n, InitialHalos);
            Gl.NamedBufferData(_gl.Buf, (IntPtr)(_gl.SsboCap * sizeof(HaloRec)), null, Gl.DYNAMIC_DRAW);
        }
        fixed (HaloRec* s = _st.Rec)
            Gl.NamedBufferSubData(_gl.Buf, IntPtr.Zero, (IntPtr)(n * sizeof(HaloRec)), s);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindHalos, _gl.Buf);
        SceneDepth.Bind();
        Probe(view, proj);
        Gl.UseProgram(_gl.Prog);
        Gl.UniformMatrix4fv(_gl.LocView, 1, 0, (float*)view);
        Gl.UniformMatrix4fv(_gl.LocProj, 1, 0, (float*)proj);
        Gl.Uniform1i(_gl.LocViz, Effects.HaloViz.V);
        Gl.Uniform1i(_gl.LocDepth, (int)SceneDepth.Unit);
        Gl.Uniform1i(_gl.LocFlare, (int)TextureSlots.Diffuse);
        Gl.Uniform1f(_gl.LocGain, Effects.Halos.V);
        new PassState(depth: false, depthWrite: false, blend: true,
                      blendSrc: AlloyRenderer.GlOne, blendDst: AlloyRenderer.GlOne).Apply();
        Gl.BindVertexArray(EmptyVao());
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.Diffuse);
        for (int at = 0; at < n;)
        {
            uint tex = _st.At[at].Tex;
            int run = 1;
            while (at + run < n && _st.At[at + run].Tex == tex) run++;
            Gl.BindTexture(Gl.TEXTURE_2D, tex);
            Gl.Uniform1i(_gl.LocTextured, tex != 0 ? 1 : 0);
            Gl.Uniform1i(_gl.LocFirst, at);
            Gl.DrawArraysInstancedBaseInstance(GlTriangleStrip, 0, 4, run, 0);
            if (tex == 0) _bare += run;
            at += run;
            _groups++;
        }
        Gl.BindTexture(Gl.TEXTURE_2D, 0);   // unit 0 is the diffuse every later pass expects to own
        Gl.BindVertexArray(0);
        Gl.Disable(Gl.BLEND);
        Gl.Enable(Gl.DEPTH_TEST);
        Gl.DepthMask(1);
        _drawn += n;
    }

    /// <summary>Dispatch this frame's occlusion probe into the ring slot two frames from being read.
    /// Runs with the halo SSBO and <see cref="SceneDepth"/> already bound, before the draw, so it shares both.</summary>
    private static void Probe(Matrix4x4* view, Matrix4x4* proj)
    {
        if (!EnsureProbe()) return;
        Collect();
        int n = _st.Count, w = _gl.VisSeq % Ring;
        if (n > _gl.VisCap)
        {
            _gl.VisCap = Math.Max(n, InitialHalos);
            _visKey = new IntPtr[Ring * _gl.VisCap];
            for (int i = 0; i < Ring; i++) _visN[i] = 0;   // the old keys' indices no longer address this ring
            if (!_gl.VisRead.Ensure((long)_gl.VisCap * sizeof(uint))) { Fail("the driver refused the readback mapping"); return; }
            Gl.EnsureStorage(ref _gl.VisDev, (long)_gl.VisCap * sizeof(uint), true);
        }
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindVis, _gl.VisDev);
        Gl.UseProgram(_gl.OccProg);
        Gl.UniformMatrix4fv(_gl.OccLoView, 1, 0, (float*)view);
        Gl.UniformMatrix4fv(_gl.OccLoProj, 1, 0, (float*)proj);
        Gl.Uniform1i(_gl.OccLoDepth, (int)SceneDepth.Unit);
        Gl.Uniform1i(_gl.OccLoCount, n);
        Gl.DispatchCompute((uint)((n + OccGroup - 1) / OccGroup), 1, 1);
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT | Gl.BUFFER_UPDATE_BARRIER_BIT);
        _gl.VisRead.Copy(_gl.VisDev, 0, w, 0, (long)n * sizeof(uint));
        _gl.VisRead.Fence(w);
        // only a light's lead element carries its key: the rest measure the same light and would step its fade again
        for (int i = 0; i < n; i++) _visKey[w * _gl.VisCap + i] = _st.At[i].Lead ? _st.At[i].Key : IntPtr.Zero;
        _visN[w] = n;
        _gl.VisSeq++;
    }

    /// <summary>Take the slot written two probes ago off the mapping, once its copy has landed. It used to be a
    /// <c>glGetNamedBufferSubData</c>, which NVIDIA queues behind the whole frame submitted so far and waits out:
    /// 7-9 ms of CPU a frame the moment an area had one flare light, fence or no fence. A slot still in flight
    /// keeps the previous sample (counted <see cref="_occLate"/>).</summary>
    private static void Collect()
    {
        int r = (_gl.VisSeq + 1) % Ring;
        int have = _visN[r];
        if (have <= 0) return;
        uint* src = (uint*)_gl.VisRead.Ready(r);
        if (src == null) { _occLate++; return; }
        if (have > _vis.Length) _vis = new uint[have];
        if (have > _readyKey.Length) _readyKey = new IntPtr[have];
        for (int i = 0; i < have; i++) { _vis[i] = src[i]; _readyKey[i] = _visKey[r * _gl.VisCap + i]; }
        _readyN = have;
    }

    private static bool EnsureProbe()
    {
        if (_gl.OccProg != 0) return true;
        if (_occFailed) return false;
        if (Gl.DispatchCompute == null || Gl.MemoryBarrier == null || !GpuReadback.EntryPoints)
            return Fail("this context has no compute, no persistent mapping or no fences");
        _gl.OccProg = GlProgram.Compute(OccCs, ShaderSource.Get(OccCs + ".glsl"), _log, "Halos occlusion probe");
        if (_gl.OccProg == 0) return Fail("the probe program did not build");
        _gl.OccLoView = GlProgram.Loc(_gl.OccProg, "uView");
        _gl.OccLoProj = GlProgram.Loc(_gl.OccProg, "uProj");
        _gl.OccLoDepth = GlProgram.Loc(_gl.OccProg, "uSceneDepth");
        _gl.OccLoCount = GlProgram.Loc(_gl.OccProg, "uCount");
        return true;
    }

    private static bool Fail(string why)
    {
        _occFailed = true;
        _log?.Info($"Halos: the flare occlusion fade is OFF for the session -- {why}. Halos keep their "
                 + "per-pixel depth occlusion and draw at full strength; PartLight+0xa0 is left alone.");
        return false;
    }

    /// <summary>An attribute-less VAO: the quad corners come from gl_VertexID.</summary>
    private static uint EmptyVao()
    {
        if (_gl.Vao == 0) { uint v; Gl.CreateVertexArrays(1, &v); _gl.Vao = v; }
        return _gl.Vao;
    }

    private static bool EnsureProgram()
    {
        if (_gl.Prog != 0) return true;
        if (_failed || Gl.DrawArraysInstancedBaseInstance == null) return false;
        string vs = ShaderStages.Source(ShdHaloVs), fs = ShaderStages.Source(ShdHaloFs);
        _gl.Prog = GlProgram.Graphics(ShdHaloVs, vs, ShdHaloFs, fs, _log, "Halos", out string vsSrc, out string fsSrc);
        if (_gl.Prog != 0 && !ShaderContract.FlareSafe(vsSrc, fsSrc))
        {
            Gl.DeleteProgram(_gl.Prog);
            _log?.Always("Halos: a flare stage override predates the element chain -- it indexes the raw instance "
                       + "or declares no uFlare, so it would draw one texture group's elements for every group. "
                       + "Rebuilding both stages from the embedded defaults (docs/shader-overrides.md).");
            _gl.Prog = GlProgram.Graphics(ShaderSource.Get(ShdHaloVs + ".glsl"), ShaderSource.Get(ShdHaloFs + ".glsl"),
                                          _log, "Halos (embedded)", content: false);
        }
        if (_gl.Prog == 0) { _failed = true; _log?.Info("Halos: program build failed -- no halos this session"); return false; }
        uint b; Gl.CreateBuffers(1, &b); _gl.Buf = b; _gl.SsboCap = 0;
        _gl.LocView = GlProgram.Loc(_gl.Prog, "uView");
        _gl.LocProj = GlProgram.Loc(_gl.Prog, "uProj");
        _gl.LocViz = GlProgram.Loc(_gl.Prog, "uViz");
        _gl.LocDepth = GlProgram.Loc(_gl.Prog, "uSceneDepth");
        _gl.LocGain = GlProgram.Loc(_gl.Prog, "uGain");
        _gl.LocFirst = GlProgram.Loc(_gl.Prog, "uFirst");
        _gl.LocFlare = GlProgram.Loc(_gl.Prog, "uFlare");
        _gl.LocTextured = GlProgram.Loc(_gl.Prog, "uTextured");
        return true;
    }

    internal static void ContextLost()
    {
        _gl = new HaloGlState();
        DropSamples();
    }

    /// <summary>Forget the probe samples in flight and the last one read back.</summary>
    private static void DropSamples()
    {
        _readyN = 0;
        for (int i = 0; i < Ring; i++) _visN[i] = 0;
    }

    /// <summary>World edge: the fades and the probe samples in flight are keyed by light-table index, which the
    /// next area re-tenants, so each goes, and the area's flare textures go with them; the new area's flares
    /// fade in as fresh engine lights do.</summary>
    internal static void Revoke()
    {
        _dbFade.Clear();
        foreach (Db.TexId id in _st.Textures.Values) Db.TexRegistry.Release(id);
        _st = new HaloStageState();
        DropSamples();
    }

    internal static void ResetPeriod() { _drawn = 0; _occFaded = 0; _occLate = 0; _groups = 0; _bare = 0; _st.Short = 0; }
    internal static string Stats() => _off ? "halos unavailable" : !Effects.HalosOn ? "halos off"
        : $"halos {_st.Count} flare elements this frame; this period {_drawn} element draws in {_groups} texture "
          + $"groups ({_bare} on the procedural profile, their texture not yet resident), "
          + $"{(_occFailed ? "occlusion fade OFF" : $"{_occFaded} fading")}"
          + (_st.Short > 0 ? $", {_st.Short} element draws skipped for a texture with no size/position/shift" : "")
          + (_occLate > 0 ? $", {_occLate} probe reads deferred (GPU behind)" : "");
    internal static string Describe() => $"{(_off ? "UNAVAILABLE" : Effects.HalosOn ? "on" : "off")} size {Effects.HaloSize.V:F2} gain {Effects.Halos.V:F2} viz {Effects.HaloViz.V}";

    /// <summary>The occlusion integrator, the tint, the population gate and the chain length, headless: too slow
    /// a fade smears flares through walls, too loose a gate lights flares vanilla never draws. Oracle: hand-written
    /// values read off <c>RenderLensFlares</c> and <c>SetupLightsForThisPositionAndRadius</c>, a stock tile lamp's
    /// authored flare block, and the embedded GLSL text.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        // Vanilla's two cases: a clear ray climbs to 1 and a blocked one falls to 0, both at 3 per second.
        check.Expect(Math.Abs(Step(0f, 1f, 0.1f) - 0.3f) < 1e-5f, "a visible flare climbs at 3 per second");
        check.Expect(Math.Abs(Step(1f, 0f, 0.1f) - 0.7f) < 1e-5f, "an occluded one falls at the same rate");
        check.Expect(Step(0.9f, 1f, 1f) == 1f, "the climb stops at the target, not past it");
        check.Expect(Step(0.1f, 0f, 1f) == 0f, "and so does the fall");
        // Partial coverage is the whole point of measuring depth rather than casting one ray.
        check.Expect(Math.Abs(Step(0.5f, 0.5f, 1f) - 0.5f) < 1e-6f, "a flare already at its coverage holds");
        check.Expect(Step(0f, 0.4f, 1f) == 0.4f, "a partly covered flare settles at its coverage, not at 1");
        // The staged colour is vanilla's sum, raw: the clamp waits for the vertex stage's view weights.
        check.Expect(Tint(new Vector3(255f, 0f, 0f), Vector3.Zero, 1f) == new Vector3(255f, 0f, 0f), "an overbright channel stages raw, not clamped");
        Vector3 summed = Tint(new Vector3(0.5f, 0f, -2f), new Vector3(0.6f, 0f, 0f), 1f);
        check.Expect(MathF.Abs(summed.X - 1.1f) < 1e-5f && summed.Y == 0f && summed.Z == 0f, "colour + shift is summed and floored at 0");
        check.Expect(Tint(new Vector3(float.NaN, 1f, 1f), Vector3.Zero, 1f).X == 0f && Tint(Vector3.One, Vector3.Zero, float.NaN) == Vector3.Zero,
                     "a NaN colour or fade stages as 0");
        check.Expect(Tint(Vector3.One, Vector3.Zero, 3f) == Vector3.One, "the fade weight is clamped");
        Vector3 inf = Tint(new Vector3(float.PositiveInfinity, 0.5f, 0f), Vector3.Zero, 1f);
        check.Expect(float.IsFinite(inf.X) && inf.Y == 0.5f, "an infinite colour stages finite");
        Vector3 torch = Tint(new Vector3(1.75f, 1.5f, 1f), new Vector3(0f, 0f, 0.5f), 0.5f);
        check.Expect(torch == new Vector3(0.875f, 0.75f, 0.75f), $"the shift joins the raw colour and the fade scales it ({torch.X:F3}, {torch.Y:F3}, {torch.Z:F3})");
        // The population gate, against hand-written cases of SetupLightsForThisPositionAndRadius's predicate. The
        // flare node is the stock tile lamp's shape: isdynamic 0, affectdynamic 1, a blue first colour shift.
        var lamp = new LightDef { Dynamic = false, AffectDynamic = true, FlareTextures = new[] { "fxpa_lensflare" },
                                  FlareColors = new[] { new Vector3(0f, 0f, 1f) } };
        Vector3 at = new(10f, 0f, 0f), warm = new(1f, 0.8f, 0.5f);
        check.Expect(Flares(lamp, at, warm, 5f, 1f, 1f, Vector3.Zero, 40f), "a lit flare lamp beside the focus flares");
        check.Expect(!Flares(lamp, at, warm, 0f, 1f, 1f, Vector3.Zero, 40f), "a lamp at radius 0 (a window before Night) does not");
        check.Expect(!Flares(lamp, at, warm, 5f, 0f, 1f, Vector3.Zero, 40f), "nor one at multiplier 0");
        check.Expect(!Flares(lamp, at, Vector3.Zero, 5f, 1f, 1f, Vector3.Zero, 40f), "nor a black one, whatever its colour shift");
        check.Expect(!Flares(lamp, at, warm, 1000f, 1f, 1f, Vector3.Zero, 40f), "nor one reaching 1000, the area pair's range");
        var bakedOnly = new LightDef { Dynamic = false, AffectDynamic = false, FlareTextures = lamp.FlareTextures, FlareColors = lamp.FlareColors };
        check.Expect(!Flares(bakedOnly, at, warm, 5f, 1f, 1f, Vector3.Zero, 40f), "nor one neither dynamic nor affecting dynamics");
        var noShift = new LightDef { AffectDynamic = true, FlareTextures = lamp.FlareTextures };
        check.Expect(!Flares(noShift, at, warm, 5f, 1f, 1f, Vector3.Zero, 40f), "nor one with textures but no colour shifts");
        // reach to the focus: radius 5 x multiplier 1 + max(45, fog end)
        Vector3 far = new(49.9f, 0f, 0f), farther = new(50.1f, 0f, 0f);
        check.Expect(Flares(lamp, far, warm, 5f, 1f, 1f, Vector3.Zero, 40f) && !Flares(lamp, farther, warm, 5f, 1f, 1f, Vector3.Zero, 40f),
                     "the focus reach is the adjusted radius plus 45 under a nearer fog end");
        check.Expect(Flares(lamp, new Vector3(64.9f, 0f, 0f), warm, 5f, 1f, 1f, Vector3.Zero, 60f), "...and plus the fog end past 45");
        // The chain length, against a stock outdoor lamp's authored block: texturenames 4 (lensflare, lensring,
        // lensring, lensflare), flaresizes 5 5 7 2, flarepositions 1 0.5 1.5 0.4, four colour shifts.
        var chain = new LightDef { AffectDynamic = true, FlareRadius = 50f,
                                   FlareTextures = new[] { "fxpa_lensflare", "fxpa_lensring", "fxpa_lensring", "fxpa_lensflare" },
                                   FlareSizes = new[] { 5f, 5f, 7f, 2f },
                                   FlarePositions = new[] { 1f, 0.5f, 1.5f, 0.4f },
                                   FlareColors = new[] { new Vector3(0f, 0f, 1f), new Vector3(-0.2f), new Vector3(-0.1f), new Vector3(-0.7f) } };
        check.Expect(Elements(chain) == 4, $"a four-element lamp stages four quads, not one ({Elements(chain)})");
        var short1 = new LightDef { FlareTextures = chain.FlareTextures, FlareSizes = chain.FlareSizes,
                                    FlarePositions = new[] { 1f }, FlareColors = chain.FlareColors };
        check.Expect(Elements(short1) == 1, "an element no flarepositions entry names is not drawn");
        check.Expect(Elements(noShift) == 0, "nor is one with textures and nothing else");
        // The draw is one instanced range per texture group, so the stage indexes from the group's first
        // element; the fragment stage samples the element's own texture and shifts its depth read to the light.
        string vs = ShaderStages.Source(ShdHaloVs), fs = ShaderStages.Source(ShdHaloFs);
        check.Expect(vs.Contains("(uFirst + gl_InstanceID) * 3"), "the vertex stage indexes from the group's first element");
        check.Expect(fs.Contains("uniform sampler2D uFlare;") && fs.Contains("texture(uFlare, vUV * 0.5 + 0.5)"),
                     "the fragment stage samples the element's authored texture");
        check.Expect(vs.Contains("out vec2 vOcc;") && fs.Contains("+ vOcc * 0.5"),
                     "and reads the scene depth at the light rather than under the element");
        log.Info(check.Ok ? "Halos.SelfCheck: passed" : "Halos.SelfCheck: FAILED -- see above");
    }
}
