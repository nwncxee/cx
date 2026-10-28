using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CX.Graphics;

public sealed unsafe partial class NullGl
{
    /// <summary>What the stubs remember between calls: names handed out, the bound texture per unit and buffer per
    /// target, pixel-store state, buffers' host memory, the engine's textures as RGBA8, and the strings GL answers.</summary>
    internal sealed class StubState
    {
        public uint NextName = 1;
        public int Unit;
        public readonly uint[] Bound = new uint[MaxUnits];
        public readonly Dictionary<uint, uint> BoundBuffer = new();
        public readonly Dictionary<uint, HostBuffer> Buffers = new();
        public readonly Dictionary<uint, HeldTexture> Textures = new();
        public int UnpackAlign = 4, UnpackRow;
        public float ClearR, ClearG, ClearB, ClearA;
        public byte* Vendor, Renderer, Version, Glsl, Empty;

        public void Arm(string vendor, string device)
        {
            Vendor = Str(vendor.Length > 0 ? vendor : "CX");
            Renderer = Str(device.Length > 0 ? device + " (Vulkan, no GL)" : "Vulkan, no GL");
            Version = Str("4.6.0 CX no-GL");
            Glsl = Str("4.60");
            Empty = Str("");
        }

        private static byte* Str(string s) => (byte*)Marshal.StringToCoTaskMemUTF8(s);
    }

    /// <summary>A buffer name's host memory: what <c>glMapBuffer</c> hands back.</summary>
    internal sealed class HostBuffer { public IntPtr Mem; public long Size; }

    /// <summary>An engine texture's level 0 as RGBA8, and the filters and wraps it was given.</summary>
    internal sealed class HeldTexture
    {
        public byte[]? Rgba;
        public int W, H;
        public int Min = GlNearestMipLinear, Mag = GlLinear, WrapS = GlRepeat, WrapT = GlRepeat;
    }

    private const int MaxUnits = 32;
    /// <summary>The first name <see cref="CX.Alloy.VkTextures"/> hands out: the stubs' names stay below it.</summary>
    private const uint NameCeiling = 0x40000000;

    private const uint GlTexture2D = 0x0DE1, GlTexture0 = 0x84C0, GlUnsignedByte = 0x1401;
    private const uint GlRed = 0x1903, GlAlpha = 0x1906, GlRgb = 0x1907, GlRgba = 0x1908, GlLuminance = 0x1909,
                       GlLuminanceAlpha = 0x190A, GlBgr = 0x80E0, GlBgra = 0x80E1, GlRg = 0x8227;
    private const uint GlUnpackAlignment = 0x0CF5, GlUnpackRowLength = 0x0CF2;
    private const uint GlMinFilter = 0x2801, GlMagFilter = 0x2800, GlWrapS = 0x2802, GlWrapT = 0x2803;
    private const int GlNearestMipLinear = 0x2702, GlLinear = 0x2601, GlRepeat = 0x2901;
    private const uint GlVendor = 0x1F00, GlRenderer = 0x1F01, GlVersion = 0x1F02, GlExtensions = 0x1F03, GlGlsl = 0x8B8C;
    private const uint GlCompileStatus = 0x8B81, GlLinkStatus = 0x8B82, GlValidateStatus = 0x8B83;
    private const uint GlFramebufferComplete = 0x8CD5, GlAlreadySignaled = 0x911A, GlInvalidIndex = 0xFFFFFFFF;
    private const uint GlColorClearValue = 0x0C22, GlMaxAnisotropy = 0x84FF;

    /// <summary>The integer queries the engine and GLEW act on, and the answer each gets with no GL: version 4.6 core
    /// with no extensions; the limits a desktop GPU reports (NVIDIA's shared-VDO answer for both element counts).</summary>
    private static int IntQuery(uint pname) => pname switch
    {
        0x821D => 0,          // GL_NUM_EXTENSIONS
        0x821B => 4,          // GL_MAJOR_VERSION
        0x821C => 6,          // GL_MINOR_VERSION
        0x9126 => 1,          // GL_CONTEXT_PROFILE_MASK: core
        0x8D57 => 8,          // GL_MAX_SAMPLES
        0x0D33 => 16384,      // GL_MAX_TEXTURE_SIZE
        0x8A34 => 256,        // GL_UNIFORM_BUFFER_OFFSET_ALIGNMENT
        0x0D57 => 8,          // GL_STENCIL_BITS
        0x80E8 => 1 << 20,    // GL_MAX_ELEMENTS_VERTICES
        0x80E9 => 1 << 20,    // GL_MAX_ELEMENTS_INDICES
        0x8872 => 32,         // GL_MAX_TEXTURE_IMAGE_UNITS
        0x8B4D => 192,        // GL_MAX_COMBINED_TEXTURE_IMAGE_UNITS
        0x8869 => 16,         // GL_MAX_VERTEX_ATTRIBS
        _ => 0,
    };

