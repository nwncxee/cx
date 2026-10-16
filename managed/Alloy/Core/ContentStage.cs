using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CX.Alloy;

/// <summary>
/// A CONTENT fragment stage -- a <c>customShaderFS</c> Alloy neither reproduces nor hosts by name -- rewritten to
/// run as the surface body of a lit program of its own (<see cref="ContentPrograms"/>). Pure text-to-text over a
/// resource-chain reader, so the self-check drives it on hand-written stages; <c>cx_content_f.glsl</c> is the
/// environment the result compiles in.
/// </summary>
internal static partial class ContentStage
{
    /// <summary>How the engine's upload feeds a content uniform from a <c>.mtr</c> <c>parameter</c> line.</summary>
    internal enum ParamKind : byte
    {
        /// <summary>A <c>float</c>: a one-value <c>parameter float</c> line (<c>glUniform1f</c>).</summary>
        Float,
        /// <summary>An <c>int</c>: a <c>parameter int</c> line (<c>glUniform1i</c>).</summary>
        Int,
        /// <summary>A <c>bool</c>: either of the two scalar uploads.</summary>
        Bool,
        /// <summary>A <c>vec4</c>: a two-to-four-value <c>parameter float</c> line (<c>glUniform4fv</c>).</summary>
        Vec4,
    }

    /// <summary>One content uniform the side table feeds: its name as the shader spells it, how the engine would
    /// upload into it, and its lane in the row.</summary>
    internal readonly record struct Param(string Name, ParamKind Kind, int Lane);

    /// <summary>Lanes a stage's parameters may take in its side-table row, which is
    /// <see cref="Db.TintTerms.ContentLanes"/>. The whole corpus measured uses five at most.</summary>
    internal const int MaxLanes = Db.TintTerms.ContentLanes;

    /// <summary>What a translation produced. <see cref="Refused"/> non-null means the stage cannot run under Alloy
    /// and says why; everything else is then empty.</summary>
    internal sealed class Result
    {
        public string Host = "";
        public Param[] Params = [];
        /// <summary>Does the stage discard or write its own coverage? Such a part's depth is the stage's, not the
        /// cutout test the depth prepass makes, so it stays out of the prepass.</summary>
        public bool Cuts;
        /// <summary>Does it read the engine's PLT scheme feed? Its records then carry the wearer's palette rows.</summary>
        public bool ReadsPlt;
        /// <summary>What the translation could only approximate, one clause each, for the log.</summary>
        public readonly List<string> Notes = new();
        public string? Refused;
    }

    /// <summary>Includes the content environment stands in for and the resolver therefore never follows: the
    /// engine's interface (<c>inc_common</c>, <c>inc_uniforms</c>) and every piece of the forward pipeline
    /// <c>inc_standard</c> pulls in, which Alloy runs itself after the body.</summary>
    private static readonly string[] Dropped =
    [
        "inc_common", "inc_uniforms", "inc_target", "inc_lighting", "inc_material", "inc_keyhole", "inc_fog",
        "inc_transform", "inc_envmap", "inc_random", "inc_tonemap", "inc_debug", "inc_displacement", "inc_framebuffer",
    ];

    /// <summary>Include nesting and count caps. The engine has neither, and no cycle guard; these turn a cycle into
    /// a refusal instead of a hang.</summary>
    private const int MaxDepth = 8, MaxIncludes = 64;

    /// <summary>The engine's 16-character resref field, which its include scanner caps the name at.</summary>
    private const int ResRefChars = 16;

    /// <summary>The <c>fs_plt_tinter</c> placeholder names <c>cx_vanenv_defs</c> maps onto the palette tint's own
    /// table. A content stage may declare uniforms by those names for its own use, so the region drops the four
    /// macros and the re-issued environment restores them.</summary>
    private static readonly string[] TintPlaceholders = ["fAnisotropy", "fSpecularIntensity", "fIllumColor", "fAlphaMipScale"];

