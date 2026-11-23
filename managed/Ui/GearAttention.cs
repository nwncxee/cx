using CX.Framework;

namespace CX.Ui;

/// <summary>What the main-menu gear's badge asks the player to look at: a list of reasons, each with its tooltip and
/// the settings group a press on the gear opens. Raised off the game thread, read by the draw without a lock.</summary>
public sealed class GearAttention
{
    /// <summary>One thing the gear asks about, keyed by whoever raised it.</summary>
    public sealed record Reason(object Owner, string Tip, KnobGroup Target);

    /// <summary>The gear at the main menu.</summary>
    public static readonly GearAttention Menu = new();

    private volatile Reason[] _reasons = [];
    private readonly object _gate = new();

    /// <summary>The tooltip of the first reason, or null when the gear has nothing to say.</summary>
    public string? Tip => _reasons is [var r, ..] ? r.Tip : null;

    /// <summary>The group the first reason points the gear at, or null.</summary>
    public KnobGroup? Target => _reasons is [var r, ..] ? r.Target : null;

    /// <summary>Whether the badge draws: at the main menu, while a reason stands.</summary>
    public bool BadgeShown(bool atMainMenu) => atMainMenu && _reasons.Length > 0;

    /// <summary>Stand <paramref name="owner"/>'s reason: in the place of the one it raised before, else last.</summary>
    public void Raise(object owner, string tip, KnobGroup target)
    {
        var next = new Reason(owner, tip, target);
        lock (_gate)
        {
            int at = Array.FindIndex(_reasons, r => r.Owner == owner);
            _reasons = at < 0 ? [.. _reasons, next] : [.. _reasons[..at], next, .. _reasons[(at + 1)..]];
        }
    }

    /// <summary>Take <paramref name="owner"/>'s reason down.</summary>
    public void Clear(object owner)
    {
        lock (_gate) _reasons = [.. _reasons.Where(r => r.Owner != owner)];
    }

    /// <summary>Two owners' reasons on one gear. Oracle: the tips and groups raised here, in the order raised.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "GearAttention.SelfCheck");
        var g = new GearAttention();
        KnobGroup ga = new("cx-gear-a", "General", "About", null, null, true, Effect.Live, null, null, "", register: false);
        KnobGroup gb = new("cx-gear-b", "General", "About", null, null, true, Effect.Live, null, null, "", register: false);
        object a = new(), b = new();
        check.Expect(!g.BadgeShown(true) && g.Tip == null && g.Target == null, "a new gear has nothing to say");
        g.Raise(a, "first", ga);
        g.Raise(b, "second", gb);
        check.Expect(g.Tip == "first" && ReferenceEquals(g.Target, ga), $"the first reason raised speaks ({g.Tip})");
        g.Raise(a, "again", ga);
        check.Expect(g.Tip == "again", $"a reason raised again keeps its place ({g.Tip})");
        g.Clear(a);
        check.Expect(g.Tip == "second" && ReferenceEquals(g.Target, gb) && g.BadgeShown(true), $"clearing it lets the next speak ({g.Tip})");
        check.Expect(!g.BadgeShown(false), "never in play");
        g.Clear(b);
        check.Expect(!g.BadgeShown(true) && g.Tip == null, $"clearing the last takes the badge down ({g.Tip})");
        log.Info(check.Ok ? "GearAttention.SelfCheck: passed" : "GearAttention.SelfCheck: FAILED");
        return check.Ok;
    }
}
