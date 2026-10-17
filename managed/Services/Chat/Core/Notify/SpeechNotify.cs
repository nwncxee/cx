using CX.Framework;

namespace CX.Services.Chat.Core;

/// <summary>Which ingested chat lines raise a desktop notification, and the text one shows. Range needs no test here:
/// the server sends talk only within 20 m and whisper within 3 m of the controlled creature, in its area.</summary>
public static class SpeechNotify
{
    /// <summary>The channels a creature speaks on: talk, tell, shout, whisper, party, DM.</summary>
    private const uint Speech = 0x001 | 0x002 | 0x004 | 0x008 | 0x040 | 0x400;  // not-an-offset: chat channel flags

    /// <summary>The playerId ingest carries when no player controls the speaker: an NPC, an object, a script.</summary>
    private const uint NoPlayer = 0xfffffffe;  // not-an-offset: engine sentinel

    /// <summary>Longest text a notification carries: the Windows balloon's own guidance (its hard cap is 255).</summary>
    private const int MaxChars = 200;

    /// <summary>A line notifies when another player's creature spoke it on a speech channel while the game window
    /// is in the background. <paramref name="localId"/> is our own playerId, whose echoes never notify.</summary>
    public static bool ShouldNotify(uint channel, uint playerId, uint? localId, bool focused)
        => !focused && (channel & Speech) != 0 && playerId != NoPlayer && playerId != localId;

    /// <summary>The line as it reads in chat, without colours, cut to <see cref="MaxChars"/>.</summary>
    public static string Text(ColorRun[] runs)
    {
        string s = string.Concat(runs.Select(r => r.Text)).Trim();
        return s.Length <= MaxChars ? s : s[..(MaxChars - 1)] + "…";
    }

    /// <summary>Hand-written oracle: the channel flags and the no-player sentinel <c>HandleServerToPlayerChat</c>
    /// passes the ingest tap, as read in the decompile and seen in a live log.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        check.Expect(ShouldNotify(0x1, 12, 5, focused: false), "a player's talk in the background notifies");
        check.Expect(!ShouldNotify(0x1, 12, 5, focused: true), "nothing while the game window has focus");
        check.Expect(ShouldNotify(0x2, 12, 5, false), "tell");
        check.Expect(ShouldNotify(0x4, 12, 5, false), "shout");
        check.Expect(ShouldNotify(0x8, 12, 5, false), "whisper");
        check.Expect(ShouldNotify(0x40, 12, 5, false), "party");
        check.Expect(ShouldNotify(0x400, 12, 5, false), "a DM speaking");
        check.Expect(ShouldNotify(0x1, 12, null, false), "no local id yet still lets another player through");
        check.Expect(!ShouldNotify(0x1, 0xfffffffe, 5, false), "an NPC or object speaking (no player behind it)");
        check.Expect(!ShouldNotify(0x4, 0xfffffffe, 5, false), "a scripted DM-channel line from SERVER");
        check.Expect(!ShouldNotify(0x2, 5, 5, false), "our own echo");
        check.Expect(!ShouldNotify(0x20, 0, 5, false), "a server message");
        check.Expect(!ShouldNotify(0x10, 12, 5, false), "a to-hit roll");
        check.Expect(!ShouldNotify(0x80, 12, 5, false), "combat feedback");
        check.Expect(!ShouldNotify(0x10000000, 0, 5, false), "a client-local echo");

        check.Expect(Text([new ColorRun("Aribeth: ", 1, 1, 1), new ColorRun("Well met! ", 1, 1, 1)])
                     == "Aribeth: Well met!", "runs join into the plain line, trimmed");
        string capped = Text([new ColorRun(new string('a', 300), 1, 1, 1)]);
        check.Expect(capped.Length == 200 && capped[199] == '…' && capped[198] == 'a', "a long line is cut to 200 with an ellipsis");

        log.Info(check.Ok ? "SpeechNotify.SelfCheck: passed" : "SpeechNotify.SelfCheck: FAILED");
    }
}
