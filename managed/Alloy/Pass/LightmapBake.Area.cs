using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Threading;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>An area's whole bake, keyed by the area itself and read while <c>LoadArea</c> runs: the key is
/// folded out of the read-aheads (tileset resref, each tile's id/orientation/height, each static placeable's
/// appearance and quantised place, the flag word, the settings the look derives from), never the server's
/// per-session area id. A plan is a HINT about an area's shape; every rect it names is still key-checked.</summary>
internal sealed class LightmapAreaPlan
{
    /// <summary>"CXLA" little-endian.</summary>
    private const uint Magic = 0x414c5843;   // not-an-offset: the file's first four bytes
    /// <summary>Bumped with the layout below or with anything the plan's fields mean (4: shelves and placed
    /// area in block-aligned cells).</summary>
    internal const int Version = 4;
    /// <summary>Unions one plan may ask the install to hold at once. A page's union is at most 96 MiB (three planes), and an
    /// area that wants more than this is refused rather than allowed to bloat the heap while it waits for the
    /// edge. ponytail: upload straight out of the inflate if an area ever legitimately needs more.</summary>
    internal const long MaxUnionBytes = 1152L << 20;

    /// <summary>The area key this plan answers, and the settings hash it was baked under (the key folds the
    /// same hash, so the two disagreeing means one of them was read across a knob change).</summary>
    internal ulong Key, Settings;
    /// <summary>The closed static set, exactly as the walk left it.</summary>
    internal ulong SetHash;
    internal Vector4[] SetPosR = [];
    internal Vector3[] SetCol = [];
    /// <summary>The page plan: how many pages, the area-wide halving, the texels placed, and per page its
    /// class and its open shelf.</summary>
    internal int Layers, AreaHalve, PackPage, FinePage;
    internal long PlacedArea;
    internal byte[] PageClass = [];
    internal int[] ShelfX = [], ShelfY = [], ShelfH = [];
    /// <summary>The page blobs whose unions cover this plan's rects, and where each sits on its page.</summary>
    internal BakeCache.Union[] Unions = [];
    internal Rec[] Rects = [];
    /// <summary>Filled by the cache thread on the read path: each union's inflated bytes and its own rect table.</summary>
    internal byte[]?[] UnionBytes = [];
    internal BakeCache.Entry[]?[] UnionTables = [];

    /// <summary>One rect of the plan: its key (and the pre-halving key every re-halving derives from), its
    /// place on the page and the world sphere the bake drew it at.</summary>
    internal struct Rec
    {
        internal ulong Key, Key0;
        internal int Halved, Tier, Page, X, Y, W, H;
        internal float S;
        internal Vector3 C; internal float R;
    }

    internal void SerializeTo(Stream s)
    {
        var w = new BinaryWriter(s);
        w.Write(Magic); w.Write(Version); w.Write(Key); w.Write(Settings);
        w.Flush();
        using var z = new DeflateStream(s, CompressionLevel.Fastest, leaveOpen: true);
        var b = new BinaryWriter(z);
        b.Write(SetHash); b.Write(SetPosR.Length);
        for (int i = 0; i < SetPosR.Length; i++)
        {
            b.Write(SetPosR[i].X); b.Write(SetPosR[i].Y); b.Write(SetPosR[i].Z); b.Write(SetPosR[i].W);
            b.Write(SetCol[i].X); b.Write(SetCol[i].Y); b.Write(SetCol[i].Z);
        }
        b.Write(Layers); b.Write(AreaHalve); b.Write(PackPage); b.Write(FinePage); b.Write(PlacedArea);
        b.Write(PageClass.Length);
        for (int i = 0; i < PageClass.Length; i++) { b.Write(PageClass[i]); b.Write(ShelfX[i]); b.Write(ShelfY[i]); b.Write(ShelfH[i]); }
        b.Write(Unions.Length);
        for (int i = 0; i < Unions.Length; i++)
        {
            ref BakeCache.Union u = ref Unions[i];
            b.Write(u.File); b.Write(u.Page); b.Write(u.X0); b.Write(u.Y0); b.Write(u.W); b.Write(u.H);
        }
        b.Write(Rects.Length);
        for (int i = 0; i < Rects.Length; i++)
        {
            ref Rec r = ref Rects[i];
            b.Write(r.Key); b.Write(r.Key0); b.Write(r.Halved); b.Write(r.Tier); b.Write(r.Page);
            b.Write(r.X); b.Write(r.Y); b.Write(r.W); b.Write(r.H); b.Write(r.S);
            b.Write(r.C.X); b.Write(r.C.Y); b.Write(r.C.Z); b.Write(r.R);
        }
        b.Flush();
    }

