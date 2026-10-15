using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The (static light, mover) PAIR tiles -- the planner and the atlas (ADR 0029). A creature under a
/// baked static light owes the lightmap a shadow the bake never saw; this replaces the light's dynamic cube
/// page with one small depth tile per (light, mover), budgeted per MOVER and emitted GROUPED BY CUBE SLOT,
/// which every reader depends on. A paired light publishes <c>CubeDynPage[slot]</c> as <c>-(first pair + 1)</c>.</summary>
internal static unsafe class PairShadows
{
    /// <summary>Pairs the atlas may ever hold. <see cref="CasterPass.MaxBakeViews"/> must cover this plus the
    /// frame's cube faces and cascades; 512 leaves it ~380 of headroom. ponytail: a bigger atlas wants that
    /// queue grown with it, and the layered flush then chunks more often.</summary>
    internal const int MaxPairs = 512;
    /// <summary>Floats per pair record, in <c>cx_pairbuf.glsl</c>'s std430 layout: mat4 vp, vec4 pr, vec4 col,
    /// vec4 sph, vec4 ix, vec4 fx.</summary>
    internal const int RecFloats = 36;
    /// <summary><c>cx_pairbuf.glsl</c>'s SSBO binding point, a shared one: every pair reader rebinds it through
    /// <see cref="BindAtlas"/>.</summary>
    internal const uint Binding = SsboSlots.Pair;
    /// <summary>Movers (bodies, and loose dynamic props) one light may pair with. Past it the newest go without
    /// that light's shadow, and the count says so out loud.</summary>
    internal const int MaxPerLight = CasterPass.DynSpheres;
    /// <summary>Candidate lights one frame ranks -- the cube budget, which is every light that could have a
    /// tile at all (a pair tap needs the slot's published geometry).</summary>
    private const int MaxLights = CasterPass.MaxCubes;

    /// <summary>Tile texels the mover sphere is inflated by before the volume test, on top of
    /// <see cref="ShadowMath.ConeSlack"/>: the soft disc (1), its bilinear texel (1) and the normal offset at
    /// its slope ceiling (<c>CX_PAIR_NORMAL_OFFSET</c> 1 + <c>CX_PAIR_SLOPE_OFFSET</c> 0.75 x
    /// <c>CX_PAIR_SLOPE_MAX</c> 2). Everything the tap can reach past the sphere's own cone.</summary>
    internal const float PadTexels = 1f + 1f + 1f + 0.75f * 2f;
    /// <summary>...and a flat metre allowance under it, as the dyn spheres take (<c>CasterPass.HotPadM</c>).</summary>
    internal const float PadMetres = 0.05f;

    /// <summary>Pairs this frame's plan committed -- what the bake walks and what the GPU table holds.</summary>
    internal static int Count { get; private set; }
    /// <summary>The depth atlas, 0 until the first claim allocated it. One 2D-array layer per pair.</summary>
    internal static uint Tex { get; private set; }
    /// <summary>A tile's side in texels.</summary>
    internal static int TileSize => _tile;

    /// <summary>Is the feature armed this frame? <c>lightmap.moverlights</c> above zero, the creature-shadow
    /// master on, the taps diagnostic not standing them down -- and the DEFERRED pass live, because the debt
    /// these tiles feed is taken there and nowhere else. One predicate, read by the planner and by the debt
    /// pass, so the bake and the receiver can never disagree about who owns a light's movers.</summary>
    internal static bool On => Lightmap.MoverLights.V > 0 && Lightmap.Movers.V && Lightmap.MoverTaps.V
                               && !_atlasFailed && !PairDebt.StoodDown && DeferredLights.Active;

    private static ExtenderLog? _log;
    private static int _tile = 128, _cap = 256;
    private static bool _booted, _atlasFailed;
    private static uint _buf;

    /// <summary>The GPU table: <c>uPairN</c> plus three pad words, then <see cref="RecFloats"/> per pair.</summary>
    private const int HeaderFloats = 4;
    private static readonly float[] _rec = new float[HeaderFloats + MaxPairs * RecFloats];
    private static readonly Matrix4x4[] _vp = new Matrix4x4[MaxPairs];
    /// <summary>Per pair, the mover sphere it frames (xyz centre, w the UNPADDED radius): the bake view's own
    /// cull volume, which is what keeps a tile to the one caster it frames.</summary>
    private static readonly Vector4[] _at = new Vector4[MaxPairs];
    /// <summary>Per pair, the light it is framed from and the mover it frames -- the two IDENTITIES the view
    /// matrix cannot tell apart (two creatures of one size standing in one another's place have the same
    /// <see cref="_vp"/>). Read only by the staleness key.</summary>
    private static readonly IntPtr[] _atLight = new IntPtr[MaxPairs];
    private static readonly int[] _atMover = new int[MaxPairs];
    /// <summary>Per cube slot, this frame's first pair plus one (0 = none). The table CubePass publishes as the
    /// negative lane of <c>CubeDynPage</c>.</summary>
    private static readonly int[] _base = new int[CasterPass.MaxCubes];

    // --- the staleness key, one row per ATLAS LAYER: what the layer's depth was baked from (CX-188 CPU wave)
    private static readonly IntPtr[] _kLight = new IntPtr[MaxPairs];
    private static readonly int[] _kMover = new int[MaxPairs];
    private static readonly Matrix4x4[] _kVp = new Matrix4x4[MaxPairs];
    private static readonly Vector4[] _kAt = new Vector4[MaxPairs];
    private static readonly bool[] _kBaked = new bool[MaxPairs], _kEvt = new bool[MaxPairs];
    private static readonly int[] _kSeq = new int[MaxPairs];
    /// <summary>The frame <see cref="Begin"/> opened, so a layer's key can be required to be LAST frame's.</summary>
    private static int _seq;

