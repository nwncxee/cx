using CX.Framework;
using CX.Ui;

namespace CX.Services.Hotkeys;

/// <summary>
/// The <c>hotkeys</c> group (spec §4.5): the live master that gates capture, <c>Emit</c> and the consent
/// prompt -- never the hooks (<c>LiveMessage</c> is shared plumbing) and never the <c>CX_ENABLED</c> advertise,
/// so inbound PresentToken/ArmSet keep being consumed and re-enabling is instant -- plus the action rows that
/// replaced the nuklear manager. Touched from <see cref="HotkeyInputService.Initialize"/> so it declares at boot.
/// </summary>
internal static class Hotkeys
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled;
    internal static readonly KnobButton Bind, Revoke, Forget, Consent;
    internal static readonly Knob<bool> KeyDiag;
    private static ExtenderLog? _log;

    static Hotkeys()
    {
        G = KnobGroup.Declare("hotkeys", "Controls", "Hotkeys", order: 540, master: "Enable server hotkeys",
            masterHelp: "Lets a server bind keys to its own actions, after you approve each one. Off, no key is sent "
                      + "and no approval is asked; keys reach the game only.",
            masterChanged: OnMaster);
        Enabled = G.Master!;
        G.Status = Status;
        G.PanelStatus = Status;
        Bind = G.Button("bind", "Bind keys on this server…", OpenServerUi,
            help: "Opens the server's own key-binding window. Greyed outside a server session.");
        Bind.Enabled = () => GameGate.InModule;
        Revoke = G.Button("revoke", "Revoke every approved key", RevokeAll, confirm: true,
            help: "Forgets every key you approved on this server and tells the server to unbind them. Click again to confirm.");
        Forget = G.Button("forget", "Forget every server", ForgetAll, confirm: true,
            help: "Wipes the approvals stored for every server you ever played on; the one you are on now is told to "
                + "unbind too. Each server asks again the next time it binds a key. Click again to confirm.");
        KeyDiag = G.Toggle("keydiag", "keydiag", false, Tier.ChatOnly, changed: () => UiInputService.KeyDiag = KeyDiag!.V);
        UiInputService.KeyDiag = KeyDiag.V;
        Consent = G.Button("consent", "consent", () => _log?.Info(HotkeyConsent.Probe()), Tier.ChatOnly);
    }

    internal static void Init(ExtenderLog log) => _log = log;

    /// <summary>Off drops an open consent modal's answer (fail-closed, the same <see cref="HotkeyConsent.Reset"/> a login does).</summary>
    private static void OnMaster()
    {
        if (!Enabled.V) HotkeyConsent.Reset();
    }

    /// <summary>Which keys this server may use -- the player's own question, so it is the panel's line as well as
    /// the chat command's.</summary>
    private static string Status()
    {
        var keys = HotkeyStore.Consented;
        if (!GameGate.InModule || keys.Count == 0) return "Approved on this server: none";
        var names = new List<string>(keys.Count);
        foreach (int code in keys) names.Add(HotkeyConsent.KeyName(code));
        return "Approved on this server: " + string.Join(", ", names);
    }

    /// <summary>The OpenUi packet the Options-panel button used to send. Guarded: with no session there is no live
    /// message object to send it through.</summary>
    private static unsafe void OpenServerUi()
    {
        if (!GameGate.InModule) return;
        ClientApi.Send(HotkeyProtocol.OpenUiMajor, HotkeyProtocol.OpenUiMinor, null, 0);
    }

    /// <summary>One Revoke carrying every consented key, then the active section emptied.</summary>
    private static void RevokeAll()
    {
        var all = HotkeyStore.Consented;
        if (all.Count == 0) return;
        HotkeyConsent.Revoke(all);
        HotkeyStore.Set(new HashSet<int>(), _log!);
        _log?.Info($"Hotkeys: revoked all {all.Count} approved key(s)");
    }

    /// <summary>Today's Revoke-all for the server in session, then every token section wiped (no packet: by the
    /// fail-closed rule, a server never told is a server that re-asks).</summary>
    private static void ForgetAll()
    {
        RevokeAll();
        HotkeyStore.Forget(_log!);
    }
}
