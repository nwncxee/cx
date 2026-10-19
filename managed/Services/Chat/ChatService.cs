using System;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Services.Chat.Render;
using CX.Ui;

namespace CX.Services.Chat;

/// <summary>
/// Native-C# chatbox: one <see cref="IExtenderService"/> whose concerns live in pure Core
/// classes (<c>Chat/Core/</c>, unit-testable, no native refs) behind this thin shell of
/// hooks/GL/SDL. State is static because the <c>[UnmanagedCallersOnly]</c> handlers must be.
/// Coexists with <c>HotkeyInputService</c>; does not absorb it.
/// </summary>
public sealed unsafe partial class ChatService : IExtenderService
{
    private static ExtenderLog? _log;

    /// <summary>Shared arbitration flag: <c>HotkeyInputService</c> reads this to
    /// suppress gameplay hotkeys while the chat edit control owns the keyboard.
    /// Only ever set true with the chatbox up (<see cref="Enabled"/>).</summary>
    public static volatile bool IsTyping;

    /// <summary>The keyboard is wanted by chat typing OR an inline settings-field edit. HotkeyInputService
    /// routes keys/text into ChatService and suppresses gameplay hotkeys whenever this is true.</summary>
    public static bool WantsKey => IsTyping || _settings.Editing;

    /// <summary>The chat master, as it booted (<c>chat.enabled</c>, a Restart row -- ADR 0025). Off, the chatbox
    /// installs nothing: no tap, render, input capture or suppression, so the stock chat window runs untouched
    /// and HotkeyInputService observes the vanilla chat-focus hooks instead. The one pivot both services read.</summary>
    public static bool Enabled => ChatKnobs.Enabled.Boot;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        UiCategory.Declare();
        ChatWindow.SelfCheck(log);     // drag/resize/tab/scroll proof (off-device)
        ChatLayout.SelfCheck(log);     // Main-undeletable / always->=1-tab / add-close window proof (off-device)
        SettingsWindow.SelfCheck(log); // settings window open/toggle/close/ownership proof (off-device)
        TellTarget.SelfCheck(log);     // tell tab send-decision proof (off-device)
        TellRouter.SelfCheck(log);     // tell auto-open routing proof (off-device)
        WindowRenderer.SelfCheck(log); // portrait-gutter emission proof (off-device)
        InputMarkup.SelfCheck(log);      // emote/speech/OOC classification of the line being typed (off-device)
        InputStripLayout.SelfCheck(log); // the input strip's one geometry: hit-test, rows, counter reserve (off-device)
        RowAlign.SelfCheck(log);       // shared row alignment/justify primitive proof (off-device)
        TextWrapper.SelfCheck(log);    // pixel-width word wrap, hard break, one advance per rune (off-device)
        ChatAnim.SelfCheck(log);       // fade/slide/scroll math proof (off-device)
        ChatConfig.SelfCheck(log);     // per-window pair round-trip proof (off-device)
        TextRig.SelfCheck(log);   // session-clear drops cached engine texture pointers (off-device)
        FontRegistry.SelfCheck(log);
        EditBuffer.SelfCheck(log);     // the input line's editing contract (off-device)
        ChatCommands.SelfCheck(log);   // slash-command grammar
        TypingSignal.SelfCheck(log);   // chat-bar focus/unfocus latch (off-device)
        Snippets.SelfCheck(log);       // chat shortcut rows
        NameCompleter.SelfCheck(log);  // Tab name completion
        HistoryFile.SelfCheck(log);    // saved sent-line history escaping
        SpeechNotify.SelfCheck(log);   // which lines raise a desktop notification
        string[] names = FontRegistry.Names;   // "default" first: the window's own entry stands in for it
        var faces = new string[names.Length];
        faces[0] = SettingsWindow.FollowUi;
        Array.Copy(names, 1, faces, 1, names.Length - 1);
        SettingsWindow.Typefaces = faces;
        if (!Enabled)   // chat.enabled=0 at boot: install nothing -> the stock chat window runs
        {
            log.Info("ChatService: chat.enabled=0 -- standing down, vanilla chat left intact (/chat enabled 1 + relaunch)");
            return;
        }
        if (!SymbolMap.Require(log, "ChatService", RequiredSymbols)) return;   // whole or nothing
        InitMessageCore(log, hooks);   // the inbound tap
        InitRender(log, hooks);        // own SwapBuffers -> draw the window (bakes the atlas)
        InitSuppression(log, hooks);   // neutralize vanilla chat rendering
        InitEditControl(log, hooks);   // the keyboard side
        _ = SymbolMap.Require(log, "Chat desktop notifications", SymbolMap.SdlGetKeyboardFocus);   // unresolved: never notifies
    }

    /// <summary>The chat unit: every symbol the chatbox cannot run without -- ingest tap, vanilla
    /// suppression, openers, senders, name lookups, SDL text/clipboard, the glyph bake and the GL loader. One
    /// unresolved member stands the whole feature down (a half-live chatbox is worse than vanilla). Optional and
    /// NOT listed: AurTextureGet (portraits), GetModule (game-time stamps fall back to wall clock).</summary>
    private static readonly string[] RequiredSymbols =
    {
        SymbolMap.AppendToMsgBuffer, SymbolMap.AppendToChatBuffer, SymbolMap.ChatWindowSetFilter,
        SymbolMap.ChatWindowShow, SymbolMap.ChatPromptShow, SymbolMap.SetFocusChatPrompt, SymbolMap.ShowChatPrompt,
        SymbolMap.PrepChatPromptForTell, SymbolMap.ChatPromptSetFocus, SymbolMap.ChatPromptCtor, SymbolMap.GuiPanelDeactivate,
        SymbolMap.SendChatTalk, SymbolMap.SendChatShout, SymbolMap.SendChatWhisper, SymbolMap.SendChatParty,
        SymbolMap.SendChatDM, SymbolMap.SendChatTell, SymbolMap.SendChatTellId, SymbolMap.NotifyServerGuiEvent,
        SymbolMap.GetPlayerByPlayerName, SymbolMap.GetPlayerByCreatureName, SymbolMap.AppManager,
        SymbolMap.SwapBuffers,
        SymbolMap.SdlStartTextInput, SymbolMap.SdlGetClipboardText, SymbolMap.SdlSetClipboardText, SymbolMap.SdlFree,
        SymbolMap.SdlGlGetProcAddress, SymbolMap.StbttInitFont, SymbolMap.StbttScaleForPixelHeight,
        SymbolMap.StbttGetFontVMetrics, SymbolMap.StbttGetCodepointHMetrics, SymbolMap.StbttGetCodepointBitmapBox,
        SymbolMap.StbttMakeCodepointBitmap,
    };

    /// <summary>Backlog every ingested line lands in; tab views read from it.</summary>
    private static readonly ChatStore _store = new();

    private static long _ingestCount;

    /// <summary>Log the first line and every Nth after, so a live run leaves visible tap-&gt;store proof.</summary>
    private const int LogEveryNth = 50;

    /// <summary>16-byte CResRef by value. Only r9 is free when it's passed, so SysV puts the whole struct on the
    /// stack (r9 unused) and declaring it as a struct makes NativeAOT lower it that way; two ulongs instead split
    /// it r9+stack and corrupt the forwarded call. Win64 passes a 17-byte aggregate as a POINTER to a caller copy
    /// (sixth argument is a <c>CResRef*</c> whose 17th byte the callee copies), so the slot is an <c>IntPtr</c>.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct CResRef16 { public ulong Lo; public ulong Hi; }
    // Left raw: AppendToMsgBuffer's last parameter has a DIFFERENT shape per ABI, so no single
    // [Fn] signature describes it.