    // ---- the stubs: every one is called by engine code on the game thread, never by CX ----

    [UnmanagedCallersOnly] private static nint Noop() => 0;

    [UnmanagedCallersOnly]
    private static byte* GetString(uint name)
    {
        StubState g = _s.Gl;
        byte* r = name switch
        {
            GlVendor => g.Vendor, GlRenderer => g.Renderer, GlVersion => g.Version, GlGlsl => g.Glsl, GlExtensions => g.Empty,
            _ => g.Empty,
        };
        if (Fresh(1L << 32 | name)) Say($"glGetString(0x{name:x}) answered '{Marshal.PtrToStringUTF8((IntPtr)r)}'");
        return r;
    }

    [UnmanagedCallersOnly] private static byte* GetStringi(uint name, uint index) => _s.Gl.Empty;

    [UnmanagedCallersOnly]
    private static void GetIntegerv(uint pname, int* v)
    {
        if (v == null) return;
        *v = IntQuery(pname);
        int a = *v;
        if (Fresh(2L << 32 | pname)) Say($"glGetIntegerv(0x{pname:x}) answered {a}");
    }

    [UnmanagedCallersOnly]
    private static void GetFloatv(uint pname, float* v)
    {
        if (v == null) return;
        StubState g = _s.Gl;
        if (pname == GlColorClearValue) { v[0] = g.ClearR; v[1] = g.ClearG; v[2] = g.ClearB; v[3] = g.ClearA; }
        else *v = pname == GlMaxAnisotropy ? 16f : IntQuery(pname);
        float a = *v;
        if (Fresh(3L << 32 | pname)) Say($"glGetFloatv(0x{pname:x}) answered {a}");
    }

    [UnmanagedCallersOnly] private static void GetBooleanv(uint pname, byte* v) { if (v != null) *v = 0; }

    [UnmanagedCallersOnly]
    private static void ClearColor(float r, float g, float b, float a)
    {
        StubState s = _s.Gl;
        s.ClearR = r; s.ClearG = g; s.ClearB = b; s.ClearA = a;
    }

    [UnmanagedCallersOnly] private static void GenNames(int n, uint* names) => Names(n, names);

    [UnmanagedCallersOnly] private static uint NewObject() => Name();

    [UnmanagedCallersOnly] private static uint NewShader(uint type) => Name();

    private static void Names(int n, uint* names)
    {
        if (names == null) return;
        for (int i = 0; i < n; i++) names[i] = Name();
    }

    private static uint Name()
    {
        StubState g = _s.Gl;
        uint n = g.NextName++;
        if (n == NameCeiling - 1 && Fresh(4L << 32)) Say("the stubs' GL names reached VkTextures' range -- names wrap to 1");
        if (g.NextName >= NameCeiling) g.NextName = 1;
        return n;
    }

    [UnmanagedCallersOnly]
    private static void GetShaderiv(uint obj, uint pname, int* v)
    {
        if (v != null) *v = pname is GlCompileStatus or GlLinkStatus or GlValidateStatus ? 1 : 0;
    }

    [UnmanagedCallersOnly]
    private static void GetInfoLog(uint obj, int max, int* len, byte* log)
    {
        if (len != null) *len = 0;
        if (log != null && max > 0) *log = 0;
    }

    [UnmanagedCallersOnly] private static int GetLocation(uint program, byte* name) => -1;

    [UnmanagedCallersOnly] private static uint GetBlockIndex(uint program, byte* name) => GlInvalidIndex;

    [UnmanagedCallersOnly] private static uint FramebufferStatus(uint target) => GlFramebufferComplete;

    [UnmanagedCallersOnly] private static nint FenceSync(uint condition, uint flags) => 1;

    [UnmanagedCallersOnly] private static uint ClientWaitSync(nint sync, uint flags, ulong timeout) => GlAlreadySignaled;

