namespace CX.Alloy.Db;

/// <summary>What one period's texture budget is worked out from: <c>alloy.texcache</c> in MiB (-1 auto), the
/// device-local heap's budget as the device reports it (0 when it answers no memory query), and the bytes Alloy
/// holds that are not textures.</summary>
internal readonly record struct TexBudgetInputs(int TexCacheMib, long HeapBudgetBytes, long OtherBytes);

/// <summary>The one texture budget the world and owned texture caches both read, worked out at each period's
/// start from that period's inputs.</summary>
internal static class TexBudget
{
    /// <summary>The current period: the inputs its budget was worked out from.</summary>
    private sealed class TexBudgetState
    {
        public TexBudgetInputs Inputs;
    }

    private static readonly TexBudgetState _state = new();

    /// <summary>The inputs the current period's budget was worked out from.</summary>
    internal static TexBudgetInputs Inputs => _state.Inputs;

    /// <summary>A period's inputs from the device's figures: <paramref name="vmaBudgetBytes"/> is the allocator's
    /// budget for the device-local heaps (the driver's own figure with <c>VK_EXT_memory_budget</c>, the allocator's
    /// estimate without) and <paramref name="heapSizeBytes"/> their size.</summary>
    internal static TexBudgetInputs InputsFrom(int texCacheMib, bool memoryBudget, long vmaBudgetBytes, long heapSizeBytes, long otherBytes)
        => new(texCacheMib, heapSizeBytes, otherBytes);

    /// <summary>Start a period: its budget is worked out from <paramref name="inputs"/>, and its count of
    /// evicted models starts at zero.</summary>
    internal static void BeginPeriod(in TexBudgetInputs inputs) => _state.Inputs = inputs;
}
