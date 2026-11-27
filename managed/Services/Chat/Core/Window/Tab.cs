namespace CX.Services.Chat.Core;

/// <summary>
/// One chat tab: a pure <em>receive</em> filter into the shared <see cref="ChatStore"/> plus its own
/// scrollback offset. The send channel is window-level (a dropdown, not a tab). A <c>class</c>:
/// <see cref="ScrollPx"/> mutates as the user scrolls.
/// </summary>
public sealed class Tab
{
    public string Label;
    public uint ChannelMask;      // bit i set => show channel i (channel value masked to <32, see ChatStore)
    public uint? PlayerId;        // when set, only this player's lines
    public float ScrollPx;        // pixels scrolled up from the bottom of the content; 0 = pinned to newest

    /// <summary>Player name this tab is pinned to for Tell auto-targeting (ADR 0002). Empty = unpinned.
    /// The name is stable across relogs; the live PlayerId is resolved fresh on each send.</summary>
    public string PinnedName = "";

    /// <summary>Explicitly kept by the user (pin button). Only meaningful on Tell-only tabs.</summary>
    public bool Kept;

    /// <summary>New messages arrived while this tab was not active. Cleared on focus.</summary>
    public bool Unread;

    /// <summary>The channel last used while this tab was active, written through by
    /// <see cref="ChatWindow.SendChannel"/> and given back by <see cref="ChatWindow.SelectTab"/>; null until the
    /// tab is first active this session. Session memory: the shell clears it on relog and it is not persisted.</summary>
    public Channel? SendChannel;

    /// <summary>The unsent input line parked on this tab (<c>chat.drafts</c>): set when chat closes unsent or the
    /// typing moves to another tab, taken back when typing returns here. Session memory, not persisted.</summary>
    public string Draft = "";

    /// <summary>A Tell-only tab that hasn't been explicitly kept. Shows a close-X + pin button,
    /// excluded from the persisted layout. Structural: any Tell-only tab is ephemeral until pinned.</summary>
    public bool Ephemeral => ChannelMask == 0x002 && !Kept;

    public Tab(string label, uint channelMask, uint? playerId = null)
    { Label = label; ChannelMask = channelMask; PlayerId = playerId; }

    /// <summary>Does a line show in this tab? Includes outbound Tell echoes whose recipient matches
    /// <see cref="PinnedName"/> (two-sided conversation), and lines its sender sent under an id from before a relog.</summary>
    public bool Matches(ChatLine line)
        => (ChannelMask & line.Channel) != 0
           && (!PlayerId.HasValue || line.PlayerId == PlayerId.Value
               || (PinnedName.Length > 0 && (line.TellRecipient == PinnedName || line.SenderName == PinnedName)));
}
