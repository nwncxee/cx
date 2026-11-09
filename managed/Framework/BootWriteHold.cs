namespace CX.Framework;

/// <summary>
/// The boot hold on the <c>cx.cfg</c> writer. Boot sets real knobs -- a self-check moves one and restores it --
/// and nobody chose those values, so every arm boot raises is counted and dropped. A first run has no text to
/// keep, and its arm is the one that outlives the hold.
/// </summary>
public sealed class BootWriteHold
{
    /// <summary>True until <see cref="End"/>.</summary>
    public bool Holding { get; private set; } = true;

    /// <summary>Arms this hold took and dropped.</summary>
    public int HeldArms { get; private set; }

    /// <summary>True once <see cref="ArmFirstRun"/> has run: the file was missing when it was read.</summary>
    public bool FirstRunPending { get; private set; }

    /// <summary>An arm. True when the caller should schedule its debounce, false when the hold took it.</summary>
    public bool Arm()
    {
        if (!Holding) return true;
        HeldArms++;
        return false;
    }

    /// <summary>The file was missing: one write is owed once every group has declared.</summary>
    public void ArmFirstRun() => FirstRunPending = true;

    /// <summary>Lift the hold. True when a first run's one write is still owed; false on a later call.</summary>
    public bool End()
    {
        if (!Holding) return false;
        Holding = false;
        return FirstRunPending;
    }
}
