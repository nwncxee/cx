using System;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Services.Chat.Render;
using CX.Ui;

namespace CX.Services.Chat;

/// <summary>The wire side of the chatbox: the vanilla SendPlayerToServerChat_* senders, the chat-bar focus notify, local
/// echo, the player roster resolver and the Tell entry / auto-open routing that leans on it.</summary>
public sealed unsafe partial class ChatService
{
    private static IPlayerResolver? _playerResolver;

    /// <summary>Native player roster resolver: CClientExoApp::GetPlayerByCreatureName first (pinned tab
    /// names are character names), then GetPlayerByPlayerName; reads the player ID at CNWCPlayer+0x8.
    /// Every name this hands out is the CHARACTER name: pins from the player list, nameplates, portraits
    /// and incoming tells must all live in one name space or a reply opens a second tab. Cold.</summary>
    // ponytail: exact-case match (engine strcmp); own roster walk (+0x38/+0x40) with case-folding if players trip
    private sealed class NativePlayerResolver : IPlayerResolver
    {
        private readonly GetPlayerByPlayerNameFn _getByName;
        private readonly GetPlayerByCreatureNameFn _getByCreatureName;

        public NativePlayerResolver(GetPlayerByPlayerNameFn getByName,
                                    GetPlayerByCreatureNameFn getByCreatureName)
        { _getByName = getByName; _getByCreatureName = getByCreatureName; }

        private static IntPtr App() => PlayerIdentity.App();

        public uint? Resolve(string name)
        {
            IntPtr app = App();
            if (app == IntPtr.Zero) return null;
            IntPtr exo = Native.NewExoString(name);
            try
            {
                IntPtr player = IntPtr.Zero;
                if (_getByCreatureName != null) player = _getByCreatureName(app, exo);
                if (player == IntPtr.Zero && _getByName != null) player = _getByName(app, exo);
                if (player == IntPtr.Zero) return null;
                uint id = new CNWCPlayer(player).PlayerId;
                return id == 0xffffffff ? null : id;
            }
            finally { Native.FreeExoString(exo); }
        }

        /// <summary>Read the character name for an ingest PlayerId (+0x8), or null if the player is gone.</summary>
        public string? NameOf(uint playerId)
        {
            foreach (CNWCPlayer player in Roster())
                if (player.PlayerId == playerId) return CharacterName(player);
            return null;
        }

        public IReadOnlyList<string> OnlineNames()
        {
            var names = new List<string>();
            foreach (CNWCPlayer player in Roster())
                if (CharacterName(player) is string n) names.Add(n);
            return names;
        }

        /// <summary>The live roster's non-null entries; empty off-module or when the count is implausible.</summary>
        private static List<CNWCPlayer> Roster()
        {
            var list = new List<CNWCPlayer>();
            IntPtr app = App();
            if (app == IntPtr.Zero) return list;
            CClientExoAppInternal intern = new CClientExoApp(app).Internal;
            if (intern.IsNull) return list;
            IntPtr listBase = intern.Players;
            int count = intern.PlayerCount;
            if (listBase == IntPtr.Zero || count <= 0 || count > 64) return list;
            var players = new ReadOnlySpan<IntPtr>((void*)listBase, count);
            for (int i = 0; i < count; i++)
            {
                var player = new CNWCPlayer(players[i]);
                if (!player.IsNull) list.Add(player);
            }
            return list;
        }

        /// <summary>A roster entry's character name (Latin1: char == source byte), or null when unset.</summary>
        private static string? CharacterName(CNWCPlayer player)
        {
            IntPtr buf = player.CreatureName;
            uint len = player.CreatureNameLen;
            if (buf == IntPtr.Zero || len == 0 || len > 128) return null;
            return Encoding.Latin1.GetString(new ReadOnlySpan<byte>((void*)buf, (int)len));
        }

        /// <summary>The local player's ingest-space playerId, from the shared roster walk. Cold (ingest: tells and desktop notifications).</summary>
        public uint? LocalIngestId()
        {
            IntPtr player = PlayerIdentity.LocalPlayer();
            return player == IntPtr.Zero ? null : new CNWCPlayer(player).PlayerId;
        }
    }