#if WINDOWS
    private static delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, IntPtr, void> _tapOrig;
#else
    // AppendToMsgBuffer(this, CExoString* text, uint channel, uint playerId, int hasPortrait, CResRef).
    private static delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, CResRef16, void> _tapOrig;
#endif

    /// <summary>Cold inbound tap on AppendToMsgBuffer -&gt; parse -&gt; ChatStore.Append.</summary>
    private static void InitMessageCore(ExtenderLog log, HookService hooks)
    {
        ColorRunParser.SelfCheck(log);
        ChatStore.SelfCheck(log);

#if WINDOWS
        _tapOrig = (delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, IntPtr, void>)hooks.Install(
            SymbolMap.AppendToMsgBuffer,
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, IntPtr, void>)&OnAppend);
#else
        _tapOrig = (delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, CResRef16, void>)hooks.Install(
            SymbolMap.AppendToMsgBuffer,
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, CResRef16, void>)&OnAppend);
#endif
        log.Info(_tapOrig != null
#if WINDOWS
            ? "ChatService: inbound tap installed (AppendToMsgBuffer, Win64 CResRef* ABI)"
#else
            ? "ChatService: inbound tap installed (AppendToMsgBuffer)"
#endif
            : "ChatService: inbound tap not installed (Install returned Zero: symbol unresolved or already hooked)");
    }

    [UnmanagedCallersOnly]
