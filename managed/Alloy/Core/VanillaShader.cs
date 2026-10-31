using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The VANILLA BRIDGE: an Alloy stage can name a CONTENT <c>.shd</c> and get the engine's own text
/// spliced into it at compile time, so shading a content repo has already customised is not re-implemented
/// here. Fragment-side only; a stock stage's lighting and any vertex stage stay out. A source splice, not a
/// linked object: Alloy supplies the environment (<see cref="AlloyRenderer.VanillaEnvFs"/>) its includes want.</summary>
internal static unsafe class VanillaShader
{
    /// <summary>The arming decision (ADR 0013 bucket 2 -- a capability gate, not a look), read once.
    /// <c>alloy.vanillabridge=0</c> in <c>cx.cfg</c> compiles every stage from its fallback stubs and asks the
    /// engine for nothing. It exists because reading a resource the engine owns is engine surgery, and a kill
    /// switch that needs no rebuild is what a player-facing failure wants.</summary>
    private static readonly bool _armed = Renderer.VanillaBridge.Boot;

    /// <summary>The content gate alone (a Restart knob). <c>alloy.hostfs=0</c> in <c>cx.cfg</c> compiles every content
    /// region from its stub while the plain splices stay, so every content pair draws the plain lit path -- the A/B for
    /// "a content stage looks wrong" against "Alloy's own shading looks wrong".</summary>
    private static readonly bool _hostArmed = Renderer.HostFs.Boot;

    /// <summary>The marker pair. Everything between them is REPLACED by the engine's copy of the named resref
    /// when there is one; what a stage writes there is a STUB -- the feature off -- never a copy of the file,
    /// which would be the second implementation this bridge deletes. The stub's job is the PROGRAM: it has to
    /// link so the rest of the stage still renders, and <c>check-shaders.py</c> validates the real file.</summary>
    private const string Begin = "// $VANILLA ", End = "// $VANILLA-END";

    /// <summary>The WHOLE-STAGE marker. Where a content region runs a stage as a material's body, this runs the
    /// stage ENTIRE: its <c>main()</c> is renamed <see cref="MainEntry"/> and the enclosing Alloy
    /// stage's own <c>main</c> calls it. For a part whose look IS its fragment shader, like the sky. Its
    /// <c>inc_standard</c> stand-in differs: a whole stage owes its own texel multiply and colour revert.</summary>
    private const string Main = "// $VANILLA-MAIN ";

    /// <summary>The name a <see cref="Main"/> region's <c>main()</c> is renamed to, and the one the enclosing
    /// stage calls. Fixed rather than per-resref because a stage hosts exactly one whole content stage -- it has
    /// one <c>main</c> to stand in for.</summary>
    internal const string MainEntry = "CxVanMain";

    /// <summary>The CONTENT marker. The region names a content pair's fragment half, then its vertex half, which Alloy
    /// runs through <see cref="ContentStage"/>'s translation in a program of the pair's own (<see cref="ContentPrograms"/>);
    /// its stub is the dispatcher that hosts nothing. Spliced here, not beside, so the stage's includes share this
    /// compilation unit's visited list with the hook region.</summary>
    internal const string ContentMark = "// $VANILLA-CONTENT ";

    /// <summary>...and its VERTEX twin, in the lit vertex stage: the region names the pair's vertex half, then its fragment
    /// half, and its stub is the vertex host that hosts nothing.</summary>
    internal const string ContentVsMark = "// $VANILLA-CONTENT-VS ";

    /// <summary>The SKIP marker. <c>// $VANILLA-SKIP a b c</c> names includes the stage's own environment
    /// stands in for, so the resolver never follows them -- per stage, where <see cref="Supplied"/> is every
    /// stage's: <c>inc_postpr</c>'s <c>inc_lighting</c>/<c>inc_fog</c> in the post stage (the forward pipeline,
    /// not a post term), <c>inc_target</c> in the lit hooks, whose stage declares the shape uniforms itself.</summary>
    private const string Skip = "// $VANILLA-SKIP ";

    /// <summary>The common prefix of all three markers -- what the region scan searches for, so a plain and
    /// a host region are found in document order by one search.</summary>
    private const string Marker = "// $VANILLA";

