using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Hotkeys;

/// <summary>
/// Client-owned consent gate: a server ArmSet that ADDS keys raises the engine's own native yes/no modal
/// (CPanelOkCancel, as "quit to main menu" uses). The client draws and owns it, so a server can neither forge
/// nor auto-confirm it -- only OK arms the keys, Esc/Cancel/close deny. The panel self-frees through its
/// deleting dtor, so it MUST come from the engine's operator new (the game's own on Windows -- static CRT).
/// </summary>
internal static unsafe class HotkeyConsent
{
    // OK / Cancel button label strrefs -- the exact pair HandleQuitClick passes to Activate.
    private const uint OkStrRef = 0x2052, CancelStrRef = 0x2053;  // not-an-offset: a TLK strref

    private static ExtenderLog? _log;
    private static IntPtr _guiManSym;                    // &g_pGuiMan -- read its value, never deref as an object
    private static bool _ready;

    // The keys awaiting the player's answer. Non-null == a modal is currently open.
    private static HashSet<int>? _pending;
    // /hotkeys consent probe open: the answer is logged, never granted/revoked (no cfg write, no packet).
    private static bool _probe;

    public static void Init(ExtenderLog log)
    {
        _log = log;
        _guiManSym = Native.Sym(SymbolMap.GuiMan);

        _ready = EngineFn.OperatorNew != null && EngineFn.OkCancelCtor != null && EngineFn.OkCancelActivate != null
                 && _guiManSym != IntPtr.Zero
                 && EngineLayout.Require(log, "HotkeyConsent", "CPanelOkCancel", "CGuiPanel", "CGuiCallback");
        if (!_ready)
            _log?.Info("HotkeyConsent: native modal unavailable on this client -- new binds will be refused");
    }

    /// <summary>Called on login and on the master's off edge; fail-closed -- any pending (unconfirmed) keys are
    /// dropped, never armed: an open modal's answer lands on a null <c>_pending</c> and is ignored.</summary>
    public static void Reset() { _pending = null; _probe = false; }

    /// <summary>The <c>/hotkeys consent</c> body: spawn the modal with a fake key set so the whole native round
    /// trip (operator new, ctor, Activate, on-close callback, self-free) runs without a server. The answer is
    /// only logged. The one place the port touches the engine allocator, so it gets its own probe.</summary>
    public static string Probe()
    {
        var fake = new HashSet<int> { 'f' | HotkeyProtocol.ModCtrl, HotkeyProtocol.MouseBase + 3 };
        if (!Prompt(fake)) return "[consent] modal not shown -- see the log";
        _probe = true;
        return "[consent] modal up: answer it, then read the log";
    }

    /// <summary>
    /// Ask the player to consent to arming `additions`. Returns true only if the modal was shown.
    /// Every false path (missing symbol, not in-game, a prompt already open) means "not armed"; the
    /// caller must not arm on false.
    /// </summary>
    public static bool Prompt(HashSet<int> additions)
    {
        if (!_ready || additions.Count == 0) return false;
        if (!Hotkeys.Enabled.V)
        {
            _log?.Info("HotkeyConsent: hotkeys.enabled is off; no approval asked, proposal dropped");
            return false;
        }
        if (_pending != null)
        {
            _log?.Info("HotkeyConsent: a consent prompt is already open; dropping this proposal");
            return false;
        }
        if (!GuiReady())
        {
            _log?.Info("HotkeyConsent: GUI not up yet; refusing bind proposal");
            return false;
        }

        IntPtr panel = EngineFn.OperatorNew((nuint)EngineLayout.OkCancelSize);
        if (panel == IntPtr.Zero) return false;
        EngineFn.OkCancelCtor(panel);                    // seeds the callback; ours overwrites it after
        CGuiCallback onClose = new CGuiPanel(panel).CloseCallback;
        onClose.Obj = panel;                             // benign non-null context; our callback ignores it
        // Left raw: a callback pointer stored INTO an engine struct field, tied to no symbol.
        onClose.Fn = (IntPtr)(delegate* unmanaged<IntPtr, int, void>)&OnAnswer;
        onClose.Adj = IntPtr.Zero;                       // Itanium invokes fn(obj + adj, ...); a no-op on MSVC

        IntPtr msg = Native.NewExoString(Describe(additions));
        EngineFn.OkCancelActivate(panel, msg, OkStrRef, CancelStrRef);   // Linux overload copies the text; does not own msg
        Native.FreeExoString(msg);

        _pending = additions;
        _log?.Info($"HotkeyConsent: prompting to arm {additions.Count} key(s)");
        return true;
    }

