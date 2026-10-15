using System;
using CX.Framework;

namespace CX.Engine;

/// <summary>A <c>std::shared_ptr</c>: the object and its control block, 16 bytes on both compilers.</summary>
public struct StdSharedPtr
{
    /// <summary>The object it holds.</summary>
    public IntPtr Ptr;
    /// <summary>The control block that counts its owners.</summary>
    public IntPtr Ctrl;
}

/// <inheritdoc cref="Nui__Layout__Layoutable"/>
public readonly unsafe partial struct Nui__Layout__Layoutable
{
    /// <summary>The child slots between the children pointers, each a <c>shared_ptr</c>.</summary>
    public Span<StdSharedPtr> ChildSlots
    {
        get
        {
            long bytes = (long)ChildrenEnd - (long)Children;
            return bytes <= 0 ? default : new Span<StdSharedPtr>((void*)Children, (int)((bytes + sizeof(StdSharedPtr) - 1) / sizeof(StdSharedPtr)));
        }
    }

    /// <summary>How many child slots lie between the children pointers.</summary>
    public int ChildCount => ChildSlots.Length;

    /// <summary>The <c>Render</c> entry of a vtable (the pointer past its symbol header, as <see cref="EngineAbi.Vtbl"/> gives it).</summary>
    public static IntPtr RenderEntry(IntPtr vtable) => *(IntPtr*)(vtable + EngineLayout.NuiRenderSlot);

    /// <summary>The element held by child slot <paramref name="i"/>.</summary>
    public IntPtr Child(int i) => ChildSlots[i].Ptr;
}
