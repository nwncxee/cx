using System;
using CX.Framework;
using CX.Alloy;   // ShaderResource: override lookup + $TOKEN$ expansion, both content/render concerns

namespace CX.Graphics;

/// <summary>Builds a program on the ENGINE's GL renderer, for the session Vulkan refused and Alloy stood
/// down: compile, link, uniform lookup. Alloy itself renders on Vulkan and compiles nothing here. Alloc-free
/// apart from the log strings on the failure paths, which are cold -- a program builds once at first use or
/// is disabled for the session.</summary>
internal static unsafe class GlProgram
{
    private const int LogChars = 1024;

    /// <summary>Link a vertex+fragment program on the engine's renderer. Returns 0 on failure (already
    /// logged). A library the resource chain overrides (<see cref="ShaderTexts.Lib"/>) is swapped in first,
    /// and a build it breaks rebuilds from the embedded text.</summary>
    internal static uint Graphics(string vs, string fs, ExtenderLog? log, string what)
    {
        string cv = ShaderTexts.Lib(vs, log, what), cf = ShaderTexts.Lib(fs, log, what);
        uint p = Pair(cv, cf, log, what);
        if (p != 0 || (ReferenceEquals(cv, vs) && ReferenceEquals(cf, fs))) return p;
        log?.Info($"{what}: a library override .shd broke the build -- rebuilding from the embedded libraries");
        return Pair(vs, fs, log, what);
    }

    private static uint Pair(string vs, string fs, ExtenderLog? log, string what)
    {
        uint v = Compile(Gl.VERTEX_SHADER, vs, log, what), f = Compile(Gl.FRAGMENT_SHADER, fs, log, what);
        if (v == 0 || f == 0) { if (v != 0) Gl.DeleteShader(v); if (f != 0) Gl.DeleteShader(f); return 0; }
        return Link(log, what, v, f);
    }

    private static uint Link(ExtenderLog? log, string what, uint a, uint b)
    {
        uint prog = Gl.CreateProgram();
        Gl.AttachShader(prog, a);
        Gl.AttachShader(prog, b);
        Gl.LinkProgram(prog);
        int ok; Gl.GetProgramiv(prog, Gl.LINK_STATUS, &ok);
        Gl.DeleteShader(a);
        Gl.DeleteShader(b);
        if (ok != 0) { Notes(prog, false, log, what); return prog; }
        log?.Info($"{what}: link failed -- {InfoLog(prog, false)}");
        Gl.DeleteProgram(prog);
        return 0;
    }

    /// <summary>Compile one stage. The source's sizing tokens are expanded here and nowhere else, so an
    /// embedded default and a content override read identically (see <see cref="ShaderResource.Tokens"/>),
    /// and <see cref="VanillaShader.Splice"/> runs at the same seam for the same reason. A spliced text that
    /// does not compile is retried with its fallback stub, so one bad .shd costs the feature, not the program.</summary>
    private static uint Compile(uint type, string rawSrc, ExtenderLog? log, string what)
    {
        uint sh = CompileText(type, ShaderResource.Tokens(VanillaShader.Splice(rawSrc, log, what, true)), log, what);
        if (sh != 0 || !VanillaShader.HasSplice(rawSrc)) return sh;
        log?.Info($"{what}: WARN the spliced vanilla text did not compile -- retrying with the fallback stub, "
                + "so the program links and the feature it carried is off");
        return CompileText(type, ShaderResource.Tokens(VanillaShader.Splice(rawSrc, log, what, false)), log, what);
    }

    private static uint CompileText(uint type, string src, ExtenderLog? log, string what)
    {
        uint sh = Gl.CreateShader(type);
        Span<byte> b = stackalloc byte[src.Length + 1];
        for (int i = 0; i < src.Length; i++) b[i] = (byte)src[i];
        b[src.Length] = 0;
        fixed (byte* p = b) { byte* pp = p; Gl.ShaderSource(sh, 1, &pp, null); }
        Gl.CompileShader(sh);
        int ok; Gl.GetShaderiv(sh, Gl.COMPILE_STATUS, &ok);
        if (ok != 0) { Notes(sh, true, log, what); return sh; }
        log?.Info($"{what}: shader compile failed (type=0x{type:x}) -- {InfoLog(sh, true)}");
        Gl.DeleteShader(sh);
        return 0;
    }

    /// <summary>A successful build's info log, when the driver left one. Mesa's compiler reports warnings
    /// there (unused varyings, implicit conversions, extension notes) that NVIDIA's never mentions, so it is
    /// the one channel a "compiles on both, differs on one" stage announces itself on. Cold: build time.</summary>
    private static void Notes(uint obj, bool shader, ExtenderLog? log, string what)
    {
        if (log == null) return;
        string s = InfoLog(obj, shader);
        if (s.Length == 0 || s == "(no info log)") return;
        log.Info($"{what}: driver notes on a successful build -- {s}");
    }

    /// <summary>Driver info log, truncated. Cold: build failures and <see cref="Notes"/>.</summary>
    private static string InfoLog(uint obj, bool shader)
    {
        Span<byte> buf = stackalloc byte[LogChars];
        int len = 0;
        fixed (byte* p = buf)
        {
            if (shader) Gl.GetShaderInfoLog(obj, LogChars, &len, p);
            else Gl.GetProgramInfoLog(obj, LogChars, &len, p);
        }
        if (len <= 0) return "(no info log)";
        if (len > LogChars) len = LogChars;
        Span<char> chars = stackalloc char[len];
        for (int i = 0; i < len; i++) chars[i] = (char)buf[i];
        return new string(chars).Replace('\n', ' ').Trim();
    }

    /// <summary>Uniform location by name; -1 when the program does not use it.</summary>
    internal static int Loc(uint prog, string name)
    {
        Span<byte> b = stackalloc byte[name.Length + 1];
        Gl.AsciiZ(name, b);
        fixed (byte* p = b) return Gl.GetUniformLocation(prog, p);
    }
}
