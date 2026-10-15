using System;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Services.Chat.Render;
using CX.Ui;

namespace CX.Services.Chat;

/// <summary>The keyboard side of the chatbox: the one EditBuffer, the skip-original vanilla openers, open / close /
/// submit, key + text routing, clipboard, and the keyboard-ownership handshake with the arbiter.</summary>
public sealed unsafe partial class ChatService
{
    /// <summary>Wires the edit buffer, typing state, history file and focus suppression.</summary>
    private static void InitEditControl(ExtenderLog log, HookService hooks)
    {
        RegisterCommands();            // chat's own commands go on the shared registry like anyone's
        _histPath = HistoryFile.PathBeside(Settings.Path);
        if (ChatKnobs.HistSave.V) Edit.LoadHistory(HistoryFile.Load(_histPath, log));
        ChatKnobs.HistSave.Changed = () =>
        {
            if (ChatKnobs.HistSave.V) HistoryFile.SaveAsync(_histPath, Edit.History, _log);
            else HistoryFile.Delete(_histPath, _log);
        };

        GetPlayerByPlayerNameFn getByName = EngineFn.GetPlayerByPlayerName;
        GetPlayerByCreatureNameFn getByCreature = EngineFn.GetPlayerByCreatureName;
        if (getByName != null || getByCreature != null)
        {
            _playerResolver = new NativePlayerResolver(getByName, getByCreature);
            log.Info("ChatService: player roster resolver installed (creature-name first, player-name fallback)");
        }

        // Own the three chat openers with skip-original handlers: install the hook but never call
        // through the trampoline, so the vanilla focus slot stays null and never grabs the keyboard.
        // Whichever opener the Enter path enters is patched at its entry, so all three flip IsTyping.
        _ = hooks.InstallSetFocusChatPrompt(&OnOpenFocus);
        _ = hooks.InstallShowChatPrompt(&OnOpenShow);
        _ = hooks.InstallPrepChatPromptForTell(&OnOpenTell);
        _ = hooks.InstallChatPromptSetFocus(&OnOpenSetFocus);
    }

    /// <summary>The one chat input line; the native shell drives it, all logic is in Core.</summary>
    public static readonly EditBuffer Edit = new();

    /// <summary>Where the sent-line history is saved (<c>chat.histsave</c>); "" when there is no settings path.</summary>
    private static string _histPath = "";

    /// <summary>The tab the live line belongs to (<c>chat.drafts</c>): where it parks when chat closes unsent or the
    /// typing moves to another tab.</summary>
    private static Tab? _typingTab;

    /// <summary>Shortcut expansion as the delegate <see cref="EditBuffer.ExpandBeforeCaret"/> takes, made once.</summary>
    private static readonly Func<string, string?> ShortcutLookup = Snippets.Lookup;


    // SDL_Keymod bits (match HotkeyInputService): Ctrl/Shift both sides.
    private const int KmodShift = 0x0003;  // not-an-offset: SDL key/event code
    private const int KmodCtrl  = 0x00C0;  // not-an-offset: SDL key/event code

    // SDL keycodes. Printable ones are their codepoint; nav keys carry SDLK_SCANCODE_MASK (0x40000000).
    private const uint KeyReturn = 0x0D, KeyEsc = 0x1B, KeyBackspace = 0x08, KeyDelete = 0x7F;  // not-an-offset: SDL key/event code
    private const uint KeyA = 0x61, KeyC = 0x63, KeyV = 0x76, KeyX = 0x78, KeyY = 0x79, KeyZ = 0x7A, KeyTab = 0x09;  // not-an-offset: SDL key/event code
    private const uint KeyRight = 0x4000004F, KeyLeft = 0x40000050, KeyDown = 0x40000051, KeyUp = 0x40000052;  // not-an-offset: SDL key/event code
    private const uint KeyHome = 0x4000004A, KeyEnd = 0x4000004D;  // not-an-offset: SDL key/event code

    /// <summary>SDL_TextInputEvent.text is char[32]; scan the null-terminated UTF-8 within it.</summary>
    private const int SdlTextMax = 31;

