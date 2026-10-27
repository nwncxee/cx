using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Alloy.Db;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The depth targets and their programs: creation and resize of the sun, cube and layered arrays, the
/// layered queue's flush, and the teardown every context loss runs.</summary>
public sealed unsafe partial class CasterPass
{
    /// <summary>What the pass still carries per context: the bracket's GPU timer. <see cref="ContextLost"/>
    /// replaces it, so nothing from a dead context survives as a field nobody remembered to zero.</summary>
    private sealed class CasterGlState
    {
        /// <summary>GPU time of the whole shadow bracket; the CPU stopwatch only sees submission.</summary>
        public GpuQuery QShadow;
    }

    private static CasterGlState _gl = new();

    /// <summary>The frame's queued stale views: every stale view is queued and one layered flush per target
    /// array bakes them together. 1024 covers the ceiling population (4 + 4 cascades and 64 x 6 x 2 cube
    /// faces); past it a view is refused, which stands its map down for the frame.</summary>
    private const int MaxBakeViews = 1024;
    /// <summary>Commands the layered buffer may hold per rotation slot (x20 B). Past it the flush chunks its
    /// views -- only a blanket-invalidate frame in a huge area ever needs more than one chunk.</summary>
    private const int MaxLayerCmds = 512 * 1024;
    private static readonly int[] _qSize = new int[MaxBakeViews], _qLayer = new int[MaxBakeViews],
                                  _qSet = new int[MaxBakeViews];
    private static readonly bool[] _qPoint = new bool[MaxBakeViews];
    /// <summary>The queued view's target as a key both flushes read: a GL name means nothing to Vulkan, and the
    /// per-target flush groups on this. Resolved once, where the view is queued.</summary>
    private static readonly CasterMap[] _qMap = new CasterMap[MaxBakeViews];
    /// <summary>This queued view is re-baked under THIS frame's camera, so the receiver-frustum cull
    /// may apply. Never set on a held (static-set) view: it would be culled against a frustum the camera
    /// has since left.</summary>
    private static readonly bool[] _qRx = new bool[MaxBakeViews];
    private static readonly Matrix4x4[] _qVp = new Matrix4x4[MaxBakeViews];
    private static readonly Vector3[] _qFocus = new Vector3[MaxBakeViews], _qDir = new Vector3[MaxBakeViews];
    private static readonly float[] _qReach = new float[MaxBakeViews], _qAlong = new float[MaxBakeViews];
    /// <summary>The queued view's two detail thresholds, resolved from its texel size where every other
    /// per-view term is -- so the layered and per-view paths pick the same level for the same caster.</summary>
    private static readonly float[] _qLodR1 = new float[MaxBakeViews], _qLodR2 = new float[MaxBakeViews];
    private static int _qN;
    /// <summary>One chunk's staged view table.</summary>
    private static readonly CasterViewRec[] _viewStage = new CasterViewRec[MaxBakeViews];
    private static long _layerFlushFails;

    /// <summary>Is the layered path taking this frame's views? It is the only path: the device carries
    /// <c>shaderOutputLayer</c> or no Vulkan backend exists at all.</summary>
    private static bool LayeredActive => VkArmed;

    /// <summary>Has the frame's staging a non-empty source the views can select out of, on whichever backend
    /// bakes them? False bakes a clear, which for an empty world IS the bake.</summary>
    private static bool GatherActive => CasterGatherVk.Active;
    /// <summary>Context loss: every GL name goes at once. Run on the loss edge, not each frame after it.</summary>
    private static void ContextLost() => _gl = new CasterGlState();

    private static void Release(string why)
    {
        // The VAO cache keys on VDO pointers that may now be freed; the GL objects can only be deleted inside
        // the bracket, so the rebuild happens there (EnsureVao). Retiring the slots is also what makes this
        // IDEMPOTENT: a lost 4.6 context disables its service permanently, so Lifecycle calls Release every
        // frame thereafter, and a guard on the caster count alone would re-log forever.
        // The texture-alpha verdicts go with it. They are keyed on GL texture IDS, and GL recycles the
        // names of deleted objects -- the same trap the VAO cache re-checks every pass for. A recycled id
        // inheriting the previous texture's verdict is a wasted fetch in one direction and, in the other, a
        // foliage texture classified as having no alpha: the solid slab back again, intermittently and only
        // after an area transition. That is not a bug anyone would trace.
        Array.Clear(_texKey); Array.Clear(_texHoles); Array.Clear(_texGraded);
        _texQueried = 0; _texWithAlpha = 0;
        // #241/#302: the recorded faces describe a world that just went away, and the previous-frame slot
        // list names ownership slots the next enrollment is free to hand to different parts. Both have to go,
        // or the first frame in the new area finds every view fresh -- shadows frozen from the old module.
        Array.Clear(_cubeBaked); Array.Clear(_cubeHadMover); Array.Clear(_cubeKeyId);
        Array.Clear(_sunBaked); Array.Clear(_sunHadMover); Array.Clear(_sunFitValid);
        CoverRelease();
        SkyRelease();
        // #305: the pool pages name lights that just went away, and the published page indices with them.
        Array.Clear(_cubeCombined); Array.Clear(CubeDynPage);
        // #306: the sun's half of the same wipe, degrade window included.
        Array.Clear(SunDynMask); Array.Clear(_sunCombined); Array.Clear(_sunWinBaked); Array.Clear(_sunDynValid);
        _sunWinFrames = 0;
        Array.Fill(_dynPageOf, -1);
        if (_dynPageSlot.Length > 0) Array.Fill(_dynPageSlot, -1);
        // ...and the log is once per RELEASE, not once per frame: a lost 4.6 context disables its service
        // permanently, so Lifecycle calls this every frame thereafter and an unguarded line would fill the log.
        bool any = _prevCount > 0 || _liveCount > 0 || CascadeCount > 0 || CubeCount > 0;
        _invalidate = true; _moverCount = 0; _ghostCount = 0;
        _prevCount = 0;
        _liveCount = 0; _liveCreatures = 0; _liveGen = -1;
        CascadeCount = 0; CubeCount = 0;   // the maps describe a world that just went away; stop the receiver sampling them
        if (any) _log?.Info($"CasterPass: caster set released ({why})");
    }