    /// <summary>Auto-open routing for an incoming Tell. Creates a tab or window per the config;
    /// never steals keyboard focus (no FocusedIndex change on ingest).</summary>
    private static void RouteIncomingTell(ChatLine line)
    {
        if (_layout == null) return;
        // Any line carrying the local player's ingest ID is our own outbound tell echo -- skip routing so it
        // never spawns a phantom tab. Gating on the "[To ]" echo format instead would misfire on servers/locales
        // whose echo lacks that prefix; a genuine self-tell not auto-opening a tab is a non-loss.
        if (_playerResolver?.LocalIngestId() == line.PlayerId)
        { _log?.Info($"[chat] tell echo skipped pid={line.PlayerId}"); return; }
        string? name = _playerResolver?.NameOf(line.PlayerId);
        if (name == null)
        { _log?.Info($"[chat] tell route: unresolvable pid=0x{line.PlayerId:x}"); return; }

        TellAutoOpen mode = ChatKnobs.TellMode;
        var decision = TellRouter.Route(mode, _layout.Windows, _layout.FocusedIndex, name);
        _log?.Info($"[chat] tell route: name=\"{name}\" mode={mode} -> {decision.Action} w={decision.WindowIndex} t={decision.TabIndex}");
        switch (decision.Action)
        {
            case TellRoute.Exists:
            {
                // ponytail: refresh the stale connection-scoped PlayerId so the filter stays live after relog.
                ChatWindow[] wins = _layout.Windows;
                if ((uint)decision.WindowIndex < (uint)wins.Length)
                {
                    var tabs = wins[decision.WindowIndex].Tabs;
                    if ((uint)decision.TabIndex < (uint)tabs.Count)
                        tabs[decision.TabIndex].PlayerId = line.PlayerId;
                }
                break;
            }
            case TellRoute.NewTab:
            {
                ChatWindow[] wins = _layout.Windows;
                if ((uint)decision.WindowIndex < (uint)wins.Length)
                {
                    var tab = new Tab($"T:{Native.EngineText(name)}", 0x002, line.PlayerId) { PinnedName = name };
                    wins[decision.WindowIndex].AddTab(tab);
                    wins[decision.WindowIndex].Dirty = true;
                    _log?.Info($"[chat] tell auto-open: new tab '{name}' (pid={line.PlayerId}) in window {decision.WindowIndex}");
                }
                break;
            }
            case TellRoute.NewWindow:
            {
                ChatWindow main = _layout.Main;
                float offset = _layout.Count * 30f;
                var tab = new Tab($"T:{Native.EngineText(name)}", 0x002, line.PlayerId) { PinnedName = name };
                var win = new ChatWindow(main.X + offset, main.Y + offset, main.W, main.H, new[] { tab });
                if (_layout.AddWindow(win) != null)
                    _log?.Info($"[chat] tell auto-open: new window for '{name}' (pid={line.PlayerId})");
                else
                {
                    ChatWindow[] wins = _layout.Windows;
                    int fi = _layout.FocusedIndex;
                    if ((uint)fi < (uint)wins.Length) { wins[fi].AddTab(tab); wins[fi].Dirty = true; }
                    _log?.Info($"[chat] tell auto-open: window cap, tab fallback for '{name}'");
                }
                break;
            }
        }
    }

    /// <summary>The one public door onto the Tell entry path, for a second consumer that has a character name
    /// and no player id (a nameplate click, #click-to-tell). Same flow as the portrait click: the pinned Tell
    /// tab when routing is on, the <c>/t "Name" </c> prefill when it is off. Cold (input thread).</summary>
    public static void StartTell(string name) { if (!string.IsNullOrEmpty(name)) StartTellTo(name, _playerResolver?.Resolve(name)); }

    /// <summary>Open the chat input targeting <paramref name="name"/>. When Tell routing is Off, falls back
    /// to pre-filling <c>/t "name" </c>. Otherwise navigates to the existing pinned tab or creates one.
    /// <paramref name="playerId"/> filters the tab to that player's messages.</summary>
    private static void StartTellTo(string name, uint? playerId)
    {
        if (_layout == null) return;
        // One pin name space: whatever the caller typed or read, a live player pins under the roster's
        // character name -- the same bytes incoming-tell routing produces, or a reply opens a second tab.
        if (playerId is uint p) name = _playerResolver?.NameOf(p) ?? name;
        TellAutoOpen mode = ChatKnobs.TellMode;
        if (mode == TellAutoOpen.Off)
        {
            Open(draft: false);
            string cmd = $"/t \"{name}\" ";
            Edit.Insert(Native.EngineText(cmd));
            Rebuild();
            return;
        }
        // Route: find or create a pinned tab, switch to it, set channel to Tell, open input.
        var decision = TellRouter.Route(mode, _layout.Windows, _layout.FocusedIndex, name);
        switch (decision.Action)
        {
            case TellRoute.Exists:
            {
                ChatWindow win = _layout.Windows[decision.WindowIndex];
                var tabs = win.Tabs;
                for (int j = 0; j < tabs.Count; j++)
                    if (tabs[j].PinnedName == name) { win.SelectTab(j); break; }
                _layout.FocusedIndex = decision.WindowIndex;
                win.SendChannel = Channel.Tell;
                break;
            }
            case TellRoute.NewTab:
            {
                ChatWindow win = _layout.Windows[decision.WindowIndex];
                win.AddTab(new Tab($"T:{Native.EngineText(name)}", 0x002, playerId) { PinnedName = name });
                win.SelectTab(win.Tabs.Count - 1);
                _layout.FocusedIndex = decision.WindowIndex;
                win.SendChannel = Channel.Tell;
                break;
            }
            case TellRoute.NewWindow:
            {
                ChatWindow main = _layout.Main;
                float offset = _layout.Count * 30f;
                var tab = new Tab($"T:{Native.EngineText(name)}", 0x002, playerId) { PinnedName = name };
                var win = new ChatWindow(main.X + offset, main.Y + offset, main.W, main.H, new[] { tab });
                if (_layout.AddWindow(win) != null)
                {
                    _layout.FocusedIndex = _layout.Count - 1;
                    win.SendChannel = Channel.Tell;
                }
                break;
            }
            default:
                return;
        }
        Open();
        SaveConfig();
        Rebuild();
    }