    // Skip-original openers: never call the trampoline (suppress vanilla focus), just take the keyboard.
    [UnmanagedCallersOnly] private static void OnOpenFocus(IntPtr self) => Open();
    [UnmanagedCallersOnly] private static void OnOpenShow(IntPtr self) => Open();
    [UnmanagedCallersOnly] private static void OnOpenTell(IntPtr self, IntPtr player)
    {
        if (player != IntPtr.Zero)
        {
            var plr = new CNWCPlayer(player);
            IntPtr buf = plr.CreatureName;
            uint len = plr.CreatureNameLen;
            if (buf != IntPtr.Zero && len > 0 && len <= 128)
            {
                var sb = new StringBuilder((int)len);
                for (uint k = 0; k < len; k++) sb.Append((char)((byte*)buf)[k]);
                string name = sb.ToString();
                uint ingestId = plr.PlayerId;
                StartTellTo(name, ingestId);
                return;
            }
        }
        Open();
    }

    /// <summary>Skip-original CGuiInGameChatPrompt::SetFocus(CExoString): the player-list "Tell Player"
    /// button (and friends) open the vanilla prompt with a prefill. '/t "Name" ' / '/tp "Name" ' route
    /// into the Tell-tab flow; any other prefill opens the box with the text inserted.</summary>
    [UnmanagedCallersOnly] private static void OnOpenSetFocus(IntPtr self, IntPtr exo)
    {
        string text = "";
        if (exo != IntPtr.Zero)
        {
            IntPtr buf = *(IntPtr*)exo;
            uint len = ((ExoStringRec*)exo)->Len;
            if (buf != IntPtr.Zero && len > 0 && len <= 256)
            {
                var sb = new StringBuilder((int)len);
                for (uint k = 0; k < len; k++) sb.Append((char)((byte*)buf)[k]);
                text = sb.ToString();
            }
            if (OperatingSystem.IsWindows()) Native.DestroyExoString(exo);   // Win64: the callee owns the copy
        }
        var (ch, tellTarget, _) = ChatCommands.Parse(text.TrimEnd());
        if (ch == Channel.Tell && !string.IsNullOrEmpty(tellTarget))
        {
            StartTellTo(tellTarget!, _playerResolver?.Resolve(tellTarget!));
            return;
        }
        Open(draft: text.Length == 0);
        if (text.Length > 0 && IsTyping)
        {
            Edit.Insert(Native.EngineText(text));
            Rebuild();
        }
    }

    /// <summary>One-shot: swallow the SDL_TEXTINPUT that the keypress which opened chat still delivers. Starting
    /// text input mid-keypress does not retract the character, and the prefix is inserted explicitly, so without
    /// this the line opens with it twice. ponytail: cleared by the first text event either way, so at worst a
    /// prefix SDL never echoed eats the next identical character.</summary>
    private static byte _eatEcho;

    /// <summary>Open chat with <paramref name="prefix"/> already typed. Used by the "/" chord to start a
    /// command line. No-op when chat is already typing, or when no window has a sendable channel.</summary>
    internal static void OpenWith(char prefix)
    {
        if (IsTyping) return;
        Open(draft: false);
        if (!IsTyping) return;             // Open() refuses when no window has a sendable channel
        Edit.InsertUtf8(new[] { (byte)prefix });
        _eatEcho = (byte)prefix;
        Rebuild();
    }

    /// <summary>Take the keyboard: reset the line, start SDL text input, gate gameplay keys. Targets the focused
    /// window, falling back to Main; refuses if neither has a sendable channel, and whenever the line would not
    /// be visible (out of game, app unfocused, a NUI window over the box) -- each would eat every key unseen.
    /// <paramref name="draft"/> takes back the line parked on the target tab (<c>chat.drafts</c>).</summary>
    private static void Open(bool draft = true)
    {
        if (_layout == null || !GameGate.InGame) return;
        ChatWindow target = _layout.Focused;
        if (!target.HasSendable)
        {
            target = _layout.Main;
            _layout.FocusedIndex = 0;
            if (_settings.Open) _settings.Reset();
        }
        if (!target.HasSendable) return;
        if (!UiInputService.AppFocused || Hidden(target))
        {
            _log?.Info($"[chat] open refused: focused={UiInputService.AppFocused} nuiOver={Hidden(target)}");
            return;
        }
        if (IsTyping) ParkDraft();
        IsTyping = true;
        if (TypingSignal.Focus()) NotifyChatBar(TypingSignal.EventFocus, target.SendChannel);
        Edit.Clear();
        _completer.Reset();
        _typingTab = target.Active;
        if (draft && ChatKnobs.Drafts.V) { Edit.Load(_typingTab.Draft); _typingTab.Draft = ""; }
        if (EngineFn.SdlStartTextInput != null) EngineFn.SdlStartTextInput();
        KeyboardMode(true);
        Rebuild();
    }

