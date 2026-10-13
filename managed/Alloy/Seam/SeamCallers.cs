using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CX.Alloy.Seam;

/// <summary>Who called us, by name. A seam slot is entered from the GAME, so the managed stack says nothing;
/// <c>backtrace</c> plus <c>dladdr</c> turns the native frame into an exported symbol, exactly, because the
/// client is unstripped. For ATTRIBUTION on cold refusal paths only, and every result is interned.
/// <para>Linux only -- on Windows the attribution is absent and the line says so.</para></summary>
internal static unsafe class SeamCallers
{
    /// <summary>Interned caller names; the id is what a shell header stores.</summary>
    private static readonly List<string> _names = new() { "?" };
    private static readonly Dictionary<long, int> _byAddr = new();
    private static bool _broken;

    /// <summary>How deep the native stack walk goes. Four frames is enough to step over this helper, the slot
    /// body and the entry thunk and land in the engine.</summary>
    private const int Depth = 12;

    /// <summary>The interned id of the first frame OUTSIDE cx_managed -- the game function that called us --
    /// or 0 when it cannot be named.</summary>
    internal static int Here()
    {
        if (_broken || OperatingSystem.IsWindows()) return 0;
        nint* frames = stackalloc nint[Depth];
        int n;
        try { n = Backtrace(frames, Depth); }
        catch { _broken = true; return 0; }
        if (n <= 0) return 0;
        for (int i = 1; i < n; i++)
        {
            nint pc = frames[i];
            if (pc == 0) continue;
            lock (_byAddr)
            {
                if (_byAddr.TryGetValue(pc, out int known)) { if (known != 0) return known; continue; }
            }
            string? name = Resolve(pc, out bool managed);
            int id = 0;
            if (!managed && name != null)
            {
                lock (_byAddr)
                {
                    _names.Add(name);
                    id = _names.Count - 1;
                    _byAddr[pc] = id;
                }
                return id;
            }
            lock (_byAddr) _byAddr[pc] = 0;
        }
        return 0;
    }

    /// <summary>The first few GAME frames above a slot, newest first, as one string. The immediate caller of a
    /// position setter is always the same forwarder; the frame that chose the value is two or three up. Cold --
    /// a refusal path and a one-shot diagnostic only.</summary>
    internal static string Chain(int want)
    {
        if (_broken || OperatingSystem.IsWindows()) return "";
        nint* frames = stackalloc nint[Depth];
        int n;
        try { n = Backtrace(frames, Depth); }
        catch { _broken = true; return ""; }
        string s = "";
        int got = 0;
        for (int i = 1; i < n && got < want; i++)
        {
            if (frames[i] == 0) continue;
            string? name = Resolve(frames[i], out bool managed);
            if (managed || name == null) continue;
            s += (got == 0 ? "" : "<-") + name;
            got++;
        }
        return s;
    }

    /// <summary>The name of <paramref name="id"/>, or <c>"?"</c>.</summary>
    internal static string Name(int id)
    {
        lock (_byAddr) return (uint)id < (uint)_names.Count ? _names[id] : "?";
    }

    /// <summary>Resolve one program counter. <paramref name="managed"/> is true for a frame inside our own
    /// image, which the walk steps over to reach the caller that matters.</summary>
    private static string? Resolve(nint pc, out bool managed)
    {
        lock (_resolved)
            if (_resolved.TryGetValue(pc, out var hit)) { managed = hit.Managed; return hit.Name; }
        string? name = ResolveUncached(pc, out managed);
        lock (_resolved) _resolved[pc] = (name, managed);
        return name;
    }

    /// <summary>Every program counter <see cref="Resolve"/> has answered: <c>dladdr</c> scans a whole symbol
    /// table per call, and the same few call sites repeat.</summary>
    private static readonly Dictionary<nint, (string? Name, bool Managed)> _resolved = new();

    private static string? ResolveUncached(nint pc, out bool managed)
    {
        managed = false;
        if (Dladdr(pc, out DlInfo info) == 0) return null;
        string? file = info.FName == 0 ? null : Marshal.PtrToStringAnsi(info.FName);
        if (file != null && file.Contains("cx_managed", StringComparison.Ordinal)) { managed = true; return null; }
        if (info.SName == 0) return file == null ? null : $"{System.IO.Path.GetFileName(file)}+0x{pc - info.FBase:x}";
        string sym = Marshal.PtrToStringAnsi(info.SName) ?? "?";
        long off = pc - info.SAddr;
        return off == 0 ? sym : $"{sym}+0x{off:x}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DlInfo { public nint FName, FBase, SName, SAddr; }

    [DllImport("libdl.so.2", EntryPoint = "dladdr")]
    private static extern int Dladdr(nint addr, out DlInfo info);

    [DllImport("libc.so.6", EntryPoint = "backtrace")]
    private static extern int Backtrace(nint* buffer, int size);
}
