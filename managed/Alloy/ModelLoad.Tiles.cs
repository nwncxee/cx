using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The tile models of an area load, read ahead out of the area message itself -- the area's own tiles,
/// not every model its tileset declares. The peek does NOT anchor at <c>CNWTileSetManager::GetTileSet</c>: a
/// VARIABLE grass block sits between that call and the first tile, so the cursor there names no tile record. It
/// anchors on the loop's own first <c>CNWTile::SetID</c>, whose id is the oracle the shape is proved against.</summary>
internal static unsafe partial class ModelLoad
{
    private static ExtenderLog? _tileLog;
    private static NwTileSetIDFn _origTileSetId;
    private static bool _tileArmed;

    /// <summary>The three <c>ReadINT</c>s that open a tile record: id, orientation, height.</summary>
    private const int TileInts = 3;
    /// <summary>Bits of the record's flags word that each add one optional <c>ReadBYTE</c>. Only the low byte
    /// is tested (<c>0x01</c> .. <c>0x80</c>) on both builds.</summary>
    private const int TileFlagMask = 0xff;   // not-an-offset: the flags word's low byte, one optional ReadBYTE per set bit
    /// <summary>Tile records one area may have before the peek refuses it. The biggest stock area grid is
    /// 32x32; this leaves two orders of magnitude of headroom and still bounds a nonsense width*height.</summary>
    private const int MaxTileRecords = 1 << 16;

    /// <summary>The tileset <c>LoadArea</c> is about to index, and the area it is loading: together they say a
    /// tile block is coming and how many records it has. Cleared when the block ends, so the same
    /// <c>SetID</c> from <c>CNWCArea::SetTile</c> @0x708e60 mid-game peeks nothing.</summary>
    private static IntPtr _tileSet, _tileArea;
    /// <summary>The ids read ahead, in loop order. Grown once per area at most; the walk is the only writer.</summary>
    private static int[] _tileIds = new int[1024];
    private static int _tileTotal, _tileSeen;
    private static bool _tilePeeked, _tileBlockOff;
    private static int _tileStood;
    /// <summary>Ids already offered for this block: one area uses a tile many times over, and the repeats
    /// then cost neither a <c>GetTileData</c> nor a resref read.</summary>
    private static readonly HashSet<int> _tileKinds = new();
    /// <summary>What the last block read ahead, for the <c>[load]</c> line.</summary>
    internal static int TileRecords, TileOffers;

    /// <summary>The hook install. Refuses loudly and installs nothing when the symbol or an offset is missing:
    /// the engine then reads every tile model on the main thread, as it does without CX.</summary>
    internal static void InstallTiles(ExtenderLog log, HookService hooks)
    {
        _tileLog = log;
        SelfCheckTiles(log);
        SelfCheckAreaKey(log);
        if (!SymbolMap.Require(log, "ModelLoad.Tiles", SymbolMap.NwTileSetID)
            || !EngineLayout.Require(log, "ModelLoad.Tiles", "CNWMessage", "CNWCArea", "CNWTileSet"))
            return;
        _origTileSetId = hooks.InstallNwTileSetID(&OnTileSetId);
        if (_origTileSetId == null)
        {
            log.Always("ModelLoad.Tiles: REFUSED -- CNWTile::SetID did not hook; the engine reads every tile "
                       + "model on the main thread");
            return;
        }
        _tileArmed = true;
        log.Info("ModelLoad.Tiles: watching LoadArea's tile loop -- the first tile's SetID reads the rest of the "
                 + "grid out of the message and offers their models in the order the loop will use them");
    }

    /// <summary>Called at <c>LoadArea</c> entry, before the engine has read anything of this area. The
    /// tile block state starts clean and the stand-down from a previous area is lifted.</summary>
    private static void ResetTilesForArea(IntPtr area)
    {
        ResetTiles();
        _tileArea = area;
        _tileBlockOff = false;
    }

    /// <summary>The block is over (or was never ours): nothing more is peeked until the next
    /// <c>GetTileSet</c>.</summary>
    private static void ResetTiles()
    {
        _tileSet = IntPtr.Zero;
        _tilePeeked = false;
        _tileSeen = 0;
        _tileTotal = 0;
        _tileKinds.Clear();
    }

    /// <summary>One tile id, mid-<c>LoadArea</c>. Cold -- once per tile, a few hundred times per area -- and
    /// the read-ahead runs on the first of them only.</summary>
    [UnmanagedCallersOnly]
    private static void OnTileSetId(IntPtr tile, int id)
    {
        if (_tileArmed && _armed && _tileSet != IntPtr.Zero && !_tileBlockOff) TrackTile(id);
        _origTileSetId(tile, id);
    }