    /// <summary>Park the live line on the tab it belongs to (<c>chat.drafts</c>). A parked line is never overwritten:
    /// only an empty slot takes one, so a prefilled or command line closing unsent leaves an older draft alone.</summary>
    private static void ParkDraft()
    {
        if (ChatKnobs.Drafts.V && _typingTab != null && _typingTab.Draft.Length == 0) _typingTab.Draft = Edit.Text;
    }

    /// <summary>The typing moved to another tab or window (a click, a tell opening): park the line on the old tab and
    /// take the new tab's (<c>chat.drafts</c>).</summary>
    private static void SyncDraftTab()
    {
        if (!IsTyping || _layout == null || !ChatKnobs.Drafts.V) return;
        Tab now = _layout.Focused.Active;
        if (ReferenceEquals(now, _typingTab)) return;
        ParkDraft();
        Edit.Load(now.Draft);
        now.Draft = "";
        _typingTab = now;
        _completer.Reset();
        Rebuild();
    }

    /// <summary>True when a NUI window covers the box's centre, so the line would be typed unseen.</summary>
    internal static bool Hidden(ChatWindow w) => UiInputService.NuiCovers(w.X + w.W * 0.5f, w.Y + w.H * 0.5f);

    /// <summary>
    /// Release the keyboard. <paramref name="submit"/> recalls the line into history; either way the
    /// line clears and gameplay input resumes.
    /// </summary>
    public static void CloseChat(bool submit)
    {
        _eatEcho = 0;
        _completer.Reset();
        if (submit)
        {
            string text = Edit.Submit();
            if (text.Length > 0 && ChatKnobs.HistSave.V) HistoryFile.SaveAsync(_histPath, Edit.History, _log);
            if (!string.IsNullOrEmpty(text) && CommandRegistry.Global.TryDispatch(text, out string? reply))
            {
                // Handled by whichever service registered it -- don't send. The chat service's only remaining
                // job is putting the handler's answer, if any, in front of the player.
                if (reply != null) EchoLocal(reply);
            }
            else if (_layout?.Focused is ChatWindow win && !string.IsNullOrEmpty(text))
            {
                var (switchTo, tellTarget, rest) = ChatCommands.Parse(text);
                Channel prev = win.SendChannel;
                if (switchTo is Channel nc) win.SendChannel = nc;
                if (!string.IsNullOrEmpty(rest))
                {
                    string? tellName = null;
                    if (win.SendChannel == Channel.Tell)
                    {
                        tellName = TellTarget.ResolveName(win.Active, tellTarget, _playerResolver);
                        _log?.Info($"[chat] tell resolve: target=\"{tellTarget}\" pin=\"{win.Active.PinnedName}\" name={tellName ?? "(null)"}");
                    }
                    _log?.Info($"[chat] submit: ch={win.SendChannel} fi={_layout!.FocusedIndex} tab={win.ActiveTab} pin=\"{win.Active.PinnedName}\" tell={tellName ?? "-"}");
                    Send(win.SendChannel, tellName, rest);
                }
                // Tell is ephemeral on unpinned tabs (each send names its target), so a /t switch never
                // sticks there; pinned Tell tabs are dedicated to one target and keep it.
                if (win.SendChannel == Channel.Tell && win.Active.PinnedName.Length == 0)
                    win.SendChannel = prev == Channel.Tell ? Channel.Talk : prev;
            }
        }
        else { ParkDraft(); Edit.Clear(); }
        _typingTab = null;
        IsTyping = false;
        // After the send, like vanilla's HandleEnter (ParseChatString, then the unfocus event). Cancel
        // paths (Esc, focus loss, a window losing its sendable channel) all funnel here too, so the
        // server never keeps a stuck "typing" state -- the same three cases vanilla covers.
        if (TypingSignal.Unfocus())
            NotifyChatBar(TypingSignal.EventUnfocus, _layout?.Focused.SendChannel ?? Channel.Talk);
        // Do NOT SDL_StopTextInput: the engine leaves text input globally on (it never toggles it) and the
        // debug console + other text fields depend on TEXTINPUT events firing. KeyboardModeOff below is the
        // real gate that stops chars reaching us. Stopping SDL text input here breaks the console.
        KeyboardMode(false);
        Rebuild();
    }

