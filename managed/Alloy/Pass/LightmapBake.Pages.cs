using System;
using System.Diagnostics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Page residency and the cost of moving it. The resident pages are ONE immutable
/// <c>TEXTURE_2D_ARRAY</c> triple, so a page more means a bigger array -- allocate, copy every live layer
/// across, delete the old names -- and that migration was the client's freeze, so it is a state machine:
/// <see cref="GrowSlice"/> layers a frame, published once the last is across, and every WRITER stands still.</summary>
internal static unsafe partial class LightmapBake
{
    /// <summary>Layers a frame may move when a page array grows; 0 = the whole migration in one frame, which
    /// is the old behaviour and the A/B.</summary>
    private static int GrowSlice => Lightmap.GrowSlice.V;

    /// <summary>What <see cref="Grow"/> answers. <b>Pending is not a refusal</b>: the pages are on their way
    /// and the caller must ask again next frame -- reading it as "the card said no" kills the adopt path.</summary>
    private enum Grew { Resident, Pending, Refused }

    /// <summary>The migration in flight: the new arrays and the layer the slice is at. The resident names
    /// (<see cref="BakeGlState.TexA"/> and friends) keep naming the OLD arrays until the last layer is across, so every
    /// read of the window runs against pages that exist -- and every WRITE stands still, see the type doc.</summary>
    private static uint _gA, _gB, _gC;
    private static int _growTo, _growAt;
    /// <summary>A <see cref="Place"/> that could not open a layer because a migration is in flight: the rect
    /// is not past the cap, it is early, so <see cref="Pack"/> puts the rest back rather than refusing them.</summary>
    private static bool _growBusy;
    /// <summary>A migration window is open: every writer of the resident pages sits it out.</summary>
    private static bool Migrating => _growTo != 0;
    /// <summary>Period counters: page growths, how many of them opened a migration window, the layer slices
    /// moved, and the frames a page writer stood down for one.</summary>
    private static int _grows, _growWindows, _growSlices, _growHeld;
    /// <summary>When the window opened: what it costs the page bake in flight is owed back to that page's
    /// "ms wall" (<see cref="_pageSkew"/>), which is meant to be the bake's own pacing.</summary>
    private static long _migT0;
    /// <summary>Ticks the array allocation took inside whatever phase called <see cref="Grow"/>; the next
    /// <see cref="Phase"/> moves them to <see cref="Ph.Grow"/> so the split stays a partition.</summary>
    private static long _growAlloc;

    private static bool AddLayer() => Grow(_gl.Layers + 1) == Grew.Resident;

    /// <summary>Grow the resident planes to <paramref name="n"/> layers. <see cref="Grew.Resident"/> = the
    /// pages are there now (nothing to do, nothing to copy, or <see cref="GrowSlice"/> 0 did it all here);
    /// <see cref="Grew.Pending"/> = a migration is under way, ask again next frame; <see cref="Grew.Refused"/>
    /// = the card would not give the arrays, which stands the pass down as it always did.</summary>
    private static Grew Grow(int n)
    {
        if (Migrating) { _growBusy = true; return Grew.Pending; }
        if (n <= _gl.Layers) return Grew.Resident;
        // billed to Grow by the next Phase call: the array allocation is the one part of a migration no slice
        // can split, so whether IT or the copies dominate is what the worst-frame clause has to say
        long t0 = Stopwatch.GetTimestamp();
        uint a = PageArray(FormatA, n), b = PageArray(FormatB, n), c = PageArray(Gl.RGBA8, n);
        _growAlloc += Stopwatch.GetTimestamp() - t0;
        if (a == 0 || b == 0 || c == 0)
        {
            if (a != 0) Gl.DeleteTextures(1, &a);
            if (b != 0) Gl.DeleteTextures(1, &b);
            if (c != 0) Gl.DeleteTextures(1, &c);
            _down.Refuse(_log, $"page {n} could not be allocated (out of video memory?)", "the rest of the area stays unbaked");
            return Grew.Refused;
        }
        _gA = a; _gB = b; _gC = c; _growTo = n; _growAt = 0; _grows++;
        _migT0 = Stopwatch.GetTimestamp();
        // no live layer to move = no window, and nothing a writer could lose: an area's first pages, and every
        // adopted plan's, are resident in the frame that asked for them
        if (GrowSlice > 0 && _gl.Layers > 0)
        {
            // the allocation is this frame's whole share: the copies start on the next one, so the frame that
            // asked for a page never pays for the array AND for moving the old one
            _log?.Info($"LightmapBake: pages {_gl.Layers} -> {n}/{_cap} ({n * PageMiB} MiB): {_gl.Layers} layer(s) to migrate, {GrowSlice} a frame; {AlloyRenderer.VramFreeMib} MiB of VRAM free before it");
            _growBusy = true; _growWindows++;
            return Grew.Pending;
        }
        // GrowSlice 0 is the A/B against the freeze; _gl.Layers 0 has nothing to copy, only the new layers to
        // clear. Either way it is Grow's time, not the caller's phase's.
        t0 = Stopwatch.GetTimestamp();
        GrowStep();
        _growAlloc += Stopwatch.GetTimestamp() - t0;
        return Grew.Resident;
    }

