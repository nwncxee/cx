using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CX.Alloy;

internal static partial class ContentStage
{
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

    /// <summary>Every name the region already has a meaning for: the macros of the environments (less the helpers'
    /// guards) and the globals they declare. A stage's own declaration of one is dropped, because it would redeclare
    /// what the environment maps it to.</summary>
    private static HashSet<string> Supplied(string env, string vanillaDefs, string ahead)
    {
        var s = new HashSet<string>(StringComparer.Ordinal);
        foreach (string t in new[] { env, vanillaDefs, vanillaDefs.Length > 0 ? AlloyRenderer.VanillaEnvFs : "", ahead })
        {
            foreach (Match m in VanillaShader.OwnDefine.Matches(t)) s.Add(m.Groups[1].Value);
            foreach (Match m in EnvDecl.Matches(Comments.Replace(t, " ")))
                foreach ((string name, _, _) in Items(m.Groups[2].Value)) s.Add(name);
        }
        s.RemoveWhere(n => n.StartsWith("CX_CON_HAS_", StringComparison.Ordinal));
        return s;
    }

    /// <summary>Rewrite every file-scope <c>uniform</c>, input and output declaration, and every file-scope global that
    /// names a supplied one. A brace counter over comment-stripped lines keeps the scan out of function bodies, whose
    /// <c>in</c> parameters are not declarations. <paramref name="vsOut"/> is a fragment half's vertex half's varyings.</summary>
    private static string Declarations(string text, bool fragment, HashSet<string> supplied, HashSet<string>? vsOut,
                                       HashSet<string> own, List<Param> ps, List<string> macros, Result r)
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
                string? rewritten = Kept(code, own) ? null : Rewrite(code, line, fragment, supplied, vsOut, ps, macros, zeroed, r);
                if (r.Refused != null) return text;
                if (rewritten != null) { sb.Append(rewritten).Append('\n'); depth += Braces(code); continue; }
            }
            sb.Append(line).Append('\n');
            depth += Braces(code);
            if (depth < 0) depth = 0;
        }
        if (zeroed.Count > 0)
            r.Notes.Add(fragment ? $"reads {string.Join(", ", zeroed)}, which its vertex half does not write -- held at zero"
                                 : $"reads the attribute(s) {string.Join(", ", zeroed)}, which no arena stream carries -- held at zero");
        return sb.ToString();
    }

    /// <summary>The uniforms a stage keeps as it wrote them, because Alloy sets them by name: the cx frame uniforms in
    /// any stage, and the images a native half (<see cref="StageResources"/>) of the pair declares on its own
    /// <c>#pragma cx_image</c> lines, with their <c>_prev</c> halves -- the two halves link into one program.</summary>
    private static HashSet<string> OwnNames(string resref, string stage, string partner, Func<string, string?> chain)
    {
        var own = new HashSet<string>(FrameNames, StringComparer.Ordinal);
        Images(own, resref, stage);
        if (partner.Length > 0) Images(own, partner, chain(partner) ?? "");
        return own;
    }

    private static void Images(HashSet<string> own, string resref, string text)
    {
        if (!resref.StartsWith(StageResources.Prefix, StringComparison.OrdinalIgnoreCase)) return;
        foreach (Match m in ImagePragma.Matches(text)) { own.Add(m.Groups[1].Value); own.Add(m.Groups[1].Value + "_prev"); }
    }

    /// <summary>The cx frame uniforms (<see cref="FrameLoc"/>), which every host sets on a program that declares one.</summary>
    private static readonly string[] FrameNames = ["cxFrame", "cxMain", "cxFresh", "cxInvVp", "cxPrevVp", "cxEye", "cxView", "cxOverlay"];
    private static readonly Regex ImagePragma = new(@"(?m)^[ \t]*#[ \t]*pragma[ \t]+cx_image[ \t]+(\w+)", RegexOptions.Compiled);

    /// <summary>Is this file-scope line a uniform declaration naming one of <paramref name="own"/>? It stays as written --
    /// its type may be an image with qualifiers the rewrite does not parse.</summary>
    private static bool Kept(string code, HashSet<string> own)
    {
        if (!UniformWord.IsMatch(code)) return false;
        foreach (Match w in Word.Matches(code)) if (own.Contains(w.Value)) return true;
        return false;
    }

    private static readonly Regex UniformWord = new(@"^\s*(?:layout\s*\([^)]*\)\s*)?uniform\b", RegexOptions.Compiled);
    private static readonly Regex Word = new(@"\w+", RegexOptions.Compiled);

    /// <summary>One file-scope line: its replacement, or null to keep it as written.</summary>
    private static string? Rewrite(string code, string line, bool fragment, HashSet<string> supplied, HashSet<string>? vsOut,
                                   List<Param> ps, List<string> macros, List<string> zeroed, Result r)
    {
        Match m;
        if ((m = UniformDecl.Match(code)).Success)
        {
            var sb = new StringBuilder();
            foreach ((string name, string? arr, string? init) in Items(m.Groups[2].Value))
                sb.Append(Uniform(m.Groups[1].Value, name, arr, init, supplied, ps, macros, r));
            return sb.ToString();
        }
        bool input = (m = InputDecl.Match(code)).Success && (fragment || m.Groups[1].Value != "varying");
        bool output = !input && (m.Success || (m = OutputDecl.Match(code)).Success);
        if (!input && !output)
            return (m = GlobalDecl.Match(code)).Success && Items(m.Groups[3].Value).Any(d => supplied.Contains(d.Name)) ? "" : null;
        string type = m.Groups[2].Value;
        var items = Items(m.Groups[3].Value).ToList();
        if (output && fragment)
        {
            if (type != "vec4" || items.Count != 1) { r.Refused = $"declares a {type} fragment output ({m.Groups[3].Value.Trim()})"; return null; }
            macros.Add(items[0].Name);
            return $"#define {items[0].Name} FragmentColor";
        }
        if (output)
        {
            // a vertex half's own varying: it reaches the fragment half by name; a stock one is a global here
            var keep = items.Where(d => !supplied.Contains(d.Name)).ToList();
            foreach (var d in keep) r.Outputs.Add(d.Name);
            if (keep.Count == items.Count) return Regex.Replace(line, @"\bvarying\b", "out");
            var sb = new StringBuilder();
            foreach (var d in keep) sb.Append(OutQual(code)).Append(type).Append(' ').Append(d.Name).Append(d.Arr).Append(";\n");
            return sb.ToString();
        }
        var outInputs = new StringBuilder();
        foreach ((string name, string? arr, _) in items)
        {
            if (vsOut != null && vsOut.Contains(name)) { outInputs.Append(InQual(code)).Append(type).Append(' ').Append(name).Append(arr).Append(";\n"); continue; }
            if (supplied.Contains(name)) continue;
            zeroed.Add(name);
            outInputs.Append(arr != null ? $"{type} {name}{arr};\n" : $"{type} {name} = {type}(0);\n");
        }
        return outInputs.ToString();
    }

    /// <summary>A declaration's interpolation qualifiers, carried over to the line that replaces it.</summary>
    private static string InQual(string code) => Interp.Match(code).Value + "in ";
    private static string OutQual(string code) => Interp.Match(code).Value + "out ";

    /// <summary>One content uniform. A name the environment supplies goes; a scalar or <c>vec4</c> the engine's upload
    /// can feed takes a lane -- the one its vertex half took, for a name both declare -- with the declared initialiser
    /// the default for a material that states nothing; a sampler is unit 0 or the probe, as an unset engine sampler
    /// is; anything else is its initialiser, because the engine has no upload that fits it.</summary>
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
        int lane = ps.FindIndex(p => p.Name == name);
        if (lane >= 0) lane = ps[lane].Lane;
        else if (ps.Count >= MaxLanes)
        {
            r.Notes.Add($"declares more than {MaxLanes} parameters -- {name} is held at its default");
            return $"#define {name} {fallback}\n";
        }
        else { lane = ps.Count; ps.Add(new Param(name, kind.Value, lane)); }
        string value = kind switch
        {
            ParamKind.Float => $"CxCp({lane}).x",
            ParamKind.Int => $"int(CxCp({lane}).x)",
            ParamKind.Bool => $"(CxCp({lane}).x != 0.0)",
            _ => $"CxCp({lane})",
        };
        return $"#define {name} (CxCpOn({lane}) ? {value} : {fallback})\n";
    }

    /// <summary>The comma-separated declarators of one declaration: name, array suffix and initialiser. The split is at
    /// commas outside parentheses, so <c>vec4(1, 2, 3, 4)</c> stays whole.</summary>
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

    /// <summary><paramref name="text"/> with every <paramref name="use"/> match -- an identifier and its opening bracket --
    /// replaced by <paramref name="call"/>, and the matching close bracket by a parenthesis: an index of any shape
    /// becomes the call's argument.</summary>
    private static string Indexed(string text, Regex use, string call)
    {
        var sb = new StringBuilder(text.Length);
        int pos = 0;
        for (Match m = use.Match(text); m.Success; m = use.Match(text, pos))
        {
            int depth = 1, i = m.Index + m.Length;
            for (; i < text.Length && depth > 0; i++)
                depth += text[i] == '[' ? 1 : text[i] == ']' ? -1 : 0;
            if (depth != 0) break;
            sb.Append(text, pos, m.Index - pos).Append(call).Append(text, m.Index + m.Length, i - 1 - m.Index - m.Length).Append(')');
            pos = i;
        }
        return sb.Append(text, pos, text.Length - pos).ToString();
    }

    /// <summary>The last <c>KEYHOLING</c> value the stage's own text defines, -1 for none (the stock default).</summary>
    private static int Keyholing(string flat)
    {
        MatchCollection ms = KeyholeDefine.Matches(flat);
        return ms.Count == 0 ? -1 : int.Parse(ms[^1].Groups[1].Value);
    }

    /// <summary>Does the stage write the alpha of its colour after the stock opener? A full <c>vec4(...)</c> assignment
    /// other than <c>vec4(1.0)</c> or one that hands the alpha back unchanged, or any write to a swizzle carrying
    /// <c>a</c>/<c>w</c>.</summary>
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

    /// <summary>Does this stage add nothing to its stock counterpart -- defines and the engine's own includes, and a
    /// <c>main</c> that is the stock opener, the two standard calls and the hand-off? Such a half IS Alloy's own stage,
    /// so the pair needs no translation of it. Read off the stage's own text: an include of its own may add anything.</summary>
    internal static bool StockShaped(string text, bool fragment)
    {
        string code = Comments.Replace(text, " ");
        foreach (Match m in Regex.Matches(code, @"(?m)^[ \t]*#[ \t]*include[ \t]+""([^""]+)"""))
        {
            string n = m.Groups[1].Value.ToLowerInvariant();
            if (n != "inc_standard" && n != "inc_scriptable" && Array.IndexOf(Dropped, n) < 0) return false;
        }
        code = Regex.Replace(code, @"(?m)^[ \t]*#[^\n]*", " ");
        Match main = Regex.Match(code, @"^\s*void\s+main\s*\(\s*(?:void)?\s*\)\s*\{(.*)\}\s*$", RegexOptions.Singleline);
        if (!main.Success) return false;
        foreach (string s in main.Groups[1].Value.Split(';'))
        {
            string st = Regex.Replace(s, @"\s+", "");
            if (st.Length == 0 || st == "ApplyStandardShader()") continue;
            if (!fragment) return false;
            if (st is "SetupStandardShaderInputs()" or "FragmentColor=vec4(1.0)" or "FragmentColor=vec4(1)"
                or "FragmentColor=COLOR_WHITE" or "gl_FragColor=FragmentColor") continue;
            return false;
        }
        return true;
    }

    private static readonly Regex MainFn = new(@"\bvoid\s+main\s*\(\s*(?:void)?\s*\)", RegexOptions.Compiled);
    private static readonly Regex Include = new(@"^\s*#\s*include\s+""([^""]+)""", RegexOptions.Compiled);
    /// <summary><c>#version</c> and <c>#extension</c> lines: the engine prepends its own version, and an extension
    /// directive is illegal past the first declaration, which is where the region sits.</summary>
    private static readonly Regex Directive = new(@"(?m)^[ \t]*#[ \t]*(?:version|extension)\b[^\n]*", RegexOptions.Compiled);
    private static readonly Regex Comments = new(@"//[^\n]*|/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);
    private const string Prec = @"(?:(?:lowp|mediump|highp)\s+)?";
    private const string Layout = @"(?:layout\s*\([^)]*\)\s*)?";
    private const string InterpQ = @"(?:(?:flat|smooth|noperspective|centroid|invariant)\s+)*";
    private static readonly Regex Interp = new(@"^\s*" + Layout + InterpQ, RegexOptions.Compiled);
    private static readonly Regex UniformDecl = new(@"^\s*" + Layout + @"uniform\s+" + Prec + @"(\w+)\s+(.+?)\s*;\s*$", RegexOptions.Compiled);
    /// <summary>An <c>in</c>, <c>varying</c> or <c>attribute</c> declaration: the keyword, the type, the declarators. In a
    /// vertex half a <c>varying</c> is an output.</summary>
    private static readonly Regex InputDecl = new(@"^\s*" + Layout + InterpQ + @"(in|varying|attribute)\s+" + Prec + @"(\w+)\s+(.+?)\s*;\s*$", RegexOptions.Compiled);
    private static readonly Regex OutputDecl = new(@"^\s*" + Layout + InterpQ + @"(out)\s+" + Prec + @"(\w+)\s+(.+?)\s*;\s*$", RegexOptions.Compiled);
    private static readonly Regex Declarator = new(@"^\s*(\w+)\s*(\[[^\]]*\])?\s*(?:=\s*(.+))?$", RegexOptions.Compiled | RegexOptions.Singleline);
    private const string GlslType = @"(?:void|float|double|int|uint|bool|[iubd]?vec[234]|d?mat[234](?:x[234])?)";
    /// <summary>A file-scope variable or constant declaration (<c>[const] type a [= x], b;</c>) at line start: its type
    /// and its declarator list, which <see cref="Items"/> splits. A function prototype has a parenthesis straight after
    /// its name and so never matches.</summary>
    private static readonly Regex GlobalDecl = new(@"(?m)^[ \t]*()(?:(?:const|uniform|lowp|mediump|highp)\s+)*(" + GlslType + @"|sampler\w*)\s+(\w+\s*(?:\[[^\]]*\])?\s*(?:(?:=|,)[^;\n]*)?);", RegexOptions.Compiled);
    /// <summary><see cref="GlobalDecl"/> at column 0 only: how the environments write their file-scope names, and what
    /// keeps the locals inside their helpers (indented) out of the supplied set.</summary>
    private static readonly Regex EnvDecl = new(@"(?m)^(?:(?:const|uniform|lowp|mediump|highp)\s+)*(" + GlslType + @"|sampler\w*)\s+(\w+\s*(?:\[[^\]]*\])?\s*(?:(?:=|,)[^;\n]*)?);", RegexOptions.Compiled);
    /// <summary>A function definition or prototype at line start: its name.</summary>
    private static readonly Regex FnDef = new(@"(?m)^[ \t]*(?:(?:lowp|mediump|highp|const)\s+)*" + GlslType + @"\s+(\w+)\s*\(", RegexOptions.Compiled);
    /// <summary>The engine globals a content stage declares itself and Alloy uploads by name per program bind (the
    /// scriptable block <c>inc_scriptable</c> declares): their declarations stay uniforms, never lanes.</summary>
    private static readonly Regex FedByName = new(@"^scriptable(?:Float|Int|Vec)\d+$", RegexOptions.Compiled);
    /// <summary>An include guard (<c>#ifndef X</c> then <c>#define X</c>): left defined after the region, because the
    /// lit stage's hook region may have spliced the same file first and its guard is what keeps the copy out.</summary>
    private static readonly Regex Guard = new(@"(?m)^[ \t]*#[ \t]*ifndef[ \t]+(\w+)[ \t]*\r?\n[ \t]*#[ \t]*define[ \t]+\1\b", RegexOptions.Compiled);
    /// <summary>A stage whose coverage the stock setup does not cut on the texel: it declares its diffuse alpha an env
    /// level, or has no texture at all.</summary>
    private static readonly Regex OwnCoverage = new(@"(?m)^[ \t]*#[ \t]*define[ \t]+(?:ENVIRONMENT_MAP|NO_TEXTURE)[ \t]+1\b", RegexOptions.Compiled);
    /// <summary>A stage that declares its diffuse alpha an env level, whatever its material says.</summary>
    private static readonly Regex EnvStage = new(@"(?m)^[ \t]*#[ \t]*define[ \t]+ENVIRONMENT_MAP[ \t]+1\b", RegexOptions.Compiled);
    private static readonly Regex KeyholeDefine = new(@"(?m)^[ \t]*#[ \t]*define[ \t]+KEYHOLING[ \t]+(\d+)", RegexOptions.Compiled);
    private static readonly Regex AlphaWrite = new(@"\b(?:FragmentColor|gl_FragColor)\s*=\s*vec4\s*\(", RegexOptions.Compiled);
    private static readonly Regex OpenerRhs = new(@"\G\s*1(?:\.0*)?\s*\)", RegexOptions.Compiled);
    /// <summary>The engine's per-light arrays, which a stage that lights itself reads.</summary>
    private static readonly Regex LightArrays = new(@"\b(?:numLights|lightPosition|lightColor|lightDiffuse|lightAmbient|lightQuadraticAtten)\b", RegexOptions.Compiled);
    /// <summary>One read of the engine's PLT scheme feed, <c>PLTscheme[i]</c>, up to its bracket.</summary>
    private static readonly Regex PltSchemeOpen = new(@"\bPLTscheme\s*\[", RegexOptions.Compiled);
    /// <summary>A declaration of the engine's bone palette, which the vertex environment reads off the draw's own slice
    /// of the bone buffer instead.</summary>
    private static readonly Regex BonesDecl = new(@"(?m)^[ \t]*uniform\b[^;\n]*\bm_bones\b[^;\n]*;[^\n]*\n?", RegexOptions.Compiled);
    /// <summary>One read of the engine's bone palette, <c>m_bones[i]</c>, up to its bracket.</summary>
    private static readonly Regex BonesUse = new(@"\bm_bones\s*\[", RegexOptions.Compiled);
}
