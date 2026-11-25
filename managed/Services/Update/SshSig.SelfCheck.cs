using System.Text;
using CX.Framework;
using static CX.Services.Update.UpdateNoticeApprovedExamples;

namespace CX.Services.Update;

/// <summary>The signature check and the notice parser on their own. Oracle: signatures ssh-keygen made over the
/// fixture notices with throwaway keys, and the fixtures' own text.</summary>
internal static class SshSigSelfCheck
{
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "SshSig.SelfCheck");
        ReleaseKey[] keys = Keys();
        check.Expect(keys.Length == 2 && keys[0].Name == "fixture-current" && keys[1].Name == "fixture-next", $"two keys read ({keys.Length})");
        check.Expect(ReleaseKeys.Parse("# a comment\nx ssh-rsa AAAA\ny ssh-ed25519 !!notbase64\n").Length == 0, "comments, other key kinds and bad base64 are skipped");

        string? Who(string manifest, out string why) => SshSig.Verify(Fixture(manifest), Encoding.UTF8.GetString(Fixture(manifest + ".sig")), keys, out why);
        check.Expect(Who("url.txt", out _) == "fixture-current", "the current key's signature is taken");
        check.Expect(Who("next.txt", out _) == "fixture-next", "the next key's signature is taken");
        check.Expect(Who("forged.txt", out string why) == null && why.Contains("does not know", StringComparison.Ordinal), $"a stranger's key is refused ({why})");
        check.Expect(Who("wrongns.txt", out why) == null && why.Contains("signed for 'file'", StringComparison.Ordinal), $"another namespace is refused ({why})");

        byte[] tampered = Fixture("url.txt");
        tampered[^2] ^= 1;
        string sig = Encoding.UTF8.GetString(Fixture("url.txt.sig"));
        check.Expect(SshSig.Verify(tampered, sig, keys, out why) == null && why.Contains("does not match", StringComparison.Ordinal), $"a changed byte is refused ({why})");
        check.Expect(SshSig.Verify(Fixture("url.txt"), sig, [keys[1]], out why) == null, "a known signer missing from the list is refused");
        int cut = sig.IndexOf("-----END", StringComparison.Ordinal);
        string truncated = sig[..(cut - 30)] + "\n" + sig[cut..];
        check.Expect(SshSig.Verify(Fixture("url.txt"), truncated, keys, out why) == null, $"a truncated signature is refused ({why})");
        byte[] blob = Unarmor(sig);
        check.Expect(SshSig.Verify(Fixture("url.txt"), Armor(blob[..^10]), keys, out why) == null, $"a short blob is refused ({why})");
        check.Expect(SshSig.Verify(Fixture("url.txt"), Armor([.. blob, 0, 0, 0, 0]), keys, out why) == null && why == "malformed signature", $"trailing bytes are refused ({why})");
        byte[] magic = (byte[])blob.Clone();
        magic[0] = (byte)'X';
        check.Expect(SshSig.Verify(Fixture("url.txt"), Armor(magic), keys, out why) == null && why.Contains("SSHSIG", StringComparison.Ordinal), $"another magic is refused ({why})");
        check.Expect(Who("sha256.txt", out why) == null && why.Contains("is not sha512", StringComparison.Ordinal), $"a SHA-256 signature is refused ({why})");
        string current = Encoding.UTF8.GetString(Fixture("allowed_signers")).Split('\n')[0].Split(' ')[2];
        check.Expect(ReleaseKeys.Parse("x ssh-rsa " + current).Length == 0, "an Ed25519 blob under another kind's name is skipped");
        string[] prints = [.. ReleaseKeys.Parse(Encoding.UTF8.GetString(Fixture("allowed_signers"))).Select(k => k.Fingerprint)];
        check.Expect(prints.SequenceEqual(new[] { "SHA256:oeiAe2Ez3GXZrj5b9d5ADo7NOYqSrBHTxTZlK0a8JlM", "SHA256:GErPRaZGR8m3+ocu5WbM+vSEagS5QKpf2E+B5BSwWgA" }),
                     $"each key's fingerprint is the text ssh-keygen -lf prints for it ({string.Join(", ", prints)})");
        check.Expect(SshSig.Verify(Fixture("url.txt"), "", keys, out why) == null && why == "not an SSH signature", $"no armor is refused ({why})");

        UpdateManifest? m = UpdateManifest.Parse(Encoding.UTF8.GetString(Fixture("v0140.txt")));
        check.Expect(m != null && m.Version == "v0.14.0" && m.Changes.SequenceEqual(new[] { "Shadows soften at dusk.", "The chat window remembers its size." }),
                     $"v0140.txt reads as its version and two lines ({m?.Version}, {m?.Changes.Length})");
        check.Expect(UpdateManifest.Parse("cx-update 1\nfuture thing\nversion v1.2.3\r\n") is { Version: "v1.2.3", Changes.Length: 0 }, "unknown lines and CRs are skipped");
        check.Expect(UpdateManifest.Parse("cx-update 1\nversion v1.2.3\nversion v9.9.9\n")?.Version == "v1.2.3", "the first version line is the one");
        check.Expect(UpdateManifest.Parse("version v1.2.3\n") == null && UpdateManifest.Parse("x\nversion v1.2.3\n") == null, "no header, no notice");
        check.Expect(UpdateManifest.Parse("cx-update 1\nversion v1.2\n") == null, "a version that is not X.Y.Z is no notice");
        check.Expect(UpdateManifest.Parse3("v0.13.11-3-g27e6276a") == (0, 13, 11) && UpdateManifest.Parse3("1.2.3") == (1, 2, 3), "a describe string reads as its X.Y.Z");
        check.Expect(UpdateManifest.Parse3("v1.-2.3") == null && UpdateManifest.Parse3("vx.2.3") == null && UpdateManifest.Parse3("v1.2.3.4") == null,
                     "a non-numeric part or a fourth part is refused");

        log.Info(check.Ok ? "SshSig.SelfCheck: passed" : "SshSig.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>The blob an armored signature carries.</summary>
    private static byte[] Unarmor(string armored)
        => Convert.FromBase64String(string.Concat(armored.Split('\n').Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal))));

    /// <summary>A blob armored the way ssh-keygen writes it.</summary>
    private static string Armor(byte[] blob) => "-----BEGIN SSH SIGNATURE-----\n" + Convert.ToBase64String(blob) + "\n-----END SSH SIGNATURE-----\n";
}
