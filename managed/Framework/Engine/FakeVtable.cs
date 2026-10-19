namespace CX.Engine;

/// <summary>A function-pointer table a self-check plants in a fake engine object: the write side of the generated
/// <c>...Fn</c> vtable members, set at the byte offset an <see cref="CX.Framework.EngineLayout"/> vtable row names.</summary>
public readonly unsafe struct FakeVtable(IntPtr table)
{
    /// <summary>Puts <paramref name="fn"/> in the entry at <paramref name="slotBytes"/> past the table's start.</summary>
    public void Set(int slotBytes, IntPtr fn) => *(IntPtr*)(table + slotBytes) = fn;
}