    // --- the plan's own frame state, all fixed-size and never allocated on a frame
    private static int _nLights;
    private static readonly int[] _lSlot = new int[MaxLights];
    private static readonly Vector4[] _lPr = new Vector4[MaxLights];     // the AREA light's (pos, R)
    private static readonly Vector3[] _lCol = new Vector3[MaxLights];
    private static readonly float[] _lFar = new float[MaxLights], _lLuma = new float[MaxLights];
    private static readonly int[] _lSet = new int[MaxLights];
    private static readonly int[] _lN = new int[MaxLights];
    private static readonly IntPtr[] _lId = new IntPtr[MaxLights];
    private static readonly Vector4[] _lMover = new Vector4[MaxLights * MaxPerLight];
    /// <summary>Per kept (light, mover), the mover's caster SLOT -- its identity across frames, which is what
    /// lets a light's run be emitted in an order that does not depend on the arrival order of the dynamic
    /// walk. Nothing drawn reads the order (a reader min()s the whole run), so sorting it is free stability.</summary>
    private static readonly int[] _lMoverId = new int[MaxLights * MaxPerLight];
    /// <summary>Per kept (light, mover), its <see cref="BudgetWeight"/>: the strength the tile's shadow is read at.</summary>
    private static readonly float[] _lW = new float[MaxLights * MaxPerLight];
    /// <summary>Movers a full light list turned away this frame (no shadow from that light), on the period line.</summary>
    private static int _planFull;

    private static int _gen, _upGen = -1;
    private static long _pairs, _slots, _refused, _overflow, _frames, _moversRanked, _crowded;
    /// <summary>Tiles whose layer already held this exact pair's depth and so were not queued, and movers the
    /// ranking never ran because every candidate light's list was already full.</summary>
    private static long _held, _late;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Diagnostic: <c>/shadow movers</c> also logs the next frame's pair plan.</summary>
    internal sealed class PlanDumpState { public bool Next; }
    internal static readonly PlanDumpState Dump = new();

    /// <summary>This frame's camera, for the shadow-in-view test; <c>Cull</c> false keeps every pair.</summary>
    internal sealed class PairViewState
    {
        public bool Cull;
        public Vector3 Pos, LookAt;
        public float Fovy, Aspect, Near, Far;
        public long Culled;
    }
    private static readonly PairViewState _view = new();

    /// <summary>Once per cube scan, after <see cref="Begin"/>: the camera the pairs' shadows are tested against,
    /// with the same inputs the cube scan's light cull takes.</summary>
    internal static void View(bool cull, Vector3 camPos, Vector3 camFwd, float fovy, float aspect, float near, float far)
    {
        _view.Cull = cull;
        _view.Pos = camPos; _view.LookAt = camPos + camFwd;
        _view.Fovy = fovy; _view.Aspect = aspect; _view.Near = near; _view.Far = far;
    }

    /// <summary>Can any pixel in the camera frustum lie in the shadow this mover casts from this light? The shadow
    /// is inside the cone from the light tangent to the mover sphere, between the sphere's near side and the
    /// light's reach; the test is that cone's bounding sphere, so it never culls a visible shadow.</summary>
    internal static bool ShadowInView(Vector3 light, float reach, Vector3 mover, float r,
                                      Vector3 camPos, Vector3 lookAt, float fovy, float aspect, float near, float far)
    {
        Vector3 d = mover - light;
        float dist = d.Length(), side2 = dist * dist - r * r;
        if (side2 < 1e-4f) return true;
        float a = MathF.Max(dist - r, 0f), b = MathF.Max(reach, dist + r);
        float rb = b * r / MathF.Sqrt(side2);
        Vector3 c = light + d * ((a + b) * 0.5f / dist);
        float half = (b - a) * 0.5f;
        float rad = (MathF.Sqrt(half * half + rb * rb) + PadMetres) * ShadowMath.FrustumCullSlack;
        return ShadowMath.SphereInCameraFrustum(c, rad, camPos, lookAt, fovy, aspect, near, far);
    }
    internal static void Note(string s) => _log?.Always("PairPlan:" + s);

    internal static void Forget()
    {
        Tex = 0; _buf = 0; _atlasFailed = false; _upGen = -1; Count = 0; _nLights = 0;
        Array.Clear(_base);
        ForgetKeys();
    }

    /// <summary>No atlas layer may be read as holding a bake: the storage is gone, or the frame's bakes never
    /// landed. Every tile re-bakes on the next committing frame.</summary>
    private static void ForgetKeys() => Array.Clear(_kBaked);

    internal static string Stats(int frames)
    {
        long p = _pairs, s = _slots, rf = _refused, o = _overflow, f = _frames, mr = _moversRanked, cr = _crowded;
        long hl = _held, lt = _late, oc = _view.Culled;
        _pairs = _slots = _refused = _overflow = _frames = _moversRanked = _crowded = _held = _late = 0;
        _view.Culled = 0;
        float n = MathF.Max(frames, 1);
        return _atlasFailed ? "pair tiles STOOD DOWN (no atlas)"
             : !On ? $"pair tiles off ({(Lightmap.MoverLights.V <= 0 ? "lightmap.moverlights 0" : "light.deferred off")})"
             : f == 0 ? "pair tiles idle"
             : $"pair tiles {p / n:F1}/frame over {s / n:F1} lights at {_tile}px "
             + $"({p * 100f / MathF.Max(f, 1) / _cap:F0}% of {_cap}, {hl / n:F1} HELD (the layer already held "
             + $"that pair), {mr / n:F1} movers ranked, {lt / n:F1} ranked nothing (every light full), "
             + $"{oc / n:F1} kept pairs whose shadow misses the view (not baked), "
             + $"{o / n:F2} lights past the atlas, {rf / n:F2} movers with a degenerate frame, {cr / n:F2} movers past a light's {MaxPerLight} (no shadow from it))";
    }

    // --- the planner, inside CasterPass's cube scan

