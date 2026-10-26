using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The bake itself, page-major and light-major: a page opens, each static light renders the static set
/// into the one scratch cube and draws every rect it reaches, and the finish stage folds the scratch pair into the
/// resident one.</summary>
internal static unsafe partial class LightmapBake
{
    /// <summary>Bytes this thread allocated inside <see cref="Run"/> this period, for the period line: the
    /// frame line's GC total said gigabytes a period and nothing said whose.</summary>
    private static long _allocRun;
    /// <summary>Once a frame, at the frame slot after the probe captures: adopt cache hits, pack newcomers,
    /// advance the bake by its pacing, collect readbacks and upload the records. Inside the colour bracket,
    /// with the shadow bracket's gather staged; the caller restores its target after.</summary>
    internal static void Run(bool capturing, int frame)
    {
        if (Renderer.OnVulkan) return;   // the Vulkan frame runs it through VkRun
        if (!Enabled) { AbortReadback(gl: true); return; }
        if (capturing) return;
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        try { RunInner(frame); }
        finally { _allocRun += GC.GetAllocatedBytesForCurrentThread() - a0; }
    }

    private static void RunInner(int frame)
    {
        if (Renderer.OnVulkan ? !VkEnsure() : !Ensure()) return;
        _frame = frame;
        TrackWindow();
        long t = Stopwatch.GetTimestamp();
        if (Renderer.OnVulkan ? !VkPool() : !Pool()) return;
        t = Phase(Ph.Pool, t);
        InstallAreaCache();   // CX-56: before the sweep -- the plan's set is what arms Enrol at all
        t = Phase(Ph.Area, t);
        if (_rekeyPending) Rekey();
        t = Phase(Ph.Rekey, t);
        Sweep();
        t = Phase(Ph.Sweep, t);
        RefineSweep();
        t = Phase(Ph.Refine, t);
        Adopt();
        t = Phase(Ph.Adopt, t);
        AreaUpload();
        t = Phase(Ph.Upload, t);
        Pack();
        t = Phase(Ph.Pack, t);
        CollectReadback();
        IssueStashed();
        t = Phase(Ph.Collect, t);
        AreaSnap();
        t = Phase(Ph.Snap, t);
        if (Renderer.OnVulkan) VkSteps();
        else
        {
            int steps = Hold ? HoldLightsPerFrame : BackgroundLightsPerFrame;
            for (int k = 0; k < steps; k++) if (!Step()) break;
        }
        t = Phase(Ph.Steps, t);
        UploadRecords();
        Phase(Ph.Records, t);
        PhaseFrameEnd();
    }

    /// <summary>Every owned slot the bake has not seen is offered from the frame, in view or not: the bake is
    /// over the area, not over what the camera happened to show, so a part seen for the first time is already
    /// baked. Round-robin: a chart arrives from the worker whenever it does.</summary>
    private const int SweepPerFrame = 1024;
    private static int _sweepAt;
    private static void Sweep()
    {
        if (SetHash == 0 || AreaAwaited) return;
        int per = Hold ? HoldSweepPerFrame : SweepPerFrame;
        // CX-102 measured this walk and left it alone: an enrolled slot costs one int read (the _slotRect test
        // short-circuits ahead of SlotPlacement) and an empty one two (_slotRect then _oTri), so a steady-state
        // pass over 1024 of them is ~3 us -- a matrix is only copied for a slot that actually enrols. A
        // dirty gate over it could not close either -- a mesh MeshChart permanently refuses leaves its slot
        // at -1 for ever (168 "no rect" charted draws a frame, live), which is indistinguishable from a chart
        // that has not landed yet, so every pass would count as having found something.
        for (int k = 0; k < per; k++)
        {
            int i = _sweepAt; if (++_sweepAt >= _slotRect.Length) _sweepAt = 0;
            if (_slotRect[i] != -1 || !AlloyRenderer.SlotPlacement(i, out int vao, out int item, out Matrix4x4 m)) continue;
            if (m.M44 == 0f) continue;   // never staged yet: the record's matrix is unwritten, the staging loop enrols it
            Enrol(i, vao, item);
        }
        StampSweep(per);
    }

