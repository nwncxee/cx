using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Hotkeys;

/// <summary>
/// Announces the extender to the server on login via NWN:EE's own capability channel
/// (SendPlayerToServerDeviceAdvertiseProperty), then sends the abuse-report contact as an ASCII device
/// message. A vanilla client never advertises, so the server treats it as un-extended. Advertises
/// CX_ENABLED=1, the version and the live capabilities (<see cref="Capabilities"/>), never the armed key list (consent is per-server); the master gates actions, not this.
/// </summary>
public sealed unsafe class HotkeyHandshakeService : IExtenderService
{
    private static ExtenderLog? _log;
    private static HandleServerToPlayerLoginFn _loginOrig;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        Capabilities.SelfCheck(log);
        if (!SymbolMap.Require(log, "HotkeyHandshakeService", HotkeyProtocol.RequiredSymbols)) return;
        _loginOrig = hooks.InstallHandleServerToPlayerLogin(&OnLogin);
    }

    [UnmanagedCallersOnly]
    private static uint OnLogin(IntPtr msg, byte subcmd)
    {
        ClientApi.LiveMessage = msg;                        // live CNWCMessage; cache for outbound sends
        uint r = _loginOrig != null ? _loginOrig(msg, subcmd) : 1;
        if (subcmd == 5)                                    // 5 = login accepted / entering game
        {
            // Fresh session: drop any prior server's active consent and clear half-answered
            // prompts. Nothing is armed until the server presents a recognised token.
            HotkeyStore.BeginSession();
            HotkeyConsent.Reset();
            ServerPolicy.Clear();                           // a policy is the server's that sent it
            Advertise(msg);
        }
        return r;
    }

    private static void Advertise(IntPtr msg)
    {
        if (EngineFn.SendDeviceAdvertiseProperty == null) return;
        Adv(msg, "CX_ENABLED", 1);
        Capabilities.Advertise((n, v) => Adv(ClientApi.LiveMessage, n, v));
        ClientApi.SendAscii(HotkeyProtocol.AbuseContactMajor, HotkeyProtocol.AbuseContactMinor, HotkeyProtocol.AbuseContact);
        _log?.Info("Handshake: advertised extender + abuse contact (no key list -- consent is per-server via token)");
    }

    private static void Adv(IntPtr msg, string name, int value)
    {
        if (msg == IntPtr.Zero || EngineFn.SendDeviceAdvertiseProperty == null) return;
        IntPtr s = Native.NewExoString(name);
        EngineFn.SendDeviceAdvertiseProperty(msg, s, value);
        Native.FreeExoString(s);
    }
}
