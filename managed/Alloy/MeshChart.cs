using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The <c>lightmap</c> group: baked static lighting -- the charter, the bake and the
/// loading-screen hold.</summary>
internal static class Lightmap
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<int> Density, Disk, Pages, Near, Tiers, HoldSeconds, GrowSlice,
                                       MoverLights, PairSize, PairCount;
    internal static readonly Knob<bool> Movers, MoverTaps;

    static Lightmap()
    {
        G = KnobGroup.Declare("lightmap", "Graphics", "Lighting", master: "Baked static lighting",
            masterHelp: "Static geometry -- tiles and placeables that never move -- gets its lights baked into a "
                      + "lightmap as the area loads, and the frame stops re-shading them. Off is the live light loop.",
            masterDefault: true, masterTier: Tier.ChatOnly, order: 316, title: "Baked lighting");
        G.Status = MeshChart.Describe;
        Density = G.Slider("density", "Lightmap density", 2, 32, 1, 8, "px/m", tier: Tier.Advanced,
                           help: "Lightmap texels per metre of world. Each instance's rectangle is capped at a quarter page; a change re-bakes the area.",
                           changed: LightmapBake.RekeyAll);
        Pages = G.Slider("pages", "Lightmap pages", 0, LightmapBake.MaxPages, 1, 0, tier: Tier.ChatOnly,
                         help: "4096x4096 pages the area may hold (128 MiB each), allocated as the area needs them. 0 sizes it from the card's memory: a quarter of it in pages, at most 32. An area that would overflow the cap bakes at a lower density instead.");
        // the pages live in ONE immutable array, so one more page means allocate-copy-delete of them all
        GrowSlice = G.Slider("growslice", "Page growth slice", 0, 8, 1, 1, "pages", tier: Tier.ChatOnly,
                             help: "Lightmap pages the client may move into a larger page array in one frame when an area needs another page. Each page is 192 MiB, so moving them all at once costs a visible freeze; 0 does exactly that, for comparison.");
        Near = G.Slider("near", "Full-density radius", 10, 200, 5, 40, "m", tier: Tier.ChatOnly,
                        help: "Metres from the player baked at the full density; each tier out doubles the radius and halves the density. The last tier covers the whole area and bakes first.");
        Tiers = G.Slider("tiers", "Density tiers", 1, LightmapBake.MaxTiers, 1, 3, tier: Tier.ChatOnly,
                         help: "Density steps between the player and the far area: 1 bakes everything at the full density, 3 is full / half / quarter. A change re-bakes the area.",
                         changed: LightmapBake.RekeyAll);
        HoldSeconds = G.Slider("hold", "Loading hold", 0, 40, 1, 20, "s", Tier.ChatOnly,
                               help: "Seconds the loading screen may wait for the area's baked lighting before Escape is offered to skip ahead; the bake then finishes in the background. 0 never waits.");
        Movers = G.Toggle("movers", "Creature shadows from baked lights", true, Tier.Advanced,
                          help: "A creature standing under a baked light casts its shadow on the baked surface. Off is cheaper: baked surfaces take no shadow from creatures under static lights.");
        MoverTaps = G.Toggle("movertaps", "movertaps", true, Tier.ChatOnly,
                             help: "Diagnostic: 0 keeps creature-hot baked lights in the light loop (the ray test) but skips their light body and shadow reads, so /alloy bench splits the creature-shadow cost.");
        MoverLights = G.Slider("moverlights", "Baked lights per creature", 0, 4, 1, 2, tier: Tier.ChatOnly,
                               help: "Baked lights a creature may cast a shadow from, strongest first, while deferred lighting is on. Each one gets a small shadow map of its own instead of the creature's shadow riding a whole depth cube. 0 is no creature shadows from baked lights on that path.");
        PairSize = G.Slider("pairsize", "Creature tile size", 32, 512, 32, 128, "px", tier: Tier.ChatOnly,
                            effect: Effect.Restart,
                            help: "Side of one creature-and-light shadow tile. A restart knob: it sizes the atlas.");
        PairCount = G.Slider("paircount", "Creature tiles", 16, global::CX.Alloy.PairShadows.MaxPairs, 16, 256,
                             tier: Tier.ChatOnly, effect: Effect.Restart,
                             help: "Creature-and-light tiles the atlas holds. A light whose creatures do not all fit keeps its depth cube, nearest lights first. A restart knob: it sizes the atlas.");
        // 2048: a smaller budget prunes the live files of the area being walked
        Disk = G.Slider("disk", "Chart cache size", 64, 8192, 64, 2048, "MiB", tier: Tier.ChatOnly,
                        help: "Disk the chart and bake caches may hold; the oldest files go first. One area's lightmap is roughly 500 MiB.");
    }
}

