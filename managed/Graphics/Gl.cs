using CX.Framework;
using static CX.Graphics.EngineTokens;   // the filters LinearClampSampler sets

namespace CX.Graphics;

/// <summary>
/// The engine's OpenGL entry points, for the one thing CX still draws on them: its own windows, in a session
/// where Vulkan refused and the engine's renderer draws. Each resolves through the client's exported
/// SDL_GL_GetProcAddress at first draw, with the engine's context current; <see cref="Init"/> refuses
/// outright where there is no GL at all. All calls are allocation-free.
/// </summary>
public static unsafe class Gl
{
    public const uint TEXTURE_2D = 0x0DE1, BLEND = 0x0BE2, DEPTH_TEST = 0x0B71,
                      CULL_FACE = 0x0B44, SCISSOR_TEST = 0x0C11;
    public const uint FLOAT = 0x1406, FALSE = 0;
    public const uint ARRAY_BUFFER = 0x8892, DYNAMIC_DRAW = 0x88E8;
    public const uint VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30;
    public const uint COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82;
    public const uint TEXTURE0 = 0x84C0;
    public const uint TEXTURE_MIN_FILTER = 0x2801, TEXTURE_MAG_FILTER = 0x2800,
                      TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803, TEXTURE_MAX_LEVEL = 0x813D;
    public const uint UNPACK_ALIGNMENT = 0x0CF5;
    /// <summary>glGetIntegerv's pname for the rect the replay sizes itself to.</summary>
    public const uint VIEWPORT = 0x0BA2;

    public static delegate* unmanaged<uint, int*, void> GetIntegerv;

    public static delegate* unmanaged<uint, void> Enable, Disable, ActiveTexture, UseProgram, BindVertexArray;
    public static delegate* unmanaged<uint, uint, void> BindTexture, BindBuffer, BlendFunc;
    public static delegate* unmanaged<int, uint*, void> GenTextures, GenVertexArrays, GenBuffers;
    public static delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> TexImage2D;
    public static delegate* unmanaged<uint, uint, int, void> TexParameteri;
    public static delegate* unmanaged<uint, int, void> PixelStorei;
    public static delegate* unmanaged<uint, IntPtr, void*, uint, void> BufferData;
    public static delegate* unmanaged<uint, int, uint, byte, int, void*, void> VertexAttribPointer;
    public static delegate* unmanaged<uint, void> EnableVertexAttribArray;
    public static delegate* unmanaged<uint, int, int, void> DrawArrays;
    public static delegate* unmanaged<int, int, int, int, void> Scissor;

    public static delegate* unmanaged<uint, uint> CreateShader;
    public static delegate* unmanaged<uint, int, byte**, int*, void> ShaderSource;
    public static delegate* unmanaged<uint, void> CompileShader, DeleteShader;
    public static delegate* unmanaged<uint, uint, int*, void> GetShaderiv, GetProgramiv;
    public static delegate* unmanaged<uint, int, int*, byte*, void> GetShaderInfoLog;
    public static delegate* unmanaged<uint> CreateProgram;
    public static delegate* unmanaged<uint, uint, void> AttachShader;
    public static delegate* unmanaged<uint, void> LinkProgram;
    public static delegate* unmanaged<uint, byte*, int> GetUniformLocation;
    public static delegate* unmanaged<int, int, byte, float*, void> UniformMatrix4fv;
    public static delegate* unmanaged<int, int, void> Uniform1i;
    public static delegate* unmanaged<int, float, void> Uniform1f;
    public static delegate* unmanaged<int, float, float, void> Uniform2f;
    public static delegate* unmanaged<int, float, float, float, float, void> Uniform4f;
    public static delegate* unmanaged<int, int, int*, void> Uniform1iv;     // (loc, count, ptr) -- per-slot ints

    public static delegate* unmanaged<int, uint*, void> GenSamplers;
    public static delegate* unmanaged<uint, uint, void> BindSampler;
    public static delegate* unmanaged<uint, uint, int, void> SamplerParameteri;

    /// <summary>A LINEAR, clamp-to-edge sampler object. 0 when the context has no sampler objects or the
    /// driver refused the name.</summary>
    public static uint LinearClampSampler()
    {
        if (GenSamplers == null || SamplerParameteri == null) return 0;
        uint s; GenSamplers(1, &s);
        if (s == 0) return 0;
        SamplerParameteri(s, TEXTURE_MIN_FILTER, LINEAR);
        SamplerParameteri(s, TEXTURE_MAG_FILTER, LINEAR);
        SamplerParameteri(s, TEXTURE_WRAP_S, CLAMP_TO_EDGE);
        SamplerParameteri(s, TEXTURE_WRAP_T, CLAMP_TO_EDGE);
        return s;
    }

    private static delegate* unmanaged<byte*, IntPtr> _getProc;
    private static bool _ready, _failed;

