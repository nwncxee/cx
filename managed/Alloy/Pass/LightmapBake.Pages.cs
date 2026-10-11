using System;
using System.Diagnostics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The resident pages: one fixed pool, sized once from <c>lightmap.vram</c> and the card and kept across
/// areas, so no area ever pays for a growth. A rect finds its page through its slot's chart record, which is the
/// residency table. A knob change or a lost context reallocates; the main menu or the pass off frees it.</summary>
internal static unsafe partial class LightmapBake
{
    /// <summary>Pure: pool pages for a budget of <paramref name="vramMib"/>, at most a quarter of the card and what
    /// its free memory leaves past <see cref="VramReserveMiB"/> (either 0 = unknown), 1..<see cref="MaxPages"/>.</summary>
    internal static int PoolPages(int vramMib, long totalMib, long freeMib)
    {
        long mib = vramMib;
        if (totalMib > 0) mib = Math.Min(mib, totalMib / 4);
        if (freeMib > 0) mib = Math.Min(mib, freeMib - Math.Max(VramReserveMiB, totalMib / 8));
        return (int)Math.Clamp(mib / PageMiB, 1, MaxPages);
    }

    /// <summary>Free VRAM, MiB, the pool must leave for what streams in after it: a high-asset server's textures
    /// arrive with each creature, long after the bake. The larger of this and an eighth of the card.</summary>
    private const int VramReserveMiB = 1024;

    /// <summary>Make the pool resident: sized once per <c>lightmap.vram</c> value, so free memory moving never
    /// resizes it. A new value frees the old pool and revokes the area first. False = the card would not give
    /// the arrays, which latches the pass off.</summary>
    private static bool Pool()
    {
        if (_gl.Layers > 0 && _gl.PoolMib == Lightmap.Vram.V) return true;
        if (_gl.Layers > 0) { FreePages(); Revoke(); }
        int n = PoolPages(Lightmap.Vram.V, AlloyRenderer.VramTotalMib, AlloyRenderer.VramFreeMib);
        uint a = PageArray(FormatA, n), b = PageArray(FormatB, n), c = PageArray(Gl.RGBA8, n);
        if (a == 0 || b == 0 || c == 0)
        {
            if (a != 0) Gl.DeleteTextures(1, &a);
            if (b != 0) Gl.DeleteTextures(1, &b);
            if (c != 0) Gl.DeleteTextures(1, &c);
            _log?.Always($"LightmapBake: a {n}-page pool ({n * PageMiB} MiB) was refused by the card ({AlloyRenderer.VramFreeMib} MiB free)");
            return _down.Fail(_log, "the page pool could not be allocated (out of video memory?)", "every surface keeps the real-time loop");
        }
        _gl.TexA = a; _gl.TexB = b; _gl.TexC = c; _gl.Layers = n; _gl.PoolMib = Lightmap.Vram.V;
        ClearPages();
        _log?.Info($"LightmapBake: page pool {n} x {PageMiB} MiB = {n * PageMiB} MiB resident (lightmap.vram {Lightmap.Vram.V}); {AlloyRenderer.VramFreeMib} MiB of VRAM free after it");
        return true;
    }

