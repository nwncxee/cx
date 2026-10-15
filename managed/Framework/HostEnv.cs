namespace CX.Framework;

/// <summary>The only environment variables the extender reads, all the host's own. A setting is a knob, never a
/// variable (ADR 0025); <c>CX_MANAGED</c> is the bootstrap's, set before any managed code runs.</summary>
public static class HostEnv
{
    /// <summary>The user's home directory, or <c>/tmp</c> when the host sets none.</summary>
#pragma warning disable RS0030 // HostEnv is the one reader of the environment
    public static string Home => Environment.GetEnvironmentVariable("HOME") ?? "/tmp";

    /// <summary>The XDG data directory: <c>XDG_DATA_HOME</c> when set, else <c>~/.local/share</c>.</summary>
    public static string DataHome => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
        ? x : System.IO.Path.Combine(Home, ".local", "share");

    /// <summary>The path the native bootstrap loaded <c>cx_managed</c> from, or null outside the launcher.</summary>
    public static string? ManagedLibrary => Environment.GetEnvironmentVariable("CX_MANAGED");
#pragma warning restore RS0030
}
