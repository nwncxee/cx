using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy.Db;

/// <summary>The CONTENT TABLE (<c>cx_conbuf.glsl</c>): what a content pair's stages need of their material and nothing
/// else in the world reads -- its parameters (<see cref="ContentRows"/>) and its four palette maps as bindless handles. A
/// row is per MATERIAL, or per OBJECT for a row carrying that object's own uniforms, so a record carries only its index,
/// in <c>feat.z</c>.</summary>
internal static unsafe class ContentTable
{
    /// <summary>SSBO binding of the table. A SHARED point (<see cref="SsboSlots.Shared"/> and up), rebound at
    /// every lit program bind: nothing else in the lit bracket claims it, and its other tenants rebind it.</summary>
    internal const uint Binding = SsboSlots.Content;

    /// <summary>Rows held at once: content MATERIALS, and the objects whose own uniforms a content pair reads. A retired
    /// recipe or a released object hands its rows back. Spill is counted and named, never silent.</summary>
    internal const int MaxRows = 1024;

    /// <summary>Parameter lanes one content pair may take: GL's guaranteed 1024 uniform components per stage, the most
    /// a stock <c>.mtr</c>'s unbounded <c>parameter</c> list can ever reach a shader with.</summary>
    internal const int ContentLanes = 1024;

    /// <summary>Lanes the pool holds across every row, in <c>uvec4</c>s (16 MiB). Spill is counted and named.</summary>
    internal const int MaxPool = 1 << 20;

    /// <summary>Lanes one <c>uvec4</c> of a row's on-mask covers.</summary>
    private const int LanesPerMask = 128;

    /// <summary>Palette map slots a row carries, which is <see cref="AlloyRenderer.PalSlots"/>.</summary>
    internal const int PalSlots = AlloyRenderer.PalSlots;

    /// <summary>One row's header, as <c>cx_conbuf.glsl</c>'s <c>CxConRow</c> declares it under std430: every member
    /// 16-aligned, so the C# sequential layout is the GPU's byte for byte. Its lanes live in the pool after the rows.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Row
    {
        /// <summary><c>texture7</c>..<c>texture10</c> as bindless handles: <c>pal01</c> then <c>pal23</c>, each
        /// 64-bit handle low word first, which is how a <c>uvec2</c> reads it.</summary>
        public PaletteHandles Pal;
        /// <summary>The row's first pool entry (<c>cp.x</c>): its on-mask, then one lane per parameter.</summary>
        public uint CpAt;
        /// <summary>The row's lane count (<c>cp.y</c>); 0 = every parameter reads its GLSL default.</summary>
        public uint CpN;
        /// <summary><c>cp.zw</c>, unused.</summary>
        public uint CpPadZ, CpPadW;
    }

    /// <summary>The four palette handles of a row.</summary>
    [InlineArray(PalSlots)]
    internal struct PaletteHandles { private ulong _handle; }

    /// <summary>Bytes per row header on the GPU.</summary>
    internal static int RowBytes => sizeof(Row);

    /// <summary>Bytes of the header array, which the GPU declares at its full <see cref="MaxRows"/>.</summary>
    internal static int HeaderBytes => MaxRows * RowBytes;

    /// <summary>Pool entries a row of <paramref name="lanes"/> lanes takes: its on-mask, then its lanes.</summary>
    internal static int SpanOf(int lanes) => lanes <= 0 ? 0 : (lanes + LanesPerMask - 1) / LanesPerMask + lanes;

