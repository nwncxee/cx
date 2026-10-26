using System;
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

    /// <summary>A content pair's parameter lanes per row, which is <c>cx_conbuf.glsl</c>'s <c>CX_CP_LANES</c>.</summary>
    internal const int ContentLanes = 12;

    /// <summary>Palette map slots a row carries, which is <see cref="AlloyRenderer.PalSlots"/>.</summary>
    internal const int PalSlots = AlloyRenderer.PalSlots;

    /// <summary>One row, as <c>cx_conbuf.glsl</c>'s <c>CxConRow</c> declares it under std430: every member 16-aligned,
    /// so the C# sequential layout is the GPU's byte for byte.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Row
    {
        /// <summary><c>texture7</c>..<c>texture10</c> as bindless handles: <c>pal01</c> then <c>pal23</c>, each
        /// 64-bit handle low word first, which is how a <c>uvec2</c> reads it.</summary>
        public PaletteHandles Pal;
        /// <summary>Which content lanes the material states, bit n = lane n (<c>cpOn.x</c>).</summary>
        public uint CpOn;
        /// <summary><c>cpOn.yzw</c>, unused: the <c>uvec4</c> keeps <c>cp</c> on its 16-byte boundary.</summary>
        public uint CpPadY, CpPadZ, CpPadW;
        /// <summary>The content pair's parameter values, one <c>vec4</c> lane each.</summary>
        public ContentLaneValues Cp;
    }

    /// <summary>The four palette handles of a row.</summary>
    [InlineArray(PalSlots)]
    internal struct PaletteHandles { private ulong _handle; }

    /// <summary>A row's content parameter lanes.</summary>
    [InlineArray(ContentLanes)]
    internal struct ContentLaneValues { private Vector4 _lane; }

    /// <summary>Bytes per row on the GPU.</summary>
    internal static int RowBytes => sizeof(Row);

    /// <summary>Everything mutable, in one owner: context loss replaces the GL half and a fresh session the
    /// rest, so no name from a dead context survives as a field nobody remembered to zero.</summary>
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
        public uint Ssbo;
        public int Used, Sized, DirtyLo = int.MaxValue, DirtyHi = -1;
        public long Uploads, HandleWrites, Spilled, ContentFills;
        /// <summary>Bumped by every row edit, so a copy of the rows knows when it is stale without comparing them.</summary>
        public long Version;
        /// <summary>Uploads whose dirty span reached past the buffer, and so were HELD rather than dropped.</summary>
        public long Short;
        /// <summary>Has a row ever existed? Until one has, the GPU block is ONE zero row rather than the
        /// whole table, and the stage's length test turns every read into a defined false.</summary>
        public bool AnyRow, SaidNoBindless;
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
            s.AnyRow = true;
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
        s.Rows[row] = default;
        Touch(row);
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
        if (!Renderer.OnVulkan && (Gl.CreateBuffers == null || Gl.NamedBufferData == null)) return;
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
                    ContentRows.Fill(ref s.Rows[r], DrawRecipe.Mtr(owner), ContentPrograms.Params(DrawRecipe.Feat(owner)),
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
                if (moved) Touch(r);
            }
        }
        if (Renderer.OnVulkan) { s.DirtyLo = int.MaxValue; s.DirtyHi = -1; return; }   // the Vulkan frame takes the rows through Snapshot
        int want = s.AnyRow ? MaxRows : 1;
        if (s.Ssbo == 0 || want != s.Sized)
        {
            if (s.Ssbo == 0) { uint b; Gl.CreateBuffers(1, &b); if (b == 0) return; s.Ssbo = b; }
            fixed (Row* p = s.Rows) Gl.NamedBufferData(s.Ssbo, (IntPtr)(want * RowBytes), p, Gl.DYNAMIC_DRAW);
            s.Sized = want;
            s.DirtyLo = int.MaxValue; s.DirtyHi = -1;
            s.Uploads++;
        }
        else if (s.DirtyHi >= s.DirtyLo && Gl.NamedBufferSubData != null)
        {
            int lo = s.DirtyLo, n = s.DirtyHi - s.DirtyLo + 1;
            if (lo + n > s.Sized)
            {
                // the dirty span is KEPT: a row dropped here is one the GPU never gets, and the terms behind
                // a record's index would stay whatever the last tenant wrote -- another material's glow
                if (s.Short++ == 0)
                    s.Log?.Always($"ContentTable: rows {s.DirtyLo}..{s.DirtyHi} reach past the {s.Sized}-row "
                                + "buffer -- held dirty for the next upload rather than dropped, which would "
                                + "leave a material wearing another one's parameters");
                Bind();
                return;
            }
            fixed (Row* p = s.Rows.AsSpan(lo))
                Gl.NamedBufferSubData(s.Ssbo, (IntPtr)(lo * RowBytes), (IntPtr)(n * RowBytes), p);
            s.Uploads++;
            s.DirtyLo = int.MaxValue; s.DirtyHi = -1;
        }
        Bind();
    }

    /// <summary>The rows the GPU block holds -- one zero row until any exists, as <see cref="Upload"/> sizes it -- copied
    /// into <paramref name="into"/> (grown to fit); their byte length. Palette handles are GL's.</summary>
    internal static long Version => Volatile.Read(ref _st.Version);

    internal static int Snapshot(ref byte[] into)
    {
        lock (_lock)
        {
            int rows = _st.AnyRow ? MaxRows : 1, n = rows * RowBytes;
            if (into.Length < n) into = new byte[n];
            MemoryMarshal.AsBytes(_st.Rows.AsSpan(0, rows)).CopyTo(into);
            return n;
        }
    }

    /// <summary>Bind the block on <see cref="Binding"/>. Called at every content program bind: the point is a shared one,
    /// and a reader that trusted an earlier bind would read the particle index block.</summary>
    internal static void Bind()
    {
        if (_st.Ssbo != 0 && Gl.BindBufferBase != null)
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, Binding, _st.Ssbo);
    }

    /// <summary>Terminal per session: the buffer belonged to the lost context. The rows themselves are CPU
    /// state, and go up whole on the next <see cref="Upload"/>.</summary>
    internal static void ContextLost()
    {
        _st.Ssbo = 0; _st.Sized = 0;
        _st.DirtyLo = 0; _st.DirtyHi = MaxRows - 1;
    }

    /// <summary>Row <paramref name="row"/> as it stands, for the self-check and the live verifier.</summary>
    internal static Row RowAt(int row) => (uint)row < MaxRows ? _st.Rows[row] : default;

    internal static string Note()
        => _st.Used == 0 && _st.Uploads == 0 ? ""
         : $"contenttable: {_st.Used} rows, {_st.Uploads} uploads, {_st.HandleWrites} handle writes, {_st.ContentFills} content fills"
         + (_st.Spilled == 0 ? "" : $", {_st.Spilled} SPILLED past {MaxRows}")
         + (_st.Short == 0 ? "" : $", {_st.Short} upload(s) HELD past the buffer's end");

    /// <summary>Off-device: the row layout against the GLSL declaration text, and the allocation contract. Oracle:
    /// <c>cx_conbuf.glsl</c>'s own <c>CxConRow</c> declaration, its binding and lane count -- a second artifact rather
    /// than a restatement -- plus hand-written values through a row.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        string src = ShaderSource.Get("cx_conbuf.glsl");
        check.Expect(src.Contains("struct CxConRow { uvec4 pal01; uvec4 pal23; uvec4 cpOn; vec4 cp[CX_CP_LANES]; };"),
          "cx_conbuf.glsl does not declare the row this stages (two palette uvec4s, cpOn, the content lanes)");
        check.Expect(src.Contains($"#define CX_CP_LANES {ContentLanes}"), $"the GLSL's content lane count is not {ContentLanes}");
        check.Expect(src.Contains($"layout(std430, binding = {Binding}) readonly buffer CxContentBuf"),
          $"cx_conbuf.glsl does not declare CxContentBuf at binding {Binding}");
        // std430 over that member list, counted by hand: three 16-byte members, then the lanes
        check.Expect(RowBytes == 3 * 16 + ContentLanes * 16
                     && Marshal.OffsetOf<Row>(nameof(Row.Pal)) == 0 && Marshal.OffsetOf<Row>(nameof(Row.CpOn)) == 32
                     && Marshal.OffsetOf<Row>(nameof(Row.Cp)) == 48,
          $"the row is {RowBytes} bytes, or a member is not where std430 puts it (pal 0, cpOn 32, cp 48)");

        int row = Alloc(recipe: int.MaxValue);
        check.Expect(row >= 0, "the table would not allocate a row");
        if (row < 0) { log.Info("ContentTable.SelfCheck: FAILED"); return false; }
        lock (_lock) SetHandle(row, 2, 0x1122334455667788ul);
        check.Expect(RowAt(row).Pal[2] == 0x1122334455667788ul && BitConverter.IsLittleEndian,
          "a palette handle did not land in its slot, or the host does not store it low word first as a uvec2 reads it");
        int objRow = ObjectRow(int.MaxValue, 7u);
        check.Expect(objRow >= 0 && objRow != row && ObjectRow(int.MaxValue, 7u) == objRow,
          "an object row is not its own, or a second ask makes another");
        Free(row, int.MaxValue);
        check.Expect(RowAt(row).Pal[2] == 0ul && _st.Owner[objRow] == -1 && Alloc(recipe: int.MaxValue) == row,
          "a freed row was not cleared, its object row outlived the recipe, or the row did not come back to the next ask");
        Free(row, int.MaxValue);
        log.Info(check.Ok ? $"ContentTable.SelfCheck: passed ({MaxRows} rows of {RowBytes} bytes at binding {Binding})"
                          : "ContentTable.SelfCheck: FAILED");
        return check.Ok;
    }
}