    /// <summary>
    /// Is the GUI manager up? g_pGuiMan is the exact global CGuiModalPanel::Activate uses, so non-null
    /// == safe to attach a modal. We only READ its value (never deref as an object), so a wrong
    /// resolution can't fault.
    /// </summary>
    private static bool GuiReady() => *(IntPtr*)_guiManSym != IntPtr.Zero;

    // The engine's on-close callback: 1 = OK, anything else = deny. Clear _pending FIRST -- the panel
    // self-frees right after this returns, and a new proposal must be able to prompt again.
    [UnmanagedCallersOnly]
    private static void OnAnswer(IntPtr context, int result)
    {
        var answered = _pending;
        _pending = null;
        // The engine's modal leaves keyboard mode ON after it closes, which eats the next Enter;
        // release it unless one of our windows is typing.
        if (!CX.Ui.UiHost.AnyKeyboardOwner) CX.Ui.UiHost.KeyboardGate?.Invoke(false);
        if (answered == null) return;
        if (_probe)
        {
            _probe = false;
            _log?.Info($"HotkeyConsent: probe answered result={result} ({(result == 1 ? "OK" : "deny")}); nothing granted");
            return;
        }
        if (result == 1)
        {
            // Record consent under this server's token (minting one if none), then hand the token +
            // consented keys back so the server can present it next login.
            string token = HotkeyStore.Grant(answered, _log!);
            SendGrantToken(token);
        }
        else
        {
            // Deny -> tell the server to forget the binding, or it re-pushes these every login and
            // re-prompts forever. The client revoking consent it never granted.
            Revoke(answered);
            _log?.Info("HotkeyConsent: player declined; revoked on the server");
        }
    }

    /// <summary>Send a Revoke for `keys` so the server drops the binding. Used by deny and by client-initiated unbind.</summary>
    public static void Revoke(IEnumerable<int> keys)
    {
        string csv = string.Join(",", keys);
        if (csv.Length == 0) return;
        ClientApi.SendAscii(HotkeyProtocol.RevokeMajor, HotkeyProtocol.RevokeMinor, csv);
    }

    // Hand the server the consent receipt: the token this consent is stored under + the consented
    // keys. The server persists it per player and presents it back next login (PresentToken).
    private static void SendGrantToken(string tokenHex)
    {
        string csv = string.Join(",", HotkeyStore.Consented);
        ClientApi.SendAscii(HotkeyProtocol.GrantTokenMajor, HotkeyProtocol.GrantTokenMinor, $"{tokenHex};{csv}");
    }

    private static string Describe(HashSet<int> keys)
    {
        var names = new List<string>(keys.Count);
        foreach (int code in keys) names.Add(KeyName(code));
        return "This server wants permission to track these keys as hotkeys:\n\n"
             + string.Join(", ", names)
             + "\n\nAllow?";
    }

    /// <summary>
    /// Human-readable label. SDLK codes go through KeyNames.Name; mouse codes (MouseBase + button)
    /// and modifier prefixes are ours. The consent prompt is a security surface -- it MUST read
    /// "Ctrl+F", not "key ?", so the player knows what they are allowing.
    /// </summary>
    internal static string KeyName(int code)
    {
        int mods = code & HotkeyProtocol.ModMask;
        if (mods != 0)
        {
            string prefix = "";
            if ((mods & HotkeyProtocol.ModCtrl) != 0) prefix += "Ctrl+";
            if ((mods & HotkeyProtocol.ModShift) != 0) prefix += "Shift+";
            if ((mods & HotkeyProtocol.ModAlt) != 0) prefix += "Alt+";
            return prefix + KeyName(code & ~HotkeyProtocol.ModMask);
        }
        // Bound the mouse range: button is a byte, but SDLK scancode keys (bit 30) are also >= MouseBase,
        // so an unbounded check would mislabel every F-key/arrow "Mouse".
        if (code >= HotkeyProtocol.MouseBase && code <= HotkeyProtocol.MouseBase + 0xFF)
        {
            int btn = code - HotkeyProtocol.MouseBase;   // SDL button index
            return btn switch
            {
                1 => "Mouse Left",
                2 => "Mouse Middle",
                3 => "Mouse Right",
                4 => "Mouse X1",
                5 => "Mouse X2",
                _ => $"Mouse {btn}",
            };
        }
        string s = KeyNames.Name(code);
        return s.Length > 0 ? s : $"key {code}";
    }
}
