using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// NUI's pixels, drawn by Alloy. <c>nk_sdl_render</c> @0xe07bb0 is the stock SDL/GL3 nuklear backend and the
/// engine's only GL draw left above the world frame; this replaces its GL half -- nuklear still runs
/// <c>nk_convert</c>, but into CX-owned buffers, recorded through <see cref="GuiCapture"/>. The frame boundary
/// issues them: panels, below-NUI windows, NUI, above-NUI windows.
/// </summary>
internal static unsafe class NuiFlush
{
    /// <summary>Last subscriber before the original, so it records after every other layer of the chain.</summary>
    private const int NkPriority = 1;

    /// <summary>nuklear's vertex: <c>{vec2 pos, vec2 uv, u8x4 colour}</c>. The engine's own backend declares the
    /// same 20 bytes, so the stream VAO <see cref="GuiStream.FmtNuklear"/> describes is vanilla's layout.</summary>
    private const int VtxStride = 20;

    /// <summary>Vertex ceiling. <see cref="GuiCapture"/> rebases every staged run onto 16-bit indices, so a run may
    /// never span more than 65535 vertices; the engine's own 512 KiB buffer is 26214, well under it.</summary>
    private const int MaxVerts = 65535;
    /// <summary>The engine's own per-frame budget, and where ours starts: <see cref="VtxStart"/> BYTES of
    /// vertices and <see cref="IdxStart"/> INDICES, which is <c>nk_sdl_render</c>'s 512 KiB and 128 KiB of
    /// <c>glBufferData</c> orphan. Unlike the engine's fixed pair, ours grows.</summary>
    private const int VtxStart = 512 * 1024, IdxStart = 64 * 1024;

    private static ExtenderLog? _log;
    private static bool _armed, _live, _stoodDown;
    private static NkSdlRenderFn* _nkSlot;
    private static NkSdlRefreshConfigFn _refresh;
    private static NkConvertFn _convert;
    private static NkClearFn _clear;
    private static IntPtr* _ctxSlot;
    private static IntPtr _cmds, _texNull;
    /// <summary>nuklear's <c>nk_draw_vertex_layout_element[]</c>, transcribed from the engine's own copy at
    /// @0x136a5c0 into unmanaged memory: the pointer is handed to <c>nk_convert</c>, so it must not move.</summary>
    private static NkLayoutElem* _layout;
    private static int* _screenW, _screenH;
    private static byte[] _vtx = [];
    private static ushort[] _idx = [];
    private static RiEnableFn _enable;
    private static RiDisableFn _disable;
    private static GlSetVertexDataObjectFn _setVdo;
    private static long _frames, _cmdsDrawn, _grew, _empty;

