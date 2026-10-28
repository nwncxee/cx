using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The embedded GLSL: every Alloy stage and library is a <c>.glsl</c> file under
/// <c>managed/Alloy/Shaders/</c>, embedded in the image (ADR 0012), and this is the one reader. A file
/// composes others with <c>#include "Other.glsl"</c> on a line of its own, spliced here; anything else stands
/// as written. A spliced library is bracketed, which is what lets <see cref="Content"/> swap a .shd into it.</summary>
internal static class ShaderSource
{
    /// <summary>Opens a spliced library: the line is this plus its resref.</summary>
    internal const string LibMark = "//@cx-lib ";
    /// <summary>Closes a spliced library: the line is this plus its resref.</summary>
    internal const string EndMark = "//@cx-end ";

    /// <summary>Include nesting cap. Four is the deepest chain that ships; the cap is what turns a cycle
    /// into a log line instead of a hang.</summary>
    private const int MaxDepth = 8;

    /// <summary>One embedded stage or library, its includes spliced. Returns "" (logged) for a name no
    /// resource answers -- the program built from it then fails to compile, which is the loud failure.
    /// ponytail: re-reads and re-expands per call, cold by construction; add a cache keyed by name if a
    /// per-frame caller ever appears.</summary>
    internal static string Get(string name) => Get(name, 0);

    private static string Get(string name, int depth)
    {
        string? src = Read(name);
        if (src == null)
        {
            KnobGroup.Log?.Info($"ShaderSource: no embedded {name} -- whatever names it will not build");
            return "";
        }
        return Expand(src, name, depth);
    }

