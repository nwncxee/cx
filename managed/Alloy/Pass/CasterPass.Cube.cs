using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Alloy.Db;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The local-light shadow cubes: slot tenancy over the light budget, the per-face re-bake predicate that
/// replaced the unconditional bake, and the small dynamic pool the receiver composes with min().</summary>
public sealed unsafe partial class CasterPass
{
    /// <summary>Local-light cube ceiling: how long the receiver's <c>uLocalLight</c> array is, and the most
    /// <c>shadow.cubes</c> may ask for. Raising it costs uniform slots only -- the cube array itself is
    /// sized from the CONFIGURED count, so an unused ceiling is free.</summary>
    internal const int MaxCubes = 64;
    /// <summary>Local lights that get a shadow cube (<c>shadow.cubes</c>, 0 = off) and the per-face size
    /// (<c>shadow.cubesize</c>). A cube ARRAY has no sampler ceiling -- the cost is VRAM and nothing else --
    /// and a torch-lit street routinely declares 60+ shadow-casting lights, where a small budget's membership
    /// churns as the player walks, which reads as shadows popping. Every slot bakes six faces a frame.</summary>
    private static int _cubeLights = 32, _cubeSize = 512;
    /// <summary>Near plane shared by every cube face. Bigger is better depth precision; it also means a
    /// caster closer to the light than this is not recorded, so it stays small.</summary>
    private const float CubeNear = 0.3f;

    /// <summary>The cube array holding the local-light shadows (0 = none yet), layer <c>6*slot + face</c>.</summary>
    internal static uint CubeTex => _gl.CubeTex;
    /// <summary>Cubes fully rendered on the last pass -- a light that lost a face to a ring stall is not in
    /// this count, so the receiver never samples a cube with one stale face in it.</summary>
    internal static int CubeCount;
    /// <summary>Per cube slot: the position the cube was baked from (xyz) and its far plane (w). The receiver
    /// reads the light's geometry from HERE, never from the live light, so its depth comparison stays welded
    /// to the depth it samples.</summary>
    internal static readonly float[] CubeLight = new float[MaxCubes * 4];
    /// <summary>Engine light identity per cube slot (equality only, never dereferenced -- the engine frees
    /// PartLight on area boundaries). This is what binds a light in the walk's snapshot to its cube.</summary>
    internal static readonly IntPtr[] CubeLightId = new IntPtr[MaxCubes];
    /// <summary>Face size of the cube array as rendered -- the receiver's texel scale.</summary>
    internal static int CubeFaceSize => _cubeSize;
    /// <summary>Near plane the cubes were rendered with; welds the receiver's depth reference to the render.</summary>
    /// <summary>Every point cube's near plane: the self-shadow skip radius (<c>shadow.near</c> x <c>light.radius</c>),
    /// never under <see cref="CubeNear"/>. The geometry a light node sits in (a brazier's bowl) casts nothing,
    /// on live receivers exactly as in the bake -- the two disagreeing cut a creature's shadow off near it.</summary>
    internal static float CubeNearPlane => MathF.Max(CubeNear, AlloyLights.ShadowNearWorld);
    private static float _cubeNearSeen;

    // --- #241: the cube re-bake predicate. Every slot's six faces baked unconditionally every frame was
    // #187's deliberate simplification, waived until the renderer owned the pipeline; #238's trace made the
    // shadow pass 51% of the heavy frame and 192 views/frame. This is the INVALIDATION that replaces it --
    // never the composite cache #187 measured and rejected, whose per-view server-side depth copy cost more
    // than the bakes it saved. A stale face is baked exactly as it is baked today; a fresh one is skipped.
    // The failure mode of a wrong answer is a shadow that FREEZES, so every term errs toward stale.

    /// <summary>The bake state of one cube slot, all of it carried across frames. Separate storage from
    /// <see cref="CubeLight"/>/<see cref="CubeLightId"/>, which the publish loop wipes every frame.</summary>
    private static readonly IntPtr[] _cubeKeyId = new IntPtr[MaxCubes];
    private static readonly float[] _cubeKeyPos = new float[MaxCubes * 3];
    private static readonly float[] _cubeKeyFar = new float[MaxCubes];
    /// <summary>Does this slot hold a complete, usable bake? False after a ring-fence timeout, a release, or
    /// any (re)creation of the array -- all of which leave the layers holding depth nobody may sample.</summary>
    private static readonly bool[] _cubeBaked = new bool[MaxCubes];
    /// <summary>Did a mover intersect this slot's light on the PREVIOUS frame? That is what invalidates the
    /// frame a mover LEAVES: it is inside no longer, so no live mover would name the slot it just vacated.
    /// Under the split this carries STATIC-EVENT evidence instead (appearances, vanishes, set transitions) --
    /// toggling <c>/shadow split</c> sets the blanket, so the two meanings never mix.</summary>
    private static readonly bool[] _cubeHadMover = new bool[MaxCubes];
    /// <summary>Frames in a row the static cap has held this slot; at <see cref="StaticHoldMax"/> it bakes
    /// regardless, so far ranks are not starved by near ones under sustained churn.</summary>
    private static readonly byte[] _cubeHeldRun = new byte[MaxCubes];
    private const int StaticHoldMax = 8;
    /// <summary>A ROAMING light: one whose position changed on consecutive scans (a carried torch). Its
    /// static pages are cleared and everything in reach bakes into its dynamic page every frame. The
    /// last-seen position is the motion term, never the bake key (a demoted slot keys nothing, so the key
    /// would read as moving forever); the count saturates at 2 so an early break cannot lose the level.</summary>
    private static readonly float[] _cubeLastPos = new float[MaxCubes * 3];
    /// <summary>The scans of the last two registered moves. Roaming = both within <c>dynhold</c> frames: the
    /// same hold the casters' dynamic set has, so a light that pauses for a frame does not flap between a
    /// 512px static bake and six clears. Registered = <see cref="RoamEpsilon"/> or more from the latched last
    /// position: torch jitter at the ULP is not motion, a crawl is.</summary>
    private static readonly int[] _cubeMovedAt = new int[MaxCubes], _cubeMovedPrev = new int[MaxCubes];
    /// <summary>World units a light must move from its latched last position to register a move. 1mm.</summary>
    private const float RoamEpsilon = 1e-3f;
    /// <summary>The "never moved" stamp. Half of <c>int.MinValue</c> so <c>_frameSeq - stamp</c> cannot wrap
    /// negative and pass the recency term.</summary>
    private const int NeverMoved = int.MinValue / 2;
    private static readonly bool[] _cubeRoam = new bool[MaxCubes];
    private static long _cubeRoamSlots;