    /// <summary>The inverse. Null names the refusal (<c>version</c>, <c>truncation</c>) and never throws: a
    /// throw on the cache thread would strand the area edge waiting for an answer.</summary>
    internal static LightmapAreaPlan? Deserialize(Stream s, out string? why)
    {
        why = null;
        try
        {
            var r = new BinaryReader(s);
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Version) { why = "version"; return null; }
            var p = new LightmapAreaPlan { Key = r.ReadUInt64(), Settings = r.ReadUInt64() };
            using var z = new DeflateStream(s, CompressionMode.Decompress, leaveOpen: true);
            var b = new BinaryReader(z);
            p.SetHash = b.ReadUInt64();
            int n = b.ReadInt32();
            if ((uint)n > (uint)MaxSet) { why = "truncation"; return null; }
            p.SetPosR = new Vector4[n]; p.SetCol = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                p.SetPosR[i] = new Vector4(b.ReadSingle(), b.ReadSingle(), b.ReadSingle(), b.ReadSingle());
                p.SetCol[i] = new Vector3(b.ReadSingle(), b.ReadSingle(), b.ReadSingle());
            }
            p.Layers = b.ReadInt32(); p.AreaHalve = b.ReadInt32(); p.PackPage = b.ReadInt32(); p.FinePage = b.ReadInt32(); p.PlacedArea = b.ReadInt64();
            int pages = b.ReadInt32();
            if (pages != LightmapBake.MaxPages || (uint)p.Layers > (uint)pages || (uint)p.AreaHalve > 8u || p.PlacedArea < 0) { why = "truncation"; return null; }
            p.PageClass = new byte[pages]; p.ShelfX = new int[pages]; p.ShelfY = new int[pages]; p.ShelfH = new int[pages];
            for (int i = 0; i < pages; i++)
            {
                p.PageClass[i] = b.ReadByte(); p.ShelfX[i] = b.ReadInt32(); p.ShelfY[i] = b.ReadInt32(); p.ShelfH[i] = b.ReadInt32();
                if ((uint)p.ShelfX[i] > LightmapBake.PageSize || (uint)p.ShelfY[i] > LightmapBake.PageSize || (uint)p.ShelfH[i] > LightmapBake.PageSize) { why = "truncation"; return null; }
            }
            int un = b.ReadInt32();
            if ((uint)un > (uint)LightmapBake.MaxPages * 4u) { why = "truncation"; return null; }
            p.Unions = new BakeCache.Union[un];
            for (int i = 0; i < un; i++)
            {
                var u = new BakeCache.Union { File = b.ReadString(), Page = b.ReadInt32(), X0 = b.ReadInt32(), Y0 = b.ReadInt32(), W = b.ReadInt32(), H = b.ReadInt32() };
                if (u.File.Length == 0 || u.File.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || (uint)u.Page >= p.Layers) { why = "truncation"; return null; }
                p.Unions[i] = u;
            }
            int rn = b.ReadInt32();
            if ((uint)rn > (uint)MaxRects) { why = "truncation"; return null; }
            p.Rects = new Rec[rn];
            for (int i = 0; i < rn; i++)
            {
                var rec = new Rec
                {
                    Key = b.ReadUInt64(), Key0 = b.ReadUInt64(), Halved = b.ReadInt32(), Tier = b.ReadInt32(), Page = b.ReadInt32(),
                    X = b.ReadInt32(), Y = b.ReadInt32(), W = b.ReadInt32(), H = b.ReadInt32(), S = b.ReadSingle(),
                };
                rec.C = new Vector3(b.ReadSingle(), b.ReadSingle(), b.ReadSingle()); rec.R = b.ReadSingle();
                if ((uint)rec.Page >= p.Layers || rec.W <= 0 || rec.H <= 0
                    || (long)rec.X + rec.W > LightmapBake.PageSize || (long)rec.Y + rec.H > LightmapBake.PageSize
                    || rec.X < 0 || rec.Y < 0 || !(rec.S > 0f)) { why = "truncation"; return null; }
                p.Rects[i] = rec;
            }
            return p;
        }
        catch (Exception) { why = "truncation"; return null; }
    }

    /// <summary>Static lights and rects a plan may name: both are a whole area's, and a file claiming more is
    /// not one of ours.</summary>
    private const int MaxSet = 1 << 16, MaxRects = 1 << 20;
}

internal static unsafe partial class LightmapBake
{
    /// <summary>The key of the area <c>LoadArea</c> just read, handed over from the game thread (the
    /// read-aheads fold it); 0 = no key, which the edge logs and counts.</summary>
    private static long _areaKeyPending;
    /// <summary>The key of the area the edge is installing for, the plan once it arrives, and whether the
    /// answer is still outstanding.</summary>
    private static ulong _areaKey;
    private static LightmapAreaPlan? _areaPlan;
    private static bool _areaWant, _areaAdopted, _areaSetOk, _areaWritten;
    /// <summary>The cursor over the plan's unions (the union, the next row of it) and the flag for "every one
    /// of them is on its page", the release's "pages uploaded" arm. A plan may name no union at all, so the
    /// flag says done, not the cursor.</summary>
    private sealed class AreaUploadState
    {
        public int Next, Row;
        public bool Done;
    }
    private static AreaUploadState _areaUp = new();
    /// <summary>The page blobs this area's rects live in: what the plan named plus every union this visit
    /// baked. Written to the plan at the release.</summary>
    private static readonly List<BakeCache.Union> _areaUnions = new();
    /// <summary>Area-cache census for the release line: rects taken off the plan, rects that had to be probed
    /// per rect after all, newcomers the plan never named, adopted rects nobody enrolled, and the refusals.</summary>
    private static int _areaAdoptedRects, _areaProbed, _areaNew, _areaKeyless, _areaLate, _areaDrift, _areaMisplaced;
    /// <summary>Wall clock the union upload may spend a frame, in play and under the hold's backdrop (which
    /// waits for the upload anyway). A union goes up in bands of <see cref="AreaBandBytes"/>, so one page-sized
    /// blob is many frames, not one long one.</summary>
    private const double AreaUploadMs = 1.0, HoldAreaUploadMs = 30.0;
    private const long AreaBandBytes = 4L << 20;

    /// <summary>Pure: rows of a <paramref name="w"/>-wide union the band starting at <paramref name="row"/>
    /// uploads -- whole block rows, at least one, never past <paramref name="h"/>.</summary>
    internal static int BandRows(int w, int h, int row)
        => (int)Math.Min(h - row, Math.Max(Block, AreaBandBytes / ((long)w * TexelBytes) & ~(long)(Block - 1)));

    /// <summary>The read-aheads have folded this area at <c>LoadArea</c>'s return: post the plan read to
    /// <c>cx-lmcache</c> now, so the inflate runs in the seconds before the AreaLoaded send rather than
    /// serially after the set closes. Game thread; 0 = the peeks could not name the area.</summary>
    internal static void OnAreaKey(ulong key)
    {
        Interlocked.Exchange(ref _areaKeyPending, (long)key);
        _areaAsked = key != 0 && Enabled;
        if (_areaAsked) _cache.PrefetchArea(key);
    }

    /// <summary>A plan read is queued on <c>cx-lmcache</c> for the pending key and its answer not yet taken.</summary>
    private static bool _areaAsked;

    /// <summary>Every dial the plan is only valid under: the bake's own look plus the packing knobs, which
    /// decide a rect's size and which tier it is packed at. Folded into the area key AND stored in the plan,
    /// so a plan read across a knob change is refused rather than adopted.</summary>
    internal static ulong AreaSettingsHash()
    {
        ulong h = ShadowLook();
        h = Mix(h, Lightmap.Density.V);
        h = Mix(h, Lightmap.Tiers.V);
        h = Mix(h, Lightmap.Near.V);
        h = Mix(h, Lightmap.Window.V);
        h = Mix(h, PageSize);
        return h;
    }