    /// <summary>Everything mutable, in one owner: context loss replaces the device half and a fresh session
    /// the rest, so no name from a dead device survives as a field nobody remembered to zero.</summary>
    private sealed class TableState
    {
        public readonly Row[] Rows = new Row[MaxRows];
        /// <summary>Which recipe owns each row, or -1. A row's palette handles are re-read off it at every
        /// <see cref="Upload"/>: a palette map lands asynchronously, and the registry re-makes a texture under us.</summary>
        public readonly int[] Owner = new int[MaxRows];
        /// <summary>A content row's object (<see cref="DbId.Value"/>), whose own uniforms it carries; 0 for the
        /// material's own row.</summary>
        public readonly uint[] Obj = new uint[MaxRows];
        /// <summary>The content layout generation a row's lanes were filled against, -1 = fill it.</summary>
        public readonly int[] Filled = new int[MaxRows];
        /// <summary>The pool span each row holds, in entries; 0 = none.</summary>
        public readonly int[] SpanLen = new int[MaxRows];
        /// <summary>The lane pool, four <c>uint</c>s an entry: masks as bits, lanes as float bits.</summary>
        public uint[] Pool = new uint[4 * 256];
        /// <summary>Entries in use from the pool's start, and the spans freed below that.</summary>
        public int PoolTop;
        public readonly List<(int At, int Len)> FreeSpans = new();
        public long PoolSpilled;
        /// <summary>The materials whose first filled object row has been logged, lane by lane.</summary>
        public readonly HashSet<string> Said = new(StringComparer.Ordinal);
        public int Used, Sized, DirtyLo = int.MaxValue, DirtyHi = -1;
        public long Uploads, HandleWrites, Spilled, ContentFills;
        /// <summary>Bumped by every row edit, so a copy of the rows knows when it is stale without comparing them.</summary>
        public long Version;
        /// <summary>Uploads whose dirty span reached past the buffer, and so were HELD rather than dropped.</summary>
        public long Short;
        public bool SaidNoBindless;
        public ExtenderLog? Log;

        public TableState() { Array.Fill(Owner, -1); }
    }

    private static TableState _st = new();
    /// <summary>One lock over the rows. A row is written where its RECIPE is interned or its record staged (the
    /// scene feed) and read where the block is uploaded (the Alloy bracket), which are not the same thread; the
    /// writes are per material and the read walks at most <see cref="MaxRows"/> rows a frame, so neither is hot.</summary>
    private static readonly object _lock = new();

    internal static void Attach(ExtenderLog log) => _st.Log = log;

    /// <summary>A content pair's row for recipe <paramref name="recipe"/>'s material, or -1 when the table is full (the
    /// pair then reads every uniform's own default). Its lanes are filled at the next <see cref="Upload"/>.</summary>
    internal static int Alloc(int recipe)
    {
        lock (_lock) return AllocLocked(recipe, 0);
    }

    /// <summary>The row carrying object <paramref name="obj"/>'s own uniforms over recipe <paramref name="recipe"/>'s
    /// material: found, or made. -1 when the table is full -- the record then keeps the material's row.
    /// ponytail: a scan of <see cref="MaxRows"/>, only for an object that sets its own uniforms and only when its
    /// record restages; a (recipe, object) index if such objects ever number in the hundreds.</summary>
    internal static int ObjectRow(int recipe, uint obj)
    {
        lock (_lock)
        {
            TableState s = _st;
            for (int r = 0; r < MaxRows; r++)
                if (s.Owner[r] == recipe && s.Obj[r] == obj) return r;
            return AllocLocked(recipe, obj);
        }
    }

    private static int AllocLocked(int recipe, uint obj)
    {
        TableState s = _st;
        for (int r = 0; r < MaxRows; r++)
        {
            if (s.Owner[r] >= 0) continue;
            s.Owner[r] = recipe;
            s.Obj[r] = obj;
            s.Filled[r] = -1;
            s.Used++;
            s.Rows[r] = default;
            Touch(r);
            return r;
        }
        if (s.Spilled++ == 0)
            s.Log?.Always($"ContentTable: {MaxRows} rows is the table's whole capacity -- a further content material reads "
                        + "its uniforms' defaults and no palette maps, and a further object keeps its material's values");
        return -1;
    }

    /// <summary>Recipe <paramref name="recipe"/> is going: its row goes back, and so does every object row over it.
    /// A row another recipe already holds is left alone, because the retire paths may ask twice.
    /// ponytail: a scan of <see cref="MaxRows"/> per content recipe retired; a per-recipe row list if a module edge shows it.</summary>
    internal static void Free(int row, int recipe)
    {
        lock (_lock)
        {
            TableState s = _st;
            if ((uint)row < MaxRows && s.Owner[row] == recipe) FreeLocked(row);
            for (int r = 0; r < MaxRows; r++)
                if (s.Owner[r] == recipe && s.Obj[r] != 0) FreeLocked(r);
        }
    }