    /// <summary>Peek the grid on the first tile, then hold the engine to what was peeked.</summary>
    private static void TrackTile(int id)
    {
        IntPtr msg = ClientApi.Message();
        if (msg == IntPtr.Zero) { StandTile("the client's CNWCMessage did not resolve"); return; }
        var m = new CNWMessage(msg);
        byte* buf = (byte*)m.Buffer;
        uint len = m.Length, cursor = m.Cursor;
        if (buf == null || cursor > len) { StandTile("the message buffer is not readable"); return; }
        if (!_tilePeeked) { PeekTiles(buf, len, cursor, id); return; }

        _tileSeen++;
        if (_tileSeen >= _tileTotal || _tileIds[_tileSeen] != id)
            StandTile($"tile {_tileSeen} of {_tileTotal} arrived as id {id}, not the one read ahead");
        else if (_tileSeen == _tileTotal - 1) ResetTiles();
    }

    /// <summary>Read the grid out of the message and offer its models in the loop's own order, so the worker
    /// stays one record ahead of the engine rather than racing it (the job queue is FIFO).</summary>
    private static void PeekTiles(byte* buf, uint len, uint cursor, int id)
    {
        long t0 = Stopwatch.GetTimestamp();
        if (_tileArea == IntPtr.Zero) { StandTile("LoadArea's area never reached the read-ahead"); return; }
        var area = new CNWCArea(_tileArea);
        long grid = (long)area.TileWidth * area.TileHeight;
        if (grid <= 0 || grid > MaxTileRecords)
        {
            StandTile($"the area reports a {area.TileWidth}x{area.TileHeight} tile grid");
            return;
        }
        int n = (int)grid, tiles = new CNWTileSet(_tileSet).TileCount;
        if (tiles <= 0) { StandTile("the tileset reports no tiles"); return; }
        if (!WalkTiles(buf, len, cursor, id, false, n, tiles)
            && !WalkTiles(buf, len, cursor, id, true, n, tiles))
        {
            StandTile($"the bytes before cursor {cursor} do not read back as {n} tile record(s) of tileset {tiles}");
            return;
        }
        _tilePeeked = true;
        AreaKeyTiles(_tileFold, n);
        _tileSeen = 0;
        _tileTotal = n;
        _tilesPeeked += n;
        TileRecords = n;
        TileOffers = 0;
        byte* resref = stackalloc byte[ResRefChars + 2];   // GetModelResRef writes all 17 bytes of the CResRef
        for (int k = 0; k < n; k++) OfferTileModel(_tileIds[k], resref);
        OfferTicks += Stopwatch.GetTimestamp() - t0;
        _tileLog?.Info($"ModelLoad.Tiles: {n} tile(s) ahead, {_tileKinds.Count} distinct, {TileOffers} model(s) offered");
        if (n == 1) ResetTiles();
    }

    /// <summary>Accept a record shape only when the id the engine was handed reads back where that shape says it
    /// is, every record of the grid fits the buffer and names a tile the tileset has. A record is a whole number
    /// of bytes -- <c>3*int + word + popcount(flags &amp; 0xff) * byte</c> -- where each width carries
    /// <c>ExpectType</c>'s leading type byte or does not, one global for the whole stream.</summary>
    private static bool WalkTiles(byte* buf, uint len, uint cursor, int id, bool typed, int n, int tiles)
    {
        uint type = typed ? 1u : 0u;                     // ExpectType's leading byte, one per typed read
        uint step = 4 + type, ints = (uint)TileInts * step, word = 2 + type, one = 1 + type;
        if (cursor < ints) return false;
        uint p = cursor - ints;
        var b = new ReadOnlySpan<byte>(buf, (int)len);
        if (I32(b, p + type) != id) return false;
        if (_tileIds.Length < n) _tileIds = new int[n];
        ulong fold = FoldSeed;
        for (int k = 0; k < n; k++)
        {
            if ((ulong)p + ints + word > len) return false;
            int t = I32(b, p + type);
            if ((uint)t >= (uint)tiles) return false;
            _tileIds[k] = t;
            // the id alone would key two areas laid out of the same tiles alike, so the record's orientation
            // and height fold in beside it
            fold = Fold(Fold(Fold(fold, t), I32(b, p + step + type)), I32(b, p + 2 * step + type));
            p += ints;
            uint flags = U16(b, p + type) & (uint)TileFlagMask;
            fold = Fold(fold, (long)flags);
            p += word + (uint)BitOperations.PopCount(flags) * one;
            if (p > len) return false;
        }
        _tileFold = fold;
        return true;
    }

