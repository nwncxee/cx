using System;

namespace CX.Framework;

/// <summary>The rows of <see cref="EngineLayout"/> the NUI hit test and the render passes read out of nuklear's own structs and out of
/// the standard library's shared-pointer block.</summary>
public static unsafe partial class EngineLayout
{
    /// <summary><c>nk_context</c>: the pointer position (<c>input.mouse.pos</c>, x then y, the space widget bounds use) and the
    /// window being built (<c>current</c>).</summary>
    public static readonly int NkCtxMousePos = Field<float>("NkContext", "mousePos", count: 2);
    /// <inheritdoc cref="NkCtxMousePos"/>
    public static readonly int NkCtxCurrent = Field<IntPtr>("NkContext", "current");
    /// <summary><c>nk_window</c>: the panel its widgets are laid out in.</summary>
    public static readonly int NkWinLayout = Field<IntPtr>("NkWindow", "layout");
    /// <summary><c>nk_command_image</c>: the texture handle the image was drawn with.</summary>
    public static readonly int NkCmdImageHandle = Field<int>("NkCommandImage", "handle");
    /// <summary>The block a <c>shared_ptr</c> points at: the count of owners, at +8 in libstdc++ and in MSVC's.</summary>
    public static readonly int SharedBlockUseCount = Field<int>("SharedPtrBlock", "useCount");
}