    /// <summary>Once per cube scan, before <see cref="AddLight"/>: this frame's plan is empty and no slot is
    /// paired. A frame that never commits leaves every <c>CubeDynPage</c> lane positive, which is the whole off
    /// switch for every reader.</summary>
    internal static void Begin(int frameSeq)
    {
        // a table that HELD pairs and now does not must still be re-uploaded: uPairN is the gate every
        // reader takes, and a stale non-zero one would outlive the frame that earned it
        if (Count > 0) _gen++;
        _seq = frameSeq;
        Count = 0; _nLights = 0; _planFull = 0; _nFullLights = 0; _view.Cull = false;
        Array.Clear(_base);
        if (!_booted)
        {
            _booted = true;
            _tile = Math.Clamp(Lightmap.PairSize.Boot, 32, 512);
            _cap = Math.Clamp(Lightmap.PairCount.Boot, 16, MaxPairs);
        }
        if (On) _frames++;
    }

    /// <summary>The frame's bakes did not land (<see cref="CasterPass"/>'s layered flush failed): no light may
    /// be read as paired, so the lanes go positive and every one of them falls back to its page -- which this
    /// frame is also unbaked, so it is a one-frame blink of the creature shadows, never a shadow in the wrong
    /// place.</summary>
    internal static void Stand() { Count = 0; Array.Clear(_base); ForgetKeys(); }

    /// <summary>Offer one of this frame's cube-slotted lights as a pair candidate. Returns false when the light
    /// cannot be one -- the feature is off, it is not in the lightmap pages, it is ambient, it has no reach, or
    /// the candidate table is full -- in which case it keeps its dynamic page exactly as before. Cold-ish: one
    /// walk of the area light records per seated slot, over two flat arrays, allocation-free.</summary>
    internal static bool AddLight(int slot, IntPtr id, Vector3 pos, float far)
    {
        if (!On || slot < 0 || slot >= CasterPass.MaxCubes || _nLights >= MaxLights) return false;
        if (!(far > CasterPass.CubeNearPlane) || id == IntPtr.Zero) return false;
        IntPtr[] ids = FrameInputs.AreaLightId;
        bool[] stat = FrameInputs.AreaLightStatic, amb = FrameInputs.AreaLightAmbient;
        int n = Math.Min(FrameInputs.LightCount, ids.Length);
        for (int i = 0; i < n; i++)
        {
            if (ids[i] != id) continue;
            if (!stat[i] || amb[i]) return false;
            Vector4 pr = FrameInputs.AreaLightPosRange[i];
            if (!(pr.W > 0f)) return false;
            Vector3 col = FrameInputs.AreaLightColor[i];
            float luma = col.X * 0.299f + col.Y * 0.587f + col.Z * 0.114f;
            if (!(luma > 0f)) return false;
            int at = _nLights++;
            _lSlot[at] = slot; _lPr[at] = new Vector4(pos, pr.W); _lCol[at] = col; _lFar[at] = far;
            _lLuma[at] = luma; _lSet[at] = FrameInputs.AreaLightSetIndex[i]; _lN[at] = 0; _lId[at] = id;
            return true;
        }
        return false;
    }

    /// <summary>Rank this frame's candidate lights at one mover and keep the <c>lightmap.moverlights</c>
    /// strongest -- the engine's own curve times luma, which is how the walk itself ranks a light. A creature
    /// arrives here ONCE, as the box round all its parts, so a body keeps one set of lamps. Hot and
    /// allocation-free: K is at most 4, so the keep is a five-element insertion.</summary>
    internal static void AddMover(float cx, float cy, float cz, float r, int moverId)
    {
        if (!On || _nLights == 0 || !(r > 0f)) return;
        if (!float.IsFinite(cx + cy + cz + r)) return;
        int K = Math.Clamp(Lightmap.MoverLights.V, 0, 4);
        if (K == 0) return;
        // Every candidate light's list is already full, so the ranking below can only produce picks the keep
        // discards: 530 of them a frame on the plaza, each paying a walk of every candidate light. Exactly
        // equivalent -- the discard is the whole of what the walk would have achieved -- and the movers it
        // skips are counted on their own, because they are NOT "ranked" and the old counter said they were.
        if (_nFullLights >= _nLights) { _late++; return; }
        _moversRanked++;
        Rank(cx, cy, cz, r, K, moverId);
    }

    /// <summary>Candidate lights whose mover list has filled, so <see cref="AddMover"/> can tell that no
    /// ranking can still land anywhere. Maintained by the one place a list grows.</summary>
    private static int _nFullLights;

    /// <summary>How much stronger than the first light the budget turns away a kept light must be to cast at
    /// full strength, as a share of that light's score. Below it the shadow thins linearly to nothing at the
    /// cut, so a step that swaps which lamps a creature keeps fades the shadows across instead of popping them.</summary>
    internal const float BudgetFade = 0.5f;

    /// <summary>A kept pair's shadow strength, 0..1: 1 when nothing was turned away (<paramref name="cut"/> 0),
    /// else how far <paramref name="s"/> clears the first turned-away score, over <see cref="BudgetFade"/> of it.
    /// Zero exactly where the two scores meet, which is where the ranking swaps them.</summary>
    internal static float BudgetWeight(float s, float cut)
        => cut > 0f ? Math.Clamp((s - cut) / (BudgetFade * cut), 0f, 1f) : 1f;