    // --- #305: the static/dynamic split. The predicate above can only SKIP a view; it cannot make a stale
    // one cheaper, and a stale cube re-bakes the whole static world it sees -- one walking creature dirties
    // the room. So the population is split: the STATIC array (the existing _gl.CubeTex) bakes on the predicate
    // and is invalidated only by static-set events, while a smaller DYNAMIC pool holds movers only and
    // re-bakes every frame, which is affordable because it is the movers and not the room. The receiver
    // composes the two with min() -- depth is a min-of-blockers, so it composes exactly, and NO COPY exists
    // anywhere in the design (this is not #187's composite cache, whose per-view glCopyImageSubData is what
    // lost). Cubes only; the sun cascades keep the plain predicate.

    /// <summary>Dynamic-pool face size (<c>shadow.dynsize</c>) and page count (<c>shadow.dyncount</c>, 0 = no
    /// pool, split permanently off; defaults to the cube budget so the pool never starves into the combined
    /// fallback). A PAGE is one light's six mover-only faces, claimed only while a mover stands inside that
    /// light, which is what keeps its VRAM a fraction of the static array's.</summary>
    private static int _cubeDynSize = 256, _cubeDynPages = 16;
    /// <summary><c>/shadow split 1|0</c>: the A/B. Off = one combined bake per cube on the plain
    /// predicate.</summary>
    private static bool _split => Shadows.Split.V;
    /// <summary>The dynamic pool's cube array (0 = none yet), layer <c>6*page + face</c>.</summary>
    internal static uint CubeDynTex => _gl.CubeDynTex;
    /// <summary>Per cube slot: the pool PAGE holding its movers, PLUS ONE -- 0 means "no movers near this
    /// light", which is also what a receiver program that never sets the uniform reads, so a stale content
    /// override degrades to static-only rather than to sampling page zero. Published fresh each frame the
    /// page bakes.</summary>
    internal static readonly float[] CubeDynPage = new float[MaxCubes];
    /// <summary>Spheres per slot in <see cref="CubeDynSphere"/>. One union sphere round every mover in a 36u
    /// reach covered the whole plaza, so the hot path's ray test rejected nothing -- and so did four once a
    /// busy plaza put twenty-odd movers under every lamp. A miss is a few ALU ops on a broadcast read; a
    /// false hit is the point-light body and a cube tap.</summary>
    internal const int DynSpheres = 16;
    /// <summary>Per slot: <see cref="DynSpheres"/> spheres (xyz, r; r &lt; 0 = unused) round the dynamic movers
    /// in the light's reach -- the only thing its page can shadow. The lit stage skips the hot path wherever the
    /// light-to-fragment ray misses all of them. Sphere 0 = the light's reach while it roams (the page then
    /// holds everything).</summary>
    internal static readonly float[] CubeDynSphere = new float[MaxCubes * DynSpheres * 4];
    /// <summary>Bumped whenever <see cref="CubeDynSphere"/> is rewritten, so the upload runs once per frame.</summary>
    internal static int CubeDynSphereGen;
    /// <summary>Slot -&gt; claimed pool page (-1 none) and page -&gt; owning slot (-1 free). Sticky, like the
    /// slots themselves: a page churn would be invisible in the depth (it re-bakes every frame) but wastes
    /// nothing to avoid.</summary>
    private static readonly int[] _dynPageOf = new int[MaxCubes];
    private static int[] _dynPageSlot = Array.Empty<int>();
    /// <summary>Per pool page, the faces an EVENT (a mover, a membership flip, a ghost) sat in last
    /// frame -- a mover's previous face needs the re-bake as much as its current one. <see cref="AllFaces"/>
    /// on claim, on a blanket and on a light that moved.</summary>
    private static int[] _dynPageMask = Array.Empty<int>();
    /// <summary>The page holds a complete bake the receiver may keep sampling through a skipped frame.</summary>
    private static bool[] _dynPageValid = Array.Empty<bool>();

    /// <summary>Frames a cube slot's shadow takes to fade in after its first bake and out after its light
    /// drops from the budget; 0 = pop. A draining slot keeps its depth and its light id and takes no newcomer
    /// until it reaches zero, so the receiver keeps finding it through CubeSlotOf.</summary>
    private static int _fadeFrames => Shadows.Fade.V;
    /// <summary>Slots held back from the ranking while the fade is on, so a light leaving the budget has a
    /// slot to drain in. Without it a full budget (the plaza's 32/32) swaps every departure into a newcomer
    /// the same frame and the fade-out never runs. <c>/shadow fadeslots N</c>.</summary>
    private static int _fadeSlots => Shadows.FadeSlots.V;
    private static readonly float[] _cubeFade = new float[MaxCubes];
    /// <summary>Per-slot shadow strength for the receiver (uLocalFade), 0..1.</summary>
    internal static readonly float[] CubeFade = new float[MaxCubes];
    private static int _cubeTurnovers;
    private static long _cubeHeldSlots;

    /// <summary>May the colour pass sample the cubes right now? Same single test as
    /// <see cref="CascadesFresh"/>: rendered on the most recent frame tick or not at all, which covers the
    /// vanilla verdict, a module unload and a disarm together.</summary>
    internal static bool CubesFresh
        => CubeCount > 0 && (Renderer.OnVulkan ? !_vk.Cubes.IsNull : _gl.CubeTex != 0) && _cubeStamp == _frameSeq;

    /// <summary>Cube slot holding <paramref name="id"/>'s shadow, or -1. Called once per area light from the
    /// light walk, so it is O(lights * cubes) = a few hundred comparisons a frame, alloc-free.</summary>
    /// <summary>The slot lost its light: whatever depth it holds describes a light no longer in the budget,
    /// and the next light to take it must not inherit it -- nor its dynamic page, mode or fade.</summary>
    private static void FreeCubeSlot(int j)
    {
        if (_cubeKeyId[j] != IntPtr.Zero) _cubeTurnovers++;
        _cubeFade[j] = 0f;
        _cubeBaked[j] = false; _cubeKeyId[j] = IntPtr.Zero; _cubeHadMover[j] = false;
        _cubeCombined[j] = false;
        if (_dynPageOf[j] >= 0) { _dynPageSlot[_dynPageOf[j]] = -1; _dynPageOf[j] = -1; }
    }

    internal static int CubeSlotOf(IntPtr id)
    {
        if (!CubesFresh || id == IntPtr.Zero) return -1;
        for (int i = 0; i < CubeCount; i++) if (CubeLightId[i] == id) return i;
        return -1;
    }