    /// <summary>Frames the hold's sweep waits for the plan's answer before it mints rects of its own: a rect minted
    /// first refuses the whole plan, and the read began at LoadArea's return, so it is normally in by the edge.</summary>
    private const int AreaWaitFrames = 30;
    private static int _areaWantFrame;
    /// <summary>The sweep stands still while the plan's answer is outstanding, hold or not, up to <see cref="AreaWaitFrames"/>.</summary>
    private static bool AreaAwaited => _areaWant && _frame - _areaWantFrame < AreaWaitFrames;

    /// <summary>Taken at the AreaLoaded send, the one moment always after this area's <c>LoadArea</c>, and again
    /// by an edge under the hold. Not consumed: each Revoke wipes what the last adopted, so the key stands until
    /// the next <c>LoadArea</c>, and an edge after the answer was taken asks <c>cx-lmcache</c> again.</summary>
    private static void TakeAreaKey()
    {
        _areaKey = (ulong)Interlocked.Read(ref _areaKeyPending);
        _areaWant = _areaKey != 0 && Enabled; _areaWantFrame = _frame;
        if (_areaWant && !_areaAsked) { _areaAsked = true; _cache.PrefetchArea(_areaKey); }
        if (_areaKey == 0) { _areaKeyless++; _log?.Info("LightmapBake: no area key for this load -- the area bakes from its rects (CX-55's read-ahead stood down, or the load named no tiles)"); }
    }

    /// <summary>The world edge: the plan, its unions and the census describe an area that is gone. The key the
    /// load folded is NOT cleared here -- it is posted before the edge and consumed by
    /// <see cref="TakeAreaKey"/> after it.</summary>
    private static void AreaForget()
    {
        AreaSnapAbandon();   // before the unions go: a plan still waiting on a readback is written as it stands
        _areaKey = 0; _areaPlan = null; _areaWant = false; _areaAdopted = false; _areaSetOk = false; _areaWritten = false;
        _areaUp = new(); _areaUnions.Clear();
        _areaAdoptedRects = _areaProbed = _areaNew = _areaDrift = _areaMisplaced = _areaBase = _areaClaimed = 0;
    }

    /// <summary>Every union of the plan is on its page. The release's "pages uploaded" arm; true when no plan
    /// was adopted at all.</summary>
    private static bool AreaUploadsIn => _areaPlan == null || _areaUp.Done;

    /// <summary>The latest plan answer off <c>cx-lmcache</c>, kept until an edge wants it: frames that run
    /// between <c>LoadArea</c>'s return and the send would otherwise drain it with nobody asking, and the edge
    /// then waits on an answer that never comes.</summary>
    private static ulong _answerKey;
    private static LightmapAreaPlan? _answerPlan;
    private static bool _answerIn;

    /// <summary>Move every queued answer into the stash; one for the load's own key is never displaced by a stale one.</summary>
    /// <summary>Once a frame while an answer is outstanding: take the plan and install it, or take the miss.
    /// Refuses -- by name, counted -- a plan for another area, one whose settings moved under it, and one
    /// that arrives after the live walk has already closed the set or minted a rect.</summary>
    private static void TakeAnswers()
    {
        ulong want = (ulong)Interlocked.Read(ref _areaKeyPending);
        while (_cache.TryTakeArea(out ulong k, out LightmapAreaPlan? p))
            if (k == want || !_answerIn || _answerKey != want) { _answerKey = k; _answerPlan = p; _answerIn = true; }
    }

    /// <summary>The load's own plan is in, and under the settings of the moment: the area is a full cache hit.</summary>
    private static bool AreaHitReady(out int rects)
    {
        TakeAnswers();
        LightmapAreaPlan? p = _answerPlan;
        bool hit = _answerIn && p != null && _answerKey == (ulong)Interlocked.Read(ref _areaKeyPending) && p.Settings == AreaSettingsHash();
        rects = hit ? p!.Rects.Length : 0;
        return hit;
    }

    /// <summary>A full hit holds the send with no backdrop (the engine's own loadscreen stays up) until the
    /// plan's baseline rects are in use: released straight away, a warm entry drew live lighting that switched to
    /// baked over the next second as parts claimed their rects.</summary>
    private static bool _silentHold;
    /// <summary>The plan's baseline rects, and how many a slot has claimed.</summary>
    private static int _areaBase, _areaClaimed;
    /// <summary>The silent hold's ceiling: a plan naming parts this visit never spawns must not stall the entry.</summary>
    private const float SilentCapSeconds = 3f;
    /// <summary>Half the plan claimed; <see cref="BaselineIn"/> adds the quiet window past the last claim.</summary>
    private static bool ClaimsIn => !_silentHold || _areaClaimed * 2 >= _areaBase;

    private static void Claimed(int ri)
    {
        _rects[ri].Claimed = true;
        if (_rects[ri].Tier == Baseline) _areaClaimed++;
        Arrived(WhyClaim);
    }

    private static void InstallAreaCache()
    {
        TakeAnswers();
        if (!_areaWant || !_answerIn || _answerKey != _areaKey) return;
        ulong key = _answerKey; LightmapAreaPlan? plan = _answerPlan;
        if (plan == null) { _answerIn = false; _answerPlan = null; _areaWant = false; _areaAsked = false; return; }
        // CX-56 step 2 closes the set on the edge frame's own walk, which runs before this: the same set is the check passed
        bool confirmed = _closed && SetHash == plan.SetHash;
        string? no = (SetHash != 0 || _closed) && !confirmed ? $"the live walk closed on another static set first ({SetHash:x16}, the plan has {plan.SetHash:x16}: {SetDiff(plan.SetPosR, plan.SetCol, plan.SetPosR.Length)})"
                   : _rectN != 0 ? $"{_rectN} rects were already minted"
                   : plan.Settings != AreaSettingsHash() ? "the bake settings moved between the load and the edge"
                   : plan.Layers > _gl.Layers ? $"{plan.Layers} pages, past the {_gl.Layers}-page pool lightmap.vram allows"
                   : null;
        _answerIn = false; _answerPlan = null; _areaWant = false; _areaAsked = false;
        if (no != null)
        {
            _areaLate++;
            _log?.Always($"LightmapBake: the area bake cache for {key:x16} is REFUSED at the edge -- {no}; the area packs and probes its rects as on a first visit");
            return;
        }
        AdoptPlan(plan);
        if (confirmed) { _areaSetOk = true; _log?.Info($"LightmapBake: area bake cache confirmed -- the edge walk had already closed on the plan's static set {SetHash:x16}"); }
    }