    /// <summary>Replace every marked region in <paramref name="src"/> with the stock text of the resref the
    /// BEGIN marker names. A miss leaves the stub in place; nothing here can fail the compile that a
    /// missing file would not already have failed. <paramref name="host"/> false leaves every HOST region
    /// at its stub while the plain regions still splice.</summary>
    internal static string Splice(string src, ExtenderLog? log, string what, bool fromContent = true, bool host = true)
    {
        int at = src.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0) return src;
        var sb = new StringBuilder(src.Length);
        // ONE visited list for the whole compilation unit: two regions each pulling inc_scriptable would
        // otherwise declare the block twice, which is a dead stage rather than a warning.
        var seen = new List<string>();
        int cursor = 0;
        while (at >= 0)
        {
            bool contentVs = string.CompareOrdinal(src, at, ContentVsMark, 0, ContentVsMark.Length) == 0;
            bool content = contentVs || string.CompareOrdinal(src, at, ContentMark, 0, ContentMark.Length) == 0;
            bool whole = !content && string.CompareOrdinal(src, at, Main, 0, Main.Length) == 0;
            if (!content && !whole && string.CompareOrdinal(src, at, Begin, 0, Begin.Length) != 0)
            {
                if (string.CompareOrdinal(src, at, Skip, 0, Skip.Length) == 0)
                {
                    int skipEol = src.IndexOf('\n', at);
                    foreach (var n in src[(at + Skip.Length)..(skipEol < 0 ? src.Length : skipEol)].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        seen.Add(n);
                }
                at = src.IndexOf(Marker, at + Marker.Length, StringComparison.Ordinal);
                continue;
            }
            int nameAt = at + (contentVs ? ContentVsMark.Length : content ? ContentMark.Length : whole ? Main.Length : Begin.Length);
            int eol = src.IndexOf('\n', nameAt);
            int end = src.IndexOf(End, nameAt, StringComparison.Ordinal);
            if (eol < 0 || end < 0) break;
            // a content region names its own half, then the pair's other half
            string[] names = src[nameAt..eol].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string resref = names.Length > 0 ? names[0] : "";
            string partner = names.Length > 1 ? names[1] : "";
            string stub = src[(eol + 1)..end];
            bool ask = fromContent && _armed && ((host && _hostArmed) || !content);
            sb.Append(src, cursor, eol + 1 - cursor);          // keep the marker line: it names the source
            if (seen.Contains(resref))
            {
                // an earlier region already included this file: its functions exist, and the stub would
                // define them a second time
                cursor = end;
                at = src.IndexOf(Marker, end + End.Length, StringComparison.Ordinal);
                continue;
            }
            string? stock = ask ? EngineText(resref, log) : null;
            string? body = stock == null ? null
                         : content ? CX.Alloy.ContentStage.Region(!contentVs, contentVs ? resref : partner,
                                                                  contentVs ? partner : resref, log, what, seen, src[..at])
                         : whole ? MainText(stock, resref, log, what, seen, src[..at])
                         : Validate(Includes(stock, log, what, seen, 0, AlloyRenderer.VanillaStandardFs), stub, resref, log, what);
            if (body != null && !content) body = GlDepth(body);
            if (body != null)
            {
                seen.Add(resref);
                sb.Append(body);
                log?.Info($"{what}: {resref}.shd " + (content ? "translated as a content stage" : whole ? "run whole" : "spliced")
                        + $" from the resource chain ({stock!.Length} chars)");
            }
            else
            {
                sb.Append(stub);                                // the stub, unchanged
                if (fromContent && stock == null)
                    log?.Info($"{what}: {resref}.shd " + (!_armed ? "not asked for (alloy.vanillabridge=0)" : ask ? "not in the resource chain" : !_hostArmed ? "not translated (alloy.hostfs=0)" : "not translated on this retry")
                            + " -- compiling the fallback stub, so whatever it carried is off");
            }
            cursor = end;
            at = src.IndexOf(Marker, end + End.Length, StringComparison.Ordinal);
        }
        sb.Append(src, cursor, src.Length - cursor);
        return sb.ToString();
    }

