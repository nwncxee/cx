using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The non-tile models of an area load. At <c>LoadArea</c> entry the area's own id lists are still empty
/// -- <c>LoadArea</c> IS the area message's parser -- so nothing can be offered there. The offers are made from
/// the FIRST static-placeable record's call instead, where the rest of the fixed-shape block is still unread
/// message bytes. The shape is proved against the engine as it goes, and a mismatch stands the block down.</summary>
internal static unsafe partial class ModelLoad
{
    private static ExtenderLog? _objLog;
    private static AreaAddPlaceableStaticFn _origAddStatic;
    private static C2DAGetCExoStringEntryFn _cell;
    private static ExoStringDtorFn _exoDtor;
    private static readonly IntPtr _rulesSym = Native.Sym(SymbolMap.Rules);
    /// <summary>The <c>ModelName</c> column, built once as an engine CExoString: what
    /// <c>GetCExoStringEntry</c> compares against, and the engine builds the same string per call.</summary>
    private static IntPtr _column;
    private static bool _objArmed;

    /// <summary>A CExoString: <c>{char* buf@0; uint len@8; uint cap@0xc}</c>. not-an-offset: the size of the
    /// caller-owned value the engine writes its cell into.</summary>
    private const int ExoSize = 16;
    /// <summary>The static-placeable record, exactly as <c>LoadArea</c> reads it: the id (never type-tagged), the
    /// appearance (<c>ReadWORD</c>) and six <c>ReadFLOAT</c>s -- position then orientation. 30 bytes, or 37 when
    /// the connection type-tags every typed read (<c>CNWMessage::ExpectType</c> prefixes one byte per read),
    /// which also moves the appearance's own bytes one along.</summary>
    private const int PlainStride = 4 + 2 + 6 * 4, PlainAppearanceAt = 4;
    /// <inheritdoc cref="PlainStride"/>
    private const int TypedStride = 4 + (1 + 2) + 6 * (1 + 4), TypedAppearanceAt = 5;
    /// <summary>The block's count is a <c>ReadWORD</c>, so its two bytes sit immediately before record 0
    /// whichever shape the stream has -- a type byte, if any, precedes them.</summary>
    private const int CountBytes = 2;
    /// <summary>A block longer than this is not a static-placeable block; the shape is refused instead of
    /// walked. ponytail: the biggest persistent-world areas run to a few thousand static placeables.</summary>
    private const int MaxRecords = 20000;
    /// <summary>How many times a refusal is written before it goes quiet.</summary>
    private const int StandDownLogCap = 4;

    private static uint _rec0, _count;
    private static int _stride, _appearanceAt, _seen;
    private static bool _peeked;
    /// <summary>The read-ahead stood itself down; nothing more is offered until the next area load.</summary>
    private static bool _blockOff;
    private static int _stood;
    /// <summary>Appearances already offered for this block: the 2DA read and the offer are skipped for the
    /// repeats, which on a heavy area are most of the records.</summary>
    private static readonly System.Collections.Generic.HashSet<ushort> _kinds = new();
    /// <summary>What the last block offered, for step 1's <c>[load]</c> line.</summary>
    internal static int ObjectOffers, ObjectRecords;

    /// <summary>The hook install. Refuses loudly and installs nothing when a symbol or an offset is missing:
    /// the engine then reads every static placeable's model on the main thread, as it does without CX.</summary>
    internal static void InstallObjects(ExtenderLog log, HookService hooks)
    {
        _objLog = log;
        SelfCheckObjects(log);
        if (!SymbolMap.Require(log, "ModelLoad.Objects", SymbolMap.AreaAddPlaceableStatic,
                               SymbolMap.C2DAGetCExoStringEntry, SymbolMap.Rules,
                               SymbolMap.ExoStringInit, SymbolMap.ExoStringDtor)
            || !EngineLayout.Require(log, "ModelLoad.Objects", "CNWMessage", "CNWRules", "CTwoDimArrays"))
            return;
        _cell = EngineFn.C2DAGetCExoStringEntry;
        _exoDtor = EngineFn.ExoStringDtor;
        _origAddStatic = hooks.InstallAreaAddPlaceableStatic(&OnAddPlaceableStatic);
        if (_cell == null || _exoDtor == null || _origAddStatic == null || EngineFn.ExoStringInit == null)
        {
            log.Always("ModelLoad.Objects: REFUSED -- AddPlaceableObjectStatic did not hook or a CExoString "
                       + "helper is missing; the engine reads every static placeable's model on the main thread");
            return;
        }
        _column = (IntPtr)NativeMemory.AllocZeroed(ExoSize);
        fixed (byte* col = "ModelName"u8) EngineFn.ExoStringInit(_column, col, uint.MaxValue);
        _objArmed = true;
        log.Info("ModelLoad.Objects: watching LoadArea's static-placeable block -- the first record's call reads "
                 + "the rest out of the message and offers their models ahead of the engine");
    }