    /// <summary>The static set moved under a page in flight: its light indices name other lights now. The
    /// page's rects stay queued and start over against the new set (or re-key away from it).</summary>
    private static void AbortBake()
    {
        _bakePage = -1; _bakeRects.Clear(); _bakeLights.Clear(); _bakePicks.Clear(); _enc.Row = 0;
    }

    /// <summary>The page finish in flight: texel rows of its union encoded so far.</summary>
    private sealed class EncodeState { public int Row; }
    private static readonly EncodeState _enc = new();
    /// <summary>Frames the next page has waited for the static caster set, and the most casters it waited on.</summary>
    private sealed class HeldWaitState { public int Frames, Peak; }
    private static readonly HeldWaitState _held = new();
    /// <summary>Texel rows of a union one encode dispatch covers: about 3 ms of a full-width union on a
    /// mid-range card. The background takes one band a frame, the hold as many as its steps allow.</summary>
    private const int EncodeRows = 128;

    /// <summary>One (page, light) step of the bake, or a band of the page's finish when its lights are done.
    /// False = nothing more this frame (no queued rect, the scratch cube could not be rendered, or the
    /// background's one encode band went out).</summary>
    private static bool Step()
    {
        if (_bakePage < 0 && !BeginPage()) return false;
        if (_bakeLight < _bakeLights.Count)
        {
            int l = _bakeLights[_bakeLight];
            Vector4 pr = _setPosR[l];
            var pos = new Vector3(pr.X, pr.Y, pr.Z);
            // the cube starts at the self-shadow skip radius: a brazier's bowl sits right under its light
            // node and blocked every steep ray, a dark disc round each one that only grazing rays escaped
            if (!CasterPass.BakeStaticCube(_gl.Cube, _cubeSize, pos, BakeNear, pr.W)) { _bakeSkips++; return false; }
            BakeLight(l, pos, pr.W);
            _bakeLight++; _bakeSteps++;
            return true;
        }
        return FinishPage();
    }

    /// <summary>Open the lowest page holding queued rects: its rect list, the static lights reaching any of
    /// them, the scratch cleared and every rect's coverage laid down.</summary>
    private static bool BeginPage()
    {
        // a queued rect is a pending one: with none pending, the scan below is 20-30k rects for nothing, every frame
        if (_pending == 0 || _frame - _queuedAt < (Hold ? HoldSettleFrames : SettleFrames)) return false;
        // the bake renders the STATIC caster set: a caster still waiting out the mover hold (every one on a fresh load) is not in it yet
        if (CasterPass.HeldStill > 0) { _held.Frames++; _held.Peak = Math.Max(_held.Peak, CasterPass.HeldStill); return false; }
        int page = int.MaxValue;
        for (int ri = 0; ri < _rectN; ri++)
            if (_rects[ri].State == RState.Queued && _rects[ri].Page < page) page = _rects[ri].Page;
        if (page == int.MaxValue) return false;
        _bakeRects.Clear(); _bakeLights.Clear(); _bakePicks.Clear();
        for (int ri = 0; ri < _rectN; ri++)
        {
            if (_rects[ri].State != RState.Queued || _rects[ri].Page != page) continue;
            // no live owner, or the chart range moved out from under it (a VDO slot re-pointed): the rect
            // cannot draw, so it is dropped and its readers re-enrol into a fresh one rather than land black
            ref Rect r = ref _rects[ri];
            if (r.Slot < 0 || !MeshChart.Range(r.Vao, r.Item, 0, out _, out _, out _, out _)) { Drop(ri); continue; }
            _bakeRects.Add(ri); _bakePicks.Add(PickOf(r.C, r.R));
        }
        if (_bakeRects.Count == 0) return false;
        float near = CasterPass.CubeNearPlane;
        for (int l = 0; l < _setN; l++)
        {
            // a reach inside the cube's near plane can never be baked: BakeStaticCube refuses it every frame and the page wedges
            if (!(_setPosR[l].W > near)) continue;
            for (int k = 0; k < _bakeRects.Count; k++)
            {
                ref Rect r = ref _rects[_bakeRects[k]];
                if (Touches(l, r.C, r.R)) { _bakeLights.Add(l); break; }
            }
        }
        _bakePage = page; _bakeLight = 0;
        _pageT0 = Stopwatch.GetTimestamp();
        // the scratch planes: A' = sum rgb + coverage alpha, B' = sum luminance * direction, C' = per channel the mover's luminance
        if (!Renderer.OnVulkan) BeginCoverage();
        else if (!VkCoverage()) { AbortBake(); return false; }
        if (_logLeft > 0) LogNearest(page);
        int crowded = 0;
        if (_logLeft > 0)
            for (int k = 0; k < _bakeRects.Count; k++)
            {
                ref Rect r = ref _rects[_bakeRects[k]];
                int n = 0;
                for (int l = 0; l < _setN && n <= Channels; l++) if (Touches(l, r.C, r.R)) n++;
                if (n > Channels) crowded++;
            }
        if (_logLeft > 0) _log?.Info($"LightmapBake: page {page} bake begins -- {_bakeRects.Count} rects, {_bakeLights.Count} static lights, "
                                     + $"{crowded} rects touched by more than {Channels} (their weaker lights' creature shadows are the live estimate)"
                                     + (_held.Frames > 0 ? $", after {_held.Frames} frame(s) waiting for up to {_held.Peak} casters to join the static set" : "")
                                     + (_knownLogged ? "" : "; known differences on baked surfaces: no static specular beyond the one directional lobe, non-Static placeables cast no shadow from static lights, a fixed-disc penumbra where the live loop runs PCSS"));
        _knownLogged = true;
        _held.Frames = 0; _held.Peak = 0;
        return true;
    }

