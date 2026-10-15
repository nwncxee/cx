using System.Collections.Generic;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// Grass, as a REPLACEMENT class (ADR 0011): the engine's <c>Scene::DoBinBucket</c> is left exactly as it is
/// and simply not called on the passes Alloy draws. GPU-driven -- the CPU culls whole patches, a compute
/// scatter regenerates each visible patch's blades from its faces' seeds -- off the ENGINE's own face data and
/// blade counts, and nothing it draws is ever semi-transparent AND depth-writing at once, as vanilla's is.
/// </summary>
internal static unsafe partial class GrassField
{
    /// <summary>Patches (one per visible bin x grass material) the cache holds, and the open-addressed slot
    /// table that interns them by <c>CAurTriangleList*</c> (power of two, ~2x for a 0.5 load factor).</summary>
    private const int MaxPatches = 32768, PatchHash = 65536;
    /// <summary>Grass FACES resident on the GPU, not blades -- the scatter regenerates every blade from
    /// its face's seed each frame, so the resident cost no longer grows with the density dial or the draw
    /// distance. 48 bytes a face (12MB at the cap); a 32x32 outdoor area is well under 100k faces.</summary>
    private const int MaxFaces = 262_144;
    /// <summary>Floats per resident face: <c>vec4 (a, first blade)</c>, <c>vec4 (b, seed)</c>, <c>vec4 (c, blades)</c>,
    /// the integers as their float bit patterns.</summary>
    /// <summary>Blades one patch may carry. A patch over it has every face's count scaled down (and the period
    /// line says so); under it, a big patch is split across up to <see cref="MaxSplit"/> workgroups.</summary>
    internal const int MaxPatchBlades = 1 << 18;
    /// <summary>Workgroups one patch may be split across, and the blades each is sized for.</summary>
    private const int MaxSplit = 64, SplitBlades = 2048;
    /// <summary>Records the scatter may append per frame: 96MB of stream. Each draw takes a region sized on the
    /// CPU to its exact upper bound this frame, so a region never overflows; only the sum can pass this, and
    /// the period line says when it did.</summary>
    private const int RecordCap = 1 << 21;
    /// <summary>Card materials drawn per frame, one indirect draw each (a card draws with its atlas bound).
    /// The tileset's own plus a server's overrides: a handful in practice.</summary>
    private const int Slots = 32;
    /// <summary>Indirect commands: procedural blades near (0) and far (1), then one per card slot.</summary>
    private const int Cmds = 2 + Slots;
    /// <summary>Procedural blade strip pairs: 7 near (15 vertices), 1 far (a single triangle).</summary>
    private const int NearSegments = 7, FarSegments = 1;
    /// <summary>Floats per drawn record: the card stage's two instance vec4s plus the ground's normal.</summary>
    private const int InstFloats = 12;
    /// <summary>SSBO bindings of the scatter, on shared points: it rebinds all of them before every dispatch, and
    /// the blade draw rebinds <see cref="BindMats"/> itself.</summary>
    private const uint BindFaces = SsboSlots.GrassFaces, BindPatches = SsboSlots.GrassPatches, BindMats = SsboSlots.GrassMats,
                       BindOut = SsboSlots.GrassOut, BindCmd = SsboSlots.GrassCmd;
    /// <summary>vec4s per material in the shared table (<c>cx_gbladebuf.glsl</c>'s <c>CX_GRASS_MATV</c>).</summary>
    private const int MatVec4 = 16;
    /// <summary>Patches baked per frame. Walking into a fresh area makes every visible bin new at once, and
    /// baking them all on one frame is a visible hitch; spread over a few frames it is not. The unbaked
    /// remainder is simply not drawn yet, which for one or two frames reads as grass fading in.</summary>
    private const int BakePerFrame = 48;
    /// <summary>Faces the bake names once per session, with every term that decides a blade count. The two
    /// scene paths share this function, so the lines are comparable between modes line for line.</summary>
    private const int DumpFaces = 6;
    private static int _dumped;
    /// <summary>A face that wants more blades than this is a bad read, not a meadow -- refuse it rather than
    /// scatter a million quads off one triangle.</summary>
    private const int MaxBladesPerFace = 4096;
    /// <summary>One face's blade count: the tileset's authored number scaled by the density dial, truncated,
    /// and capped. The one definition, so the SelfCheck can call it rather than restate the arithmetic.
    /// </summary>
    private static int BladesForFace(int authored, float per) => Math.Min((int)(authored * per), MaxBladesPerFace);
    /// <summary><see cref="SceneDb.HitObjClass"/>'s "effect" value: spell VFX and target reticles, which are
    /// drawn over the grass rather than standing in it.</summary>
    private const byte ModelClassEffect = 1;
    /// <summary>Vertices in the blade template: two quads crossed at 90 degrees, six vertices each. Vanilla
    /// draws ONE flat card per blade, which reads as a paper cutout from the side; a cross costs one extra
    /// quad per blade and is the cheapest thing that makes grass look like grass from any angle.</summary>
    private const int TemplateVerts = 12;
    /// <summary>Grass material slots the pass resolves textures for. The engine's own <c>g_GrassInfo</c> list
    /// is short -- the tileset default is slot 3 and area/server overrides fill the rest -- and the list's
    /// real count still gates every read; this only bounds the per-frame resolve loop.</summary>
    private const int MaxGrassMaterials = 128;

    /// <summary>The blade template's six corners as (side, z, u): side is -1 or +1 across the quad's own
    /// axis, z is 0 at the root and 1 at the tip, and v equals z.</summary>
    private static readonly float[] Corner =
    {
        -1f, 0f, 0f,   1f, 0f, 1f,   1f, 1f, 1f,
        -1f, 0f, 0f,   1f, 1f, 1f,  -1f, 1f, 0f,
    };

    private static ExtenderLog? _log;
    private static ReloadGrassFn _origReload;
    private static AurTextureGetFn _aurGet;
    /// <summary><c>binbucket</c> (the BSP walk's active bins) and <c>g_GrassInfo</c> (the material records)
    /// are both bare list heads at global addresses.</summary>
    private static EngineList _binBucket, _grassInfo;
    private static byte* _grassEnabled;
    private static float* _farDist, _nearRadius;
    /// <summary>The RESOLUTION latch: a grass global, the texture loader or a hook that did not come up, so
    /// this client cannot run the Alloy pass at all. Separate from <see cref="Grass.Enabled"/> on purpose --
    /// <c>/grass enabled 1</c> must never re-enable a pass that could not arm.</summary>
    private static bool _off;
    private static readonly StandDown _down = new("GrassField");

    /// <summary>Baked patches. <see cref="Patch.List"/> is an IDENTITY, never dereferenced outside the frame
    /// it was sighted in <c>binbucket</c> on -- the bins are freed wholesale by <c>ReloadGrass</c>, which is
    /// what <see cref="Forget"/> hangs off.</summary>
    private struct Patch
    {
        public IntPtr List;
        public Vector3 Min, Max;
        /// <summary><c>First</c>/<c>Count</c> are resident FACES; <c>Blades</c> the patch's blade total.</summary>
        public int Mat, First, Count, Blades, Seen;
    }

    private static readonly Patch[] _patches = new Patch[MaxPatches];
    private static readonly int[] _slot = new int[PatchHash];
    private static int _patchCount, _faceCount;
    private static long _bladesResident;
    /// <summary>Lines left for the copy's framebuffer diagnostic (<see cref="Grass.GTint"/> and
    /// <see cref="Grass.GViz"/> re-arm three). GViz: 1 = every blade paints the copy texel behind it (the
    /// scene should show through), 2 = the copy's centre texel, 3 = gl_FragCoord over the copy size as
    /// red/green, 4 = a normalized texture() read.</summary>
    private static int _groundDiag = 3;
    private static int _prepGen = -1, _seen;
    /// <summary>CPU-side face stream. Allocated once, cold: the draw path never allocates.</summary>
    private static GrassFace[]? _faces;
    private static int _uploaded;

