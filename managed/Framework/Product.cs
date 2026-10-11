using System.Reflection;

namespace CX.Framework;

/// <summary>The one place the product names itself (spec §4.1 About): what <c>/about</c> and the About submenu
/// print, and the address the hotkey handshake advertises (<c>HotkeyProtocol.AbuseContact</c>).</summary>
public static class Product
{
    public const string Name = "NWNCX:EE";
    public const string Author = "Michał Piasecki";
    public const string Contact = "mpiasecki720@protonmail.com";
    public const string Url = "https://github.com/nwncxee/cx/releases";
    /// <summary>Where a crash with the extender loaded is reported (<c>CrashReport</c>).</summary>
    public const string Issues = "https://github.com/nwncxee/cx/issues";

    /// <summary><c>AssemblyInformationalVersion</c>: what <c>build.sh</c> stamps from <c>git describe</c>.</summary>
    public static readonly string Version = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>The About status lines: name + version, author, contact. The releases URL is the group's link
    /// row, which the bare command prints beneath these (spec §4.1's four lines).</summary>
    public static string About() => $"{Name} {Version}\nauthor: {Author}\ncontact: {Contact}";
}
