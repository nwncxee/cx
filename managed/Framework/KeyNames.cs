using System;
using CX.Engine;

namespace CX.Framework;

/// <summary>The one parser of a stored key name: "F", "]" or "Mouse4" mean the same press wherever a setting binds
/// them, read through SDL's own key-name table.</summary>
internal static unsafe class KeyNames
{
    /// <summary>The press code for <paramref name="name"/>: an SDL key code, -1 to -5 for Mouse1 to Mouse5, 0 for blank or
    /// a name SDL does not know. Off the client a one-character name reads as SDL would read it. Cold: a setting's edit.</summary>
    internal static int Code(string? name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return 0;
        if (name.StartsWith("mouse", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(5), out int btn) && btn is >= 1 and <= 5)
            return -btn;
        // SDL answers a one-character name with that character, but a key event's sym is the LOWER-case letter.
        if (name.Length == 1) name = name.ToLowerInvariant();
#pragma warning disable RS0030 // KeyNames.Code is the one reader of SDL's key-name table
        var fromName = EngineFn.SdlGetKeyFromName;
#pragma warning restore RS0030
        if (fromName == null) return name.Length == 1 && name[0] is > ' ' and < (char)0x7f ? name[0] : 0;
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = utf8) return fromName(p);
    }

    /// <summary>The name a setting stores for press <paramref name="code"/>, the one <see cref="Code"/> reads back: a
    /// printable key as its upper-case character, Mouse1 to Mouse5 for -1 to -5, else SDL's name. "" when SDL has none.</summary>
    internal static string Name(int code)
    {
        if (code is >= -5 and <= -1) return "Mouse" + (-code).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (code is > ' ' and < 0x7f) return char.ToUpperInvariant((char)code).ToString();
        if (code <= 0 || EngineFn.SdlGetKeyName == null) return "";
        byte* p = EngineFn.SdlGetKeyName(code);
        return p == null ? "" : new string((sbyte*)p);
    }
}