    [UnmanagedCallersOnly] private static byte True() => 1;

    [UnmanagedCallersOnly]
    private static void BindBuffer(uint target, uint name)
    {
        StubState g = _s.Gl;
        g.BoundBuffer[target] = name;
    }

    [UnmanagedCallersOnly]
    private static void BufferData(uint target, nint size, void* data, uint usage)
    {
        StubState g = _s.Gl;
        if (!g.BoundBuffer.TryGetValue(target, out uint name) || name == 0 || size <= 0) return;
        if (!g.Buffers.TryGetValue(name, out HostBuffer? b)) g.Buffers[name] = b = new HostBuffer();
        if (b.Size < size)
        {
            if (b.Mem != IntPtr.Zero) NativeMemory.Free((void*)b.Mem);
            b.Mem = (IntPtr)NativeMemory.AllocZeroed((nuint)size);
            b.Size = size;
        }
        if (data != null) Buffer.MemoryCopy(data, (void*)b.Mem, b.Size, size);
    }

    [UnmanagedCallersOnly]
    private static void* MapBuffer(uint target, uint access) => Mapped(target, 0);

    [UnmanagedCallersOnly]
    private static void* MapBufferRange(uint target, nint offset, nint length, uint access) => Mapped(target, offset);

    private static void* Mapped(uint target, nint offset)
    {
        StubState g = _s.Gl;
        void* p = g.BoundBuffer.TryGetValue(target, out uint name) && g.Buffers.TryGetValue(name, out HostBuffer? b)
                  && offset == 0 ? (void*)b.Mem : null;
        if (Fresh(5L << 32 | target))
            Say(p == null
            ? $"glMapBuffer on target 0x{target:x} with no buffer data, or a range past its start -- answered null"
            : $"glMapBuffer on target 0x{target:x} answered host memory");
        return p;
    }

    [UnmanagedCallersOnly]
    private static void DeleteBuffers(int n, uint* names)
    {
        StubState g = _s.Gl;
        for (int i = 0; names != null && i < n; i++)
            if (g.Buffers.Remove(names[i], out HostBuffer? b) && b.Mem != IntPtr.Zero) NativeMemory.Free((void*)b.Mem);
    }

    [UnmanagedCallersOnly]
    private static void ActiveTexture(uint unit)
    {
        int u = (int)(unit - GlTexture0);
        if ((uint)u < MaxUnits) _s.Gl.Unit = u;
    }

    [UnmanagedCallersOnly]
    private static void BindTexture(uint target, uint name)
    {
        if (target == GlTexture2D) { StubState g = _s.Gl; g.Bound[g.Unit] = name; }
    }

    [UnmanagedCallersOnly]
    private static void PixelStorei(uint pname, int v)
    {
        StubState g = _s.Gl;
        if (pname == GlUnpackAlignment) g.UnpackAlign = v is 1 or 2 or 4 or 8 ? v : 4;
        else if (pname == GlUnpackRowLength) g.UnpackRow = Math.Max(0, v);
    }

    [UnmanagedCallersOnly] private static void TexParameteri(uint target, uint pname, int v) => Param(target, pname, v);

    [UnmanagedCallersOnly] private static void TexParameterf(uint target, uint pname, float v) => Param(target, pname, (int)v);

    [UnmanagedCallersOnly] private static void TexParameteriv(uint target, uint pname, int* v) { if (v != null) Param(target, pname, *v); }

    private static void Param(uint target, uint pname, int v)
    {
        if (target != GlTexture2D || Held(create: true) is not HeldTexture t) return;
        lock (_s.Gl.Textures)
        {
            if (pname == GlMinFilter) t.Min = v;
            else if (pname == GlMagFilter) t.Mag = v;
            else if (pname == GlWrapS) t.WrapS = v;
            else if (pname == GlWrapT) t.WrapT = v;
        }
    }

    /// <summary>The texture bound on the active unit, held (made on <paramref name="create"/>); null for name 0.</summary>
    private static HeldTexture? Held(bool create)
    {
        StubState g = _s.Gl;
        uint name = g.Bound[g.Unit];
        if (name == 0) return null;
        lock (g.Textures)
        {
            if (g.Textures.TryGetValue(name, out HeldTexture? t)) return t;
            if (!create) return null;
            g.Textures[name] = t = new HeldTexture();
            return t;
        }
    }

