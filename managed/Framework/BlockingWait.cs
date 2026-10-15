namespace CX.Framework;

/// <summary>The one way a thread blocks. The caller states why blocking is safe there: a worker, a self-check, a
/// shutdown path. The game's main thread never blocks, because every frame hook runs on it.</summary>
public static class BlockingWait
{
    /// <summary>Sleeps the calling thread for <paramref name="ms"/> milliseconds. <paramref name="why"/> is read in review.</summary>
    public static void Sleep(int ms, string why)
    {
        _ = why;
#pragma warning disable RS0030 // BlockingWait is the one place a thread sleeps
        Thread.Sleep(ms);
#pragma warning restore RS0030
    }
}