    /// <summary>Install a plan: the static set (which arms Sweep and Enrol at once), the page plan, every rect
    /// as already placed, and the pages to hold them. The unions upload over the next frames; until one lands
    /// its rects sit Probing, exactly as a freshly packed rect does, so nothing reads a page that holds no
    /// bake. The pages are resident before this runs.</summary>
    private static void AdoptPlan(LightmapAreaPlan plan)
    {
        _setPosR = plan.SetPosR; _setCol = plan.SetCol; _setN = plan.SetPosR.Length;
        SetHash = plan.SetHash;
        _areaHalve = plan.AreaHalve; _placedArea = plan.PlacedArea;
        Array.Copy(plan.PageClass, _pageClass, MaxPages);
        Array.Copy(plan.ShelfX, _shelfX, MaxPages); Array.Copy(plan.ShelfY, _shelfY, MaxPages); Array.Copy(plan.ShelfH, _shelfH, MaxPages);
        _packPage = plan.PackPage; _finePage = plan.FinePage;
        for (int k = 0; k < plan.Rects.Length; k++)
        {
            ref LightmapAreaPlan.Rec p = ref plan.Rects[k];
            int ri = NewRect();
            _rects[ri] = new Rect
            {
                Key = p.Key, Key0 = p.Key0, Slot = -1, Vao = -1, Item = -1, Page = p.Page, X = p.X, Y = p.Y, W = p.W, H = p.H,
                S = p.S, C = p.C, R = p.R, State = RState.Probing, Halved = p.Halved, Tier = p.Tier, Adopted = true,
            };
            (_owners[ri] ??= new List<int>(2)).Clear();
            _byKey[p.Key] = ri;
            _pending++;
        }
        _areaBase = 0; _areaClaimed = 0;
        foreach (var p in plan.Rects) if (p.Tier == Baseline) _areaBase++;
        _areaPlan = plan; _areaUp = new(); _areaAdopted = true; _areaAdoptedRects = plan.Rects.Length;
        _areaUnions.Clear(); _areaUnions.AddRange(plan.Unions);
        Arrived(WhyAdopt);
        _log?.Info($"LightmapBake: area bake cache {_areaKey:x16} adopted -- {plan.Rects.Length} rects on {plan.Layers} pages, "
                 + $"{plan.Unions.Length} page blob(s) to upload, static set {plan.SetHash:x16} ({_setN} lights); the walk still closes its own set and is compared against it");
    }

    /// <summary>Upload the adopted unions in row bands under a wall-clock cap (<see cref="AreaUploadMs"/>);
    /// once a union's last band is up, every rect its own table names lands. A rect no union covers is probed
    /// per rect from here -- the cache may hold it in another area's blob, and if it does not it bakes.</summary>
    private static void AreaUpload()
    {
        LightmapAreaPlan? plan = _areaPlan;
        AreaUploadState up = _areaUp;
        if (plan == null || up.Done) return;
        long t0 = Stopwatch.GetTimestamp(), wall = (long)((Hold ? HoldAreaUploadMs : AreaUploadMs) * Stopwatch.Frequency / 1000);
        while (up.Next < plan.Unions.Length && Stopwatch.GetTimestamp() - t0 < wall)
        {
            int u = up.Next;
            ref BakeCache.Union un = ref plan.Unions[u];
            byte[]? bytes = plan.UnionBytes[u];
            BakeCache.Entry[]? table = plan.UnionTables[u];
            if (bytes != null && table != null)
            {
                int y = up.Row, rows = BandRows(un.W, un.H, y);
                fixed (byte* p = bytes)
                {
                    Gl.CompressedTextureSubImage3D(_gl.TexA, 0, un.X0, un.Y0 + y, un.Page, un.W, rows, 1, FormatA, un.W * rows, &p[UnionOffset(0, 0, y, un.W, un.H)]);
                    Gl.CompressedTextureSubImage3D(_gl.TexB, 0, un.X0, un.Y0 + y, un.Page, un.W, rows, 1, FormatB, un.W * rows, &p[UnionOffset(1, 0, y, un.W, un.H)]);
                    Gl.TextureSubImage3D(_gl.TexC, 0, un.X0, un.Y0 + y, un.Page, un.W, rows, 1, Gl.RGBA, Gl.UNSIGNED_BYTE, &p[UnionOffset(2, 0, y, un.W, un.H)]);
                }
                up.Row = y + rows;
                if (up.Row < un.H) continue;
            }
            up.Next++; up.Row = 0;
            plan.UnionBytes[u] = null;   // the inflate is the heap's biggest tenant: let it go once it is up
            if (bytes == null || table == null) continue;
            for (int k = 0; k < table.Length; k++)
            {
                if (!_byKey.TryGetValue(table[k].Key, out int ri) || _rects[ri].State != RState.Probing) continue;
                ref Rect r = ref _rects[ri];
                // the key alone is not enough: a rect minted this visit can carry a key the blob holds at
                // another place, and landing it would hand it another placement's texels. It stays Probing
                // and is probed per rect below, which is what a rect no union covers does anyway.
                if (!UnionPlaces(un, table[k], r.Page, r.X, r.Y, r.W, r.H)) { _areaMisplaced++; continue; }
                r.Unioned = true;
                Land(ri, true); _pending--;
            }
        }
        if (up.Next < plan.Unions.Length) return;
        up.Done = true;
        int probed = 0;
        for (int ri = 0; ri < _rectN; ri++)
        {
            ref Rect r = ref _rects[ri];
            if (!r.Adopted || r.State != RState.Probing) continue;
            // no union of the plan covers it: the per-rect index may still hold its key from another area's
            // page blob, and a miss there queues it for the bake exactly as a newcomer's probe does
            _cache.Probe(ri, r.Key, r.W, r.H);
            probed++;
        }
        _areaProbed = probed;
        _log?.Info($"LightmapBake: area bake cache uploaded -- {plan.Unions.Length} union(s) on {plan.Layers} pages, {_landed} rects landed"
                 + (probed > 0 ? $", {probed} rect(s) no union covered are probed per rect" : "")
                 + (_areaMisplaced > 0 ? $", {_areaMisplaced} union key(s) named a rect this visit placed elsewhere" : ""));
    }