    [UnmanagedCallersOnly]
    private static void TexImage2D(uint target, int level, int ifmt, int w, int h, int border, uint fmt, uint type, byte* data)
    {
        if (target != GlTexture2D || level != 0 || Held(create: true) is not HeldTexture t) return;
        StubState g = _s.Gl;
        uint name = g.Bound[g.Unit];
        int bpp = Bpp(fmt, type);
        if (bpp == 0 || w <= 0 || h <= 0)
        {
            if (Fresh(6L << 32 | fmt << 16 | (type & 0xFFFF)))
                Say($"glTexImage2D format 0x{fmt:x} type 0x{type:x} is not kept -- a texture uploaded so draws nothing on Vulkan");
            lock (g.Textures) { t.Rgba = null; t.W = w; t.H = h; }
            CX.Alloy.GuiPassVk.ForgetEngine(name);
            return;
        }
        byte[] px = new byte[w * h * 4];
        if (data != null) Widen(new ReadOnlySpan<byte>(data, Extent(w, h, bpp, g.UnpackAlign, g.UnpackRow)), 0, 0, w, h, w, fmt, bpp,
                                g.UnpackAlign, g.UnpackRow, px);
        lock (g.Textures) { t.Rgba = px; t.W = w; t.H = h; }
        CX.Alloy.GuiPassVk.ForgetEngine(name);
    }

    [UnmanagedCallersOnly]
    private static void TexSubImage2D(uint target, int level, int x, int y, int w, int h, uint fmt, uint type, byte* data)
    {
        if (target != GlTexture2D || level != 0 || data == null || Held(create: false) is not HeldTexture t) return;
        StubState g = _s.Gl;
        int bpp = Bpp(fmt, type);
        lock (g.Textures)
        {
            if (bpp == 0 || t.Rgba == null || x < 0 || y < 0 || x + w > t.W || y + h > t.H) return;
            Widen(new ReadOnlySpan<byte>(data, Extent(w, h, bpp, g.UnpackAlign, g.UnpackRow)), x, y, w, h, t.W, fmt, bpp,
                  g.UnpackAlign, g.UnpackRow, t.Rgba);
        }
        CX.Alloy.GuiPassVk.ForgetEngine(g.Bound[g.Unit]);
    }

    [UnmanagedCallersOnly]
    private static void DeleteTextures(int n, uint* names)
    {
        StubState g = _s.Gl;
        for (int i = 0; names != null && i < n; i++)
        {
            lock (g.Textures) g.Textures.Remove(names[i]);
            for (int u = 0; u < MaxUnits; u++) if (g.Bound[u] == names[i]) g.Bound[u] = 0;
            CX.Alloy.GuiPassVk.ForgetEngine(names[i]);
        }
    }

    /// <summary>Bytes per texel of an unsigned-byte upload the stubs keep; 0 for anything else.</summary>
    private static int Bpp(uint fmt, uint type) => type != GlUnsignedByte ? 0 : fmt switch
    {
        GlRgba or GlBgra => 4, GlRgb or GlBgr => 3, GlRg or GlLuminanceAlpha => 2, GlRed or GlAlpha or GlLuminance => 1, _ => 0,
    };

    /// <summary>A <paramref name="w"/> x <paramref name="h"/> unsigned-byte block, rows padded to <paramref name="align"/>
    /// and <paramref name="rowLen"/> texels long (0 = <paramref name="w"/>), widened to RGBA8 into <paramref name="dst"/>
    /// (<paramref name="dstW"/> texels wide) at (<paramref name="x"/>, <paramref name="y"/>), the way GL expands each format.</summary>
    internal static void Widen(ReadOnlySpan<byte> src, int x, int y, int w, int h, int dstW, uint fmt, int bpp, int align, int rowLen,
                               byte[] dst)
    {
        int row = Pitch(w, bpp, align, rowLen);
        for (int j = 0; j < h; j++)
        {
            int s = j * row, d = ((y + j) * dstW + x) * 4;
            for (int i = 0; i < w; i++, s += bpp, d += 4)
            {
                byte r, g = 0, b = 0, a = 255;
                switch (fmt)
                {
                    case GlRgba: r = src[s]; g = src[s + 1]; b = src[s + 2]; a = src[s + 3]; break;
                    case GlBgra: r = src[s + 2]; g = src[s + 1]; b = src[s]; a = src[s + 3]; break;
                    case GlRgb: r = src[s]; g = src[s + 1]; b = src[s + 2]; break;
                    case GlBgr: r = src[s + 2]; g = src[s + 1]; b = src[s]; break;
                    case GlRg: r = src[s]; g = src[s + 1]; break;
                    case GlLuminanceAlpha: r = g = b = src[s]; a = src[s + 1]; break;
                    case GlLuminance: r = g = b = src[s]; break;
                    case GlAlpha: r = 0; a = src[s]; break;
                    default: r = src[s]; break;
                }
                dst[d] = r; dst[d + 1] = g; dst[d + 2] = b; dst[d + 3] = a;
            }
        }
    }