    /// <summary>Called at <c>LoadArea</c> entry. There is nothing here to offer (see the class summary); this
    /// resets the block state for the area about to be parsed and, the first time, logs what the area actually
    /// holds at entry so the claim is measured and not merely decompiled.</summary>
    static partial void OfferObjects(nint area)
    {
        Reset();
        _blockOff = false;
        if (_saidEmpty || _objLog == null || area == 0) return;
        _saidEmpty = true;
        var a = new CNWCArea(area);
        _objLog.Info($"ModelLoad.Objects: at LoadArea entry this area holds {a.ObjectCount} object(s), "
                     + $"{a.StaticPlaceableCount} static placeable(s), {a.PlaceableLightCount} placeable light(s) "
                     + "-- the message LoadArea is about to read is the only source of the rest");
    }

    private static bool _saidEmpty;

    private static void Reset()
    {
        _peeked = false; _seen = 0; _count = 0; _rec0 = 0; _stride = 0; _appearanceAt = 0;
        _kinds.Clear();
    }

    /// <summary>One static placeable, mid-<c>LoadArea</c>. Cold -- once per record, a few hundred times per
    /// area -- and the read-ahead runs on the first of them only.</summary>
    [UnmanagedCallersOnly]
    private static int OnAddPlaceableStatic(IntPtr area, uint id, ushort appearance, VectorArg pos, VectorArg orient)
    {
        if (_objArmed && !_blockOff) Track(appearance);
        return _origAddStatic(area, id, appearance, pos, orient);
    }

    /// <summary>Peek the block on the first record, then hold the engine to what was peeked.</summary>
    private static void Track(ushort appearance)
    {
        IntPtr msg = ClientApi.Message();
        if (msg == IntPtr.Zero) { Stand("the client's CNWCMessage did not resolve"); return; }
        var m = new CNWMessage(msg);
        byte* buf = (byte*)m.Buffer;
        uint len = m.Length, cursor = m.Cursor;
        if (buf == null || cursor > len) { Stand("the message buffer is not readable"); return; }
        if (!_peeked) { Peek(buf, len, cursor, appearance); return; }

        _seen++;
        uint at = _rec0 + (uint)_seen * (uint)_stride + (uint)_appearanceAt;
        if (_seen >= _count || at + CountBytes > len || U16(new ReadOnlySpan<byte>(buf, (int)len), at) != appearance)
            Stand($"record {_seen} of {_count} arrived as appearance {appearance}, not the one read ahead");
        else if (_seen == _count - 1) Reset();
    }

    /// <summary>Read the block out of the message and offer its models. The engine has just read record 0, so
    /// the cursor is one record past its start and the count sits before it.</summary>
    private static void Peek(byte* buf, uint len, uint cursor, ushort appearance)
    {
        if (!Shape(buf, len, cursor, appearance, PlainStride, PlainAppearanceAt)
            && !Shape(buf, len, cursor, appearance, TypedStride, TypedAppearanceAt))
        {
            Stand($"the bytes before cursor {cursor} do not read back as a static-placeable record array");
            return;
        }
        _peeked = true;
        _kinds.Add(appearance);
        uint count = _count, rec0 = _rec0;
        int stride = _stride, at = _appearanceAt;
        ObjectRecords = (int)count;
        ObjectOffers = 0;
        AreaKeyObjects(FoldPlaceables(buf, rec0, count, (uint)stride, (uint)at), (int)count);
        var b = new ReadOnlySpan<byte>(buf, (int)len);
        for (uint k = 1; k < count && !_blockOff; k++)
            OfferAppearance(U16(b, rec0 + k * (uint)stride + (uint)at));
        if (_blockOff) return;
        _objLog?.Info($"ModelLoad.Objects: {count} static placeable(s) ahead, {_kinds.Count} appearance(s), "
                      + $"{ObjectOffers} model(s) offered");
        if (count == 1) Reset();
    }

    /// <summary>Accept a record shape only when the appearance the engine was handed reads back where that
    /// shape says it is and the whole block fits the buffer.</summary>
    private static bool Shape(byte* buf, uint len, uint cursor, ushort appearance, int stride, int at)
    {
        if (cursor > len || cursor < (uint)stride + CountBytes) return false;
        uint rec0 = cursor - (uint)stride;
        var b = new ReadOnlySpan<byte>(buf, (int)len);
        if (U16(b, rec0 + at) != appearance) return false;
        uint count = U16(b, rec0 - CountBytes);
        if (count == 0 || count > MaxRecords || (ulong)rec0 + (ulong)count * (ulong)stride > len) return false;
        _rec0 = rec0; _stride = stride; _appearanceAt = at; _count = count;
        return true;
    }