    /// <summary>The SPLICED text is validated, not just the embedded copy. Every function the stub defines is
    /// one the stage calls, so a chain copy that lacks any of them -- a stale HAK whose fork predates a
    /// function the stage has started calling -- is caught here by name and falls back to the stub for THIS
    /// region, instead of failing the whole program and taking every other region with it.</summary>
    private static string? Validate(string text, string stub, string resref, ExtenderLog? log, string what)
    {
        foreach (Match m in StubFn.Matches(stub))
        {
            string name = m.Groups[1].Value;
            if (Regex.IsMatch(text, @"\b" + name + @"\s*\(")) continue;
            log?.Info($"{what}: {resref}.shd from the resource chain does not define {name} -- a stale copy? "
                    + "compiling the stub for this region");
            return null;
        }
        return text;
    }

    /// <summary>A function DEFINITION in stub text (name, then its parameter list, then the opening brace on
    /// the same line) -- anchored on the brace so a line-initial <c>return Helper(x);</c> in a stub body
    /// never becomes a name the content file is required to define.</summary>
    private static readonly Regex FbDepthRead = new(@"\b(texture2D|texture|textureLod|texelFetch)\s*\(\s*texFBDepth\s*,", RegexOptions.Compiled);
    private static readonly Regex FragZ = new(@"\bgl_FragCoord\s*\.\s*z\b", RegexOptions.Compiled);

    /// <summary>Content text <paramref name="text"/> reading GL's window depth, as the engine gives it: the world's depth is
    /// reversed, so each <c>texFBDepth</c> read goes through <c>cx_envblk</c>'s <c>CxGlDepth</c> family and a
    /// fragment's own <c>gl_FragCoord.z</c> reads as one minus itself.</summary>
    internal static string GlDepth(string text)
    {
        text = FbDepthRead.Replace(text, m => m.Groups[1].Value switch
        {
            "textureLod" => "CxGlDepthLod(texFBDepth,",
            "texelFetch" => "CxGlDepthFetch(texFBDepth,",
            _ => "CxGlDepth(texFBDepth,",
        });
        return FragZ.Replace(text, "(1.0 - gl_FragCoord.z)");
    }

    private static readonly Regex StubFn = new(@"(?m)^\s*(?:\w+\s+)+(\w+)\s*\([^)]*\)\s*\{", RegexOptions.Compiled);
    private static readonly Regex MainFn = new(@"\bvoid\s+main\s*\(\s*(?:void)?\s*\)", RegexOptions.Compiled);
    internal static readonly Regex OwnDefine = new(@"(?m)^[ \t]*#[ \t]*define[ \t]+(\w+)", RegexOptions.Compiled);

    /// <summary>The engine's PLT scheme feed, which it hands a custom shader as <c>uniform float
    /// PLTscheme[15]</c> -- one normalised master-palette row per layer, set per MATERIAL BIND. Alloy draws a
    /// multidraw batch, so there is no per-draw uniform to set: the same numbers come out of the per-record
    /// row block as a function, so the declaration goes and every index becomes that call.</summary>
    internal static readonly Regex PltSchemeDecl =
        new(@"(?m)^[ \t]*uniform\b[^;\n]*\bPLTscheme\b[^;\n]*;[^\n]*\n?", RegexOptions.Compiled);

    /// <summary>The declaration strip for one environment: every <c>uniform</c> line naming a macro
    /// <paramref name="defs"/> supplies, initialiser and all (legal GLSL, and what a stage written for both
    /// clients states so its stock half has a default). Built per environment, because a whole-stage region
    /// sits in a stage of its own whose macros are not the lit stage's.</summary>
    private static Regex DeclStrip(string defs)
    {
        string names = string.Join("|", OwnDefine.Matches(defs).Select(m => m.Groups[1].Value).Distinct());
        // An empty alternation matches the empty string, i.e. every line -- so a defs block with no macros
        // strips nothing rather than everything.
        // The declared NAME is the identifier before the `=`, which is why the part ahead of it may not
        // cross one: a macro named in an INITIALISER would otherwise strip some other declaration.
        return names.Length == 0 ? new Regex(@"(?!)", RegexOptions.Compiled)
             : new Regex(@"(?m)^[ \t]*uniform\b[^;=\n]*\b(?:" + names + @")[ \t]*(?:=[^;\n]*)?;[^\n]*\n?",
                         RegexOptions.Compiled);
    }

