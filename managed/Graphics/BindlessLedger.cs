using System.Collections.Generic;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Graphics;

/// <summary>ARB_bindless_texture's rules, checked on every GPU: AMD's driver faults on breaches NVIDIA and Mesa
/// let pass, so a breach is logged here as a VIOLATION. Shims over the five texture-lifetime pointers in
/// <see cref="Gl"/>; it reports, and every call still reaches the driver unchanged.</summary>
internal static unsafe class BindlessLedger
{
    /// <summary>The breaches, each one an AMD fault that shipped.</summary>
    internal enum Rule
    {
        WrongContext, ReResident, NotResident, ParamOnHandled, DeleteResident, TableNotResident, Count,
    }

    private static readonly string[] Why =
    [
        "a residency call outside Alloy's 4.6 context: residency is per context, so it does nothing there and the handle stays resident",
        "a handle made resident twice (INVALID_OPERATION)",
        "a handle made non-resident that is not resident in this context (INVALID_OPERATION)",
        "a sampler or texture parameter set on a texture that has a handle -- frozen by the spec, INVALID_OPERATION",
        "a texture deleted while its handle is still resident -- make it non-resident first",
        "the GPU handle table carries a handle that is not resident, or a null one -- the shader samples it",
    ];

    /// <summary>Every handle minted and which are resident, keyed both ways, plus the driver's real entry points.</summary>
    private sealed class LedgerState
    {
        public readonly Dictionary<uint, ulong> HandleOf = new(4096);
        public readonly HashSet<ulong> Resident = new(4096);
        public readonly long[] Hits = new long[(int)Rule.Count];
        public ExtenderLog? Log;
        public delegate* unmanaged<uint, ulong> Get;
        public delegate* unmanaged<ulong, void> MakeResident, MakeNonResident;
        public delegate* unmanaged<uint, uint, int, void> Param;
        public delegate* unmanaged<int, uint*, void> Delete;
    }

    private static LedgerState _s = new();
    private static readonly object _lock = new();

    /// <summary>Lines logged per rule; the count keeps going past it.</summary>
    private const int LoudPerRule = 4;

    /// <summary>Breaches since boot, every rule.</summary>
    internal static long Total
    {
        get { long n = 0; foreach (long h in _s.Hits) n += h; return n; }
    }

    /// <summary>Swap the shims in over the resolved pointers. From <see cref="Gl.Init"/>, once.</summary>
    internal static void Install(ExtenderLog log)
    {
        LedgerState s = _s;
        if (s.Log != null) return;
        s.Log = log;
        if (Gl.GetTextureHandleARB != null) { s.Get = Gl.GetTextureHandleARB; Gl.GetTextureHandleARB = &OnGet; }
        if (Gl.MakeTextureHandleResidentARB != null) { s.MakeResident = Gl.MakeTextureHandleResidentARB; Gl.MakeTextureHandleResidentARB = &OnResident; }
        if (Gl.MakeTextureHandleNonResidentARB != null) { s.MakeNonResident = Gl.MakeTextureHandleNonResidentARB; Gl.MakeTextureHandleNonResidentARB = &OnNonResident; }
        if (Gl.TextureParameteri != null) { s.Param = Gl.TextureParameteri; Gl.TextureParameteri = &OnParam; }
        if (Gl.DeleteTextures != null) { s.Delete = Gl.DeleteTextures; Gl.DeleteTextures = &OnDelete; }
    }

    [UnmanagedCallersOnly]
    private static ulong OnGet(uint tex)
    {
        ulong v = _s.Get(tex);
        lock (_lock) Minted(_s, tex, v);
        return v;
    }

    [UnmanagedCallersOnly]
    private static void OnResident(ulong h)
    {
        lock (_lock) Resident(_s, h, GlContextService.Current46);
        _s.MakeResident(h);
    }

    [UnmanagedCallersOnly]
    private static void OnNonResident(ulong h)
    {
        lock (_lock) NonResident(_s, h, GlContextService.Current46);
        _s.MakeNonResident(h);
    }

    [UnmanagedCallersOnly]
    private static void OnParam(uint tex, uint pname, int v)
    {
        lock (_lock) Param(_s, tex);
        _s.Param(tex, pname, v);
    }

    [UnmanagedCallersOnly]
    private static void OnDelete(int n, uint* names)
    {
        lock (_lock) for (int i = 0; i < n; i++) Deleted(_s, names[i]);
        _s.Delete(n, names);
    }