    /// <summary>The walk closed its own set under an adopted plan: confirm it or say it drifted. True = the
    /// close is settled here and <c>EndWalk</c>'s tail (which would repack the area out from under the
    /// adoption) must not run.</summary>
    private static bool AreaConfirm(ulong hash)
    {
        if (!_areaAdopted) return false;
        _areaChanged = false;
        if (hash == SetHash)
        {
            _areaSetOk = true;
            _log?.Info($"LightmapBake: area bake cache confirmed -- the walk closed on the same static set {hash:x16} ({_setN} lights)");
            return true;
        }
        _log?.Always($"LightmapBake: area bake cache set DRIFTED -- the walk closed on {hash:x16} with {_stN} lights, the plan was baked against {SetHash:x16} with {_setN}; every rect re-keys -- {SetDiff(_setPosR, _setCol, _setN)}");
        _areaDrift++;
        if (_setPosR.Length < _stN) { _setPosR = new Vector4[_stN]; _setCol = new Vector3[_stN]; }
        Array.Copy(_stPosR, _setPosR, _stN); Array.Copy(_stCol, _setCol, _stN);
        _setN = _stN;
        SetHash = hash;
        AbortBake();
        if (hash == 0) Repack(); else RekeyAll();
        return true;
    }

    /// <summary>A union this visit read back joins the plan: a page bake's own readback, or -- while
    /// <see cref="_uSnap"/> is up -- one of <see cref="AreaSnap"/>'s whole pages, which replaces every baked
    /// union of that page in the plan that goes out.</summary>
    private static void AreaUnionBaked(string file, int page, int x0, int y0, int w, int h)
        => (_uSnap ? _areaSnapUnions : _areaUnions).Add(new BakeCache.Union { File = file, Page = page, X0 = x0, Y0 = y0, W = w, H = h });

    /// <summary>The plan's union list: what this visit baked, minus every page a snapshot re-read whole, plus
    /// those snapshots. Pure -- the self-check drives it; cold -- once per visit.</summary>
    internal static BakeCache.Union[] MergeUnions(List<BakeCache.Union> baked, List<BakeCache.Union> snap)
    {
        Span<bool> re = stackalloc bool[MaxPages];
        for (int i = 0; i < snap.Count; i++) if ((uint)snap[i].Page < MaxPages) re[snap[i].Page] = true;
        var all = new List<BakeCache.Union>(baked.Count + snap.Count);
        for (int i = 0; i < baked.Count; i++) if ((uint)baked[i].Page >= MaxPages || !re[baked[i].Page]) all.Add(baked[i]);
        all.AddRange(snap);
        return all.ToArray();
    }

    /// <summary>Whether a union's table entry names the rect the plan placed there: same page, same size, and
    /// the entry's union-relative origin IS the rect's page origin. Pure.</summary>
    internal static bool UnionPlaces(in BakeCache.Union u, in BakeCache.Entry e, int page, int x, int y, int w, int h)
        => page == u.Page && w == e.W && h == e.H && x == u.X0 + e.X && y == u.Y0 + e.Y;

    /// <summary>The plan built and waiting for the pages a snapshot still has to read back.</summary>
    private static LightmapAreaPlan? _areaPlanOut;
    /// <summary>Pages whose landed rects this visit did not put in a blob of its own, the cursor over them, and
    /// the unions the readbacks produced.</summary>
    private static readonly bool[] _areaSnapWant = new bool[MaxPages];
    private static int _areaSnapNext;
    private static readonly List<BakeCache.Union> _areaSnapUnions = new();
    /// <summary>The rect list of the readback in flight (the blob's table), and whether that readback is a
    /// snapshot rather than a page bake's own.</summary>
    private static readonly List<int> _areaSnapRects = new();
    private static bool _uSnap;
    /// <summary>Census: pages read back whole this session, pages an area edge took before their readback,
    /// plans whose snapshot would not fit <see cref="LightmapAreaPlan.MaxUnionBytes"/>, and -- per plan --
    /// pages whose readback the driver could not queue at all.</summary>
    private static int _areaSnapped, _areaSnapLost, _areaSnapCapped, _areaSnapMissed;
    /// <summary>Frames the bake must sit still before a visit writes its plan on its own. The hold's release
    /// writes it the moment it lets go; a visit that adopted a plan holds nothing at all (the send passes
    /// straight through on a hit), and the area edge -- the only other writer -- Revokes the pages, so without
    /// this a plan whose rects came off the per-rect index could never be read back and re-written whole.</summary>
    private const int AreaCalmFrames = 120;
    private static int _areaCalmFrom;

    /// <summary>Everything <see cref="AreaWrite"/> would refuse a plan for, as a silent predicate: the frame
    /// driver asks it every frame, so a refusal here must not log.</summary>
    private static bool AreaWriteReady
        => _areaKey != 0 && !_areaWritten && Enabled && SetHash != 0 && _closed && !_edgePending
        && (!_areaAdopted || _areaSetOk) && _rectN > 0 && _pending == 0 && _toPack.Count == 0 && _bakePage < 0
        && _refused + _pastCap == 0 && _bakeSkips == 0 && MeshChart.InFlight == 0;

    /// <summary>Once a frame: drive the page readbacks a pending plan is waiting on, or -- for a visit no hold
    /// released -- write the plan once the bake has been calm for <see cref="AreaCalmFrames"/>.</summary>
    private static void AreaSnap()
    {
        if (_areaPlanOut != null) { AreaSnapStep(); return; }
        if (!AreaWriteReady) { _areaCalmFrom = _frame; return; }
        if (_frame - _areaCalmFrom >= AreaCalmFrames) AreaWrite(true);
    }

