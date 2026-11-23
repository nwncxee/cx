namespace CX.Services.Update;

/// <summary>A release-signing key: the principal an allowed-signers file names it by, and its Ed25519 public half.</summary>
internal readonly record struct ReleaseKey(string Name, byte[] Public);

/// <summary>The keys a release notice may be signed with, read from allowed-signers text.</summary>
internal static class ReleaseKeys
{
    /// <summary>Each <c>name ssh-ed25519 base64</c> line of <paramref name="text"/>; any other line is skipped.</summary>
    public static ReleaseKey[] Parse(string text) => [];
}

/// <summary>Checks an OpenSSH signature (<c>ssh-keygen -Y sign</c>) over a message against a list of keys.</summary>
internal static class SshSig
{
    /// <summary>The namespace release notices are signed under.</summary>
    public const string Namespace = "cx-update";

    /// <summary>The name of the key in <paramref name="keys"/> that signed <paramref name="message"/> under
    /// <see cref="Namespace"/>, or null with <paramref name="why"/> saying what failed.</summary>
    public static string? Verify(ReadOnlySpan<byte> message, string armored, ReleaseKey[] keys, out string why)
    {
        why = "not checked";
        return null;
    }
}