    /// <summary>Translate content stage <paramref name="resref"/>, whose text and includes <paramref name="chain"/>
    /// reads off the resource chain. <paramref name="env"/> is the content environment's text,
    /// <paramref name="vanillaDefs"/> the lit stage's own vanilla macros, and <paramref name="ahead"/> the lit stage's
    /// text ahead of the region, whose file-scope names the stage's own declarations must not redeclare.</summary>
    internal static Result Translate(string resref, Func<string, string?> chain, string env, string vanillaDefs,
                                     string ahead, int buildVersion, int buildRevision)
    {
        var r = new Result();
        string? stage = chain(resref);
        if (stage == null) { r.Refused = $"{resref}.shd is not in the resource chain"; return r; }
        int count = 0;
        string? text = Resolve(stage, chain, AlloyRenderer.ContentStandardFs, new List<string> { resref.ToLowerInvariant() },
                               0, ref count, out string? missing);
        if (text == null) { r.Refused = missing; return r; }
        text = Directive.Replace(text, "");
        if (!MainFn.IsMatch(text)) { r.Refused = $"{resref}.shd has no void main() to run"; return r; }
        // the stage's OWN text, the stand-in's cut left out: what decides whether its coverage is the texel test
        count = 0;
        string flat = Comments.Replace(Resolve(stage, chain, "", new List<string> { resref.ToLowerInvariant() },
                                               0, ref count, out _) ?? text, " ");
        r.ReadsPlt = Regex.IsMatch(flat, @"\bPLTscheme\b");
        r.Cuts = Regex.IsMatch(flat, @"\bdiscard\b|\bAlphaDiscard\s*\(") || WritesAlpha(flat) || OwnCoverage.IsMatch(flat);
        if (!Regex.IsMatch(flat, @"\bSetupStandardShaderInputs\s*\("))
            r.Notes.Add("never runs SetupStandardShaderInputs, so what it writes to FragmentColor is shaded as the surface colour");
        if (EditsAfterApply(flat))
            r.Notes.Add("edits its colour after ApplyStandardShader, which under Alloy lands on the surface colour before the lighting");

        var supplied = Supplied(env, vanillaDefs, ahead);
        text = MainFn.Replace(text, "void CxContentMain()");
        text = Regex.Replace(text, @"\bgl_FragColor\b", "FragmentColor");
        text = VanillaShader.PltSchemeDecl.Replace(text, "");
        text = VanillaShader.PltSchemeUse.Replace(text, "CxPltScheme(vDraw, $1)");
        var paramList = new List<Param>();
        var macros = new List<string>();
        text = Declarations(text, supplied, paramList, macros, r);
        if (r.Refused != null) return r;
        r.Params = paramList.ToArray();

        var guards = Guard.Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var own = VanillaShader.OwnDefine.Matches(text).Select(m => m.Groups[1].Value)
                               .Where(n => !guards.Contains(n)).Distinct().ToList();
        var fns = FnDef.Matches(Comments.Replace(text, " ")).Select(m => m.Groups[1].Value).Distinct().ToList();
        var envDefs = VanillaShader.OwnDefine.Matches(env).Select(m => m.Groups[1].Value).Distinct().ToList();
        var sb = new StringBuilder(text.Length + env.Length + 2048);
        sb.Append("// ---- content stage ").Append(resref).Append('\n');
        foreach (string p in TintPlaceholders) sb.Append("#undef ").Append(p).Append('\n');
        sb.Append("#define BUILD_VERSION ").Append(buildVersion).Append('\n');
        sb.Append("#define BUILD_REVISION ").Append(buildRevision).Append('\n');
        foreach (string f in fns) sb.Append("#define CX_CON_HAS_").Append(f).Append('\n');
        sb.Append(env);
        if (!env.EndsWith('\n')) sb.Append('\n');
        foreach (string n in own) sb.Append("#undef ").Append(n).Append('\n');
        sb.Append(text).Append('\n');
        sb.Append("void CxContentBody()\n{\n")
          .Append("    cxConUV = CxHostUV;\n")
          .Append("    cxConRow = CxContentRow(vDraw);\n")
          .Append("    fEnvMapLevel = 0.0;\n")
          .Append("    fOcclusion = 1.0;\n")
          .Append("    CxContentMain();\n")
          .Append("    fSpecularity = MATERIAL_SPECULARITY_VALUE;\n")
          .Append("    fRoughness = MATERIAL_ROUGHNESS_VALUE;\n")
          .Append("    fMetallicness = MATERIAL_METALLICNESS_VALUE;\n")
          .Append("    SpecularColor = MATERIAL_SPECULAR_COLOR_VALUE;\n}\n");
        foreach (string n in own) sb.Append("#undef ").Append(n).Append('\n');
        foreach (string n in envDefs.Concat(macros).Concat(fns.Select(f => "CX_CON_HAS_" + f)))
            sb.Append("#undef ").Append(n).Append('\n');
        sb.Append("#undef BUILD_VERSION\n#undef BUILD_REVISION\n");
        sb.Append(vanillaDefs);
        if (!vanillaDefs.EndsWith('\n')) sb.Append('\n');
        sb.Append("#define CX_CONTENT_PROGRAM 1\n");
        sb.Append("bool CxHostSurface(int feat)\n{\n")
          .Append("    if (feat < CX_FEAT_CONTENT0) return false;\n")
          .Append("    CxContentBody();\n")
          .Append("    return true;\n}\n");
        r.Host = sb.ToString();
        return r;
    }

