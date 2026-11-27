using System;
using CX.Engine;

namespace CX.Framework;

/// <summary>The one parser of a stored key name: "F", "]" or "Mouse4" mean the same press wherever a setting binds
/// them, read through SDL's own key-name table.</summary>
internal static unsafe class KeyNames
{
    /// <summary>The press code for <paramref name="name"/>: an SDL key code, -1 to -5 for Mouse1 to Mouse5, a modifier by its
    /// SDL name, 0 for blank or a name SDL does not know. Off the client a one-character name, and Space, read as SDL would read them. Cold: a setting's edit.</summary>
    internal static int Code(string? name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return 0;
        int modifier = Array.FindIndex(Modifiers, m => m.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (modifier >= 0) return FirstModifier + modifier;
        if (name.StartsWith("mouse", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(5), out int btn) && btn is >= 1 and <= 5)
            return -btn;
        // SDL answers a one-character name with that character, but a key event's sym is the LOWER-case letter.
        if (name.Length == 1) name = name.ToLowerInvariant();
#pragma warning disable RS0030 // KeyNames.Code is the one reader of SDL's key-name table
        var fromName = EngineFn.SdlGetKeyFromName;
#pragma warning restore RS0030
        if (fromName == null)
            return name.Length == 1 && name[0] is > ' ' and < (char)0x7f ? name[0] : name.Equals("Space", StringComparison.OrdinalIgnoreCase) ? ' ' : 0;
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = utf8) return fromName(p);
    }

    /// <summary>The eight modifier keys as SDL names them, by code from <see cref="FirstModifier"/> on, named here so a
    /// modifier reads and parses the same off the client.</summary>
    private static readonly string[] Modifiers = { "Left Ctrl", "Left Shift", "Left Alt", "Left GUI", "Right Ctrl", "Right Shift", "Right Alt", "Right GUI" };
    /// <summary>SDL's code for Left Ctrl, the first modifier key.</summary>
    private const int FirstModifier = 0x400000E0;  // not-an-offset: SDL key code

    /// <summary>Whether <paramref name="code"/> is one of the eight modifier keys, Shift, Ctrl, Alt or GUI, left or right.</summary>
    internal static bool IsModifier(long code) => code >= FirstModifier && code < FirstModifier + Modifiers.Length;

    /// <summary>Whether <paramref name="name"/> is press <paramref name="code"/>: a text key setting's
    /// <see cref="Knob.BindsKey"/>.</summary>
    internal static bool Binds(string name, int code) => Code(name) == code;

    /// <summary>The name a setting stores for press <paramref name="code"/>, the one <see cref="Code"/> reads back: a
    /// printable key as its upper-case character, Mouse1 to Mouse5 for -1 to -5, a modifier as SDL names it, else SDL's name. "" when SDL has none.</summary>
    internal static string Name(int code)
    {
        if (code is >= -5 and <= -1) return "Mouse" + (-code).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (IsModifier(code)) return Modifiers[code - FirstModifier];
        if (code is > ' ' and < 0x7f) return char.ToUpperInvariant((char)code).ToString();
#pragma warning disable RS0030 // KeyNames.Name is the one writer from SDL's key-name table
        var toName = EngineFn.SdlGetKeyName;
#pragma warning restore RS0030
        if (code <= 0) return "";
        if (toName == null) return code == ' ' ? "Space" : "";
        byte* p = toName(code);
        return p == null ? "" : new string((sbyte*)p);
    }
}
