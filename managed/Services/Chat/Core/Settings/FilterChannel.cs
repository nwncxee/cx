using System.Globalization;
using CX.Framework;

namespace CX.Services.Chat.Core;

/// <summary>GUIINGAME_MESSAGE_* flag values read from the binary's rodata. The channel value from the
/// ingest hook IS the flag, and both <see cref="Tab.Matches"/> and <see cref="ChatStore"/> filter via
/// direct <c>mask &amp; channel</c>. "Combat" maps to TO_HIT; CCMESSAGE (0x80) is feedback text.</summary>
public static class FilterChannel
{
    /// <summary>The tab editor's channel toggles. A channel the game names takes its word from the TLK's chat
    /// channel names, read on every call so a language or a server override shows on the next layout.</summary>
    public static (uint Bit, string Label)[] Editable() => new[]
    {
        (0x001u, Word(66754)),
        (0x002u, Word(66753)),
        (0x004u, Word(66751)),
        (0x008u, Word(66752)),
        (0x010u, "To-Hit"),
        (0x020u, "Server"),
        (0x040u, Word(66755)),
        (0x080u, "Feedback"),
        (0x400u, Word(66756)),
    };

    /// <summary>The TLK's text for a strref; the number itself where no TLK is loaded (the stub host).</summary>
    private static string Word(uint strref) => Native.TlkText(strref) ?? strref.ToString(CultureInfo.InvariantCulture);
}