    /// <summary>Register the chat service's own window-management commands into the shared registry. Every
    /// other command on the surface belongs to the service that implements it.</summary>
    private static void RegisterCommands()
    {
        var r = CommandRegistry.Global;
        r.Register("/newwin", "open a new chat window", _ => { CreateWindow(); return null; });
        r.Register("/closewin", "close the focused chat window", _ =>
        {
            if (_layout == null) return null;
            int idx = _layout.FocusedIndex;
            if (_layout.CloseWindow(idx))
            {
                SaveConfig();
                _log?.Info($"[chat] /closewin: window {idx} closed");
            }
            return null;
        });
    }

    /// <summary>Insert finished UTF-8 codepoints (SDL_TEXTINPUT payload) at the caret.</summary>
    public static void InsertUtf8(IntPtr text)
    {
        if (text == IntPtr.Zero) return;
        byte* p = (byte*)text;
        int n = 0;
        while (n < SdlTextMax && p[n] != 0) n++;
        if (_eatEcho != 0)
        {
            byte echo = _eatEcho;
            _eatEcho = 0;
            if (n == 1 && p[0] == echo) return;   // the opening keypress's own character, already inserted
        }
        string s = Encoding.UTF8.GetString(p, n);
        if (_settings.Editing)   // typed chars go to the focused settings value box, not the chat line
        { _settings.EditInsert(s); return; }
        _completer.Reset();
        s = Native.EngineTyped(s);
        if (s == " ") Edit.ExpandBeforeCaret(ShortcutLookup);
        Edit.Type(s, ChatKnobs.Pairs.V);
        Rebuild();   // reflect the new text in the footer
    }

    /// <summary>The framework key delegate: route the key to the edit line only while chat owns the
    /// keyboard (typing or editing a settings field), consuming it; otherwise decline so it falls to gameplay.
    /// The arbiter routes here first via the keyboard-owner path when <see cref="WantsKey"/> is set.</summary>
    private static bool OnChatKey(uint key, int mods)
    {
        if (!WantsKey) return false;
        RouteKey(key, mods);
        return true;
    }

    /// <summary>Dispatch a control/nav key to the edit line while the box owns the keyboard.</summary>
    public static void RouteKey(uint key, int mods)
    {
        // A settings value box owns the keyboard first (before the chat ctrl block, or Ctrl+V would hit chat).
        if (_settings.Editing)
        {
            switch (key)
            {
                case KeyReturn:    _settings.CommitEdit(_focused); break;
                case KeyEsc:       _settings.CancelEdit(); break;
                case KeyBackspace: _settings.EditBackspace(); break;
                // other keys (nav/ctrl) are ignored while typing a number
            }
            if (_settings.FontChanged && _focused != null) { _settings.FontChanged = false; EnsureFace(_focused); }   // Enter on the size / typeface list
            DirtyAll();
            SaveConfig();   // a keyboard commit has no paired mouse-up to persist on
            SyncFieldKeyboard(); Rebuild();
            return;
        }
        bool shift = (mods & KmodShift) != 0;
        bool ctrl  = (mods & KmodCtrl) != 0;
        if (key != KeyTab) _completer.Reset();
        if (ctrl)
        {
            if (key == KeyA) { Edit.SelectAll(); Rebuild(); return; }
            if (key == KeyC) { Copy(); return; }
            if (key == KeyV) { Paste(); return; }
            if (key == KeyX) { Cut(); return; }
            if (key == KeyZ || key == KeyY)
            {
                if (ChatKnobs.Undo.V) { if (key == KeyY || shift) Edit.Redo(); else Edit.Undo(); }
                Rebuild(); return;
            }
        }
        bool words = ctrl && ChatKnobs.DelWord.V;
        switch (key)
        {
            case KeyReturn:    Edit.ExpandBeforeCaret(ShortcutLookup); CloseChat(true); break;
            case KeyEsc:       CloseChat(false); break;
            case KeyBackspace: if (words) Edit.DeleteWordLeft(); else Edit.Backspace(); break;
            case KeyDelete:    if (words) Edit.DeleteWordRight(); else Edit.Delete(); break;
            case KeyLeft:      if (ctrl) Edit.MoveWordLeft(shift); else Edit.MoveLeft(shift); break;
            case KeyRight:     if (ctrl) Edit.MoveWordRight(shift); else Edit.MoveRight(shift); break;
            case KeyHome:      Edit.Home(shift); break;
            case KeyEnd:       Edit.End(shift); break;
            case KeyUp:        if (!RowMove(-1, shift)) Edit.HistoryPrev(ChatKnobs.HistSearch.V); break;
            case KeyDown:      if (!RowMove(1, shift)) Edit.HistoryNext(); break;
            case KeyTab:       if (ChatKnobs.Complete.V) Complete(shift); break;
        }
        Rebuild();   // caret move / edit / history recall all change the footer
    }

