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
        string tail = Regex.Replace(blank[from..to], @"\bFragmentColor\s*=\s*FragmentColor\s*;", "");
        tail = Regex.Replace(tail, @"(?m)^[ \t]*#[^\n]*", "");
        return tail.Trim(' ', '\t', '\r', '\n', ';', '{', '}').Length > 0;
    }

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
