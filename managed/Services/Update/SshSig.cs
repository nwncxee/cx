using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using CX.Framework;

namespace CX.Services.Update;

/// <summary>A release-signing key: the principal an allowed-signers file names it by, its Ed25519 public half, and its
/// fingerprint as <c>ssh-keygen</c> prints it (<c>SHA256:</c> and the unpadded base64 of its wire blob's SHA-256).</summary>
internal readonly record struct ReleaseKey(string Name, byte[] Public, string Fingerprint);

/// <summary>The keys a release notice may be signed with, read from allowed-signers text.</summary>
internal static class ReleaseKeys
{
    /// <summary>The one key kind release keys and signatures are made with.</summary>
    internal const string Ed25519 = "ssh-ed25519";

    /// <summary>Each <c>name ssh-ed25519 base64</c> line of <paramref name="text"/>; any other line is skipped.</summary>
    public static ReleaseKey[] Parse(string text)
    {
        var keys = new List<ReleaseKey>();
        foreach (string raw in text.Split('\n'))
        {
            string[] f = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 3 || f[0].StartsWith('#') || f[1] != Ed25519) continue;
            byte[] blob;
            try { blob = Convert.FromBase64String(f[2]); } catch (FormatException) { continue; }
            var r = new SshReader(blob);
            if (r.Text() == Ed25519 && r.Bytes() is { Length: 32 } pk && r.Done)
                keys.Add(new ReleaseKey(f[0], pk, "SHA256:" + Convert.ToBase64String(Sha256.HashData(blob)).TrimEnd('=')));
        }
        return [.. keys];
    }
}

/// <summary>Checks an OpenSSH signature (<c>ssh-keygen -Y sign</c>) over a message against a list of keys.</summary>
internal static unsafe class SshSig
{
    /// <summary>The namespace release notices are signed under.</summary>
    public const string Namespace = "cx-update";
    private const string Armor = "SSH SIGNATURE";

    [DllImport("cxsig")]
    private static extern void crypto_sha512(byte* hash, byte* message, nuint size);
    [DllImport("cxsig")]
    private static extern int crypto_ed25519_check(byte* signature, byte* publicKey, byte* message, nuint size);

    /// <summary>The name of the key in <paramref name="keys"/> that signed <paramref name="message"/> under
    /// <see cref="Namespace"/>, or null with <paramref name="why"/> saying what failed.</summary>
    public static string? Verify(ReadOnlySpan<byte> message, string armored, ReleaseKey[] keys, out string why)
    {
        byte[]? blob = Unarmor(armored);
        if (blob == null) { why = "not an SSH signature"; return null; }
        var r = new SshReader(blob);
        if (!r.Magic() || r.U32() != 1) { why = "not an SSHSIG v1 blob"; return null; }
        var pub = new SshReader(r.Bytes() ?? []);
        string? ns = r.Text(), reserved = r.Text(), hash = r.Text();
        var sig = new SshReader(r.Bytes() ?? []);
        if (pub.Text() != ReleaseKeys.Ed25519 || pub.Bytes() is not { } pk) { why = "not an Ed25519 signature"; return null; }
        if (ns != Namespace) { why = $"signed for '{ns}', not '{Namespace}'"; return null; }
        if (hash != "sha512") { why = $"hash '{hash}' is not sha512"; return null; }
        if (sig.Text() != ReleaseKeys.Ed25519 || sig.Bytes() is not { Length: 64 } s || !r.Done) { why = "malformed signature"; return null; }
        ReleaseKey? key = null;
        foreach (ReleaseKey k in keys) if (k.Public.AsSpan().SequenceEqual(pk)) { key = k; break; }
        if (key == null) { why = "signed by a key CX does not know"; return null; }
        byte[] signed = Signed(message, reserved ?? "");
        fixed (byte* sp = s, kp = pk, mp = signed)
            if (crypto_ed25519_check(sp, kp, mp, (nuint)signed.Length) != 0) { why = "the signature does not match"; return null; }
        why = "";
        return key.Value.Name;
    }

    /// <summary>What the key signs: the magic, the namespace, the reserved field, the hash name and the message's SHA-512.</summary>
    private static byte[] Signed(ReadOnlySpan<byte> message, string reserved)
    {
        byte[] digest = new byte[64];
        fixed (byte* d = digest, m = message) crypto_sha512(d, m, (nuint)message.Length);
        var w = new MemoryStream();
        w.Write("SSHSIG"u8);
        Put(w, Encoding.ASCII.GetBytes(Namespace));
        Put(w, Encoding.ASCII.GetBytes(reserved));
        Put(w, "sha512"u8.ToArray());
        Put(w, digest);
        return w.ToArray();
    }

    private static void Put(MemoryStream w, byte[] b)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)b.Length);
        w.Write(len);
        w.Write(b);
    }

    /// <summary>The bytes between the armor lines, or null when the text is not armored base64.</summary>
    private static byte[]? Unarmor(string armored)
    {
        int a = armored.IndexOf("-----BEGIN " + Armor + "-----", StringComparison.Ordinal);
        int b = armored.IndexOf("-----END " + Armor + "-----", StringComparison.Ordinal);
        if (a < 0 || b < a) return null;
        int start = a + Armor.Length + 16;
        try { return Convert.FromBase64String(armored[start..b].Replace("\n", "").Replace("\r", "")); }
        catch (FormatException) { return null; }
    }
}

/// <summary>Reads the SSH wire format: big-endian lengths, length-prefixed strings. A short read yields null and
/// leaves the reader failed, so a truncated blob never reads as a shorter valid one.</summary>
internal ref struct SshReader(ReadOnlySpan<byte> data)
{
    private ReadOnlySpan<byte> _d = data;
    private bool _bad;

    /// <summary>Every byte was read and none was missing.</summary>
    public readonly bool Done => !_bad && _d.IsEmpty;

    public bool Magic()
    {
        if (_d.Length < 6 || !_d[..6].SequenceEqual("SSHSIG"u8)) { _bad = true; return false; }
        _d = _d[6..];
        return true;
    }

    public uint U32()
    {
        if (_d.Length < 4) { _bad = true; return 0; }
        uint v = BinaryPrimitives.ReadUInt32BigEndian(_d);
        _d = _d[4..];
        return v;
    }

    public byte[]? Bytes()
    {
        uint n = U32();
        if (_bad || n > (uint)_d.Length) { _bad = true; return null; }
        byte[] b = _d[..(int)n].ToArray();
        _d = _d[(int)n..];
        return b;
    }

    public string? Text() => Bytes() is { } b ? Encoding.UTF8.GetString(b) : null;
}
