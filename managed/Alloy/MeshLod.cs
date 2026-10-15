using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The <c>lod</c> group: automatic mesh detail. Touched from <see cref="RenderCommands.Register"/>
/// so the group declares at Initialize on the main thread, as every other Alloy group does.</summary>
internal static class Lod
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<float> Px1, Px2, Bias;

    static Lod()
    {
        G = KnobGroup.Declare("lod", "Graphics", "Quality", master: "Automatic mesh detail",
            masterHelp: "Distant walls and floors are drawn from a simplified copy of their own mesh. "
                      + "Off draws every model at full detail at every distance, which is what the game does.",
            masterTier: Tier.ChatOnly, order: 304, title: "Model detail",
            masterChanged: CasterPass.ShadowInvalidate);
        G.Status = MeshLod.Describe;
        Px1 = G.Slider("px1", "Full detail above", 50f, 2000f, 10f, 500f, "px", Tier.Advanced,
                       help: "A mesh wider than this on screen is drawn exactly as authored.",
                       changed: CasterPass.ShadowInvalidate)
               .Ladder(Quality.Detail, 800f, 650f, 500f, 300f);
        Px2 = G.Slider("px2", "Lowest detail below", 10f, 1000f, 10f, 150f, "px", Tier.Advanced,
                       help: "Below this a mesh is drawn from its smallest copy. Between the two it takes the middle one.",
                       changed: CasterPass.ShadowInvalidate)
               .Ladder(Quality.Detail, 300f, 220f, 150f, 80f);
        // every row here feeds the SHADOW views' thresholds too, and no static bake is keyed on them
        Bias = G.Slider("bias", "Detail bias", 0.25f, 4f, 0.05f, 1f, tier: Tier.ChatOnly,
                        help: "Scales both thresholds' effect: above 1 keeps detail further away, below 1 drops it sooner.",
                        changed: CasterPass.ShadowInvalidate);
    }
}

/// <summary>
/// Automatic mesh LOD. NWN models carry no LOD chain, so a mesh draws its full triangle count at every distance;
/// this generates two simplified INDEX RUNS per eligible mesh -- roughly half and a fifth of its triangles --
/// into one Alloy-owned element buffer, and <see cref="Level"/> picks one by the part's projected screen radius.
/// Reindex only: no vertex is touched, so an absent level simply falls back to the finer run.
/// </summary>
internal static unsafe class MeshLod
{
    /// <summary>Detail levels: the engine's own run, then the two generated ones.</summary>
    internal const int Levels = 3;
    /// <summary>Triangle fraction each generated level aims at.</summary>
    internal const float Target1 = 0.5f, Target2 = 0.2f;
    /// <summary>Error budget per level as a fraction of the mesh's squared bounding-box diagonal. A collapse
    /// past it is refused, so a mesh whose shape cannot survive thinning keeps the triangles it has.</summary>
    internal const float Error1 = 0.0004f, Error2 = 0.004f;
    /// <summary>Triangles under which a mesh is not worth a level of its own: two extra runs and a batch cut
    /// cost more than the triangles they save.</summary>
    internal const int MinTris = 200;
    /// <summary>A generated run has to retire this fraction of the triangles to be kept at all; otherwise the
    /// level is left absent and the draw stays on the engine's range.</summary>
    internal const float MinSaving = 0.15f;

    /// <summary>Meshes waiting on the worker: a full queue refuses the offer without creating a table entry,
    /// so the next enrollment offers the same mesh again.</summary>
    private const int MaxPending = 256;
    /// <summary>Meshes the table may hold. An area's tileset is a few thousand distinct ranges.</summary>
    private const int MaxEntries = 1 << 14;
    /// <summary>Indices the shared element buffer may hold before the arena stops accepting runs (16 MiB).</summary>
    private const int ArenaCeiling = 8 << 20;

    private static ExtenderLog? _log;

    /// <summary>One mesh's generated runs. <see cref="Off1"/>/<see cref="Off2"/> are BYTE offsets into
    /// <see cref="_ebo"/>, which is what <c>glMultiDrawElementsBaseVertex</c> takes; a zero count is a level
    /// the simplifier declined and the selection falls through to the next coarser one, or to the engine's.</summary>
    private struct Entry : ISlotEntry
    {
        internal nint Off1, Off2;
        internal int Cnt1, Cnt2;
        internal bool Done;
        public ulong Sig { get; set; }
    }

