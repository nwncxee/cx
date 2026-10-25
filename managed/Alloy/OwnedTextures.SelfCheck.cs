using System;
using System.Buffers;
using System.Numerics;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

internal static unsafe partial class OwnedTextures
{
    /// <summary>Off-device: the residency contract, with no GL and no engine -- that identity is content, that
    /// the engine's GL name is an index rewritten at both of its edges, and that a re-upload retires the old
    /// texture instead of respecifying it. The upload itself needs a client, and is only testable live.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        if (!TexScan.SelfCheck(log)) return false;
        bool wasArmed = _armed;
        long savedRetired = _retired;
        var savedRes = new Dictionary<ulong, Entry>(_res);
        var savedIdx = new Dictionary<int, ulong>(_index);
        var savedOwned = new HashSet<uint>(_owned);
        var savedBorrowed = new HashSet<int>(_borrowed);
        int savedPending = _pending.Count;
        _res.Clear(); _index.Clear(); _owned.Clear(); _borrowed.Clear();
        // The ledger the borrows below are counted in is this check's own, with every shape already "said" so
        // a synthetic tid writes no session line.
        BorrowState savedBorrow = _borrow;
        _borrow = new();
        Array.Fill(_borrow.Said, true);

        _armed = false;
        bool ok = Draw(7) == 7 && _borrowed.Count == 0;             // stood down: pass-through, nothing noticed
        _armed = true;
        ok &= Draw(0) == 0 && Draw(-1) == -1 && _borrowed.Count == 0;   // no id is not a texture

        // Never uploaded through our funnel -> BORROWED, counted once per distinct name rather than per frame.
        ok &= Draw(11) == 11 && Draw(11) == 11 && Borrowed == 1;
        // CX-339: and the shape it is counted under. 11 reached no funnel; 12 was declined for want of a
        // context, which is the shape the one refresh exists to empty. Expected tallies are literals here.
        Declined(12, Borrow.PreContext);
        ok &= Draw(12) == 12 && Draw(12) == 12 && Borrowed == 2
           && _borrow.Count[(int)Borrow.Funnel] == 1 && _borrow.Count[(int)Borrow.PreContext] == 1
           && _borrow.PreCtx == 1 && BorrowNote() == " by shape: never funnelled 1, pre-context 1";
        // A delete takes the reason with the name: the next texture wearing 12 is a fresh question.
        uint* recycled = stackalloc uint[1] { 12 };
        Retire(1, recycled);
        ok &= Borrowed == 1 && !_borrow.Why.ContainsKey(12);
        ok &= Draw(12) == 12 && _borrow.Count[(int)Borrow.Funnel] == 2;
        ForgetBorrow(12);

        // The content key is the identity; the engine name is an index onto it. Two names may resolve one
        // resource, and one name resolves a different resource after a re-upload. The key itself is the
        // engine's hash, so it needs a client -- what is asserted here is the table it drives.
        const ulong k0 = 0x5AF300000000UL, k1 = k0 | 1;   // not-an-offset: splitmix64 mixing constants
        Replace(k0, 13, 99, 4096);
        _index[14] = k0;                                             // a second live name onto one resource
        ok &= Draw(13) == 99 && Draw(14) == 99 && Borrowed == 1;

        // #220: the ownership question the bindless path asks. A borrowed name is NOT owned.
        ok &= IsOwned(99) && !IsOwned(11) && !IsOwned(13) && !IsOwned(0) && !IsOwned(-1);

        // The re-upload path -- a NEW texture rather than a respecified one, because a resident bindless
        // handle makes respecifying storage undefined -- with the old one queued and the generation bumped.
        Replace(k0, 14, 100, 4096);
        ok &= Draw(14) == 100 && Draw(13) == 100 && !IsOwned(99) && IsOwned(100)
              && _res[k0].Gen == 1 && _pending.Count == savedPending + 1 && _pending[savedPending] == 99;

        // The delete funnel is the world edge: index entry gone, resource queued, borrowed mark cleared
        // (a recycled name is a new question), and the tid resolves nothing until an upload re-adopts it.
        uint* dead = stackalloc uint[3] { 13, 14, 11 };
        Retire(3, dead);   // the budget-0 edge: Retire discards (FilterRetire is the cache edge, checked in SelfCheckCache)
        ok &= !_index.ContainsKey(13) && !_index.ContainsKey(14) && !_res.ContainsKey(k0)
              && !IsOwned(100) && _pending.Count == savedPending + 2 && Borrowed == 0;
        ok &= Draw(13) == 13 && Draw(14) == 14 && Borrowed == 2 && !_res.ContainsKey(k1);
        _pending.RemoveRange(savedPending, 2);

        // #397: the single-upload shape, where the engine name IS the owned name. A refresh into our own name
        // retires 100 and Drain deletes it; the driver reissues a freed name at once, so an index entry that
        // outlives the texture hands the next engine texture wearing 100 the OLD resource's image.
        Replace(k1, 100, 100, 64); Replace(k1, 101, 101, 64);
        ok &= Draw(101) == 101 && _pending[^1] == 100;
        Forget(100);   // what Drain does with a name it deletes
        ok &= !_index.ContainsKey(100) && Draw(100) == 100 && Borrowed == 3 && Draw(101) == 101;
        _pending.RemoveRange(savedPending, _pending.Count - savedPending);