    private static string Expand(string src, string name, int depth)
    {
        if (depth >= MaxDepth)
        {
            KnobGroup.Log?.Info($"ShaderSource: {name} is {MaxDepth} includes deep -- a cycle? its own includes stand unexpanded");
            return src;
        }
        if (!src.Contains("#include \"")) return src;
        string[] lines = src.Split('\n');
        StringBuilder sb = new(src.Length * 2);
        for (int i = 0; i < lines.Length; i++)
        {
            string inc = Included(lines[i]);
            if (inc.Length > 0)
            {
                string body = Get(inc, depth + 1);
                if (body.Length > 0)
                {
                    string r = inc[..^5];
                    sb.Append(LibMark).Append(r).Append('\n').Append(body);
                    if (!body.EndsWith('\n')) sb.Append('\n');
                    sb.Append(EndMark).Append(r).Append('\n');
                    continue;
                }
            }
            sb.Append(lines[i]);
            if (i < lines.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>True when <paramref name="src"/> carries a spliced library a content override could replace.</summary>
    internal static bool HasLibs(string src) => src.Contains(LibMark, StringComparison.Ordinal);

    /// <summary><paramref name="src"/> with every bracketed library the resource chain supplies as
    /// <c>&lt;resref&gt;.shd</c> swapped in; the rest stays embedded, and an override's own <c>#include</c>s
    /// are expanded (and themselves overridable). Returns <paramref name="src"/> itself when nothing was
    /// swapped. ponytail: one resman ask per bracket, not per resref -- cache if build time ever shows it.</summary>
    internal static string Content(string src, ExtenderLog? log)
        => HasLibs(src) ? Content(src, r => ShaderResource.Override(r, log), 0) : src;

    private static string Content(string src, Func<string, string?> lookup, int depth)
    {
        if (depth >= MaxDepth) return src;
        StringBuilder? sb = null;
        int copied = 0, pos = 0, at;
        while ((at = src.IndexOf(LibMark, pos, StringComparison.Ordinal)) >= 0)
        {
            int eol = src.IndexOf('\n', at);
            if (eol < 0) break;
            string r = src[(at + LibMark.Length)..eol];
            string end = EndMark + r + "\n";
            int close = src.IndexOf(end, eol, StringComparison.Ordinal);
            if (close < 0) break;
            int after = close + end.Length;
            string inner = src[(eol + 1)..close];
            string? over = lookup(r);
            string body = Content(over != null ? Expand(over.Replace("\r\n", "\n"), r + ".shd", depth + 1) : inner,
                                  lookup, depth + 1);
            pos = after;
            if (over == null && ReferenceEquals(body, inner)) continue;
            sb ??= new StringBuilder(src.Length + 1024);
            sb.Append(src, copied, eol + 1 - copied).Append(body);
            if (!body.EndsWith('\n')) sb.Append('\n');
            sb.Append(end);
            copied = after;
        }
        return sb == null ? src : sb.Append(src, copied, src.Length - copied).ToString();
    }

    /// <summary>The resource an <c>#include</c> line names, or "" for any other line. The whole line has to
    /// be the directive: the SMAA notice carries <c>#include "SMAA.h"</c> inside a comment block, and a
    /// hosted stand-in carries the engine's <c>#include "inc_scriptable"</c>.</summary>
    private static string Included(string line)
    {
        string t = line.Trim();
        return t.StartsWith("#include \"", StringComparison.Ordinal) && t.EndsWith(".glsl\"", StringComparison.Ordinal)
             ? t[10..^1] : "";
    }

    /// <summary>Every embedded library -- a file no stage resref names -- as (resref, its own text, includes
    /// unexpanded): the dump's starting point for a library override.</summary>
    internal static IEnumerable<(string Ref, string Text)> Libraries()
    {
        HashSet<string> stages = new();
        foreach ((string r, _) in ShaderStages.All) stages.Add(r);
        foreach (string res in typeof(ShaderSource).Assembly.GetManifestResourceNames())
            if (res.EndsWith(".glsl", StringComparison.Ordinal) && !stages.Contains(res[..^5]) && Read(res) is string t)
                yield return (res[..^5], t);
    }

    private static string? Read(string name)
    {
        using Stream? s = typeof(ShaderSource).Assembly.GetManifestResourceStream(name);
        if (s == null) return null;
        using StreamReader r = new(s);
        // A CRLF checkout would otherwise put \r inside every line the stages match on themselves.
        return r.ReadToEnd().Replace("\r\n", "\n");
    }

    /// <summary>Every GLSL text the image ships, named: each embedded stage and library with its includes
    /// spliced, plus the stages composed in C#. This is the text a binding-contract check reads, so a block
    /// or a slot declared anywhere is seen by it.</summary>
    internal static List<(string Name, string Src)> EveryStage()
    {
        var texts = new List<(string, string)>();
        foreach (string res in typeof(ShaderSource).Assembly.GetManifestResourceNames())
            if (res.EndsWith(".glsl", StringComparison.Ordinal)) texts.Add((res, Get(res)));
        texts.Add(("CasterGather.GatherCs", CasterGather.GatherCs));
        texts.Add(("CasterGather.LayerCs", CasterGather.LayerCs));
        texts.Add(("bindless header", AlloyRenderer.BindlessFs("uniform sampler2D uTex;\n")));
        texts.Add(("GpuGolden.CaseBlocks", GpuGolden.CaseBlocks(1, 1)));
        return texts;
    }

    /// <summary>Every embedded .glsl loads and every include in it resolves. A mistyped include is
    /// otherwise invisible until the one program that carries it builds -- on a machine that reaches that
    /// feature.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        int n = 0;
        List<string> bad = new();
        foreach (string res in typeof(ShaderSource).Assembly.GetManifestResourceNames())
        {
            if (!res.EndsWith(".glsl", StringComparison.Ordinal)) continue;
            n++;
            // a hak/NWSync entry is a 16-char resref, and ShaderResource only ever asks for cx_* names
            if (res.Length - 5 > ShaderResource.ResRefBytes || !res.StartsWith("cx_", StringComparison.Ordinal))
            { bad.Add($"{res} is not a cx_* resref of <= {ShaderResource.ResRefBytes} chars"); continue; }
            string src = Get(res);
            if (src.Length == 0) { bad.Add(res); continue; }
            foreach (string line in src.Split('\n'))
                if (Included(line).Length > 0) { bad.Add($"{res} -> {Included(line)}"); break; }
        }
        // the library swap: nothing supplied is the same object back; a nested library is reached; an
        // override's own #include expands; the text either side of the bracket survives
        string lit = Get("cx_ownlit_fs.glsl");
        string over = "// SELFCHECK FOGMIX\n#include \"cx_ssaolib.glsl\"\n";
        string swapped = Content(lit, r => r == "cx_fogmix" ? over : null, 0);
        if (!ReferenceEquals(Content(lit, _ => null, 0), lit)
            || !swapped.Contains("// SELFCHECK FOGMIX") || swapped.Contains("vec3 CxFogColourLin(")
            || !lit.Contains("vec3 CxFogColourLin(") || !swapped.Contains(LibMark + "cx_ssaolib\n")
            || swapped.Length - swapped.Replace("#include \"", "").Length != 0
            || !swapped.StartsWith(lit[..lit.IndexOf(LibMark + "cx_fogmix\n", StringComparison.Ordinal)], StringComparison.Ordinal)
            || !swapped.EndsWith(lit[(lit.IndexOf(EndMark + "cx_fogmix\n", StringComparison.Ordinal))..], StringComparison.Ordinal))
            bad.Add("the library swap (Content)");
        bool ok = n > 0 && bad.Count == 0;
        log.Info(ok ? $"ShaderSource.SelfCheck: passed ({n} embedded .glsl, every include resolved)"
                    : $"ShaderSource.SelfCheck: FAILED -- {(n == 0 ? "no .glsl embedded at all" : "unresolved: " + string.Join(", ", bad))}");
        return ok;
    }
}
