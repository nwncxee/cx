using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CX.Alloy;

internal static partial class ContentStage
{
    /// <summary>A fragment half's <c>main</c> (already <see cref="PreFn"/>), split where the engine's lighting runs: the
    /// statements before its top-level <c>ApplyStandardShader()</c> stay the surface body, the ones after become
    /// <see cref="PostFn"/>, run on the lit colour. A local the post body reads becomes a global, renamed so it can
    /// clash with nothing. A stage that never makes the call is all post: it lights itself, as it does in the engine.</summary>
    private static string SplitMain(string text, Result r)
    {
        Match head = Regex.Match(text, @"\bvoid\s+" + PreFn + @"\s*\(\s*\)");
        if (!head.Success) return text;
        string blank = Blank(text);
        int open = blank.IndexOf('{', head.Index + head.Length);
        int close = open < 0 ? -1 : Matching(blank, open);
        if (close < 0) { r.Refused = "its main() has no closing brace"; return text; }
        int b0 = open + 1;
        var stmts = Statements(blank, b0, close);
        int apply = -1, applyEnd = -1;
        foreach ((int s, int e) in stmts)
        {
            Match m = ApplyCall.Match(blank, s, e - s);
            if (m.Success && m.Index == s && blank[s..e].TrimEnd().Length == m.Length) { apply = s; applyEnd = e; break; }
        }
        bool calls = ApplyAny.IsMatch(blank[b0..close]);
        if (apply < 0 && calls)
        {
            Match nested = ApplyAny.Match(blank, b0, close - b0);
            if (EditsAfter(blank, nested.Index + nested.Length, close))
                r.Notes.Add("edits its colour after an ApplyStandardShader() it makes inside a block, which under Alloy lands "
                          + "on the surface colour before the lighting");
            return text;
        }
        if (apply >= 0 && !EditsAfter(blank, applyEnd, close)) return text;
        // the post body: everything after the call, or the whole of main for a stage that never makes it
        int postFrom = apply >= 0 ? applyEnd : b0;
        int preTo = apply >= 0 ? apply : b0;
        r.Post = true;
        r.AlphaPost = apply >= 0 && AlphaOnly(blank[postFrom..close]);
        if (apply < 0) r.Notes.Add("never calls ApplyStandardShader(), so it lights itself: its colour is drawn as it leaves it");
        var hoist = new List<(string Type, string Name, string? Arr)>();
        var used = Idents(blank[postFrom..close]);
        var pre = new StringBuilder();
        int at = b0;
        foreach ((int s, int e) in stmts)
        {
            if (e > preTo) break;
            Match d = LocalDecl.Match(blank, s, e - s);
            if (!d.Success || d.Index != s || Keyword.IsMatch(d.Groups[2].Value) || blank[s..e].TrimEnd().Length != d.Length) continue;
            var items = Items(d.Groups[3].Value).ToList();
            if (!items.Any(i => used.Contains(i.Name))) continue;
            pre.Append(text, at, d.Index - at);
            var keep = new List<string>();
            foreach ((string name, string? arr, string? init) in items)
            {
                if (!used.Contains(name)) { keep.Add(name + arr + (init != null ? " = " + init : "")); continue; }
                hoist.Add((d.Groups[2].Value, name, arr));
                if (init != null) pre.Append(HoistName(name)).Append(" = ").Append(init).Append("; ");
            }
            if (keep.Count > 0) pre.Append(d.Groups[1].Value).Append(d.Groups[2].Value).Append(' ').Append(string.Join(", ", keep)).Append(';');
            at = d.Index + d.Length;
        }
        pre.Append(text, at, preTo - at);
        string post = text[postFrom..close];
        string preText = pre.ToString();
        foreach ((_, string name, _) in hoist)
        {
            var word = new Regex(@"(?<![.\w])" + Regex.Escape(name) + @"\b");
            preText = word.Replace(preText, HoistName(name));
            post = word.Replace(post, HoistName(name));
        }
        var sb = new StringBuilder(text.Length + 256);
        sb.Append(text, 0, head.Index);
        foreach ((string type, string name, string? arr) in hoist) sb.Append(type).Append(' ').Append(HoistName(name)).Append(arr).Append(";\n");
        sb.Append("void ").Append(PreFn).Append("()\n{").Append(preText).Append("\n}\n");
        sb.Append("void ").Append(PostFn).Append("()\n{").Append(post).Append("\n}");
        sb.Append(text, close + 1, text.Length - close - 1);
        return sb.ToString();
    }

