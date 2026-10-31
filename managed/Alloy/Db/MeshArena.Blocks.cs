using System;
using CX.Graphics;
using Vortice.Vulkan;

namespace CX.Alloy.Db;

/// <summary>Where a mesh LANDS: one VMA virtual block per vertex format and one over the shared element
/// arena, each a TLSF free list over element counts and nothing more -- no device, no memory, pure CPU
/// bookkeeping. A freed range's place goes back to its block and the next mesh that fits takes it, so a
/// range placed once never moves and no consumer is ever told its geometry did.</summary>
public static unsafe partial class MeshArena
{
    /// <summary>Best fit: a mesh lands in the SMALLEST free gap that holds it, so a freed range's place goes
    /// to a mesh of its size instead of the untouched tail being spent on it.</summary>
    private const VmaVirtualAllocationCreateFlags Fit = VmaVirtualAllocationCreateFlags.StrategyMinMemory;

    /// <summary>A TLSF block over <paramref name="elements"/> places (vertices, or indices for the element
    /// arena). Null when VMA refuses one or the staged library is not there, which is named loudly: the
    /// arena then holds no geometry at all and every acquire is a counted refusal.</summary>
    private static VmaVirtualBlock NewBlock(long elements)
    {
        // The block is native, and the arena's first one is made from a static initializer -- before any
        // backend has loaded the staged library. The load is idempotent and by absolute path.
        VulkanToolchain.Load(VulkanToolchain.VmaLibrary);
        var ci = new VmaVirtualBlockCreateInfo { size = (ulong)elements };
        try
        {
            if (Vma.vmaCreateVirtualBlock(&ci, out VmaVirtualBlock b) == VkResult.Success) return b;
        }
        // a missing, stale or foreign library: the type initializer this runs in must not throw at boot
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { }
        return VmaVirtualBlock.Null;
    }

    /// <summary>Place <paramref name="n"/> elements in <paramref name="block"/>, best fit, alignment 1 (the
    /// unit IS a vertex or an index). False = they do not fit, which is the caller's counted refusal.</summary>
    private static bool Place(VmaVirtualBlock block, int n, out VmaVirtualAllocation at, out int first)
    {
        at = VmaVirtualAllocation.Null; first = 0;
        if (block.IsNull || n <= 0) return false;
        var ci = new VmaVirtualAllocationCreateInfo { size = (ulong)n, alignment = 1, flags = Fit };
        VmaVirtualAllocation a;
        ulong off;
        if (Vma.vmaVirtualAllocate(block, &ci, &a, &off) != VkResult.Success) return false;
        at = a; first = (int)off;
        return true;
    }

    /// <summary>Give one placement back: its elements are the next fitting mesh's.</summary>
    private static void Unplace(VmaVirtualBlock block, ref VmaVirtualAllocation at)
    {
        if (!block.IsNull && !at.IsNull) Vma.vmaVirtualFree(block, at);
        at = VmaVirtualAllocation.Null;
    }

    /// <summary>Elements the live placements in <paramref name="block"/> occupy -- VMA's own sum over them,
    /// not a counter kept beside it.</summary>
    private static long Occupied(VmaVirtualBlock block)
    {
        if (block.IsNull) return 0;
        Vma.vmaGetVirtualBlockStatistics(block, out VmaStatistics st);
        return (long)st.allocationBytes;
    }

    /// <summary>Hand every block back to VMA, placements and all: the arena's tear-down edge
    /// (<see cref="ResetForCheck"/>), which makes a fresh one for each store straight after.</summary>
    private static void DestroyBlocks()
    {
        foreach (Section s in _sect)
        {
            if (!s.Block.IsNull) Vma.vmaDestroyVirtualBlock(s.Block);
            s.Block = VmaVirtualBlock.Null;
        }
        if (!_idx.Block.IsNull) Vma.vmaDestroyVirtualBlock(_idx.Block);
        _idx.Block = VmaVirtualBlock.Null;
    }

    /// <summary>The element spans of one store the next pump must send, half-open. A new span is merged into
    /// the FIRST it touches, so two may still overlap and send the same bytes twice, and past <see cref="Cap"/>
    /// of them one covers the rest: more bytes than needed, never fewer. Allocates nothing.</summary>
    private sealed class Spans
    {
        private const int Cap = 32;
        private readonly int[] _from = new int[Cap], _to = new int[Cap];
        private int _n;

        internal int Count => _n;

        /// <summary>Span <paramref name="i"/>, as the half-open element range <c>[From, To)</c>.</summary>
        internal (int From, int To) At(int i) => (_from[i], _to[i]);

        internal void Add(int from, int to)
        {
            if (to <= from) return;
            for (int i = 0; i < _n; i++)
                if (from <= _to[i] && to >= _from[i])
                {
                    _from[i] = Math.Min(_from[i], from);
                    _to[i] = Math.Max(_to[i], to);
                    return;
                }
            if (_n < Cap) { _from[_n] = from; _to[_n] = to; _n++; return; }
            int lo = from, hi = to;
            for (int i = 0; i < _n; i++) { lo = Math.Min(lo, _from[i]); hi = Math.Max(hi, _to[i]); }
            _from[0] = lo; _to[0] = hi; _n = 1;
        }

        /// <summary>Everything up to <paramref name="high"/> is dirty: a new device buffer, or a lost context.</summary>
        internal void All(int high)
        {
            _n = 0;
            Add(0, high);
        }

        internal void Clear() => _n = 0;
    }
}