    /// <summary>One tile id -&gt; its <c>CNWTileData</c> -&gt; its model resref -&gt; an offer. Repeats cost
    /// nothing.</summary>
    private static void OfferTileModel(int id, byte* resref)
    {
        if (!_tileKinds.Add(id)) return;
        IntPtr td = new CNWTileSet(_tileSet).TileSetGetTileData(id);
        if (td == IntPtr.Zero) return;
        new CNWTileData(td).TileDataGetModelResRef(resref);
        resref[ResRefChars] = 0;   // a CResRef's 17th byte is its buffered LENGTH, not a terminator
        OfferOne(resref);
        TileOffers++;
    }

    /// <summary>The read-ahead disagreed with the engine, or a source it needs went missing: stop offering for
    /// this area and say so. The engine's own loads are untouched either way.</summary>
    private static void StandTile(string why)
    {
        ResetTiles();
        _tileBlockOff = true;
        if (_tileStood++ < StandDownLogCap)
            _tileLog?.Always($"ModelLoad.Tiles: read-ahead STOOD DOWN -- {why}. The engine reads this area's "
                             + "tile models on the main thread, as it does without CX");
    }

    /// <summary>Off-device, no engine: a message buffer is built in each of the two shapes the stream can have,
    /// with records carrying different flag words so a walk that got the variable stride wrong cannot reach the
    /// last one, and <see cref="WalkTiles"/> must recognise it from the cursor and the first tile's id. Then the
    /// refusals: a wrong id, a grid running past the buffer, and an id the tileset does not have.</summary>
    internal static bool SelfCheckTiles(ExtenderLog log)
    {
        var check = new Checker(log, "ModelLoad.Tiles.SelfCheck");
        const int Records = 4, Pad = 8, Tiles = 500;
        int[] ids = { 17, 0, 499, 42 };
        ushort[] flags = { 0x0000, 0x00ff, 0x0081, 0x1234 };   // not-an-offset: fixture flag words, low byte = byte count

        foreach (bool typed in new[] { false, true })
        {
            uint type = typed ? 1u : 0u;
            uint ints = (uint)TileInts * (4 + type), word = 2 + type, one = 1 + type;
            int len = Pad;
            for (int k = 0; k < Records; k++)
                len += (int)(ints + word + (uint)BitOperations.PopCount(flags[k] & (uint)TileFlagMask) * one);
            byte* buf = stackalloc byte[len];
            for (int i = 0; i < len; i++) buf[i] = 0;
            var bs = new Span<byte>(buf, len);
            uint p = Pad;
            for (int k = 0; k < Records; k++)
            {
                Put(bs, p + type, ids[k]);
                p += ints;
                Put(bs, p + type, flags[k]);
                p += word + (uint)BitOperations.PopCount(flags[k] & (uint)TileFlagMask) * one;
            }

            uint cursor = Pad + ints;
            check.Expect(WalkTiles(buf, (uint)len, cursor, ids[0], typed, Records, Tiles),
                         $"typed={typed}: the first tile's shape is recognised");
            check.Expect(_tileIds[0] == ids[0] && _tileIds[Records - 1] == ids[Records - 1],
                         $"typed={typed}: every record reads back at the flags-dependent stride");
            check.Expect(!WalkTiles(buf, (uint)len, cursor, ids[0] + 1, typed, Records, Tiles),
                         $"typed={typed}: a different tile id is not this shape");
            check.Expect(!WalkTiles(buf, (uint)len, cursor, ids[0], typed, Records + 1, Tiles),
                         $"typed={typed}: a grid running past the buffer is refused");
            check.Expect(!WalkTiles(buf, (uint)len, cursor, ids[0], typed, Records, 499),
                         $"typed={typed}: an id the tileset does not have is refused");
            check.Expect(!WalkTiles(buf, (uint)len, (uint)(ints - 1), ids[0], typed, Records, Tiles),
                         $"typed={typed}: a cursor with no room for a record ahead of it is refused");
        }
        ResetTiles();
        log.Info(check.Ok ? "ModelLoad.Tiles.SelfCheck: passed" : "ModelLoad.Tiles.SelfCheck: FAILED");
        return check.Ok;
    }
}

/// <summary>The tile read-ahead's one install, in its own service: <c>ModelLoad</c>'s own install site belongs
/// to the model read.</summary>
internal sealed class ModelTileLoadService : IExtenderService
{
    public void Initialize(ExtenderLog log, HookService hooks) => ModelLoad.InstallTiles(log, hooks);
}