/// <summary>
/// The charter: every static mesh gets a lightmap UV chart, cut by the vendored xatlas on a background worker and
/// cached on disk by mesh CONTENT, so the second load of an area is a file read. A chart splits vertices at every
/// seam, so the worker emits the WHOLE mesh -- pos, normal, UV, chart UV -- as a <see cref="CX.Alloy.Db.MeshArena"/>
/// range with its chart lane filled; a mesh with no chart keeps drawing its own range, unlit by the bake.
/// </summary>
internal static unsafe partial class MeshChart
{
    /// <summary>The mesh atlas's own density: charts are cut once at unit scale and the per-instance record
    /// folds the density knob and the transform in at bake time. Chart UVs are in these
    /// texel units; changing this invalidates every chart file (see <see cref="LedgerVersion"/>).</summary>
    internal const float AtlasTexelsPerUnit = 8f;
    /// <summary>Texels xatlas leaves between charts inside the mesh atlas: a two-texel gutter.</summary>
    internal const uint Padding = 2;
    /// <summary>Floats per arena vertex in <see cref="CX.Alloy.Db.MeshArena.Fmt.Static"/>: pos3 nrm3 uv2 chart2.
    /// The charted interleave below writes exactly this layout, which is why a charted mesh needs no format of
    /// its own.</summary>
    internal const int Stride = 10;
    /// <summary>Alloy attribute location of the chart UV stream (3..7 are free; 0/1/2 pos/uv/nrm, 8/9 bones).</summary>
    internal const uint LocChartUv = 3;

    private const int MaxPending = 256;
    /// <summary>xatlas threads: a job is one mesh on its own atlas, so the meshes of an area chart in parallel.
    /// Two cores stay with the game and render threads.</summary>
    private static int Workers => Math.Clamp(Environment.ProcessorCount - 2, 1, 8);
    private const int MaxEntries = 1 << 16;   // the plaza filled 1 << 14 exactly and the rest stayed magenta
    private const int LogCap = 8;

    private static ExtenderLog? _log;

    /// <summary>Why a mesh has no chart. Indexes <see cref="RefusalName"/>; 0 is charted. The first four are
    /// the worker's; <c>Arena</c> is the ceiling; <c>Unlit</c> (no normal stream: nothing to bake against) and
    /// <c>Corners</c> (past <see cref="MeshSimplify.MaxCorners"/>) are refused at the offer, before any copy.</summary>
    internal enum Refusal : byte { None, Input, Xatlas, Uncharted, Overflow, Arena, Unlit, Corners }
    internal static readonly string[] RefusalName = { "none", "input", "xatlas", "uncharted", "overflow", "arena", "unlit", "corners" };

    /// <summary>One charted mesh: its <see cref="CX.Alloy.Db.MeshArena"/> range (which carries the base vertex
    /// and the three index runs) and the atlas size in texels.</summary>
    private struct Entry : ISlotEntry
    {
        /// <summary>The arena range, -1 for a refused entry. Every holder of one owns a reference to it.</summary>
        internal int Range;
        internal ushort W, H;
        internal ulong Content;
        /// <summary>The mesh's bounds in ITS OWN space (world space for a tile part): the engine's part
        /// bbox is unpopulated on tile parts (min == max), so the bake sizes its light reach from these.</summary>
        internal Vector3 Bmin, Bmax;
        internal bool Done;
        internal Refusal Refused;
        /// <summary>The range's signature at offer time (<c>AlloyRenderer.RangeSig</c>): a pool item the engine
        /// freed and refilled keeps its (slot, item) key, and without this the new tenant drew the OLD mesh
        /// out of the arena -- the floating nonsense after an area change.</summary>
        public ulong Sig { get; set; }
        /// <summary>Charted vertices, for the log line.</summary>
        internal int NV;
    }

    private static readonly SlotJobTable<Job, Entry> _jobs = new(MaxEntries, MaxPending);
    private static Dictionary<long, Entry> _table => _jobs.Entries;

    /// <summary>Bumped by every edit that can change a <see cref="Range"/> answer (file, drop, compaction, the
    /// world edge): a <see cref="RangeMemo"/> stamped with an older one re-probes. Starts at 1 so a
    /// zeroed memo is stale.</summary>
    private static int _epoch = 1;

    /// <summary>One caller slot's cached <see cref="Range"/> answer, valid while the table epoch, the key and
    /// the asked LOD all match: the per-part draw skips the dictionary probe and its Entry copy.</summary>
    internal struct RangeMemo
    {
        internal long Key;
        internal nint Off;
        internal int Epoch, Base, Count;
        internal byte Lod, Level;
        internal bool Hit;
    }

    /// <summary>One mesh handed to the worker and back. Input arrays are DENSE: <see cref="Offer"/> remaps
    /// the engine's indices to first-use order, so the content key sees no stale pool bytes and two parts
    /// of one mesh hash the same.</summary>
    internal sealed class Job
    {
        internal long Key;
        internal ulong Sig;
        internal ulong Content;
        internal int IdxCount, VertCount;
        internal bool HasNrm, HasUv;
        internal ushort[] Idx = Array.Empty<ushort>();
        internal float[] Pos = Array.Empty<float>(), Nrm = Array.Empty<float>(), Uv = Array.Empty<float>();
        // the charted result
        internal int OutVerts, Out0N, Out1N, Out2N, Charts, Loose;
        internal ushort W, H;
        internal ushort[] Xref = Array.Empty<ushort>();
        internal float[] ChartUv = Array.Empty<float>();
        internal ushort[] Out0 = Array.Empty<ushort>(), Out1 = Array.Empty<ushort>(), Out2 = Array.Empty<ushort>();
        internal float[] OutPos = Array.Empty<float>();
        /// <summary>Positions rebased to the mesh's min corner and quantised to <see cref="Grid"/> -- what the
        /// content key and xatlas see. Tile geometry arrives in WORLD space, so without the rebase every placement
        /// of one tile model is its own content; a chart is translation-invariant, so the rebased mesh's chart is
        /// the placed mesh's.</summary>
        internal float[] Local = Array.Empty<float>();
        internal Vector3 Bmin, Bmax;
        internal Refusal Refused;
        internal bool Hit, Stale;
    }

