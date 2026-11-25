using CX.Framework;
using CX.Ui;

namespace CX.Services.Update;

/// <summary>One launch's release notice: asks the release source once, if the player agreed, and raises the gear's
/// badge for a newer signed release the player has neither opened About for nor muted.</summary>
internal sealed class UpdateNotice
{
    /// <summary>The gear's tooltip while the question is unanswered.</summary>
    public const string AskTip = "Should CX tell you about updates?";

    /// <summary>The gear's tooltip for a newer release, naming it.</summary>
    public static string NewTip(string version) => "An update is available: " + version;

    /// <summary>The notice and its signature, beside each release's assets.</summary>
    public const string ManifestUrl = Product.Url + "/latest/download/cx-update", SignatureUrl = ManifestUrl + ".sig";

    private readonly UpdateKnobs _k;
    private readonly string _running;
    private readonly string _os;
    private readonly ExtenderLog _log;
    private readonly ReleaseKey[] _keys;
    private readonly Func<string, byte[]?> _fetch;
    private readonly Func<string, bool> _copy;
    private readonly GearAttention _gear;

    public UpdateNotice(UpdateKnobs k, string running, string os, ReleaseKey[] keys, ExtenderLog log, Func<string, byte[]?> fetch,
                        Func<string, bool> copy, GearAttention gear)
    {
        _k = k;
        _running = running;
        _os = os;
        _log = log;
        _keys = keys;
        _fetch = fetch;
        _copy = copy;
        _gear = gear;
        k.Owner = this;
    }

    /// <summary>The newer release this launch found, or null.</summary>
    public UpdateManifest? Notice { get; private set; }

    /// <summary>The found release's zip for this install's OS and its SHA-256, or null when the notice names none.</summary>
    public (string Zip, string Sum)? Sum
    {
        get
        {
            if (Notice is not { } n) return null;
            string zip = $"cx-{_os}-x64-{n.Version}.zip";
            return n.Sums.TryGetValue(zip, out string? s) ? (zip, s) : null;
        }
    }

    /// <summary>Ask the source, judge what it returns, and publish the badge. Blocking: off the game thread.</summary>
    public void Check()
    {
        ConsentChanged();
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
        if (_k.Found.V != m.Version) _k.Found.Set(m.Version);
        if (!Beyond(m.Version, _k.Muted.V)) return;
        Notice = m;
        if (Beyond(m.Version, _k.Seen.V)) _gear.Raise(this, NewTip(m.Version), _k.Group);
    }

    /// <summary>The player answered the question: an unanswered one asks on the gear, any answer takes it down.</summary>
    public void ConsentChanged()
    {
        if (_k.Consent.V == UpdateKnobs.Ask) _gear.Raise(this, AskTip, _k.Group);
        else _gear.Clear(this);
    }

    /// <summary>Copy the releases page's address.</summary>
    public void Copy() => _copy(Product.Url);

    /// <summary>Copy the SHA-256 of this OS's zip, when the notice names it.</summary>
    public void CopySum() { if (Sum is { } s) _copy(s.Sum); }

    /// <summary>Stop telling the player about the release this launch found.</summary>
    public void Mute()
    {
        if (Notice is not { } n) return;
        _k.Muted.Set(n.Version);
        Notice = null; _gear.Clear(this);
    }

    /// <summary>The player opened About with a release to tell about: the badge stays down until a newer one.</summary>
    public void Viewed()
    {
        if (Notice is not { } n) return;
        _gear.Clear(this); if (_k.Seen.V != n.Version) _k.Seen.Set(n.Version);
    }

    /// <summary>Whether <paramref name="v"/> is past the release a mark (seen, muted) names; an empty mark is passed by all.</summary>
    private static bool Beyond(string v, string mark) => UpdateManifest.Parse3(mark) == null || Newer(v, mark);

    /// <summary>Whether <paramref name="found"/>'s X.Y.Z is past <paramref name="running"/>'s. A version either side
    /// cannot read (a build with no tag) is never newer, so it raises nothing.</summary>
    public static bool Newer(string found, string running)
        => UpdateManifest.Parse3(found) is { } f && UpdateManifest.Parse3(running) is { } r && f.CompareTo(r) > 0;

    /// <summary>cx.log's first line: the running build, and a newer release a previous session found.</summary>
    public static string BuildLine(string running, string? found)
        => "NWNCX:EE build " + running + (found != null && Newer(found, running) ? $" ({found} is out)" : "");

    /// <summary>The crash report's line about a newer release a previous session found, or empty.</summary>
    public static string CrashLine(string running, string? found)
        => found != null && Newer(found, running) ? $"A newer release, {found}, is out: {Product.Url}\n" : "";
}