    private static readonly Dictionary<string, Regex> _strips = new();
    private static Regex StripFor(string defs)
    {
        lock (_strips)
        {
            if (!_strips.TryGetValue(defs, out Regex? r)) _strips[defs] = r = DeclStrip(defs);
            return r;
        }
    }

    /// <summary>A content fragment stage rewritten to run WHOLE as a function of an Alloy stage -- the
    /// <see cref="Main"/> marker's body: the includes resolved, <c>main</c> renamed, <c>gl_FragColor</c> redirected.
    /// <paramref name="env"/> is the enclosing stage's text AHEAD of the region, so the declaration strip
    /// drops what THAT stage supplies as a macro; nothing is re-issued afterwards.</summary>
    internal static string? MainText(string stock, string resref, ExtenderLog? log, string what,
                                     List<string> seen, string env)
    {
        string text = Includes(stock, log, what, seen, 0, AlloyRenderer.VanillaWholeFs);
        if (!MainFn.IsMatch(text))
        {
            log?.Info($"{what}: {resref}.shd from the resource chain has no main() to run -- compiling the stub for this region");
            return null;
        }
        text = MainFn.Replace(text, "void " + MainEntry + "()", 1);
        text = Regex.Replace(text, @"\bgl_FragColor\b", "FragmentColor");
        text = StripFor(env).Replace(text, "");
        var own = OwnDefine.Matches(stock).Select(m => m.Groups[1].Value).Distinct().ToList();
        var sb = new StringBuilder(text.Length + 512);
        foreach (var n in own) sb.Append("#undef ").Append(n).Append('\n');
        sb.Append(text).Append('\n');
        foreach (var n in own) sb.Append("#undef ").Append(n).Append('\n');
        return sb.ToString();
    }

    /// <summary>The engine's own text for a stock <c>.shd</c>, read through the engine's own entry points.
    /// Not <see cref="ShaderResource.Override"/>: that makes a bare <c>CRes</c> where none exists, and the
    /// engine reads a <c>.shd</c> back as a typed <c>CResSHD</c>, storing a back-pointer 8 bytes before the
    /// data -- a bare one kills the client. ponytail: the <c>Resources</c> trio must run back to back.</summary>
    private static string? EngineText(string resref, ExtenderLog? log)
    {
        AurResGetFn get = EngineFn.AurResGet;
        AurResGetDataFn data = EngineFn.AurResGetData;
        AurResFreeFn free = EngineFn.AurResFree;
        if (get == null || data == null || free == null)
        {
            log?.Info("VanillaShader: AurResGet/AurResGetData/AurResFree unresolved -- no text to splice");
            return null;
        }
        byte* name = stackalloc byte[ShaderResource.ResRefBytes + 1];
        if (resref.Length > ShaderResource.ResRefBytes) return null;
        for (int i = 0; i < resref.Length; i++) name[i] = (byte)resref[i];
        name[resref.Length] = 0;
        byte* ext = stackalloc byte[5];
        ext[0] = (byte)'.'; ext[1] = (byte)'s'; ext[2] = (byte)'h'; ext[3] = (byte)'d'; ext[4] = 0;

        int size = 0;
        IntPtr handle = get(name, ext, &size, 1);
        if (handle == IntPtr.Zero || size <= 0) return null;
        byte* bytes = data(size);
        string? text = bytes != null ? Encoding.UTF8.GetString(bytes, size) : null;
        free(handle);
        return text;
    }

    /// <summary>The resource chain's text for <paramref name="resref"/><c>.shd</c>, or null -- also when the bridge is
    /// disarmed, which then asks the engine for nothing. The reader a content stage's translation resolves through.</summary>
    internal static string? ChainText(string resref) => _armed && resref.Length <= ShaderResource.ResRefBytes ? EngineText(resref, null) : null;

