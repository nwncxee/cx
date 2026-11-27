using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Services.Chat;
using CX.Ui;

namespace CX.Services.Hotkeys;

/// <summary>
/// The runtime pipeline: a bound key press -> a debounced packet to the server's toolbar system. The framework
/// input layer (<c>UiInputService</c>) owns raw capture and every environmental gate; this service is a tier-2
/// chord consumer that maps a fallen-through key or button to its wire code and does consent + debounce + send.
/// With the CX chatbox off it also observes the vanilla chat prompt's focus hooks. Cold hooks, may allocate.
/// </summary>
public sealed unsafe class HotkeyInputService : IExtenderService
{
    /// <summary>Per-key rate limit (keyed by code, so different hotkeys never throttle each other).</summary>
    private const long DebounceMs = 250;

    private static ExtenderLog? _log;
    private static volatile bool _typing;   // the vanilla chat prompt has focus (chat master off); input thread
    private static readonly Dictionary<int, long> _lastSent = new();

    // SDL_Keymod bits. Ctrl/Shift take both sides; Alt is LALT ONLY -- RALT is AltGr (accented
    // chars; on Windows AltGr = LCTRL+RALT), so binding it would hijack normal typing.
    // ponytail: expose RAlt only if someone genuinely wants an AltGr chord.
    private const int KmodShift = 0x0003;   // not-an-offset: SDL key/event code; LSHIFT | RSHIFT
    private const int KmodCtrl  = 0x00C0;   // not-an-offset: SDL key/event code; LCTRL | RCTRL
    private const int KmodLAlt  = 0x0100;   // not-an-offset: SDL key/event code; LALT only

    // Vanilla chat-focus trampolines (chat master off only).
    private static SetFocusChatPromptFn _chatOnOrig;
    private static ChatHandleLMouseDownFn _clickOrig;
    private static ChatHandleEscFn _escOrig;
    private static ChatHandleLoseFocusFn _loseOrig;
    /// <summary>Chain slot on the prompt's Enter: PromptCommandService shares the symbol below us, so this layer
    /// observes at a higher priority and always calls through; <c>*_enterSlot</c> is the next layer or the original.</summary>
    private static ChatHandleEnterFn* _enterSlot;
    /// <summary>Above <c>PromptCommandService.EnterPriority</c> (0): the typing flag clears on every Enter, whatever the layer below does.</summary>
    private const int EnterObserverPriority = 10;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        Hotkeys.Init(log);   // declares the group (and applies hotkeys.keydiag) whether or not the unit arms below
        // No HotkeyStore.Load here: our loader runs pre-main, so the engine user dir (where the cfg lives)
        // isn't resolvable yet. The store lazy-loads on the first login-path call. SelfCheck is in-memory.
        HotkeyStore.SelfCheck(log);   // before the symbol gate, so the stub host runs it too
        if (!SymbolMap.Require(log, "HotkeyInputService", HotkeyProtocol.RequiredSymbols)) return;

