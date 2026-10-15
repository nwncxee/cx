using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CX.Framework;

namespace CX.Alloy.Seam;

public static unsafe partial class Facade
{
    /// <summary>A 16-byte all-float return, which SysV classifies into xmm0:xmm1 with no hidden buffer -- the
    /// shape every by-value <c>Vector</c>, <c>Quaternion</c> and <c>Plane</c> return takes on Linux.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Quad { public float X, Y, Z, W; }

    private static nint _tableBase;
    private static nint _tableBytes;
    private static bool _built;
    private static readonly nint[] _table = new nint[6];
    private static readonly nint[] _poison = new nint[6];
    private static long _unimplementedCalls;

    /// <summary>One bit per word of the vtable block, set at the twelve words that are an ADDRESS POINT --
    /// what <see cref="Shell"/> tests a vptr against. A bitmap rather than a scan of <see cref="_table"/> and
    /// <see cref="_poison"/> because <c>Shell</c> runs on every slot call.</summary>
    private static ulong[] _pointBits = Array.Empty<ulong>();

    /// <summary>Record <paramref name="point"/> as one of ours.</summary>
    private static void PointBit(nint point)
    {
        int w = (int)((point - _tableBase) >> 3);
        _pointBits[w >> 6] |= 1UL << (w & 63);
    }

    /// <summary>The address point of <paramref name="t"/>'s built table -- the value a shell's vptr carries.
    /// The two words before it are the Itanium prefix: offset-to-top, then a null typeinfo.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint Table(SeamTable t) => _table[(int)t];

    /// <summary>The POISON twin of <paramref name="t"/>: the same shape, every slot the refusal stub. A
    /// retired shell's vptrs are swapped to these, so a use-after-free cannot reach a live slot body at all
    /// -- which is the only way to cover the slots that answer from the shell's own words and never resolve a
    /// handle (<c>GetName</c>, <c>GetScene</c>, the external-data pair, the by-value getters).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint Poison(SeamTable t) => _poison[(int)t];

    /// <summary>True once <see cref="Build"/> has run; nothing may be minted before.</summary>
    public static bool Built => _built;

    /// <summary>Lay out every table in this OS's slot order from the registered implementations.
    /// One contiguous block holds them all, which is also what makes <see cref="Shell"/>'s
    /// "is this vptr one of ours" a single range test. Returns the number of slots left unimplemented, each
    /// named in the log: an unimplemented slot is a loud refusal at boot, not a surprise at the first call.</summary>
    internal static int Build(SeamRegistry reg, ExtenderLog log, bool census = true)
    {
        if (_built) return 0;
        IntPtr[] impl = reg.Impls;
        SeamTable[] tables = { SeamTable.Gob, SeamTable.Scene, SeamTable.Camera, SeamTable.StringGob, SeamTable.CAurString, SeamTable.CameraGob };

        int words = 0;
        foreach (SeamTable t in tables) words += 2 * (2 + SeamVtables.Order(t).Length);   // live + poison twin
        _tableBytes = words * sizeof(nint);
        void* block = NativeMemory.AlignedAlloc((nuint)_tableBytes, 64);
        // _tableBytes back to 0 with it: Shell's range test is what keeps it off the (still empty) point
        // bitmap, so a block that never existed must read as a block of no bytes.
        if (block == null)
        {
            _tableBytes = 0;
            log.Always("seam: could not allocate the vtable block -- the seam stands down");
            return -1;
        }
        NativeMemory.Fill(block, (nuint)_tableBytes, 0);
        _tableBase = (nint)block;
        _pointBits = new ulong[(words + 63) / 64];

        // One refusal stub PER TABLE. A shared stub cannot say which slot was called -- it is handed only
        // `this` -- but it can say which interface, and the boot census already lists that table's
        // unimplemented slots by name, so the pair identifies the call without a stub per slot.
        nint byValue = OperatingSystem.IsWindows()
            ? (nint)(delegate* unmanaged<nint, nint, nint>)&PoisonSret
            : (nint)(delegate* unmanaged<nint, Quad>)&PoisonQuad;

        int missing = 0;
        var slots = new Span<nint>(block, words);
        int cursor = 0;
        foreach (SeamTable t in tables)
        {
            nint zero = Refusal(t);
            ReadOnlySpan<short> order = SeamVtables.Order(t);
            slots[cursor] = SeamVtables.OffsetToTop(t);
            slots[cursor + 1] = 0;
            int point = cursor + 2;
            _table[(int)t] = (nint)Unsafe.AsPointer(ref slots[point]);
            PointBit(_table[(int)t]);
            var unfilled = new StringBuilder();
            for (int s = 0; s < order.Length; s++)
            {
                int logical = order[s];
                nint fn = logical >= 0 && logical < impl.Length ? impl[logical] : IntPtr.Zero;
                if (fn == IntPtr.Zero)
                {
                    fn = logical >= 0 && SeamVtables.ByValue(logical) ? byValue : zero;
                    missing++;
                    if (unfilled.Length < 1200)
                    {
                        if (unfilled.Length > 0) unfilled.Append(", ");
                        unfilled.Append(SeamVtables.Name(logical));
                    }
                }
                slots[point + s] = fn;
            }
            if (unfilled.Length > 0 && census)
                log.Always($"seam: {t} has unimplemented slots, each a counted refusal when called: {unfilled}");
            cursor = point + order.Length;

            // The poison twin, laid out right after: same offset-to-top so a sub-object pointer still
            // normalises, same length so a slot index cannot run past it, every entry a refusal.
            slots[cursor] = SeamVtables.OffsetToTop(t);
            slots[cursor + 1] = 0;
            int pPoint = cursor + 2;
            _poison[(int)t] = (nint)Unsafe.AsPointer(ref slots[pPoint]);
            PointBit(_poison[(int)t]);
            for (int s2 = 0; s2 < order.Length; s2++)
            {
                int logical = order[s2];
                slots[pPoint + s2] = logical >= 0 && SeamVtables.ByValue(logical) ? byValue : zero;
            }
            cursor = pPoint + order.Length;
        }
        _built = true;
        if (census) log.Info($"seam: {tables.Length} vtables built for {(OperatingSystem.IsWindows() ? "Windows" : "Linux")} slot order, "
               + $"{words - 2 * tables.Length} slots, {missing} unimplemented, {_tableBytes} bytes");
        return missing;
    }

