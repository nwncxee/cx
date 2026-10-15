using System;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CX.Engine;

namespace CX.Services.Nui;

/// <summary>
/// Reads an engine <c>nlohmann::json</c> value out of memory and writes it as compact JSON text. The value is 16 bytes on both
/// builds -- a type byte, then the payload at +8 -- but the containers behind it are the compiler's: an object is a
/// <c>std::map</c> (libstdc++ red-black tree or MSVC's), an array a <c>std::vector</c>, a string a <c>std::string</c>.
/// Every read is bounded (<see cref="MaxNodes"/>, <see cref="MaxDepth"/>, <see cref="MaxText"/>), so a damaged value ends the dump.
/// </summary>
internal static unsafe partial class NuiJsonRead
{
    /// <summary>Most values one dump visits.</summary>
    internal const int MaxNodes = 20000;
    /// <summary>Deepest nesting followed, in values and in tree levels.</summary>
    internal const int MaxDepth = 64;
    /// <summary>Most characters one dump writes.</summary>
    internal const int MaxText = 200_000;

    private const byte TObject = 1, TArray = 2, TString = 3, TBool = 4, TInt = 5, TUint = 6, TFloat = 7, TNull = 0;

    /// <summary>One dump's counters.</summary>
    private struct Budget { public int Nodes; public bool Over; }

    /// <summary>The value at <paramref name="json"/> as compact JSON text, or null when it passed a bound. <paramref name="msvc"/>
    /// picks the container shapes: the running OS by default.</summary>
    internal static string? Dump(IntPtr json, bool? msvc = null)
    {
        var sb = new StringBuilder();
        var b = new Budget();
        Write(sb, new NuiJsonValue((byte*)json), msvc ?? OperatingSystem.IsWindows(), 0, ref b);
        return b.Over ? null : sb.ToString();
    }

    /// <summary>The text of a <c>std::string</c> at <paramref name="s"/>, or null when its length is not credible.</summary>
    internal static string? ReadString(byte* s, bool msvc) => ReadString(new NuiStdString(s, msvc));

    private static string? ReadString(NuiStdString s)
    {
        ulong len = s.Length;
        if (len > MaxText) return null;
        byte* p = s.Chars;
        return p == null ? null : Encoding.UTF8.GetString(p, (int)len);
    }

    /// <summary>Whether the <c>std::string</c> at <paramref name="s"/> holds exactly the bytes <paramref name="utf8"/>.</summary>
    internal static bool StringIs(byte* s, bool msvc, ReadOnlySpan<byte> utf8)
    {
        var str = new NuiStdString(s, msvc);
        if (str.Length != (ulong)utf8.Length) return false;
        byte* p = str.Chars;
        return p != null && new ReadOnlySpan<byte>(p, utf8.Length).SequenceEqual(utf8);
    }

    /// <summary>The first object in the value at <paramref name="json"/> whose <c>bind</c> member is the string <paramref name="name"/>:
    /// the address of that object's own value, which the engine's <c>Bind</c> reads. Zero when there is none.</summary>
    internal static IntPtr FindBind(IntPtr json, string name, bool? msvc = null)
    {
        int budget = MaxNodes;
        return (IntPtr)Find(new NuiJsonValue((byte*)json), name, msvc ?? OperatingSystem.IsWindows(), 0, ref budget).Ptr;
    }

    private static NuiJsonValue Find(NuiJsonValue v, string name, bool msvc, int depth, ref int budget)
    {
        if (v.Ptr == null || --budget < 0 || depth > MaxDepth) return default;
        if (v.Type == TArray)
        {
            var vec = new NuiJsonVector(v.Pointer);
            if (vec.Ptr == null || vec.End < vec.Begin) return default;
            for (var e = new NuiJsonValue(vec.Begin); e.Ptr < vec.End && budget > 0; e = e.Next())
                if (Find(e, name, msvc, depth + 1, ref budget) is var f && f.Ptr != null) return f;
            return default;
        }
        if (v.Type != TObject) return default;
        var map = new NuiJsonMap(v.Pointer);
        return map.Ptr == null ? default : FindIn(map.Root(msvc, out var head), head, v, name, msvc, depth, 0, ref budget);
    }

