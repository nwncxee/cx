using CX.Framework;

namespace CX.Services.Chat.Core;

/// <summary>
/// Parses a leading channel slash-command off a submitted line. Pure -- text in, (channel, rest) out, no
/// native refs -- so it self-checks off-device. The channel tokens are vanilla's ParseChatString's. A bare
/// <c>/dm</c> is sent as-is, since servers run their own DM command under that name. Any other slash is sent as-is.
/// </summary>
public static class ChatCommands
{
    /// <summary>Map a leading <c>/token</c> to a send channel, an optional quoted tell target, and the
    /// remaining message. <c>/t "Alice" hi</c> → <c>(Tell, "Alice", "hi")</c>;
    /// <c>/s hi</c> → <c>(Shout, null, "hi")</c>; plain text → <c>(null, null, original)</c>.</summary>
    // ponytail: /t takes a quoted target only (the portrait-click prefill); /o's unquoted first word is vanilla's own grammar
    public static (Channel? channel, string? tellTarget, string rest) Parse(string text)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '/') return (null, null, text);
        int sp = text.IndexOfAny(Whitespace);
        string token = (sp < 0 ? text.Substring(1) : text.Substring(1, sp - 1)).ToLowerInvariant();
        Channel? ch = token switch
        {
            "t" or "tell" or "tp" or "tellplayer" => Channel.Tell,
            "s" or "shout"                        => Channel.Shout,
            "w" or "whisper"                      => Channel.Whisper,
            "p" or "party"                        => Channel.Party,
            "tk" or "talk"                        => Channel.Talk,
            "dm"                                  => Channel.DM,
            "o"                                   => Channel.Tell,
            _                                     => null,
        };
        if (ch is null) return (null, null, text);
        string rest = sp < 0 ? "" : text.Substring(sp + 1).TrimStart();
        if (ch == Channel.DM && rest.Length == 0) return (null, null, text);
        if (token == "o" && rest.Length > 0 && rest[0] != '"')
        {
            int end = rest.IndexOfAny(Whitespace);
            return end < 0 ? (ch, rest, "") : (ch, rest.Substring(0, end), rest.Substring(end + 1).TrimStart());
        }
        if (ch == Channel.Tell && rest.Length > 1 && rest[0] == '"')
        {
            int close = rest.IndexOf('"', 1);
            if (close > 1)
            {
                string target = rest.Substring(1, close - 1);
                string msg = close + 1 < rest.Length ? rest.Substring(close + 1).TrimStart() : "";
                return (ch, target, msg);
            }
        }
        return (ch, null, rest);
    }

    private static readonly char[] Whitespace = { ' ', '\t' };

    /// <summary>Off-device proof of the command grammar (surfaces in run-demo's cx.log).</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        check.Expect(Parse("/s hi") == (Channel.Shout, null, "hi"), "shout with message");
        check.Expect(Parse("/s") == (Channel.Shout, null, ""), "bare switch, no message");
        check.Expect(Parse("/W  Hello") == (Channel.Whisper, null, "Hello"), "case-insensitive + trimmed rest");
        check.Expect(Parse("/party lets go") == (Channel.Party, null, "lets go"), "long alias");
        check.Expect(Parse("/dm secret") == (Channel.DM, null, "secret"), "dm with a message sends it on DM, as vanilla does");
        check.Expect(Parse("/DM  secret") == (Channel.DM, null, "secret"), "dm is case-insensitive");
        check.Expect(Parse("/dm") == ((Channel?)null, null, "/dm"), "bare dm passes through whole, for the server's own /dm command");
        check.Expect(Parse("/dm ") == ((Channel?)null, null, "/dm "), "dm with no message passes through too");
        check.Expect(Parse("/o Bob hi there") == (Channel.Tell, "Bob", "hi there"), "/o names its target unquoted, as vanilla does");
        check.Expect(Parse("/o \"A B\" hey") == (Channel.Tell, "A B", "hey"), "/o takes a quoted target too");
        check.Expect(Parse("/o Bob") == (Channel.Tell, "Bob", ""), "/o target, no message");
        check.Expect(Parse("hello") == (null, null, "hello"), "plain text untouched");
        check.Expect(Parse("/unknown x") == (null, null, "/unknown x"), "unknown slash sent verbatim");
        check.Expect(Parse("") == (null, null, ""), "empty");

        // Tell with quoted target
        check.Expect(Parse("/t \"Alice\" hi") == (Channel.Tell, "Alice", "hi"), "tell with quoted target");
        check.Expect(Parse("/tp \"Bob\"") == (Channel.Tell, "Bob", ""), "tell target, no message");
        check.Expect(Parse("/tell \"A B\" hey") == (Channel.Tell, "A B", "hey"), "tell target with space");
        check.Expect(Parse("/t hi") == (Channel.Tell, null, "hi"), "tell without quotes = no target");
        check.Expect(Parse("/t \"\" hi") == (Channel.Tell, null, "\"\" hi"), "empty quotes = no target (close at 1, not >1)");

        log.Info(check.Ok ? "ChatCommands.SelfCheck PASS" : "ChatCommands.SelfCheck had FAILURES");
    }
}