    /// <summary>The engine deleted this texture in its own context, where no shim sees it: drop it from the ledger.</summary>
    internal static void Forget(uint tex)
    {
        lock (_lock)
            if (_s.HandleOf.Remove(tex, out ulong h)) _s.Resident.Remove(h);
    }

    /// <summary>Under <c>alloy.gldebug=1</c>: every entry of a GPU handle table about to be uploaded is resident.</summary>
    internal static void CheckTable(ulong* p, long n, string table)
    {
        if (!GlContextService.Debug) return;
        long bad = 0; ulong first = 0;
        lock (_lock)
            for (long k = 0; k < n; k++)
                if (p[k] == 0 || !_s.Resident.Contains(p[k])) { if (bad++ == 0) first = p[k]; }
        if (bad > 0) lock (_lock) Hit(_s, Rule.TableNotResident, first, 0, $"{bad} of {n} entries of {table}");
    }

    /// <summary>A handle belongs to the share group, so minting is legal in either context; only residency is not.</summary>
    private static void Minted(LedgerState s, uint tex, ulong v)
    {
        if (v != 0) s.HandleOf[tex] = v;
    }

    private static void Resident(LedgerState s, ulong h, bool here)
    {
        if (!here) Hit(s, Rule.WrongContext, h, 0, "glMakeTextureHandleResidentARB");
        else if (!s.Resident.Add(h)) Hit(s, Rule.ReResident, h, 0, "glMakeTextureHandleResidentARB");
    }

    private static void NonResident(LedgerState s, ulong h, bool here)
    {
        if (!here) Hit(s, Rule.WrongContext, h, 0, "glMakeTextureHandleNonResidentARB");
        else if (!s.Resident.Remove(h)) Hit(s, Rule.NotResident, h, 0, "glMakeTextureHandleNonResidentARB");
    }

    private static void Param(LedgerState s, uint tex)
    {
        if (s.HandleOf.TryGetValue(tex, out ulong h)) Hit(s, Rule.ParamOnHandled, h, tex, "glTextureParameteri");
    }

    private static void Deleted(LedgerState s, uint tex)
    {
        if (tex == 0 || !s.HandleOf.Remove(tex, out ulong h)) return;
        if (s.Resident.Remove(h)) Hit(s, Rule.DeleteResident, h, tex, "glDeleteTextures");
    }

    private static void Hit(LedgerState s, Rule r, ulong h, uint tex, string call)
    {
        long n = ++s.Hits[(int)r];
        if (n <= LoudPerRule)
            s.Log?.Always($"BindlessLedger: VIOLATION {r} at {call} (handle 0x{h:x}, texture {tex}): {Why[(int)r]}. "
                        + $"AMD's driver faults on this; NVIDIA and Mesa let it pass ({n} so far{(n == LoudPerRule ? ", quiet from here" : "")})");
    }

    /// <summary>Each rule fires on its breach and stays quiet on the legal sequence. Oracle: the hand-written
    /// call sequences below and the extension spec's rules, run on a scratch ledger with no GL and no log.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        LedgerState s = new();
        long Hits(Rule r) => s.Hits[(int)r];

        Minted(s, 7, 0x70); Resident(s, 0x70, true); NonResident(s, 0x70, true); Deleted(s, 7);
        long clean = 0; foreach (long h in s.Hits) clean += h;
        check.Expect(clean == 0, "the legal mint, resident, non-resident, delete sequence reported a breach");

        Minted(s, 8, 0x80); Resident(s, 0x80, true);
        NonResident(s, 0x80, false);
        check.Expect(Hits(Rule.WrongContext) == 1, "a non-resident call from the engine's context was not reported");
        Resident(s, 0x80, true);
        check.Expect(Hits(Rule.ReResident) == 1, "a second resident call on one handle was not reported");
        Param(s, 8);
        check.Expect(Hits(Rule.ParamOnHandled) == 1, "a parameter on a handled texture was not reported");
        Param(s, 9);
        check.Expect(Hits(Rule.ParamOnHandled) == 1, "a parameter on a texture with no handle was reported");
        Deleted(s, 8);
        check.Expect(Hits(Rule.DeleteResident) == 1, "a delete under a resident handle was not reported");
        NonResident(s, 0x80, true);
        check.Expect(Hits(Rule.NotResident) == 1, "a non-resident call on a deleted texture's handle was not reported");

        log.Info(check.Ok ? "BindlessLedger.SelfCheck: passed" : "BindlessLedger.SelfCheck: FAILED");
        return check.Ok;
    }
}