    /// <summary>Object <paramref name="obj"/> has no uniforms of its own any more, or is gone: its rows go back.</summary>
    internal static void FreeObject(uint obj)
    {
        if (obj == 0) return;
        lock (_lock)
            for (int r = 0; r < MaxRows; r++)
                if (_st.Owner[r] >= 0 && _st.Obj[r] == obj) FreeLocked(r);
    }

    /// <summary>Object <paramref name="obj"/>'s uniforms changed: its rows fill again at the next upload.</summary>
    internal static void TouchObject(uint obj)
    {
        if (obj == 0) return;
        lock (_lock)
            for (int r = 0; r < MaxRows; r++)
                if (_st.Owner[r] >= 0 && _st.Obj[r] == obj) _st.Filled[r] = -1;
    }

    private static void FreeLocked(int row)
    {
        TableState s = _st;
        s.Owner[row] = -1;
        s.Obj[row] = 0;
        s.Used--;
        ReleaseSpan(row);
        s.Rows[row] = default;
        Touch(row);
    }

    /// <summary>Row <paramref name="row"/>'s pool span goes back, for a later row of that size or less.</summary>
    private static void ReleaseSpan(int row)
    {
        TableState s = _st;
        if (s.SpanLen[row] > 0) s.FreeSpans.Add(((int)s.Rows[row].CpAt, s.SpanLen[row]));
        s.SpanLen[row] = 0;
        s.Rows[row].CpAt = 0;
        s.Rows[row].CpN = 0;
    }

    /// <summary>A pool span of <paramref name="len"/> entries for row <paramref name="row"/>: a freed one that fits, else
    /// the top, grown. False (counted, said once) past <see cref="MaxPool"/>; the row then states no lanes.</summary>
    private static bool TakeSpan(int row, int len)
    {
        TableState s = _st;
        if (s.SpanLen[row] >= len) return true;
        ReleaseSpan(row);
        if (len == 0) return true;
        int at = -1;
        for (int i = 0; i < s.FreeSpans.Count; i++)
            if (s.FreeSpans[i].Len >= len)
            {
                (int fa, int fl) = s.FreeSpans[i];
                at = fa;
                if (fl == len) s.FreeSpans.RemoveAt(i);
                else s.FreeSpans[i] = (fa + len, fl - len);
                break;
            }
        if (at < 0)
        {
            if (s.PoolTop + len > MaxPool)
            {
                if (s.PoolSpilled++ == 0)
                    s.Log?.Always($"ContentTable: the lane pool's {MaxPool} entries are spent -- a further content row reads "
                                + "its uniforms' defaults");
                return false;
            }
            at = s.PoolTop;
            s.PoolTop += len;
            if (s.Pool.Length < 4 * s.PoolTop) Array.Resize(ref s.Pool, Math.Max(4 * s.PoolTop, 2 * s.Pool.Length));
        }
        s.SpanLen[row] = len;
        s.Rows[row].CpAt = (uint)at;
        return true;
    }

    /// <summary>Fill row <paramref name="row"/>'s lanes for a stage laid out as <paramref name="ps"/>.</summary>
    private static void FillLanes(int row, MtrDef? mtr, ReadOnlySpan<ContentStage.Param> ps,
                                  IReadOnlyList<Seam.MaterialParams.ContentOverride>? overs)
    {
        TableState s = _st;
        int n = 0;
        foreach (ContentStage.Param p in ps) n = Math.Max(n, p.Lane + 1);
        n = Math.Min(n, ContentLanes);
        if (!TakeSpan(row, SpanOf(n))) { s.Rows[row].CpN = 0; return; }
        s.Rows[row].CpN = (uint)n;
        if (n == 0) return;
        int masks = SpanOf(n) - n;
        Span<uint> span = s.Pool.AsSpan(4 * (int)s.Rows[row].CpAt, 4 * SpanOf(n));
        ContentRows.Fill(span[..(4 * masks)], MemoryMarshal.Cast<uint, Vector4>(span[(4 * masks)..]), mtr, ps, overs);
        if (s.Log != null && mtr != null && s.Obj[row] != 0 && s.Said.Add(mtr.Name)) SayRow(row, mtr.Name, ps, overs?.Count ?? 0);
    }