    /// <summary>Every layer to zero blocks in A and B (black, and what the readback of a gap files) and no mover
    /// share in C. A and B are compressed, so no framebuffer clears them: they upload from a zeroed buffer.</summary>
    private static void ClearPages()
    {
        float* none = stackalloc float[4] { 0f, 0f, 0f, 0f };
        uint* bufs = stackalloc uint[1] { Gl.COLOR_ATTACHMENT0 };
        uint zero = 0;
        Gl.CreateBuffers(1, &zero);
        Gl.NamedBufferStorage(zero, (IntPtr)LayerBlockBytes, null, 0);
        Gl.ZeroBuffer(zero, LayerBlockBytes);
        Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, zero);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.Fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT1, Gl.TEXTURE_2D, 0, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT2, Gl.TEXTURE_2D, 0, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, 0, 0);
        Gl.DrawBuffers(1, bufs);
        for (int l = 0; l < _gl.Layers; l++)
        {
            Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, _gl.TexC, 0, l);
            Gl.ClearBufferfv(Gl.COLOR, 0, none);
            Gl.CompressedTextureSubImage3D(_gl.TexA, 0, 0, 0, l, PageSize, PageSize, 1, FormatA, LayerBlockBytes, null);
            Gl.CompressedTextureSubImage3D(_gl.TexB, 0, 0, 0, l, PageSize, PageSize, 1, FormatB, LayerBlockBytes, null);
        }
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, 0); Gl.DeleteBuffers(1, &zero);
    }

    private static void FreePages()
    {
        if (_gl.TexA != 0) { uint t = _gl.TexA; Gl.DeleteTextures(1, &t); }
        if (_gl.TexB != 0) { uint t = _gl.TexB; Gl.DeleteTextures(1, &t); }
        if (_gl.TexC != 0) { uint t = _gl.TexC; Gl.DeleteTextures(1, &t); }
        _log?.Info($"LightmapBake: {_gl.Layers} pages freed ({_gl.Layers * PageMiB} MiB)");
        _gl.TexA = _gl.TexB = _gl.TexC = 0; _gl.Layers = 0;
    }

    /// <summary>From the health bracket, which runs where the colour bracket does not: at the main menu, or with
    /// the pass off or stood down, the pool's memory goes back. Never under the hold, which is an area loading.</summary>
    internal static void ReleaseIdle()
    {
        if (_gl.Layers == 0 || Held || (Enabled && GameGate.InModule)) return;
        FreePages(); Revoke();
    }

    /// <summary>Pages up to the last one holding a placed rect (an evicted fine page leaves a hole below it).</summary>
    private static int PagesUsed
    {
        get { int n = MaxPages; while (n > 0 && _pageClass[n - 1] == 0) n--; return n; }
    }

    // ---------------------------------------------------------------------------------------------------
    // The pass's own frame accounting.

    /// <summary>The phases of one <see cref="Run"/>. The period line names the one that dominated the worst
    /// frame, so a hitch in the bake says what it was rather than only how long it took.</summary>
    private enum Ph { Area, Rekey, Sweep, Refine, Adopt, Upload, Pack, Pool, Collect, Snap, Steps, Records, Count }
    private static readonly string[] PhName = { "area", "rekey", "sweep", "refine", "adopt", "upload", "pack", "pool", "collect", "snap", "steps", "records" };
    private static readonly long[] _phFrame = new long[(int)Ph.Count];
    /// <summary>The worst single frame of a period and the phase that dominated it, plus how many frames went
    /// past <see cref="WorstFloorMs"/>. The bake owns a frame slot inside the colour bracket, so this is the
    /// number that says whether it is ever the frame.</summary>
    private sealed class WorstFrameState { public double Ms; public int Ph = -1, Frames; }
    /// <summary>Kept apart: a frame under the loading hold is behind the backdrop, one in play is a visible hitch.</summary>
    private static WorstFrameState _worstSeen = new(), _worstHeld = new();
    /// <summary>The budget a Lightmap frame is counted against: over this a frame is a hitch, not a slice.</summary>
    private const double WorstFloorMs = 4.0;

    /// <summary>Close one phase and open the next: returns the timestamp the caller passes on.</summary>
    private static long Phase(Ph p, long t)
    {
        long n = Stopwatch.GetTimestamp();
        _phFrame[(int)p] += n - t;
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
        WorstFrameState w = Hold ? _worstHeld : _worstSeen;
        if (ms > WorstFloorMs) w.Frames++;
        if (ms > w.Ms) { w.Ms = ms; w.Ph = Dominant(_phFrame); }
        Array.Clear(_phFrame);
    }

    /// <summary>The period line's clause: the worst frame the bake cost in play and what it was, then the same
    /// under the hold when the period had one.</summary>
    private static string PhaseStats()
    {
        string s = $", worst frame in play {Worst(_worstSeen)}"
                 + (_worstHeld.Ph >= 0 ? $", under the hold {Worst(_worstHeld)}" : "");
        PhaseReset();
        return s;
    }

    private static string Worst(WorstFrameState w)
        => $"{w.Ms:F1} ms in {(w.Ph < 0 ? "nothing" : PhName[w.Ph])} ({w.Frames} frame(s) over {WorstFloorMs:F0} ms)";

    /// <summary>The period's counters, whether or not the clause went out: a period that prints nothing must
    /// not hand its worst frame to the next one.</summary>
    private static void PhaseReset()
    {
        _worstSeen = new(); _worstHeld = new();
    }
}
