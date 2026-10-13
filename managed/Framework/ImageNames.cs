using System;

namespace CX.Framework;

/// <summary>
/// The NUI image names CX draws itself instead of loading a texture (<c>docs/ui-policy-contract.md</c>): an item the client
/// knows, an item described by its look, and a live model view. The one reader of the object ids they carry.
/// </summary>
internal static class ImageNames
{
    /// <summary>The prefixes: <c>@item:</c> then a hex object id, <c>@look:</c> then hex fields, <c>@model:</c> then a view's key.</summary>
    public const string Item = "@item:", Look = "@look:", Model = "@model:";
    /// <summary><c>CGameObjectArray::LISTTYPE_MASK</c>: the server ORs it into every id it sends and the client keeps ids as read,
    /// so the server's <c>ObjectToString</c> value <c>e04</c> is client object <c>0x80000e04</c>. The wire format, the same on every build.</summary>
    public const uint ServerIdBit = 0x80000000;   // not-an-offset: object id bit
    /// <summary>The most hex digits an object id has.</summary>
    public const int IdDigits = 8;

    /// <summary>True, and <paramref name="id"/> set, when <paramref name="s"/> is <c>@item:</c> then 1-8 hex digits. What follows them is
    /// ignored, as the engine's image loader ignores it.</summary>
    public static bool TryItem(ReadOnlySpan<char> s, out uint id)
    {
        id = 0;
        if (!s.StartsWith(Item, StringComparison.Ordinal)) return false;
        int n = 0;
        for (; n < IdDigits && Item.Length + n < s.Length; n++)
        {
            int d = HexColour.Digit(s[Item.Length + n]);
            if (d < 0) break;
            id = id << 4 | (uint)d;
        }
        return n > 0;
    }

    /// <summary><see cref="TryItem(ReadOnlySpan{char}, out uint)"/> over UTF-8 bytes.</summary>
    public static bool TryItem(ReadOnlySpan<byte> s, out uint id)
    {
        id = 0;
        if (!s.StartsWith("@item:"u8)) return false;
        int n = 0;
        for (; n < IdDigits && Item.Length + n < s.Length; n++)
        {
            int d = HexColour.Digit((char)s[Item.Length + n]);
            if (d < 0) break;
            id = id << 4 | (uint)d;
        }
        return n > 0;
    }

    /// <summary>The key of an <c>@model:</c> name, or null when <paramref name="s"/> is not one or has no key.</summary>
    public static string? ModelKey(string s) => s.StartsWith(Model, StringComparison.Ordinal) && s.Length > Model.Length ? s[Model.Length..] : null;

    /// <summary>Oracle: hand-written names and the ids a reader would write down for them.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(TryItem("@item:7F00a1ff2", out uint id) && id == 0x7f00a1ff, "@item: reads up to eight hex digits, either case");
        check.Expect(TryItem("@item:1a;x", out id) && id == 0x1a && TryItem("@item:1a"u8, out id) && id == 0x1a, "what follows the digits is ignored, in chars and in bytes");
        check.Expect(!TryItem("@item:", out _) && !TryItem("@item:zz", out _) && !TryItem("@look:1a", out _) && !TryItem("@item:"u8, out _), "no digits or another prefix is not an item");
        check.Expect(ModelKey("@model:doll") == "doll" && ModelKey("@model:") == null && ModelKey("doll") == null, "a model name's key is what follows the prefix");
        log.Info(check.Ok ? "ImageNames.SelfCheck: passed" : "ImageNames.SelfCheck: FAILED");
    }
}