    /// <summary>The ranking itself -- the one copy, so <see cref="SelfCheck"/> cannot pass against arithmetic
    /// the frame does not run. The K strongest candidates at (<paramref name="cx"/>, <paramref name="cy"/>,
    /// <paramref name="cz"/>), descending, each taking this mover into its own list with its
    /// <see cref="BudgetWeight"/>.</summary>
    private static void Rank(float cx, float cy, float cz, float r, int K, int moverId)
    {
        Vector2 at = AlloyLights.Atten;
        if (!(at.X > 0f)) at = new Vector2(1f, 1f);
        // the K strongest plus the first one turned away, descending: its score is where the fade bottoms out
        float* sc = stackalloc float[5];
        int* pick = stackalloc int[5];
        int have = 0, keep = K + 1;
        for (int i = 0; i < _nLights; i++)
        {
            Vector4 pr = _lPr[i];
            float dx = cx - pr.X, dy = cy - pr.Y, dz = cz - pr.Z;
            float dd = dx * dx + dy * dy + dz * dz;
            float f2 = dd / (pr.W * pr.W);
            if (f2 >= 1f) continue;
            // cx_lightbuild's Importance, term for term: the engine's curve at the mover times the light's luma
            float s = (1f - f2) / (at.X + at.Y * f2) * _lLuma[i];
            if (!(s > 0f)) continue;
            int j;
            if (have < keep) { j = have; have++; }
            else if (s > sc[keep - 1]) j = keep - 1;
            else continue;
            while (j > 0 && sc[j - 1] < s) { sc[j] = sc[j - 1]; pick[j] = pick[j - 1]; j--; }
            sc[j] = s; pick[j] = i;
        }
        float cut = have > K ? sc[K] : 0f;
        if (Dump.Next && moverId < 0)
        {
            var sb = new System.Text.StringBuilder($"  body {moverId} at ({cx:F1},{cy:F1},{cz:F1}) r {r:F2} ranks:");
            for (int j = 0; j < have; j++) sb.Append($" light {pick[j]} (slot {_lSlot[pick[j]]}) score {sc[j]:F3} w {BudgetWeight(sc[j], cut):F2}{(_lN[pick[j]] >= MaxPerLight ? " FULL" : "")};");
            Note(sb.ToString());
        }
        for (int j = 0; j < have && j < K; j++)
        {
            float w = BudgetWeight(sc[j], cut);
            if (!(w > 0f)) continue;
            int li = pick[j];
            // after the ranking, so a visible shadow keeps the weight it would have had with every pair kept
            if (_view.Cull && !ShadowInView(new Vector3(_lPr[li].X, _lPr[li].Y, _lPr[li].Z), _lPr[li].W, new Vector3(cx, cy, cz), r,
                                            _view.Pos, _view.LookAt, _view.Fovy, _view.Aspect, _view.Near, _view.Far))
            { _view.Culled++; continue; }
            // A full list drops the newest, counted: keeping the page instead would shadow none of them on baked
            // ground, which the deferred frame reads from the tiles alone (cx_pairdebt).
            if (_lN[li] >= MaxPerLight) { _planFull++; continue; }
            // Admitted on ARRIVAL, but INSERTED by the mover's identity, so the run's order is a function of
            // its membership and not of the order the dynamic walk happened to visit in. Nothing drawn reads
            // the order (a reader min()s the whole run), and a run that reorders every frame would make every
            // layer key miss.
            int b = li * MaxPerLight, k = _lN[li]++;
            while (k > 0 && _lMoverId[b + k - 1] > moverId)
            { _lMover[b + k] = _lMover[b + k - 1]; _lMoverId[b + k] = _lMoverId[b + k - 1]; _lW[b + k] = _lW[b + k - 1]; k--; }
            _lMover[b + k] = new Vector4(cx, cy, cz, r); _lMoverId[b + k] = moverId; _lW[b + k] = w;
            if (_lN[li] >= MaxPerLight) _nFullLights++;
        }
    }

    /// <summary>Turn the plan into this frame's pair table: per candidate light, a tile per mover it kept and
    /// can frame. An unframeable mover goes without that light's shadow, counted, and does not cost the
    /// light's other movers theirs. Emitted grouped by light (and so by cube slot), in the cube scan's RANK
    /// order, so when the atlas fills it is the farthest lights that keep their pages.</summary>
    internal static void Commit()
    {
        if (!On || _nLights == 0) return;
        if (_planFull > 0) _crowded += _planFull;
        for (int i = 0; i < _nLights; i++)
        {
            int boxes = _lN[i];
            if (boxes <= 0) continue;
            if (Count + boxes > _cap) { _overflow++; continue; }
            if (!EnsureAtlas()) return;
            int at = Count;
            Vector3 pos = new(_lPr[i].X, _lPr[i].Y, _lPr[i].Z);
            float far = _lFar[i];
            int kept = 0;
            for (int q = 0; q < boxes; q++)
            {
                Vector4 m = _lMover[i * MaxPerLight + q];
                var c = new Vector3(m.X, m.Y, m.Z);
                float dist = (c - pos).Length();
                float near = MathF.Max(dist - m.W, CasterPass.CubeNearPlane);
                if (!ShadowMath.PairViewProj(pos, c, m.W, near, far, out Matrix4x4 vp, out float halfSin))
                { _refused++; continue; }
                int pi = at + kept++;
                _vp[pi] = vp; _at[pi] = new Vector4(c, m.W);
                _atLight[pi] = _lId[i]; _atMover[pi] = _lMoverId[i * MaxPerLight + q];
                // the world size of one tile texel per unit of distance from the light: 2 tan(halfFov) / size
                float texel = 2f * halfSin / MathF.Sqrt(MathF.Max(1f - halfSin * halfSin, 1e-6f)) / _tile;
                // the volume test's sphere: the cone's own slack plus everything the tap reads past it
                float pad = PadMetres + PadTexels * texel * dist;
                fixed (float* d = _rec)
                {
                    float* w = &d[HeaderFloats + (long)pi * RecFloats];
                    Buffer.MemoryCopy(&vp, w, 64, 64);
                    w[16] = _lPr[i].X; w[17] = _lPr[i].Y; w[18] = _lPr[i].Z; w[19] = _lPr[i].W;
                    w[20] = _lCol[i].X; w[21] = _lCol[i].Y; w[22] = _lCol[i].Z; w[23] = texel;
                    w[24] = c.X; w[25] = c.Y; w[26] = c.Z; w[27] = m.W * ShadowMath.ConeSlack + pad;
                    w[28] = pi; w[29] = 1f / _tile; w[30] = _lSlot[i]; w[31] = _lSet[i];
                    w[32] = _lW[i * MaxPerLight + q]; w[33] = w[34] = w[35] = 0f;
                }
            }
            if (kept == 0) continue;
            Count = at + kept;
            _base[_lSlot[i]] = at + 1;
            _gen++; _pairs += kept; _slots++;
        }
        if (Count > 0) { _rec[0] = BitConverter.Int32BitsToSingle(Count); _gen++; }
        if (Dump.Next)
        {
            Dump.Next = false;
            for (int i = 0; i < _nLights; i++)
            {
                Note($" light {i} slot {_lSlot[i]} pos {_lPr[i]} luma {_lLuma[i]:F2} movers {_lN[i]} paired at {Pairs(_lSlot[i])}");
                for (int q = 0; q < _lN[i]; q++)
                {
                    Vector4 m = _lMover[i * MaxPerLight + q];
                    float dist = (new Vector3(m.X, m.Y, m.Z) - new Vector3(_lPr[i].X, _lPr[i].Y, _lPr[i].Z)).Length();
                    Note($"   mover {_lMoverId[i * MaxPerLight + q]} at {m} dist {dist:F2} w {_lW[i * MaxPerLight + q]:F2} sin {m.W / dist * ShadowMath.ConeSlack:F2}{(m.W / dist * ShadowMath.ConeSlack > ShadowMath.ConeMaxSin ? " TOO CLOSE" : "")}");
                }
            }
            Note($" committed {Count} pairs");
        }
    }

