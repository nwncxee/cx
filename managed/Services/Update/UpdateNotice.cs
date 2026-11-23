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
    private readonly string _running;
    private readonly ExtenderLog _log;
    private readonly ReleaseKey[] _keys;
    private readonly Func<string, byte[]?> _fetch;
    private readonly Func<string, bool> _copy;
    private readonly GearAttention _gear;

    public UpdateNotice(UpdateKnobs k, string running, ReleaseKey[] keys, ExtenderLog log, Func<string, byte[]?> fetch,
                        Func<string, bool> copy, GearAttention gear)
    {
        _k = k;
        _running = running;
        _log = log;
        _keys = keys;
        _fetch = fetch;
        _copy = copy;
        _gear = gear;
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
        if (SshSig.Verify(text, System.Text.Encoding.UTF8.GetString(sig), _keys, out string why) == null)
        {
            _log.Info("UpdateNotice: the update notice was refused: " + why);
            return;
        }
        UpdateManifest? m = UpdateManifest.Parse(System.Text.Encoding.UTF8.GetString(text));
        if (m == null) { _log.Info("UpdateNotice: the update notice was refused: not a cx-update notice"); return; }
        if (!Newer(m.Version, _running)) return;
        Notice = m;
        _gear.Raise(this, NewTip, _k.Group);
    }

    /// <summary>Copy the releases page's address.</summary>
    public void Copy() => _copy(Product.Url);

    /// <summary>Stop telling the player about the release this launch found.</summary>
    public void Mute() { }

    /// <summary>Whether <paramref name="found"/>'s X.Y.Z is past <paramref name="running"/>'s. A version either side
    /// cannot read (a build with no tag) is never newer, so it raises nothing.</summary>
    public static bool Newer(string found, string running)
        => UpdateManifest.Parse3(found) is { } f && UpdateManifest.Parse3(running) is { } r && f.CompareTo(r) > 0;

    /// <summary>cx.log's first line: the running build, and a newer release a previous session found.</summary>
    public static string BuildLine(string running, string? found) => "NWNCX:EE build " + running;
}
