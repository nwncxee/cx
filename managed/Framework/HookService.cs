using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CX.Framework;

/// <summary>
/// Wraps funchook. A service calls <see cref="Install"/> in its Initialize; the host
/// calls <see cref="Commit"/> once, after all services are up. Pass the LINUX (Itanium)
/// mangled name (<c>./sym.sh -m &lt;name&gt;</c>); SymbolMap maps it to the running client.
/// </summary>
public sealed class HookService : IDisposable
{
    private const string Lib = "funchook";

    [DllImport(Lib)] private static extern IntPtr funchook_create();
    [DllImport(Lib)] private static extern int funchook_prepare(IntPtr fh, ref IntPtr target, IntPtr hook);
    [DllImport(Lib)] private static extern int funchook_install(IntPtr fh, int flags);
    [DllImport(Lib)] private static extern int funchook_uninstall(IntPtr fh, int flags);
    [DllImport(Lib)] private static extern int funchook_destroy(IntPtr fh);
    [DllImport(Lib)] private static extern int funchook_set_debug_file([MarshalAs(UnmanagedType.LPStr)] string name);

    private readonly ExtenderLog _log;
    private readonly IntPtr _fh;
    private bool _committed;

    public HookService(ExtenderLog log)
    {
        _log = log;
        // funchook's own trace beside the log (a hook that faults at first call); this first touch of General declares the group.
        if (General.HookDebug.Boot) FollowLogs(UserFolders.Logs);
        _fh = funchook_create();
        SelfCheck(log);
    }