        // #265: the census window -- tallies split by path, silence settles into ONE report, and the next
        // upload opens a fresh window. The +0x70 read itself needs a client; what is pinned is the window.
        int svRaw = _censusRaw, svBcn = _censusBcn, svLast = _censusLastUpload;
        long svRawB = _censusRawBytes, svBcnB = _censusBcnBytes;
        bool svRep = _censusReported; int svDrip2 = _dripFrame;
        ExtenderLog? svLog = _log; _log = null;   // the check's synthetic report is not a session's line
        int svTrans = _censusTrans; long svSaved = _censusSaved;
        _censusRaw = _censusBcn = _censusTrans = 0;
        _censusRawBytes = _censusBcnBytes = _censusSaved = 0;
        _censusReported = false; _dripFrame = 0;
        int svGrid = _censusGrid, svGridCells = _censusGridCells; long svGridB = _censusGridBytes;
        _censusGrid = _censusGridCells = 0; _censusGridBytes = 0;
        CensusTally(false, 100, 300); CensusTally(true, 300, 0); CensusTally(false, 100, 0);
        ok &= _censusRaw == 2 && _censusBcn == 1 && _censusRawBytes == 200 && _censusBcnBytes == 300
           && _censusTrans == 1 && _censusSaved == 300;
        // #266: a 2x2 grid -- cell 0 counts the texture and its cell total, every cell counts bytes.
        CensusTally(true, 10, 0, 4, 0); CensusTally(true, 10, 0, 4, 1);
        CensusTally(true, 10, 0, 4, 2); CensusTally(true, 10, 0, 4, 3);
        ok &= _censusGrid == 1 && _censusGridCells == 4 && _censusGridBytes == 40;
        CensusSettleCheck(); ok &= !_censusReported;              // still uploading: not settled
        _dripFrame += CensusSettle;
        CensusSettleCheck(); ok &= _censusReported;               // quiet long enough: one report
        CensusTally(true, 50, 0);                                 // next area's first upload resets the window
        ok &= !_censusReported && _censusRaw == 0 && _censusBcn == 1 && _censusBcnBytes == 50 && _censusTrans == 0
           && _censusGrid == 0 && _censusGridCells == 0 && _censusGridBytes == 0;
        _censusGrid = svGrid; _censusGridCells = svGridCells; _censusGridBytes = svGridB;
        _censusRaw = svRaw; _censusBcn = svBcn; _censusRawBytes = svRawB; _censusBcnBytes = svBcnB;
        _censusTrans = svTrans; _censusSaved = svSaved;
        _censusLastUpload = svLast; _censusReported = svRep; _dripFrame = svDrip2; _log = svLog;

        _res.Clear(); _index.Clear(); _owned.Clear(); _borrowed.Clear();
        foreach (var kv in savedRes) _res[kv.Key] = kv.Value;
        foreach (var kv in savedIdx) _index[kv.Key] = kv.Value;
        foreach (uint o in savedOwned) _owned.Add(o);
        foreach (int b in savedBorrowed) _borrowed.Add(b);
        // #358: the hold itself, over a stand-in flag. A re-clear that does not actually zero the global is
        // the whole bug wearing a fixed label, and the readers' trampolines are two lines around this call.
        int stand = 1;
        int* svPlt = _pltOnGpu; long svForced = _pltForced; int svDiag = _pltDiagLeft;
        _pltOnGpu = &stand; _pltForced = 1; _pltDiagLeft = 0;      // budget spent: no log line from a check
        KeepPltOnCpu();
        ok &= stand == 0 && _pltForced == 2;
        KeepPltOnCpu();
        ok &= _pltForced == 2;                                      // already zero: nothing to re-clear
        _pltOnGpu = svPlt; _pltForced = svForced; _pltDiagLeft = svDiag;

        ok &= SelfCheckUpload(log);