    /// <summary>One page per readback: the one in flight finishes (<see cref="CollectReadback"/>) before the
    /// next is queued, so the snapshot costs one region's read and its sliced host copy at a time.</summary>
    private static void AreaSnapStep()
    {
        // CX-102: !ReadbackIdle, not the fence alone -- a copy still draining out of the open map, or a landing
        // waiting behind it, IS a readback in flight. Stepping the cursor over a page while one is would
        // consume that page (and every one after it) and count it missed for ever, and the hold's release
        // arrives one or two frames after the last landing queued its readback, so that was every release.
        if (!ReadbackIdle || _bakePage >= 0 || _rekeyPending) return;
        while (_areaSnapNext < MaxPages)
        {
            int p = _areaSnapNext++;
            if (!_areaSnapWant[p]) continue;
            if (AreaSnapQueue(p, out bool empty)) return;
            if (!empty) _areaSnapMissed++;   // the readback itself was refused: that page's rects go without a union
        }
        AreaPlanOut(0);
    }

    /// <summary>The page region a snapshot reads back: the bounding box of the page's landed rects' cells,
    /// the same rectangle <see cref="FinishPage"/> reads after a bake -- whole blocks, gutters in. False = the
    /// page holds no landed rect any more.</summary>
    private static bool AreaSnapRegion(int page, out int x0, out int y0, out int w, out int h)
    {
        int ax0 = PageSize, ay0 = PageSize, ax1 = 0, ay1 = 0;
        for (int ri = 0; ri < _rectN; ri++)
        {
            ref Rect r = ref _rects[ri];
            if (r.Page != page || r.State != RState.Landed) continue;
            CellOf(r, out int cx, out int cy, out int cw, out int ch);
            ax0 = Math.Min(ax0, cx); ay0 = Math.Min(ay0, cy);
            ax1 = Math.Max(ax1, cx + cw); ay1 = Math.Max(ay1, cy + ch);
        }
        x0 = Math.Max(ax0, 0); y0 = Math.Max(ay0, 0);
        w = Math.Min(ax1, PageSize) - x0; h = Math.Min(ay1, PageSize) - y0;
        return w > 0 && h > 0;
    }

    /// <summary>Queue one page's readback off the resident pair. Its table is every landed rect of the page --
    /// the set the plan itself writes -- so the union covers the plan whole. False = nothing queued, and
    /// <paramref name="empty"/> then says the page simply holds no landed rect any more rather than the
    /// readback having been refused.</summary>
    private static bool AreaSnapQueue(int page, out bool empty)
    {
        empty = false;
        // no transient test here: a busy readback is the STEP's guard, because a refusal at this point has
        // already consumed the page's turn -- the only refusals left are permanent ones
        if (Gl.FenceSync == null || Gl.MapNamedBufferRange == null || page >= _gl.Layers) return false;
        _areaSnapRects.Clear();
        for (int ri = 0; ri < _rectN; ri++) if (_rects[ri].Page == page && _rects[ri].State == RState.Landed) _areaSnapRects.Add(ri);
        if (!AreaSnapRegion(page, out int x0, out int y0, out int w, out int h) || _areaSnapRects.Count == 0) { empty = true; return false; }
        _uSnap = true;
        QueueReadback(page, x0, y0, w, h, _areaSnapRects);
        if (_gl.Fence != IntPtr.Zero) { _areaSnapped++; return true; }
        _uSnap = false;
        return false;
    }

    /// <summary>The pages are going away (the area edge, or a repack under a drift) with a plan still waiting:
    /// it goes out with the snapshots that did land, and says how many pages it is short. Idempotent.</summary>
    private static void AreaSnapAbandon()
    {
        int lost = _uSnap ? 1 : 0;   // the readback in flight is lost too: its fence is deleted with the pages
        AbortReadback(gl: true);
        _uSnap = false;
        if (_areaPlanOut == null) return;
        for (int p = _areaSnapNext; p < MaxPages; p++) if (_areaSnapWant[p]) lost++;
        AreaPlanOut(lost);
    }

    /// <summary>Hand the pending plan to <c>cx-lmcache</c>, the snapshots folded in.</summary>
    private static void AreaPlanOut(int lost)
    {
        LightmapAreaPlan? plan = _areaPlanOut;
        if (plan == null) return;
        _areaPlanOut = null;
        plan.Unions = MergeUnions(_areaUnions, _areaSnapUnions);
        lost += _areaSnapMissed; _areaSnapMissed = 0;
        if (lost > 0)
        {
            _areaSnapLost += lost;
            _log?.Always($"LightmapBake: the area bake cache for {plan.Key:x16} is written SHORT -- {lost} page(s) went without a readback (the area ended first, "
                       + "or the card refused one), so their rects carry no union and the next visit probes them one by one");
        }
        _cache.SaveArea(plan);
        _log?.Info($"LightmapBake: area bake cache {plan.Key:x16} written -- {plan.Rects.Length} rects on {plan.Layers} pages over {plan.Unions.Length} page blob(s)"
                 + $" ({_areaSnapUnions.Count} read back whole), static set {plan.SetHash:x16}");
        _areaSnapUnions.Clear();
    }

    /// <summary>The area clause of the hold's release line.</summary>
    private static string AreaStats()
    {
        if (_areaKey == 0) return ", area cache: no key for this load";
        int unclaimed = 0, fine = 0;
        for (int ri = 0; ri < _rectN; ri++)
        {
            ref Rect r = ref _rects[ri];
            if (!r.Adopted || r.State == RState.PastCap) continue;
            var o = _owners[ri];
            if (o != null && o.Count > 0) continue;
            if (r.Tier < Baseline) fine++; else unclaimed++;
        }
        return $", area cache {(_areaAdopted ? "hit" : "miss")} {_areaKey:x16}, {_areaAdoptedRects} adopted, {_areaNew} new, {unclaimed} unclaimed"
             + (fine > 0 ? $" (+{fine} fine, claimed as the player nears them)" : "")
             + (_areaProbed > 0 ? $", {_areaProbed} probed per rect" : "")
             + (_areaMisplaced > 0 ? $", {_areaMisplaced} union key(s) misplaced" : "")
             + $", set {(!_areaAdopted ? "first visit" : _areaSetOk ? "confirmed" : _areaDrift > 0 ? "DRIFTED" : "not closed yet")}";
    }