    private static int[] _remap = Array.Empty<int>();

    /// <summary>The interleave scratch one charted mesh is built in before it goes to the arena. Render
    /// thread only, reused: the collect is one mesh at a time.</summary>
    private static float[] _stage = Array.Empty<float>();

    private static int _offered, _hit, _missed, _built, _refused, _stale, _tableFull, _logLeft = LogCap;
    /// <summary>Charted meshes at or past <see cref="MeshLod.MinTris"/>, and those that kept a generated level.</summary>
    private static int _lodBig, _lodKept;
    private static readonly int[] _refusedBy = new int[RefusalName.Length];

    /// <summary>Meshes offered, meshes answered (hit or built), and the difference still on the worker.</summary>
    internal static int Offered => _offered;
    internal static int Done => _hit + _missed;
    internal static int InFlight => _offered - _hit - _missed;
    /// <summary>A mesh refused before the worker ever saw it: counted, so the period line's refused total
    /// names every static mesh that has no chart, not only the ones that reached xatlas.</summary>
    internal static void Skip(Refusal why) { _refused++; _refusedBy[(int)why]++; }
    private static int _drawn;

    /// <summary>Is a charted range available to pick this frame? False leaves every draw on its own uncharted
    /// arena range -- the stand-down, not a second code path.</summary>
    internal static bool Armed => Vao != 0 && Lightmap.G.Master is { V: true };