    /// <summary>One mesh handed to the worker and back. The arrays are pooled by the table -- an area's
    /// enrollment offers thousands of these.</summary>
    private sealed class Job
    {
        internal long Key;
        internal ulong Sig;
        internal int IdxCount, VertCount, Out1N, Out2N;
        internal ushort[] Idx = Array.Empty<ushort>();
        internal float[] Pos = Array.Empty<float>();
        internal ushort[] Out1 = Array.Empty<ushort>();
        internal ushort[] Out2 = Array.Empty<ushort>();
    }

    private static readonly SlotJobTable<Job, Entry> _jobs = new(MaxEntries, MaxPending);
    private static Dictionary<long, Entry> _table => _jobs.Entries;

    private static ushort[] _mirror = Array.Empty<ushort>();
    private static int _used, _uploaded;
    private static uint _ebo;
    private static bool _arenaFull;

    private static int _offered, _generated, _declined, _dropped;
    /// <summary>Offers refused under <see cref="MinTris"/>: the census that says whether this area has anything to simplify.</summary>
    private static int _small;
    private static int _logLeft = 8;
    private static long _srcTris, _lod1Tris, _lod2Tris;
    private static readonly long[] _drawnTris = new long[Levels];

    /// <summary>Is a generated level available to pick this frame? False leaves every draw on the engine's own
    /// range, which is vanilla detail -- the stand-down, not a second code path.</summary>
    internal static bool Armed => _ebo != 0 && On;

    /// <summary>The master knob alone: the staging walk picks a level whenever this is on, because
    /// <see cref="MeshChart"/>'s levels live in its own arena and never arm this class's buffer.</summary>
    internal static bool On => Lod.G.Master is { V: true };

