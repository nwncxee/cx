using System;
using System.Globalization;
using System.Text;

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
    private const int ValueAt = 8, JsonSize = 16, StringSize = 32;
    private const int NodeValueAt = 32;
    private const int GccTreeRoot = 16, GccLeft = 16, GccRight = 24;
    private const int MsvcLeft = 0, MsvcParent = 8, MsvcRight = 16, MsvcIsNil = 25;
    private const int MsvcStringSize = 16, MsvcStringCap = 24, GccStringSize = 8;

    /// <summary>One dump's counters.</summary>
    private struct Budget { public int Nodes; public bool Over; }

    /// <summary>The value at <paramref name="json"/> as compact JSON text, or null when it passed a bound. <paramref name="msvc"/>
    /// picks the container shapes: the running OS by default.</summary>
    internal static string? Dump(IntPtr json, bool? msvc = null)
    {
        var sb = new StringBuilder();
        var b = new Budget();
        Write(sb, (byte*)json, msvc ?? OperatingSystem.IsWindows(), 0, ref b);
        return b.Over ? null : sb.ToString();
    }

    /// <summary>The text of a <c>std::string</c> at <paramref name="s"/>, or null when its length is not credible.</summary>
    internal static string? ReadString(byte* s, bool msvc)
    {
        ulong len = *(ulong*)(s + (msvc ? MsvcStringSize : GccStringSize));
        if (len > MaxText) return null;
        byte* p = msvc && *(ulong*)(s + MsvcStringCap) < 16 ? s : *(byte**)s;
        return p == null ? null : Encoding.UTF8.GetString(p, (int)len);
    }

    /// <summary>The first object in the value at <paramref name="json"/> whose <c>bind</c> member is the string <paramref name="name"/>:
    /// the address of that object's own value, which the engine's <c>Bind</c> reads. Zero when there is none.</summary>
    internal static IntPtr FindBind(IntPtr json, string name, bool? msvc = null)
    {
        int budget = MaxNodes;
        return (IntPtr)Find((byte*)json, name, msvc ?? OperatingSystem.IsWindows(), 0, ref budget);
    }

    private static byte* Find(byte* v, string name, bool msvc, int depth, ref int budget)
    {
        if (v == null || --budget < 0 || depth > MaxDepth) return null;
        if (*v == TArray)
        {
            byte** vec = *(byte***)(v + ValueAt);
            if (vec == null || vec[1] < vec[0]) return null;
            for (byte* e = vec[0]; e < vec[1] && budget > 0; e += JsonSize)
                if (Find(e, name, msvc, depth + 1, ref budget) is var f && f != null) return f;
            return null;
        }
        if (*v != TObject) return null;
        byte* map = *(byte**)(v + ValueAt);
        if (map == null) return null;
        byte* head = msvc ? *(byte**)map : null;
        byte* root = msvc ? (head == null ? null : *(byte**)(head + MsvcParent)) : *(byte**)(map + GccTreeRoot);
        return FindIn(root, head, v, name, msvc, depth, 0, ref budget);
    }

    private static byte* FindIn(byte* n, byte* head, byte* owner, string name, bool msvc, int depth, int level, ref int budget)
    {
        if (n == null || (msvc && (n == head || n[MsvcIsNil] != 0)) || --budget < 0 || level > MaxDepth) return null;
        byte* hit = FindIn(*(byte**)(n + (msvc ? MsvcLeft : GccLeft)), head, owner, name, msvc, depth, level + 1, ref budget);
        if (hit != null) return hit;
        byte* val = n + NodeValueAt + StringSize;
        if (ReadString(n + NodeValueAt, msvc) == "bind" && *val == TString)
        {
            byte* str = *(byte**)(val + ValueAt);
            if (str != null && ReadString(str, msvc) == name) return owner;
        }
        else if (Find(val, name, msvc, depth + 1, ref budget) is var f && f != null) return f;
        return FindIn(*(byte**)(n + (msvc ? MsvcRight : GccRight)), head, owner, name, msvc, depth, level + 1, ref budget);
    }

    private static void Write(StringBuilder sb, byte* v, bool msvc, int depth, ref Budget b)
    {
        if (b.Over) return;
        if (++b.Nodes > MaxNodes || depth > MaxDepth || sb.Length > MaxText) { b.Over = true; return; }
        switch (*v)
        {
            case TObject: WriteObject(sb, *(byte**)(v + ValueAt), msvc, depth, ref b); break;
            case TArray:
            {
                byte** vec = *(byte***)(v + ValueAt);
                if (vec == null) { b.Over = true; return; }
                byte* first = vec[0], last = vec[1];
                if (last < first || (last - first) / JsonSize > MaxNodes) { b.Over = true; return; }
                sb.Append('[');
                for (byte* e = first; e < last; e += JsonSize)
                {
                    if (e != first) sb.Append(',');
                    Write(sb, e, msvc, depth + 1, ref b);
                }
                sb.Append(']');
                break;
            }
            case TString:
            {
                byte* str = *(byte**)(v + ValueAt);
                string? s = str == null ? null : ReadString(str, msvc);
                if (s == null) { b.Over = true; return; }
                Quote(sb, s);
                break;
            }
            case TBool: sb.Append(v[ValueAt] != 0 ? "true" : "false"); break;
            case TInt: sb.Append(((long*)(v + ValueAt))[0].ToString(CultureInfo.InvariantCulture)); break;
            case TUint: sb.Append(((ulong*)(v + ValueAt))[0].ToString(CultureInfo.InvariantCulture)); break;
            case TFloat:
            {
                double d = ((double*)(v + ValueAt))[0];
                sb.Append(double.IsFinite(d) ? d.ToString("R", CultureInfo.InvariantCulture) : "null");
                break;
            }
            case TNull: sb.Append("null"); break;
            default: b.Over = true; break;
        }
    }

    private static void WriteObject(StringBuilder sb, byte* map, bool msvc, int depth, ref Budget b)
    {
        if (map == null) { b.Over = true; return; }
        sb.Append('{');
        bool first = true;
        if (msvc)
        {
            byte* head = *(byte**)map;
            byte* root = head == null ? null : *(byte**)(head + MsvcParent);
            Walk(sb, root, head, true, ref first, depth, 0, ref b);
        }
        else Walk(sb, *(byte**)(map + GccTreeRoot), null, false, ref first, depth, 0, ref b);
        sb.Append('}');
    }

    /// <summary>Visits a tree node and both subtrees, smallest key first. libstdc++ ends a branch with a null; MSVC with the head
    /// node, whose <c>_Isnil</c> byte is set.</summary>
    private static void Walk(StringBuilder sb, byte* n, byte* head, bool msvc, ref bool first, int depth, int level, ref Budget b)
    {
        if (b.Over || n == null || (msvc && (n == head || n[MsvcIsNil] != 0))) return;
        if (++b.Nodes > MaxNodes || level > MaxDepth) { b.Over = true; return; }
        Walk(sb, *(byte**)(n + (msvc ? MsvcLeft : GccLeft)), head, msvc, ref first, depth, level + 1, ref b);
        string? key = ReadString(n + NodeValueAt, msvc);
        if (key == null) { b.Over = true; return; }
        if (!first) sb.Append(',');
        first = false;
        Quote(sb, key);
        sb.Append(':');
        Write(sb, n + NodeValueAt + StringSize, msvc, depth + 1, ref b);
        Walk(sb, *(byte**)(n + (msvc ? MsvcRight : GccRight)), head, msvc, ref first, depth, level + 1, ref b);
    }

    private static void Quote(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        sb.Append('"');
    }
}
