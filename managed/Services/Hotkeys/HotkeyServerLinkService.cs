using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Hotkeys;

/// <summary>
/// Receives the armed-key set from the server (a server-authored NUI arms keys; the server
/// sends the result here). Hooks the client's inbound dispatcher and intercepts only our
/// ArmSet/PresentToken opcodes -- every other message passes straight through. Not gated by
/// <c>hotkeys.enabled</c>: the set is consumed either way, so flipping the master back on needs no relog.
/// </summary>
public sealed unsafe class HotkeyServerLinkService : IExtenderService
{
    private static ExtenderLog? _log;
    private static HandleServerToPlayerMessageFn _inboundOrig;

    /// <summary>
    /// A server asking to track an implausible number of keys is refused wholesale, to bound a
    /// pathological payload -- NOT to limit the player (additions require consent regardless). The
    /// real ceiling is one key per toolbar button (~80 offered), so 256 is generous headroom.
    /// </summary>
    private const int MaxArmed = 256;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!SymbolMap.Require(log, "HotkeyServerLinkService", HotkeyProtocol.RequiredSymbols)) return;
        HotkeyConsent.Init(_log!);
        SelfCheck();   // the fail-closed consent policy is security-critical; verify it at startup
        ServerPolicy.Log = log;
        ServerPolicy.Send = (minor, ascii) => ClientApi.SendAscii(ServerPolicy.DeviceMajor, minor, ascii);
        CommandRegistry.Global.Register("/policy", "list what the server's UI policy holds", _ => ServerPolicy.Report());
        _inboundOrig = hooks.InstallHandleServerToPlayerMessage(&OnInbound);
    }

    // The engine frames every server->client message as ['P'][major][minor][payload...]
    // (decomp-verified: HandleServerToPlayerMessage gates on data[0]=='P', reads major/minor at
    // [1]/[2]). So we match past the 'P' marker, not from data[0].
    private const byte FrameMarker = (byte)'P';

    [UnmanagedCallersOnly]
    private static uint OnInbound(IntPtr self, byte* data, uint size)
    {
        ClientApi.LiveMessage = self;   // the live CNWCMessage; cache for outbound sends (g_pAppManager segfaults here)

        if (size >= 3 && data[0] == FrameMarker && data[1] == HotkeyProtocol.ArmSetMajor)  // 0x36: our device major
        {
            switch (data[2])
            {
                case HotkeyProtocol.PresentTokenMinor:   // restore this server's prior consent (sent before ArmSet)
                    HandlePresentToken(data, size);
                    return 1;                            // consume: ours, the stock client must not see it
                case HotkeyProtocol.ArmSetMinor:
                    HandleArmSet(data, size);
                    return 1;
                case ServerPolicy.PolicyMinor:
                    if (size - 3 <= int.MaxValue) ServerPolicy.Apply(System.Text.Encoding.UTF8.GetString(data + 3, (int)(size - 3)));
                    return 1;
            }
        }
        return _inboundOrig != null ? _inboundOrig(self, data, size) : 1;
    }

    /// <summary>
    /// Apply a proposed armed set. The set is a PROPOSAL measured against what the player has CONSENTED to,
    /// not against what is currently captured: capture shrinks silently, but a server asking for fewer keys
    /// never un-consents the rest -- it is the same server across a character swap, and a character with no
    /// bindings taken as a revocation wipes the stored consent and re-prompts on the way back.
    /// </summary>
    private static void HandleArmSet(byte* data, uint size)
    {
        var proposed = Parse(data, size);

        var (rejected, capture, additions) = Plan(HotkeyStore.Consented, proposed, MaxArmed);
        if (rejected)
        {
            _log?.Info($"ServerLink: refusing oversized arm set ({proposed.Count} > {MaxArmed})");
            return;
        }

        HotkeyStore.Capture(capture);               // consented keys only; session state, never the file
        if (additions.Count > 0)                    // new keys -- ask the player before arming any of them
            HotkeyConsent.Prompt(additions);
    }

    /// <summary>
    /// Pure consent policy, isolated so <see cref="SelfCheck"/> can prove the fail-closed defaults.
    /// rejected = over cap (arm nothing); capture = what may be armed right now (proposed keys the
    /// player has consented to); additions = proposed keys not yet consented (these need consent).
    /// </summary>
    private static (bool rejected, HashSet<int> capture, HashSet<int> additions) Plan(
        HashSet<int> consented, HashSet<int> proposed, int cap)
    {
        if (proposed.Count > cap) return (true, new HashSet<int>(), new HashSet<int>());

        var capture = new HashSet<int>(proposed);
        capture.IntersectWith(consented);           // never capture a key the player did not allow
        var additions = new HashSet<int>(proposed);
        additions.ExceptWith(consented);            // proposed minus what is already consented

        return (false, capture, additions);
    }

    /// <summary>
    /// The server presents the token it was granted last time (payload = hex). Recognised -> the
    /// armed set becomes that server's consented keys, so the following ArmSet produces no additions
    /// and no re-prompt. Unrecognised / empty -> armed set stays empty, awaiting consent.
    /// </summary>
    private static void HandlePresentToken(byte* data, uint size)
    {
        string tok = AsciiPayload(data, size);
        if (tok.Length == 0) { HotkeyStore.BeginSession(); return; }   // server holds no token -> nothing to restore
        HotkeyStore.Activate(tok, _log!);
    }

    private static HashSet<int> Parse(byte* data, uint size)
    {
        var set = new HashSet<int>();
        foreach (string part in AsciiPayload(data, size).Split(new[] { ',', ';', ' ', '\n', '\r', '\t' },
                                                               StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part, out int vk)) set.Add(vk);
        return set;
    }

    /// <summary>The ASCII payload after the 3-byte ['P'][major][minor] header, trimmed.</summary>
    private static string AsciiPayload(byte* data, uint size)
    {
        if (size <= 3) return "";
        int len = (int)size - 3;
        var bytes = new byte[len];
        Marshal.Copy((IntPtr)(data + 3), bytes, 0, len);
        return System.Text.Encoding.ASCII.GetString(bytes).Trim();
    }

    /// <summary>
    /// One runnable check on the security-critical bits: additions never sneak through, the cap
    /// rejects wholesale, and a pure add never reports a removal. Logs loudly if broken.
    /// </summary>
    private static void SelfCheck()
    {
        var consented = new HashSet<int> { 1, 2, 3 };

        // pure addition: 4 needs consent, the rest capture as before
        var (r1, cap1, add1) = Plan(consented, new HashSet<int> { 1, 2, 3, 4 }, MaxArmed);
        bool ok = !r1 && cap1.SetEquals(new HashSet<int> { 1, 2, 3 }) && add1.SetEquals(new HashSet<int> { 4 });

        // removal + addition: 3 stops being captured, 9 needs consent
        var (r2, cap2, add2) = Plan(consented, new HashSet<int> { 1, 2, 9 }, MaxArmed);
        ok &= !r2 && cap2.SetEquals(new HashSet<int> { 1, 2 }) && add2.SetEquals(new HashSet<int> { 9 });

        // a character with no bindings captures nothing and asks for nothing -- consent survives
        var (r4, cap4, add4) = Plan(consented, new HashSet<int>(), MaxArmed);
        ok &= !r4 && cap4.Count == 0 && add4.Count == 0;

        // over cap: reject wholesale, arm nothing
        var (r3, _, add3) = Plan(consented, new HashSet<int> { 1, 2, 3, 4, 5 }, 4);
        ok &= r3 && add3.Count == 0;

        _log?.Info(ok ? "ServerLink: consent policy self-check passed"
                      : "ServerLink: CONSENT POLICY SELF-CHECK FAILED -- arming logic is unsafe");
    }
}