    /// <summary>The frame's visible patch table, two uvec4 each (<c>cx_gblade_c.glsl</c>'s <c>uPatch</c>).</summary>
    private static readonly uint[] _visTable = new uint[MaxPatches * 8];
    private static readonly float[] _matTable = new float[MaxGrassMaterials * MatVec4 * 4];
    /// <summary>Draw slot -> the material drawn in it; <see cref="_slotCount"/> are live this frame.</summary>
    private static readonly int[] _slotMat = new int[Slots];
    private static readonly int[] _slotBase = new int[Slots];
    private static int _slotCount, _slotsOver, _clipped;

    private static int _drawnGen = -1, _frame;
    /// <summary>Set from the chat thread by a <see cref="Grass.Density"/> set; consumed by the render thread
    /// at the top of <see cref="Draw"/>. The tables it clears are the render thread's, so the flag is the
    /// handoff -- clearing them where the command runs would race a live bake.</summary>
    private static volatile bool _rebake;

    // period counters
    private static int _frames, _draws, _visible, _culled, _baked, _refused, _disabled;
    private static int _mipsBuilt;
    /// <summary>Records the scatter appended per command this period (off the persistent map), and the blade
    /// indices it was handed -- the difference is the per-blade cull.</summary>
    private static long _drawnNear, _drawnFar, _drawnCards;
    private static int _bladeNear, _bladeFar;
    private static long _candidates;
    private static int _scatters;
    /// <summary>Cap latches, cleared with the tables in <see cref="Forget"/> rather than per period: they
    /// describe the resident field, not the frame, and a cap that stops being reported reads as headroom
    /// that is not there.</summary>
    private static bool _capped, _patchCapped;
    private static int _thinned;
    private static int _samples = 1;

    /// <summary>Drop the resident faces and bake them again at the next pass.</summary>
    internal static void Rebake() => _rebake = true;
    internal static void SamplerDirty() => _samplerDirty = true;
    internal static void GroundDiag() => _groundDiag = 3;
    internal static void DumpMipsNext() => _dumpMips = true;
    internal static void DumpShadowNext() => _dumpShadow = true;

    // ---- install -----------------------------------------------------------------------------

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _binBucket = new EngineList(Native.Sym(SymbolMap.BinBucket));
        _grassInfo = new EngineList(Native.Sym(SymbolMap.GrassInfo));
        _grassEnabled = (byte*)Native.Sym(SymbolMap.GrassEnabled);
        // Not fatal: no gob list just means nothing displaces the grass (the pre-#127-followup look).
        _farDist = (float*)Native.Sym(SymbolMap.GrassFarDistance);
        _nearRadius = (float*)Native.Sym(SymbolMap.GrassNearRadius);
        _aurGet = EngineFn.AurTextureGet;
        if (_binBucket.IsNull || _grassInfo.IsNull || _grassEnabled == null || _farDist == null
            || _nearRadius == null || _aurGet == null || !AurTex.Ready)
        {
            _off = true;
            log.Info("GrassField: grass globals unresolved -- grass stays on the engine's own path");
            return;
        }

