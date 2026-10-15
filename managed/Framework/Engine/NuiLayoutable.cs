using System;
using CX.Framework;

namespace CX.Engine;

/// <inheritdoc cref="Nui__Layout__Layoutable"/>
public readonly unsafe partial struct Nui__Layout__Layoutable
{
    private const int SharedPtrSize = 16;

    /// <summary>How many child slots lie between the children pointers; each is a <c>shared_ptr</c>, 16 bytes on both compilers.</summary>
    public int ChildCount
    {
        get
        {
            long bytes = (long)Children - (long)ChildrenEnd;
            return bytes >= 0 ? 0 : (int)((-bytes + SharedPtrSize - 1) / SharedPtrSize);
        }
    }

    /// <summary>The <c>Render</c> entry of a vtable (the pointer past its symbol header, as <see cref="EngineAbi.Vtbl"/> gives it).</summary>
    public static IntPtr RenderEntry(IntPtr vtable) => *(IntPtr*)(vtable + EngineLayout.NuiRenderSlot);

    /// <summary>The element held by child slot <paramref name="i"/>.</summary>
    public IntPtr Child(int i) => *(IntPtr*)(Children + i * SharedPtrSize);
}
