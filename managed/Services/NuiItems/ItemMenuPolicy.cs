using CX.Framework;

namespace CX.Services.NuiItems;

/// <summary>
/// The session's item menu: the one <see cref="ItemMenuModel"/> the <c>menu.</c> policy family feeds, and the lines it
/// refused, listed by <c>/policy</c>.
/// </summary>
internal static class ItemMenuPolicy
{
    /// <summary>The <see cref="ServerPolicy"/> prefix every menu key carries.</summary>
    public const string Prefix = "menu.";

    /// <summary>The menu the server has open, if any.</summary>
    public static readonly ItemMenuModel Menu = new();

    /// <summary>Hooks the menu into <see cref="ServerPolicy"/>.</summary>
    public static void Init() => ServerPolicy.Handle(Prefix, Set, Release, Menu.Report);

    /// <summary>One <c>menu.&lt;key&gt;</c> line; an empty value releases it. False when refused.</summary>
    public static bool Set(string key, string value)
    {
        string? why = Menu.Set(key, value);
        if (why == null) return true;
        PolicyRefusals.Add(RefusalKind.Held, Prefix + key, value, why);
        return false;
    }

    /// <summary>A wildcard release or a new session closes the menu whole: a half-released menu would offer rows the server retired.</summary>
    public static void Release(string prefix) => Menu.Close();
}
