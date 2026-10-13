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
    /// <summary><c>nk_command</c>, the header of every queued draw command: its <c>nk_command_type</c> and the buffer offset of the next.</summary>
    public static readonly int NkCmdType = Field<int>("NkCommand", "type");
    /// <inheritdoc cref="NkCmdType"/>
    public static readonly int NkCmdNext = Field<ulong>("NkCommand", "next");
    /// <summary><c>nk_command_image</c>: where the image was drawn, <c>short x, y</c> then <c>ushort w, h</c>, in layout space.</summary>
    public static readonly int NkCmdImageX = Field<short>("NkCommandImage", "x");
    /// <inheritdoc cref="NkCmdImageX"/>
    public static readonly int NkCmdImageY = Field<short>("NkCommandImage", "y");
    /// <inheritdoc cref="NkCmdImageX"/>
    public static readonly int NkCmdImageW = Field<ushort>("NkCommandImage", "w");
    /// <inheritdoc cref="NkCmdImageX"/>
    public static readonly int NkCmdImageH = Field<ushort>("NkCommandImage", "h");
    /// <summary><c>nk_command_image</c>: the texture handle the image was drawn with.</summary>
    public static readonly int NkCmdImageHandle = Field<int>("NkCommandImage", "handle");
    /// <summary>The block a <c>shared_ptr</c> points at: the count of owners, at +8 in libstdc++ and in MSVC's.</summary>
    public static readonly int SharedBlockUseCount = Field<int>("SharedPtrBlock", "useCount");
}
