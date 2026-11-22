using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace CX.Framework;

/// <summary>
/// The slash-command surface (bucket 1 of ADR 0013). Services register their own commands during
/// <see cref="IExtenderService.Initialize"/>; the chat input surface only hands text to
/// <see cref="TryDispatch"/> and echoes whatever comes back, so it knows nothing about any command.
/// Cold by construction -- one dispatch per submitted line -- so the hot-path allocation rule does not apply.
/// </summary>
public sealed class CommandRegistry
{
    /// <summary>A command body. Takes the argument text after the name (lowercased, trimmed, may be empty) and returns
    /// the single line to echo back to the player, or null to stay silent.</summary>
    public delegate string? Handler(string args);

    /// <summary>One registered command, as enumeration sees it. <paramref name="Name"/> carries the leading slash,
    /// <paramref name="Aliases"/> dispatch to the same handler, <paramref name="Help"/> is the one line a listing
    /// prints, and <paramref name="Exempt"/> dispatches even while <see cref="Gate"/> is closed -- <c>/settings</c>,
    /// the one door in.</summary>
    public sealed record Command(string Name, string[] Aliases, string Help, Handler Run, bool Exempt = false);

    /// <summary>The one live registry every service registers into.</summary>
    public static readonly CommandRegistry Global = new();

    private readonly List<Command> _all = new();
    private readonly Dictionary<string, Command> _byName = new(StringComparer.Ordinal);

    /// <summary>Every registered command in registration order. Aliases are folded into their command, not
    /// listed separately.</summary>
    public IReadOnlyList<Command> Commands => _all;

    /// <summary>Log for refusals. Set once by the host; a refusal is an init-time collision between two
    /// services, and the command it costs would otherwise just not exist with no diagnostic anywhere.</summary>
    public ExtenderLog? Log;

    /// <summary>The command gate (spec §4.1, <c>general.commands</c>): while it answers false, every command
    /// that is not <see cref="Command.Exempt"/> is unknown to <see cref="Lookup"/>, so BOTH entry points --
    /// the CX chatbox and the vanilla prompt chain -- fall through and the line goes to the server as chat.
    /// Null = open (the SelfCheck's private instance). The one place the gate lives.</summary>
    public Func<bool>? Gate;

    /// <summary>Register a command. Names are normalised: a leading slash is added if missing and the whole
    /// name is lowercased. Returns false and changes nothing if the name or any alias is already taken --
    /// first registration wins, so a later service can never silently steal an existing command. A refusal is
    /// logged, so a caller that discards the return value still leaves a trace.</summary>
    public bool Register(string name, string help, Handler run, params string[] aliases)
        => Register(name, help, run, exempt: false, aliases);

    /// <summary><see cref="Register(string, string, Handler, string[])"/> with <paramref name="exempt"/>: the command
    /// dispatches even while <see cref="Gate"/> is closed.</summary>
    public bool Register(string name, string help, Handler run, bool exempt, params string[] aliases)
    {
        string canon = Norm(name);
        string[] alt = Array.ConvertAll(aliases, Norm);
        if (_byName.ContainsKey(canon)) return Refuse(canon, canon);
        foreach (string a in alt) if (_byName.ContainsKey(a)) return Refuse(canon, a);

        var cmd = new Command(canon, alt, help, run, exempt);
        _byName[canon] = cmd;
        foreach (string a in alt) _byName[a] = cmd;
        _all.Add(cmd);
        return true;
    }

    /// <summary>Run the command named by the first token of <paramref name="text"/>. Returns false -- with no
    /// side effect -- when the text is not a slash command or names nothing registered, which is what lets the
    /// caller fall through to the channel-switch grammar and the send path.</summary>
    // ponytail: dispatch is first-token only, so an exact-match command now tolerates trailing junk
    // (`/fps off` toggles rather than being sent to the server as chat). Per-command "takes arguments"
    // metadata would preserve the old fall-through exactly; it is not worth a field.
    public bool TryDispatch(string text, out string? reply)
    {
        reply = null;
        if (!Lookup(text, out string cmd, out int sp, out Command? c)) return false;
        reply = c.Run(sp < 0 ? "" : cmd[(sp + 1)..].TrimStart());
        // A command WITH arguments is a knob change; the log is what ties a period's numbers to its setting.
        if (sp >= 0 && reply != null) Log?.Info($"[cmd] {cmd} -> {(reply.Length > 240 ? reply[..240] : reply)}");
        return true;
    }

    /// <summary>Whether <see cref="TryDispatch"/> WOULD handle <paramref name="text"/>, running nothing. The vanilla
    /// prompt has to close before a handler runs (a handler may take the keyboard), so on that path the decision and
    /// the dispatch are two calls.</summary>
    public bool Knows(string text) => Lookup(text, out _, out _, out _);