    /// <summary>Resolve, then chain. A unit gate: anything missing and nuklear keeps its own flush, on both
    /// platforms, with one line saying which symbol.</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!SymbolMap.Require(log, "NuiFlush", SymbolMap.NkSdlRender, SymbolMap.Nuklear, SymbolMap.NkSdlRefreshConfig,
                               SymbolMap.NkConvert, SymbolMap.NkClear, SymbolMap.NkCmds, SymbolMap.NkTexNull,
                               SymbolMap.RiEnable, SymbolMap.RiDisable, SymbolMap.GlSetVertexDataObject)) return;
        if (!EngineLayout.Require(log, "NuiFlush", "NkContext")) return;
        _screenW = (int*)Native.Sym(SymbolMap.ScreenWidth);
        _screenH = (int*)Native.Sym(SymbolMap.ScreenHeight);
        if (_screenW == null || _screenH == null)
        { log.Always("NuiFlush: g_nScreenWidth/Height unresolved -- NUI stays the ENGINE's draw"); return; }
        _refresh = EngineFn.NkSdlRefreshConfig; _convert = EngineFn.NkConvert; _clear = EngineFn.NkClear;
        _enable = EngineFn.RiEnable; _disable = EngineFn.RiDisable; _setVdo = EngineFn.GlSetVertexDataObject;
        _ctxSlot = (IntPtr*)Native.Sym(SymbolMap.Nuklear);
        _cmds = Native.Sym(SymbolMap.NkCmds);
        _texNull = Native.Sym(SymbolMap.NkTexNull);
        _vtx = new byte[VtxStart];
        _idx = new ushort[IdxStart];
        _layout = (NkLayoutElem*)NativeMemory.AllocZeroed(4, (nuint)sizeof(NkLayoutElem));
        _layout[0] = new NkLayoutElem { Attribute = 0, Format = 6,  Offset = 0 };    // POSITION, FLOAT
        _layout[1] = new NkLayoutElem { Attribute = 2, Format = 6,  Offset = 8 };    // TEXCOORD, FLOAT
        _layout[2] = new NkLayoutElem { Attribute = 1, Format = 11, Offset = 16 };   // COLOR, R8G8B8A8
        _layout[3] = new NkLayoutElem { Attribute = 3, Format = 19, Offset = 0 };    // NK_VERTEX_LAYOUT_END
        _nkSlot = hooks.ChainNkSdlRender(&OnNk, NkPriority);
        if (_nkSlot == null) { log.Always("NuiFlush: nk_sdl_render did not chain -- NUI stays the ENGINE's draw"); return; }
        NuiWindows.Init(log);
        Capabilities.Register("nuirender", 1, static () => _armed && !_stoodDown && Renderer.Gui.V && CX.Services.Nui.NuiKnobs.Enabled.V);
        Renderer.Gui.Changed += Capabilities.Refresh;
        _armed = true;
        log.Info("NuiFlush: armed -- on a captured frame nuklear converts into CX buffers and Alloy draws NUI "
               + "(the engine's flush is not called)");
    }

    /// <summary>The last chain layer before nuklear's own flush. Hot and allocation-free once the staging is grown.</summary>
    [UnmanagedCallersOnly]
    private static void OnNk(float dt)
    {
        IntPtr ctx = _ctxSlot == null ? IntPtr.Zero : *_ctxSlot;
        if (!_armed || _stoodDown || ctx == IntPtr.Zero || !GuiPass.Capturing) { (*_nkSlot)(dt); return; }
        new NkContext(ctx).DeltaTime = dt;
        _refresh();
        NuiWindows.Apply(ctx, _texNull == IntPtr.Zero ? 0 : (int)(long)((NkDrawNullTexture*)_texNull)->Texture,
            CX.Services.Nui.NuiKnobs.Enabled.V, CX.Services.Nui.NuiKnobs.PolicyProbe.V);
        if (!CX.Ui.LoadScreen.PanelUp) Convert(ctx);   // the loadscreen covers the screen: NUI's queue is dropped
        NuiWindows.Finish(ctx);
        _clear(ctx);
        BufferClear(_cmds);
        HandBackEngineState();
        _frames++;
    }

    /// <summary>The net effect the function we replaced had on <c>GLRender</c>'s state caches, and therefore the
    /// contract the engine's overlay views and <c>AurRenderStrings</c> are entitled to: blend and scissor off,
    /// cull and depth on, the current-VDO latch cleared. Cached, so each is a no-op when it already holds. The
    /// raw GL leaks <c>nk_sdl_render</c> left behind are deliberately NOT reproduced: nothing downstream reads them.</summary>
    private static void HandBackEngineState()
    {
        const int CapBlend = 0, CapCull = 3, CapDepthTest = 0xb, CapScissor = 0xd;   // not-an-offset: engine cap enum
        _disable(CapBlend);
        _disable(CapScissor);
        _enable(CapCull);
        _enable(CapDepthTest);
        _setVdo(IntPtr.Zero);
    }

    /// <summary>Convert nuklear's queue into our buffers and record every draw command. Grows once per frame at
    /// most; a conversion that still will not fit is said once and stands NUI's Alloy draw down for the session
    /// -- there is no quiet half-drawn interface.</summary>
    private static void Convert(IntPtr ctx)
    {
        for (int attempt = 0; ; attempt++)
        {
            byte res;
            fixed (byte* v = _vtx)
            fixed (ushort* e = _idx)
            {
                NkBuffer vbuf = Fixed(v, _vtx.Length), ebuf = Fixed(e, _idx.Length * sizeof(ushort));
                NkConvertConfig cfg = Config();
                BufferClear(_cmds);
                res = _convert(ctx, _cmds, (IntPtr)(&vbuf), (IntPtr)(&ebuf), (IntPtr)(&cfg));
                if (res == 0) { Record(ctx, v); return; }
            }
            if (attempt > 0 || !Grow(res)) { StandDown(res); return; }
        }
    }

    /// <summary>Widen whichever buffer overflowed, within the 16-bit index ceiling. False = nothing left to give.</summary>
    private static bool Grow(byte res)
    {
        const byte VertexFull = 4, ElementFull = 8;   // not-an-offset: NK_CONVERT_VERTEX/ELEMENT_BUFFER_FULL
        bool grew = false;
        if ((res & VertexFull) != 0 && _vtx.Length < MaxVerts * VtxStride)
        { _vtx = new byte[Math.Min(_vtx.Length * 2, MaxVerts * VtxStride)]; grew = true; }
        if ((res & ElementFull) != 0 && _idx.Length < GuiCapture.IdxMax)
        { _idx = new ushort[Math.Min(_idx.Length * 2, GuiCapture.IdxMax)]; grew = true; }
        if (grew) { _grew++; _log?.Info($"NuiFlush: staging grown to {_vtx.Length / VtxStride} vertices / {_idx.Length} indices"); }
        return grew;
    }

    private static void StandDown(byte res)
    {
        _stoodDown = true;
        Capabilities.Refresh();
        _log?.Always($"NuiFlush: nk_convert refused (flags 0x{res:x}) at {_vtx.Length / VtxStride} vertices / "
                   + $"{_idx.Length} indices -- NUI goes back to the ENGINE's flush for the rest of the session");
        Notice.Show(_log, "Alloy could not convert the interface's geometry; the game's renderer draws NUI instead.");
    }

    /// <summary>Walk the draw commands and hand each run to <see cref="GuiCapture"/>, opening a new raw layer only
    /// when the scissor changes. The layers are flushed at the frame boundary, after the below-NUI windows and before the
    /// above-NUI ones.</summary>
    private static void Record(IntPtr ctx, byte* vtx)
    {
        var c = new NkContext(ctx);
        int n = c.DrawCmdCount;
        if (n <= 0) { _empty++; return; }
        int w = *_screenW, h = *_screenH;
        if (w <= 0 || h <= 0) { _empty++; return; }
        Matrix4x4 mvp = Ortho(w, h);
        NkDrawCommand* cmd = (NkDrawCommand*)FirstCmd(_cmds, n, c);
        if (cmd == null) { _empty++; return; }
        int offset = 0, sx = int.MinValue, sy = 0, sw = 0, sh = 0;
        bool open = false;
        for (int step = 0; step < n; step++)
        {
            ref NkDrawCommand d = ref cmd[-step];
            int count = (int)d.ElemCount;
            if (count <= 0) continue;
            Scissor(d.ClipX, d.ClipY, d.ClipW, d.ClipH, h, out int nx, out int ny, out int nw, out int nh);
            if (nw <= 0 || nh <= 0) { offset += count; continue; }
            if (!open || nx != sx || ny != sy || nw != sw || nh != sh)
            {
                if (!GuiCapture.BeginRawLayer(0, 0, w, h, nx, ny, nw, nh)) break;
                sx = nx; sy = ny; sw = nw; sh = nh; open = true;
            }
            fixed (ushort* run = _idx.AsSpan(offset, count))
                GuiCapture.Raw(vtx, VtxStride, GuiStream.FmtNuklear, run, count, 0, Gl.TRIANGLES, mvp,
                               d.TextureId, Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
            offset += count;
            _cmdsDrawn++;
        }
        // The panels are recorded later, at SeamDraw's boundary, which flushes them, the below-NUI windows, then these.
        if (!_live) { _live = true; _log?.Info($"NuiFlush: LIVE -- {n} nuklear draw command(s) drawn by Alloy; nk_sdl_render is not called"); }
    }

    // ---- the pure halves, checked off-device ----

    /// <summary>nuklear's ortho, as the GUI program wants it. The engine builds the same constants and divides the
    /// first two by the WINDOW size (<c>nk_sdl_render</c> @0xe07c18): x' = 2x/w - 1, y' = 1 - 2y/h. Written in the
    /// row-vector order <c>cx_gui_v.glsl</c> reads (its <c>mat4(uGui[r..r+3])</c> takes each vec4 as a COLUMN, so
    /// the record is the transpose of the matrix the shader multiplies).</summary>
    internal static Matrix4x4 Ortho(int w, int h) => new(
        2f / w, 0f, 0f, 0f,
        0f, -2f / h, 0f, 0f,
        0f, 0f, -1f, 0f,
        -1f, 1f, 0f, 1f);

    /// <summary>A draw command's clip rect as a GL scissor box, bottom-left. <c>nk_sdl_render</c> scales by
    /// drawable/window; the whole Alloy GUI path takes <c>g_nScreenWidth/Height</c> as both, so that factor is 1
    /// here (a HiDPI drawable would need <c>SDL_GL_GetDrawableSize</c>, which no part of this path reads yet).</summary>
    internal static void Scissor(float cx, float cy, float cw, float ch, int screenH,
                                 out int x, out int y, out int w, out int h)
    {
        x = (int)cx;
        y = screenH - (int)(cy + ch);
        w = (int)cw;
        h = (int)ch;
        if (x < 0) { w += x; x = 0; }
        if (y < 0) { h += y; y = 0; }
    }

    /// <summary>The first draw command. The list is allocated BACKWARDS from the end of <paramref name="cmds"/>, and
    /// the context's <c>drawCmdOffset</c> is the distance from that end to the FIRST command -- written once, in the
    /// <c>cmd_count == 0</c> branch of the push (@0xddc480), so it stays one command's worth however many follow.
    /// The walk then runs DOWNWARD in submission order. Null when the buffer is unset or empty.</summary>
    internal static byte* FirstCmd(IntPtr cmds, int count, NkContext ctx)
    {
        ref NkBuffer b = ref *(NkBuffer*)cmds;
        if (count <= 0 || b.Size == 0 || b.MemoryPtr == IntPtr.Zero) return null;
        return NkQueue.FirstDrawCommand(b.MemoryPtr, (long)b.MemorySize, ctx);
    }

    /// <summary>A fixed <c>nk_buffer</c> over our own memory -- the four stores <c>nk_buffer_init_fixed</c>
    /// makes, which is exactly what the Windows client does inlined.</summary>
    private static NkBuffer Fixed(void* memory, int bytes)
    {
        NkBuffer b = default;
        b.Type = 0;   // NK_BUFFER_FIXED
        b.MemoryPtr = (IntPtr)memory;
        b.MemorySize = (nuint)bytes;
        b.Size = (nuint)bytes;
        return b;
    }

    /// <summary><c>nk_buffer_clear</c>'s four stores, inlined for the same reason.</summary>
    private static void BufferClear(IntPtr buf)
    {
        ref NkBuffer b = ref *(NkBuffer*)buf;
        b.Allocated = 0; b.Needed = 0; b.Calls = 0; b.Size = b.MemorySize;
    }

    /// <summary>The convert config the engine builds every frame: AA on, 22 segments, the live null texture and
    /// nuklear's 20-byte vertex. The layout table is ours -- it is four records of constants, so it needs no
    /// engine address.</summary>
    private static NkConvertConfig Config()
    {
        NkConvertConfig c = default;
        c.GlobalAlpha = 1f;
        c.LineAa = 1; c.ShapeAa = 1;
        c.CircleSegments = 22; c.ArcSegments = 22; c.CurveSegments = 22;
        c.TexNull = *(NkDrawNullTexture*)_texNull;
        c.VertexLayout = (IntPtr)_layout;
        c.VertexSize = VtxStride;
        c.VertexAlignment = 4;
        return c;
    }

    // ---- nuklear's structs. Sequential with the padding named, so the layout IS the offsets: nk_buffer 0x78,
    //      nk_convert_config 0x40, nk_draw_command 0x20, all measured on both binaries (§1, §3 of the research). ----

    [StructLayout(LayoutKind.Sequential)]
    private struct NkBuffer
    {
        private long _marker0, _marker1, _marker2, _marker3;          // marker[2]: {int active; nk_size offset;}
        private IntPtr _poolUserdata, _poolAlloc, _poolFree;          // nk_allocator
        public int Type;
        private int _pad0;
        public IntPtr MemoryPtr;
        public nuint MemorySize;
        private float _growFactor;
        private int _pad1;
        public nuint Allocated, Needed, Calls, Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NkDrawNullTexture { public IntPtr Texture; public float U, V; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NkConvertConfig
    {
        public float GlobalAlpha;
        public int LineAa, ShapeAa;
        public uint CircleSegments, ArcSegments, CurveSegments;
        public NkDrawNullTexture TexNull;
        public IntPtr VertexLayout;
        public nuint VertexSize, VertexAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NkDrawCommand
    {
        public uint ElemCount;
        public float ClipX, ClipY, ClipW, ClipH;
        private int _pad;
        /// <summary>The <c>nk_handle</c>'s low dword -- the GL texture name, which is all the engine's own loop
        /// reads (<c>RenderInterface::BindTexture(4, puVar9[6])</c>).</summary>
        public int TextureId;
        private int _textureHi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NkLayoutElem { public uint Attribute, Format; public nuint Offset; }

    internal static string Stats() => !_armed ? "nui: engine (not armed)" : _stoodDown ? "nui: engine (nk_convert REFUSED)"
        : $"nui: Alloy -- {_frames} flushes, {_cmdsDrawn} commands, {_empty} empty, {_grew} grows";

    /// <summary>Off-device: the three pure halves -- nuklear's struct sizes, the backwards command walk, the
    /// clip-rect-to-scissor flip and the ortho the GUI program is handed.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(sizeof(NkBuffer) == 0x78, "nk_buffer is 0x78 bytes");
        check.Expect(sizeof(NkConvertConfig) == 0x40, "nk_convert_config is 0x40 bytes");
        check.Expect(sizeof(NkDrawCommand) == 0x20, "nk_draw_command is 0x20 bytes");
        check.Expect(sizeof(NkLayoutElem) == 0x10, "a vertex-layout element is 0x10 bytes");
        NkBuffer b = default;
        byte* bb = (byte*)&b;
        check.Expect((byte*)Unsafe.AsPointer(ref b.Type) - bb == 0x38, "nk_buffer.type sits at +0x38");
        check.Expect((byte*)Unsafe.AsPointer(ref b.MemoryPtr) - bb == 0x40, "...memory.ptr at +0x40");
        check.Expect((byte*)Unsafe.AsPointer(ref b.MemorySize) - bb == 0x48, "...memory.size at +0x48");
        check.Expect((byte*)Unsafe.AsPointer(ref b.Allocated) - bb == 0x58, "...allocated at +0x58");
        check.Expect((byte*)Unsafe.AsPointer(ref b.Size) - bb == 0x70, "...size at +0x70");
        NkConvertConfig c = default;
        byte* cb = (byte*)&c;
        check.Expect((byte*)Unsafe.AsPointer(ref c.TexNull) - cb == 0x18, "nk_convert_config.tex_null sits at +0x18");
        check.Expect((byte*)Unsafe.AsPointer(ref c.VertexLayout) - cb == 0x28, "...vertex_layout at +0x28");
        NkDrawCommand d = default;
        check.Expect((byte*)Unsafe.AsPointer(ref d.TextureId) - (byte*)&d == 0x18, "nk_draw_command.texture sits at +0x18");

        // The walk: three commands allocated backwards from the end of a 256-byte buffer. cmd_offset is one
        // command's worth, because the push only writes it for the first one.
        NkDrawCommand* recs = stackalloc NkDrawCommand[3];
        NkBuffer cmds = Fixed(recs, 3 * sizeof(NkDrawCommand));
        for (int i = 0; i < 3; i++) recs[2 - i].ElemCount = (uint)(i + 1);
        Span<byte> ctxMem = stackalloc byte[EngineLayout.NkCtxDrawCmdOffset + sizeof(ulong)];
        ctxMem.Clear();
        NkContext fx;
        fixed (byte* ctxAt = ctxMem) fx = new NkContext((IntPtr)ctxAt);
        fx.DrawCmdOffset = sizeof(NkDrawCommand);
        byte* first = FirstCmd((IntPtr)(&cmds), 3, fx);
        check.Expect(first - (byte*)recs == 2 * sizeof(NkDrawCommand), "the first command is the last record in the buffer");
        var firstCmd = (NkDrawCommand*)first;
        int seen = 0;
        for (int k = 0; k < 3; k++) seen = seen * 10 + (int)firstCmd[-k].ElemCount;
        check.Expect(seen == 123, "the walk runs downwards through every command, in submission order");
        fx.DrawCmdOffset = 0;
        check.Expect(FirstCmd((IntPtr)(&cmds), 0, fx) == null, "no commands is no walk");

        Scissor(10f, 20f, 100f, 50f, 768, out int sx, out int sy, out int sw, out int sh);
        check.Expect(sx == 10 && sw == 100 && sh == 50 && sy == 768 - 70, "a clip rect flips to a bottom-left scissor");
        Scissor(-8192f, -8192f, 16384f, 16384f, 768, out int nx, out int ny, out int nw, out int nh);
        check.Expect(nx == 0 && ny == 0 && nw == 8192 && nh == 8960, "nk_null_rect clamps to a non-negative origin, keeping its far edge");

        Matrix4x4 m = Ortho(1024, 768);
        Vector4 topLeft = Vector4.Transform(new Vector4(0f, 0f, 0f, 1f), m);
        Vector4 bottomRight = Vector4.Transform(new Vector4(1024f, 768f, 0f, 1f), m);
        check.Expect(MathF.Abs(topLeft.X + 1f) < 1e-5f && MathF.Abs(topLeft.Y - 1f) < 1e-5f,
          "nuklear's origin is the top-left corner of clip space");
        check.Expect(MathF.Abs(bottomRight.X - 1f) < 1e-5f && MathF.Abs(bottomRight.Y + 1f) < 1e-5f,
          "...and (w, h) is the bottom-right");
        NuiWindows.SelfCheck(log);
        log.Info(check.Ok ? "NuiFlush.SelfCheck: passed" : "NuiFlush.SelfCheck: FAILED");
    }
}
