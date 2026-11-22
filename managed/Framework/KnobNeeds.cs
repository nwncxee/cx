using System;

namespace CX.Framework;

/// <summary>What a knob needs to apply at all: nothing, or the Alloy renderer.</summary>
public enum KnobNeed { None, Alloy }

/// <summary>The availability decision's inputs, replaced whole by <see cref="KnobNeeds.AssumeAlloyOff"/>.</summary>
internal sealed class KnobNeedsState
{
    /// <summary>Alloy is known to be off. False until something says so: settings act as if the renderer runs.</summary>
    public bool AlloyOff;
    /// <summary>The renderer's own answer to "is Alloy drawing this session", once it has registered one.</summary>
    public Func<bool>? AlloyOn;
}

/// <summary>Whether a knob's need is met, and the one declared reason a row gives when it is not.</summary>
public static class KnobNeeds
{
    /// <summary>The reason a row that needs Alloy shows while Alloy is off.</summary>
    public const string AlloyOffReason = "Needs the Alloy renderer.";

    private static KnobNeedsState _state = new();

    /// <summary>A self-check's seam: decide availability as if Alloy is off (true) or undecided (false); returns the
    /// previous setting so the caller can put it back.</summary>
    internal static bool AssumeAlloyOff(bool off)
    {
        bool was = _state.AlloyOff;
        _state = new KnobNeedsState { AlloyOff = off, AlloyOn = _state.AlloyOn };
        return was;
    }

    /// <summary>The renderer registers its live verdict here, so a row that needs Alloy greys in a session that
    /// runs vanilla.</summary>
    internal static void WatchAlloy(Func<bool> on) => _state = new KnobNeedsState { AlloyOff = _state.AlloyOff, AlloyOn = on };

    /// <summary>True when <paramref name="need"/> is not met now.</summary>
    public static bool Missing(KnobNeed need)
        => need == KnobNeed.Alloy && (_state.AlloyOff || _state.AlloyOn is { } on && !on());

    /// <summary>The declared reason for an unmet <paramref name="need"/>; null for <see cref="KnobNeed.None"/>.</summary>
    public static string? Reason(KnobNeed need) => need == KnobNeed.Alloy ? AlloyOffReason : null;
}