    /// <summary>Write the plan, but only for a visit that bakes the whole area and whose set the walk closed
    /// and confirmed: a skipped, capped or refusing hold would write a partial packing the next visit would
    /// adopt whole. A page holding a rect no union of this visit covers is read back whole first
    /// (<see cref="AreaSnap"/>), so every rect the plan places is inside a union laid out where it places it.</summary>
    private static void AreaWrite(bool complete)
    {
        if (_areaKey == 0 || _areaWritten || !Enabled || SetHash == 0) return;
        string? no = !complete ? "the hold released before the bake was in"
                   : !_closed ? "the static set never closed on a walk"
                   : _areaAdopted && !_areaSetOk ? "the adopted set drifted, so the area is still re-keying"
                   : _rectN == 0 ? "the area placed no rect"
                   : _refused + _pastCap > 0 ? $"{_refused + _pastCap} rect(s) were refused or past the page cap"
                   : _pending > 0 || _toPack.Count > 0 ? "rects are still pending"
                   : _bakeSkips > 0 ? $"{_bakeSkips} bake step(s) were skipped" : null;
        if (no != null) { _log?.Info($"LightmapBake: the area bake cache for {_areaKey:x16} is NOT written -- {no}"); return; }
        var plan = new LightmapAreaPlan
        {
            Key = _areaKey, Settings = AreaSettingsHash(), SetHash = SetHash,
            SetPosR = new Vector4[_setN], SetCol = new Vector3[_setN],
            Layers = PagesUsed, AreaHalve = _areaHalve, PackPage = _packPage, FinePage = _finePage, PlacedArea = _placedArea,
            PageClass = (byte[])_pageClass.Clone(), ShelfX = (int[])_shelfX.Clone(), ShelfY = (int[])_shelfY.Clone(), ShelfH = (int[])_shelfH.Clone(),
            Unions = _areaUnions.ToArray(),
        };
        _areaWritten = true;
        Array.Copy(_setPosR, plan.SetPosR, _setN); Array.Copy(_setCol, plan.SetCol, _setN);
        int n = 0;
        for (int ri = 0; ri < _rectN; ri++) if (_rects[ri].State == RState.Landed && _rects[ri].Page >= 0) n++;
        plan.Rects = new LightmapAreaPlan.Rec[n];
        n = 0;
        for (int ri = 0; ri < _rectN; ri++)
        {
            ref Rect r = ref _rects[ri];
            if (r.State != RState.Landed || r.Page < 0) continue;
            plan.Rects[n++] = new LightmapAreaPlan.Rec
            {
                Key = r.Key, Key0 = r.Key0, Halved = r.Halved, Tier = r.Tier, Page = r.Page,
                X = r.X, Y = r.Y, W = r.W, H = r.H, S = r.S, C = r.C, R = r.R,
            };
        }
        _areaPlanOut = plan; _areaSnapNext = 0; _areaSnapMissed = 0; _areaSnapUnions.Clear(); Array.Clear(_areaSnapWant);
        int want = 0; long snap = 0;
        for (int ri = 0; ri < _rectN; ri++)
        {
            ref Rect r = ref _rects[ri];
            if (r.State != RState.Landed || r.Page < 0 || r.Unioned || _areaSnapWant[r.Page]) continue;
            _areaSnapWant[r.Page] = true; want++;
            if (AreaSnapRegion(r.Page, out _, out _, out int w, out int h)) snap += (long)w * h * TexelBytes;
        }
        long keep = 0;
        for (int i = 0; i < _areaUnions.Count; i++)
            if (!_areaSnapWant[_areaUnions[i].Page]) keep += (long)_areaUnions[i].W * _areaUnions[i].H * TexelBytes;
        if (want == 0) { AreaPlanOut(0); return; }
        if (keep + snap > LightmapAreaPlan.MaxUnionBytes)
        {
            _areaSnapCapped++;
            _log?.Always($"LightmapBake: the area bake cache for {_areaKey:x16} is written WITHOUT its {want} uncovered page(s) -- reading them back would make "
                       + $"{(keep + snap) >> 20} MiB of unions, past the {LightmapAreaPlan.MaxUnionBytes >> 20} MiB the next visit's install may hold; their rects are probed per rect");
            Array.Clear(_areaSnapWant);
            AreaPlanOut(0);
            return;
        }
        _log?.Info($"LightmapBake: area bake cache {_areaKey:x16} holds {n} rects on {PagesUsed} pages; {want} page(s) ({snap >> 20} MiB) are read back whole "
                 + "before it goes out -- their rects landed from another packing's blobs, which cannot be uploaded whole");
    }