    /// <summary>The bytes between rows of an upload: <paramref name="rowLen"/> texels (0 = <paramref name="w"/>) rounded
    /// up to <paramref name="align"/>, as GL's unpack state reads them.</summary>
    private static int Pitch(int w, int bpp, int align, int rowLen)
        => ((rowLen > 0 ? rowLen : w) * bpp + align - 1) / align * align;

    /// <summary>The bytes an upload of <paramref name="w"/> x <paramref name="h"/> reads: every row's pitch but the last's tail.</summary>
    private static int Extent(int w, int h, int bpp, int align, int rowLen)
        => (h - 1) * Pitch(w, bpp, align, rowLen) + w * bpp;

    /// <summary>The engine texture <paramref name="name"/> as the stubs hold it: RGBA8 level 0 and its sampler state, or
    /// null when the engine never uploaded a kept format under it. A copy, so the caller may hold it past a re-upload.</summary>
    internal static byte[]? TakeTexture(uint name, out int w, out int h, out int min, out int mag, out int wrapS, out int wrapT)
    {
        w = h = min = mag = wrapS = wrapT = 0;
        StubState g = _s.Gl;
        lock (g.Textures)
        {
            if (!g.Textures.TryGetValue(name, out HeldTexture? t) || t.Rgba == null) return null;
            w = t.W; h = t.H; min = t.Min; mag = t.Mag; wrapS = t.WrapS; wrapT = t.WrapT;
            return (byte[])t.Rgba.Clone();
        }
    }

    [UnmanagedCallersOnly]
    private static int QueryVersion(nint dpy, int* major, int* minor)
    {
        if (major != null) *major = 1;
        if (minor != null) *minor = 4;
        return 1;
    }

    [UnmanagedCallersOnly] private static byte* ClientString(nint dpy, int name) => _s.Gl.Empty;

    [UnmanagedCallersOnly]
    private static IntPtr GetProc(byte* name) => name == null ? IntPtr.Zero : Proc(Marshal.PtrToStringUTF8((IntPtr)name) ?? "");

    /// <summary>The stub answering GL entry <paramref name="name"/>: its own where the engine acts on the answer, else
    /// the no-op that returns 0. Never null, so GLEW finds every entry it asks for.</summary>
    internal static IntPtr Proc(string name)
    {
        Dictionary<string, IntPtr> t = Procs();
        if (t.TryGetValue(name, out IntPtr p)) return p;
        _s.Generic++;
        return (IntPtr)(delegate* unmanaged<nint>)&Noop;
    }

    private sealed class ProcTable { public Dictionary<string, IntPtr>? Map; }
    private static readonly ProcTable _procs = new();