    /// <summary>The page's coverage on GL: the scratch planes cleared and every rect drawn at coverage.</summary>
    private static void BeginCoverage()
    {
        BindScratch();
        float* zero = stackalloc float[4] { 0f, 0f, 0f, 0f };
        Gl.ClearBufferfv(Gl.COLOR, 0, zero); Gl.ClearBufferfv(Gl.COLOR, 1, zero); Gl.ClearBufferfv(Gl.COLOR, 2, zero);
        Gl.DepthMask(1); Gl.ClearBufferfv(Gl.DEPTH, 0, zero); Gl.DepthMask(0);
        Gl.UseProgram(_gl.ProgBake);
        AlloyLights.BindForDraw();
        Gl.Uniform1i(_bMode, 0);
        BindNullCubes();
        Gl.Uniform1f(_bDepth, -1f);
        uint vao = 0;
        for (int k = 0; k < _bakeRects.Count; k++) DrawRect(_bakeRects[k], ref vao);
        Gl.BindVertexArray(0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
    }

    /// <summary>Diag for a surface baked wrong: the page's rect nearest the camera, its sphere, and the
    /// static lights that reach it (with the nearest one's distance against its reach when none does).</summary>
    private static void LogNearest(int page)
    {
        Vector3 cam = FrameInputs.CameraPos;
        int best = -1; float bd = float.MaxValue;
        for (int k = 0; k < _bakeRects.Count; k++)
        {
            float d = (_rects[_bakeRects[k]].C - cam).LengthSquared();
            if (d < bd) { bd = d; best = _bakeRects[k]; }
        }
        if (best < 0) return;
        ref Rect r = ref _rects[best];
        int n = 0, nl = -1; float nd = float.MaxValue;
        for (int l = 0; l < _setN; l++)
        {
            if (Touches(l, r.C, r.R)) n++;
            float d = (new Vector3(_setPosR[l].X, _setPosR[l].Y, _setPosR[l].Z) - r.C).Length();
            if (d < nd) { nd = d; nl = l; }
        }
        _log?.Info($"LightmapBake: [diag] page {page} nearest rect {r.W}x{r.H} at ({r.C.X:F1},{r.C.Y:F1},{r.C.Z:F1}) r={r.R:F1} slot {r.Slot}: "
                   + $"{n} static lights reach it" + (nl >= 0 ? $", nearest light {nd:F1}u away with reach {_setPosR[nl].W:F1}" : "")
                   + $", {(cam - r.C).Length():F1}u from the camera");
    }

    /// <summary>Compare-on stand-ins on both cube units: the bake's shadow samplers are validated on every
    /// draw, sampled or not, and a unit a lit pass left empty is undefined behaviour (NVIDIA 131222).</summary>
    private static void BindNullCubes()
    {
        uint n = AlloyRenderer.CubeShadowOr(0);
        Gl.ActiveTexture(Gl.TEXTURE0 + AlloyRenderer.CubeUnit); Gl.BindTexture(Gl.TEXTURE_CUBE_MAP_ARRAY, n);
        Gl.ActiveTexture(Gl.TEXTURE0 + AlloyRenderer.CubeDynUnit); Gl.BindTexture(Gl.TEXTURE_CUBE_MAP_ARRAY, n);
        Gl.ActiveTexture(Gl.TEXTURE0);
    }

    private static void BindScratch()
    {
        AttachScratch();
        new PassState(depth: false, depthWrite: false, blend: true, cull: false, srgb: false, blendSrc: Gl.ONE, blendDst: Gl.ONE).Apply();
    }

    /// <summary>The FBO over the three scratch planes and the scratch depth as its outputs.</summary>
    private static void AttachScratch()
    {
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.Fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _gl.ScrA, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT1, Gl.TEXTURE_2D, _gl.ScrB, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT2, Gl.TEXTURE_2D, _gl.ScrC, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, _gl.ScrZ, 0);
        uint* bufs = stackalloc uint[3] { Gl.COLOR_ATTACHMENT0, Gl.COLOR_ATTACHMENT1, Gl.COLOR_ATTACHMENT2 };
        Gl.DrawBuffers(3, bufs);
        Gl.Viewport(0, 0, PageSize, PageSize);
    }