#if WINDOWS
    private static void OnAppend(IntPtr self, IntPtr text, uint channel, uint playerId,
                                 int hasPortrait, IntPtr portraitPtr)
    {
        CResRef16 portrait = portraitPtr != IntPtr.Zero ? *(CResRef16*)portraitPtr : default;
        string raw = Native.ReadExo(text);   // BEFORE the forward: the Win64 callee destroys its by-value CExoString
        if (_tapOrig != null) _tapOrig(self, text, channel, playerId, hasPortrait, portraitPtr); // vanilla chat still needs it
#else
    private static void OnAppend(IntPtr self, IntPtr text, uint channel, uint playerId,
                                 int hasPortrait, CResRef16 portrait)
    {
        string raw = Native.ReadExo(text);
        if (_tapOrig != null) _tapOrig(self, text, channel, playerId, hasPortrait, portrait); // vanilla chat still needs it
#endif
        try
        {
            string portraitRef = hasPortrait != 0 ? ResRef(portrait) : "";
            // Outbound Tell echoes: the server sends back a plain tell with speaker = self and no recipient
            // anywhere in it (nwserver SendServerToPlayerChatMessage case 4), so the name comes from what we sent.
            string tellRecip = (channel & 0x002) != 0 && _playerResolver?.LocalIngestId() == playerId
                ? TellTarget.TakeEcho(raw) : "";
            // ponytail: senderName "" -- the tap doesn't carry it (inline in raw). SenderName component is the ceiling.
            var line = new ChatLine(channel, "", playerId, portraitRef,
                ColorRunParser.Parse(raw), DateTime.UtcNow, raw, GameNow(), tellRecip);
            // Tells are rare + this path is cold: log every one unconditionally (throttle covers the rest).
            if ((channel & 0x002) != 0)
                _log?.Info($"[chat] tell ingest: pid={playerId} local={_playerResolver?.LocalIngestId()} recip=\"{tellRecip}\" raw={raw}");
            _store.Append(line);
            if (ChatKnobs.Notify.V
                && SpeechNotify.ShouldNotify(channel, playerId, _playerResolver?.LocalIngestId(), DesktopNotify.Background))
                DesktopNotify.Show(FilterChannel.Label(channel), SpeechNotify.Text(line.ColorRuns), _log);

            long n = ++_ingestCount;
            // One-shot thread-model probe: is ingest really a separate thread from the frame
            // present? If ingest tid == swap tid the "cold network thread" comments are wrong and the store
            // races don't exist. Compare the two [chatthread] lines in a live cx.log.
            if (n == 1)
            {
                int itid = Environment.CurrentManagedThreadId;
                _log?.Info($"[chatthread] ingest tid={itid} swap tid={_swapTid} same={(itid == _swapTid)}");
            }
            if (n == 1 || n % LogEveryNth == 0)
                _log?.Info($"[chat] ingest #{n} ch=0x{channel:x} pid={playerId} portrait={portraitRef} " +
                           $"runs={line.ColorRuns.Length} text={raw}");
        }
        // An escaped exception aborts the NativeAOT process (the live client), so swallow ingest
        // faults -- but log the first, or a parse/append bug is permanently silent (no ingest ever).
        catch (Exception e) { if (_faults++ == 0) _log?.Info($"[chat] ingest fault (swallowed): {e}"); }
    }

    private static long _faults;

    /// <summary>Decode a 16-byte CResRef (null-padded ASCII) to its resref string.</summary>
    private static string ResRef(CResRef16 r)
    {
        Span<byte> b = stackalloc byte[16];
        BitConverter.TryWriteBytes(b, r.Lo);
        BitConverter.TryWriteBytes(b.Slice(8), r.Hi);
        int n = 0;
        while (n < 16 && b[n] != 0) n++;
        return Encoding.ASCII.GetString(b.Slice(0, n));
    }


    /// <summary>The chat window layout: the ordered window list ChatService drives for draw,
    /// input, and persistence. Index 0 is the undeletable Main; <see cref="ChatLayout.FocusedIndex"/>
    /// tracks which window owns the keyboard + input strip.</summary>
    private static ChatLayout? _layout;

    /// <summary>The focused chat window (owns keyboard + input strip). Falls back to Main.</summary>
    private static ChatWindow? _focused => _layout?.Focused;

    /// <summary>The chat settings window (opened from the gear icon). Edits
    /// <see cref="ChatWindow.Toggles"/>; holds no option state of its own.</summary>
    private static readonly SettingsWindow _settings = new();

    /// <summary>Neutralize vanilla chat so only our box draws, never reached with the master off. SkipOriginal on
    /// the TEXT path (AppendToChatBuffer, SetFilter) loses nothing: AppendToMsgBuffer fills the shared ring and
    /// frees the by-value CExoString before they run. Chrome: Window/Prompt Show, never Hide -- Hide clears
    /// IsVisible (win+0xc68), so <see cref="InGame"/> gates on module presence. Openers: InitEditControl.</summary>
    private static void InitSuppression(ExtenderLog log, HookService hooks)
    {
        _ = hooks.InstallAppendToChatBuffer(&OnSuppressedExo);   // text: new lines
        if (OperatingSystem.IsWindows())
        {
            // MSVC inlined CGuiInGameChatPrompt::Show into the prompt's ctor, so the skip on Show alone
            // leaves the "Talk:" prompt activated from birth: undo it right after construction (= vanilla Hide).
            _promptCtorOrig = hooks.InstallChatPromptCtor(&OnPromptCtor);
            _panelDeactivate = EngineFn.GuiPanelDeactivate;
            // ponytail: CGuiInGameChatWindow::DoSplitScreen (0x879560) also inlines Show, so toggling the
            // split-chat option activates the docked window on Windows; hook it the same way if anyone trips it.
        }
        _ = hooks.InstallChatWindowSetFilter(&OnSuppressed);   // text: backlog re-render + reply buttons
        _ = hooks.InstallChatWindowShow(&OnSuppressed);        // chrome: docked message panels
        _ = hooks.InstallChatPromptShow(&OnSuppressed);        // chrome: the "Talk:" input prompt
        log.Info("ChatService: vanilla chat suppressed (text: AppendToChatBuffer+SetFilter; chrome: Window/Prompt Show)");
    }

    // Skip-original: install the hook, never call the trampoline, so the vanilla path is a no-op. (IntPtr
    // self) ignores any further args -- safe under SysV caller-cleanup since we forward nothing. Return 0:
    // the Itanium mangling hides each real return type, so leave rax = 0 (reads as false/null/nothing-done)
    // in case a caller consumes it. One handler serves every single-`this` suppression target.
    [UnmanagedCallersOnly] private static long OnSuppressed(IntPtr self) => 0;

    /// <summary>Skip for a target taking a by-value CExoString. Win64 hands the callee a copy IT must destroy
    /// (decomp: every such body ends in delete[] + zero), so a skipped body would leak one buffer per line;
    /// SysV's caller destroys, so there the arg is left alone.</summary>
    [UnmanagedCallersOnly] private static long OnSuppressedExo(IntPtr self, IntPtr exo)
    {
        if (OperatingSystem.IsWindows()) Native.DestroyExoString(exo);
        return 0;
    }

    private static ChatPromptCtorFn _promptCtorOrig;
    /// <summary>Resolved on the Windows path only: a null here is also "not the Windows chat-prompt fix-up".</summary>
    private static GuiPanelDeactivateFn _panelDeactivate;

    [UnmanagedCallersOnly]
    private static IntPtr OnPromptCtor(IntPtr self)
    {
        IntPtr r = _promptCtorOrig != null ? _promptCtorOrig(self) : self;
        var prompt = new CGuiInGameChatPrompt(self);
        if (_panelDeactivate != null && prompt.Shown != 0)
        {
            _panelDeactivate(self, 0);
            prompt.Shown = 0;
        }
        return r;
    }
}