    /// <summary>The local-light cubes: six faces per light, budget spent nearest the shadow focus first. A
    /// slot's six faces are baked only when the predicate says its recorded depth no longer describes the
    /// world; everything else republishes the slot untouched. A light is published only once all six faces
    /// landed -- a cube has no innermost-first ordering, so five fresh faces and one stale one would lie.</summary>
    private static void CubePass(Vector3 focus)
    {
        int n = FrameInputs.LocalLightCount;
        if (n > _cubeLights) n = _cubeLights;
        // #322 review: the SAME predicate and slack the receiver-side light cull runs
        // (ShadowMath.SphereInCameraFrustum, self-checked) -- not a second hand-rolled cone. Two divergent
        // answers to "is this light visible" starve a light one of them keeps; one answer cannot drift.
        // A light whose LIT SPHERE misses the frustum shades no visible fragment, so neither its static
        // bake nor its dynamic page can be sampled this frame. The one consumer this lies to is the probe
        // capture (it renders all six directions), which is rare and env-mapped-only.
        bool cull = FrameInputs.HasPerspective && FrameInputs.HasSceneCam;
        Vector3 camPos = FrameInputs.SceneCamPos;
        Vector3 camFwd = cull ? Vector3.Normalize(FrameInputs.SceneCamFwd) : Vector3.UnitX;
        float camFar = FrameInputs.CamFar;
        // Nearest-first by camera distance: the budget is spent on the lights the player is standing in, and
        // a carried torch is by construction among them. Insertion sort, n <= MaxCubes, alloc-free.
        int* order = stackalloc int[MaxCubes];
        float* dist = stackalloc float[MaxCubes];
        int have = 0;
        // shadow.cubedist: a light past it claims no slot (no bake, no page, no fetch); its shadow fades over
        // the last quarter of the way there so the budget's edge never pops
        float cubeDist = Shadows.CubeDist.V, cubeDist2 = cubeDist > 0f ? cubeDist * cubeDist : float.PositiveInfinity;
        for (int i = 0; i < FrameInputs.LocalLightCount; i++)
        {
            // Rank by distance to the shadow FOCUS (the PC), not the camera. Historically the camera was
            // whatever the last SetViewTransform left -- live-observed as (0, 0, 742), a UI/ortho pass --
            // and sorting by distance to a point 742u overhead ranks lights by ALTITUDE: which lights got a
            // cube was near-arbitrary and reshuffled as the light set shifted, so a carried torch's shadow
            // blinked in and out as the player walked. #257 made the camera a direct read and that hazard
            // is gone, but the focus is still the right anchor -- it is the PC, which is what the shadows
            // are for, and the sun fit takes the same point.
            float d = (FrameInputs.LocalLightPos[i] - focus).LengthSquared();
            if (d > cubeDist2 || (have == n && (n == 0 || d >= dist[n - 1]))) continue;
            int k = have < n ? have : n - 1;
            while (k > 0 && dist[k - 1] > d) { order[k] = order[k - 1]; dist[k] = dist[k - 1]; k--; }
            order[k] = i; dist[k] = d;
            if (have < n) have++;
        }

        // the drain reserve: the ranking's tail gives up its slots to lights on the way out
        int reserve = _fadeFrames > 0 ? Math.Min(_fadeSlots, _cubeLights - 1) : 0;
        if (have > _cubeLights - reserve) have = _cubeLights - reserve;
        // #241: STICKY slots. Slot used to be rank in the nearest-first sort, so a walking player reshuffled
        // every light's slot every frame and the predicate's slot term would have fired on all of them -- the
        // exact re-bake storm #151 measured under rank keys. A light keeps the slot it had; newcomers take
        // what is free. The receiver looks slots up by light id (CubeSlotOf), so which number it is never
        // mattered to it -- only that it stops moving.
        int* pick = stackalloc int[MaxCubes];
        bool* seated = stackalloc bool[MaxCubes];
        for (int j = 0; j < _cubeLights; j++) { pick[j] = -1; seated[j] = false; }
        for (int s2 = 0; s2 < have; s2++)
        {
            IntPtr id = FrameInputs.LocalLightId[order[s2]];
            if (id == IntPtr.Zero) continue;
            for (int j = 0; j < _cubeLights; j++)
                if (_cubeKeyId[j] == id && pick[j] < 0) { pick[j] = s2; seated[s2] = true; break; }
        }
        // a slot still fading its last light out is taken (-2): its depth and id stay published until it is dark
        for (int j = 0; j < _cubeLights; j++)
            if (pick[j] < 0 && _cubeFade[j] > 0f && _cubeKeyId[j] != IntPtr.Zero && _cubeBaked[j]) pick[j] = -2;
        for (int s2 = 0, j = 0; s2 < have; s2++)
        {
            if (seated[s2]) continue;
            while (j < _cubeLights && pick[j] != -1) j++;
            if (j >= _cubeLights)
            {   // full budget (the plaza's steady state): a newcomer takes the dimmest draining slot rather
                // than waiting it out, which under 32/32 would leave every arriving light dark for 2x fade.
                // The departing light pops out at its residual strength -- the one pop the fade leaves.
                int steal = -1;
                for (int k = 0; k < _cubeLights; k++)
                    if (pick[k] == -2 && (steal < 0 || _cubeFade[k] < _cubeFade[steal])) steal = k;
                if (steal < 0) break;
                FreeCubeSlot(steal); pick[steal] = s2;
                continue;
            }
            pick[j++] = s2;
        }
        // ...and the walk itself stays in RANK order, which is what the ring-stall break below depends on.
        // Iterating by slot index would, under sticky seating, drop an ARBITRARY set of lights on a stall --
        // and the same low slots every frame, so the player's own torch could sit in slot 27 and lose its
        // shadow indefinitely rather than for one frame.
        int* slotOf = stackalloc int[MaxCubes];
        for (int s2 = 0; s2 < have; s2++) slotOf[s2] = -1;
        for (int j = 0; j < _cubeLights; j++)
        {
            if (pick[j] >= 0) { slotOf[pick[j]] = j; continue; }
            if (pick[j] == -2)
            {
                _cubeFade[j] -= _fadeFrames > 0 ? 1f / _fadeFrames : 1f;
                if (_cubeFade[j] > 0f) continue;
            }
            FreeCubeSlot(j);
        }

        // A slot nobody claims is published INACTIVE (far = 0, id = 0) rather than compacted away: the
        // receiver reads far <= near as "lit", so holes in the slot array are free.
        for (int j = 0; j < _cubeLights; j++)
        {
            CubeLight[j * 4 + 3] = 0f; CubeLightId[j] = IntPtr.Zero; CubeDynPage[j] = 0f;
            if (pick[j] != -2 || !_cubeBaked[j]) continue;
            CubeLight[j * 4] = _cubeKeyPos[j * 3]; CubeLight[j * 4 + 1] = _cubeKeyPos[j * 3 + 1];
            CubeLight[j * 4 + 2] = _cubeKeyPos[j * 3 + 2]; CubeLight[j * 4 + 3] = _cubeKeyFar[j];
            CubeLightId[j] = _cubeKeyId[j];
        }

        int done = 0, frameCreatures = 0, nearestCreatures = -1, staticBaked = 0;
        // #241: the transition below is only meaningful about a slot this frame BAKED -- a skipped nearest
        // light stages nothing and would otherwise read as the creature shadow vanishing every still frame.
        bool nearestBaked = false;
        bool invalidate = _frameInvalidate;
        if (CubeNearPlane != _cubeNearSeen) { _cubeNearSeen = CubeNearPlane; invalidate = true; }   // a knob moved the near plane: every cube re-bakes
        bool scannedAll = true;
        // #305: the split runs only when its pool exists -- shadow.dyncount 0 or a refused allocation is the
        // combined bake everywhere, exactly as /shadow split 0 spells it.
        bool split = _split && _cubeDynPages > 0 && (Renderer.OnVulkan || _gl.CubeDynTex != 0);
        // #322 review: one hidden UNBAKED slot per frame bakes anyway, nearest first -- the warm-up drain.
        // Without it a camera turn finds every hidden slot unbaked at once and the ring smears the burst
        // as visible shadow pop-in; with it the churn is paid gradually while hidden, which is what the
        // pre-cull behaviour paid continuously.
        int warmBudget = 1;
        MoverBox* dynBox = stackalloc MoverBox[DynSpheres];
        CubeDynSphereGen++;

        // #322 review's visibility verdict, hoisted: ONE SphereInCameraFrustum per ranked light, read below
        // where it used to be called inline. The hoist is what lets the pair plan (CX-188 step 4) ask the same
        // question -- a hidden light must not be paired, for the same reason it must not hold a pool page.
        bool* culled = stackalloc bool[MaxCubes];
        for (int rank = 0; rank < have; rank++)
        {
            culled[rank] = false;
            int slot0 = slotOf[rank];
            if (slot0 < 0 || !cull) continue;
            int li0 = order[rank];
            culled[rank] = !ShadowMath.SphereInCameraFrustum(FrameInputs.LocalLightPos[li0],
                                                            FrameInputs.LocalLightRange[li0] * ShadowMath.FrustumCullSlack,
                                                            camPos, camPos + camFwd, FrameInputs.CamFovy,
                                                            FrameInputs.CamAspect, FrameInputs.CamNear, camFar);
        }
        // CX-188 step 4: the PER-MOVER light budget. The candidate lights come in rank order (so the atlas
        // fills from the nearest), every dynamic mover then ranks them at its own position and keeps the
        // lightmap.moverlights strongest, and Commit turns the survivors into pair tiles grouped by slot.
        // Off unless the deferred pass is live, and then a paired light publishes no pool page below.
        PairShadows.Begin(_frameSeq);
        PairShadows.View(cull, camPos, camFwd, FrameInputs.CamFovy, FrameInputs.CamAspect, FrameInputs.CamNear, camFar);
        if (PairShadows.On && split)
        {
            for (int rank = 0; rank < have; rank++)
            {
                int slot0 = slotOf[rank];
                if (slot0 < 0 || culled[rank] || rank >= _dynRanks) continue;
                int li0 = order[rank];
                bool okL = PairShadows.AddLight(slot0, FrameInputs.LocalLightId[li0], FrameInputs.LocalLightPos[li0],
                                     FrameInputs.LocalLightRange[li0]);
                if (PairShadows.Dump.Next) PairShadows.Note($"  rank {rank} slot {slot0} pos {FrameInputs.LocalLightPos[li0]} far {FrameInputs.LocalLightRange[li0]:F1} -> {(okL ? "candidate" : "REFUSED by AddLight")}");
            }
            if (PairShadows.Dump.Next)
                for (int rank = 0; rank < have; rank++)
                    if (slotOf[rank] < 0 || culled[rank] || rank >= _dynRanks)
                        PairShadows.Note($"  rank {rank} slot {slotOf[rank]} pos {FrameInputs.LocalLightPos[order[rank]]} NOT OFFERED ({(slotOf[rank] < 0 ? "no slot" : culled[rank] ? "culled" : $"rank past dynRanks {_dynRanks}")})");
            // the same mover population the page claim walks, filtered the same way: a NaN sphere is no
            // mover's, and a still lantern under dyn.min is not worth a tile any more than a page. A creature's
            // parts (a player character is ~19 rigid meshes) fold into ONE mover, the box round them all: one tile
            // per body, which the bake fills with every dynamic part inside it. Per part, a light's 16-mover list
            // overflowed on a single character and the parts past it cast nothing.
            _aggN = 0;
            for (int q = 0; q < _dMoverCount; q++)
            {
                int m = _dMoverIdx[q];
                int o = _liveObj[m];
                if (o < 0 || !float.IsFinite(_liveCx[m] + _liveCy[m] + _liveCz[m] + _liveR[m])) continue;
                // the FRAMING radius, not the cull's: a skinned part's pose slack (+2 m, at least 3) boxed over a
                // body framed a 5.6 m ball, which every lamp within 7 m refused as too close to frame
                float fr = MathF.Min(AlloyRenderer.CasterFrameRadius(_liveSlot[m]), _liveR[m]);
                var lo = new Vector3(_liveCx[m] - fr, _liveCy[m] - fr, _liveCz[m] - fr);
                var hi = new Vector3(_liveCx[m] + fr, _liveCy[m] + fr, _liveCz[m] + fr);
                if (_aggSeq[o] != _frameSeq) { _aggSeq[o] = _frameSeq; _aggLo[o] = lo; _aggHi[o] = hi; if (_aggN < _aggObj.Length) _aggObj[_aggN++] = o; }
                else { _aggLo[o] = Vector3.Min(_aggLo[o], lo); _aggHi[o] = Vector3.Max(_aggHi[o], hi); }
            }
            // ORDER: bodies before loose parts -- a light's mover list is first-come, and a plaza's swaying foliage
            // fills every lamp's sixteen. A body's identity is its object, complemented so it never equals a slot.
            for (int k = 0; k < _aggN; k++)
            {
                int o = _aggObj[k];
                Vector3 c = (_aggLo[o] + _aggHi[o]) * 0.5f;
                PairShadows.AddMover(c.X, c.Y, c.Z, (_aggHi[o] - _aggLo[o]).Length() * 0.5f, ~o);
            }
            // ...then the loose dynamic parts, with the caster's SLOT as identity: stable while it is enrolled,
            // which is what lets a light's run keep its order and a tile's layer key still match next frame
            for (int q = 0; q < _dMoverCount; q++)
            {
                int m = _dMoverIdx[q];
                if (_liveObj[m] >= 0 || !float.IsFinite(_liveCx[m] + _liveCy[m] + _liveCz[m] + _liveR[m]) || _liveR[m] < Shadows.DynMin.V) continue;
                PairShadows.AddMover(_liveCx[m], _liveCy[m], _liveCz[m], _liveR[m], _liveSlot[m]);
            }
            PairShadows.Commit();
        }

        for (int rank = 0; rank < have; rank++)
        {
            int slot = slotOf[rank];
            if (slot < 0) continue;   // more lights than slots: the tail of the ranking gets no cube
            int li = order[rank];
            int lightCreatures = 0;
            Vector3 pos = FrameInputs.LocalLightPos[li];
            float far = FrameInputs.LocalLightRange[li];
            IntPtr id = FrameInputs.LocalLightId[li];
            if (!(far > CubeNearPlane)) { _cubeBaked[slot] = false; continue; }   // no reach, nothing worth sampling

            // #241 term 1+2: the light itself. Position, reach and identity are the three things the recorded
            // faces are a function of, so any of them differing is a bake -- as is a slot holding no complete
            // bake at all (a fence timeout, a release, a fresh array).
            // Exact on purpose: a cube face is a 90-degree perspective, so a world-space slack of one texel at
            // reach is far/d texels for a caster at distance d. A light that moves is a roamer (below).
            bool slotChanged = !_cubeBaked[slot] || _cubeKeyId[slot] != id || _cubeKeyFar[slot] != far
                               || _cubeKeyPos[slot * 3] != pos.X || _cubeKeyPos[slot * 3 + 1] != pos.Y
                               || _cubeKeyPos[slot * 3 + 2] != pos.Z;
            // #322 review: the visibility verdict, BEFORE any page claim. Hidden lights must not hold pool
            // pages (they never bake them, so holding one only starves a visible light into the combined
            // fallback -- a six-face re-bake every mover frame), and their split mode must not flap.
            // ...the verdict itself taken in the hoisted pre-loop above, so the pair plan and this share one
            // answer to "is this light visible" (two divergent ones starve a light the other keeps).
            bool off = false, culledHere = false;
            if (culled[rank])
            {
                off = culledHere = true;
                if (_cubeBaked[slot] || warmBudget <= 0) _cubeOffSlots++;
                else { warmBudget--; off = false; }   // the warm-up drain: bake this hidden slot after all
            }
            // A light in motion is a mover by definition, and the same rule applies: continuous motion lives
            // in the dynamic page and must not re-bake the static world every frame. Measured before the
            // page claim, because a roaming light needs a page whether or not any caster near it moves.
            // Off the CULLED verdict, not the drained one: a roaming slot is never baked, so the drain would
            // pick it every frame and bake six page faces nobody sees while every other hidden slot starved.
            float lx = _cubeLastPos[slot * 3] - pos.X, ly = _cubeLastPos[slot * 3 + 1] - pos.Y, lz = _cubeLastPos[slot * 3 + 2] - pos.Z;
            if (_cubeKeyId[slot] != id) _cubeMovedAt[slot] = _cubeMovedPrev[slot] = NeverMoved;
            if (lx * lx + ly * ly + lz * lz > RoamEpsilon * RoamEpsilon || _cubeKeyId[slot] != id)
            {
                _cubeLastPos[slot * 3] = pos.X; _cubeLastPos[slot * 3 + 1] = pos.Y; _cubeLastPos[slot * 3 + 2] = pos.Z;
                if (_cubeKeyId[slot] == id) { _cubeMovedPrev[slot] = _cubeMovedAt[slot]; _cubeMovedAt[slot] = _frameSeq; }
            }
            // The gap term at dynhold 0 would be unsatisfiable (two stamps are never equal): 1 is the floor.
            bool roam = split && !culledHere && rank < _dynRanks && _frameSeq - _cubeMovedAt[slot] <= _dynHold
                        && _cubeMovedAt[slot] - _cubeMovedPrev[slot] <= Math.Max(_dynHold, 1);
            // #305: is a DYNAMIC caster inside this light's reach? That is what claims (and keeps) a pool
            // page -- and, when the pool is dry, what forces the combined fallback below.
            bool hasDyn = roam;
            int boxes = 0;
            if (split && !off && rank < _dynRanks)
                for (int q = 0; q < _dMoverCount; q++)
                {
                    int m = _dMoverIdx[q];
                    if (!float.IsFinite(_liveCx[m] + _liveCy[m] + _liveCz[m] + _liveR[m])) { _nanMovers++; continue; }
                    // a still scene's 20 matrix movers a frame were flames and swaying lanterns (run 9): each
                    // claimed a page under its light and turned that light HOT on every baked fragment in reach
                    if (!_liveCreature[m] && _liveR[m] < Shadows.DynMin.V) continue;
                    float mx = _liveCx[m] - pos.X, my = _liveCy[m] - pos.Y, mz = _liveCz[m] - pos.Z;
                    float mr = far + _liveR[m];
                    if (mx * mx + my * my + mz * mz > mr * mr) continue;
                    hasDyn = true;
                    boxes = AddMoverBox(dynBox, boxes, _liveCx[m], _liveCy[m], _liveCz[m], _liveR[m]);
                }
            WriteDynSpheres(slot, roam ? 0 : boxes, dynBox, pos, far);
            // CX-188 step 4: a light the pair plan claimed needs NO pool page -- its movers are in tiles of
            // their own, and the lane published below says so with a negative. A ROAMING light keeps its page:
            // the pair views were framed on its movers from a position the roamer's own motion invalidates, and
            // a roamer's static faces are cleared, so min() has nothing else to compose with.
            int pairBase = PairShadows.Pairs(slot);
            if (pairBase >= 0 && (off || roam)) { PairShadows.Drop(slot); pairBase = -1; }
            if (pairBase >= 0) hasDyn = false;
            int page = _dynPageOf[slot];
            if (off && page >= 0)
            {   // hidden: the page returns to the pool now; re-entry reclaims one
                _dynPageSlot[page] = -1; _dynPageOf[slot] = -1; page = -1;
            }
            else if (split && hasDyn && page < 0)
            {
                for (int p = 0; p < _cubeDynPages && page < 0; p++)
                    if (_dynPageSlot[p] < 0) { _dynPageSlot[p] = slot; _dynPageOf[slot] = page = p; _dynPageMask[p] = AllFaces; _dynPageValid[p] = false; }
            }
            else if (page >= 0 && (!split || !hasDyn))
            {   // no movers left near this light: the page returns to the pool and the receiver stops
                // sampling it (the publish wipe already left CubeDynPage[slot] at 0)
                _dynPageSlot[page] = -1; _dynPageOf[slot] = -1; page = -1;
            }
            if (page < 0) roam = false;   // pool dry: the light falls through to today's static re-bake
            // Pool exhausted: this slot bakes BOTH halves into the static array every mover frame -- the
            // per-slot fallback to pre-split behaviour. A mode change re-keys the slot, or a mover baked in
            // while combined would outlive the mode. A HIDDEN slot keeps its stored mode: the state nobody
            // sampled must not mark the slot changed.
            bool combined = !split || (hasDyn && page < 0);
            if (off) combined = _cubeCombined[slot];
            else if (_cubeCombined[slot] != combined) { slotChanged = true; _cubeCombined[slot] = combined; }

            // #241 term 3: a mover inside this light's reach -- under the split (#305), a STATIC-SET EVENT:
            // continuous motion lives in the dynamic page and must not re-bake the room. Scanned EVERY frame
            // whatever the verdict, because _cubeHadMover has to be current for the frame the mover leaves.
            bool hasMover = false;
            int[] evtIdx = combined ? _moverIdx : _sMoverIdx;
            int evtN = combined ? _moverCount : _sMoverCount;
            for (int q = 0; q < evtN && !hasMover; q++)
            {
                int m = evtIdx[q];
                float mx = _liveCx[m] - pos.X, my = _liveCy[m] - pos.Y, mz = _liveCz[m] - pos.Z;
                float mr = far + _liveR[m];
                hasMover = mx * mx + my * my + mz * mz <= mr * mr;
            }
            for (int q = 0; q < _ghostCount && !hasMover; q++)
            {
                float mx = _ghostCx[q] - pos.X, my = _ghostCy[q] - pos.Y, mz = _ghostCz[q] - pos.Z;
                float mr = far + _ghostR[q];
                hasMover = mx * mx + my * my + mz * mz <= mr * mr;
            }
            bool stale = ViewStale(invalidate, slotChanged, _cubeHadMover[slot], hasMover);
            _cubeHadMover[slot] = hasMover;

            // Off-screen: skip every bake for this light, static and dynamic. The mover scan above still
            // ran, so a stale verdict is remembered by unbaking the slot -- re-entry then re-bakes once
            // (slotChanged), or sooner through the warm-up drain above. A slot still holding a good bake
            // republishes it; one that does not stays at the INACTIVE wipe.
            if (off)
            {
                if (stale) _cubeBaked[slot] = false;
                if (_cubeBaked[slot])
                {
                    CubeLight[slot * 4] = _cubeKeyPos[slot * 3];
                    CubeLight[slot * 4 + 1] = _cubeKeyPos[slot * 3 + 1];
                    CubeLight[slot * 4 + 2] = _cubeKeyPos[slot * 3 + 2];
                    CubeLight[slot * 4 + 3] = _cubeKeyFar[slot];
                    CubeLightId[slot] = id;
                }
                done++;
                continue;
            }

            if (roam)
            {   // Demoted: the static faces hold NOTHING (min(vs, vd) collapses to the page), the slot is left
                // unbaked so the first still frame re-keys and re-bakes it, and the page below bakes everything.
                _cubeRoamSlots++;
                bool cleared = true;
                for (int f = 0; f < CubeFaces && !_cubeRoam[slot] && cleared; f++)
                    cleared = ClearLayer(CasterMap.Cubes, _gl.CubeTex, _cubeSize, slot * CubeFaces + f);
                _cubeBaked[slot] = false;
                if (!cleared) { scannedAll = false; break; }   // _cubeRoam stays false: the clear is retried
                _cubeRoam[slot] = true;
                CubeLight[slot * 4] = pos.X; CubeLight[slot * 4 + 1] = pos.Y; CubeLight[slot * 4 + 2] = pos.Z;
                CubeLight[slot * 4 + 3] = far;
                CubeLightId[slot] = id;
                done++;
            }
            else if (!stale)
            {   // The six layers still describe this world. Republish the slot unchanged -- the receiver
                _cubeRoam[slot] = false;
                // reads the light's geometry out of CubeLight, and it must keep matching the depth it samples.
                _cubeSkippedSlots++;
                CubeLight[slot * 4] = _cubeKeyPos[slot * 3]; CubeLight[slot * 4 + 1] = _cubeKeyPos[slot * 3 + 1];
                CubeLight[slot * 4 + 2] = _cubeKeyPos[slot * 3 + 2]; CubeLight[slot * 4 + 3] = _cubeKeyFar[slot];
                CubeLightId[slot] = id;
                done++;
            }
            else if (_staticCap > 0 && staticBaked >= _staticCap && _cubeBaked[slot] && !slotChanged && !invalidate && _cubeHeldRun[slot] < StaticHoldMax)
            {   // Held for the cap: the standing page stays published; hadMover = true is what carries the staleness.
                _cubeRoam[slot] = false; _cubeHadMover[slot] = true; _cubeHeldRun[slot]++;
                _cubeHeldSlots++; FrameZones.Count(FrameZones.Ev.CubeHeld);
                CubeLight[slot * 4] = _cubeKeyPos[slot * 3]; CubeLight[slot * 4 + 1] = _cubeKeyPos[slot * 3 + 1];
                CubeLight[slot * 4 + 2] = _cubeKeyPos[slot * 3 + 2]; CubeLight[slot * 4 + 3] = _cubeKeyFar[slot];
                CubeLightId[slot] = id;
                done++;
            }
            else
            {
                _cubeRoam[slot] = false; _cubeHeldRun[slot] = 0;
                _cubeBakedSlots++; staticBaked++; FrameZones.Count(FrameZones.Ev.CubeStatic);
                ChargeBake(WhyCube, invalidate, slotChanged);
                bool whole = true;
                for (int f = 0; f < CubeFaces && whole; f++)
                {
                    Vector3 fwd = ShadowMath.CubeFaceForward(f);
                    Matrix4x4 vp = ShadowMath.PointLightFaceViewProj(pos, f, CubeNearPlane, far);
                    whole = RenderView(CasterMap.Cubes, _gl.CubeTex, _cubeSize, slot * CubeFaces + f, &vp,
                                       fwd, pos, far, 0f, point: true,
                                       set: combined ? CasterGather.SetAll : CasterGather.SetStatic);
                    lightCreatures += _submitCreatures;
                }
                // A partial bake is not a bake: leaving the slot unbaked is what forces the whole cube again
                // next frame instead of letting five fresh faces and one stale one be published as fresh
                // forever.
                _cubeBaked[slot] = whole;
                // The ring is behind; the lights after this one would stall too. Leaving the scan INCOMPLETE
                // is load-bearing: the slots past this point never ran their mover test, so this frame's
                // evidence must not be consumed on their behalf.
                if (!whole) { scannedAll = false; break; }
                _cubeKeyId[slot] = id; _cubeKeyFar[slot] = far;
                _cubeKeyPos[slot * 3] = pos.X; _cubeKeyPos[slot * 3 + 1] = pos.Y; _cubeKeyPos[slot * 3 + 2] = pos.Z;
                frameCreatures += lightCreatures;
                if (rank == 0) { nearestCreatures = lightCreatures; nearestBaked = true; }
                CubeLight[slot * 4] = pos.X; CubeLight[slot * 4 + 1] = pos.Y; CubeLight[slot * 4 + 2] = pos.Z;
                CubeLight[slot * 4 + 3] = far;
                CubeLightId[slot] = id;
                done++;
            }
            // #305: the dynamic page bakes EVERY frame it exists -- no predicate, because its whole content
            // is the movers. Published only on a complete bake, for the same five-fresh-faces reason; an
            // unpublished page costs the movers' shadow for one frame, never a lie.
            // #336: ...per FACE, and only the faces an event sits in -- now, or last frame (a mover's old
            // face still shows it). A face with no event in either keeps its depth: a held-still caster's
            // shadow stands, an empty face stays cleared. The page is republished whatever was baked.
            if (page >= 0)
            {
                // From the position the receiver SAMPLES with (CubeLight): the published key, which the texel
                // slack lets differ from pos by under a texel. A roamer publishes pos itself.
                Vector3 dpos = roam ? pos : new Vector3(_cubeKeyPos[slot * 3], _cubeKeyPos[slot * 3 + 1], _cubeKeyPos[slot * 3 + 2]);
                int now = _dynEvents && !roam ? EventFaceMask(dpos, far) : AllFaces;
                bool forced = invalidate || slotChanged || roam || _rxForce;
                int bake = forced ? AllFaces : now | _dynPageMask[page];
                // The cadence ladder: a far light's page holds one frame, carrying its events forward. Keyed on
                // the FORCING terms, not on the mask -- with animated props on every face the mask is all six
                // nearly every frame, and the first cut read only 3,8 of 28 pages held.
                if (!forced && bake != 0 && rank >= _dynFull && _dynPageValid[page] && ((_frameSeq + slot) & 1) != 0)
                {
                    _dynPageMask[page] = bake;
                    CubeDynPage[slot] = page + 1;
                    _cubeDynSkipped++;
                    continue;
                }
                bool dynWhole = true;
                for (int f = 0; f < CubeFaces && dynWhole; f++)
                {
                    if ((bake >> f & 1) == 0) continue;
                    Matrix4x4 vp = ShadowMath.PointLightFaceViewProj(dpos, f, CubeNearPlane, far);
                    dynWhole = RenderView(CasterMap.CubeDyn, _gl.CubeDynTex, _cubeDynSize, page * CubeFaces + f, &vp,
                                          ShadowMath.CubeFaceForward(f), dpos, far, 0f, point: true,
                                          set: roam ? CasterGather.SetAll : CasterGather.SetDynamic, receiver: true);
                    _cubeDynBakedFaces++;
                }
                _dynPageMask[page] = dynWhole ? now : bake;
                if (!dynWhole) { scannedAll = false; break; }
                _dynPageValid[page] = true;
                CubeDynPage[slot] = page + 1;
                if (bake != 0) { _cubeDynBakedPages++; FrameZones.Count(FrameZones.Ev.CubeDyn); }
            }
            // CX-188 step 4: the PAIR TILES, in place of that page -- whatever the static branch above did,
            // because their whole content is one mover each. The cull is the DIRECTIONAL one round the mover's
            // own sphere (focus = its centre, reach = along = its radius), which is what keeps a tile to the
            // one caster it frames, and the dynamic set is the only population it may admit. The lane goes
            // NEGATIVE once they are queued: that is what makes every `uLocalDynPage[slot] > 0.5` reader treat
            // this light as pageless (it is) and what tells CxPairBase and cx_pairdebt where its run starts.
            // CX-188 CPU wave: ...and NOT every frame any more. A tile whose layer already holds this pair's
            // depth is held exactly as a cube's dynamic face is: keyed on the pair, and re-baked on an EVENT
            // inside its own cull volume -- which is why the event population here is the dynamic page's and
            // not just this tile's own mover (a second mover walking through the tile, a mover leaving the
            // dynamic set, a caster switched off) and why it unions this frame's with the baked frame's.
            if (pairBase >= 0)
            {
                int pn = PairShadows.PairCountOf(slot);
                bool pairWhole = true;
                bool pairForce = invalidate || slotChanged || _rxForce;
                for (int q = 0; q < pn && pairWhole; q++)
                {
                    int pi = pairBase + q;
                    PairShadows.PairView(pi, out Matrix4x4 pvp, out Vector3 at, out float r);
                    Vector3 dir = at - pos;
                    float dl = dir.Length();
                    Vector3 fwd = dl > 1e-4f ? dir / dl : new Vector3(0f, 0f, 1f);
                    // the SAME predicate the sun's fits take, against the same (focus, reach, along) this
                    // tile's own RenderView passes the gather -- so the tile's cull and its staleness test
                    // cannot disagree except in the direction that costs a bake
                    bool evt = EventInFit(at, fwd, r, 0f);
                    if (!PairShadows.TileStale(pi, evt, pairForce)) { PairShadows.TileHeld(pi); continue; }
                    pairWhole = RenderView(CasterMap.Other, PairShadows.Tex, PairShadows.TileSize, pi, &pvp,
                                           fwd, at, r, 0f, point: false, set: CasterGather.SetDynamic);
                    if (pairWhole) PairShadows.TileBaked(pi, evt);
                    _pairBakedTiles++;
                }
                if (!pairWhole) { scannedAll = false; break; }
                CubeDynPage[slot] = -(pairBase + 1);
            }
        }
        // Only a scan that reached every slot may consume the frame's invalidation: the break above leaves
        // the slots after it with no record of this frame's movers.
        _cubeScanned = scannedAll; _cubeRan = true;
        // a slot published under its ranked light this frame fades in; one that is not (unbaked, hidden with
        // no bake) holds, so a newcomer starts dark and rises only once there is depth to show
        for (int j = 0; j < _cubeLights; j++)
        {
            float distFade = 1f;
            if (pick[j] >= 0 && CubeLightId[j] != IntPtr.Zero)
            {
                _cubeFade[j] = MathF.Min(1f, _cubeFade[j] + (_fadeFrames > 0 ? 1f / _fadeFrames : 1f));
                if (cubeDist > 0f) distFade = 1f - SmoothStep(cubeDist * 0.75f, cubeDist, MathF.Sqrt(dist[pick[j]]));
            }
            CubeFade[j] = _cubeFade[j] * distFade;
        }
        CubeCount = done > 0 ? _cubeLights : 0;
        if (done > 0) _cubeStamp = _frameSeq;
        _cubeLightsSum += done;
    }

