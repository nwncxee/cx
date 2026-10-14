using CX.Framework;

namespace CX.Services.Chat.Core;

/// <summary>The <c>chat</c> group: the Restart master (<see cref="Enabled"/>, read through <c>Boot</c> -- off,
/// <c>ChatService.Initialize</c> installs nothing and the stock chat window runs), the User rows, the Advanced
/// input-line toys, the Chat-only markup colours, and the Hidden per-window state (<see cref="Windows"/>: every
/// <c>w&lt;N&gt;.*</c> key <see cref="ChatConfig"/> writes, edited on a window's gear, outside <c>/chat reset</c>).</summary>
internal static class ChatKnobs
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled;
    internal static readonly Knob<int> TellOpen;
    internal static readonly Knob<bool> Pairs, Complete, Markup, Counter;
    internal static readonly Knob<bool> DelWord, Undo, Mouse, Rows, Drafts;
    internal static readonly Knob<bool> HistSearch, HistSave;
    internal static readonly Knob<uint> EmoteCol, SpeechCol, OocCol;
    internal static readonly KnobBag Windows;

    static ChatKnobs()
    {
        G = KnobGroup.Declare("chat", "Interface", "Chat", master: "Enable the CX chatbox",
            masterHelp: "CX's own chat: multiple windows, tabs, portraits, tells routing. Off, the stock chat window runs. Takes effect at the next launch.",
            masterEffect: Effect.Restart, order: 210,
            header: "Window animation, background shape and tabs are on each window's own gear; the colours "
                  + "come from the look under UI.");
        Enabled = G.Master!;
        TellOpen = G.Dropdown("tellopen", "When a tell arrives", new[] { 0, 1, 2 }, 0,
                              new[] { "nothing", "open a tab", "open a window" },
                              help: "Where a tell from someone new lands: nowhere special, a new tab in the main window, or its own window.");

        G.Section("Typing");
        Pairs = G.Toggle("pairs", "Close brackets and quotes", true, Tier.Advanced,
                         help: "Typing ( [ \" or * adds the closing mark after the cursor, and typing it again steps over it. Backspace between an empty pair removes both. With text selected, the mark wraps it: select a few words, press * and they become *a few words*.");
        Complete = G.Toggle("complete", "Complete names with Tab", true,
                            help: "Tab finishes the name you started typing: the people and creatures around you, nearest first, then everyone online. Press Tab again for the next match, Shift+Tab for the one before.");
        Markup = G.Toggle("markup", "Colour emotes, speech and OOC", false,
                          help: "While you type, *emotes*, \"speech\" and ((out of character)) text take their own colours, so a missing closing * shows before you send.");
        Counter = G.Toggle("counter", "Show a character count", false, Tier.Advanced,
                           help: "A running count of the characters in the line, at the right end of the input bar. A line holds up to 4000.");

        G.Section("Editing");
        DelWord = G.Toggle("delword", "Delete whole words with Ctrl", true, Tier.Advanced,
                           help: "Ctrl+Backspace deletes the word before the cursor, Ctrl+Delete the word after it.");
        Undo = G.Toggle("undo", "Undo and redo", true, Tier.Advanced,
                        help: "Ctrl+Z takes back the last change to the line, Ctrl+Y (or Ctrl+Shift+Z) puts it back.");
        Mouse = G.Toggle("mouse", "Place the cursor with the mouse", true,
                         help: "Click in the input bar to move the cursor, drag to select, double-click to select a word, triple-click for the whole line. Shift+click extends the selection.");
        Rows = G.Toggle("rows", "Arrow keys move through a long message", true, Tier.Advanced,
                        help: "When the line wraps, Up and Down move the cursor between its rows. They reach the sent-line history only from the first or last row.");
        Drafts = G.Toggle("drafts", "Keep unsent lines", true,
                          help: "Escape closes the input without losing what you typed; it is back the next time you open chat. Each tab keeps its own unsent line, so you can answer a tell and come back to a half-written post.");

        G.Section("History");
        HistSearch = G.Toggle("histsearch", "Search history by what you typed", true, Tier.Advanced,
                              help: "With text in the line, Up recalls only earlier lines that start with it. With an empty line, Up walks every line.");
        HistSave = G.Toggle("histsave", "Remember sent lines between sessions", true,
                            help: "Lines you sent stay in the Up-arrow history after a restart. They are kept in cx-chat-history.txt beside cx.cfg; turning this off deletes the file.");

        G.Section("Markup colours");
        EmoteCol = G.Swatch("emotecol", "Emote colour", 0xe0b872ff, Tier.Advanced);
        SpeechCol = G.Swatch("speechcol", "Speech colour", 0xf2f2e6ff, Tier.Advanced);
        OocCol = G.Swatch("ooccol", "OOC colour", 0x8c96a0ff, Tier.Advanced);

        Windows = G.Bag("w");
    }

    /// <summary>The tell routing mode <see cref="TellOpen"/> stores.</summary>
    internal static TellAutoOpen TellMode => (TellAutoOpen)TellOpen.V;
}

/// <summary>The <c>snippets</c> group: the Shortcuts heading of the Chat submenu. The master switches expansion;
/// <see cref="List"/> holds one shortcut per item, written <c>abbreviation expansion</c> (the first space splits
/// them), parsed by <see cref="Snippets.Parse"/>. Last in the submenu so a long list doesn't bury the chat
/// toggles.</summary>
internal static class SnippetKnobs
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled;
    internal static readonly Knob<TextList> List;

    static SnippetKnobs()
    {
        G = KnobGroup.Declare("snippets", "Interface", "Chat", master: "Expand chat shortcuts",
            masterHelp: "Type a shortcut, then a space (or Enter), and it turns into its text. Ctrl+Z right after takes it back.",
            parent: "chat.enabled", order: 212, title: "Shortcuts");
        Enabled = G.Master!;
        List = G.List("list", "Shortcuts", hint: "Add one:  ;bow *bows deeply*",
                      help: "Each entry is the word to type, a space, then what it becomes. Enter adds the next one; clear an entry or press × to remove it.");
    }
}