    /// <summary>Up / Down inside a wrapped line (<c>chat.rows</c>): true when the caret moved to the row above
    /// (<paramref name="dir"/> -1) or below; false on the first / last row, where the arrows walk history.</summary>
    private static bool RowMove(int dir, bool shift)
    {
        if (!ChatKnobs.Rows.V || _layout == null || TextRig.Atlas is not GlyphAtlas atlas) return false;
        int to = InputStripLayout.VerticalMove(_layout.Focused, atlas, Edit.Text, Edit.Cursor, dir);
        if (to < 0) return false;
        Edit.SetCaret(to, shift);
        return true;
    }

    private static void Copy()
    {
        if (EngineFn.SdlSetClipboardText == null || !Edit.HasSelection) return;
        byte[] u = Encoding.UTF8.GetBytes(Edit.SelectedText);
        Span<byte> buf = stackalloc byte[u.Length + 1];
        u.CopyTo(buf);
        buf[u.Length] = 0;
        fixed (byte* b = buf) EngineFn.SdlSetClipboardText(b);
    }

    /// <summary>Ctrl+X: copy the selection to the clipboard, then delete it (Backspace removes a selection).</summary>
    private static void Cut()
    {
        if (!Edit.HasSelection) return;
        Copy();
        Edit.Backspace();
        Rebuild();
    }

    private static void Paste()
    {
        if (EngineFn.SdlGetClipboardText == null) return;
        IntPtr p = EngineFn.SdlGetClipboardText();
        if (p == IntPtr.Zero) return;
        byte* s = (byte*)p;
        int n = 0;
        while (s[n] != 0) n++;
        Edit.Insert(Native.EngineTyped(Encoding.UTF8.GetString(s, n)));
        if (EngineFn.SdlFree != null) EngineFn.SdlFree(p);   // SDL_GetClipboardText returns SDL-malloc'd memory
        Rebuild();   // Ctrl+V early-returns from RouteKey before its trailing Rebuild -> redraw the footer here
    }

    /// <summary>Sync the engine keyboard-capture gate with a settings field edit: take the keyboard (so
    /// TEXTINPUT/keydown reach us and gameplay keys are gated) while a value box is being typed, release it
    /// when neither a field nor chat wants it. Called after any settings interaction that may start/end an edit.</summary>
    private static void SyncFieldKeyboard()
    {
        if (_settings.Editing) { KeyboardMode(true); if (EngineFn.SdlStartTextInput != null) EngineFn.SdlStartTextInput(); }
        else if (!IsTyping) KeyboardMode(false);
    }

    /// <summary>Take/release the keyboard by asserting this window's keyboard ownership on the arbiter. The
    /// engine gate itself (KeyboardModeOn/Off plus the drive-latch reset) lives in <c>UiInputService</c>, once
    /// for all consumers; chat only declares intent.</summary>
    private static void KeyboardMode(bool on)
    {
        if (_uiWindow != null) UiHost.RequestKeyboard(_uiWindow, on);
    }
}