    /// <summary>One appearance -> placeables.2da <c>ModelName</c> -> an offer. Repeats cost nothing.</summary>
    private static void OfferAppearance(ushort appearance)
    {
        if (!_kinds.Add(appearance)) return;
        IntPtr twoda = Placeables();
        if (twoda == IntPtr.Zero) { Stand("placeables.2da did not resolve off g_pRules"); return; }
        byte* exo = stackalloc byte[ExoSize];
        for (int i = 0; i < ExoSize; i++) exo[i] = 0;   // MANDATORY: the engine frees a non-null buffer first
        if (_cell(twoda, appearance, _column, (IntPtr)exo) != 0)
        {
            byte* name = *(byte**)exo;
            if (name != null && *name != 0) { Offer(name); ObjectOffers++; }
        }
        _exoDtor((IntPtr)exo);
    }

    /// <summary>placeables.2da, two hops off <c>g_pRules</c>.</summary>
    private static IntPtr Placeables()
    {
        if (_rulesSym == IntPtr.Zero) return IntPtr.Zero;
        IntPtr rules = *(IntPtr*)_rulesSym;
        if (rules == IntPtr.Zero) return IntPtr.Zero;
        IntPtr tables = new CNWRules(rules).TwoDimArrays;
        return tables == IntPtr.Zero ? IntPtr.Zero : new CTwoDimArrays(tables).Placeables;
    }

    /// <summary>The read-ahead disagreed with the engine, or a source it needs went missing: stop offering for
    /// this block and say so. The engine's own loads are untouched either way.</summary>
    private static void Stand(string why)
    {
        Reset();
        _blockOff = true;
        if (_stood++ < StandDownLogCap)
            _objLog?.Always($"ModelLoad.Objects: read-ahead STOOD DOWN -- {why}. The engine reads this area's "
                            + "static placeable models on the main thread, as it does without CX");
    }

    /// <summary>Off-device, no engine: a message buffer is built in each of the two shapes the stream can have --
    /// <c>ReadWORD</c> count then fixed records, with and without <c>ExpectType</c>'s type byte -- and
    /// <see cref="Shape"/> must recognise it from the cursor and the first record's appearance. Then the refusals:
    /// a wrong appearance, a count running past the buffer, and a count of zero.</summary>
    internal static bool SelfCheckObjects(ExtenderLog log)
    {
        var check = new Checker(log, "ModelLoad.Objects.SelfCheck");
        const int Records = 3, Pad = 8;
        ushort[] kinds = { 1234, 77, 1234 };

        foreach (bool typed in new[] { false, true })
        {
            int stride = typed ? TypedStride : PlainStride, at = typed ? TypedAppearanceAt : PlainAppearanceAt;
            uint rec0 = Pad + (typed ? 1u : 0u) + CountBytes;
            uint len = rec0 + (uint)(Records * stride);
            byte* buf = stackalloc byte[(int)len];
            for (uint i = 0; i < len; i++) buf[i] = 0;
            var bs = new Span<byte>(buf, (int)len);
            Put(bs, rec0 - CountBytes, (ushort)Records);
            for (int k = 0; k < Records; k++) Put(bs, rec0 + k * stride + at, kinds[k]);

            uint cursor = rec0 + (uint)stride;
            Reset();
            check.Expect(Shape(buf, len, cursor, kinds[0], stride, at), $"typed={typed}: the first record's shape is recognised");
            check.Expect(_rec0 == rec0 && _count == Records, $"typed={typed}: record 0 and the count come back off the cursor");
            check.Expect(U16(bs, _rec0 + 2 * (uint)_stride + (uint)_appearanceAt) == kinds[2],
                         $"typed={typed}: the last record reads ahead at the same stride");
            Reset();
            check.Expect(!Shape(buf, len, cursor, (ushort)(kinds[0] + 1), stride, at), $"typed={typed}: a different appearance is not this shape");
            Reset();
            check.Expect(!Shape(buf, cursor, cursor, kinds[0], stride, at), $"typed={typed}: a block running past the buffer is refused");
            Put(bs, rec0 - CountBytes, (ushort)0);
            Reset();
            check.Expect(!Shape(buf, len, cursor, kinds[0], stride, at), $"typed={typed}: a zero count is refused");
        }
        Reset();
        log.Info(check.Ok ? "ModelLoad.Objects.SelfCheck: passed" : "ModelLoad.Objects.SelfCheck: FAILED");
        return check.Ok;
    }
}

/// <summary>The static-placeable read-ahead's one install, in its own service: <c>ModelLoad</c>'s own install
/// site belongs to the model read.</summary>
internal sealed class ModelObjectLoadService : IExtenderService
{
    public void Initialize(ExtenderLog log, HookService hooks) => ModelLoad.InstallObjects(log, hooks);
}