    /// <summary>The environment library's resref, bracketed in the region so a hak's <c>cx_content_f.shd</c>
    /// replaces it like any other Alloy library.</summary>
    internal const string EnvResref = "cx_content_f";

    /// <summary>The last translation of each stage this module, by resref: what its program's lanes, prepass
    /// routing and log line come from. One owner, replaced whole on a module change.</summary>
    private sealed class RegionState
    {
        public readonly Dictionary<string, Result> Last = new(StringComparer.OrdinalIgnoreCase);
    }

    private static RegionState _regions = new();

    /// <summary>The <c>$VANILLA-CONTENT</c> region's body for <paramref name="resref"/>, from inside
    /// <see cref="VanillaShader.Splice"/>: an include an earlier region of the same program spliced resolves to
    /// nothing, as the visited list resolves it for the hosted stages. Null when the stage is refused (the stub
    /// stays). <paramref name="ahead"/> is the program's text before the region.</summary>
    internal static string? Region(string resref, CX.Framework.ExtenderLog? log, string what, List<string> seen, string ahead)
    {
        (int v, int rev) = Build();
        string env = ShaderSource.Content(ShaderSource.LibMark + EnvResref + "\n" + AlloyRenderer.ContentEnvFs
                                          + ShaderSource.EndMark + EnvResref + "\n", log);
        Result r = Translate(resref, n => seen.Exists(s => s.Equals(n, StringComparison.OrdinalIgnoreCase)) ? "" : VanillaShader.ChainText(n), env,
                             AlloyRenderer.VanillaEnvDefs, ahead, v, rev);
        _regions.Last[resref] = r;
        if (r.Refused != null)
        {
            log?.Info($"{what}: {resref}.shd cannot run under Alloy -- {r.Refused}");
            return null;
        }
        return r.Host;
    }

    /// <summary>The last translation of <paramref name="resref"/> this module, or null before its first build.</summary>
    internal static Result? Last(string resref) => _regions.Last.TryGetValue(resref, out Result? r) ? r : null;

    /// <summary>A module change: the next build reads the new module's files.</summary>
    internal static void Forget() => _regions = new RegionState();

    /// <summary>The running client's <c>BUILD_VERSION</c> and <c>BUILD_REVISION</c> as the engine prologue states them,
    /// off the build file's name (<c>8193.37-17</c>); the build CX is pinned to.</summary>
    private static (int Version, int Revision) Build()
    {
        string name = CX.Framework.ClientBuilds.Current?.Name ?? "";
        Match m = BuildName.Match(name);
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : (0, 0);
    }

    private static readonly Regex BuildName = new(@"^(\d+)\.(\d+)", RegexOptions.Compiled);