    /// <summary>Resolve every entry point once (the engine's context must be current). False = a required
    /// proc was missing, or the session has no GL at all; latched, so nothing re-resolves or logs per frame.</summary>
    public static bool Init(ExtenderLog log)
    {
        if (_ready) return true;
        if (_failed) return false;
        if (NullGl.Active)
        {
            _failed = true;
            log.Always("Gl: a GL entry point was asked for in a session with NO GL context -- refused for the session; the "
                     + "asker's GL path does not run. First asker:\n" + Environment.StackTrace);
            return false;
        }
        _getProc = (delegate* unmanaged<byte*, IntPtr>)Native.Sym(SymbolMap.SdlGlGetProcAddress);
        if (_getProc == null) { log.Info("Gl: SDL_GL_GetProcAddress unresolved"); _failed = true; return false; }

        GetIntegerv = (delegate* unmanaged<uint, int*, void>)P("glGetIntegerv");
        Enable = (delegate* unmanaged<uint, void>)P("glEnable");
        Disable = (delegate* unmanaged<uint, void>)P("glDisable");
        BlendFunc = (delegate* unmanaged<uint, uint, void>)P("glBlendFunc");
        ActiveTexture = (delegate* unmanaged<uint, void>)P("glActiveTexture");
        UseProgram = (delegate* unmanaged<uint, void>)P("glUseProgram");
        BindVertexArray = (delegate* unmanaged<uint, void>)P("glBindVertexArray");
        BindTexture = (delegate* unmanaged<uint, uint, void>)P("glBindTexture");
        BindBuffer = (delegate* unmanaged<uint, uint, void>)P("glBindBuffer");
        GenTextures = (delegate* unmanaged<int, uint*, void>)P("glGenTextures");
        GenVertexArrays = (delegate* unmanaged<int, uint*, void>)P("glGenVertexArrays");
        GenBuffers = (delegate* unmanaged<int, uint*, void>)P("glGenBuffers");
        TexImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)P("glTexImage2D");
        TexParameteri = (delegate* unmanaged<uint, uint, int, void>)P("glTexParameteri");
        PixelStorei = (delegate* unmanaged<uint, int, void>)P("glPixelStorei");
        BufferData = (delegate* unmanaged<uint, IntPtr, void*, uint, void>)P("glBufferData");
        VertexAttribPointer = (delegate* unmanaged<uint, int, uint, byte, int, void*, void>)P("glVertexAttribPointer");
        EnableVertexAttribArray = (delegate* unmanaged<uint, void>)P("glEnableVertexAttribArray");
        DrawArrays = (delegate* unmanaged<uint, int, int, void>)P("glDrawArrays");
        Scissor = (delegate* unmanaged<int, int, int, int, void>)P("glScissor");
        CreateShader = (delegate* unmanaged<uint, uint>)P("glCreateShader");
        ShaderSource = (delegate* unmanaged<uint, int, byte**, int*, void>)P("glShaderSource");
        CompileShader = (delegate* unmanaged<uint, void>)P("glCompileShader");
        DeleteShader = (delegate* unmanaged<uint, void>)P("glDeleteShader");
        GetShaderiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetShaderiv");
        GetProgramiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetProgramiv");
        GetShaderInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetShaderInfoLog");
        CreateProgram = (delegate* unmanaged<uint>)P("glCreateProgram");
        AttachShader = (delegate* unmanaged<uint, uint, void>)P("glAttachShader");
        LinkProgram = (delegate* unmanaged<uint, void>)P("glLinkProgram");
        GetUniformLocation = (delegate* unmanaged<uint, byte*, int>)P("glGetUniformLocation");
        UniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)P("glUniformMatrix4fv");
        Uniform1i = (delegate* unmanaged<int, int, void>)P("glUniform1i");
        Uniform1f = (delegate* unmanaged<int, float, void>)P("glUniform1f");
        Uniform2f = (delegate* unmanaged<int, float, float, void>)P("glUniform2f");
        Uniform4f = (delegate* unmanaged<int, float, float, float, float, void>)P("glUniform4f");
        Uniform1iv = (delegate* unmanaged<int, int, int*, void>)P("glUniform1iv");
        GenSamplers = (delegate* unmanaged<int, uint*, void>)P("glGenSamplers");
        BindSampler = (delegate* unmanaged<uint, uint, void>)P("glBindSampler");
        SamplerParameteri = (delegate* unmanaged<uint, uint, int, void>)P("glSamplerParameteri");

        _ready = CreateProgram != null && GenVertexArrays != null && BufferData != null && DrawArrays != null;
        if (!_ready) { log.Info("Gl: a required GL 3.x proc was missing"); _failed = true; }
        return _ready;
    }

    /// <summary>Uniform location by name; -1 when the program does not use it.</summary>
    internal static int Loc(uint prog, string name)
    {
        Span<byte> b = stackalloc byte[name.Length + 1];
        AsciiZ(name, b);
        fixed (byte* p = b) return GetUniformLocation(prog, p);
    }

    /// <summary>Copy an ASCII name into <paramref name="dst"/>, NUL-terminated: <c>gl*</c> entry points and GLSL
    /// identifiers are ASCII, so the byte cast is exact. <paramref name="dst"/> holds <c>name.Length + 1</c>
    /// bytes, which is what lets every caller keep its buffer on the stack.</summary>
    internal static void AsciiZ(string name, Span<byte> dst)
    {
        for (int i = 0; i < name.Length; i++) dst[i] = (byte)name[i];
        dst[name.Length] = 0;
    }

    /// <summary>Resolve one entry point by name.</summary>
    private static IntPtr P(string name)
    {
        Span<byte> b = stackalloc byte[name.Length + 1];
        AsciiZ(name, b);
        fixed (byte* p = b) return _getProc(p);
    }
}
