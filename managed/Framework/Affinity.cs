using System;
using System.Runtime.InteropServices;

namespace CX.Framework;

/// <summary>The main-thread pin (<c>pin=N</c> in cx.cfg, -1 = off): the load balancer migrates the game's main
/// thread between cores tens of times a second, each move a cold cache, and a pinned thread cannot be migrated.
/// Pinned at the FIRST FRAME, not at load -- a thread inherits its creator's mask and the engine spawns its own
/// between the two. ponytail: a later loader thread inherits the pin and shares the core; hook pthread_create.</summary>
public static unsafe class Affinity
{
    [DllImport("libc.so.6")] private static extern int sched_setaffinity(int pid, nuint size, byte* mask);
    [DllImport("libc.so.6")] private static extern int sched_getcpu();
    private const int MaskBytes = 128;   // 1024 CPUs, glibc's cpu_set_t

    /// <summary>Pin the calling thread to one CPU. False if the kernel refused.</summary>
    public static bool PinThis(int cpu)
    {
        if (OperatingSystem.IsWindows() || cpu < 0 || cpu >= MaskBytes * 8) return false;
        byte* m = stackalloc byte[MaskBytes];
        for (int i = 0; i < MaskBytes; i++) m[i] = 0;
        m[cpu >> 3] = (byte)(1 << (cpu & 7));
        return sched_setaffinity(0, MaskBytes, m) == 0;
    }

    /// <summary>Let the calling thread run anywhere: for a worker created by a pinned thread.</summary>
    public static void ClearThis()
    {
        if (OperatingSystem.IsWindows()) return;
        int n = Math.Min(Environment.ProcessorCount, MaskBytes * 8);
        byte* m = stackalloc byte[MaskBytes];
        for (int i = 0; i < MaskBytes; i++) m[i] = 0;
        for (int c = 0; c < n; c++) m[c >> 3] |= (byte)(1 << (c & 7));
        sched_setaffinity(0, MaskBytes, m);
    }

    public static int CurrentCpu => OperatingSystem.IsWindows() ? -1 : sched_getcpu();
}