    /// <summary>The stage with its includes resolved the way the engine resolves them -- every <c>#include</c>
    /// followed, no dedupe (the files' own guards do that), the dropped pipeline skipped and <c>inc_standard</c>
    /// replaced by the content stand-in. Null with <paramref name="missing"/> set when an include does not resolve,
    /// which fails the stage in the engine too; inside the stand-in, whose includes are Alloy's, a miss is a skip.</summary>
    private static string? Resolve(string text, Func<string, string?> chain, string standard, List<string> stack, int depth,
                                   ref int count, out string? missing, bool optional = false)
    {
        missing = null;
        if (!text.Contains("#include", StringComparison.Ordinal)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (string line in text.Split('\n'))
        {
            Match m = Include.Match(line);
            if (!m.Success) { sb.Append(line).Append('\n'); continue; }
            string name = m.Groups[1].Value;
            if (name.Length > ResRefChars) name = name[..ResRefChars];
            string key = name.ToLowerInvariant();
            if (Array.IndexOf(Dropped, key) >= 0) continue;
            string? body = key == "inc_standard" ? standard : chain(name);
            if (body == null && optional) continue;
            if (body == null) { missing = $"includes \"{name}\", which nothing in the resource chain supplies"; return null; }
            if (depth + 1 >= MaxDepth || stack.Contains(key) || ++count > MaxIncludes)
            { missing = $"its includes nest past {MaxDepth} deep, repeat, or number over {MaxIncludes} (\"{name}\")"; return null; }
            stack.Add(key);
            string? inner = Resolve(body, chain, standard, stack, depth + 1, ref count, out missing,
                                    optional || key == "inc_standard");
            stack.RemoveAt(stack.Count - 1);
            if (inner == null) return null;
            sb.Append(inner).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Every name the region already has a meaning for: the macros of both environments (less the tint
    /// placeholders and the helpers' guards) and the globals they declare. A stage's own declaration of one is
    /// dropped, because it would redeclare what the environment maps it to.</summary>
    private static HashSet<string> Supplied(string env, string vanillaDefs, string ahead)
    {
        var s = new HashSet<string>(StringComparer.Ordinal);
        foreach (string t in new[] { env, vanillaDefs, AlloyRenderer.VanillaEnvFs, ahead })
        {
            foreach (Match m in VanillaShader.OwnDefine.Matches(t)) s.Add(m.Groups[1].Value);
            foreach (Match m in EnvDecl.Matches(Comments.Replace(t, " ")))
                foreach ((string name, _, _) in Items(m.Groups[2].Value)) s.Add(name);
        }
        foreach (string p in TintPlaceholders) s.Remove(p);
        s.RemoveWhere(n => n.StartsWith("CX_CON_HAS_", StringComparison.Ordinal));
        return s;
    }

    /// <summary>Rewrite every file-scope <c>uniform</c>, <c>in</c>/<c>varying</c> and <c>out</c> declaration, and
    /// every file-scope global that names a supplied one. A brace counter over comment-stripped lines keeps the
    /// scan out of function bodies, whose <c>in</c> parameters are not declarations.</summary>
    private static string Declarations(string text, HashSet<string> supplied, List<Param> ps, List<string> macros, Result r)
    {
        var sb = new StringBuilder(text.Length + 1024);
        var zeroed = new List<string>();
        int depth = 0;
        bool block = false;
        foreach (string line in text.Split('\n'))
        {
            string code = StripLine(line, ref block);
            if (depth == 0 && !code.TrimStart().StartsWith('#'))
            {
                string? rewritten = Rewrite(code, supplied, ps, macros, zeroed, r);
                if (r.Refused != null) return text;
                if (rewritten != null) { sb.Append(rewritten).Append('\n'); depth += Braces(code); continue; }
            }
            sb.Append(line).Append('\n');
            depth += Braces(code);
            if (depth < 0) depth = 0;
        }
        if (zeroed.Count > 0)
            r.Notes.Add($"reads {string.Join(", ", zeroed)}, which its vertex stage writes and Alloy runs none of -- held at zero");
        return sb.ToString();
    }

    /// <summary>One file-scope line: its replacement, or null to keep it as written.</summary>
    private static string? Rewrite(string code, HashSet<string> supplied, List<Param> ps, List<string> macros,
                                   List<string> zeroed, Result r)
    {
        Match m;
        if ((m = UniformDecl.Match(code)).Success)
        {
            var sb = new StringBuilder();
            foreach ((string name, string? arr, string? init) in Items(m.Groups[2].Value))
                sb.Append(Uniform(m.Groups[1].Value, name, arr, init, supplied, ps, macros, r));
            return sb.ToString();
        }
        if ((m = InputDecl.Match(code)).Success)
        {
            var sb = new StringBuilder();
            string type = m.Groups[1].Value;
            foreach ((string name, string? arr, _) in Items(m.Groups[2].Value))
            {
                if (supplied.Contains(name)) continue;
                zeroed.Add(name);
                sb.Append(arr != null ? $"{type} {name}{arr};\n" : $"{type} {name} = {type}(0);\n");
            }
            return sb.ToString();
        }
        if ((m = OutputDecl.Match(code)).Success)
        {
            string name = m.Groups[2].Value;
            if (m.Groups[1].Value != "vec4") { r.Refused = $"declares a {m.Groups[1].Value} fragment output ({name})"; return null; }
            macros.Add(name);
            return $"#define {name} FragmentColor";
        }
        if ((m = GlobalDecl.Match(code)).Success && Items(m.Groups[2].Value).Any(d => supplied.Contains(d.Name))) return "";
        return null;
    }

    /// <summary>One content uniform. A name the environment supplies goes; a scalar or <c>vec4</c> the engine's
    /// upload can feed takes a lane (the declared initialiser stays the default for a material that states
    /// nothing); a sampler is unit 0 or the probe, as an unset engine sampler is; anything else is its
    /// initialiser, because the engine has no upload that fits it.</summary>
    private static string Uniform(string type, string name, string? arr, string? init, HashSet<string> supplied,
                                  List<Param> ps, List<string> macros, Result r)
    {
        if (supplied.Contains(name)) return "";
        if (FedByName.IsMatch(name)) return $"uniform {type} {name}{arr}" + (init != null ? $" = {init}" : "") + ";\n";
        if (arr != null) { r.Notes.Add($"declares the uniform array {name}, which no .mtr can feed -- held at zero"); return $"{type} {name}{arr};\n"; }
        if (type.StartsWith("sampler", StringComparison.Ordinal))
        {
            string to = type == "sampler2D" ? "uTex" : type == "samplerCube" ? "uProbe" : "";
            if (to.Length == 0) { r.Refused = $"declares a {type} ({name}), which no material unit carries"; return ""; }
            macros.Add(name);
            return $"#define {name} {to}\n";
        }
        ParamKind? kind = type switch
        {
            "float" => ParamKind.Float, "int" => ParamKind.Int, "bool" => ParamKind.Bool, "vec4" => ParamKind.Vec4,
            _ => null,
        };
        string fallback = init != null ? $"({init})" : $"{type}(0)";
        macros.Add(name);
        if (kind == null)
        {
            r.Notes.Add($"declares the {type} {name}, which the engine's .mtr upload (1 or 4 components) never fits -- held at its default");
            return $"#define {name} {fallback}\n";
        }
        if (ps.Count >= MaxLanes)
        {
            r.Notes.Add($"declares more than {MaxLanes} parameters -- {name} is held at its default");
            return $"#define {name} {fallback}\n";
        }
        int lane = ps.Count;
        ps.Add(new Param(name, kind.Value, lane));
        string value = kind switch
        {
            ParamKind.Float => $"CxCp({lane}).x",
            ParamKind.Int => $"int(CxCp({lane}).x)",
            ParamKind.Bool => $"(CxCp({lane}).x != 0.0)",
            _ => $"CxCp({lane})",
        };
        return $"#define {name} (CxCpOn({lane}) ? {value} : {fallback})\n";
    }

    /// <summary>The comma-separated declarators of one declaration: name, array suffix and initialiser. The split
    /// is at commas outside parentheses, so <c>vec4(1, 2, 3, 4)</c> stays whole.</summary>
    private static IEnumerable<(string Name, string? Arr, string? Init)> Items(string list)
    {
        int depth = 0, start = 0;
        var parts = new List<string>();
        for (int i = 0; i < list.Length; i++)
        {
            char c = list[i];
            if (c == '(' || c == '[') depth++;
            else if (c == ')' || c == ']') depth--;
            else if (c == ',' && depth == 0) { parts.Add(list[start..i]); start = i + 1; }
        }
        parts.Add(list[start..]);
        foreach (string p in parts)
        {
            Match m = Declarator.Match(p);
            if (m.Success)
                yield return (m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null,
                              m.Groups[3].Success ? m.Groups[3].Value.Trim() : null);
        }
    }

    /// <summary>Does the stage write the alpha of its colour after the stock opener? A full <c>vec4(...)</c>
    /// assignment other than <c>vec4(1.0)</c> or one that hands the alpha back unchanged, or any write to a swizzle
    /// carrying <c>a</c>/<c>w</c>.</summary>
    private static bool WritesAlpha(string flat)
    {
        foreach (Match m in AlphaWrite.Matches(flat))
            if (!OpenerRhs.IsMatch(flat, m.Index + m.Length) && !KeepsAlpha(flat, m.Index + m.Length)) return true;
        return Regex.IsMatch(flat, @"\b(?:FragmentColor|gl_FragColor)\s*\.\s*[rgbaxyzw]*[aw][rgbaxyzw]*\s*[-+*/]?=(?!=)");
    }

    /// <summary>Is the <c>vec4(</c> constructor opening at <paramref name="at"/> one whose last argument is the colour's
    /// own alpha (<c>vec4(rgb, FragmentColor.a)</c>) or a full one? Neither can open a hole the setup's cut did not.</summary>
    private static bool KeepsAlpha(string flat, int at)
    {
        int depth = 1, lastComma = -1, i = at;
        for (; i < flat.Length && depth > 0; i++)
        {
            char c = flat[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 1) lastComma = i;
        }
        return lastComma >= 0 && OwnAlpha.IsMatch(flat[(lastComma + 1)..(i - 1)]);
    }

    private static readonly Regex OwnAlpha = new(@"^\s*(?:(?:FragmentColor|gl_FragColor)\s*\.\s*[aw]|1(?:\.0*)?)\s*$", RegexOptions.Compiled);

    /// <summary>Does <c>main</c> touch the colour after its <c>ApplyStandardShader()</c> call, beyond the stock
    /// hand-off to <c>gl_FragColor</c>?</summary>
    private static bool EditsAfterApply(string flat)
    {
        Match main = MainFn.Match(flat);
        if (!main.Success) return false;
        int apply = flat.IndexOf("ApplyStandardShader", main.Index, StringComparison.Ordinal);
        if (apply < 0) return false;
        string tail = flat[apply..];
        tail = Regex.Replace(tail, @"\bgl_FragColor\s*=\s*FragmentColor\s*;", "");
        return Regex.IsMatch(tail, @"\b(?:FragmentColor|gl_FragColor)\b(?:\s*\.\s*\w+)?\s*[-+*/]?=(?!=)");
    }

    /// <summary>A line with its comments blanked, carrying the block-comment state across lines.</summary>
    private static string StripLine(string line, ref bool block)
    {
        var sb = new StringBuilder(line.Length);
        for (int i = 0; i < line.Length; i++)
        {
            if (block)
            {
                if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '/') { block = false; i++; }
                continue;
            }
            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*') { block = true; i++; continue; }
            sb.Append(line[i]);
        }
        return sb.ToString();
    }

    private static int Braces(string code)
    {
        int d = 0;
        foreach (char c in code) d += c == '{' ? 1 : c == '}' ? -1 : 0;
        return d;
    }

    private static readonly Regex MainFn = new(@"\bvoid\s+main\s*\(\s*(?:void)?\s*\)", RegexOptions.Compiled);
    private static readonly Regex Include = new(@"^\s*#\s*include\s+""([^""]+)""", RegexOptions.Compiled);
    /// <summary><c>#version</c> and <c>#extension</c> lines: the engine prepends its own version, and an extension
    /// directive is illegal past the first declaration, which is where the region sits.</summary>
    private static readonly Regex Directive = new(@"(?m)^[ \t]*#[ \t]*(?:version|extension)\b[^\n]*", RegexOptions.Compiled);
    private static readonly Regex Comments = new(@"//[^\n]*|/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);
    private const string Prec = @"(?:(?:lowp|mediump|highp)\s+)?";
    private const string Layout = @"(?:layout\s*\([^)]*\)\s*)?";
    private static readonly Regex UniformDecl = new(@"^\s*" + Layout + @"uniform\s+" + Prec + @"(\w+)\s+(.+?)\s*;\s*$", RegexOptions.Compiled);
    private static readonly Regex InputDecl = new(@"^\s*" + Layout + @"(?:(?:flat|smooth|noperspective|centroid)\s+)*(?:in|varying)\s+" + Prec + @"(\w+)\s+(.+?)\s*;\s*$", RegexOptions.Compiled);
    private static readonly Regex OutputDecl = new(@"^\s*" + Layout + @"out\s+" + Prec + @"(\w+)\s+(\w+)\s*;\s*$", RegexOptions.Compiled);
    private static readonly Regex Declarator = new(@"^\s*(\w+)\s*(\[[^\]]*\])?\s*(?:=\s*(.+))?$", RegexOptions.Compiled | RegexOptions.Singleline);
    private const string GlslType = @"(?:void|float|double|int|uint|bool|[iubd]?vec[234]|d?mat[234](?:x[234])?)";
    /// <summary>A file-scope variable or constant declaration (<c>[const] type a [= x], b;</c>) at line start: its
    /// type and its declarator list, which <see cref="Items"/> splits. A function prototype has a parenthesis
    /// straight after its name and so never matches.</summary>
    private static readonly Regex GlobalDecl = new(@"(?m)^[ \t]*(?:(?:const|uniform|lowp|mediump|highp)\s+)*(" + GlslType + @"|sampler\w*)\s+(\w+\s*(?:\[[^\]]*\])?\s*(?:(?:=|,)[^;\n]*)?);", RegexOptions.Compiled);
    /// <summary><see cref="GlobalDecl"/> at column 0 only: how the environments write their file-scope names, and
    /// what keeps the locals inside their helpers (indented) out of the supplied set.</summary>
    private static readonly Regex EnvDecl = new(@"(?m)^(?:(?:const|uniform|lowp|mediump|highp)\s+)*(" + GlslType + @"|sampler\w*)\s+(\w+\s*(?:\[[^\]]*\])?\s*(?:(?:=|,)[^;\n]*)?);", RegexOptions.Compiled);
    /// <summary>A function definition or prototype at line start: its name.</summary>
    private static readonly Regex FnDef = new(@"(?m)^[ \t]*(?:(?:lowp|mediump|highp|const)\s+)*" + GlslType + @"\s+(\w+)\s*\(", RegexOptions.Compiled);
    /// <summary>The engine globals a content stage declares itself and Alloy uploads by name per program bind (the
    /// scriptable block <c>inc_scriptable</c> declares): their declarations stay uniforms, never lanes.</summary>
    private static readonly Regex FedByName = new(@"^scriptable(?:Float|Int|Vec)\d+$", RegexOptions.Compiled);
    /// <summary>An include guard (<c>#ifndef X</c> then <c>#define X</c>): left defined after the region, because the
    /// lit stage's hook region may have spliced the same file first and its guard is what keeps the copy out.</summary>
    private static readonly Regex Guard = new(@"(?m)^[ \t]*#[ \t]*ifndef[ \t]+(\w+)[ \t]*\r?\n[ \t]*#[ \t]*define[ \t]+\1\b", RegexOptions.Compiled);
    /// <summary>A stage whose coverage the stock setup does not cut on the texel: it declares its diffuse alpha an
    /// env level, or has no texture at all.</summary>
    private static readonly Regex OwnCoverage = new(@"(?m)^[ \t]*#[ \t]*define[ \t]+(?:ENVIRONMENT_MAP|NO_TEXTURE)[ \t]+1\b", RegexOptions.Compiled);
    private static readonly Regex AlphaWrite = new(@"\b(?:FragmentColor|gl_FragColor)\s*=\s*vec4\s*\(", RegexOptions.Compiled);
    private static readonly Regex OpenerRhs = new(@"\G\s*1(?:\.0*)?\s*\)", RegexOptions.Compiled);
}