    private static void DrawRect(int ri, ref uint vao)
    {
        ref Rect r = ref _rects[ri];
        if (r.Slot < 0 || !MeshChart.Range(r.Vao, r.Item, 0, out int baseVertex, out nint off, out int count, out _)) return;
        uint want = MeshChart.VaoOf(r.Vao, r.Item);
        if (want == 0) return;
        if (want != vao) { Gl.BindVertexArray(want); vao = want; }
        Gl.Uniform1i(_bSlot, r.Slot);
        Gl.Uniform4f(_bRect, r.X, r.Y, r.S, 0f);
        Gl.Uniform1f(_bSoftWorld, 1f / (MeshChart.AtlasTexelsPerUnit * r.S));
        Gl.DrawElementsBaseVertex(Gl.TRIANGLES, count, CasterPass.GlUnsignedShort, (void*)off, baseVertex);
    }

    /// <summary>One light over the page: the bake program takes the light and its scratch cube exactly the
    /// way the lit stage takes a cube slot (slot 0 of a one-cube array), and every rect it reaches adds.</summary>
    /// <summary>The scratch cube's near plane: the live receiver's self-shadow skip radius (<c>shadow.near</c>
    /// x <c>light.radius</c>), so the geometry a light node sits in casts nothing in the bake.</summary>
    private static float BakeNear => AlloyLights.ShadowNearWorld;

