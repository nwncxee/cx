using System.Threading;

namespace CX.Framework;

/// <summary>
/// The boot hold on the <c>cx.cfg</c> writer. Boot sets real knobs -- a self-check moves one and restores it --
/// and nobody chose those values, so every arm boot raises is counted and dropped. A first run has no text to
/// keep, and its arm is the one that outlives the hold. A set arms on whatever thread set it, so the flag is
/// volatile and the count interlocked.
/// </summary>
public sealed class BootWriteHold
{
    private volatile bool _holding = true;
    private volatile bool _firstRunPending;
    private int _heldArms;

    /// <summary>True until <see cref="End"/>.</summary>
    public bool Holding => _holding;

    /// <summary>Arms this hold took and dropped.</summary>
    public int HeldArms => Volatile.Read(ref _heldArms);

    /// <summary>True once <see cref="ArmFirstRun"/> has run: the file was missing when it was read.</summary>
    public bool FirstRunPending => _firstRunPending;

    /// <summary>An arm. True when the caller should schedule its debounce, false when the hold took it.</summary>
    public bool Arm()
    {
        if (!_holding) return true;
        Interlocked.Increment(ref _heldArms);
        return false;
    }

    /// <summary>The file was missing: one write is owed once every group has declared.</summary>
    public void ArmFirstRun() => _firstRunPending = true;

    /// <summary>Lift the hold. True when a first run's one write is still owed; false on a later call.</summary>
    public bool End()
    {
        if (!_holding) return false;
        _holding = false;
        return _firstRunPending;
    }
}