    /// <summary>Tell the server the chat bar took or gave back the keyboard, the way vanilla's chat
    /// prompt does (ChatBarFocus / ChatBarUnFocus; servers draw a "typing" indicator from it). The
    /// latch suppresses repeats; the engine call drops the packet when we are not in a module.
    /// Cold -- only reached from the input thread's open/close.</summary>
    private static void NotifyChatBar(int eventType, Channel ch)
    {
        if (EngineFn.NotifyServerGuiEvent == null) return;
        float* pos = stackalloc float[3];   // Vector const& -- vanilla passes {0,0,0} at every chat-bar site
        pos[0] = 0f; pos[1] = 0f; pos[2] = 0f;
        // `this` is dead in the callee (rdi is clobbered before any read) and the not-in-a-module guard is inside it.
        EngineFn.NotifyServerGuiEvent(IntPtr.Zero, eventType, TypingSignal.ModeOf(ch), TypingSignal.ObjectInvalid, (IntPtr)pos);
    }

    /// <summary>Put a line in the local chat store without touching the wire -- command feedback the player
    /// can read while standing in the scene they are tuning. Talk channel so it lands in the default tab.</summary>
    private static void EchoLocal(string text)
    {
        _store.Append(new ChatLine(0x001, "", 0, "", ColorRunParser.Parse(text, engine: false), DateTime.UtcNow, text, GameNow()));
    }

    /// <summary>Route submitted text to the server on <paramref name="ch"/> via the vanilla
    /// SendPlayerToServerChat_* senders (major 0x09, wire unchanged -> the extender stays optional).
    /// Cold (input thread) so allocation is fine. The callee copies the CExoString; we own + free it.</summary>
    // ponytail: no content is logged (repo's no-keylog stance) -- the send echoes back through the
    // inbound tap, which logs the delivered line, so a live run still proves each channel end-to-end.
    private static void Send(Channel ch, string? tellName, string text)
    {
        IntPtr msg = ClientApi.Message();
        if (msg == IntPtr.Zero) { _log?.Info($"[chat] send dropped (ch={ch}): no live CNWCMessage yet"); return; }
        IntPtr exo = Native.NewExoString(text);
        string wire = Encoding.Latin1.GetString(Native.EngineBytes(text));   // what the tell echo comes back as
        try
        {
            switch (ch)
            {
                case Channel.Talk    when EngineFn.SendChatTalk    != null: EngineFn.SendChatTalk(msg, exo); break;
                case Channel.Shout   when EngineFn.SendChatShout   != null: EngineFn.SendChatShout(msg, exo); break;
                case Channel.Whisper when EngineFn.SendChatWhisper != null: EngineFn.SendChatWhisper(msg, exo); break;
                case Channel.Party   when EngineFn.SendChatParty   != null: EngineFn.SendChatParty(msg, exo); break;
                case Channel.DM      when EngineFn.SendChatDM      != null: EngineFn.SendChatDM(msg, exo); break;
                case Channel.Tell:
                    if (tellName == null) { _log?.Info("[chat] tell dropped: no target name"); break; }
                    // Vanilla /t path: resolve client-side to the playerId (CNWCPlayer+0x8) and send the
                    // id-based form. The name-based TellName form is resolved server-side by FIRST name
                    // only, so full character names always miss -- keep it only as a fallback.
                    if (_playerResolver?.Resolve(tellName) is uint pid && EngineFn.SendChatTellId != null)
                    {
                        _log?.Info($"[chat] tell SENDING via id: name=\"{tellName}\" pid={pid}");
                        TellTarget.Sent(_playerResolver.NameOf(pid) ?? tellName, wire);   // canonical pin name
                        EngineFn.SendChatTellId(msg, pid, exo);
                    }
                    else if (EngineFn.SendChatTell != null)
                    {
                        IntPtr nameExo = Native.NewExoString(tellName);
                        _log?.Info($"[chat] tell SENDING via TellName fallback: name=\"{tellName}\"");
                        TellTarget.Sent(tellName, wire);
                        try { EngineFn.SendChatTell(msg, nameExo, exo); }
                        finally { Native.FreeExoString(nameExo); }
                    }
                    else _log?.Info($"[chat] tell dropped: sender symbols unresolved");
                    break;
                // A slash line that reached here missed dispatch: log its first token (never the rest -- /tell carries content).
                default: _log?.Info($"[chat] send dropped (ch={ch}): sender symbol unresolved" +
                                    (text.StartsWith('/') ? $" -- undispatched command token '{text.Split(' ', 2)[0]}' len={text.Length}" : "")); break;
            }
        }
        finally { Native.FreeExoString(exo); }
    }
}