    /// <summary>This slot's first pair, or -1 when it has none and keeps its dynamic page. Read by
    /// <see cref="CasterPass"/> to publish the lane and to queue the bakes.</summary>
    internal static int Pairs(int slot)
        => slot >= 0 && slot < _base.Length && _base[slot] > 0 ? _base[slot] - 1 : -1;

    /// <summary>This slot is not paired after all -- the cube scan found it hidden or roaming after the plan
    /// committed, so it keeps its dynamic page. Its tiles stay in the table unbaked and unreferenced: the lane
    /// stays positive, and no reader can reach a pair whose slot has no lane.</summary>
    internal static void Drop(int slot)
    {
        if (slot >= 0 && slot < _base.Length) _base[slot] = 0;
    }

    /// <summary>How many pairs slot <paramref name="slot"/> owns -- its run's length in the table.</summary>
    internal static int PairCountOf(int slot)
    {
        int at = Pairs(slot);
        if (at < 0) return 0;
        int n = 0;
        while (at + n < Count && (int)_rec[HeaderFloats + (at + n) * RecFloats + 30] == slot) n++;
        return n;
    }

    /// <summary>One committed pair's bake view: the projection to record it with, and the mover sphere it
    /// frames (the view's own cull volume).</summary>
    internal static void PairView(int pi, out Matrix4x4 vp, out Vector3 at, out float radius)
    {
        vp = _vp[pi]; at = new Vector3(_at[pi].X, _at[pi].Y, _at[pi].Z); radius = _at[pi].W;
    }

    /// <summary>Must this pair's atlas layer be re-baked, or does it already hold this exact tile's depth? The
    /// key is every input the layer's depth is a function of: the light, the mover (identity, centre, radius),
    /// the view projection, and the LAYER, so an atlas reshuffle re-bakes rather than inheriting another
    /// pair's depth. A frame that dropped the pair leaves nothing to match, which keeps the union honest.</summary>
    internal static bool TileStale(int pi, bool evt, bool forced)
    {
        if (pi < 0 || pi >= Count) return true;
        if (forced || evt || _kEvt[pi] || !_kBaked[pi] || _kSeq[pi] != _seq - 1) return true;
        return _kLight[pi] != _atLight[pi] || _kMover[pi] != _atMover[pi]
            || _kAt[pi] != _at[pi] || _kVp[pi] != _vp[pi];
    }

    /// <summary>The tile baked: the layer holds this pair's depth, keyed, together with the events it was
    /// baked among -- so the next frame re-bakes once more to clear whatever moved out of it.</summary>
    internal static void TileBaked(int pi, bool evt)
    {
        if (pi < 0 || pi >= Count) return;
        _kLight[pi] = _atLight[pi]; _kMover[pi] = _atMover[pi]; _kAt[pi] = _at[pi]; _kVp[pi] = _vp[pi];
        _kBaked[pi] = true; _kEvt[pi] = evt; _kSeq[pi] = _seq;
    }

    /// <summary>The tile was not queued because its layer already held it: carry the key forward to this
    /// frame, so the next frame may match against it in turn.</summary>
    internal static void TileHeld(int pi)
    {
        if (pi < 0 || pi >= Count) return;
        _kSeq[pi] = _seq; _held++;
    }