    private static string HoistName(string local) => "cxHoist_" + local;

    /// <summary>Does the code from <paramref name="from"/> to <paramref name="to"/> do anything but hand the colour to
    /// <c>gl_FragColor</c> (which the rename has made <c>FragmentColor = FragmentColor;</c>) and close its blocks?</summary>
    private static bool EditsAfter(string blank, int from, int to)
    {
        string tail = HandOff.Replace(blank[from..to], "");
        tail = Regex.Replace(tail, @"(?m)^[ \t]*#[^\n]*", "");
        return tail.Trim(' ', '\t', '\r', '\n', ';', '{', '}').Length > 0;
    }

    /// <summary>Does a post body touch nothing but the alpha? It may write <c>FragmentColor = vec4(FragmentColor.rgb, x)</c>
    /// or <c>FragmentColor.a</c>, its own locals, discard, and hand the colour on unchanged (<c>gl_FragColor = FragmentColor</c>);
    /// a call it could not be sure of, or any other write, says no.</summary>
    private static bool AlphaOnly(string post)
    {
        string held = FragmentAlpha.Replace(KeepsRgb.Replace(HandOff.Replace(post, ""), "cxHeld = vec4("), "cxHeld");
        if (Regex.IsMatch(held, @"\bFragmentColor\b")) return false;
        var locals = new HashSet<string>(LocalNames.Matches(held).Select(m => m.Groups[1].Value)) { "cxHeld" };
        return AssignTarget.Matches(held).All(m => locals.Contains(m.Groups[1].Value))
            && CallName.Matches(held).All(m => PureCalls.Contains(m.Groups[1].Value));
    }