    private static void BakeLight(int l, Vector3 pos, float far)
    {
        bool q = _gl.QBake.Begin(Gl.TIME_ELAPSED);
        BindScratch();
        new PassState(depth: true, depthWrite: true, blend: true, cull: false, srgb: false, blendSrc: Gl.ONE, blendDst: Gl.ONE).Apply();
        Gl.DepthFunc(Gl.GREATER);
        Gl.UseProgram(_gl.ProgBake);
        AlloyLights.BindForDraw();
        Gl.Uniform1i(_bMode, 1);
        Gl.Uniform1f(_bDepth, (float)(_bakeLight + 1) / BakeDepthSteps * 2f - 1f);
        Vector3 c = _setCol[l];
        Gl.Uniform4f(_bLight, pos.X, pos.Y, pos.Z, far);
        Gl.Uniform3f(_bColor, c.X, c.Y, c.Z);
        BindNullCubes();
        Gl.ActiveTexture(Gl.TEXTURE0 + AlloyRenderer.CubeUnit); Gl.BindTexture(Gl.TEXTURE_CUBE_MAP_ARRAY, _gl.Cube);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.Uniform1i(_bCubeCount, 1);
        // CX-43: the scratch cube as slot 0 of the pass block; the colour pass's next bind puts its own back.
        byte* b = AlloyRenderer.PassBegin();
        int* vp = stackalloc int[4] { 0, 0, PageSize, PageSize };
        AlloyLights.FillDraw(b, vp, 1f, 100f);
        var blk = (PassBlock*)b;
        blk->Light[0] = new Vector4(pos.X, pos.Y, pos.Z, far);
        blk->LocalPlane = new Vector2(MathF.Max(BakeNear, CasterPass.CubeNearPlane), 1f / _cubeSize);
        // the disc everywhere (no screen to stand it down against), the near skip, no cap; PCSS off:
        // the bake has no raw view of its cube -- a fixed disc at the softness knob. ponytail: bind the
        // scratch cube through the raw sampler too if the bake's penumbra ever needs to match the live one.
        blk->LocalSoft = new Vector4(AlloyLights.ShadowSoft, AlloyLights.ShadowNearWorld, 0f, 0f);
        blk->LocalFloor = AlloyLights.ShadowFloor;
        blk->LocalPcss = new Vector4(0f, 0f, Shadows.Taps.V, 0f);
        blk->Fade[0].Value = 1f;
        AlloyRenderer.FlushPassBlock();
        uint vao = 0;
        for (int k = 0; k < _bakeRects.Count; k++)
        {
            ref Rect r = ref _rects[_bakeRects[k]];
            if (!Touches(l, r.C, r.R)) continue;
            Gl.Uniform1i(_bChan, _bakePicks[k].ChannelOf(l));
            DrawRect(_bakeRects[k], ref vao);
        }
        Gl.BindVertexArray(0);
        Gl.DepthFunc(Gl.LEQUAL);   // every later pass assumes LEQUAL (#364)
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        // the frame's own cube array back on the unit the lit programs expect it on
        Gl.ActiveTexture(Gl.TEXTURE0 + AlloyRenderer.CubeUnit); Gl.BindTexture(Gl.TEXTURE_CUBE_MAP_ARRAY, CasterPass.CubeTex);
        Gl.ActiveTexture(Gl.TEXTURE0);
        if (q) _gl.QBake.End(Gl.TIME_ELAPSED);
    }

    /// <summary>The page's final pass, a band of <see cref="EncodeRows"/> a call: the scratch planes encode to
    /// BC6H/BC7 blocks for A and B, C goes straight into the layer; after the last band each baked rect's cell
    /// is copied in (cells only: cache-hit rects on the page stay untouched), then the readback and the
    /// records. Returns <see cref="Step"/>'s answer.</summary>
    private static bool FinishPage()
    {
        int page = _bakePage;
        // the encode and the readback cover the union of what landed (its cells: whole blocks), never the page
        int x0 = PageSize, y0 = PageSize, x1 = 0, y1 = 0;
        for (int k = 0; k < _bakeRects.Count; k++)
        {
            CellOf(_rects[_bakeRects[k]], out int cx, out int cy, out int cw, out int ch);
            x0 = Math.Min(x0, cx); y0 = Math.Min(y0, cy);
            x1 = Math.Max(x1, cx + cw); y1 = Math.Max(y1, cy + ch);
        }
        int y = y0 + _enc.Row, bw = (x1 - x0) / Block, bh = Math.Min(EncodeRows, y1 - y) / Block;
        // a rect of the page in flight was evicted or dropped between bands and the union shrank under the
        // rows already encoded: the page starts over rather than dispatch a negative region
        if (bw <= 0 || bh <= 0) { AbortBake(); return true; }
        if (Renderer.OnVulkan) { if (!VkEncode(page, x0 / Block, y / Block, bw, bh)) return false; }
        else EncodeBand(page, x0 / Block, y / Block, bw, bh);
        _enc.Row += bh * Block;
        if (y0 + _enc.Row < y1) return Hold;
        if (!Renderer.OnVulkan) Gl.MemoryBarrier(Gl.ALL_BARRIER_BITS);
        for (int k = 0; k < _bakeRects.Count; k++)
        {
            CellOf(_rects[_bakeRects[k]], out int cx, out int cy, out int cw, out int ch);
            if (Renderer.OnVulkan) { VkCopyCell(page, cx, cy, cw, ch); continue; }
            Gl.CopyImageSubData(_gl.BlkA, Gl.TEXTURE_2D, 0, cx / Block, cy / Block, 0, _gl.TexA, Gl.TEXTURE_2D_ARRAY, 0, cx, cy, page, cw / Block, ch / Block, 1);
            Gl.CopyImageSubData(_gl.BlkB, Gl.TEXTURE_2D, 0, cx / Block, cy / Block, 0, _gl.TexB, Gl.TEXTURE_2D_ARRAY, 0, cx, cy, page, cw / Block, ch / Block, 1);
        }
        FinishLanding(page, x0, y0, x1, y1);
        return true;
    }