        Array.Fill(_slot, -1);
        _origReload = hooks.InstallReloadGrass(&OnReloadGrass);
        if (_origReload == null)
        {
            _off = true;
            log.Info("GrassField: the grass reload hook did not install -- no grass draws");
            return;
        }
        log.Info($"GrassField: armed -- Alloy instanced grass, {Describe()} (/grass tunes it)");
    }

    /// <summary>Every bin and triangle list the cache holds is freed inside here (area load, a grass option
    /// change, a server grass override), so every captured identity is void the moment it returns. Forgetting
    /// AFTER the original is what keeps a recycled allocation from serving the previous area's blades, and
    /// this is the only rebuild path: the area-reload slot's override calls straight back through this hook.</summary>
    [UnmanagedCallersOnly]
    private static void OnReloadGrass(IntPtr scene)
    {
        if (_origReload == null) return;
        _origReload(scene);
        Forget();
    }

    /// <summary>Context loss: the GL names mean nothing once the context is gone, so forget rather than
    /// delete -- and forget the captured world with them, since a rebuilt context re-scatters anyway. Both
    /// EnsureProgram and EnsureBuffers short-circuit on a non-zero name, so leaving these set makes the next
    /// context draw through a dead program.</summary>
    internal static void ContextLost()
    {
        _gl = new GrassGlState();
        _down.Forget();
        Forget();
    }

    internal static void Forget()
    {
        ForgetDb();
        _patchCount = 0; _shadowN = 0;
        _faceCount = 0; _bladesResident = 0;
        _uploaded = 0;
        Array.Fill(_cls, default);
        _drawnGen = -1;
        _capped = false;
        _patchCapped = false;
        _refused = 0;
        Array.Fill(_slot, -1);
    }

    // ---- the pass ----------------------------------------------------------------------------

    /// <summary>The Alloy grass pass, at its <see cref="FrameGraph"/> slot inside the colour bracket.
    /// Bake-then-draw: newly visible patches are scattered and appended to the instance stream (a bounded
    /// number per frame), then every patch sighted this frame is culled, sorted and issued.</summary>
    private static bool Gate()
    {
        if (_off || !Grass.Enabled.V || _down.Failed || _binBucket.IsNull) return false;
        if (*_grassEnabled == 0) return false;
        if (Gl.DrawArraysIndirect == null || Gl.DispatchCompute == null
            || Gl.MemoryBarrier == null || Gl.BindBufferBase == null)
        { _down.Fail(_log, "no indirect-draw or compute entry point", "no grass draws"); return false; }
        if (!EnsureProgram() || !EnsureBuffers()) return false;
        return Grass.Density.V > 0f;
    }

    /// <summary>Once per PassGen: bake, upload, push map. The seen count is kept for a second caller.</summary>
    private static int Prepare(Matrix4x4* vp)
    {
        if (_prepGen == AlloyRenderer.PassGen) return _seen;
        _prepGen = AlloyRenderer.PassGen;
        if (_rebake) { _rebake = false; Forget(); }
        _frame++;
        _frames++;
        _seen = Collect(vp);
        if (_seen == 0) return 0;
        Upload();
        BuildPush();
        return _seen;
    }

    internal static void Draw(Matrix4x4* vp, int* viewport, float near, float far, bool lightingOn)
    {
        // Toggle-off returns WITHOUT stamping the generation: the class goes back to the ENGINE, which is
        // the opposite of the density-0 branch below (nobody draws it, and the stamp is what keeps it that way).
        if (_off || !Grass.Enabled.V || _down.Failed || _binBucket.IsNull) return;
        if (*_grassEnabled == 0) return;      // the player's "Grass Mode" option: off means off, both paths
        if (Gl.DrawArraysIndirect == null || Gl.DispatchCompute == null
            || Gl.MemoryBarrier == null || Gl.BindBufferBase == null)
        { _down.Fail(_log, "no indirect-draw or compute entry point", "no grass draws"); return; }
        if (!EnsureProgram() || !EnsureBuffers()) return;
        // Density 0 means the player asked for no grass. Stamp the generation and stop: the class is still
        // OWNED (so the engine does not draw it back), and Collect never runs -- otherwise every frame would
        // re-attempt a bake budget's worth of patches that scatter nothing.
        if (Grass.Density.V <= 0f) { _drawnGen = AlloyRenderer.PassGen; return; }

        int n = Prepare(vp);
        if (n == 0) { _drawnGen = AlloyRenderer.PassGen; return; }
        Issue(n, vp, viewport, near, far, lightingOn);
        // Stamped even on a frame that drew nothing visible: the Alloy pass OWNS the class this pass, and a
        // patch culled by distance must not come back as an engine draw.
        _drawnGen = AlloyRenderer.PassGen;
    }

    /// <summary>Walk this frame's bins -- Alloy's own set, culled out of the scene's bin grid
    /// (<see cref="OwnedBins"/>), or the engine's <c>binbucket</c> when that stands down or the knob asks --
    /// baking whatever is new and marking everything sighted. A bin that stops appearing has gone out of view
    /// or been freed; either way it simply stops being drawn, and no pointer of it is ever touched again.</summary>
    private static int Collect(Matrix4x4* vp)
    {
        // The database's own patches: Scene::InitBins/AddFacesToBins are poisoned, so there are no engine bins to
        // walk -- the source is the part table.
        int budget = BakePerFrame;
        return CollectDb(AlloyRenderer._dbPassScene, vp, ref budget);
    }

    /// <summary>Interned slot of a triangle list, or -1. Open addressing on the pointer; the table is sized
    /// at 2x the patch cap so a probe never walks far.</summary>
    private static int Find(IntPtr list)
    {
        uint h = Hash((uint)((long)list >> 4) ^ (uint)((long)list >> 36)) & (PatchHash - 1);
        for (int probe = 0; probe < PatchHash; probe++)
        {
            int s = _slot[(h + probe) & (PatchHash - 1)];
            if (s < 0) return -1;
            if (_patches[s].List == list) return s;
        }
        return -1;
    }

    private static void Intern(IntPtr list, int p)
    {
        uint h = Hash((uint)((long)list >> 4) ^ (uint)((long)list >> 36)) & (PatchHash - 1);
        for (int probe = 0; probe < PatchHash; probe++)
        {
            int i = (int)((h + probe) & (PatchHash - 1));
            if (_slot[i] < 0) { _slot[i] = p; return; }
        }
    }

    /// <summary>Intern one triangle list as a patch of resident FACES: the face vertices and the per-face
    /// blade counts are the ENGINE's, and the scatter regenerates each blade from its face's seed every frame
    /// -- deterministically, so a rebuild puts every blade back rather than reshuffling the meadow. A list
    /// this cannot scatter is interned as an EMPTY patch, since a refusal is a stable property of the list.</summary>
    private static int Bake(IntPtr list)
    {
        var tl = new CAurTriangleList(list);
        GrassTriRec* verts = (GrassTriRec*)tl.Verts;
        int* blades = (int*)tl.Blades;
        int faces = tl.VertFloats / (sizeof(GrassTriRec) / sizeof(float));
        // The list's own two arrays have to agree before either is read: the blade counts are indexed by face.
        if (verts != null && blades != null && tl.BladeCount < faces) { verts = null; }
        return BakeFaces(list, verts, blades, faces, tl.Mat);
    }

    /// <summary>Intern and scatter one patch from world-space faces and their per-face blade counts, whatever
    /// produced them -- an engine <c>CAurTriangleList</c> or the scene database (<see cref="BakeDb"/>).
    /// <paramref name="key"/> is the patch's identity in the intern table.</summary>
    private static int BakeFaces(IntPtr key, GrassTriRec* verts, int* blades, int faces, int mat)
    {
        // The one refusal that CANNOT be interned: there is no slot to intern it in. Counted instead, and
        // loud in the period line -- past this point every list retries, which is the state to see.
        if (_patchCount >= MaxPatches || _faces == null) { _patchCapped = true; return -1; }
        int p = _patchCount++;
        _patches[p] = new Patch { List = key, Seen = _frame };   // Count 0: refused until proven otherwise
        Intern(key, p);
        if (verts == null || blades == null || faces <= 0) { _refused++; return p; }
        CAurGrassInfo info = GrassInfo(mat);
        if (info.IsNull || mat >= MaxGrassMaterials) { _refused++; return p; }
        float height = info.Height;
        if (!(height > 0f)) { _refused++; return p; }

        // procedural blades are thin: they take the blade multiplier, and the scatter thins a card cell back out
        float per = Grass.Density.V * BladeMul;
        long want = 0;
        for (int f = 0; f < faces; f++) want += BladesForFace(blades[f], per);
        // a patch past the cap keeps its shape at a lower density
        float thin = want > MaxPatchBlades ? (float)MaxPatchBlades / want : 1f;
        if (thin < 1f) _thinned++;
        if (_dumped < DumpFaces)
            for (int f = 0; f < faces && _dumped < DumpFaces; f++)
            {
                ref GrassTriRec v = ref verts[f];
                Vector3 a0 = v.A, b0 = v.B, c0 = v.C;
                _dumped++;
                _log?.Info($"GrassField: [face] mat={mat} density={info.Density:F3} height={height:F2} "
                           + $"area={Vector3.Cross(b0 - a0, c0 - a0).Length() * 0.5f:F3} blades={blades[f]} "
                           + $"per={Grass.Density.V * BladeMul:F3} src=db");
            }
        int first = _faceCount, total = 0;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int f = 0; f < faces; f++)
        {
            int n = (int)(BladesForFace(blades[f], per) * thin);
            if (n <= 0) continue;
            if (_faceCount >= MaxFaces) { _capped = true; break; }
            ref GrassTriRec v = ref verts[f];
            Vector3 a = v.A, b = v.B, c = v.C;
            uint seed = Hash(BitConverter.SingleToUInt32Bits(a.X)
                           ^ (BitConverter.SingleToUInt32Bits(b.Y) * 2654435761u)
                           ^ (BitConverter.SingleToUInt32Bits(c.Z) * 40503u));
            _faces[_faceCount] = new GrassFace { A = a, FirstBlade = total, B = b, Seed = seed, C = c, BladeCount = n };
            _faceCount++;
            total += n;
            min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
            max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
        }
        if (total == 0) { _refused++; return p; }
        // Padded by what a blade occupies around its root: its lean and far widening each way, its full
        // height (and the ladder's growth) up.
        min -= new Vector3(height, height, 0f);
        max += new Vector3(height, height, height * 1.5f);

        _patches[p] = new Patch { List = key, Min = min, Max = max, Mat = mat, First = first, Count = _faceCount - first,
                                  Blades = total, Seen = _frame };
        _bladesResident += total;
        _baked++;
        return p;
    }

    private static CAurGrassInfo GrassInfo(int mat)
    {
        var recs = new CAurGrassInfo(_grassInfo.Items);
        if (recs.IsNull || mat < 0 || mat >= _grassInfo.Count) return default;
        return recs[mat];
    }

    /// <summary>Push whatever the bake appended. One <c>BufferSubData</c> over the new tail, never the whole
    /// stream -- the resident faces do not change once interned.</summary>
    private static void Upload()
    {
        if (_faces == null || _faceCount <= _uploaded) return;
        int bytes = (_faceCount - _uploaded) * sizeof(GrassFace);
        // The first write after a Forget (area load, /grass density) starts at byte 0, over a range the GPU
        // may still be reading from last frame -- orphan it rather than make the driver serialise, at exactly
        // the two moments this class exists to keep smooth. Every later write is an append past the read range.
        if (_uploaded == 0)
            Gl.NamedBufferData(_gl.FaceBuf, (IntPtr)((long)MaxFaces * sizeof(GrassFace)), null, Gl.DYNAMIC_DRAW);
        fixed (GrassFace* src = _faces.AsSpan(_uploaded))
            Gl.NamedBufferSubData(_gl.FaceBuf, (IntPtr)(_uploaded * sizeof(GrassFace)), (IntPtr)bytes, src);
        _uploaded = _faceCount;
    }

    /// <summary>Samples of the surface this pass draws into, without a per-frame <c>glGet</c>.
    /// Every target Alloy binds -- the post chain's HDR target, the present texture -- is built from plain
    /// 2D attachments, so only framebuffer 0 can be multisampled, and its count cannot change under a live
    /// context.</summary>
    private static int TargetSamples()
    {
        if (PostChain.Armed || PresentTarget.Fbo46 != 0) return 1;
        if (_gl.Fb0Samples == 0)
        {
            int s = 0;
            Gl.GetIntegerv(Gl.SAMPLES, &s);
            _gl.Fb0Samples = s < 1 ? 1 : s;
        }
        return _gl.Fb0Samples;
    }

    /// <summary>Cull the patches, scatter their blades on the GPU, and draw what survived: procedural blades
    /// in two indirect strips (near full-detail, far one triangle), and one indirect draw of the engine's own
    /// alpha-cut cards per material that has card cells. The CPU's share is the patch cull and a table of a
    /// few thousand entries; the blade count never reaches it.</summary>
    private static void Issue(int seen, Matrix4x4* vp, int* viewport, float near, float far, bool lightingOn)
    {
        Span<Vector4> planes = stackalloc Vector4[6];
        Frustum(in *vp, planes);
        Vector3 eye = AlloyRenderer.EyePos;
        float farDist = FarDist();
        // #355: the dither ramp is the CUT-OFF's edge, not the whole range -- the ladder thins the field by
        // count from the lod radius outward, and a blade the ramp would have dithered is simply not drawn instead.
        float nearDist = farDist * 0.85f;
        if (farDist <= nearDist) farDist = nearDist + 1f;
        // A patch wholly past the fog's opaque distance is a fully-fogged pixel the ground already is; the
        // engine's grass option runs to 173u and the fog end is commonly 125u, so it was never cut.
        float opaque = CullPass.FogCull ? SceneFog.OpaqueDistance() : float.PositiveInfinity;
        Vector3 fogOrigin = SceneFog.Origin(out _);

        // Every grass texture resolved BEFORE anything else: AurTextureGet's create path (first sight of a
        // resref) can touch GL and rebind buffers, and it must not do that between our binds. Re-fetched
        // every frame and never cached (#33) -- the engine frees its textures on area transitions, and a
        // stored CAurTexture* dangles into freed heap the moment it is bound.
        // The IN-USE byte comes off the same walk. It is the engine's own AurGrassMaterialInUse @0x4b4730,
        // and it is the ONLY gate on a grass material being drawn: RemoveGrassOverride (server minor 0x16)
        // clears it and forces a reload, but a reload rebuilds the bins for that material anyway -- the
        // engine's DoBinBucket simply refuses to draw them. Ignoring it renders grass a server just removed.
        int* tids = stackalloc int[MaxGrassMaterials];
        bool* live = stackalloc bool[MaxGrassMaterials];
        ResolveTextures(tids, live);
        if (_dumpMips) { _dumpMips = false; DumpMips(tids, live); }
        EnsureSamplers();
        int samples = TargetSamples();
        _samples = samples;
        bool msaa = samples > 1;
        int* bits = stackalloc int[MaxGrassMaterials];
        bool* cards = stackalloc bool[MaxGrassMaterials];
        ClassifyAll(tids, live, bits, cards);

        // each draw's upper bound this frame: the blades its visible patches can keep at their nearest point.
        // A patch straddling the blade detail radius counts toward both blade draws.
        long* bound = stackalloc long[MaxGrassMaterials];
        for (int m = 0; m < MaxGrassMaterials; m++) bound[m] = 0;
        long nearB = 0, farB = 0, most = 0;
        float bladeLod = Grass.BladeLod.V;
        int n = 0;
        for (int p = 0; p < _patchCount; p++)
        {
            ref Patch q = ref _patches[p];
            if (q.Seen != _frame || q.Count <= 0) continue;
            if (q.Mat < 0 || q.Mat >= MaxGrassMaterials || !live[q.Mat]) { _disabled++; continue; }
            float d = BoxDistance(q.Min, q.Max, eye);
            if (d > farDist || BoxDistance(q.Min, q.Max, fogOrigin) > opaque || !Visible(q.Min, q.Max, planes)) { _culled++; continue; }
            // keep(NEAREST point of the patch), so it is never smaller than the keep any blade of the patch
            // computes for itself: the scatter rejects on it before the blade's own test closes the gap
            float keep = Keep(d);
            int o = n * 8;
            _visTable[o] = (uint)q.First; _visTable[o + 1] = (uint)q.Count; _visTable[o + 2] = (uint)q.Blades;
            _visTable[o + 3] = BitConverter.SingleToUInt32Bits(keep); _visTable[o + 4] = (uint)q.Mat;
            _visTable[o + 5] = 0; _visTable[o + 6] = 0; _visTable[o + 7] = 0;
            long upto = (long)MathF.Ceiling(q.Blades * MathF.Min(keep * 1.2f, 1f));
            if (bits[q.Mat] != 0)
            {
                if (d < bladeLod) nearB += upto;
                if (BoxFar(q.Min, q.Max, eye) >= bladeLod) farB += upto;
            }
            if (cards[q.Mat]) bound[q.Mat] += upto;
            _candidates += upto;
            most = Math.Max(most, q.Blades);
            n++;
        }
        if (n == 0) return;
        _visible += n;
        _shadowN = n; _shadowEye = eye; _shadowNear = nearDist; _shadowFar = farDist;
        int matN = WriteMaterials(live, bound, bits, nearB, farB);

        float* pw = stackalloc float[3] { _pushOriginX, _pushOriginY, _pushPerUnit / PushRes };
        AlloyRenderer.GpuBegin(AlloyRenderer.GpuTerm.GrassDepth);
        Scatter(n, (int)Math.Clamp((most + SplitBlades - 1) / SplitBlades, 1, MaxSplit), planes, &eye, nearDist, farDist, matN, pw);
        Gl.BindVertexArray(_gl.Vao);
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, _gl.CmdBuf);
        bool prepass = _slotCount > 0 && Grass.Prepass.V && _gl.ProgDepth != 0;
        bool soft = Grass.SoftEdge.V && prepass && !msaa;
        if (prepass)
        {
            Gl.UseProgram(_gl.ProgDepth);
            Gl.Uniform1i(_gl.LocD.SoftEdge, soft ? 1 : 0);
            SharedUniforms(in _gl.LocD, in _gl.FeatLocD, vp, &eye, nearDist, farDist, msaa, pw, viewport, vlit: false);
            new PassState(depth: true, depthWrite: true, cull: false).Apply();
            Gl.ColorMask(0, 0, 0, 0);
            Gl.DepthFunc(Gl.LESS);
            if (msaa) Gl.Enable(Gl.SAMPLE_ALPHA_TO_COVERAGE);
            DrawSlots(tids, in _gl.LocD, depth: true);
            if (msaa) Gl.Disable(Gl.SAMPLE_ALPHA_TO_COVERAGE);
            Gl.ColorMask(1, 1, 1, 1);
            Gl.DepthFunc(Gl.LEQUAL);
        }
        AlloyRenderer.GpuEnd(AlloyRenderer.GpuTerm.GrassDepth);

        // The frame as it stands before the grass, for the root blend and the blades' fade into the ground.
        // One colour-only blit (the shader reads no depth); the water pass takes its own, with depth, later.
        bool blades = _bladeNear + _bladeFar > 0;
        bool ground = (Grass.GTint.V > 0f || blades) && FrameSnapshot.Copy(viewport, depth: false);
        if (ground && _groundDiag > 0) GroundDiagBefore(viewport);
        if (lightingOn) AlloyLights.BindForDraw();
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.GrassGround);
        Gl.BindTexture(Gl.TEXTURE_2D, FrameSnapshot.ColorTex);
        Gl.ActiveTexture(Gl.TEXTURE0);
        // CX-51: a blade reflects the probe as the ground around it does
        AlloyRenderer.BindProbe();
        AlloyRenderer.GpuBegin(AlloyRenderer.GpuTerm.Grass);
        // Procedural blades: opaque, depth-writing, two-sided, and no discard -- early-Z rejects the field's own
        // overdraw with no prepass.
        if (blades)
        {
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindMats, _gl.MatBuf);
            ColourUniforms(_gl.ProgBlade, in _gl.LocB, in _gl.FeatLocB, vp, &eye, nearDist, farDist, pw, viewport, near, far, lightingOn, ground);
            new PassState(depth: true, depthWrite: true, cull: false).Apply();
            DrawBlades(in _gl.LocB);
        }
        if (_slotCount > 0)
        {
            ColourUniforms(_gl.Prog, in _gl.Loc, in _gl.FeatLoc, vp, &eye, nearDist, farDist, pw, viewport, near, far, lightingOn, ground);
            Gl.Uniform1i(_gl.Loc.Msaa, msaa ? 1 : 0);
            Gl.Uniform1i(_gl.Loc.SoftEdge, soft ? 1 : 0);
            Gl.Uniform1i(_gl.Loc.GroundViz, Grass.GViz.V);
            // The class's whole point, in one declaration: blending OFF (or the soft fringe alone), the coverage
            // decision made by the fragment stage's discards, depth from the prepass when it ran. Alpha-to-coverage
            // only where the target has samples to cover. #292: cull OFF, a card is seen from both faces.
            new PassState(depth: true, depthWrite: !prepass, cull: false, blend: soft,
                          blendSrc: Gl.SRC_ALPHA, blendDst: Gl.ONE_MINUS_SRC_ALPHA).Apply();
            if (msaa) Gl.Enable(Gl.SAMPLE_ALPHA_TO_COVERAGE);
            DrawSlots(tids, in _gl.Loc, depth: false);
            if (msaa) Gl.Disable(Gl.SAMPLE_ALPHA_TO_COVERAGE);
        }
        AlloyRenderer.GpuEnd(AlloyRenderer.GpuTerm.Grass);
        // CX-48: what was drawn, for the velocity sweep that re-issues it later in the frame.
        NoteIssued(eye, nearDist, farDist, ground);
        if (ground && _groundDiag > 0) GroundDiagAfter(viewport);
        // the drawn count, for Stats: copied GPU-side into a persistent map, read a frame or more late, never waited on
        if (_gl.Stat != null)
        {
            _drawnNear += _gl.Stat[1]; _drawnFar += _gl.Stat[5];
            for (int s = 0; s < _slotCount; s++) _drawnCards += _gl.Stat[(2 + s) * 4 + 1];
            Gl.CopyNamedBufferSubData(_gl.CmdBuf, _gl.StatBuf, IntPtr.Zero, IntPtr.Zero, (IntPtr)(Cmds * 4 * sizeof(uint)));
        }
        if (_gl.SampMip != 0) Gl.BindSampler(0, 0);
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, 0);
        Gl.BindVertexArray(0);
    }

    /// <summary>The procedural blade multiplier the bake applies: 1 under the texture style.</summary>
    private static float BladeMul => Grass.Style.V == Grass.StyleBlades ? Grass.BladeDensity.V : 1f;

    /// <summary>Does this material draw any card? Any drawn cell (cell 0 is the engine's fallback) that is not
    /// a BLADE cell. Asked once per material per frame, from <see cref="ClassifyAll"/>.</summary>
    private static bool CardCells(CAurGrassInfo info, int bits)
    {
        if (info.IsNull) return false;
        Span<float> prob = info.Prob;
        int used = 1;
        for (int k = 0; k < 4; k++) if (prob[k] > 0f) used |= 1 << k;
        return (used & ~bits) != 0;
    }

    /// <summary>Lay the frame's regions out -- near blades, far blades, then a card slot per material with card
    /// cells and visible blades, each sized to its upper bound -- and fill the material table. Returns one
    /// past the highest material written.</summary>
    private static int WriteMaterials(bool* live, long* bound, int* bits, long nearB, long farB)
    {
        long used = 0;
        bool clipped = false;
        long Take(long want) { long c = Math.Min(want, RecordCap - used); clipped |= c < want; used += c; return c; }
        _bladeNear = (int)Take(nearB);
        _bladeFar = (int)Take(farB);
        _slotCount = 0;
        int top = 0;
        for (int m = 0; m < MaxGrassMaterials; m++)
        {
            int o = m * MatVec4 * 4;
            _matTable[o + 5] = -1f;
            CAurGrassInfo info = live[m] ? GrassInfo(m) : default;
            if (info.IsNull) continue;
            top = m + 1;
            Span<float> prob = info.Prob;
            for (int k = 0; k < 4; k++) _matTable[o + k] = prob[k];
            _matTable[o + 4] = info.Height;
            _matTable[o + 7] = bits[m];
            Vector3 dif = info.Diffuse, amb = info.Ambient;
            _matTable[o + 8] = dif.X; _matTable[o + 9] = dif.Y; _matTable[o + 10] = dif.Z;
            _matTable[o + 12] = amb.X; _matTable[o + 13] = amb.Y; _matTable[o + 14] = amb.Z;
            for (int r = 0; r < 12; r++)
            {
                Vector3 c = _ramp[m * 12 + r];
                int q = o + 16 + r * 4;
                _matTable[q] = c.X; _matTable[q + 1] = c.Y; _matTable[q + 2] = c.Z;
                if (r % 3 == 0) _matTable[q + 3] = _bladeLen[m * 4 + r / 3];
            }
            if (bound[m] <= 0) continue;
            if (_slotCount >= Slots) { _slotsOver++; continue; }
            long cap = Take(bound[m]);
            if (cap <= 0) continue;
            int slot = _slotCount++;
            _slotMat[slot] = m;
            _slotBase[slot] = (int)(used - cap);
            _matTable[o + 5] = slot;
            _matTable[o + 6] = cap;
        }
        if (clipped) _clipped++;
        return top;
    }

    /// <summary>Upload the frame's tables, reset the commands, and run the scatter: one workgroup per visible
    /// patch (times <paramref name="split"/> for the biggest), then the clamp. The barrier covers every way its
    /// output is read: as commands, as instance attributes, and by the stat copy.</summary>
    private static void Scatter(int n, int split, ReadOnlySpan<Vector4> planes, Vector3* eye, float fadeFrom, float farDist, int matN, float* pw)
    {
        fixed (uint* t = _visTable)
        {
            Gl.NamedBufferData(_gl.PatchBuf, (IntPtr)(n * 8 * sizeof(uint)), t, Gl.DYNAMIC_DRAW);
        }
        fixed (float* m = _matTable)
        {
            Gl.NamedBufferData(_gl.MatBuf, (IntPtr)(Math.Max(matN, 1) * MatVec4 * 4 * sizeof(float)), m, Gl.DYNAMIC_DRAW);
        }
        uint* cmd = stackalloc uint[Cmds * 4];
        for (int c = 0; c < Cmds; c++)
        {
            cmd[c * 4] = (uint)(c == 0 ? 2 * NearSegments + 1 : c == 1 ? 2 * FarSegments + 1 : TemplateVerts);
            cmd[c * 4 + 1] = 0; cmd[c * 4 + 2] = 0;
            cmd[c * 4 + 3] = (uint)(c == 0 ? 0 : c == 1 ? _bladeNear : c - 2 < _slotCount ? _slotBase[c - 2] : 0);
        }
        Gl.NamedBufferSubData(_gl.CmdBuf, IntPtr.Zero, (IntPtr)(Cmds * 4 * sizeof(uint)), cmd);

        Gl.UseProgram(_gl.ProgScatter);
        fixed (Vector4* pl = planes) Gl.Uniform4fv(_gl.LocS.Planes, 6, (float*)pl);
        Gl.Uniform3fv(_gl.LocS.Eye, 1, (float*)eye);
        Gl.Uniform4f(_gl.LocS.Lod, Grass.Lod.V, Grass.LodMin.V, Grass.LodWidth.V, Grass.LodHeight.V);
        Gl.Uniform2f(_gl.LocS.Fade, fadeFrom, farDist);
        Gl.Uniform1f(_gl.LocS.SnowThin, SnowThinShare(Weather.Snow, Weather.CurrentKind == Weather.Kind.Snow ? Weather.Density : 0f));
        Gl.Uniform1f(_gl.LocS.Near, Grass.BladeLod.V);
        Gl.Uniform2f(_gl.LocS.Clump, ClumpCell, Grass.Clump.V);
        Gl.Uniform1f(_gl.LocS.CardKeep, 1f / MathF.Max(BladeMul, 1f));
        Gl.Uniform2i(_gl.LocS.BladeCap, _bladeNear, _bladeFar);
        Gl.Uniform1i(_gl.LocS.Clamp, 0);
        Gl.Uniform1i(_gl.LocS.PushTex, (int)PushUnit);
        Gl.Uniform3fv(_gl.LocS.PushWindow, 1, pw);
        Gl.ActiveTexture(Gl.TEXTURE0 + PushUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, _gl.PushTex);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindFaces, _gl.FaceBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindPatches, _gl.PatchBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindMats, _gl.MatBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindOut, _gl.OutBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCmd, _gl.CmdBuf);
        Gl.DispatchCompute((uint)n, (uint)split, 1);
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT);
        Gl.Uniform1i(_gl.LocS.Clamp, Math.Max(matN, 1));
        Gl.DispatchCompute(1, 1, 1);
        Gl.MemoryBarrier(Gl.COMMAND_BARRIER_BIT | Gl.VERTEX_ATTRIB_ARRAY_BARRIER_BIT | Gl.BUFFER_UPDATE_BARRIER_BIT);
        _scatters++;
    }

    /// <summary>World size of a clump cell: procedural blades inside one share a lean and a height.</summary>
    private const float ClumpCell = 0.6f;

    /// <summary>Distance from a point to the FARTHEST point of the box.</summary>
    internal static float BoxFar(Vector3 min, Vector3 max, Vector3 p)
    {
        float dx = MathF.Max(p.X - min.X, max.X - p.X), dy = MathF.Max(p.Y - min.Y, max.Y - p.Y), dz = MathF.Max(p.Z - min.Z, max.Z - p.Z);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary><c>GL_DRAW_INDIRECT_BUFFER_BINDING</c>.</summary>
    private const uint DrawIndirectBinding = 0x8F43;   // not-an-offset: GL enum

    /// <summary>Everything a colour program reads beyond the shared terms: the light rig, the sun cascades,
    /// the local cubes, the ground copy. Leaves <paramref name="prog"/> bound.</summary>
    private static void ColourUniforms(uint prog, in Loc loc, in AlloyRenderer.FeatLoc feat, Matrix4x4* vp, Vector3* eye,
                                       float nearDist, float farDist, float* pw, int* viewport, float near, float far,
                                       bool lightingOn, bool ground)
    {
        Gl.UseProgram(prog);
        SharedUniforms(in loc, in feat, vp, eye, nearDist, farDist, false, pw, viewport, vlit: Grass.VLight.V && lightingOn);
        Gl.Uniform1f(loc.Shade, Grass.Shade.V);
        Gl.Uniform1i(loc.Viz, AlloyRenderer._lightViz);
        Gl.Uniform1i(loc.Lit, lightingOn ? 1 : 0);
        Gl.Uniform1f(loc.RootAo, Grass.RootAo.V);
        Gl.Uniform1f(loc.GroundTint, Grass.GTint.V);
        Gl.Uniform1i(loc.GroundOn, ground ? 1 : 0);
        Gl.Uniform4f(loc.Look, Grass.Backlight.V, Grass.Sheen.V, Weather.WetShown, Weather.WetDark.V);
        Weather.UploadMap(loc.WxMapXf, loc.WxMapP, false);
        Weather.UploadGrassEdge(loc.CoverVp, loc.CoverLayer, loc.WxEdge, loc.WxCache);
        SkyView.UploadLit(loc.SkyVp, loc.SkyTerm, capturing: false);
        AlloyRenderer.BindPass(loc.CubeCount, viewport, near, far);
    }

    private static void GroundDiagBefore(int* viewport)
    {
        _groundDiag--;
        int fb; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &fb);
        int tw = 0, th = 0, fmt = 0;
        Gl.GetTextureLevelParameteriv(FrameSnapshot.ColorTex, 0, Gl.TEXTURE_WIDTH, &tw);
        Gl.GetTextureLevelParameteriv(FrameSnapshot.ColorTex, 0, Gl.TEXTURE_HEIGHT, &th);
        Gl.GetTextureLevelParameteriv(FrameSnapshot.ColorTex, 0, Gl.TEXTURE_INTERNAL_FORMAT, &fmt);
        _log?.Info($"[grassground] copy taken from fbo {fb} (post hdr fbo {PostChain.Fbo}) at {viewport[2]}x{viewport[3]}, "
                 + $"copy tex {FrameSnapshot.ColorTex} {tw}x{th} fmt 0x{fmt:x} on unit {TextureSlots.GrassGround} (loc {_gl.Loc.Ground}), "
                 + $"copies so far {FrameSnapshot.Copies}, pass gen {AlloyRenderer.PassGen}, capturing {AlloyRenderer.Capturing}");
        FrameSnapshot.Compare((uint)fb, viewport[2], viewport[3], "before draw");
    }

    private static void GroundDiagAfter(int* viewport)
    {
        int bound = 0, samp = 0, uval = -1, act = 0;
        Gl.GetIntegerv(0x84E0 /*ACTIVE_TEXTURE*/, &act);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.GrassGround);
        Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &bound);
        Gl.GetIntegerv(0x8919 /*SAMPLER_BINDING*/, &samp);
        Gl.ActiveTexture((uint)act);
        if (Gl.GetUniformiv != null) Gl.GetUniformiv(_gl.Prog, _gl.Loc.Ground, &uval);
        _log?.Info($"[grassground] after draw: unit {TextureSlots.GrassGround} holds tex {bound} sampler obj {samp}, uGround = {uval}, viz {Grass.GViz.V}, active unit was 0x{act:x}");
        int fb2; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &fb2);
        FrameSnapshot.Compare((uint)fb2, viewport[2], viewport[3], "after draw");
    }

    /// <summary>Share of the blades gone by rank: the cover's thinning, or while snow falls the
    /// <see cref="Grass.SnowFall"/> cut scaled by the falling density, whichever takes more.</summary>
    internal static float SnowThinShare(float cover, float falling)
        => MathF.Max(cover * Grass.SnowThin.V, falling * (1f - Grass.SnowFall.V));

    /// <summary>The uniforms both grass programs read: camera, ladder, fade, wind, push map, feature terms.</summary>
    private static void SharedUniforms(in Loc loc, in AlloyRenderer.FeatLoc feat, Matrix4x4* vp, Vector3* eye,
                                       float nearDist, float farDist, bool msaa, float* pw, int* viewport, bool vlit)
    {
        Gl.UniformMatrix4fv(loc.Vp, 1, 0, (float*)vp);
        Gl.Uniform1f(loc.Tuft, Grass.Tuft.V);
        Gl.Uniform1f(loc.Width, Grass.Width.V);
        Gl.Uniform1i(loc.VLit, vlit ? 1 : 0);   // the depth program never lights: its vertex stage skips the rig
        Gl.Uniform1i(loc.Cast, 0);
        Gl.Uniform1f(loc.CastWidth, Grass.CastWidth.V);
        // the snow on the ground (Weather's integrator) thins the field by rank and winters the survivors
        Gl.Uniform2f(loc.Snow, SnowThinShare(Weather.Snow, Weather.CurrentKind == Weather.Kind.Snow ? Weather.Density : 0f), Weather.Snow * Grass.SnowLook.V);
        Gl.Uniform3fv(loc.Eye, 1, (float*)eye);
        Gl.Uniform2f(loc.Fade, nearDist, farDist);
        Gl.Uniform1i(loc.Frame, PostChain.TaaOn ? AlloyRenderer.FrameSeq & 63 : 0);
        Gl.Uniform4f(loc.Lod, Grass.Lod.V, Grass.LodMin.V, Grass.LodWidth.V, Grass.LodHeight.V);
        Gl.Uniform1i(loc.Tex, 0);
        Gl.Uniform1f(loc.WindGain, Grass.Wind.V);
        Gl.Uniform1f(loc.WindSpeed, Grass.WindSpeed.V);
        Gl.Uniform1i(loc.Msaa, msaa ? 1 : 0);
        Gl.Uniform1f(loc.Edge, MathF.Max(Grass.Edge.V, 0.01f));
        Gl.Uniform1i(loc.PushTex, (int)PushUnit);
        Gl.Uniform1f(loc.PushGain, _gl.PushTex == 0 ? 0f : Grass.Push.V);
        Gl.Uniform3fv(loc.PushWindow, 1, pw);
        AlloyRenderer.UploadFeatureTerms(feat, viewport);
    }

    /// <summary>The two procedural blade draws: near strips, then far single triangles. The program is bound.</summary>
    private static void DrawBlades(in Loc loc)
    {
        Gl.Uniform1i(loc.Segments, NearSegments);
        if (_bladeNear > 0) Gl.DrawArraysIndirect(Gl.TRIANGLE_STRIP, (void*)0);
        Gl.Uniform1i(loc.Segments, FarSegments);
        if (_bladeFar > 0) Gl.DrawArraysIndirect(Gl.TRIANGLE_STRIP, (void*)(4 * sizeof(uint)));
        _draws += 2;
    }

    /// <summary>Every live slot's indirect draw: its material's atlas bound, and in the colour pass its
    /// colours. The depth pass binds the atlas too -- its stage reads the alpha.</summary>
    private static void DrawSlots(int* tids, in Loc loc, bool depth)
    {
        for (int slot = 0; slot < _slotCount; slot++) DrawSlot(slot, tids, in loc, depth);
    }

    private static void DrawSlot(int slot, int* tids, in Loc loc, bool depth)
    {
        int mat = _slotMat[slot];
        CAurGrassInfo info = GrassInfo(mat);
        int tid = tids[mat];
        if (info.IsNull || tid == 0) return;   // not uploaded yet: skip the material, not the pass
        if (!depth)
        {
            Vector3 dif = info.Diffuse, amb = info.Ambient;
            Gl.Uniform3fv(loc.MatDif, 1, (float*)&dif);
            Gl.Uniform3fv(loc.MatAmb, 1, (float*)&amb);
        }
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)tid);
        if (_gl.SampMip != 0) Gl.BindSampler(0, HasMips(tid) ? _gl.SampMip : _gl.SampFlat);
        Gl.DrawArraysIndirect(Gl.TRIANGLES, (void*)((2 + slot) * 4 * sizeof(uint)));
        _draws++;
    }

    // ---- the distance ladder (#355) ----------------------------------------------------------

    /// <summary>The cut-off radius: <c>/grass far</c> when set, else the engine's own option, but never under
    /// <see cref="MinFar"/>. The option defaults to 30u (900 squared), sized for vanilla's per-blade alpha
    /// fade; under the ladder a far blade is nearly free, and a field that thins from 25u and is gone at 30u
    /// reads as the ground darkening a few steps ahead of the player.</summary>
    private static float FarDist() => Grass.Far.V > 0f ? Grass.Far.V : MathF.Max(MathF.Sqrt(MathF.Max(*_farDist, 0f)), MinFar);
    private const float MinFar = 90f;

    /// <summary>The fraction of a patch's blades drawn at distance <paramref name="d"/>: all of them inside
    /// <see cref="Grass.Lod"/>, then <c>(lod/d)^2</c> -- the screen area a patch covers falls with the square
    /// of its distance, so this holds the blades PER PIXEL constant -- floored at <see cref="Grass.LodMin"/>.
    /// The vertex stage's <c>CxGrassKeep</c> is this function character for character.</summary>
    internal static float Keep(float d)
    {
        if (d <= Grass.Lod.V) return 1f;
        float r = Grass.Lod.V / d;
        return MathF.Max(Grass.LodMin.V, r * r);
    }

    // ---- culling maths -----------------------------------------------------------------------

    /// <summary>The six frustum planes of a row-vector <c>clip = v * VP</c> matrix (the convention every owned
    /// stage uses): each plane is a COLUMN of VP added to or subtracted from the w column.</summary>
    internal static void Frustum(in Matrix4x4 m, Span<Vector4> planes)
    {
        Vector4 w = new(m.M14, m.M24, m.M34, m.M44);
        for (int i = 0; i < 6; i++)
        {
            Vector4 col = (i >> 1) switch
            {
                0 => new Vector4(m.M11, m.M21, m.M31, m.M41),
                1 => new Vector4(m.M12, m.M22, m.M32, m.M42),
                _ => new Vector4(m.M13, m.M23, m.M33, m.M43),
            };
            planes[i] = (i & 1) == 0 ? w + col : w - col;
        }
    }

    /// <summary>Is the AABB on the inside of every plane? The "positive vertex" test: only the corner furthest
    /// along each plane's normal can save the box, so one dot product per plane decides it.</summary>
    internal static bool Visible(Vector3 min, Vector3 max, ReadOnlySpan<Vector4> planes)
    {
        for (int i = 0; i < 6; i++)
        {
            float nx = planes[i].X, ny = planes[i].Y, nz = planes[i].Z, d = planes[i].W;
            float px = nx >= 0f ? max.X : min.X;
            float py = ny >= 0f ? max.Y : min.Y;
            float pz = nz >= 0f ? max.Z : min.Z;
            if (nx * px + ny * py + nz * pz + d < 0f) return false;
        }
        return true;
    }

    /// <summary>Distance from a point to the nearest point of the box, 0 inside it.</summary>
    internal static float BoxDistance(Vector3 min, Vector3 max, Vector3 p)
    {
        float dx = MathF.Max(MathF.Max(min.X - p.X, 0f), p.X - max.X);
        float dy = MathF.Max(MathF.Max(min.Y - p.Y, 0f), p.Y - max.Y);
        float dz = MathF.Max(MathF.Max(min.Z - p.Z, 0f), p.Z - max.Z);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>A cheap integer avalanche (Murmur-style finalizer). Deterministic across runs and platforms,
    /// which is what the scatter needs -- <c>Random</c> is neither, and the engine's <c>srand</c>/<c>rand</c>
    /// is process-global state we must not perturb.</summary>
    internal static uint Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16;
        return x;
    }

    internal static float Rnd(ref uint st) { st = Hash(st + 0x9e3779b9u); return (st >> 8) * (1f / 16777216f); }

    // ---- live tuning (/grass) ----------------------------------------------------------------

    /// <summary>One line for the <c>/grass</c> echo and the arm-time log.</summary>
    internal static string Describe()
        => $"{(_off ? "UNAVAILABLE" : Grass.Enabled.V ? "on" : "off")} density x{Grass.Density.V:0.##} wind x{Grass.Wind.V:0.##} windspeed {Grass.WindSpeed.V:0.####} " +
           $"soften {Grass.Soften.V:0.##} aniso {_appliedAniso:0.#}x edge {Grass.Edge.V:0.##} " +
           $"push x{Grass.Push.V:0.##} r{Grass.PushRadius.V:0.##} over {Grass.PushExtent.V:0.#}u " +
           $"cull {(Grass.Cull.V ? $"on shrink {Grass.CullShrink.V:0.##}" + (Grass.CullMax.V > 0f ? $" max {Grass.CullMax.V:0.#}u" : "") : "off")} " +
           $"lod {Grass.Lod.V:0.#}u min {Grass.LodMin.V:0.###} w{Grass.LodWidth.V:0.##} h{Grass.LodHeight.V:0.##} shade {Grass.Shade.V:0.#}u tuft {Grass.Tuft.V:0} vlight {(Grass.VLight.V ? 1 : 0)} shadow {Grass.Shadow.V} castwidth {Grass.CastWidth.V:0.#} ({_shadowDraws} cascade draws) rootao {Grass.RootAo.V:0.##} gtint {Grass.GTint.V:0.##} softedge {(Grass.SoftEdge.V ? 1 : 0)} far {(Grass.Far.V > 0f ? $"{Grass.Far.V:0.#}u" : "engine")} prepass {(Grass.Prepass.V ? (_gl.ProgDepth != 0 ? "on" : "UNAVAILABLE") : "off")} " +
           $"({_pushers} displacing, {_cullers} culling, {_reticles} non-solid skipped) " +
           $"style {(Grass.Style.V == Grass.StyleBlades ? (_gl.ProgBlade != 0 ? $"blades x{Grass.BladeDensity.V:0} width {Grass.Width.V:0.##} clump {Grass.Clump.V:0.##} detail {Grass.BladeLod.V:0}u" : "blades UNAVAILABLE (cards)") : "textures")} " +
           $"backlight {Grass.Backlight.V:0.##} sheen {Grass.Sheen.V:0.##} " +
           $"({_patchCount} patches, {_faceCount} faces / {_bladesResident} blades resident; " +
           $"{(_samples > 1 ? $"{_samples}x MSAA, alpha-to-coverage live" : "no MSAA, dithered edge")}" +
           $"{(_anyFlat ? ", A TEXTURE HAS NO MIPS" : "")})";

    // ---- diagnostics -------------------------------------------------------------------------

    internal static void ResetPeriod()
    { _slotsOver = 0; _clipped = 0; _frames = 0; _draws = 0; _visible = 0; _culled = 0; _baked = 0; _disabled = 0; _candidates = 0; _scatters = 0; _drawnNear = 0; _drawnFar = 0; _drawnCards = 0; _pushWalked = 0; _pushSkips = 0; _pushTexels = 0; _pushOver = 0; _pushFrames = 0; }

    internal static string Stats()
    {
        if (_off) return "no grass (Alloy pass unavailable)";
        if (!Grass.Enabled.V) return "no grass (/grass enabled 0)";
        if (_down.Failed) return "no grass (Alloy program unavailable)";
        if (_frames == 0) return "grass idle";
        int f = Math.Max(_scatters, 1);
        return $"grass {_patchCount} patches / {_faceCount} faces / {_bladesResident} blades resident, " +
               $"{(float)_visible / _frames:F0} patches drawn + {(float)_culled / _frames:F0} culled per frame, " +
               // the scatter's own count, off the GPU a frame late: what the ladder kept, then what survived
               // the per-blade frustum/solid/snow cull, by draw
               $"{(float)_candidates / f:F0} blades past the ladder -> {(float)_drawnNear / f:F0} near + {(float)_drawnFar / f:F0} far blades + {(float)_drawnCards / f:F0} cards drawn per frame" +
               (_gl.Stat == null ? " (NO COUNT READBACK)" : "") +
               $", {(float)_draws / _frames:F0} draws/frame, {_baked} baked" + DbNote() +
               (_thinned == 0 ? "" : $", {_thinned} patches thinned to {MaxPatchBlades} blades") +
               (_slotsOver == 0 ? "" : $", {_slotsOver} MATERIAL-FRAMES PAST THE {Slots} DRAW SLOTS (not drawn)") +
               (_clipped == 0 ? "" : $", {_clipped} FRAMES OVER THE {RecordCap} RECORD CAP (far grass clipped) -- lower /grass density or far") +
               // A refusal is invisible in every other number here -- the patch is present and empty, so
               // "0 blades" and "the area has no grass" read identically without it.
               (_refused == 0 ? "" : $", {_refused} LISTS REFUSED (no faces, no height, or a material index " +
                                     "past the live grass-info count)") +
               (_capped ? $", FACE CAP HIT ({MaxFaces}) -- the rest of the area has no grass" : "") +
               (_patchCapped ? $", PATCH CAP HIT ({MaxPatches}) -- the rest of the area has no grass" : "") +
               // The two facts that decide how the cutout edge is resolved, and neither is visible anywhere
               // else: with no MSAA the edge is dithered rather than coverage-resolved, and a texture with
               // no mip chain gets the flat sampler (so anisotropy and the soften bias do nothing on it).
               $", {(_samples > 1 ? $"{_samples}x MSAA (A2C)" : "no MSAA (dithered edge)")}, mips built {_mipsBuilt}" +
               $", aniso {_appliedAniso:0.#}x" + (_anyFlat ? ", A GRASS TEXTURE HAS NO MIPS" : "") +
               // Zero displacers with creatures on screen means the gob list, not the field: the shader term
               // is doing its job over a map nothing was splatted into, which no other number distinguishes.
               // A material a server removed is skipped here rather than never baked -- so it shows up as
               // resident blades that never draw, which without this reads as culling gone wrong.
               (_disabled == 0 ? "" : $", {(float)_disabled / _frames:F0} patches/frame on DISABLED materials") +
               $", {_pushers} gobs displacing, {_cullers} culling (push field walked {(float)_pushWalked / Math.Max(_pushFrames, 1):F0} objects/frame" +
               // The last walk, broken down so it ADDS UP: a census with a hole in it cannot say which reject
               // is doing the work. "touching no texel" is the outer shell the box-sized window test admits
               // and nothing writes into -- a creature's box widens the reach though a creature never culls.
               $"; last frame {_pushWalkedNow} = {_pushOut} outside the window or off the grass + {_pushNoGeom} without geometry" +
               $" + {_pushIdle} touching no texel + {_reticles} non-solid + {_pushers} displacing)" +
               // The field's own bus traffic. A high skip share with a low texel count is the gate working:
               // an unchanged list is neither cleared nor uploaded, and a changed one carries its rectangle.
               $", push field {(float)_pushSkips / Math.Max(_pushFrames, 1) * 100f:F0}% frames unchanged, " +
               $"{(float)_pushTexels / Math.Max(_pushFrames, 1):F0} texels uploaded/frame of {PushRes * PushRes}" +
               (_pushOver == 0 ? "" : $", {_pushOver} frames PAST {MaxPushers} PUSHERS (rebuilt whole)") +
               // Non-solid gobs are the target reticle and the spell VFX. A zero here on a client that is
               // clearly targeting something means PartTriMesh::GetIsOpaque did not resolve and everything
               // counts as solid again.
               $", {_reticles} non-solid skipped"
               + " (no pusher population -- displacement off)";
    }
}
