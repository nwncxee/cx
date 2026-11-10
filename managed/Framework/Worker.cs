using System;
using System.Threading;

namespace CX.Framework;

/// <summary>Every CX thread starts here. On Linux a new thread inherits its creator's CPU affinity mask, so a
/// worker the main thread starts after <c>pin=N</c> took effect would sit on the game's core and compete with
/// it; this clears the mask inside the new thread, before its body runs.</summary>
public static unsafe class Worker
{
    /// <summary>The mask a worker inherits is the machine's, not the pinned creator's. Measured, because the
    /// inheritance is the kernel's behaviour and the clear is one call that is easy to lose.</summary>
    public static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "Worker.SelfCheck");
        byte* saved = stackalloc byte[Affinity.MaskBytes];
        int had = Affinity.ReadThis(saved);
        if (had < 2)
        {
            check.Skip($"this thread may run on {(had < 0 ? "no readable set of" : had.ToString())} CPUs, so a "
                       + "one-core pin cannot be told from the mask it inherited");
            return true;
        }
        int cpu = Affinity.CurrentCpu;
        bool pinned = Affinity.PinThis(cpu);
        bool restored;
        try
        {
            // A host that will not pin cannot be asked whether a child inherits the pin: nothing is pinned.
            if (!pinned) return check.Skip($"the kernel refused a pin of this thread to cpu {cpu}");
            check.Expect(Cpus() == 1, "the pinned thread may still run on more than one CPU, so the pin did not take");
            int childCpus = 0;
            Start("cx-worker-check", () => childCpus = Cpus()).Join();
            check.Expect(childCpus > 1,
                         $"a worker started by a thread pinned to cpu {cpu} may run on {childCpus} CPU(s) -- it "
                         + "inherited the pin and will compete with the game's main thread");
        }
        finally
        {
            restored = Affinity.SetThis(saved);
        }
        // This runs ON the thread that becomes the game's main thread: a mask not given back is the session.
        check.Expect(restored, $"the mask this check pinned to cpu {cpu} could not be given back");
        if (!restored)
            log.Always($"Worker.SelfCheck: the CPU mask of THIS thread could not be restored after the pin to cpu "
                       + $"{cpu} -- it may run on one core for the rest of the session");
        log.Info(check.Ok ? "Worker.SelfCheck: passed"
                          : "Worker.SelfCheck: FAILED -- a CX worker would share the pinned main thread's core");
        return check.Ok;
    }

    /// <summary>How many CPUs the calling thread may run on, or -1 where there is no mask to read.</summary>
    private static int Cpus()
    {
        byte* m = stackalloc byte[Affinity.MaskBytes];
        return Affinity.ReadThis(m);
    }

    /// <summary>Starts a named background thread and hands it back, for a caller that joins it.</summary>
    public static Thread Start(string name, Action body, ThreadPriority priority = ThreadPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(body);
#pragma warning disable RS0030 // the one Thread construction: it is what wraps every body in the mask clear
        var t = new Thread(() =>
        {
            Affinity.ClearThis();
            body();
        })
        { IsBackground = true, Name = name };
#pragma warning restore RS0030
        if (priority != ThreadPriority.Normal) t.Priority = priority;
        t.Start();
        return t;
    }

    /// <summary>Queues one piece of work on the thread pool, with the mask cleared inside it. The runtime
    /// grows a pool thread as a child of whichever thread queued the work, so work queued from the pinned main
    /// thread lands on the game's core -- and stays there, for as long as the pool keeps that thread.</summary>
    public static void Queue<T>(Action<T> body, T state)
    {
        ArgumentNullException.ThrowIfNull(body);
#pragma warning disable RS0030 // the one pool queue: it is what wraps every body in the mask clear
        ThreadPool.QueueUserWorkItem(static a => { Affinity.ClearThis(); a.Body(a.State); },
                                     (Body: body, State: state), preferLocal: false);
#pragma warning restore RS0030
    }

    /// <inheritdoc cref="Queue{T}(Action{T}, T)"/>
    public static void Queue(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        Queue(static b => b(), body);
    }
}