    /// <summary>Pool pages currently claimed -- the period line's occupancy figure. Cold.</summary>
    private static int DynPagesUsed()
    {
        int used = 0;
        for (int p = 0; p < _cubeDynPages && p < _dynPageSlot.Length; p++) if (_dynPageSlot[p] >= 0) used++;
        return used;
    }

    /// <summary>Fold one mover's box into at most <see cref="DynSpheres"/> AABBs (min xyz, max xyz): a new box
    /// while there is room, else the one whose squared extent grows least (which favours small boxes).
    /// Returns the box count.</summary>
    private static int AddMoverBox(MoverBox* b, int n, float x, float y, float z, float r)
    {
        int at = n;
        if (n == DynSpheres)
        {
            at = 0;   // a NaN mover compares false everywhere: without this it would write one box past the array
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                ref MoverBox e = ref b[i];
                float ux = MathF.Max(e.Max.X, x + r) - MathF.Min(e.Min.X, x - r), uy = MathF.Max(e.Max.Y, y + r) - MathF.Min(e.Min.Y, y - r),
                      uz = MathF.Max(e.Max.Z, z + r) - MathF.Min(e.Min.Z, z - r);
                float ex = e.Max.X - e.Min.X, ey = e.Max.Y - e.Min.Y, ez = e.Max.Z - e.Min.Z;
                float d = ux * ux + uy * uy + uz * uz - (ex * ex + ey * ey + ez * ez);
                if (d < best) { best = d; at = i; }
            }
            ref MoverBox t = ref b[at];
            t.Radius = -1f;
            t.Min = new Vector3(MathF.Min(t.Min.X, x - r), MathF.Min(t.Min.Y, y - r), MathF.Min(t.Min.Z, z - r));
            t.Max = new Vector3(MathF.Max(t.Max.X, x + r), MathF.Max(t.Max.Y, y + r), MathF.Max(t.Max.Z, z + r));
            return n;
        }
        ref MoverBox w = ref b[at];
        w.Min = new Vector3(x - r, y - r, z - r); w.Max = new Vector3(x + r, y + r, z + r); w.Radius = r;
        return n + 1;
    }

    /// <summary>Metres the hot-path ray test forgives round every mover sphere, before the angular share below.</summary>
    private const float HotPadM = 0.05f;
    /// <summary>How far, in dyn-page texels, the mover-visibility read (cx_localshadow <c>CxMoverVisQ</c>) can
    /// sample off the fragment's own ray: the soft disc's diagonal (sqrt 2), the hardware tap's bilinear texel
    /// (1), the normal offset at its slope ceiling (CX_LOCAL_NORMAL_OFFSET 1 + CX_LOCAL_SLOPE_OFFSET 0.75 x
    /// CX_LOCAL_SLOPE_MAX 2). A texel at major-axis distance m is 2 m / face size, so this is an ANGLE.</summary>
    internal const float HotPadTexels = 1.4143f + 1f + 1f + 0.75f * 2f;

    /// <summary>The slack a mover sphere <paramref name="d"/> metres from its light is published with: everything
    /// the shadow read can reach past the sphere's own cone. It was a flat 0.3 m, three times a rat's radius --
    /// a rat under a torch turned ten times its own shadow's area hot.</summary>
    internal static float HotPad(float d) => HotPadM + 2f * HotPadTexels / _cubeDynSize * d;

    /// <summary>Hot-path census since boot: slots publishing mover spheres, the spheres, the spheres whose light
    /// stands inside their own cone's apex reach (every fragment in the light's range is hot), roaming slots.
    /// Read by the period line and by <c>/alloy bench</c> per step.</summary>
    internal static long HotSlots, HotSpheres, HotEngulf, HotRoam;
    /// <summary>Casters admitted as moving, summed since boot (the bench's per-step "what the scene did").</summary>
    internal static long MoverFrames;

    /// <summary>Publish a slot's mover spheres, each padded by <see cref="HotPad"/> for its distance from the
    /// light: the boxes' bounding spheres, or -- with no box (roaming, or no mover found) -- the light's whole
    /// reach in sphere 0, which is the conservative answer.</summary>
    private static void WriteDynSpheres(int slot, int boxes, MoverBox* b, Vector3 pos, float far)
    {
        if (boxes == 0) HotRoam++; else HotSlots++;
        for (int i = 0; i < DynSpheres; i++)
        {
            int k = (slot * DynSpheres + i) * 4;
            if (boxes == 0)
            {
                CubeDynSphere[k] = pos.X; CubeDynSphere[k + 1] = pos.Y; CubeDynSphere[k + 2] = pos.Z;
                CubeDynSphere[k + 3] = i == 0 ? far : -1f;
                continue;
            }
            if (i >= boxes) { CubeDynSphere[k + 3] = -1f; continue; }
            ref MoverBox e = ref b[i];
            float hx = (e.Max.X - e.Min.X) * 0.5f, hy = (e.Max.Y - e.Min.Y) * 0.5f, hz = (e.Max.Z - e.Min.Z) * 0.5f;
            CubeDynSphere[k] = e.Min.X + hx; CubeDynSphere[k + 1] = e.Min.Y + hy; CubeDynSphere[k + 2] = e.Min.Z + hz;
            // a lone mover's box is its sphere's cube: the corner sphere (r x 1.73) tripled the shadow cone every
            // fragment under a hot light ray-tests, and with it the fragments that pay the hot path
            float r = e.Radius >= 0f ? e.Radius : MathF.Sqrt(hx * hx + hy * hy + hz * hz);
            float dx = e.Min.X + hx - pos.X, dy = e.Min.Y + hy - pos.Y, dz = e.Min.Z + hz - pos.Z, dd = dx * dx + dy * dy + dz * dz;
            r += HotPad(MathF.Sqrt(dd));
            CubeDynSphere[k + 3] = r;
            HotSpheres++;
            if (dd <= 2f * r * r) HotEngulf++;
        }
    }

    /// <summary>The lightmap bake's scratch cube -- the STATIC set's depth from one light, six views
    /// submitted synchronously into <paramref name="tex"/> (a one-cube cube array, <paramref name="size"/> a
    /// side) through the same gather the frame's cubes use, filtered to still casters. Every static light is
    /// baked shadowed this way. False = the caller leaves the light for a later frame.</summary>
    internal static bool BakeStaticCube(uint tex, int size, Vector3 pos, float near, float far)
    {
        near = MathF.Max(near, CubeNear);
        if (!_gStaged || tex == 0 || !(far > near)) return false;
        for (int f = 0; f < CubeFaces; f++)
        {
            Matrix4x4 vp = ShadowMath.PointLightFaceViewProj(pos, f, near, far);
            bool ok = CasterGather.Active
                // CX-50: full detail, thresholds 0. This cube is baked ONCE into the lightmap, so its
                // triangles are no part of the frame budget, and a coarser caster here would quietly move
                // baked lighting -- and the bake's cache keys say nothing about a detail level.
                ? GatherSubmit(tex, size, f, &vp, ShadowMath.CubeFaceForward(f), pos, far, 0f, true,
                               0f, 0f, CasterGather.SetStatic, false)
                : ClearLayer(CasterMap.Other, tex, size, f);
            if (!ok) return false;
        }
        return true;
    }
}