    /// <summary>The one parse both entry points share: first token of the trimmed, lowercased line names the
    /// command; a closed <see cref="Gate"/> then hides everything but an exempt command.</summary>
    private bool Lookup(string text, out string cmd, out int sp, [NotNullWhen(true)] out Command? c)
    {
        cmd = ""; sp = -1; c = null;
        if (text.Length < 2 || text[0] != '/') return false;
        // The whole line is lowercased, arguments included -- the if-chain this replaced did the same, and the
        // knob keys depend on it (`/light GAIN 2` must reach AlloyLights as "gain").
        cmd = text.Trim().ToLowerInvariant();
        sp = cmd.IndexOf(' ');
        string name = sp < 0 ? cmd : cmd[..sp];
        if (!_byName.TryGetValue(name, out c)) return false;
        if (c.Exempt || Gate == null || Gate()) return true;
        c = null;
        return false;
    }

    private bool Refuse(string canon, string taken)
    {
        Log?.Info($"CommandRegistry: '{canon}' refused -- '{taken}' is already registered by "
                  + $"'{_byName[taken].Name}' (first registration wins)");
        return false;
    }

    private static string Norm(string n) => (n.StartsWith('/') ? n : "/" + n).ToLowerInvariant();

    /// <summary>Registration, lookup, case folding, argument passthrough, duplicate refusal and enumeration,
    /// off-device. Runs against a private instance so nothing it registers reaches the live surface.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        var r = new CommandRegistry();
        string? seen = null;
        check.Expect(r.Register("/probe", "probe help", a => { seen = a; return "echo " + a; }, "/pb", "pb2"),
            "a fresh name registers");
        check.Expect(r.Register("/silent", "silent help", _ => null), "a second command registers");

        check.Expect(r.TryDispatch("/probe", out string? reply), "the canonical name dispatches");
        check.Expect(seen == "" && reply == "echo ", "no arguments means an empty argument string");
        check.Expect(r.TryDispatch("/pb x", out _) && seen == "x", "an alias dispatches to the same handler");
        check.Expect(r.TryDispatch("/pb2 x", out _) && seen == "x", "an alias without a slash is normalised");
        check.Expect(r.TryDispatch("/PROBE GAIN 2", out _) && seen == "gain 2",
            "the name is case-insensitive and the arguments arrive lowercased");
        check.Expect(r.TryDispatch("/probe  a  b  ", out _) && seen == "a  b",
            "the line is trimmed at the end; inner spacing is the handler's business");
        check.Expect(!r.TryDispatch("  /probe", out _), "a leading space means it was never a command (as before)");
        check.Expect(r.TryDispatch("/silent", out string? none) && none == null, "a handler may decline to echo");

        seen = null;
        check.Expect(!r.TryDispatch("/nosuch", out reply) && reply == null && seen == null,
            "an unknown command is not handled and runs nothing");
        check.Expect(!r.TryDispatch("/", out _), "a bare slash is not a command");
        check.Expect(!r.TryDispatch("hello", out _), "plain text is not a command");
        check.Expect(!r.TryDispatch("/probefoo", out _), "the name must match the whole first token");
        seen = null;
        check.Expect(r.Knows("/probe x") && r.Knows("/PB") && !r.Knows("/nosuch") && !r.Knows("hello") && seen == null,
            "Knows answers what TryDispatch would, and runs nothing");

        check.Expect(!r.Register("/probe", "dupe", _ => "stolen"), "a duplicate name is refused");
        check.Expect(!r.Register("/pb", "dupe", _ => "stolen"), "a duplicate alias is refused");
        check.Expect(!r.Register("/other", "dupe alias", _ => "stolen", "/probe"), "a new name with a taken alias is refused");
        check.Expect(r.TryDispatch("/probe", out reply) && reply == "echo ", "the first registration still owns the name");
        check.Expect(!r.TryDispatch("/other", out _), "the refused registration left nothing behind");

        check.Expect(r.Commands.Count == 2, "enumeration lists each command once, aliases folded in");
        check.Expect(r.Commands[0].Name == "/probe" && r.Commands[0].Help == "probe help", "enumeration carries name + help");
        check.Expect(r.Commands[0].Aliases.Length == 2 && r.Commands[0].Aliases[0] == "/pb"
            && r.Commands[0].Aliases[1] == "/pb2", "enumeration carries the normalised aliases");
        check.Expect(r.Commands[1].Name == "/silent", "enumeration is in registration order");

        bool open = false;
        r.Gate = () => open;
        check.Expect(r.Register("/door", "door help", _ => "opened", exempt: true), "an exempt command registers");
        seen = null;
        check.Expect(!r.TryDispatch("/probe x", out reply) && reply == null && seen == null && !r.Knows("/probe"),
            "a closed gate hides a command from both entry points and runs nothing");
        check.Expect(r.TryDispatch("/door", out reply) && reply == "opened" && r.Knows("/door"), "an exempt command passes a closed gate");
        open = true;
        check.Expect(r.TryDispatch("/probe x", out _) && seen == "x", "an open gate dispatches as before");
        check.Expect(r.Commands.Count == 3 && r.Commands[2].Exempt && !r.Commands[0].Exempt, "enumeration carries the exemption");

        log.Info(check.Ok ? "CommandRegistry.SelfCheck: passed" : "CommandRegistry.SelfCheck: FAILED");
    }
}
