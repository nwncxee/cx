namespace CX.Alloy.Db;

/// <summary>What one period's texture budget is worked out from: <c>alloy.texcache</c> in MiB (-1 auto), the
/// device-local heap's budget as the device reports it (0 when it answers no memory query), and the bytes Alloy
/// holds that are not textures.</summary>
internal readonly record struct TexBudgetInputs(int TexCacheMib, long HeapBudgetBytes, long OtherBytes);

/// <summary>The one texture budget the world and owned texture caches both read, worked out at each period's
/// start from that period's inputs.</summary>
internal static class TexBudget
{
    /// <summary>The budget when the device answers no memory query and <c>alloy.texcache</c> is auto.</summary>
    internal const long FallbackBytes = 1024L << 20;

    /// <summary>The share of the heap's budget, in tenths, textures and everything else may fill together.</summary>
    private const long HeapTenths = 9;

    /// <summary>The current period: its inputs, the budget they give, the models evicted for it and how far
    /// what must stay resident runs past it.</summary>
    private sealed class TexBudgetState
    {
        public TexBudgetInputs Inputs = new(-1, 0, 0);
        public long Bytes = FallbackBytes;
        public int ModelsEvicted;
        public long OverBytes;
    }

    private static readonly TexBudgetState _state = new();

    /// <summary>The inputs the current period's budget was worked out from.</summary>
    internal static TexBudgetInputs Inputs => _state.Inputs;

    /// <summary>The current period's budget in bytes, for the world and owned texture caches together.</summary>
    internal static long Bytes => _state.Bytes;

    /// <summary>The models evicted to keep inside the current period's budget.</summary>
    internal static int ModelsEvicted => _state.ModelsEvicted;

    /// <summary>The bytes the textures in use ran past the budget at the last eviction, 0 when they fit.</summary>
    internal static long OverBytes => _state.OverBytes;

    /// <summary>A period's inputs from the device's figures: <paramref name="vmaBudgetBytes"/> is the allocator's
    /// budget for the device-local heaps (the driver's own figure with <c>VK_EXT_memory_budget</c>, the allocator's
    /// estimate without, so <paramref name="memoryBudget"/> changes nothing here) and <paramref name="heapSizeBytes"/>
    /// their size, which stands in only when the allocator gave no budget.</summary>
    internal static TexBudgetInputs InputsFrom(int texCacheMib, bool memoryBudget, long vmaBudgetBytes, long heapSizeBytes, long otherBytes)
        => new(texCacheMib, vmaBudgetBytes > 0 ? vmaBudgetBytes : heapSizeBytes, otherBytes);

    /// <summary>The budget <paramref name="inputs"/> give: <c>alloy.texcache</c> when set, else nine tenths of the
    /// heap's budget less what is not textures (never below 0), else <see cref="FallbackBytes"/>.</summary>
    internal static long BytesFor(in TexBudgetInputs inputs)
    {
        if (inputs.TexCacheMib > 0) return (long)inputs.TexCacheMib << 20;
        if (inputs.HeapBudgetBytes <= 0) return FallbackBytes;
        return System.Math.Max(0, inputs.HeapBudgetBytes / 10 * HeapTenths - inputs.OtherBytes);
    }

    /// <summary>Start a period: its budget is worked out from <paramref name="inputs"/>, and its count of
    /// evicted models starts at zero.</summary>
    internal static void BeginPeriod(in TexBudgetInputs inputs)
    {
        _state.Inputs = inputs;
        _state.Bytes = BytesFor(inputs);
        _state.ModelsEvicted = 0;
        _state.OverBytes = 0;
    }

    /// <summary>Count one model evicted for the current period's budget.</summary>
    internal static void CountEvicted() => _state.ModelsEvicted++;

    /// <summary>Record how far the textures in use ran past the budget at an eviction.</summary>
    internal static void NoteOver(long bytes) => _state.OverBytes = System.Math.Max(0, bytes);
}
