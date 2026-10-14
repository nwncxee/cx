using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CX.Services.Panels;

/// <summary>
/// The inline markup of a panel's text: <c>&lt;c=#RRGGBB[AA]&gt;</c> or <c>&lt;c=@role[/aa]&gt;</c>, <c>&lt;s=N&gt;</c> (snapped to the size ladder),
/// <c>&lt;i&gt;</c>, their closing tags, <c>&lt;icon=NAME&gt;</c> and <c>&lt;br&gt;</c>; a newline in the string breaks the line
/// too, and <c>&lt;&lt;</c> is a literal <c>&lt;</c>. A tag it does not know, or a bad value, stays in the text as typed.
/// Cold: parses once, when a declaration arrives.
/// </summary>
internal static class PanelMarkup
{
    /// <summary>The style a text snaps its sizes with when the caller names none; only read.</summary>
    private static readonly PanelStyle Default = new();

    /// <summary>The runs of <paramref name="s"/>, or false with the reason when it is over the text or run limit.</summary>
    public static bool TryParse(string s, out Run[] runs, out string error, PanelStyle? style = null)
    {
        style ??= Default;
        runs = [];
        error = "";
        if (s.Length > PanelLimits.MaxText) { error = $"text over {PanelLimits.MaxText} characters"; return false; }
        var list = new List<Run>();
        var colors = new List<Paint>();
        var sizes = new List<float>();
        int italic = 0;
        var buf = new StringBuilder();
        void Flush()
        {
            if (buf.Length == 0) return;
            list.Add(new Run(buf.ToString(), "", colors.Count > 0 ? colors[^1] : default, sizes.Count > 0 ? sizes[^1] : 0, italic > 0, false));
            buf.Clear();
        }
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch == '\n') { Flush(); list.Add(new Run("", "", default, 0, false, true)); continue; }
            if (ch != '<') { buf.Append(ch); continue; }
            if (i + 1 < s.Length && s[i + 1] == '<') { buf.Append('<'); i++; continue; }
            int end = s.IndexOf('>', i + 1);
            if (end < 0 || !Tag(s.AsSpan(i + 1, end - i - 1), out string name, out string arg, out float size)) { buf.Append('<'); continue; }
            Flush();
            switch (name)
            {
                case "c": colors.Add(Paint.TryParse(arg, out Paint c) ? c : default); break;
                case "/c": if (colors.Count > 0) colors.RemoveAt(colors.Count - 1); break;
                case "s": sizes.Add(style.Snap(size)); break;
                case "/s": if (sizes.Count > 0) sizes.RemoveAt(sizes.Count - 1); break;
                case "i": italic++; break;
                case "/i": if (italic > 0) italic--; break;
                case "br": list.Add(new Run("", "", default, 0, false, true)); break;
                case "icon":
                    if (!PanelLimits.NameOk(arg)) { error = $"icon '{arg}' is over {PanelLimits.MaxResref} characters, the longest resref"; return false; }
                    list.Add(new Run("", arg, colors.Count > 0 ? colors[^1] : default, sizes.Count > 0 ? sizes[^1] : 0, false, false)); break;
            }
            i = end;
        }
        Flush();
        if (list.Count > PanelLimits.MaxRuns) { error = $"more than {PanelLimits.MaxRuns} runs in one text"; return false; }
        runs = list.ToArray();
        return true;
    }

    /// <summary>True when <paramref name="body"/> is a tag this markup knows, with a value that parses.</summary>
    private static bool Tag(ReadOnlySpan<char> body, out string name, out string arg, out float size)
    {
        size = 0f;
        int eq = body.IndexOf('=');
        name = (eq < 0 ? body : body[..eq]).ToString();
        arg = eq < 0 ? "" : body[(eq + 1)..].ToString();
        switch (name)
        {
            case "/c": case "/s": case "i": case "/i": case "br": return eq < 0;
            case "c": return Paint.TryParse(arg, out _);
            case "s": return float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out size) && float.IsFinite(size);
            case "icon": return arg.Length is > 0 and <= PanelLimits.MaxIconName;
            default: return false;
        }
    }
}