    /// <summary>Can the resource chain answer for this resref, and is the bridge armed to ask? A
    /// caller whose fallback stub is not a usable picture asks BEFORE building, and refuses instead of
    /// linking a program that draws the stub -- the sky, whose stub is a black dome. Cold, and it reads the
    /// file a second time at the splice; a program builds once per module.</summary>
    internal static bool InChain(string resref) => _armed && EngineText(resref, null) != null;

    /// <summary>Does this source carry a marked region at all? The question a failed compile asks before it
    /// decides whether there is a stub to retry with.</summary>
    internal static bool HasSplice(string src) => src.Contains(Begin, StringComparison.Ordinal)
                                               || src.Contains(Main, StringComparison.Ordinal)
                                               || src.Contains(ContentMark, StringComparison.Ordinal)
                                               || src.Contains(ContentVsMark, StringComparison.Ordinal);

    /// <summary>Resolve the file's own <c>#include</c> lines against the resource chain, once each. An include
    /// is followed when the chain answers for it and dropped when it does not: a CONTENT file exists to compose
    /// its own includes, while an environment-supplied one is never in the chain. The visited list stops a
    /// cycle; <see cref="MaxDepth"/> (the content stages' own nesting limit) drops a deeper include out loud.</summary>
    private const int MaxDepth = 8, MaxIncludes = 64;

    /// <summary>Includes the ENVIRONMENT supplies, and which must therefore never be followed.
    /// <c>inc_common</c>/<c>inc_uniforms</c> are the engine's whole uniform and varying interface, so pulling
    /// them into an Alloy stage would redeclare hundreds of names against a different vertex layout. A stage that
    /// declares more of it skips that per stage (the lit hooks skip <c>inc_target</c>). Everything else is followed.</summary>
    private static readonly string[] Supplied = { "inc_common", "inc_uniforms" };

    /// <summary>The one include the environment SUBSTITUTES rather than drops. A content stage's
    /// <c>inc_standard</c> is the engine's whole forward pipeline; what a hosted stage needs from it is the
    /// two entry points it calls around its own body, as no-ops, plus the scriptable block. WHICH stand-in is
    /// the caller's: a stage run whole owes its own texel multiply and colour revert, a hosted body does not.</summary>
    private const string Substituted = "inc_standard";

    /// <summary>A content stage's whole text with every include it names resolved from the resource chain --
    /// nothing supplied, nothing substituted -- for a program built the way the engine builds it. Null when the
    /// bridge is off or the chain has no such file.</summary>
    internal static string? ContentStage(string resref, ExtenderLog? log, string what)
    {
        if (!_armed) return null;
        string? text = EngineText(resref, log);
        return text == null ? null : Includes(text, log, what, new List<string>(), 0, null);
    }

    private static string Includes(string text, ExtenderLog? log, string what, List<string> seen, int depth,
                                  string? standard)
    {
        if (!text.Contains("#include", StringComparison.Ordinal)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var line in text.Split('\n'))
        {
            string t = line.TrimStart();
            if (!t.StartsWith("#include", StringComparison.Ordinal)) { sb.Append(line).Append('\n'); continue; }
            int a = t.IndexOf('"'), b = a < 0 ? -1 : t.IndexOf('"', a + 1);
            string? name = a >= 0 && b > a ? t[(a + 1)..b] : null;
            if (name == null || seen.Contains(name)) continue;
            if (seen.Count >= MaxIncludes) { log?.Info($"{what}: include cap {MaxIncludes} reached -- {name} dropped"); continue; }
            if (depth >= MaxDepth) { log?.Info($"{what}: {name} is included {MaxDepth} deep -- dropped"); continue; }
            if (standard != null && Array.IndexOf(Supplied, name) >= 0) continue;
            seen.Add(name);
            string? body = standard != null && name == Substituted ? standard
                         : name.Length <= ShaderResource.ResRefBytes ? EngineText(name, null) : null;
            if (body == null) continue;                       // the environment supplies it, or nothing does
            sb.Append(Includes(body, log, what, seen, depth + 1, standard)).Append('\n');
        }
        return sb.ToString();
    }

}