    /// <summary>One line per material, at its first object row: every lane as name=value, a lane its material does
    /// not state as name=off, so a content pair that draws wrong shows what it was handed.</summary>
    private static void SayRow(int row, string mtr, ReadOnlySpan<ContentStage.Param> ps, int overs)
    {
        var b = new System.Text.StringBuilder();
        b.Append($"ContentTable: row {row} for {mtr}, object {_st.Obj[row]}, {overs} override(s), {_st.Rows[row].CpN} lanes at {_st.Rows[row].CpAt}:");
        foreach (ContentStage.Param p in ps)
        {
            (bool on, Vector4 v) = LaneAt(row, p.Lane);
            b.Append(on ? $" {p.Name}={v.X:G9}" : $" {p.Name}=off");
        }
        _st.Log!.Always(b.ToString());
    }

    /// <summary>One palette map's handle into row <paramref name="row"/>, slot <paramref name="k"/> of
    /// <see cref="PalSlots"/> (<c>texture7</c> is 0).</summary>
    private static bool SetHandle(int row, int k, ulong handle)
    {
        ref ulong h = ref _st.Rows[row].Pal[k];
        if (h == handle) return false;
        h = handle;
        _st.HandleWrites++;
        return true;
    }

    private static void Touch(int row)
    {
        _st.Version++;
        if (row < _st.DirtyLo) _st.DirtyLo = row;
        if (row > _st.DirtyHi) _st.DirtyHi = row;
    }

    /// <summary>Re-read every row's palette handles off its recipe, fill every row whose layout moved, push what changed
    /// and bind the block, in Alloy's bracket (on Vulkan the walk only). A map lands asynchronously and a pair's lanes
    /// are known only once its program built, so this is a per-frame walk of at most <see cref="MaxRows"/> rows. Bound
    /// on EVERY frame -- an unbound binding is an undefined read.</summary>
    internal static void Upload()
    {
        lock (_lock) UploadLocked();
    }

    private static void UploadLocked()
    {
        TableState s = _st;
        if (s.Used > 0)
        {
            bool handles = AlloyRenderer.Bindless;
            if (!handles && !s.SaidNoBindless)
            {
                s.SaidNoBindless = true;
                s.Log?.Always("ContentTable: a content pair is in sight and the bindless handle table is OFF -- the lit stage "
                            + "spends texture units 0..30, so texture7..texture10 have nowhere to sit and a stage that "
                            + "samples them reads its diffuse; `alloy.bindless 1` plus a restart is what turns them on");
            }
            // a null handle is a GPU fault on AMD's driver, so every empty slot takes a stand-in's instead
            ulong nullH = handles ? AlloyRenderer.HandleFor(OwnedTextures.NullTid()) : 0ul;
            for (int r = 0; r < MaxRows; r++)
            {
                int owner = s.Owner[r];
                if (owner < 0) continue;
                int epoch = ContentPrograms.Epoch(DrawRecipe.Feat(owner));
                if (s.Filled[r] != epoch)
                {
                    FillLanes(r, DrawRecipe.Mtr(owner), ContentPrograms.Params(DrawRecipe.Feat(owner)),
                              Seam.MaterialParams.ContentOverrides(s.Obj[r]));
                    s.Filled[r] = epoch;
                    s.ContentFills++;
                    Touch(r);
                }
                bool moved = false;
                for (int k = 0; k < PalSlots; k++)
                {
                    ulong h = handles ? DrawRecipe.PalHandle(owner, k) : 0ul;
                    moved |= SetHandle(r, k, h != 0 ? h : nullH);
                }
                if (moved)
                {
                    Touch(r);
                    if (s.Obj[r] != 0 && DrawRecipe.Mtr(owner) is { } m && s.Said.Add("pal " + m.Name))
                        s.Log?.Always($"ContentTable: row {r} for {m.Name} palette handles texture7..10 = "
                                    + $"{s.Rows[r].Pal[0]:x} {s.Rows[r].Pal[1]:x} {s.Rows[r].Pal[2]:x} {s.Rows[r].Pal[3]:x} (null stand-in {nullH:x})");
                }
            }
        }
        s.DirtyLo = int.MaxValue; s.DirtyHi = -1;   // the Vulkan frame takes the rows through Snapshot
    }

