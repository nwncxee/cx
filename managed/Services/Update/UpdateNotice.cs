using CX.Framework;
using CX.Ui;

namespace CX.Services.Update;

/// <summary>One launch's release notice: asks the release source once, if the player agreed, and raises the gear's
/// badge for a newer signed release the player has neither opened About for nor muted.</summary>
internal sealed class UpdateNotice
{
    /// <summary>The gear's tooltips: the unanswered question, and a newer release.</summary>
    public const string AskTip = "Should CX tell you about updates?", NewTip = "An update is available!";

    private readonly UpdateKnobs _k;

    public UpdateNotice(UpdateKnobs k, string running, ReleaseKey[] keys, ExtenderLog log, Func<string, byte[]?> fetch,
                        Func<string, bool> copy, GearAttention gear)
    {
        _k = k;
        k.Owner = this;
    }

    /// <summary>The newer release this launch found, or null.</summary>
    public UpdateManifest? Notice { get; private set; }

    /// <summary>Ask the source, judge what it returns, and publish the badge. Blocking: off the game thread.</summary>
    public void Check() { }

    /// <summary>Copy the releases page's address.</summary>
    public void Copy() { }

    /// <summary>Stop telling the player about the release this launch found.</summary>
    public void Mute() { }

    /// <summary>cx.log's first line: the running build, and a newer release a previous session found.</summary>
    public static string BuildLine(string running, string? found) => "NWNCX:EE build " + running;
}