    /// <summary>Headless: the plan round-trips byte for byte, and each refusal comes back by its own name. No
    /// GL, no disk -- the install is a GL path and the file layout is what a second session depends on.</summary>
    internal static bool AreaSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "LightmapBake.AreaSelfCheck");
        var plan = new LightmapAreaPlan
        {
            Key = 0xA1B2C3D4E5F60718UL, Settings = 0x1122334455667788UL, SetHash = 0xDEADBEEFUL,
            SetPosR = [new Vector4(1f, 2f, 3f, 12.5f), new Vector4(-4f, 5f, 6f, 30f)],
            SetCol = [new Vector3(1f, 0.5f, 0.25f), Vector3.One],
            Layers = 3, AreaHalve = 1, PackPage = 2, FinePage = 1, PlacedArea = 123456789L,
            PageClass = new byte[MaxPages], ShelfX = new int[MaxPages], ShelfY = new int[MaxPages], ShelfH = new int[MaxPages],
            Unions = [new BakeCache.Union { File = "0123456789abcdef.lmp", Page = 0, X0 = 16, Y0 = 32, W = 256, H = 128 },
                      new BakeCache.Union { File = "fedcba9876543210.lmp", Page = 2, X0 = 0, Y0 = 0, W = 64, H = 64 }],
            Rects = [new LightmapAreaPlan.Rec { Key = 7, Key0 = 7, Halved = 1, Tier = 2, Page = 0, X = 18, Y = 34, W = 32, H = 16, S = 0.5f, C = new Vector3(9f, 8f, 7f), R = 4f },
                     new LightmapAreaPlan.Rec { Key = 9, Key0 = 8, Halved = 2, Tier = 0, Page = 2, X = 2, Y = 2, W = 8, H = 8, S = 0.25f, C = Vector3.Zero, R = 1f }],
        };
        plan.PageClass[0] = ClassBase; plan.PageClass[2] = ClassFine; plan.ShelfX[0] = 512; plan.ShelfY[0] = 64; plan.ShelfH[0] = 18;
        var ms = new MemoryStream();
        plan.SerializeTo(ms);
        byte[] blob = ms.ToArray();
        LightmapAreaPlan? back = LightmapAreaPlan.Deserialize(new MemoryStream(blob), out string? why);
        check.Expect(back != null && why == null, "the plan round-trips");
        check.Expect(back != null && back.Key == plan.Key && back.Settings == plan.Settings && back.SetHash == plan.SetHash, "the key, the settings hash and the set hash round-trip");
        check.Expect(back != null && back.SetPosR.Length == 2 && back.SetPosR[1].W == 30f && back.SetCol[0].Y == 0.5f, "the static set round-trips");
        check.Expect(back != null && back.Layers == 3 && back.AreaHalve == 1 && back.PlacedArea == 123456789L
                     && back.PageClass[2] == ClassFine && back.ShelfX[0] == 512 && back.ShelfH[0] == 18, "the page plan round-trips");
        check.Expect(back != null && back.Unions.Length == 2 && back.Unions[1].File == "fedcba9876543210.lmp" && back.Unions[0].X0 == 16 && back.Unions[0].H == 128,
                     "the unions round-trip with the page origin a whole upload needs");
        check.Expect(back != null && back.Rects.Length == 2 && back.Rects[0].Key == 7 && back.Rects[1].Key0 == 8
                     && back.Rects[1].Tier == 0 && back.Rects[0].S == 0.5f && back.Rects[0].C.X == 9f, "the rect table round-trips");
        blob[0] ^= 1;
        check.Expect(LightmapAreaPlan.Deserialize(new MemoryStream(blob), out why) == null && why == "version", "a file that is not a plan at all is refused as version");
        blob[0] ^= 1;
        blob[4] ^= 1;
        check.Expect(LightmapAreaPlan.Deserialize(new MemoryStream(blob), out why) == null && why == "version", "another version is refused as version");
        blob[4] ^= 1;
        check.Expect(LightmapAreaPlan.Deserialize(new MemoryStream(blob, 0, blob.Length / 2), out why) == null && why == "truncation", "a cut plan is refused as truncation");
        // a rect off its page, and a union naming a page the plan does not have: both are the file lying about
        // where a texel lives, which is the one thing the install cannot check for itself
        plan.Rects[0].X = PageSize; ms = new MemoryStream(); plan.SerializeTo(ms);
        check.Expect(LightmapAreaPlan.Deserialize(new MemoryStream(ms.ToArray()), out why) == null && why == "truncation", "a rect running off its page is refused");
        plan.Rects[0].X = 18; plan.Unions[1].Page = 9; ms = new MemoryStream(); plan.SerializeTo(ms);
        check.Expect(LightmapAreaPlan.Deserialize(new MemoryStream(ms.ToArray()), out why) == null && why == "truncation", "a union on a page the plan never allocated is refused");
        plan.Unions[1].Page = 2;
        // the settings hash is the one term a plan is adopted against at the edge: it must move with each knob
        ulong s0 = AreaSettingsHash();
        check.Expect(s0 == AreaSettingsHash(), "the settings hash is deterministic");
        check.Expect(s0 != ShadowLook(), "the settings hash folds the packing knobs, not only the bake's look");
        // a page read back whole replaces what this visit baked on it; a page nothing was read back for keeps it
        var baked = new List<BakeCache.Union>
        {
            new() { File = "a" + BakeCache.Ext, Page = 0, X0 = 0, Y0 = 0, W = 64, H = 64 },
            new() { File = "b" + BakeCache.Ext, Page = 0, X0 = 0, Y0 = 64, W = 64, H = 64 },
            new() { File = "c" + BakeCache.Ext, Page = 1, X0 = 0, Y0 = 0, W = 32, H = 32 },
        };
        var snapped = new List<BakeCache.Union> { new() { File = "s" + BakeCache.Ext, Page = 0, X0 = 0, Y0 = 0, W = 128, H = 128 } };
        BakeCache.Union[] merged = MergeUnions(baked, snapped);
        check.Expect(merged.Length == 2 && merged[0].File == "c" + BakeCache.Ext && merged[1].File == "s" + BakeCache.Ext,
                     "a page read back whole drops the unions this visit baked on it, and no other page's");
        check.Expect(MergeUnions(baked, new List<BakeCache.Union>()).Length == 3, "a visit with nothing to read back writes the unions it baked");
        check.Expect(MergeUnions(new List<BakeCache.Union>(), snapped).Length == 1, "a visit that baked nothing writes its snapshots");
        // the union upload lands a rect only where the plan placed it: the key alone would hand a rect minted
        // this visit another placement's texels
        var un = new BakeCache.Union { File = "u" + BakeCache.Ext, Page = 2, X0 = 100, Y0 = 200, W = 256, H = 256 };
        var ent = new BakeCache.Entry { Key = 5, X = 10, Y = 20, W = 32, H = 16 };
        check.Expect(UnionPlaces(un, ent, 2, 110, 220, 32, 16), "a rect at the union's own offset is landed by its key");
        check.Expect(!UnionPlaces(un, ent, 3, 110, 220, 32, 16) && !UnionPlaces(un, ent, 2, 111, 220, 32, 16)
                     && !UnionPlaces(un, ent, 2, 110, 220, 32, 17), "a rect on another page, at another place or of another size is refused");
        log.Info(check.Ok ? "LightmapBake.AreaSelfCheck: passed" : "LightmapBake.AreaSelfCheck: FAILED -- see above");
        return check.Ok;
    }
}