    /// <summary>The GPU block -- every row header, then the lane pool (one zero entry when empty, so the runtime array
    /// is never zero-length) -- copied into <paramref name="into"/> (grown to fit); its byte length. Handles are GL's.</summary>
    internal static long Version => Volatile.Read(ref _st.Version);

    internal static int Snapshot(ref byte[] into)
    {
        lock (_lock)
        {
            int pool = Math.Max(_st.PoolTop, 1), n = HeaderBytes + pool * 16;
            if (into.Length < n) into = new byte[n];
            MemoryMarshal.AsBytes(_st.Rows.AsSpan()).CopyTo(into);
            Span<byte> lanes = into.AsSpan(HeaderBytes, pool * 16);
            lanes.Clear();
            MemoryMarshal.AsBytes(_st.Pool.AsSpan(0, 4 * _st.PoolTop)).CopyTo(lanes);
            return n;
        }
    }

    /// <summary>Terminal per session: the buffer belonged to the lost device. The rows themselves are CPU
    /// state, and go up whole on the next <see cref="Upload"/>.</summary>
    internal static void ContextLost()
    {
        _st.Sized = 0;
        _st.DirtyLo = 0; _st.DirtyHi = MaxRows - 1;
    }

    /// <summary>Row <paramref name="row"/> as it stands, for the self-check and the live verifier.</summary>
    internal static Row RowAt(int row) => (uint)row < MaxRows ? _st.Rows[row] : default;

    /// <summary>Row <paramref name="row"/>'s lane <paramref name="lane"/> and whether its material states it.</summary>
    internal static (bool On, Vector4 Value) LaneAt(int row, int lane)
    {
        if ((uint)row >= MaxRows || (uint)lane >= _st.Rows[row].CpN) return (false, default);
        int at = (int)_st.Rows[row].CpAt, masks = SpanOf((int)_st.Rows[row].CpN) - (int)_st.Rows[row].CpN;
        ReadOnlySpan<uint> mask = _st.Pool.AsSpan(4 * (at + lane / LanesPerMask), 4);
        bool on = (mask[(lane / 32) & 3] & (1u << (lane & 31))) != 0;
        return (on, MemoryMarshal.Cast<uint, Vector4>(_st.Pool.AsSpan())[at + masks + lane]);
    }

    internal static string Note()
        => _st.Used == 0 && _st.Uploads == 0 ? ""
         : $"contenttable: {_st.Used} rows, {_st.Uploads} uploads, {_st.HandleWrites} handle writes, {_st.ContentFills} content fills"
         + $", {_st.PoolTop} pool lanes"
         + (_st.Spilled == 0 ? "" : $", {_st.Spilled} SPILLED past {MaxRows}")
         + (_st.PoolSpilled == 0 ? "" : $", {_st.PoolSpilled} SPILLED past the {MaxPool}-lane pool")
         + (_st.Short == 0 ? "" : $", {_st.Short} upload(s) HELD past the buffer's end");