    /// <summary>Hand the vtable block back; only the self-check and shutdown call this.</summary>
    internal static void DropTables()
    {
        if (_tableBase != 0) NativeMemory.AlignedFree((void*)_tableBase);
        _tableBase = 0; _tableBytes = 0; _built = false;
        _pointBits = Array.Empty<ulong>();
        Array.Clear(_table);
    }

    /// <summary>Calls that landed on a slot no stream implemented.</summary>
    public static long UnimplementedCalls => _unimplementedCalls;

    /// <summary>The refusal entry point for <paramref name="t"/>'s unimplemented slots.</summary>
    private static nint Refusal(SeamTable t) => t switch
    {
        SeamTable.Gob => (nint)(delegate* unmanaged<nint, nint>)&PoisonGob,
        SeamTable.Scene => (nint)(delegate* unmanaged<nint, nint>)&PoisonScene,
        SeamTable.Camera => (nint)(delegate* unmanaged<nint, nint>)&PoisonCamera,
        SeamTable.StringGob => (nint)(delegate* unmanaged<nint, nint>)&PoisonStringGob,
        SeamTable.CAurString => (nint)(delegate* unmanaged<nint, nint>)&PoisonCAurString,
        _ => (nint)(delegate* unmanaged<nint, nint>)&PoisonCameraGob,
    };

    [UnmanagedCallersOnly] private static nint PoisonGob(nint self) => Unimplemented(self, SeamTable.Gob);
    [UnmanagedCallersOnly] private static nint PoisonScene(nint self) => Unimplemented(self, SeamTable.Scene);
    [UnmanagedCallersOnly] private static nint PoisonCamera(nint self) => Unimplemented(self, SeamTable.Camera);
    [UnmanagedCallersOnly] private static nint PoisonStringGob(nint self) => Unimplemented(self, SeamTable.StringGob);
    [UnmanagedCallersOnly] private static nint PoisonCAurString(nint self) => Unimplemented(self, SeamTable.CAurString);
    [UnmanagedCallersOnly] private static nint PoisonCameraGob(nint self) => Unimplemented(self, SeamTable.CameraGob);

    private static nint Unimplemented(nint self, SeamTable t)
    {
        _unimplementedCalls++;
        if ((_unimplementedCalls & (_unimplementedCalls - 1)) == 0)
            Log?.Always($"seam: an unimplemented {t} slot was called on 0x{self:x}; returned zero "
                      + $"({_unimplementedCalls} so far -- the boot line for {t} names which of its slots are unimplemented)");
        return 0;
    }

    /// <summary>Win64's shape for a by-value return: the hidden buffer arrives in rdx and its address is also
    /// the return value, so a refusal must zero the buffer and hand it back.</summary>
    [UnmanagedCallersOnly]
    private static nint PoisonSret(nint self, nint sret)
    {
        _unimplementedCalls++;
        if (sret != 0) NativeMemory.Fill((void*)sret, 16, 0);
        if ((_unimplementedCalls & (_unimplementedCalls - 1)) == 0)
            Log?.Always($"seam: an unimplemented by-value slot was called on 0x{self:x}; returned zero ({_unimplementedCalls} so far)");
        return sret;
    }

    /// <summary>SysV's shape for the same: xmm0:xmm1, no buffer.</summary>
    [UnmanagedCallersOnly]
    private static Quad PoisonQuad(nint self)
    {
        _unimplementedCalls++;
        if ((_unimplementedCalls & (_unimplementedCalls - 1)) == 0)
            Log?.Always($"seam: an unimplemented by-value slot was called on 0x{self:x}; returned zero ({_unimplementedCalls} so far)");
        return default;
    }
}