    /// <summary>Start the worker and read the boot knob. Once, from the pass roll-call.</summary>
    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _ = Lod.G;
        if (_jobs.Running) return;
        _jobs.Start("cx-meshlod", Work);
        log.Info("MeshLod: armed -- eligible static meshes get two generated index runs (lod.enabled=0 draws every mesh as authored)");
    }

    private static long KeyOf(int slot, int item) => SlotJobTable<Job, Entry>.KeyOf(slot, item);

    /// <summary>LOD runs on file, claimed or landed -- the area edge's tally.</summary>
    internal static int TableCount => _table.Count;

    /// <summary>Should the enrollment walk copy this range out for the worker? Asked before anything is read,
    /// so a mesh already on file, already queued, or past a budget costs one dictionary probe.</summary>
    internal static bool Wanted(int slot, int item, int idxCount, ulong sig)
    {
        if (!_jobs.Running || _arenaFull || idxCount > MeshSimplify.MaxCorners || !Lod.G.Master!.V) return false;
        if (idxCount < MinTris * 3) { _small++; return false; }
        if (_table.TryGetValue(KeyOf(slot, item), out Entry have))
        {
            if (have.Sig == sig) return false;
            _table.Remove(KeyOf(slot, item));
        }
        if (_table.Count >= MaxEntries) return false;
        return _jobs.QueueHasRoom;
    }

    /// <summary>Copy one part's index range and the positions it addresses off the pools' host shadows and
    /// queue it. Called from the enrollment walk, on whatever thread that runs -- it reads engine memory and
    /// allocates, and does neither on a frame path.</summary>
    internal static void Offer(int slot, int item, byte* elemHost, long first, int count,
                               byte* vertHost, in VertexStream pos, int baseVertex, ulong sig)
    {
        if (elemHost == null || vertHost == null || count < 3 || count % 3 != 0) return;
        if (pos.Type != Gl.FLOAT || pos.Size != 3) return;
        var src = new HostIndices(elemHost, first);
        int maxI = -1;
        for (int i = 0; i < count; i++) { int v = src[i]; if (v > maxI) maxI = v; }
        int verts = maxI + 1;
        if (verts <= 0 || verts > MeshSimplify.MaxVerts) return;

        Job j = _jobs.Rent();
        j.Key = KeyOf(slot, item); j.Sig = sig;
        j.IdxCount = count; j.VertCount = verts; j.Out1N = 0; j.Out2N = 0;
        Grow(ref j.Idx, count); Grow(ref j.Out1, count); Grow(ref j.Out2, count);
        if (j.Pos.Length < verts * 3) j.Pos = new float[verts * 3];
        for (int i = 0; i < count; i++) j.Idx[i] = src[i];
        for (int i = 0; i < count; i++)
        {
            int v = src[i];
            ref Vector3 p = ref new HostVertices(vertHost, pos).Pos(baseVertex + v);
            j.Pos[v * 3] = p.X; j.Pos[v * 3 + 1] = p.Y; j.Pos[v * 3 + 2] = p.Z;
        }
        _offered++;
        _jobs.Offer(j, j.Key, sig);
    }

    /// <summary>Queue a simplify straight off a parsed <c>MeshDef</c>: the parse has the whole mesh already, so
    /// neither the range copy nor the index-range scan <see cref="Offer"/> needs is wanted here. False = the
    /// mesh is outside the simplifier's bounds (too many corners or vertices, or not a triangle list), which is
    /// a decline, not a silent skip.</summary>
    internal static bool OfferModel(int slot, int item, CX.Alloy.Mdl.MeshDef mesh, ulong sig)
    {
        int count = mesh.Indices.Length, verts = mesh.Positions.Length;
        if (count < 3 || count % 3 != 0 || count > MeshSimplify.MaxCorners) return false;
        if (verts <= 0 || verts > MeshSimplify.MaxVerts) return false;
        Job j = _jobs.Rent();
        j.Key = KeyOf(slot, item); j.Sig = sig;
        j.IdxCount = count; j.VertCount = verts; j.Out1N = 0; j.Out2N = 0;
        Grow(ref j.Idx, count); Grow(ref j.Out1, count); Grow(ref j.Out2, count);
        if (j.Pos.Length < verts * 3) j.Pos = new float[verts * 3];
        for (int i = 0; i < count; i++) j.Idx[i] = (ushort)mesh.Indices[i];
        for (int v = 0; v < verts; v++)
        {
            j.Pos[v * 3] = mesh.Positions[v].X;
            j.Pos[v * 3 + 1] = mesh.Positions[v].Y;
            j.Pos[v * 3 + 2] = mesh.Positions[v].Z;
        }
        _offered++;
        _jobs.Offer(j, j.Key, sig);
        return true;
    }

    private static void Grow(ref ushort[] a, int n) { if (a.Length < n) a = new ushort[n]; }

    /// <summary>The worker body: both levels over one copied mesh. A fault files a decline, not a partial run.</summary>
    private static void Work(Job j)
    {
        int tris = j.IdxCount / 3;
        try
        {
            j.Out1N = MeshSimplify.Run(j.Idx, j.IdxCount, j.Pos, j.VertCount,
                                       (int)(tris * Target1), Error1, j.Out1);
            if (j.Out1N > tris * 3 * (1f - MinSaving)) j.Out1N = 0;
            j.Out2N = MeshSimplify.Run(j.Idx, j.IdxCount, j.Pos, j.VertCount,
                                       (int)(tris * Target2), Error2, j.Out2);
            if (j.Out2N > tris * 3 * (1f - MinSaving) || j.Out2N >= j.Out1N && j.Out1N > 0) j.Out2N = 0;
        }
        catch (Exception e) { j.Out1N = j.Out2N = 0; _log?.Info($"MeshLod: worker fault -- {e.Message}"); }
    }

    /// <summary>Collect whatever the worker finished and append it to the shared element buffer. Render
    /// thread, inside the Alloy bracket, once a frame: this is the only place a GL name is touched.</summary>
    internal static void Pump()
    {
        if (!_jobs.Running || Gl.CreateBuffers == null) return;
        int dirtyFrom = _used;
        while (_jobs.TryTake(out Job j))
        {
            var e = new Entry { Done = true, Sig = j.Sig };
            if (j.Out1N > 0 && Append(j.Out1, j.Out1N, out e.Off1)) { e.Cnt1 = j.Out1N; _lod1Tris += j.Out1N / 3; }
            if (j.Out2N > 0 && Append(j.Out2, j.Out2N, out e.Off2)) { e.Cnt2 = j.Out2N; _lod2Tris += j.Out2N / 3; }
            if (e.Cnt1 > 0 || e.Cnt2 > 0)
            {
                _generated++; _srcTris += j.IdxCount / 3;
                // Bounded: the first few are the acceptance signal, and a whole area's worth would be a flood.
                if (_logLeft-- > 0)
                    _log?.Info($"MeshLod: mesh {j.IdxCount / 3} tris -> {e.Cnt1 / 3} / {e.Cnt2 / 3} at lod 1 / 2");
            }
            else _declined++;
            _jobs.File(j.Key, j.Sig, e);
            _jobs.Recycle(j);
        }
        if (_used == dirtyFrom && _uploaded == _used) return;
        Upload(dirtyFrom);
    }

    /// <summary>Reserve arena room for one run. False means the ceiling is reached and the level is dropped.</summary>
    private static bool Append(ushort[] run, int n, out nint off)
    {
        off = 0;
        if (_used + n > ArenaCeiling) { _arenaFull = true; _dropped++; return false; }
        if (_mirror.Length < _used + n)
        {
            int cap = _mirror.Length == 0 ? 1 << 16 : _mirror.Length;
            while (cap < _used + n) cap *= 2;
            if (cap > ArenaCeiling) cap = ArenaCeiling;
            Array.Resize(ref _mirror, cap);
        }
        Array.Copy(run, 0, _mirror, _used, n);
        off = (nint)((long)_used * 2);
        _used += n;
        return true;
    }

    /// <summary>Push the arena to GL. A grow respecifies the buffer -- the NAME is unchanged, so every LOD VAO
    /// built over it stays valid -- and re-sends everything; otherwise only the new tail goes up.</summary>
    private static void Upload(int dirtyFrom)
    {
        if (_ebo == 0) { uint b; Gl.CreateBuffers(1, &b); if (b == 0) return; _ebo = b; _uploaded = 0; }
        fixed (ushort* p = _mirror)
        {
            if (_uploaded < _used && _capacity >= _used)
                fixed (ushort* tail = _mirror.AsSpan(dirtyFrom, _used - dirtyFrom))
                    Gl.NamedBufferSubData(_ebo, (IntPtr)((long)dirtyFrom * 2), (IntPtr)((long)(_used - dirtyFrom) * 2), tail);
            else
            {
                _capacity = _mirror.Length;
                Gl.NamedBufferData(_ebo, (IntPtr)((long)_capacity * 2), p, Gl.STATIC_DRAW);
            }
        }
        _uploaded = _used;
    }

    private static int _capacity;

    /// <summary>The detail level a sphere of world radius <paramref name="r"/> at <paramref name="dist"/> from
    /// the eye earns. <paramref name="scale"/> is the cull pass's pixels-per-unit-radius-at-unit-depth; the
    /// distance stands in for the clip w the GPU test uses, which is exact on the view axis and conservative
    /// (one level sooner at most) off it.</summary>
    internal static int Level(float r, float dist, float scale)
    {
        if (scale <= 0f || dist <= 0f || r <= 0f) return 0;
        float px = 2f * r * scale / dist * MathF.Max(Lod.Bias.V, 0.01f);
        if (px >= Lod.Px1.V) return 0;
        return px >= MathF.Min(Lod.Px2.V, Lod.Px1.V) ? 1 : 2;
    }

    /// <summary>The two bound thresholds a SHADOW view's detail pick compares a caster against:
    /// <see cref="Level"/>'s curve with <paramref name="texel"/>, the view's world units per shadow texel, in
    /// place of the screen projection. 0/0 = every caster bakes the base run (detail off, or a view with no
    /// texel size of its own).</summary>
    internal static void ShadowThresholds(float texel, out float r1, out float r2)
    {
        r1 = 0f; r2 = 0f;
        // a non-finite texel (a view with no map size yet) must fall to the BASE run, not past both
        // thresholds into the coarsest one, which is what an infinite texel divides out to
        if (!On || !(texel > 0f) || !float.IsFinite(texel)) return;
        // px = bound * k, so the two pixel thresholds divide straight through into bounds
        float k = 2f * MathF.Max(Lod.Bias.V, 0.01f) / texel;
        r1 = Lod.Px1.V / k;
        r2 = MathF.Min(Lod.Px2.V, Lod.Px1.V) / k;
    }

    /// <summary>The level a bound of <paramref name="q"/> earns against them -- a radius for a cascade, a
    /// radius over the distance from the light for a cube face, whose texels grow with it. The expression
    /// both gather dispatches spell, so a check can run the ladder the GPU runs.</summary>
    internal static int ShadowLevel(float q, float r1, float r2) => q >= r1 ? 0 : q >= r2 ? 1 : 2;

    /// <summary>This mesh's generated range at <paramref name="lod"/>, falling back to the coarser level's
    /// neighbour when one was declined. False means "draw the engine's own range", which every caller must be
    /// able to do -- there is no level a mesh is obliged to have.</summary>
    internal static bool Range(int slot, int item, int lod, out nint off, out int count, out int level)
    {
        off = 0; count = 0; level = 0;
        if (lod <= 0 || _ebo == 0) return false;
        if (!_table.TryGetValue(KeyOf(slot, item), out Entry e) || !e.Done) return false;
        if (lod >= 2 && e.Cnt2 > 0) { off = e.Off2; count = e.Cnt2; level = 2; return true; }
        if (e.Cnt1 > 0) { off = e.Off1; count = e.Cnt1; level = 1; return true; }
        if (e.Cnt2 > 0) { off = e.Off2; count = e.Cnt2; level = 2; return true; }
        return false;
    }

    /// <summary>Period-line tally: triangles submitted at each level. Called from the batch walk, which is
    /// single-threaded by the time it runs.</summary>
    internal static void Drew(int lod, int indices) => _drawnTris[lod] += indices / 3;

    /// <summary>A VDO slot was vacated or re-pointed: every range keyed on it names geometry that is no
    /// longer there. The arena bytes are left where they are -- the next <see cref="Revoke"/> reclaims
    /// them -- because compacting it would move every other mesh's offsets.</summary>
    internal static void ForgetSlot(int slot) => _jobs.ForgetSlot(slot);

    /// <summary>The world edge: the part table is gone, so every (slot, item) key means nothing. The arena is
    /// rewound with it, which is the only point its bytes are ever reclaimed.</summary>
    internal static void Revoke()
    {
        _jobs.Revoke();
        _used = 0; _uploaded = 0; _arenaFull = false;
        Array.Clear(_drawnTris);
        _srcTris = _lod1Tris = _lod2Tris = 0;
        _generated = _declined = _offered = _dropped = _small = 0;
    }

    /// <summary>The context is gone, so the buffer name means nothing -- forgotten, never deleted (the number
    /// is already back in circulation). The arena's bytes survive in the mirror and go back up on the first
    /// <see cref="Pump"/> of the next context.</summary>
    internal static void ContextLost()
    {
        _ebo = 0; _uploaded = 0; _capacity = 0;
    }

    /// <summary>The settings panel's status block.</summary>
    internal static string Describe()
        => !_jobs.Running
           ? "not running"
           : $"{_offered} meshes offered, {_generated} simplified, {_declined} declined, {_small} under {MinTris} triangles; "
             + $"{_srcTris} source triangles -> {_lod1Tris} at half / {_lod2Tris} at a fifth"
             + (_arenaFull ? "; the index arena is full and further levels are dropped" : "")
             + (MeshChart.LodNote() is { Length: > 0 } c ? "; " + c : "")
             + DrawnNote();

    /// <summary>The last period's triangles drawn per level, kept for the panel after <see cref="Stats"/> clears the tally.</summary>
    private static string DrawnNote()
        => _shown[0] + _shown[1] + _shown[2] == 0 ? ""
         : $"; drawn per frame at lod 0/1/2: {_shown[0]}/{_shown[1]}/{_shown[2]} triangles";

    private static readonly long[] _shown = new long[Levels];

    /// <summary>One clause of the Alloy period line: what the frame actually drew at each level.</summary>
    internal static string Stats(int frames)
    {
        long t0 = _drawnTris[0], t1 = _drawnTris[1], t2 = _drawnTris[2];
        Array.Clear(_drawnTris);
        if (frames <= 0) frames = 1;
        _shown[0] = t0 / frames; _shown[1] = t1 / frames; _shown[2] = t2 / frames;
        if (t0 + t1 + t2 == 0) return "";
        return $"lod {t0 / frames}/{t1 / frames}/{t2 / frames} tris per frame at lod 0/1/2"
               + $" ({_generated} meshes simplified, {_declined} declined, {_dropped} dropped)";
    }

    /// <summary>Off-device: the arena hands out non-overlapping byte offsets, a level the simplifier declined
    /// is absent rather than empty, and a coarse request falls back to the level that exists.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        if (!MeshSimplify.SelfCheck(log)) return false;
        var check = new Checker(log);

        int savedUsed = _used;
        uint savedEbo = _ebo;
        _used = 0; _ebo = 1;   // a stand-in name: nothing below binds it
        var run = new ushort[] { 0, 1, 2, 0, 2, 3 };
        check.Expect(Append(run, 6, out nint a) && a == 0, "the first run does not start at offset 0");
        check.Expect(Append(run, 6, out nint b) && b == 12, "the second run does not follow the first");
        _table[KeyOf(7, 3)] = new Entry { Done = true, Off1 = a, Cnt1 = 6 };
        check.Expect(Range(7, 3, 1, out nint o1, out int c1, out int l1) && o1 == a && c1 == 6 && l1 == 1, "a level-1 request does not find its run");
        check.Expect(Range(7, 3, 2, out nint o2, out int c2, out int l2) && o2 == a && c2 == 6 && l2 == 1, "a missing level 2 does not fall back to level 1");
        check.Expect(!Range(7, 3, 0, out _, out _, out _), "level 0 is a generated range");
        check.Expect(!Range(7, 4, 1, out _, out _, out _), "a mesh with no entry answered a range");
        _table.Remove(KeyOf(7, 3));
        _used = savedUsed; _ebo = savedEbo;

        // CX-50: the colour draw's level substitution, over two REAL arena ranges. The oracle is the fixture:
        // index runs of 12/6/3 shorts, so a hit at a level is that level's own hand-written count, and the
        // range that was given no coarse run must serve its base run at ANY requested level -- never nothing.
        ushort[] full = { 0, 1, 2, 0, 2, 3, 1, 2, 3, 0, 1, 3 }, half = { 0, 1, 2, 1, 2, 3 }, fifth = { 0, 1, 3 };
        var verts = new float[4 * MeshChart.Stride];
        int three = Db.MeshArena.AcquireRaw(0xC50_0001, Db.MeshArena.Fmt.Static, 4, verts,
                                            full, 12, half, 6, fifth, 3, default, default);
        int one = Db.MeshArena.AcquireRaw(0xC50_0002, Db.MeshArena.Fmt.Static, 4, verts,
                                          full, 12, null, 0, null, 0, default, default);
        nint ro = 0; int rb = 0, rc = 0, rl = 0;
        check.Expect(AlloyRenderer.ArenaLevelRuns(three, 2, ref ro, ref rb, ref rc, ref rl) && rc == 3 && rl == 2,
          "a level-2 draw does not take the level-2 run");
        nint roTwo = ro;
        rc = 0; rl = 0;
        check.Expect(AlloyRenderer.ArenaLevelRuns(three, 1, ref ro, ref rb, ref rc, ref rl) && rc == 6 && rl == 1,
          "a level-1 draw does not take the level-1 run");
        check.Expect(ro != roTwo, "two levels of one mesh draw from the same byte offset");
        rc = 0; rl = 0;
        check.Expect(AlloyRenderer.ArenaLevelRuns(one, 2, ref ro, ref rb, ref rc, ref rl) && rc == 12 && rl == 0,
          "a mesh with no coarse run does not fall back to its base run");
        rc = 0; rl = 0;
        check.Expect(!AlloyRenderer.ArenaLevelRuns(-1, 2, ref ro, ref rb, ref rc, ref rl) && rc == 0,
          "a slot with no arena range substituted a run anyway");
        // CX-50: ...and the ORDER the colour draw applies them in. A chart that has not landed must still
        // fall through to the level run: a bakeable part (most of a scene) whose chart is absent or refused
        // took the chart branch, missed, and kept full detail. Slot 0 holds no DB part here, so DbBakeable
        // refuses and the fall-through is the path taken -- the same path a chart MISS takes, by
        // construction: DbChart returning false is the only other way into it.
        check.Expect(!AlloyRenderer.DbSlot(0), "the order check's stand-in slot holds a DB part");
        ro = 0; rb = 0; rc = 0; rl = 0;
        AlloyRenderer.DbRuns(0, three, 2, chartArmed: true, ref ro, ref rb, ref rc, ref rl);
        check.Expect(rc == 3 && rl == 2, "a chart that has not landed does not fall through to the level run");
        rc = 0; rl = 0;
        AlloyRenderer.DbRuns(0, three, 0, chartArmed: false, ref ro, ref rb, ref rc, ref rl);
        check.Expect(rc == 0, "a draw at level 0 substituted a run");
        rc = 0; rl = 0;
        AlloyRenderer.DbRuns(0, -1, 2, chartArmed: false, ref ro, ref rb, ref rc, ref rl);
        check.Expect(rc == 0, "a slot with no recipe substituted a run");

        // CX-50: ...and the runs a shadow view picks among, in INDICES (the indirect command takes elements,
        // where the arena stores bytes). The fixture is the oracle twice over: the two counts are its own,
        // and its runs were appended back to back, so level 2 starts exactly 6 INDICES after level 1 -- 12
        // if these were still byte offsets.
        check.Expect(AlloyRenderer.ArenaLevels(three, out uint sc1, out uint sf1, out uint sc2, out uint sf2)
                     && sc1 == 6 && sc2 == 3 && sf2 - sf1 == 6,
          "the staged coarse runs are not the fixture's two generated runs in index form");
        check.Expect(AlloyRenderer.ArenaLevels(one, out uint oc1, out _, out uint oc2, out _) && oc1 == 0 && oc2 == 0,
          "a mesh the simplifier declined twice reports a coarse run anyway");
        Db.MeshArena.Release(three); Db.MeshArena.Release(one);

        // CX-50: the shadow view's level curve. The oracle is arithmetic done by hand: at a 40 u cascade fit
        // over a 2048 map a texel is 2*40/2048 = 0,0390625 u, so a bound of r spans 2r/0,0390625 px -- 614 px
        // at 12 u (full detail above 500), 256 px at 5 u, 51 px at 1 u (lowest detail below 150). A cube
        // face's texel is 2d/size, so the same 256 px falls to a 20 u bound 40 u from a 512 light.
        float sPx1 = Lod.Px1.V, sPx2 = Lod.Px2.V, sBias = Lod.Bias.V;
        bool sMaster = Lod.G.Master is { V: true };
        if (Lod.G.Master != null) Lod.G.Master.V = true;
        Lod.Px1.V = 500f; Lod.Px2.V = 150f; Lod.Bias.V = 1f;
        ShadowThresholds(2f * 40f / 2048f, out float c1t, out float c2t);
        check.Expect(ShadowLevel(12f, c1t, c2t) == 0 && ShadowLevel(5f, c1t, c2t) == 1 && ShadowLevel(1f, c1t, c2t) == 2,
          "a cascade's detail levels do not follow its texel size");
        check.Expect(ShadowLevel(1e30f, c1t, c2t) == 0, "a bound exempt from the cull does not bake full detail");
        ShadowThresholds(2f / 512f, out float f1t, out float f2t);
        check.Expect(ShadowLevel(20f / 10f, f1t, f2t) == 0 && ShadowLevel(20f / 40f, f1t, f2t) == 1
                     && ShadowLevel(2f / 40f, f1t, f2t) == 2,
          "a cube face's detail levels do not follow its distance");
        ShadowThresholds(0f, out float z1, out float z2);
        check.Expect(z1 == 0f && z2 == 0f && ShadowLevel(0f, z1, z2) == 0, "a view with no texel size does not bake full detail");
        ShadowThresholds(float.PositiveInfinity, out float i1, out float i2);
        check.Expect(ShadowLevel(0.001f, i1, i2) == 0, "a view with no map size bakes the coarsest run instead of the base one");
        if (Lod.G.Master != null) Lod.G.Master.V = sMaster;
        Lod.Px1.V = sPx1; Lod.Px2.V = sPx2; Lod.Bias.V = sBias;

        // the table's own round trip, no worker: claim, file under the claim, refuse a stale sig, forget one slot
        var t = new SlotJobTable<Job, Entry>(8, 1);
        t.Offer(t.Rent(), KeyOf(7, 1), 11); t.Offer(t.Rent(), KeyOf(8, 1), 12);
        check.Expect(!t.QueueHasRoom, "a full queue still has room");
        check.Expect(t.File(KeyOf(7, 1), 11, new Entry { Done = true, Sig = 11 }), "a result under its claim was refused");
        check.Expect(!t.File(KeyOf(7, 1), 99, default), "a result under a stale signature was filed");
        t.ForgetSlot(7);
        check.Expect(!t.Entries.ContainsKey(KeyOf(7, 1)) && t.Entries.ContainsKey(KeyOf(8, 1)), "ForgetSlot did not drop exactly its slot");
        t.Revoke();
        check.Expect(t.Entries.Count == 0 && t.QueueHasRoom, "Revoke left entries or queued jobs behind");

        log.Info(check.Ok ? "MeshLod.SelfCheck: passed" : "MeshLod.SelfCheck: FAILED");
        return check.Ok;
    }
}
