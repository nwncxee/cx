using System;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Panels;

/// <summary>
/// A colour as a declaration writes it: a literal <c>#RRGGBB[AA]</c>, or a <see cref="Role"/> the player's look decides
/// (<c>@accent</c>, <c>@panelbg/f2</c>). Reading it as a <c>uint</c> resolves a role against the current theme, so a look
/// change re-colours it; the read allocates nothing. <c>default</c> is the unset colour, which means "inherit".
/// </summary>
internal readonly struct Paint
{
    /// <summary>A literal colour as 0xRRGGBBAA, or the alpha byte alone when a role is named.</summary>
    private readonly uint _v;
    /// <summary>The named role's index + 1, so 0 names none and <c>default</c> is unset.</summary>
    private readonly ushort _role;

    private Paint(uint v, ushort role) { _v = v; _role = role; }

    /// <summary>The colour 0xRRGGBBAA, as written.</summary>
    public static Paint Literal(uint rgba) => new(rgba, 0);

    /// <summary>Whatever the player's look resolves <paramref name="role"/> to, at <paramref name="alpha"/>.</summary>
    public static Paint Of(Role role, byte alpha = 0xFF) => new(alpha, (ushort)((int)role + 1));

    /// <summary>True once a colour was written; the unset colour inherits instead of drawing.</summary>
    public bool Set => _role != 0 || _v != 0;

    /// <summary>True when a role was named rather than a literal colour.</summary>
    public bool IsRole => _role != 0;

    /// <summary>The role named; meaningless unless <see cref="IsRole"/>.</summary>
    public Role Role => (Role)(_role - 1);

    /// <summary>The opacity, whether the colour is literal or a role.</summary>
    public byte Alpha => (byte)_v;

    /// <summary>The same literal colour or role at <paramref name="alpha"/>.</summary>
    public Paint WithAlpha(byte alpha) => new((_v & RgbMask) | alpha, _role);

    /// <summary>Everything but the alpha byte of a literal colour.</summary>
    private const uint RgbMask = 0xFFFFFF00u;  // not-an-offset: the RGB channels of an RRGGBBAA colour

    /// <summary>The colour to draw now: a role resolved against the current theme, a literal as written.</summary>
    public static implicit operator uint(Paint p) => p._role == 0 ? p._v : Theme.Rgba(p.Role, p.Alpha);

    /// <summary>True, and <paramref name="p"/> set, for <c>#RRGGBB</c>, <c>#RRGGBBAA</c>, a role (<c>@accent</c>) or a role
    /// with hex opacity (<c>@accent/80</c>). Anything else, an unknown role included, is false.</summary>
    public static bool TryParse(string? s, out Paint p)
    {
        p = default;
        if (s is null || s.Length == 0) return false;
        if (s[0] != '@')
        {
            if (!HexColour.TryParse(s, out uint rgba)) return false;
            p = Literal(rgba);
            return true;
        }
        int slash = s.IndexOf('/');
        byte a = 0xFF;
        if (slash >= 0)
        {
            if (s.Length - slash != 3) return false;
            int hi = HexColour.Digit(s[slash + 1]), lo = HexColour.Digit(s[slash + 2]);
            if (hi < 0 || lo < 0) return false;
            a = (byte)(hi << 4 | lo);
        }
        if (!Theme.TryRoleName(s.AsSpan(0, slash < 0 ? s.Length : slash), out Role role)) return false;
        p = Of(role, a);
        return true;
    }
}