    private static bool EnsureAtlas()
    {
        if (Tex != 0) return true;
        if (_atlasFailed) return false;
        uint t; Gl.CreateTextures(Gl.TEXTURE_2D_ARRAY, 1, &t);
        Gl.DrainErrors();   // an earlier pass's error must not be read as this allocation's
        Gl.TextureStorage3D(t, 1, Gl.DEPTH_COMPONENT24, _tile, _tile, _cap);
        if (Gl.GetError() != 0)
        {
            Gl.DeleteTextures(1, &t);
            _atlasFailed = true;
            _log?.Always($"PairShadows: a {_tile}x{_tile}x{_cap} depth array could not be allocated -- hot "
                       + "static lights keep their dynamic cube pages and the deferred frame owes them nothing "
                       + "(lightmap.paircount / lightmap.pairsize size it)");
            return false;
        }
        // LINEAR + depth-compare IS the filter: one hardware 2x2 comparison per fetch, as the cubes take it
        Gl.TextureParameteri(t, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
        Gl.TextureParameteri(t, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        Gl.TextureParameteri(t, Gl.TEXTURE_COMPARE_MODE, (int)Gl.COMPARE_REF_TO_TEXTURE);
        Gl.TextureParameteri(t, Gl.TEXTURE_COMPARE_FUNC, (int)Gl.LEQUAL);
        Tex = t;
        ForgetKeys();   // fresh storage holds no bake, whatever the keys say (#241's rule for the cubes)
        _log?.Info($"PairShadows: pair atlas {_cap} x {_tile}x{_tile} depth "
                 + $"({(long)_cap * _tile * _tile * 4 / (1024 * 1024)} MB; lightmap.paircount / lightmap.pairsize)");
        return true;
    }

    /// <summary>From <see cref="AlloyLights.BindForDraw"/> -- every program that lights, plus the debt
    /// dispatch: the pair table and the tile atlas. Bound whether or not anything is paired, because the
    /// table's own <c>uPairN</c> is the gate <c>CxPairBase</c> reads and a stale binding would make it
    /// garbage. Leaves unit 0 current.</summary>
    internal static void BindAtlas()
    {
        Gl.EnsureStorage(ref _buf, (long)(HeaderFloats + MaxPairs * RecFloats) * sizeof(float), false);
        if (_gen != _upGen)
        {
            _upGen = _gen;
            // Count 0 still uploads the header: uPairN has to read zero on a frame that paired nothing.
            if (Count <= 0) _rec[0] = BitConverter.Int32BitsToSingle(0);
            fixed (float* r = _rec)
                Gl.NamedBufferSubData(_buf, IntPtr.Zero,
                                      (IntPtr)((long)(HeaderFloats + Math.Max(Count, 0) * RecFloats) * sizeof(float)), r);
        }
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, Binding, _buf);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.PairMap);
        Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, Tex);
        Gl.ActiveTexture(Gl.TEXTURE0);
    }

    /// <summary>Off-device: the pair frustum frames its mover and stops at the light's reach, the per-mover
    /// budget keeps the strongest lights, the table's grouping holds, the GLSL contract matches these
    /// constants, and the shadow-in-view test agrees with hand-worked cones.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        var light = new Vector3(0f, 0f, 0f);
        var at = new Vector3(10f, 0f, 2f);
        const float r = 1f, far = 30f;
        float near = MathF.Max((at - light).Length() - r, 0.05f);
        check.Expect(ShadowMath.PairViewProj(light, at, r, near, far, out Matrix4x4 vp, out float hs),
                     "a mover 10 m from its light frames");
        Vector3 c = ShadowMath.WorldToLightNdc(vp, at);
        check.Expect(MathF.Abs(c.X) < 1e-3f && MathF.Abs(c.Y) < 1e-3f && c.Z > -1f && c.Z < 1f,
                     $"the mover's centre is mid-tile ({c.X:F3},{c.Y:F3},{c.Z:F3})");
        // the rim lands on whichever tile axis the light's own basis puts it on, so the test is its radius
        Vector3 rim = ShadowMath.WorldToLightNdc(vp, at + new Vector3(0f, r, 0f));
        float rimR = MathF.Sqrt(rim.X * rim.X + rim.Y * rim.Y);
        check.Expect(rimR > 0.5f && rimR < 1f, $"the sphere's rim is inside the tile with room for the disc ({rimR:F3})");
        Vector3 past = ShadowMath.WorldToLightNdc(vp, Vector3.Normalize(at) * (far + 1f));
        check.Expect(past.Z > 1f, $"a point past the light's reach is past the far plane ({past.Z:F3})");
        // hand-worked: 8 m at 10 m is sin 0.92 past the 0.85 ceiling, so the view clamps there instead of refusing
        check.Expect(ShadowMath.PairViewProj(light, at, 8f, near, far, out Matrix4x4 wide, out float hsWide) && hsWide == 0.85f,
                     $"a mover too close to fit frames at the cone ceiling (sin {hsWide:F3})");
        Vector3 mid = ShadowMath.WorldToLightNdc(wide, at);
        check.Expect(MathF.Abs(mid.X) < 1e-3f && MathF.Abs(mid.Y) < 1e-3f, "...still aimed at the mover's centre");
        // a lamp inside the body's sphere looks straight down: a point 3 m below it is mid-tile
        check.Expect(ShadowMath.PairViewProj(light, new Vector3(0.5f, 0f, 0f), 1f, 0.05f, far, out Matrix4x4 down, out _),
                     "a mover round its light still frames");
        Vector3 below = ShadowMath.WorldToLightNdc(down, new Vector3(0f, 0f, -3f));
        check.Expect(MathF.Abs(below.X) < 1e-3f && MathF.Abs(below.Y) < 1e-3f && below.Z > -1f && below.Z < 1f,
                     $"...looking straight down ({below.X:F3},{below.Y:F3},{below.Z:F3})");
        check.Expect(hs > r / (at - light).Length(), "the framed half-angle carries the disc's slack");

        check.Expect(MaxPairs <= 1024 - 64 * 6, "the atlas fits inside CasterPass's bake-view queue beside the cube faces");
        string buf = ShaderSource.Get("cx_pairbuf.glsl"), lib = ShaderSource.Get("cx_pairshad.glsl");
        check.Expect(ShaderContract.RecordSafe(buf, "CxPair", RecFloats),
                     "the GLSL CxPair declaration does not add up to the record stride C# stages");
        check.Expect(buf.Contains($"layout(std430, binding = {Binding}) readonly buffer CxPairBuf")
                  && buf.Contains("struct CxPair { mat4 vp; vec4 pr; vec4 col; vec4 sph; vec4 ix; vec4 fx; };"),
                     "the pair table's binding and layout are the ones C# stages");
        check.Expect(lib.Contains($"layout(binding = {TextureSlots.PairMap}) uniform sampler2DArrayShadow uPairMap;"),
                     "the atlas is on the unit C# binds");
        // the lane that says "paired": negative, so every existing `< 0.5` reader is left alone by construction
        check.Expect(lib.Contains("int at = pg < -0.5 ? int(-pg) - 1 : -1;"),
                     "CxPairBase decodes the NEGATIVE CubeDynPage lane");
        string ls = ShaderSource.Get("cx_localshadow.glsl");
        check.Expect(ls.Contains("vd = CxPairVis(slot, worldPos, N, ndlRaw, soft);"),
                     "an unbaked receiver gets its movers back through the pair tiles");
        // ...and that tap is GUARDED. Unguarded it would inline into every program that lights, which is
        // exactly what the reverted attempt (504b6e0) measured as 7.8 ms on the feature's own OFF baseline.
        check.Expect(ls.Contains("#ifdef CX_PAIR_RECV") && ls.IndexOf("#ifdef CX_PAIR_RECV", StringComparison.Ordinal)
                                                          < ls.IndexOf("vd = CxPairVis", StringComparison.Ordinal),
                     "the pair tap is behind CX_PAIR_RECV, so only the deferred stages compile it");
        check.Expect(ShaderSource.Get("cx_deferred_cs.glsl").Contains("#define CX_PAIR_RECV 1")
                  && ShaderSource.Get("cx_pairdebt.glsl").Contains("#define CX_PAIR_RECV 1")
                  // step 3's record pass is the deferred path's shadow verdict for an UNBAKED receiver, so it
                  // is the third stage that needs the tap -- CxLocalShadowQ is not what the walk calls there
                  && ShaderSource.Get("cx_defshadow_cs.glsl").Contains("#define CX_PAIR_RECV 1")
                  && !ShaderStages.Source(AlloyRenderer.ShdLitFs).Contains("#define CX_PAIR_RECV"),
                     "CX_PAIR_RECV is the three deferred stages' and not the forward lit stage's");
        check.Expect(ls.Contains("vd = CxPairVis(slot, worldPos, N, ndlRaw, false);"),
                     "the FAST filter takes the pair tap too, which is where the deferred record comes from");
        check.Expect(ShaderSource.Get("cx_deferred_cs.glsl").Contains("#define CX_NO_HOT 1"),
                     "the deferred stage compiles the walk's hot branch out");
        check.Expect(ShaderSource.Get("cx_lightfrag.glsl").Contains("#ifdef CX_NO_HOT")
                  && ShaderSource.Get("cx_lightfrag.glsl").Contains("const bool hot = false;"),
                     "cx_lightfrag honours CX_NO_HOT");

        // the per-mover budget itself, on a made-up frame: three lights of falling luma round one mover, and
        // the two strongest are the ones kept. Driven through the real Add/Commit path with no GL in it.
        check.Expect(BudgetCheck(out string why), why);
        // hand-worked: lamp at the origin, reach 10, a 1 m mover at x=5 -- its shadow runs x=4..10 along +X
        var lamp = Vector3.Zero; var body = new Vector3(5f, 0f, 0f);
        check.Expect(ShadowInView(lamp, 10f, body, 1f, new Vector3(5f, -10f, 0f), new Vector3(5f, -9f, 0f), 45f, 1f, 0.1f, 100f),
                     "a camera looking at the mover sees its shadow");
        check.Expect(!ShadowInView(lamp, 10f, body, 1f, new Vector3(5f, -10f, 0f), new Vector3(5f, -11f, 0f), 45f, 1f, 0.1f, 100f),
                     "a camera facing away from the lamp's whole reach does not");
        // the mover sits 4 m left of a camera at (9,-3) looking +Y (outside a 45deg cone at 3 m), its shadow's far end dead ahead
        check.Expect(ShadowInView(lamp, 10f, body, 1f, new Vector3(9f, -3f, 0f), new Vector3(9f, -2f, 0f), 45f, 1f, 0.1f, 100f),
                     "an off-screen mover whose shadow falls on screen is kept");
        check.Expect(!ShadowInView(lamp, 10f, body, 1f, new Vector3(-20f, 0f, 0f), new Vector3(-21f, 0f, 0f), 45f, 1f, 0.1f, 100f),
                     "a camera behind the lamp looking away culls it");
        KeyCheck(check);
        log.Info(check.Ok ? "PairShadows.SelfCheck: passed" : "PairShadows.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>The layer key, off the committed table: a tile whose every input is unchanged is HELD, and
    /// each input ON ITS OWN flips it back to stale -- which is the whole of what makes the skip safe. Drives
    /// <see cref="TileStale"/> / <see cref="TileBaked"/> exactly as the frame's bake loop does, with no GL and
    /// no atlas.</summary>
    private static void KeyCheck(Checker check)
    {
        int savedCount = Count, savedSeq = _seq;
        var vp = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        Count = 2;
        void Seed(int seq)
        {
            _seq = seq;
            _vp[0] = vp; _at[0] = new Vector4(1f, 2f, 3f, 0.5f);
            _atLight[0] = (IntPtr)0x1234; _atMover[0] = 7;
        }
        _kBaked[0] = _kBaked[1] = false;
        Seed(10);
        check.Expect(TileStale(0, false, false), "an unbaked layer is stale");
        TileBaked(0, false);
        Seed(11);
        check.Expect(!TileStale(0, false, false), "the same pair on the very next frame is HELD");
        // ...and one input at a time, each off that held baseline
        Seed(11); _atLight[0] = (IntPtr)0x5678;
        check.Expect(TileStale(0, false, false), "another light framing the same mover is stale");
        Seed(11); _atMover[0] = 8;
        check.Expect(TileStale(0, false, false), "another mover of the same size in the same place is stale");
        Seed(11); _at[0] = new Vector4(1f, 2f, 3f, 0.6f);
        check.Expect(TileStale(0, false, false), "a grown mover sphere is stale");
        Seed(11); _at[0] = new Vector4(1.1f, 2f, 3f, 0.5f);
        check.Expect(TileStale(0, false, false), "a moved mover centre is stale");
        Seed(11); _vp[0] = Matrix4x4.CreateTranslation(1f, 2f, 3.5f);
        check.Expect(TileStale(0, false, false), "a re-framed view projection is stale");
        Seed(11);
        check.Expect(TileStale(0, true, false), "an event inside the tile's own cull volume is stale");
        check.Expect(TileStale(0, false, true), "a forced frame (invalidate, re-key, rx force) is stale");
        Seed(12);
        check.Expect(TileStale(0, false, false), "a frame's gap since the key is stale");
        // the event union, as the dynamic page's per-face mask takes it: the frame AFTER an event bakes once
        // more, because that is what clears whatever moved out of the tile
        Seed(13); TileBaked(0, true);
        Seed(14);
        check.Expect(TileStale(0, false, false), "the frame after an event bakes again to clear it");
        TileBaked(0, false);
        Seed(15);
        check.Expect(!TileStale(0, false, false), "...and the frame after that is held again");
        // an atlas reshuffle: the same pair at another LAYER inherits nothing
        _vp[1] = _vp[0]; _at[1] = _at[0]; _atLight[1] = _atLight[0]; _atMover[1] = _atMover[0];
        check.Expect(TileStale(1, false, false), "the same pair moved to another atlas layer is stale");
        check.Expect(TileStale(5, false, false), "a layer past the committed count is stale");
        _kBaked[0] = _kBaked[1] = false;
        Count = savedCount; _seq = savedSeq;
    }

    /// <summary>The ranking alone, off the frame-input arrays: three lights at the same distance with falling
    /// luma, one mover between them, <c>lightmap.moverlights</c> 2 -- the two brightest must be the ones that
    /// keep it. No GL: the atlas is never touched because <see cref="On"/> is false in a headless check, so
    /// this drives <see cref="AddMover"/>'s arithmetic directly.</summary>
    private static bool BudgetCheck(out string why)
    {
        _nLights = 0; _planFull = 0; _nFullLights = 0;
        Vector2 at = AlloyLights.Atten;
        if (!(at.X > 0f)) at = new Vector2(1f, 1f);
        for (int i = 0; i < 3; i++)
        {
            _lSlot[i] = i; _lPr[i] = new Vector4(i * 4f, 0f, 0f, 20f); _lFar[i] = 20f;
            _lCol[i] = new Vector3(1f - 0.3f * i); _lLuma[i] = 1f - 0.3f * i; _lSet[i] = i; _lN[i] = 0;
        }
        _nLights = 3;
        // the frame's own ranking with the On gate (false headless) stepped round: the mover sits 4 m from
        // light 1, so distance alone would rank it first -- luma is what must decide, as the walk's does
        Rank(4f, 0f, 0f, 0.5f, 2, 0);
        bool ok = _lN[0] == 1 && _lN[1] == 1 && _lN[2] == 0;
        why = ok ? "the per-mover budget keeps the two strongest lights by curve x luma, fades at its cut and counts what a full light turns away"
                 : $"the per-mover budget kept {_lN[0]}/{_lN[1]}/{_lN[2]} instead of 1/1/0";
        // ...and both clear the turned-away light by more than the fade band, so neither is thinned
        if (ok && !(_lW[0] == 1f && _lW[MaxPerLight] == 1f))
        { ok = false; why = $"kept lights well clear of the cut were thinned to {_lW[0]:F2}/{_lW[MaxPerLight]:F2}"; }
        // the fade: nothing at the cut, all of it past the band, monotonic between -- so a swap is continuous
        if (ok && !(BudgetWeight(0.4f, 0.4f) == 0f && BudgetWeight(0.4f * (1f + BudgetFade), 0.4f) == 1f
                    && BudgetWeight(0.5f, 0.4f) is > 0f and < 1f && BudgetWeight(0.1f, 0f) == 1f))
        { ok = false; why = "the budget weight is not 0 at the cut and 1 past the fade band"; }
        // ...and a light barely clearing the one it turned away casts a thin shadow, not a full one
        _lN[0] = _lN[1] = _lN[2] = 0;
        // lights 0 and 2 sit at one distance, so luma alone orders them whatever the attenuation pair is
        _lLuma[1] = 0f; _lLuma[2] = 0.69f;
        Rank(4f, 0f, 0f, 0.5f, 1, 0);
        float near0 = _lN[0] == 1 ? _lW[0] : -1f;
        if (ok && !(near0 > 0f && near0 < 1f))
        { ok = false; why = $"a light barely over the budget's cut cast at {near0:F2} instead of thinly"; }
        _lLuma[1] = 0.7f; _lLuma[2] = 0.4f;
        // a full light list turns the newest away, counted, and keeps the movers it already holds
        _lN[0] = _lN[1] = _lN[2] = 0; _nFullLights = 0; _planFull = 0;
        for (int q = 0; q <= MaxPerLight; q++) Rank(0.5f, 0f, 0f, 0.5f, 1, q);
        if (ok && !(_lN[0] == MaxPerLight && _planFull == 1))
        { ok = false; why = $"a light past {MaxPerLight} movers kept {_lN[0]} and counted {_planFull}"; }
        _planFull = 0;
        // ...and a light's run comes out in its movers' IDENTITY order whatever order they arrived in: a run
        // that reorders when the dynamic walk does would make every layer key miss (CX-188 CPU wave).
        _lN[0] = 0; _nFullLights = 0;
        Rank(0.5f, 0f, 0f, 0.5f, 1, 9);
        Rank(0.5f, 0f, 0f, 0.5f, 1, 4);
        if (ok && !(_lN[0] == 2 && _lMoverId[0] == 4 && _lMoverId[1] == 9))
        { ok = false; why = $"the light's run came out {_lMoverId[0]},{_lMoverId[1]} instead of 4,9 ({_lN[0]} kept)"; }
        _nLights = 0; _nFullLights = 0; Array.Clear(_base); Count = 0;
        return ok;
    }

}