    /// <summary>One frame's slice of a migration: up to <see cref="GrowSlice"/> layers copied into the new
    /// arrays (or, past the old layer count, C cleared to "no mover share"), and on the last one the new arrays become
    /// the resident ones and the old names go. Finishes in one call when there is nothing to copy, or when
    /// <see cref="GrowSlice"/> is 0.</summary>
    private static void GrowStep()
    {
        if (!Migrating) return;
        int per = GrowSlice > 0 && _gl.Layers > 0 ? GrowSlice : int.MaxValue;
        // a fresh layer is all-zero blocks in A and B (black, and what the readback of a gap files) and no mover
        // share in C. A and B are compressed, so no framebuffer clears them: they upload from a zeroed buffer
        float* none = stackalloc float[4] { 0f, 0f, 0f, 0f };
        uint* bufs = stackalloc uint[1] { Gl.COLOR_ATTACHMENT0 };
        bool bound = false;
        uint zero = 0;
        for (int k = 0; k < per && _growAt < _growTo; k++, _growAt++, _growSlices++)
        {
            int l = _growAt;
            if (l < _gl.Layers)
            {
                CopyLayer(_gl.TexA, _gA, l); CopyLayer(_gl.TexB, _gB, l); CopyLayer(_gl.TexC, _gC, l);
                continue;
            }
            if (!bound)
            {
                Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.Fbo);
                Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT1, Gl.TEXTURE_2D, 0, 0);
                Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT2, Gl.TEXTURE_2D, 0, 0);
                Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, 0, 0);
                Gl.DrawBuffers(1, bufs); bound = true;
            }
            Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, _gC, 0, l);
            Gl.ClearBufferfv(Gl.COLOR, 0, none);
            if (zero == 0)
            {
                Gl.CreateBuffers(1, &zero);
                Gl.NamedBufferStorage(zero, (IntPtr)LayerBlockBytes, null, 0);
                Gl.ZeroBuffer(zero, LayerBlockBytes);
                Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, zero);
            }
            Gl.CompressedTextureSubImage3D(_gA, 0, 0, 0, l, PageSize, PageSize, 1, FormatA, LayerBlockBytes, null);
            Gl.CompressedTextureSubImage3D(_gB, 0, 0, 0, l, PageSize, PageSize, 1, FormatB, LayerBlockBytes, null);
        }
        if (zero != 0) { Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, 0); Gl.DeleteBuffers(1, &zero); }
        if (bound) Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        if (_growAt < _growTo) return;
        // the page bake stood still for this window: hand its wall clock back, or "ms wall per page" reports the
        // migration as the bake's own pacing
        if (_bakePage >= 0) _pageSkew += Stopwatch.GetTimestamp() - _migT0;
        if (_gl.TexA != 0) { uint t = _gl.TexA; Gl.DeleteTextures(1, &t); }
        if (_gl.TexB != 0) { uint t = _gl.TexB; Gl.DeleteTextures(1, &t); }
        if (_gl.TexC != 0) { uint t = _gl.TexC; Gl.DeleteTextures(1, &t); }
        int from = _gl.Layers;
        _gl.TexA = _gA; _gl.TexB = _gB; _gl.TexC = _gC;
        _gA = _gB = _gC = 0; _gl.Layers = _growTo; _growTo = 0; _growAt = 0; _growBusy = false;
        _log?.Info($"LightmapBake: pages {from} -> {_gl.Layers}/{_cap} resident ({_gl.Layers * PageMiB} MiB)");
    }

    private static void CopyLayer(uint src, uint dst, int layer)
        => Gl.CopyImageSubData(src, Gl.TEXTURE_2D_ARRAY, 0, 0, 0, layer, dst, Gl.TEXTURE_2D_ARRAY, 0, 0, 0, layer, PageSize, PageSize, 1);

    /// <summary>A page writer that has to sit out this frame's migration window, counted once a frame.</summary>
    private static bool HeldByMigration()
    {
        if (!Migrating) return false;
        if (_growHeldAt != _frame) { _growHeldAt = _frame; _growHeld++; }
        return true;
    }
    private static int _growHeldAt = -1;

    /// <summary>Throw away a migration in flight: the pages it was copying from are about to go (a repack, the
    /// world edge, the master switched off) or their names are already void (a context loss,
    /// <paramref name="gl"/> false). Idempotent -- hundreds of MiB of arrays are what it releases.</summary>
    private static void GrowAbort(bool gl)
    {
        if (gl && Gl.DeleteTextures != null)
        {
            if (_gA != 0) { uint t = _gA; Gl.DeleteTextures(1, &t); }
            if (_gB != 0) { uint t = _gB; Gl.DeleteTextures(1, &t); }
            if (_gC != 0) { uint t = _gC; Gl.DeleteTextures(1, &t); }
        }
        // the page bake stood still for the part of the window that ran: its wall clock is owed back either way
        if (_growTo != 0 && _bakePage >= 0) _pageSkew += Stopwatch.GetTimestamp() - _migT0;
        _gA = _gB = _gC = 0; _growTo = 0; _growAt = 0; _growBusy = false;
    }

    /// <summary>Free the pages: after a <see cref="Repack"/> every texel on them is dead (each rect lands
    /// again), so keeping them only cost the card the memory and the next <see cref="Grow"/> the copy of that
    /// dead content. The next Pack allocates what the area needs in one go.</summary>
    private static void DropPages()
    {
        GrowAbort(gl: true);
        if (_gl.TexA != 0) { uint t = _gl.TexA; Gl.DeleteTextures(1, &t); }
        if (_gl.TexB != 0) { uint t = _gl.TexB; Gl.DeleteTextures(1, &t); }
        if (_gl.TexC != 0) { uint t = _gl.TexC; Gl.DeleteTextures(1, &t); }
        if (_gl.Layers > 0) _log?.Info($"LightmapBake: {_gl.Layers} pages freed ({_gl.Layers * PageMiB} MiB)");
        _gl.TexA = _gl.TexB = _gl.TexC = 0; _gl.Layers = 0;
    }

    // ---------------------------------------------------------------------------------------------------
    // The pass's own frame accounting.

    /// <summary>The phases of one <see cref="Run"/>. The period line names the one that dominated the worst
    /// frame, so a hitch in the bake says what it was rather than only how long it took.</summary>
    private enum Ph { Area, Rekey, Sweep, Refine, Adopt, Upload, Pack, Grow, Collect, Snap, Steps, Records, Count }
    private static readonly string[] PhName = { "area", "rekey", "sweep", "refine", "adopt", "upload", "pack", "grow", "collect", "snap", "steps", "records" };
    private static readonly long[] _phFrame = new long[(int)Ph.Count];
    /// <summary>The worst single frame of the period and the phase that dominated it, plus how many frames
    /// went past <see cref="WorstFloorMs"/>. The bake owns a frame slot inside the colour bracket, so this is
    /// the number that says whether it is ever the frame.</summary>
    private static double _worstMs;
    private static int _worstPh = -1, _worstFrames;
    /// <summary>The budget a Lightmap frame is counted against: over this a frame is a hitch, not a slice.</summary>
    private const double WorstFloorMs = 4.0;

    /// <summary>Close one phase and open the next: returns the timestamp the caller passes on. Time
    /// <see cref="Grow"/> booked to itself from inside this phase comes off it, so the split is a partition
    /// whichever phase called Grow.</summary>
    private static long Phase(Ph p, long t)
    {
        long n = Stopwatch.GetTimestamp();
        _phFrame[(int)p] += n - t - _growAlloc;
        _phFrame[(int)Ph.Grow] += _growAlloc;
        _growAlloc = 0;
        return n;
    }

    /// <summary>Pure: the index of the largest entry -- which phase dominated the frame.</summary>
    private static int Dominant(long[] t)
    {
        int arg = 0;
        for (int i = 1; i < t.Length; i++) if (t[i] > t[arg]) arg = i;
        return arg;
    }

    /// <summary>Fold this frame's phase split into the worst-frame record.</summary>
    private static void PhaseFrameEnd()
    {
        long tot = 0;
        for (int i = 0; i < (int)Ph.Count; i++) tot += _phFrame[i];
        double ms = tot * 1000.0 / Stopwatch.Frequency;
        if (ms > WorstFloorMs) _worstFrames++;
        if (ms > _worstMs) { _worstMs = ms; _worstPh = Dominant(_phFrame); }
        Array.Clear(_phFrame);
    }

    /// <summary>The period line's clause: the worst frame the bake cost, what it was, and the migrations.</summary>
    private static string PhaseStats()
    {
        string s = $", worst frame {_worstMs:F1} ms in {(_worstPh < 0 ? "nothing" : PhName[_worstPh])} ({_worstFrames} frame(s) over {WorstFloorMs:F0} ms)"
                 + (_grows > 0 ? $", {_grows} page growth(s)" + (_growWindows > 0 ? $" ({_growWindows} sliced over {_growSlices} layer slices)" : " (nothing to migrate)") : "")
                 + (_growHeld > 0 ? $", {_growHeld} frame(s) a migration held the page writers" : "")
                 + (Migrating ? $", a migration to {_growTo} pages at layer {_growAt}" : "");
        PhaseReset();
        return s;
    }

    /// <summary>The period's counters, whether or not the clause went out: a period that prints nothing must
    /// not hand its worst frame to the next one.</summary>
    private static void PhaseReset()
    {
        _worstMs = 0; _worstPh = -1; _worstFrames = 0; _grows = 0; _growWindows = 0; _growSlices = 0; _growHeld = 0;
    }
}