        // the framework input layer owns raw capture. Hotkeys are a chord consumer at tier 2 -- fired only
        // when no window consumed the key and the chord gate is clear (the arbiter's job). No raw hooks here.
        UiHost.AddChordConsumer(Chord);
        log.Info("HotkeyInputService: registered as a chord consumer (framework owns raw capture)");
        // Vanilla-prompt typing observer. With the chatbox up ChatService SKIPS the chat-focus openers
        // (suppressing vanilla) and owns the keyboard through the arbiter, so these must NOT also install
        // (funchook single-hooks). With the master off we observe the same hooks (forward to the trampoline)
        // to track _typing, so vanilla chat keeps working and hotkeys still withhold while typing.
        if (!ChatService.Enabled)
        {
            _chatOnOrig = hooks.InstallSetFocusChatPrompt(&OnChatOn);
            _clickOrig  = hooks.InstallChatHandleLMouseDown(&OnChatClick);
            _enterSlot  = hooks.ChainChatHandleEnter(&OnChatEnter, EnterObserverPriority);
            _escOrig    = hooks.InstallChatHandleEsc(&OnChatEsc);
            _loseOrig   = hooks.InstallChatHandleLoseFocus(&OnChatLose);
        }
    }

    /// <summary>Map a raw SDL_Keymod bitset to the HotkeyProtocol.Mod* wire encoding.</summary>
    private static int ModsFrom(int m)
    {
        int bits = 0;
        if ((m & KmodCtrl)  != 0) bits |= HotkeyProtocol.ModCtrl;
        if ((m & KmodShift) != 0) bits |= HotkeyProtocol.ModShift;
        if ((m & KmodLAlt)  != 0) bits |= HotkeyProtocol.ModAlt;
        return bits;
    }

    /// <summary>Hotkeys' one chord consumer, registered once the symbols resolve.</summary>
    internal static readonly IChordConsumer Chord = new Consumer();

    /// <summary>The chord-consumer face of hotkeys (the default path). The framework input layer owns raw
    /// capture, precedence, the auto-repeat gate, and every environmental gate; this consumer only maps a
    /// fallen-through key/button to its wire code and does the consent + debounce + send. Tier 2: below a
    /// window's focused content, above the engine's gameplay keybinds.</summary>
    private sealed class Consumer : IChordConsumer
    {
        public int Priority => 2;

        /// <summary>Arbiter query: true when some armed code shares this base key, a mouse button asked as key -button
        /// matching its mouse code. Off master: nothing is wanted, the key reaches the game only.</summary>
        public bool WantsKey(int key)
        {
            if (!Hotkeys.Enabled.V) return false;
            int wire = key < 0 ? HotkeyProtocol.MouseBase - key : key;
            foreach (int code in HotkeyStore.Armed)
                if ((code & ~HotkeyProtocol.ModMask) == wire) return true;
            return false;
        }

        public void Fire(uint key, int sdlMods) => Emit((int)key | ModsFrom(sdlMods));
        public void FireMouse(int button, int sdlMods) => Emit(HotkeyProtocol.MouseBase + button | ModsFrom(sdlMods));
    }

    /// <summary>The master, consent (armed), the vanilla-prompt typing gate, per-key debounce, send. The repeat
    /// gate and the other env gates are the framework's; this is the send tail of the consumer path.</summary>
    private static void Emit(int code)
    {
        if (!Hotkeys.Enabled.V || !HotkeyStore.Armed.Contains(code) || _typing) return;
        long now = Environment.TickCount64;
        _lastSent.TryGetValue(code, out long last);
        if (now - last < DebounceMs) return;
        _lastSent[code] = now;
        Send(code);
    }

    private static void Send(int code)
    {
        _log?.Info($"HotkeyInput: sending code {code} to server");
        ClientApi.SendAscii(HotkeyProtocol.KeyPressMajor, HotkeyProtocol.KeyPressMinor, code.ToString());
    }

    // Vanilla-prompt typing gate (chat master off only). Two entries (chat opened by key, or clicked into),
    // three exits (Enter/Esc/click-away). We forward to the trampoline -- vanilla chat runs normally; we only
    // track _typing to withhold hotkeys.
    [UnmanagedCallersOnly] private static void OnChatOn(IntPtr self)   { _typing = true;  if (_chatOnOrig != null) _chatOnOrig(self); }
    [UnmanagedCallersOnly] private static void OnChatClick(IntPtr self){ _typing = true;  if (_clickOrig  != null) _clickOrig(self); }
    [UnmanagedCallersOnly] private static void OnChatEnter(IntPtr self){ _typing = false; if (_enterSlot != null) (*_enterSlot)(self); }
    [UnmanagedCallersOnly] private static void OnChatEsc(IntPtr self)  { _typing = false; if (_escOrig    != null) _escOrig(self); }
    [UnmanagedCallersOnly] private static void OnChatLose(IntPtr self) { _typing = false; if (_loseOrig   != null) _loseOrig(self); }
}