        _armed = wasArmed;
        _retired = savedRetired;   // the check's own synthetic deletes are not a session's
        _borrow = savedBorrow;
        log.Info(ok ? "OwnedTextures.SelfCheck: passed" : "OwnedTextures.SelfCheck: FAILED");
        return ok;
    }

    /// <summary>The engine's upload decisions, off-device. Oracle: the GL enums <c>GLRender::SetTextureParameters</c>
    /// @0x4ca180 writes per branch and <c>SetTextureMipmapLevelParameters</c> @0x4c9c70's MAX_LEVEL, read off the
    /// disassembly and written here as literals.</summary>
    private static bool SelfCheckUpload(ExtenderLog log)
    {
        var check = new Checker(log);
        Sampler s = EngineSampler(0, linear: true, mip: true);
        check.Expect(s == new Sampler(0x2703, 0x2601, 0x2901, 0x2901, 0x2901, true), $"linear mipped repeat: {s}");      // not-an-offset: GL enums
        s = EngineSampler(3, linear: true, mip: false);
        check.Expect(s == new Sampler(0x2600, 0x2601, 0x812F, 0x812F, 0x2901, false), $"linear unmipped clamped: {s}");  // not-an-offset: GL enums
        s = EngineSampler(2, linear: false, mip: true);
        check.Expect(s == new Sampler(0x2700, 0x2600, 0x2901, 0x812F, 0x2901, false), $"nearest mipped, T clamped: {s}"); // not-an-offset: GL enums
        s = EngineSampler(1, linear: false, mip: false);
        check.Expect(s == new Sampler(0x2600, 0x2600, 0x812F, 0x2901, 0x2901, false), $"nearest unmipped, S clamped: {s}"); // not-an-offset: GL enums
        s = EngineSampler(CubeClamp, linear: true, mip: true);
        check.Expect(s == new Sampler(0x2703, 0x2601, 0x812F, 0x812F, 0x812F, true), $"cube: {s}");             // not-an-offset: GL enums
        check.Expect(EngineLevels(256, 256) == 9 && EngineLevels(512, 64) == 10 && EngineLevels(1, 1) == 1 && EngineLevels(3, 5) == 3,
                     $"levels: {EngineLevels(256, 256)} {EngineLevels(512, 64)} {EngineLevels(1, 1)} {EngineLevels(3, 5)}");

        // CX-339: the storage/format/type triple per bytes-per-texel. Oracle: the GL enum values written as
        // literals, and for two channels the engine's own format table entry 9 (GL_RGBA4), which is what
        // ImageConvert32to16bit @0x4b7d50 stamps on the 4-bits-per-channel image it packs.
        bool got = RawFormat(4, out uint sz, out uint fm, out uint ty);
        check.Expect(got && sz == 0x8058 && fm == 0x1908 && ty == 0x1401, $"bpp 4: {sz:x}/{fm:x}/{ty:x}");       // not-an-offset: GL enums
        got = RawFormat(3, out sz, out fm, out ty);
        check.Expect(got && sz == 0x8051 && fm == 0x1907 && ty == 0x1401, $"bpp 3: {sz:x}/{fm:x}/{ty:x}");       // not-an-offset: GL enums
        got = RawFormat(2, out sz, out fm, out ty);
        check.Expect(got && sz == 0x8056 && fm == 0x1908 && ty == 0x8033, $"bpp 2: {sz:x}/{fm:x}/{ty:x}");       // not-an-offset: GL enums
        got = RawFormat(1, out sz, out fm, out ty);
        check.Expect(got && sz == 0x8229 && fm == 0x1903 && ty == 0x1401, $"bpp 1: {sz:x}/{fm:x}/{ty:x}");       // not-an-offset: GL enums
        check.Expect(!RawFormat(0, out _, out _, out _) && !RawFormat(5, out _, out _, out _), "no texel width but 1..4 uploads");

        // The 4_4_4_4 read itself, hand-unpacked: the two bytes ImageConvert32to16bit writes for one RGBA8
        // texel, back through GL's RGBA/UNSIGNED_SHORT_4_4_4_4 rule (R from the top nibble down). Oracle: the
        // packing written out by hand from the disassembly, and the expected channels as literals.
        check.Expect(Pack4444(0x12, 0x34, 0x56, 0x78) == 0x1357 && Pack4444(0xff, 0x00, 0xff, 0x0f) == 0xF0F0,
                     $"pack: {Pack4444(0x12, 0x34, 0x56, 0x78):x4} {Pack4444(0xff, 0x00, 0xff, 0x0f):x4}");
        Unpack4444(0x1357, out int r, out int g, out int b, out int a);
        check.Expect(r == 1 && g == 3 && b == 5 && a == 7, $"unpack: {r} {g} {b} {a}");
        return check.Ok;
    }

    /// <summary>One RGBA8 texel as <c>ImageConvert32to16bit</c> @0x4b7d50 packs it: a little-endian ushort whose
    /// low byte is <c>(B &amp; 0xf0) | (A &gt;&gt; 4)</c> and high byte <c>(R &amp; 0xf0) | (G &gt;&gt; 4)</c>.
    /// Check-only: the engine's packer is dead code in the shipped client, and the bytes come off disk.</summary>
    private static ushort Pack4444(int r, int g, int b, int a)
        => (ushort)((((b & 0xf0) | (a >> 4)) & 0xff) | ((((r & 0xf0) | (g >> 4)) & 0xff) << 8));

    /// <summary>The same ushort as <c>GL_RGBA</c> + <c>GL_UNSIGNED_SHORT_4_4_4_4</c> reads it: first component in
    /// the most significant nibble. Check-only -- the real read is the driver's.</summary>
    private static void Unpack4444(ushort v, out int r, out int g, out int b, out int a)
    {
        r = (v >> 12) & 0xf; g = (v >> 8) & 0xf; b = (v >> 4) & 0xf; a = v & 0xf;
    }
}