    /// <summary>Off-device: the row layout against the GLSL declaration text, and the allocation contract. Oracle:
    /// <c>cx_conbuf.glsl</c>'s own <c>CxConRow</c> declaration, its binding and lane count -- a second artifact rather
    /// than a restatement -- plus hand-written values through a row.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        string src = ShaderSource.Get("cx_conbuf.glsl");
        check.Expect(src.Contains("struct CxConRow { uvec4 pal01; uvec4 pal23; uvec4 cp; };"),
          "cx_conbuf.glsl does not declare the row header this stages (two palette uvec4s, then cp)");
        check.Expect(src.Contains($"#define CX_CON_ROWS {MaxRows}") && src.Contains("CxConRow uConTab[CX_CON_ROWS]; uvec4 uConLanes[];"),
          $"the GLSL's header array is not {MaxRows} rows followed by the lane pool");
        check.Expect(src.Contains($"#define CX_CP_MASK {LanesPerMask}"), $"the GLSL's on-mask does not cover {LanesPerMask} lanes an entry");
        check.Expect(src.Contains($"layout(std430, binding = {Binding}) readonly buffer CxContentBuf"),
          $"cx_conbuf.glsl does not declare CxContentBuf at binding {Binding}");
        // std430 over that member list, counted by hand: three 16-byte members
        check.Expect(RowBytes == 3 * 16
                     && Marshal.OffsetOf<Row>(nameof(Row.Pal)) == 0 && Marshal.OffsetOf<Row>(nameof(Row.CpAt)) == 32
                     && Marshal.OffsetOf<Row>(nameof(Row.CpN)) == 36,
          $"the row is {RowBytes} bytes, or a member is not where std430 puts it (pal 0, cp.x 32, cp.y 36)");
        check.Expect(SpanOf(0) == 0 && SpanOf(1) == 2 && SpanOf(128) == 129 && SpanOf(129) == 131 && SpanOf(1024) == 1032,
          "a row's span is not ceil(lanes / 128) mask entries plus one entry a lane");

        int row = Alloc(recipe: int.MaxValue);
        check.Expect(row >= 0, "the table would not allocate a row");
        if (row < 0) { log.Info("ContentTable.SelfCheck: FAILED"); return false; }
        lock (_lock) SetHandle(row, 2, 0x1122334455667788ul);
        check.Expect(RowAt(row).Pal[2] == 0x1122334455667788ul && BitConverter.IsLittleEndian,
          "a palette handle did not land in its slot, or the host does not store it low word first as a uvec2 reads it");
        int objRow = ObjectRow(int.MaxValue, 7u);
        check.Expect(objRow >= 0 && objRow != row && ObjectRow(int.MaxValue, 7u) == objRow,
          "an object row is not its own, or a second ask makes another");
        MtrDef wide = MtrDef.Parse(System.Text.Encoding.ASCII.GetBytes(
            "customshaderfs wide\nparameter float p0 -1\nparameter float p79 16711680\nparameter float p130 0.5\n"), "wide_mat");
        var wps = new ContentStage.Param[131];
        for (int i = 0; i < wps.Length; i++) wps[i] = new($"p{i}", ContentStage.ParamKind.Float, i);
        lock (_lock) FillLanes(row, wide, wps, [new("", "p79", "255.000000 0.000000 0.000000 0.000000")]);
        check.Expect(RowAt(row).CpN == 131 && LaneAt(row, 0) == (true, new Vector4(-1f, 0, 0, 0))
                     && LaneAt(row, 79) == (true, new Vector4(255f, 0, 0, 0)) && LaneAt(row, 130) == (true, new Vector4(0.5f, 0, 0, 0))
                     && !LaneAt(row, 1).On && !LaneAt(row, 129).On,
          "a 131-parameter pair did not get a lane for each, an object override past lane 12 did not land, or an unstated lane is on");
        Free(row, int.MaxValue);
        check.Expect(RowAt(row).Pal[2] == 0ul && _st.Owner[objRow] == -1 && Alloc(recipe: int.MaxValue) == row,
          "a freed row was not cleared, its object row outlived the recipe, or the row did not come back to the next ask");
        Free(row, int.MaxValue);
        log.Info(check.Ok ? $"ContentTable.SelfCheck: passed ({MaxRows} rows of {RowBytes} bytes at binding {Binding})"
                          : "ContentTable.SelfCheck: FAILED");
        return check.Ok;
    }
}