    /// <summary>Render the frame's queued views, one layered pass per distinct target texture.
    /// False = the flush could not complete; the caller must publish NOTHING this frame.</summary>
    private static bool FlushLayered()
    {
        int n = _qN; _qN = 0;
        if (n == 0) return true;
        // the views are only QUEUED here: the colour frame flushes them once its own uploads have published the
        // buffers the bake's binds name
        _vk.Queued = n;
        return true;
    }

    private static void StageView(CasterViewRec* dst, int vi, int k)
    {
        ref CasterViewRec r = ref dst[vi];
        r.Vp = _qVp[k];
        r.Focus = _qFocus[k]; r.Reach = _qReach[k];
        r.Dir = _qDir[k]; r.Along = _qAlong[k];
        r.Receiver = _qRx[k] ? 1f : 0f;   // #488: the receiver cull applies to this view
        r.Point = _qPoint[k] ? 1f : 0f;
        r.Set = _qSet[k];
        r.Layer = _qLayer[k];
        r.LodR1 = _qLodR1[k];
        r.LodR2 = _qLodR2[k];
    }

    /// <summary>Bake one view (a cascade layer, or one face of one cube) from what it sees this frame: the
    /// gather dispatch selects out of the frame's staging and the survivors draw by indirect count, with
    /// <paramref name="point"/> picking which view test it runs. False = nothing usable, so the caller must
    /// not publish. The layer is cleared even when nothing survives -- uncleared is LAST frame's depth.</summary>
    private static bool RenderView(CasterMap map, int size, int layer,
                                   Matrix4x4* vp, Vector3 lightDir, Vector3 focus, float reach, float margin,
                                   bool point, int set = CasterGather.SetAll, bool receiver = false,
                                   bool detail = true)
    {
        // zeroed ahead of every early-out: a view that bakes nothing staged no creatures, and a stale count
        // would be re-added per cube face
        _submitCreatures = 0;
        if (!_gStaged) return false;
        LastViews++;
        if (!GatherActive) return ClearLayer(map, layer);   // an EMPTY world still bakes: the clear is the bake
        // CX-50: this view's two detail thresholds, where every other per-view term of the gather is resolved
        // -- one funnel, so the layered and per-view paths cannot pick different levels for one caster. An
        // ORTHO view's texel is its fit over the map (CascadeTexel's own expression, and a pair tile's);
        // a 90deg cube face spans 2d at depth d, so its texel is per unit of DISTANCE and the dispatch
        // divides the caster's bound by that distance.
        // /shadow detail 0 is the A/B for the whole of it: thresholds of 0 bake every caster's base run,
        // which is what the pass did before, with the colour path's own detail left alone. A view that is
        // not a shadow passes detail: false and takes that same base run -- see the cover pass.
        MeshLod.ShadowThresholds(detail && Shadows.Detail.V ? point ? 2f / size : 2f * reach / size : 0f,
                                 out float lodR1, out float lodR2);
        // #307: under the layered bake a stale view is QUEUED, not submitted -- the flush after the pass
        // loop renders every queued view of a target in one layered pass. The optimistic true is covered by
        // the flush-failure handling, which publishes nothing for the frame. An overflowing queue renders
        // inline rather than dropping a view.
        if (LayeredActive && _qN < MaxBakeViews)
        {
            // a target the Vulkan bake has no array for -- the sky view's own, a pair tile -- is refused rather than
            // queued: its caller must not publish a map nothing baked
            if (map == CasterMap.Other) return VkNoTarget();
            _submitCreatures = _gCreatures;
            _qMap[_qN] = map;
            _qSize[_qN] = size; _qLayer[_qN] = layer; _qVp[_qN] = *vp;
            _qFocus[_qN] = focus; _qDir[_qN] = lightDir; _qReach[_qN] = reach; _qAlong[_qN] = reach + margin;
            _qPoint[_qN] = point; _qSet[_qN] = set; _qRx[_qN] = receiver;
            _qLodR1[_qN] = lodR1; _qLodR2[_qN] = lodR2;
            _qN++;
            return true;
        }
        // past the queue's ceiling the view is refused, which stands its map down for the frame
        return VkQueueFull();
    }

    /// <summary>Clear one depth layer to "nothing casts" without a bake: the static faces of a roaming light.</summary>
    private static bool ClearLayer(CasterMap map, int layer)
        => map != CasterMap.Other ? VkClearLayer(map, layer) : VkNoTarget();
}