    /// <summary>One encode band on GL: the scratch planes into the block images, C into the page layer.</summary>
    private static void EncodeBand(int page, int bx, int by, int bw, int bh)
    {
        Gl.UseProgram(_gl.ProgEnc);
        Gl.ActiveTexture(Gl.TEXTURE0); Gl.BindTexture(Gl.TEXTURE_2D, _gl.ScrA);
        Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_2D, _gl.ScrB);
        Gl.ActiveTexture(Gl.TEXTURE0 + 2); Gl.BindTexture(Gl.TEXTURE_2D, _gl.ScrC);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindImageTexture(0, _gl.BlkA, 0, 0, 0, Gl.WRITE_ONLY, Gl.RGBA32UI);
        Gl.BindImageTexture(1, _gl.BlkB, 0, 0, 0, Gl.WRITE_ONLY, Gl.RGBA32UI);
        Gl.BindImageTexture(2, _gl.TexC, 0, 0, page, Gl.WRITE_ONLY, Gl.RGBA8);
        int* region = stackalloc int[4] { bx, by, bw, bh };
        Gl.Uniform4iv(_eRegion, 1, region);
        Gl.DispatchCompute((uint)(bw + EncGroup - 1) / EncGroup, (uint)(bh + EncGroup - 1) / EncGroup, 1);
        for (uint u = 0; u < 3; u++) Gl.BindImageTexture(u, 0, 0, 0, 0, Gl.WRITE_ONLY, Gl.RGBA8);
        Gl.ActiveTexture(Gl.TEXTURE0 + 2); Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.ActiveTexture(Gl.TEXTURE0); Gl.BindTexture(Gl.TEXTURE_2D, 0);
    }

    /// <summary>The page's cells are in: queue (or stash) its readback, land its rects, close the page.</summary>
    private static void FinishLanding(int page, int x0, int y0, int x1, int y1)
    {
        // the readback: the union's raw bits into one PBO, fenced; CollectReadback writes the files. A previous
        // page's fence still open is polled, never waited on: the render thread does not stall for a cache
        // file, and a page landing that close behind another goes uncached (a miss next session, nothing else).
        if (!ReadbackIdle) { CollectReadback(); IssueStashed(); }
        if (!Renderer.OnVulkan && (Gl.FenceSync == null || Gl.MapNamedBufferRange == null)) _uncached += _bakeRects.Count;
        else if (ReadbackIdle) QueueReadback(page, x0, y0, x1 - x0, y1 - y0, _bakeRects);
        // CX-102: a copy still draining must not turn this landing into a cache miss -- its region waits and is
        // queued the moment the PBO is free. A landed rect keeps its page and place until a Repack.
        else StashReadback(page, x0, y0, x1 - x0, y1 - y0, _bakeRects);
        for (int k = 0; k < _bakeRects.Count; k++) { Land(_bakeRects[k], false); _pending--; }
        double ms = (Stopwatch.GetTimestamp() - _pageT0) * 1000.0 / Stopwatch.Frequency;
        _pageMs += ms; _pageMsN++;
        if (_logLeft-- > 0) _log?.Info($"LightmapBake: page {page} landed -- {_bakeRects.Count} rects over {_bakeLights.Count} lights in {ms:F0} ms wall" + (_gl.QBake.Measured ? $", {_gl.QBake.Avg / 1e6:F2} ms GPU per light step" : ""));
        AbortBake();
    }
}