    /// <summary>The arena's static-format VAO, zero until the first mesh lands in this context.</summary>
    internal static uint Vao => CX.Alloy.Db.MeshArena.Vao(CX.Alloy.Db.MeshArena.Fmt.Static);

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _ = Lightmap.G;
        if (_jobs.Running) return;
        _jobs.Start("cx-chart", Work, Workers);
        log.Info($"MeshChart: {Workers} worker(s) up -- static meshes are charted as they enrol while lightmap.enabled is on");
    }

    internal static long KeyOf(int slot, int item) => SlotJobTable<Job, Entry>.KeyOf(slot, item);

    /// <summary>The slot half of a mesh's chart key, shared by every scene-DB mesh: the item half is the ARENA
    /// RANGE, an identity the arena already dedups by content, so one range is one distinct mesh and two
    /// instances of it share a chart. Negative, so it can never collide with a real VDO slot index.</summary>
    internal const int DbSlot = -1;

    /// <summary>Offer the arena's unoffered static ranges, a few per frame, from the GAME thread's frame fold.
    /// The draw cannot be the only offer site: the bake's loading-screen hold waits for charts while holding back
    /// the very frames the batch build would offer from, so it would wait out its whole cap. The arena has the
    /// geometry as soon as the area is built, which is the creation edge the offers belong on.</summary>
    internal static void DripDb(int budget)
    {
        if (!_jobs.Running || !Lightmap.G.Master!.V) return;
        for (int n = 0; n < budget; n++)
        {
            if (!HasRoom()) return;
            int range = CX.Alloy.Db.MeshArena.NextOfferable(ref _dripCursor);
            if (range < 0) return;
            OfferDb(range);
        }
    }

    /// <inheritdoc cref="DripDb"/>
    private static int _dripCursor;

    /// <summary>Offer an arena range to the charter, reading its geometry off the arena's own host mirror --
    /// already dense and already interleaved in the layout the chart files back into, so nothing is re-indexed.
    /// False = nothing to do (no room, already charted, skinned or dead). Cold: once per mesh, on the enrolment
    /// path, and it allocates the job's arrays.</summary>
    internal static bool OfferDb(int range)
    {
        if (!CX.Alloy.Db.MeshArena.HostMeshSize(range, out int nv, out int ni, out ulong content, out uint gen))
            return false;
        // The signature is the content hash and the range's hand-out generation: a recycled range index is a
        // DIFFERENT mesh under the same item, and Wanted re-charts on a signature change.
        ulong sig = CX.Alloy.Db.MeshArena.Sig(content, gen);
        if (!Wanted(DbSlot, range, ni, sig, out _)) return false;
        Job j = _jobs.Rent();
        j.Key = KeyOf(DbSlot, range); j.Sig = sig;
        j.IdxCount = ni; j.HasNrm = true; j.HasUv = true;
        j.Refused = Refusal.None; j.Hit = false; j.Stale = false;
        j.Out0N = j.Out1N = j.Out2N = 0; j.OutVerts = 0; j.Charts = 0; j.Loose = 0;
        Grow(ref j.Idx, ni);
        if (j.Pos.Length < nv * 3) j.Pos = new float[nv * 3];
        if (j.Nrm.Length < nv * 3) j.Nrm = new float[nv * 3];
        if (j.Uv.Length < nv * 2) j.Uv = new float[nv * 2];
        if (!CX.Alloy.Db.MeshArena.HostMeshCopy(range, j.Pos, j.Nrm, j.Uv, j.Idx))
        { _jobs.Recycle(j); Skip(Refusal.Input); return false; }
        j.VertCount = nv;
        _offered++;
        CX.Alloy.Db.MeshArena.MarkOffered(range);
        _jobs.Offer(j, j.Key, sig);
        return true;
    }


    /// <summary>Charts on file, claimed or landed -- the area edge's tally.</summary>
    internal static int TableCount => _table.Count;

    /// <summary>Asked before anything is read: a mesh on file, queued or past a budget costs one probe.
    /// <paramref name="later"/> = not now but not refused either: the worker's queue is full (an area's whole
    /// tile set enrols in one walk). The caller MUST re-offer when it drains, or the mesh stays uncharted.</summary>
    internal static bool Wanted(int slot, int item, int idxCount, ulong sig, out bool later)
    {
        later = false;
        if (!_jobs.Running || idxCount < 3 || !Lightmap.G.Master!.V) return false;
        if (_table.TryGetValue(KeyOf(slot, item), out Entry have))
        {
            if (have.Sig == sig) return false;
            _table.Remove(KeyOf(slot, item));   // the item was refilled under the same key: chart the new mesh
            _epoch++;
            Drop(have);
        }
        if (_table.Count >= MaxEntries) { _tableFull++; return false; }
        if (idxCount > MeshSimplify.MaxCorners) { Skip(Refusal.Corners); return false; }
        later = !_jobs.QueueHasRoom;
        return !later;
    }

    /// <summary>Room in the worker's queue for a deferred offer?</summary>
    internal static bool HasRoom() => _jobs.QueueHasRoom;

    /// <summary>Copy one part's range and every attribute it addresses off the pools' host shadows, densely
    /// re-indexed, and queue it. Enrollment walk only: reads engine memory and allocates, off every frame path.</summary>
    internal static void Offer(int slot, int item, byte* elemHost, long first, int count,
                               byte* posHost, in VertexStream pos, byte* nrmHost, in VertexStream nrm,
                               byte* uvHost, in VertexStream uv, int baseVertex, ulong sig)
    {
        if (elemHost == null || posHost == null || count < 3 || count % 3 != 0) return;
        if (pos.Type != Gl.FLOAT || pos.Size != 3) return;
        bool hasNrm = nrmHost != null && nrm.Type == Gl.FLOAT && nrm.Size == 3;
        bool hasUv = uvHost != null && uv.Type == Gl.FLOAT && uv.Size == 2;
        ushort* src = (ushort*)(elemHost + first * 2);
        int maxI = -1;
        for (int i = 0; i < count; i++) { int v = src[i]; if (v > maxI) maxI = v; }
        if (maxI < 0) return;
        if (_remap.Length <= maxI) _remap = new int[Math.Max(maxI + 1, MeshSimplify.MaxVerts)];
        Array.Fill(_remap, -1, 0, maxI + 1);

        Job j = _jobs.Rent();
        j.Key = KeyOf(slot, item); j.Sig = sig;
        j.IdxCount = count; j.HasNrm = hasNrm; j.HasUv = hasUv;
        j.Refused = Refusal.None; j.Hit = false; j.Stale = false; j.Out0N = j.Out1N = j.Out2N = 0; j.OutVerts = 0; j.Charts = 0; j.Loose = 0;
        Grow(ref j.Idx, count);
        if (j.Pos.Length < (maxI + 1) * 3) j.Pos = new float[(maxI + 1) * 3];
        if (hasNrm && j.Nrm.Length < (maxI + 1) * 3) j.Nrm = new float[(maxI + 1) * 3];
        if (hasUv && j.Uv.Length < (maxI + 1) * 2) j.Uv = new float[(maxI + 1) * 2];
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            int v = src[i];
            int d = _remap[v];
            if (d < 0)
            {
                d = n++; _remap[v] = d;
                float* p = (float*)(posHost + pos.Off + (long)(baseVertex + v) * pos.Stride);
                j.Pos[d * 3] = p[0]; j.Pos[d * 3 + 1] = p[1]; j.Pos[d * 3 + 2] = p[2];
                if (hasNrm)
                {
                    float* q = (float*)(nrmHost + nrm.Off + (long)(baseVertex + v) * nrm.Stride);
                    j.Nrm[d * 3] = q[0]; j.Nrm[d * 3 + 1] = q[1]; j.Nrm[d * 3 + 2] = q[2];
                }
                if (hasUv)
                {
                    float* t = (float*)(uvHost + uv.Off + (long)(baseVertex + v) * uv.Stride);
                    j.Uv[d * 2] = t[0]; j.Uv[d * 2 + 1] = t[1];
                }
            }
            j.Idx[i] = (ushort)d;
        }
        j.VertCount = n;
        _offered++;
        _jobs.Offer(j, j.Key, sig);
    }

    private static void Grow(ref ushort[] a, int n) { if (a.Length < n) a = new ushort[n]; }
    private static void Grow(ref float[] a, int n) { if (a.Length < n) a = new float[n]; }

    /// <summary>A worker: content key, ledger probe, xatlas on a miss, then the two LOD runs over the charted mesh.
    /// No GL and no engine memory -- everything it reads was copied at the offer, and its only calls out are xatlas
    /// (pure, one single-threaded atlas per mesh, so byte-identical across runs) and the ledger files. Results file
    /// in completion order, which nothing downstream depends on.</summary>
    private static void Work(Job j)
    {
        try { Chart(j); }
        catch (Exception e) { j.Refused = Refusal.Input; _log?.Info($"MeshChart: worker fault -- {e.Message}"); }
    }

    /// <summary>Quantum of <see cref="Job.Local"/>: 1/1024 m, under any texel at the densest knob, coarse
    /// enough that two placements' float rounding lands on one grid point.</summary>
    private const float Grid = 1024f;

    /// <summary>Chart one job in place: the pure step the self-check drives on a fixture too.</summary>
    internal static void Chart(Job j, bool useLedger = true)
    {
        j.Content = ContentKey(j);
        if (useLedger && LedgerLoad(j)) j.Hit = true;
        else
        {
            j.Refused = Unwrap(j);
            if (j.Refused != Refusal.None) return;
            if (useLedger) LedgerSave(j);
        }
        Expand(j);
        Simplify(j);
    }

    private static void Rebase(Job j)
    {
        int n = j.VertCount * 3;
        Grow(ref j.Local, n);
        float mx = float.MaxValue, my = float.MaxValue, mz = float.MaxValue;
        float hx = float.MinValue, hy = float.MinValue, hz = float.MinValue;
        for (int i = 0; i < n; i += 3)
        {
            mx = MathF.Min(mx, j.Pos[i]); my = MathF.Min(my, j.Pos[i + 1]); mz = MathF.Min(mz, j.Pos[i + 2]);
            hx = MathF.Max(hx, j.Pos[i]); hy = MathF.Max(hy, j.Pos[i + 1]); hz = MathF.Max(hz, j.Pos[i + 2]);
        }
        j.Bmin = new Vector3(mx, my, mz); j.Bmax = new Vector3(hx, hy, hz);
        for (int i = 0; i < n; i += 3)
        {
            j.Local[i] = MathF.Round((j.Pos[i] - mx) * Grid) / Grid;
            j.Local[i + 1] = MathF.Round((j.Pos[i + 1] - my) * Grid) / Grid;
            j.Local[i + 2] = MathF.Round((j.Pos[i + 2] - mz) * Grid) / Grid;
        }
    }

    /// <summary>FNV-1a over the dense mesh: counts, rebased positions, normals, UVs, indices. The disk identity.</summary>
    internal static ulong ContentKey(Job j)
    {
        Rebase(j);
        ulong h = 14695981039346656037UL;
        void Mix(ReadOnlySpan<byte> b) { foreach (byte x in b) { h ^= x; h *= 1099511628211UL; } }
        Span<int> hdr = stackalloc int[4] { j.VertCount, j.IdxCount, j.HasNrm ? 1 : 0, j.HasUv ? 1 : 0 };
        Mix(MemoryMarshal.AsBytes(hdr));
        Mix(MemoryMarshal.AsBytes(j.Local.AsSpan(0, j.VertCount * 3)));
        if (j.HasNrm) Mix(MemoryMarshal.AsBytes(j.Nrm.AsSpan(0, j.VertCount * 3)));
        if (j.HasUv) Mix(MemoryMarshal.AsBytes(j.Uv.AsSpan(0, j.VertCount * 2)));
        Mix(MemoryMarshal.AsBytes(j.Idx.AsSpan(0, j.IdxCount)));
        return h;
    }

    /// <summary>The ARENA identity of a charted mesh: <see cref="ContentKey"/> is translation-invariant (the
    /// disk cache shares a chart between translated copies, which is right for UVs), but the arena range holds
    /// ABSOLUTE positions -- keyed on the content alone, a wall segment one storey up in another tile model drew
    /// out of the first one's range, a level too high. The origin it was rebased from goes into the key.</summary>
    internal static ulong PlacedKey(ulong content, Vector3 origin)
    {
        ulong h = content;
        foreach (float f in stackalloc float[3] { origin.X, origin.Y, origin.Z })
        { h ^= BitConverter.SingleToUInt32Bits(f); h *= 1099511628211UL; }
        return h;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChartOut
    {
        internal void* Atlas; internal float* Uv; internal uint* Xref; internal uint* Index;
        internal uint VertexCount, IndexCount, ChartCount, Width, Height, LooseVerts;
    }

    [DllImport("cxatlas")]
    private static extern int cx_chart(float* pos, float* nrm, float* uv, uint vertCount, ushort* idx, uint idxCount,
                                       float texelsPerUnit, uint padding, ChartOut* result);
    [DllImport("cxatlas")]
    private static extern void cx_chart_free(ChartOut* result);

    /// <summary>xatlas over the job's input; fills Xref/ChartUv/Out0 and the atlas size. Refusal codes are
    /// the shim's, with Overflow for a result past 16-bit indexing.</summary>
    private static Refusal Unwrap(Job j)
    {
        ChartOut r;
        int rc;
        fixed (float* p = j.Local) fixed (float* n = j.Nrm) fixed (float* u = j.Uv) fixed (ushort* ix = j.Idx)
            rc = cx_chart(p, j.HasNrm ? n : null, j.HasUv ? u : null, (uint)j.VertCount, ix, (uint)j.IdxCount,
                          AtlasTexelsPerUnit, Padding, &r);
        if (rc != 0) return rc switch { 1 => Refusal.Input, 2 => Refusal.Xatlas, 4 => Refusal.Overflow, _ => Refusal.Uncharted };
        try
        {
            if (r.VertexCount > 65535 || r.IndexCount != (uint)j.IdxCount || r.Width > 65535 || r.Height > 65535)
                return Refusal.Overflow;
            int nv = (int)r.VertexCount, ni = (int)r.IndexCount;
            Grow(ref j.Xref, nv); Grow(ref j.ChartUv, nv * 2); Grow(ref j.Out0, ni);
            for (int i = 0; i < nv; i++)
            {
                if (r.Xref[i] >= (uint)j.VertCount) return Refusal.Xatlas;
                j.Xref[i] = (ushort)r.Xref[i];
                j.ChartUv[i * 2] = r.Uv[i * 2]; j.ChartUv[i * 2 + 1] = r.Uv[i * 2 + 1];
            }
            for (int i = 0; i < ni; i++)
            {
                if (r.Index[i] >= (uint)nv) return Refusal.Xatlas;
                j.Out0[i] = (ushort)r.Index[i];
            }
            j.OutVerts = nv; j.Out0N = ni; j.W = (ushort)r.Width; j.H = (ushort)r.Height; j.Charts = (int)r.ChartCount; j.Loose = (int)r.LooseVerts;
            return Refusal.None;
        }
        finally { cx_chart_free(&r); }
    }

    /// <summary>De-interleaved positions of the charted vertices, for the simplifier.</summary>
    private static void Expand(Job j)
    {
        Grow(ref j.OutPos, j.OutVerts * 3);
        for (int i = 0; i < j.OutVerts; i++)
        {
            int s = j.Xref[i] * 3;
            j.OutPos[i * 3] = j.Pos[s]; j.OutPos[i * 3 + 1] = j.Pos[s + 1]; j.OutPos[i * 3 + 2] = j.Pos[s + 2];
        }
    }

    /// <summary>MeshLod's two levels over the CHARTED mesh -- simplify runs after the chart, so every level carries
    /// chart UVs -- with MeshLod's targets, error budgets and "kept only if it saves enough" rule. Chart seams are
    /// attribute discontinuities, which the simplifier locks.</summary>
    private static void Simplify(Job j)
    {
        int tris = j.Out0N / 3;
        j.Out1N = j.Out2N = 0;
        if (tris < MeshLod.MinTris) return;
        Grow(ref j.Out1, j.Out0N); Grow(ref j.Out2, j.Out0N);
        j.Out1N = MeshSimplify.Run(j.Out0, j.Out0N, j.OutPos, j.OutVerts, (int)(tris * MeshLod.Target1), MeshLod.Error1, j.Out1);
        if (j.Out1N > tris * 3 * (1f - MeshLod.MinSaving)) j.Out1N = 0;
        j.Out2N = MeshSimplify.Run(j.Out0, j.Out0N, j.OutPos, j.OutVerts, (int)(tris * MeshLod.Target2), MeshLod.Error2, j.Out2);
        if (j.Out2N > tris * 3 * (1f - MeshLod.MinSaving) || j.Out2N >= j.Out1N && j.Out1N > 0) j.Out2N = 0;
    }

    /// <summary>Collect what the worker finished into the arenas. Render thread, inside the bracket, once a
    /// frame, before <c>BuildBatches</c>: the only place a GL name is touched.</summary>
    internal static void Pump()
    {
        if (!_jobs.Running || Gl.CreateBuffers == null) return;
        while (_jobs.TryTake(out Job j))
        {
            var e = new Entry { Done = true, Refused = j.Refused, Content = j.Content, Bmin = j.Bmin, Bmax = j.Bmax, Sig = j.Sig, Range = -1 };
            if (j.Hit) _hit++; else _missed++;
            if (j.Stale) _stale++;
            int lod1 = 0, lod2 = 0;
            if (j.Refused == Refusal.None)
            {
                if (!j.Hit) _built++;
                e.Range = FileRange(j);
                if (e.Range < 0) e.Refused = Refusal.Arena;
                else
                {
                    CX.Alloy.Db.MeshArena.Runs(e.Range, 1, out _, out _, out lod1, out _);
                    CX.Alloy.Db.MeshArena.Runs(e.Range, 2, out _, out _, out lod2, out _);
                    if (j.Out0N / 3 >= MeshLod.MinTris) _lodBig++;
                    if (j.Out1N > 0 || j.Out2N > 0) _lodKept++;
                    e.W = j.W; e.H = j.H; e.NV = j.OutVerts;
                }
            }
            if (e.Refused != Refusal.None)
            {
                _refused++; _refusedBy[(int)e.Refused]++;
                if (_logLeft-- > 0)
                    _log?.Info($"MeshChart: REFUSED {RefusalName[(int)e.Refused]} -- mesh {j.IdxCount / 3} tris, {j.VertCount} verts, content {j.Content:x16}");
            }
            else if (_logLeft-- > 0)
                _log?.Info($"MeshChart: mesh {j.IdxCount / 3} tris {j.VertCount} verts -> {j.OutVerts} charted verts, atlas {j.W}x{j.H} ({(j.Hit ? "cache hit" : "built")}), lod {lod1 / 3}/{lod2 / 3}" + (j.Loose > 0 ? $", {j.Loose} loose verts (no face)" : ""));
            if (_jobs.File(j.Key, j.Sig, e)) _epoch++;
            else Drop(e);   // nobody claimed it: the reference goes straight back
            _jobs.Recycle(j);
        }
    }

    /// <summary>Interleave one charted mesh into <see cref="CX.Alloy.Db.MeshArena.Fmt.Static"/>'s layout and
    /// file it. The chart UVs are the stream the arena's fourth column exists for; two charted meshes with the
    /// same bytes come back as ONE range, refcounted, like every other mesh in the arena.</summary>
    private static int FileRange(Job j)
    {
        int n = j.OutVerts;
        if (n <= 0 || j.Out0N < 3) return -1;
        if (_stage.Length < n * Stride) _stage = new float[Math.Max(n * Stride, (1 << 12) * Stride)];
        for (int i = 0; i < n; i++)
        {
            int o = i * Stride, src = j.Xref[i];
            _stage[o] = j.Pos[src * 3]; _stage[o + 1] = j.Pos[src * 3 + 1]; _stage[o + 2] = j.Pos[src * 3 + 2];
            if (j.HasNrm) { _stage[o + 3] = j.Nrm[src * 3]; _stage[o + 4] = j.Nrm[src * 3 + 1]; _stage[o + 5] = j.Nrm[src * 3 + 2]; }
            else _stage[o + 3] = _stage[o + 4] = _stage[o + 5] = 0f;
            if (j.HasUv) { _stage[o + 6] = j.Uv[src * 2]; _stage[o + 7] = j.Uv[src * 2 + 1]; }
            else _stage[o + 6] = _stage[o + 7] = 0f;
            _stage[o + 8] = j.ChartUv[i * 2]; _stage[o + 9] = j.ChartUv[i * 2 + 1];
        }
        return CX.Alloy.Db.MeshArena.AcquireRaw(PlacedKey(j.Content, j.Bmin), CX.Alloy.Db.MeshArena.Fmt.Static, n,
                                                _stage.AsSpan(0, n * Stride),
                                                j.Out0, j.Out0N, j.Out1, j.Out1N, j.Out2, j.Out2N,
                                                j.Bmin, j.Bmax);
    }

    /// <summary>Give one entry's arena reference back. The arena frees the range at its own frame edge once
    /// nothing holds it, so a key forgotten here never pulls geometry out from under a draw in flight.</summary>
    private static void Drop(in Entry e)
    {
        if (e.Done && e.Refused == Refusal.None && e.Range >= 0) CX.Alloy.Db.MeshArena.Release(e.Range);
    }

    /// <summary>This mesh's charted range at <paramref name="lod"/> (falling back to the finer level that
    /// exists). False = no chart; the caller draws the mesh's own uncharted range, which it must be able to do.</summary>
    internal static bool Range(int slot, int item, int lod, out int baseVertex, out nint off, out int count, out int level)
    {
        baseVertex = 0; off = 0; count = 0; level = 0;
        if (Vao == 0 || !_table.TryGetValue(KeyOf(slot, item), out Entry e) || !e.Done || e.Refused != Refusal.None
            || !Current(slot, item, in e)) return false;
        return CX.Alloy.Db.MeshArena.Runs(e.Range, lod, out baseVertex, out off, out count, out level);
    }

    /// <summary>Is a DB entry's chart still of the mesh its arena range holds? The key is the range INDEX, and an
    /// area hop recycles indices: until the new tenant is re-offered, the old entry would hand its draws the
    /// previous tenant's charted geometry -- a whole roof drawn from another tile's mesh, lightmap on only.</summary>
    private static bool Current(int slot, int item, in Entry e)
        => slot != DbSlot || e.Sig == CX.Alloy.Db.MeshArena.SigOf(item);

    /// <summary><see cref="Range"/> through the caller's <paramref name="m"/>: one probe per (key, LOD) per
    /// table epoch instead of one per call.</summary>
    internal static bool Range(ref RangeMemo m, int slot, int item, int lod, out int baseVertex, out nint off, out int count, out int level)
    {
        if (Vao == 0) { baseVertex = 0; off = 0; count = 0; level = 0; return false; }
        long key = KeyOf(slot, item);
        // the arena's epoch is part of this one: a compaction there rebases every offset a memo cached
        int epoch = _epoch + CX.Alloy.Db.MeshArena.Epoch;
        if (m.Epoch != epoch || m.Key != key || m.Lod != lod)
        {
            m.Hit = Range(slot, item, lod, out m.Base, out m.Off, out m.Count, out int lvl);
            m.Key = key; m.Epoch = epoch; m.Lod = (byte)lod; m.Level = (byte)lvl;
        }
        baseVertex = m.Base; off = m.Off; count = m.Count; level = m.Level;
        return m.Hit;
    }

    /// <summary>The atlas size a charted mesh's UVs are in, its content key and its own-space bounds, for the
    /// bake. False = not charted (yet, or refused).</summary>
    internal static bool Atlas(int slot, int item, out int w, out int h, out ulong content, out Vector3 bmin, out Vector3 bmax)
    {
        w = h = 0; content = 0; bmin = bmax = default;
        if (!_table.TryGetValue(KeyOf(slot, item), out Entry e) || !e.Done || e.Refused != Refusal.None
            || !Current(slot, item, in e)) return false;
        w = e.W; h = e.H; content = e.Content; bmin = e.Bmin; bmax = e.Bmax;
        return true;
    }

    internal static void Drew(int indices) => _drawn += indices / 3;

    /// <summary>A VDO slot was vacated or re-pointed: every range keyed on it names geometry that is no
    /// longer there. Arena bytes stay until <see cref="Revoke"/>.</summary>
    internal static void ForgetSlot(int slot)
    {
        _jobs.ForgetSlot(slot, static e => Drop(e));
        _epoch++;
    }

    /// <summary>Drop every entry no enrolled part keys: an in-module area change is no world edge, so without
    /// this the table carries the last areas' entries. Their arena bytes count as dead; the next Pump compacts.
    /// Cold, once per area -- an entry a not-yet-enrolled part will want is offered again at its enrolment.</summary>
    internal static void Sweep(HashSet<long> live)
    {
        if (_table.Count == 0) return;
        List<long>? dead = null;
        foreach (var kv in _table)
            if (!live.Contains(kv.Key)) (dead ??= new List<long>()).Add(kv.Key);
        if (dead == null) return;
        _epoch++;
        int verts = 0;
        for (int i = 0; i < dead.Count; i++)
        {
            if (_table.TryGetValue(dead[i], out Entry e)) { Drop(e); if (e.Done && e.Refused == Refusal.None) verts += e.NV; }
            _table.Remove(dead[i]);
            // a DB key's item is its source range, still marked offered: unmarked, a later seat charts it again
            if ((int)(dead[i] >> 32) == DbSlot) CX.Alloy.Db.MeshArena.ClearOffered((int)dead[i]);
        }
        _log?.Info($"MeshChart: swept {dead.Count} entries nobody enrolled ({verts} vertices) -- {_table.Count} kept");
    }

    /// <summary>The world edge: every (slot, item) key means nothing, and every arena reference those keys
    /// held goes back. The arena itself keeps what OTHER holders still want -- its ranges are keyed on
    /// content, so an area change invalidates nothing in it.</summary>
    internal static void Revoke()
    {
        foreach (var kv in _table) Drop(kv.Value);
        _jobs.Revoke();
        // and the offer marks, or the next area charts nothing: the mark says a range is the charter's already
        CX.Alloy.Db.MeshArena.ClearOfferMarks();
        _epoch++;
        _offered = _hit = _missed = _built = _refused = _stale = _lodBig = _lodKept = 0; Array.Clear(_refusedBy);
        _logLeft = LogCap;
    }

    /// <summary>The context is gone. Nothing to do: the names, the mirrors and the re-upload are the arena's
    /// (<see cref="CX.Alloy.Db.MeshArena.ContextLost"/>, registered on its own pass), and this table holds
    /// range ids, which survive it.</summary>
    internal static void ContextLost() { }

    /// <summary>The <c>lod</c> panel's clause for the charted population, which never reaches <see cref="MeshLod"/>.</summary>
    internal static string LodNote()
        => !Armed ? "" : $"{Done - _refused} charted meshes, {_lodBig} of them at least {MeshLod.MinTris} triangles, {_lodKept} simplified";

    internal static string Describe()
        => !_jobs.Running ? "not running"
         : !Lightmap.G.Master!.V ? "Off — every mesh draws from the game's own geometry"
         : $"{_offered} meshes offered: {_hit} charts from cache, {_built} built, {_refused} refused; "
           + CX.Alloy.Db.MeshArena.Describe() + "; " + LightmapBake.Describe();

    /// <summary>One clause of the Alloy period line: charts hit/missed/built/refused, and what drew from them.</summary>
    internal static string Stats(int frames)
    {
        int d = _drawn; _drawn = 0;
        if (_offered == 0 && d == 0) return "";
        if (frames <= 0) frames = 1;
        string why = "";
        for (int k = 1; k < RefusalName.Length; k++) if (_refusedBy[k] > 0) why += $" {RefusalName[k]} {_refusedBy[k]}";
        return $"charts {_hit} hit / {_missed} missed / {_built} built / {_refused} refused{(why.Length > 0 ? " (" + why.Trim() + ")" : "")}"
               + (_tableFull > 0 ? $", {_tableFull} offers PAST THE {MaxEntries}-MESH TABLE" : "")
               + (_stale > 0 ? $", {_stale} stale files replaced" : "")
               + $", {d / frames} charted tris per frame";
    }
}