    private static Dictionary<string, IntPtr> Procs()
    {
        if (_procs.Map != null) return _procs.Map;
        var m = new Dictionary<string, IntPtr>(StringComparer.Ordinal)
        {
            ["glGetString"] = (IntPtr)(delegate* unmanaged<uint, byte*>)&GetString,
            ["glGetStringi"] = (IntPtr)(delegate* unmanaged<uint, uint, byte*>)&GetStringi,
            ["glGetIntegerv"] = (IntPtr)(delegate* unmanaged<uint, int*, void>)&GetIntegerv,
            ["glGetFloatv"] = (IntPtr)(delegate* unmanaged<uint, float*, void>)&GetFloatv,
            ["glGetBooleanv"] = (IntPtr)(delegate* unmanaged<uint, byte*, void>)&GetBooleanv,
            ["glClearColor"] = (IntPtr)(delegate* unmanaged<float, float, float, float, void>)&ClearColor,
            ["glCreateShader"] = (IntPtr)(delegate* unmanaged<uint, uint>)&NewShader,
            ["glCreateProgram"] = (IntPtr)(delegate* unmanaged<uint>)&NewObject,
            ["glGetShaderiv"] = (IntPtr)(delegate* unmanaged<uint, uint, int*, void>)&GetShaderiv,
            ["glGetProgramiv"] = (IntPtr)(delegate* unmanaged<uint, uint, int*, void>)&GetShaderiv,
            ["glGetShaderInfoLog"] = (IntPtr)(delegate* unmanaged<uint, int, int*, byte*, void>)&GetInfoLog,
            ["glGetProgramInfoLog"] = (IntPtr)(delegate* unmanaged<uint, int, int*, byte*, void>)&GetInfoLog,
            ["glGetUniformLocation"] = (IntPtr)(delegate* unmanaged<uint, byte*, int>)&GetLocation,
            ["glGetAttribLocation"] = (IntPtr)(delegate* unmanaged<uint, byte*, int>)&GetLocation,
            ["glGetUniformBlockIndex"] = (IntPtr)(delegate* unmanaged<uint, byte*, uint>)&GetBlockIndex,
            ["glCheckFramebufferStatus"] = (IntPtr)(delegate* unmanaged<uint, uint>)&FramebufferStatus,
            ["glFenceSync"] = (IntPtr)(delegate* unmanaged<uint, uint, nint>)&FenceSync,
            ["glClientWaitSync"] = (IntPtr)(delegate* unmanaged<nint, uint, ulong, uint>)&ClientWaitSync,
            ["glUnmapBuffer"] = (IntPtr)(delegate* unmanaged<byte>)&True,
            ["glBindBuffer"] = (IntPtr)(delegate* unmanaged<uint, uint, void>)&BindBuffer,
            ["glBufferData"] = (IntPtr)(delegate* unmanaged<uint, nint, void*, uint, void>)&BufferData,
            ["glMapBuffer"] = (IntPtr)(delegate* unmanaged<uint, uint, void*>)&MapBuffer,
            ["glMapBufferRange"] = (IntPtr)(delegate* unmanaged<uint, nint, nint, uint, void*>)&MapBufferRange,
            ["glDeleteBuffers"] = (IntPtr)(delegate* unmanaged<int, uint*, void>)&DeleteBuffers,
            ["glActiveTexture"] = (IntPtr)(delegate* unmanaged<uint, void>)&ActiveTexture,
            ["glBindTexture"] = (IntPtr)(delegate* unmanaged<uint, uint, void>)&BindTexture,
            ["glPixelStorei"] = (IntPtr)(delegate* unmanaged<uint, int, void>)&PixelStorei,
            ["glTexParameteri"] = (IntPtr)(delegate* unmanaged<uint, uint, int, void>)&TexParameteri,
            ["glTexParameterf"] = (IntPtr)(delegate* unmanaged<uint, uint, float, void>)&TexParameterf,
            ["glTexParameteriv"] = (IntPtr)(delegate* unmanaged<uint, uint, int*, void>)&TexParameteriv,
            ["glTexImage2D"] = (IntPtr)(delegate* unmanaged<uint, int, int, int, int, int, uint, uint, byte*, void>)&TexImage2D,
            ["glTexSubImage2D"] = (IntPtr)(delegate* unmanaged<uint, int, int, int, int, int, uint, uint, byte*, void>)&TexSubImage2D,
            ["glDeleteTextures"] = (IntPtr)(delegate* unmanaged<int, uint*, void>)&DeleteTextures,
            ["glXQueryVersion"] = (IntPtr)(delegate* unmanaged<nint, int*, int*, int>)&QueryVersion,
            ["glXGetClientString"] = (IntPtr)(delegate* unmanaged<nint, int, byte*>)&ClientString,
            ["glXGetProcAddressARB"] = (IntPtr)(delegate* unmanaged<byte*, IntPtr>)&GetProc,
            ["glXGetProcAddress"] = (IntPtr)(delegate* unmanaged<byte*, IntPtr>)&GetProc,
            ["wglGetProcAddress"] = (IntPtr)(delegate* unmanaged<byte*, IntPtr>)&GetProc,
        };
        foreach (string n in (string[])["glGenTextures", "glGenBuffers", "glGenVertexArrays", "glGenFramebuffers",
                                         "glGenRenderbuffers", "glGenQueries", "glGenSamplers", "glGenProgramPipelines"])
            m[n] = (IntPtr)(delegate* unmanaged<int, uint*, void>)&GenNames;
        return _procs.Map = m;
    }
}
