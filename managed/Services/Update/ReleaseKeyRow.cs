using CX.Framework;

namespace CX.Services.Update;

/// <summary>About's release key row: the fingerprint of each key this build trusts, and a button that copies the
/// build's <c>allowed_signers</c> text for checking a download by hand.</summary>
internal sealed class ReleaseKeyRow
{
    public readonly KnobButton Copy;

    private ReleaseKeyRow(KnobGroup g, string allowedSigners, Func<string, bool> copy)
    {
        Copy = g.Button("releasekey", "Copy release key", () => { }, help: "Copies the release key this install trusts.");
    }

    /// <summary>Declare the row on <paramref name="g"/> over the build's <paramref name="allowedSigners"/> text.</summary>
    public static ReleaseKeyRow Declare(KnobGroup g, string allowedSigners, Func<string, bool> copy) => new(g, allowedSigners, copy);
}
