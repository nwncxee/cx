using CX.Framework;

namespace CX.Services.Chat.Core;

/// <summary>The channel flag CX files an inbound line under, from the engine's flag and the server's chat minor.
/// The engine shows a DM-channel line as Shout with a DM tag; CX files it under DM, where its windows look.</summary>
public static class InboundChannel
{
    /// <summary>The server's chat minor for the DM channel, and the same minor when a DM sends it.</summary>
    public const int DmChannel = 0x0e, DmChannelFromDm = 0x1e;   // not-an-offset: chat minor

    /// <summary>The engine's Shout and DM message flags (GUIINGAME_MESSAGE_SHOUT, _DM_MESSAGE).</summary>
    public const uint Shout = 0x004, Dm = 0x400;   // not-an-offset: chat channel flags

    /// <summary>No chat minor latched: the line did not come through the server chat handler.</summary>
    public const int NoMinor = -1;

    /// <summary>The flag CX files a line under; every line but a DM-channel one keeps the engine's.</summary>
    public static uint Filed(uint flag, int minor) =>
        flag == Shout && (minor == DmChannel || minor == DmChannelFromDm) ? Dm : flag;

    /// <summary>Hand-written oracle: the minors and flags read from HandleServerToPlayerChat's switch, Linux and Windows.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(Filed(0x004, 0x0e) == 0x400, "a player's DM-channel line is filed under DM");
        check.Expect(Filed(0x004, 0x1e) == 0x400, "a DM's DM-channel line is filed under DM");
        check.Expect(Filed(0x004, 0x02) == 0x004, "a shout stays a shout");
        check.Expect(Filed(0x004, 0x12) == 0x004, "a DM's shout stays a shout");
        check.Expect(Filed(0x004, -1) == 0x004, "a shout no chat minor latched stays a shout");
        check.Expect(Filed(0x400, 0x11) == 0x400, "a DM speaking on Talk keeps its DM flag");
        check.Expect(Filed(0x080, 0x0e) == 0x080, "feedback during a DM-channel line keeps its own flag");
        check.Expect(Filed(0x001, 0x01) == 0x001, "talk stays talk");
        log.Info(check.Ok ? "InboundChannel.SelfCheck PASS" : "InboundChannel.SelfCheck had FAILURES");
    }
}