    private static NuiJsonValue FindIn(NuiJsonMapNode n, NuiJsonMapNode head, NuiJsonValue owner, string name, bool msvc, int depth, int level, ref int budget)
    {
        if (n.IsEnd(head, msvc) || --budget < 0 || level > MaxDepth) return default;
        var hit = FindIn(n.Left(msvc), head, owner, name, msvc, depth, level + 1, ref budget);
        if (hit.Ptr != null) return hit;
        var val = n.Value;
        if (ReadString(n.Key(msvc)) == "bind" && val.Type == TString)
        {
            byte* str = val.Pointer;
            if (str != null && ReadString(str, msvc) == name) return owner;
        }
        else if (Find(val, name, msvc, depth + 1, ref budget) is var f && f.Ptr != null) return f;
        return FindIn(n.Right(msvc), head, owner, name, msvc, depth, level + 1, ref budget);
    }

    private static void Write(StringBuilder sb, NuiJsonValue v, bool msvc, int depth, ref Budget b)
    {
        if (b.Over) return;
        if (++b.Nodes > MaxNodes || depth > MaxDepth || sb.Length > MaxText) { b.Over = true; return; }
        switch (v.Type)
        {
            case TObject: WriteObject(sb, new NuiJsonMap(v.Pointer), msvc, depth, ref b); break;
            case TArray:
            {
                var vec = new NuiJsonVector(v.Pointer);
                if (vec.Ptr == null) { b.Over = true; return; }
                byte* first = vec.Begin, last = vec.End;
                if (last < first || (last - first) / NuiJsonValue.Bytes > MaxNodes) { b.Over = true; return; }
                sb.Append('[');
                for (var e = new NuiJsonValue(first); e.Ptr < last; e = e.Next())
                {
                    if (e.Ptr != first) sb.Append(',');
                    Write(sb, e, msvc, depth + 1, ref b);
                }
                sb.Append(']');
                break;
            }
            case TString:
            {
                byte* str = v.Pointer;
                string? s = str == null ? null : ReadString(str, msvc);
                if (s == null) { b.Over = true; return; }
                Quote(sb, s);
                break;
            }
            case TBool: sb.Append(v.Bool != 0 ? "true" : "false"); break;
            case TInt: sb.Append(v.Int.ToString(CultureInfo.InvariantCulture)); break;
            case TUint: sb.Append(v.Uint.ToString(CultureInfo.InvariantCulture)); break;
            case TFloat:
            {
                double d = v.Float;
                sb.Append(double.IsFinite(d) ? d.ToString("R", CultureInfo.InvariantCulture) : "null");
                break;
            }
            case TNull: sb.Append("null"); break;
            default: b.Over = true; break;
        }
    }

    private static void WriteObject(StringBuilder sb, NuiJsonMap map, bool msvc, int depth, ref Budget b)
    {
        if (map.Ptr == null) { b.Over = true; return; }
        sb.Append('{');
        bool first = true;
        Walk(sb, map.Root(msvc, out var head), head, msvc, ref first, depth, 0, ref b);
        sb.Append('}');
    }

    /// <summary>Visits a tree node and both subtrees, smallest key first.</summary>
    private static void Walk(StringBuilder sb, NuiJsonMapNode n, NuiJsonMapNode head, bool msvc, ref bool first, int depth, int level, ref Budget b)
    {
        if (b.Over || n.IsEnd(head, msvc)) return;
        if (++b.Nodes > MaxNodes || level > MaxDepth) { b.Over = true; return; }
        Walk(sb, n.Left(msvc), head, msvc, ref first, depth, level + 1, ref b);
        string? key = ReadString(n.Key(msvc));
        if (key == null) { b.Over = true; return; }
        if (!first) sb.Append(',');
        first = false;
        Quote(sb, key);
        sb.Append(':');
        Write(sb, n.Value, msvc, depth + 1, ref b);
        Walk(sb, n.Right(msvc), head, msvc, ref first, depth, level + 1, ref b);
    }

    private static void Quote(StringBuilder sb, string s) => sb.Append('"').Append(JsonEncodedText.Encode(s, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value).Append('"');
}