    /// <summary>Point funchook's trace at funchook.log in <paramref name="logs"/>, beside cx.log, when
    /// <c>general.hookdebug</c> asked for it.</summary>
    internal static void FollowLogs(string logs)
    {
        if (!General.HookDebug.Boot) return;
        try { Directory.CreateDirectory(logs); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        funchook_set_debug_file(Path.Combine(logs, "funchook.log"));
    }

    private readonly Dictionary<IntPtr, string> _prepared = new();
    private readonly Dictionary<IntPtr, List<Link>> _chains = new();
    private int _seq;

    /// <summary>One subscriber of a chained target. <see cref="Slot"/> holds the pointer that
    /// subscriber calls through: the next-lower handler, or the trampoline for the bottom layer.</summary>
    private readonly struct Link(int priority, int seq, IntPtr handler, IntPtr slot)
    {
        public readonly int Priority = priority;
        public readonly int Seq = seq;
        public readonly IntPtr Handler = handler;
        public readonly IntPtr Slot = slot;
    }

    /// <summary>
    /// Hooks <paramref name="linux"/> with <paramref name="handler"/> (a pointer to an
    /// [UnmanagedCallersOnly] static matching the target ABI) as its ONLY owner. Returns the trampoline
    /// (the original) to call through, or Zero if the symbol didn't resolve or is already taken.
    /// Symbols with more than one consumer use <see cref="Chain"/>.
    /// </summary>
    public IntPtr Install(string linux, IntPtr handler)
    {
        IntPtr target = SymbolMap.Resolve(linux, _log);
        if (target == IntPtr.Zero) return IntPtr.Zero;

        // funchook does NOT detect duplicate targets: a second prepare's install overwrites the first
        // patch and silently orphans the first handler (this once killed the part-draw capture). One
        // owner per function; co-consumers use Chain instead.
        if (_chains.ContainsKey(target) || !_prepared.TryAdd(target, linux))
        {
            _log.Info($"hook: DUPLICATE target refused for {linux} -- already prepared; use Chain instead");
            return IntPtr.Zero;
        }

        int rc = funchook_prepare(_fh, ref target, handler); // target -> trampoline on success
        if (rc != 0)
        {
            _log.Info($"hook: prepare failed (rc={rc}): {linux}");
            return IntPtr.Zero;
        }

        _log.Info($"hook: prepared {linux}");
        return target;
    }

    /// <summary>
    /// <see cref="Install"/> by ADDRESS, for a function that is not the engine's -- an export of a library CX
    /// ships beside itself, such as the pinned OpenAL. <paramref name="label"/> names it in the log.
    /// </summary>
    public IntPtr InstallAt(IntPtr target, IntPtr handler, string label)
    {
        if (target == IntPtr.Zero) return IntPtr.Zero;
        if (_chains.ContainsKey(target) || !_prepared.TryAdd(target, label))
        {
            _log.Info($"hook: DUPLICATE target refused for {label} -- already prepared");
            return IntPtr.Zero;
        }
        int rc = funchook_prepare(_fh, ref target, handler);
        if (rc != 0)
        {
            _log.Info($"hook: prepare failed (rc={rc}): {label}");
            return IntPtr.Zero;
        }
        _log.Info($"hook: prepared {label}");
        return target;
    }

    /// <summary>
    /// Subscribes <paramref name="handler"/> to <paramref name="linux"/> alongside other subscribers, highest
    /// <paramref name="priority"/> outermost. Returns a slot the caller keeps and calls through at runtime
    /// (<c>*slot</c> = the next-lower subscriber, or the original at the bottom); not calling it swallows the call
    /// below. Null if the symbol did not resolve. The slot is filled at <see cref="Commit"/>: never deref it before.
    /// </summary>
    public unsafe IntPtr* Chain(string linux, IntPtr handler, int priority)
    {
        IntPtr target = SymbolMap.Resolve(linux, _log);
        if (target == IntPtr.Zero) return null;

        if (_prepared.ContainsKey(target))
        {
            _log.Info($"hook: chain refused for {linux} -- target already has a single Install() owner");
            return null;
        }

        if (!_chains.TryGetValue(target, out var links)) _chains[target] = links = new List<Link>();
        // Never freed: slots live as long as the hooks do, and hot handlers deref them every frame.
        IntPtr* slot = (IntPtr*)NativeMemory.AllocZeroed((nuint)sizeof(IntPtr));
        links.Add(new Link(priority, _seq++, handler, (IntPtr)slot));
        _log.Info($"hook: chained {linux} at priority {priority} (layer {links.Count})");
        return slot;
    }

    /// <summary>Highest priority first; equal priorities keep subscription order.</summary>
    private static void Order(List<Link> links) =>
        links.Sort((a, b) => a.Priority != b.Priority ? b.Priority.CompareTo(a.Priority)
                                                      : a.Seq.CompareTo(b.Seq));

    /// <summary>Each layer's slot points at the next layer down; the bottom one at the trampoline.</summary>
    private static unsafe void Wire(List<Link> links, IntPtr tramp)
    {
        for (int i = 0; i < links.Count; i++)
            *(IntPtr*)links[i].Slot = i + 1 < links.Count ? links[i + 1].Handler : tramp;
    }

    /// <summary>Order + slot wiring check (the part that has no runtime symbol to lean on).</summary>
    public static unsafe void SelfCheck(ExtenderLog log)
    {
        IntPtr* slots = stackalloc IntPtr[3];
        var links = new List<Link>
        {
            new(10, 0, (IntPtr)0xA0, (IntPtr)(slots + 0)),
            new(90, 1, (IntPtr)0xB0, (IntPtr)(slots + 1)),
            new(90, 2, (IntPtr)0xC0, (IntPtr)(slots + 2)),
        };
        Order(links);
        Wire(links, (IntPtr)0xEE);
        bool ok = links[0].Handler == 0xB0 && links[1].Handler == 0xC0 && links[2].Handler == 0xA0
                  && slots[1] == 0xC0    // 0xB0 (top) calls 0xC0
                  && slots[2] == 0xA0    // 0xC0 calls 0xA0
                  && slots[0] == 0xEE;   // 0xA0 (bottom) calls the original
        log.Info(ok ? "HookService.SelfCheck: passed" : "HookService.SelfCheck: FAILED -- chain order/wiring wrong");
    }

    /// <summary>Installs every prepared hook at once. Idempotent.</summary>
    public unsafe void Commit()
    {
        if (_committed) return;
        _committed = true;

        foreach (var (target, links) in _chains)
        {
            Order(links);
            // One funchook patch per target, pointing at the outermost handler; the rest of the chain is
            // just slot wiring -- layer i calls layer i+1, and the last one calls the trampoline.
            IntPtr tramp = target;
            int prc = funchook_prepare(_fh, ref tramp, links[0].Handler);
            if (prc != 0)
            {
                _log.Info($"hook: chain prepare failed (rc={prc}) for {links.Count} subscribers");
                continue;
            }
            Wire(links, tramp);
        }

        int rc = funchook_install(_fh, 0);
        _log.Info($"hook: install rc={rc}");
    }

    public void Dispose()
    {
        funchook_uninstall(_fh, 0);
        funchook_destroy(_fh);
    }
}
