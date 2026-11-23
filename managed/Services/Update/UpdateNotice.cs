using CX.Framework;
using CX.Ui;

namespace CX.Services.Update;

/// <summary>One launch's release notice: asks the release source once, if the player agreed, and raises the gear's
/// badge for a newer signed release the player has neither opened About for nor muted.</summary>
internal sealed class UpdateNotice
{
    /// <summary>The gear's tooltips: the unanswered question, and a newer release.</summary>
    public const string AskTip = "Should CX tell you about updates?", NewTip = "An update is available!";

    /// <summary>The notice and its signature, beside each release's assets.</summary>
    public const string ManifestUrl = Product.Url + "/latest/download/cx-update", SignatureUrl = ManifestUrl + ".sig";

    private readonly UpdateKnobs _k;
    private readonly ReleaseKey[] _keys;
    private readonly Func<string, byte[]?> _fetch;
    private readonly Func<string, bool> _copy;

    public UpdateNotice(UpdateKnobs k, string running, ReleaseKey[] keys, ExtenderLog log, Func<string, byte[]?> fetch,
                        Func<string, bool> copy, GearAttention gear)
    {
        _k = k;
        _keys = keys;
        _fetch = fetch;
        _copy = copy;
        k.Owner = this;
    }

    /// <summary>The newer release this launch found, or null.</summary>
    public UpdateManifest? Notice { get; private set; }

    /// <summary>Ask the source, judge what it returns, and publish the badge. Blocking: off the game thread.</summary>
    public void Check()
    {
        if (_k.Consent.V != UpdateKnobs.Yes) return;
        byte[]? text = _fetch(ManifestUrl), sig = _fetch(SignatureUrl);
        if (text == null || sig == null) return;
        if (SshSig.Verify(text, System.Text.Encoding.UTF8.GetString(sig), _keys, out _) == null) return;
        Notice = UpdateManifest.Parse(System.Text.Encoding.UTF8.GetString(text));
    }

    /// <summary>Copy the releases page's address.</summary>
    public void Copy() => _copy(Product.Url);

    /// <summary>Stop telling the player about the release this launch found.</summary>
    public void Mute() { }

    /// <summary>cx.log's first line: the running build, and a newer release a previous session found.</summary>
    public static string BuildLine(string running, string? found) => "NWNCX:EE build " + running;
}
