using System;

namespace CX.Framework;

/// <summary>What the engine-free core asks the engine, and the user directory it falls back to. The host wires the
/// engine's answers at boot; unwired, each answers as it does before the engine exists.</summary>
public static class EngineLink
{
    /// <summary>The client's own resolved user directory, or null before the engine sets it up or headless.</summary>
    internal static Func<string?> UserDirectory { get; private set; } = static () => null;

    /// <summary>The path an engine alias (no colon) maps to, or null before the engine has loaded its aliases.</summary>
    internal static Func<string, string?> AliasPath { get; private set; } = static _ => null;

    /// <summary>Is this process the game client, not a stub host?</summary>
    internal static Func<bool> InClient { get; private set; } = static () => false;

    /// <summary>Hand the core the engine's answers. Called once, first thing in the host's boot.</summary>
    internal static void Wire(Func<string?> userDirectory, Func<string, string?> aliasPath, Func<bool> inClient)
    {
        UserDirectory = userDirectory;
        AliasPath = aliasPath;
        InClient = inClient;
    }

    /// <summary>
    /// Absolute path for a CX file in the client's user directory (next to nwn.ini/settings.tml): a setting, or a folder
    /// whose default the game keeps there.
    /// Falls back to <see cref="DefaultUserDirectory"/> when the engine dir is unresolvable -- pre-main, or
    /// headless under run-demo.
    /// </summary>
    public static string ConfigPath(string fileName) =>
#pragma warning disable RS0030 // a settings file before the engine names its user directory reads the default one
        System.IO.Path.Combine(UserDirectory() ?? DefaultUserDirectory(), fileName);
#pragma warning restore RS0030

    /// <summary>
    /// Where the client would put its user directory with no <c>-userdirectory</c> override: SDL's pref path,
    /// <c>$XDG_DATA_HOME</c> or <c>~/.local/share</c>, plus the app name -- read off the client's own SDL block
    /// rather than assumed, so it agrees with <see cref="UserDirectory"/>. This is the PRE-MAIN answer every boot
    /// knob is read through, and the launch line is askable there, so <c>-userdirectory</c> still wins.
    /// </summary>
    public static string DefaultUserDirectory()
    {
        string? launched = LaunchUserDirectory();
        if (launched != null) return launched;
        if (OperatingSystem.IsWindows())
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Neverwinter Nights");
        return System.IO.Path.Combine(HostEnv.DataHome, "Neverwinter Nights");
    }

    /// <summary>The <c>-userdirectory</c> the client was launched with, or null. Read from the command line, the one
    /// thing available before the engine has resolved anything: without it a client launched with the switch reads
    /// its BOOT knobs from the default directory while the engine uses the one it was given. Case-insensitive and
    /// LAST occurrence wins, as the engine's own <c>SetUpUserDirectory</c> parse is; a valueless switch is ignored.</summary>
    private static string? LaunchUserDirectory()
    {
        if (_launchDirTried) return _launchDir;
        _launchDirTried = true;
        string[] args;
        try { args = Environment.GetCommandLineArgs(); } catch { return null; }
        for (int i = args.Length - 2; i >= 0; i--)
            if (string.Equals(args[i], "-userdirectory", StringComparison.OrdinalIgnoreCase)
                && args[i + 1].Length > 0)
            { _launchDir = args[i + 1]; break; }
        return _launchDir;
    }

    /// <inheritdoc cref="LaunchUserDirectory"/>
    private static string? _launchDir;
    /// <inheritdoc cref="LaunchUserDirectory"/>
    private static bool _launchDirTried;
}
