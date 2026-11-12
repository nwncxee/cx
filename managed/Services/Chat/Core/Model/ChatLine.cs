using System;

namespace CX.Services.Chat.Core;

/// <summary>
/// One ingested chat line. A <c>class</c> (not struct): ingest is a cold network-thread path where
/// per-line allocation is fine, and reference identity lets the ring alias entries cheaply.
/// Immutable after construction -- a hard design rule so a data-bearing toggle (e.g. timestamps)
/// changes only lines ingested afterward, never rewrites the backlog.
/// </summary>
public sealed class ChatLine
{
    public uint Channel { get; }
    /// <summary>The speaker's character name, read off the roster as the line arrived; "" for a line with no
    /// portrait or a speaker the roster did not hold. It outlives <see cref="PlayerId"/>, which a relog replaces.</summary>
    public string SenderName { get; }
    public uint PlayerId { get; }
    public string PortraitResref { get; }
    public ColorRun[] ColorRuns { get; }
    public DateTime Timestamp { get; }
    /// <summary>In-game world time at ingest (for the Game timestamp source); default/invalid = use local.</summary>
    public GameStamp GameTime { get; }
    public string RawText { get; }
    /// <summary>For outbound Tell echoes: the recipient's name parsed from the "[To Name]" prefix.
    /// Empty for incoming Tells and all other channels. Enables two-sided conversation tabs.</summary>
    public string TellRecipient { get; }

    public ChatLine(uint channel, string senderName, uint playerId, string portraitResref,
                    ColorRun[] colorRuns, DateTime timestamp, string rawText, GameStamp gameTime = default,
                    string tellRecipient = "")
    {
        Channel = channel;
        SenderName = senderName;
        PlayerId = playerId;
        PortraitResref = portraitResref;
        ColorRuns = colorRuns;
        Timestamp = timestamp;
        GameTime = gameTime;
        RawText = rawText;
        TellRecipient = tellRecipient;
    }
}