    /// <summary>The hand-off to <c>gl_FragColor</c>, which the rename has made the colour assigned to itself.</summary>
    private static readonly Regex HandOff = new(@"\bFragmentColor\s*=\s*FragmentColor\s*;", RegexOptions.Compiled);
    private static readonly Regex KeepsRgb = new(@"\bFragmentColor\s*=\s*vec4\s*\(\s*FragmentColor\s*\.\s*rgb\s*,", RegexOptions.Compiled);
    private static readonly Regex FragmentAlpha = new(@"\bFragmentColor\s*\.\s*a\b", RegexOptions.Compiled);
    private static readonly Regex LocalNames = new(@"\b(?:float|int|uint|bool|vec[234]|mat[234])\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
    private static readonly Regex AssignTarget = new(@"([A-Za-z_]\w*)(?:\s*\.\s*\w+|\s*\[[^\]]*\])?\s*[-+*/]?=(?!=)", RegexOptions.Compiled);
    private static readonly Regex CallName = new(@"([A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);

    /// <summary>What a post body that touches only the alpha may call: GLSL's own, which write nothing.</summary>
    private static readonly HashSet<string> PureCalls =
    [
        "if", "float", "int", "bool", "vec2", "vec3", "vec4", "mat3", "mat4", "texture", "texture2D", "clamp", "min", "max", "dot",
        "mix", "step", "smoothstep", "abs", "pow", "sqrt", "exp", "length", "normalize", "floor", "fract", "sin", "cos",
    ];

    /// <summary><paramref name="text"/> with every comment blanked to spaces, newlines kept: same length, same offsets.</summary>
    private static string Blank(string text)
    {
        var c = text.ToCharArray();
        for (int i = 0; i < c.Length - 1; i++)
        {
            if (c[i] == '/' && c[i + 1] == '/')
                for (; i < c.Length && c[i] != '\n'; i++) c[i] = ' ';
            else if (c[i] == '/' && c[i + 1] == '*')
            {
                int e = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                e = e < 0 ? c.Length : e + 2;
                for (; i < e; i++) if (c[i] != '\n') c[i] = ' ';
                i--;
            }
        }
        return new string(c);
    }

    /// <summary>The name a stage's own local or parameter <c>FragmentColor</c> takes, so it no longer hides the global.</summary>
    internal const string ShadowName = "cxOwnFragmentColor";

    /// <summary>The GLSL types a <c>FragmentColor</c> of the stage's own is declared with.</summary>
    private const string ShadowTypes = @"(?:[biu]?vec[234]|float|int|uint|bool|mat[234])";

    /// <summary>A declaration of <c>FragmentColor</c> as a local or a parameter: after its type, or after a comma in a
    /// declaration (<see cref="DeclStatement"/> says which commas are), then what may follow the name.</summary>
    private static readonly Regex ShadowDecl = new(@"(?:\b" + ShadowTypes + @"\s+|,\s*)FragmentColor\b(?=\s*[=;,)\[])",
                                                   RegexOptions.Compiled);

    /// <summary>A statement that opens with a type, its qualifiers before it: a declaration.</summary>
    private static readonly Regex DeclStatement = new(@"^\s*(?:(?:const|highp|mediump|lowp)\s+)*" + ShadowTypes + @"\s+\w",
                                                      RegexOptions.Compiled);

    /// <summary>A use of the name, not a member of the same name (<c>l.FragmentColor</c>).</summary>
    private static readonly Regex FragmentColorUse = new(@"(?<!\.)\bFragmentColor\b", RegexOptions.Compiled);

    /// <summary><paramref name="text"/> with every local or parameter it names <c>FragmentColor</c> renamed <see cref="ShadowName"/>
    /// through its scope, so the rename of <c>gl_FragColor</c> onto the global cannot land on the stage's own variable. Text whose
    /// braces do not balance is returned unchanged.</summary>
    internal static string Unshadow(string text)
    {
        string blank = Blank(text);
        if (blank.Count(c => c == '{') != blank.Count(c => c == '}')) return text;
        var scopes = new List<(int From, int To)>();
        foreach (Match m in ShadowDecl.Matches(blank))
            if ((blank[m.Index] != ',' || InDeclaration(blank, m.Index)) && ShadowScope(blank, m.Index) is int end)
                scopes.Add((m.Index, end));
        if (scopes.Count == 0) return text;
        var sb = new StringBuilder(text.Length + 32 * scopes.Count);
        int cursor = 0;
        foreach (Match u in FragmentColorUse.Matches(blank))
        {
            if (!scopes.Exists(s => u.Index >= s.From && u.Index <= s.To)) continue;
            sb.Append(text, cursor, u.Index - cursor).Append(ShadowName);
            cursor = u.Index + u.Length;
        }
        return sb.Append(text, cursor, text.Length - cursor).ToString();
    }

    /// <summary>Where the variable declared at <paramref name="at"/> goes out of scope: the brace closing the block it is
    /// declared in, or for a parameter the one closing its function's body. Null for a global or a prototype's parameter.</summary>
    private static int? ShadowScope(string blank, int at)
    {
        var open = new Stack<int>();
        int paren = 0;
        for (int i = 0; i < at; i++)
        {
            char c = blank[i];
            if (c == '{') open.Push(i);
            else if (c == '}' && open.Count > 0) open.Pop();
            else if (open.Count == 0 && c == '(') paren++;
            else if (open.Count == 0 && c == ')') paren--;
        }
        if (open.Count > 0) return StructBody(blank, open.Peek()) ? null : Matching(blank, open.Peek());
        if (paren <= 0) return null;
        int close = at;
        for (int depth = paren; close < blank.Length && depth > 0; close++)
            depth += blank[close] == '(' ? 1 : blank[close] == ')' ? -1 : 0;
        int body = close;
        while (body < blank.Length && char.IsWhiteSpace(blank[body])) body++;
        return body < blank.Length && blank[body] == '{' ? Matching(blank, body) : null;
    }

    /// <summary>Is the comma at <paramref name="comma"/> between two names of one declaration, not two arguments of a call?</summary>
    private static bool InDeclaration(string blank, int comma)
    {
        int start = blank.LastIndexOfAny([';', '{', '}'], comma) + 1;
        string head = blank[start..comma];
        return DeclStatement.IsMatch(head) && head.Count(c => c == '(') == head.Count(c => c == ')');
    }

    /// <summary>Does the brace at <paramref name="open"/> open a struct's members rather than a block?</summary>
    private static bool StructBody(string blank, int open)
        => Regex.IsMatch(blank[..open], @"\bstruct\s+\w+\s*$");

    /// <summary>The brace closing the one at <paramref name="open"/>, or -1.</summary>
    private static int Matching(string blank, int open)
    {
        int depth = 0;
        for (int i = open; i < blank.Length; i++)
        {
            if (blank[i] == '{') depth++;
            else if (blank[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>The top-level statements between <paramref name="from"/> and <paramref name="to"/>: each ends at a
    /// <c>;</c> or at the brace that closes a block back to the top level. A statement's leading preprocessor lines are
    /// left out of its span, so a declaration under an <c>#if</c> still reads as one.</summary>
    private static List<(int Start, int End)> Statements(string blank, int from, int to)
    {
        var list = new List<(int, int)>();
        int depth = 0, paren = 0, start = from;
        for (int i = from; i < to; i++)
        {
            char c = blank[i];
            if (c == '(') paren++;
            else if (c == ')') paren--;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0 && paren == 0) { list.Add((Skip(blank, start, i + 1), i + 1)); start = i + 1; }
            else if (c == ';' && depth == 0 && paren == 0) { list.Add((Skip(blank, start, i + 1), i + 1)); start = i + 1; }
        }
        return list;
    }

    /// <summary>Past the whitespace and the preprocessor lines a statement span opens with.</summary>
    private static int Skip(string blank, int s, int e)
    {
        while (s < e)
        {
            while (s < e && char.IsWhiteSpace(blank[s])) s++;
            if (s >= e || blank[s] != '#') return s;
            while (s < e && blank[s] != '\n') s++;
        }
        return s;
    }

    /// <summary>The identifiers a span names, member accesses (<c>.x</c>) left out.</summary>
    private static HashSet<string> Idents(string span)
        => Regex.Matches(span, @"(?<![.\w])[A-Za-z_]\w*").Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    private static readonly Regex ApplyCall = new(@"\bApplyStandardShader\s*\(\s*\)\s*;", RegexOptions.Compiled);
    private static readonly Regex ApplyAny = new(@"\bApplyStandardShader\s*\(\s*\)", RegexOptions.Compiled);
    /// <summary>A local declaration statement: its <c>const</c>/precision prefix, its type, its declarators.</summary>
    private static readonly Regex LocalDecl = new(@"((?:(?:const|lowp|mediump|highp)\s+)*)([A-Za-z_]\w*)\s+([A-Za-z_]\w*\s*(?:\[[^\]]*\])?\s*(?:=[^;]*|,[^;]*)?)\s*;", RegexOptions.Compiled | RegexOptions.Singleline);
    /// <summary>Words that open a statement and are not a type.</summary>
    private static readonly Regex Keyword = new(@"^(?:return|else|if|for|while|do|switch|case|discard|break|continue)$", RegexOptions.Compiled);
}
