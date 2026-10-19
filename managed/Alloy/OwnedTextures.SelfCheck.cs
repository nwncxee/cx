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

        _armed = false;
        bool ok = Draw(7) == 7 && _borrowed.Count == 0;             // stood down: pass-through, nothing noticed
        _armed = true;
        ok &= Draw(0) == 0 && Draw(-1) == -1 && _borrowed.Count == 0;   // no id is not a texture

        // Never uploaded through our funnel -> BORROWED, counted once per distinct name rather than per frame.
        ok &= Draw(11) == 11 && Draw(11) == 11 && Borrowed == 1;

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
        // #360: the queue's two edges, off-device. A copy that outlives the name it was queued against
        // reads a recycled texture, which is the whole of what killed #211's copy path -- so the drop is
        // asserted here, and asserted to be exact rather than a wipe.
        int svQDied = (int)_pltCopyDied;
        var svQ = new List<PltCopy>(_pltQ);
        _pltQ.Clear();
        _pltQ.Add(new PltCopy(41, 100)); _pltQ.Add(new PltCopy(42, 200)); _pltQ.Add(new PltCopy(41, 300));
        uint deadName = 41;
        Retire(1, &deadName);
        ok &= _pltQ.Count == 1 && _pltQ[0].Src == 42 && _pltCopyDied == svQDied + 2;
        _pltQ.Clear();
        foreach (PltCopy q in svQ) _pltQ.Add(q);
        _pltCopyDied = svQDied;

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

        _armed = wasArmed;
        _retired = savedRetired;   // the check's own synthetic deletes are not a session's
        log.Info(ok ? "OwnedTextures.SelfCheck: passed" : "OwnedTextures.SelfCheck: FAILED");
        return ok;
    }
}
