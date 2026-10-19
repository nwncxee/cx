using System;

namespace CX.Framework;

/// <summary>The rows of <see cref="EngineLayout"/> the NUI services and the render passes read out of nuklear's own structs and out of
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
    /// <summary><c>nk_context</c>: the window that has the focus (<c>active</c>).</summary>
    public static readonly int NkCtxActive = Field<IntPtr>("NkContext", "active");
    /// <summary><c>nk_window</c>: non-zero while a text edit in the window has the focus (<c>edit.active</c>).</summary>
    public static readonly int NkWinEditActive = Field<int>("NkWindow", "editActive");
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
    /// <summary><c>nk_panel</c>: the scroll offsets (pointers to the window's two <c>uint</c>s), the layout cursor and the extent reached.</summary>
    public static readonly int NkPanelOffsetX = Field<IntPtr>("NkPanel", "offsetX");
    /// <inheritdoc cref="NkPanelOffsetX"/>
    public static readonly int NkPanelOffsetY = Field<IntPtr>("NkPanel", "offsetY");
    /// <inheritdoc cref="NkPanelOffsetX"/>
    public static readonly int NkPanelAtX = Field<float>("NkPanel", "atX");
    /// <inheritdoc cref="NkPanelOffsetX"/>
    public static readonly int NkPanelAtY = Field<float>("NkPanel", "atY");
    /// <inheritdoc cref="NkPanelOffsetX"/>
    public static readonly int NkPanelMaxX = Field<float>("NkPanel", "maxX");
    /// <summary><c>nk_panel</c>: its bounds rectangle (x, y, w, h).</summary>
    public static readonly int NkPanelBounds = Field<float>("NkPanel", "bounds", count: 4);
    /// <summary><c>nk_panel</c>: the clip rectangle (x, y, w, h).</summary>
    public static readonly int NkPanelClip = Field<float>("NkPanel", "clip", count: 4);
    /// <summary><c>nk_panel</c> row state: the row type, the next item's index, the row height, its column count and the running item offset.</summary>
    public static readonly int NkPanelRowType = Field<int>("NkPanel", "rowType");
    /// <inheritdoc cref="NkPanelRowType"/>
    public static readonly int NkPanelRowIndex = Field<int>("NkPanel", "rowIndex");
    /// <inheritdoc cref="NkPanelRowType"/>
    public static readonly int NkPanelRowHeight = Field<float>("NkPanel", "rowHeight");
    /// <inheritdoc cref="NkPanelRowType"/>
    public static readonly int NkPanelRowColumns = Field<int>("NkPanel", "rowColumns");
    /// <inheritdoc cref="NkPanelRowType"/>
    public static readonly int NkPanelRowItemOffset = Field<float>("NkPanel", "rowItemOffset");
    /// <summary><c>nk_panel</c> free-space row: the rectangle (x, y, w, h) of the item being placed.</summary>
    public static readonly int NkPanelRowItem = Field<float>("NkPanel", "rowItem", count: 4);
    /// <summary><c>nk_context</c> styles: the vertical and horizontal scrollbar style records.</summary>
    public static readonly int NkCtxStyleScrollV = Field<CX.Engine.NkStyleScrollbar>("NkContext", "styleScrollV", embedded: true);
    /// <inheritdoc cref="NkCtxStyleScrollV"/>
    public static readonly int NkCtxStyleScrollH = Field<CX.Engine.NkStyleScrollbar>("NkContext", "styleScrollH", embedded: true);
    /// <summary><c>nk_context</c>: the scrollbar thickness along each axis.</summary>
    public static readonly int NkCtxBarSizeX = Field<float>("NkContext", "barSizeX");
    /// <inheritdoc cref="NkCtxBarSizeX"/>
    public static readonly int NkCtxBarSizeY = Field<float>("NkContext", "barSizeY");
    /// <summary><c>nk_input</c> inside the context: the left button's down and clicked state, where it was clicked, the mouse delta and the wheel.</summary>
    public static readonly int NkCtxLeftDown = Field<int>("NkContext", "leftDown");
    /// <inheritdoc cref="NkCtxLeftDown"/>
    public static readonly int NkCtxLeftClicked = Field<uint>("NkContext", "leftClicked");
    /// <inheritdoc cref="NkCtxLeftDown"/>
    public static readonly int NkCtxClicked = Field<float>("NkContext", "clicked", count: 2);
    /// <inheritdoc cref="NkCtxLeftDown"/>
    public static readonly int NkCtxDelta = Field<float>("NkContext", "delta", count: 2);
    /// <inheritdoc cref="NkCtxLeftDown"/>
    public static readonly int NkCtxWheel = Field<float>("NkContext", "wheel", count: 2);
    /// <summary><c>nk_command_buffer</c>: its <c>nk_buffer</c>, the end offset of its commands and the layout panel of the window it belongs to.</summary>
    public static readonly int NkCmdBufBase = Field<IntPtr>("NkCommandBuffer", "base");
    /// <inheritdoc cref="NkCmdBufBase"/>
    public static readonly int NkCmdBufEnd = Field<ulong>("NkCommandBuffer", "end");
    /// <inheritdoc cref="NkCmdBufBase"/>
    public static readonly int NkCmdBufLayout = Field<IntPtr>("NkCommandBuffer", "layout");
    /// <summary><c>nk_buffer</c>: the memory block and the bytes allocated in it.</summary>
    public static readonly int NkBufMemory = Field<IntPtr>("NkBuffer", "memory");
    /// <inheritdoc cref="NkBufMemory"/>
    public static readonly int NkBufAllocated = Field<ulong>("NkBuffer", "allocated");
    /// <summary><c>nk_command_scissor</c>: the clip, <c>short x, y</c> then <c>ushort w, h</c>.</summary>
    public static readonly int NkCmdScissorX = Field<short>("NkCommandScissor", "x");
    /// <inheritdoc cref="NkCmdScissorX"/>
    public static readonly int NkCmdScissorY = Field<short>("NkCommandScissor", "y");
    /// <inheritdoc cref="NkCmdScissorX"/>
    public static readonly int NkCmdScissorW = Field<ushort>("NkCommandScissor", "w");
    /// <inheritdoc cref="NkCmdScissorX"/>
    public static readonly int NkCmdScissorH = Field<ushort>("NkCommandScissor", "h");
    /// <summary><c>NkCommandLine</c>: its colour.</summary>
    public static readonly int NkCmdLineColor = Field<CX.Engine.NkColor>("NkCommandLine", "color");
    /// <summary><c>NkCommandCurve</c>: its colour.</summary>
    public static readonly int NkCmdCurveColor = Field<CX.Engine.NkColor>("NkCommandCurve", "color");
    /// <summary><c>NkCommandRect</c>: its colour.</summary>
    public static readonly int NkCmdRectColor = Field<CX.Engine.NkColor>("NkCommandRect", "color");
    /// <summary><c>nk_command_rect_filled</c>: the box, <c>short x, y</c> then <c>ushort w, h</c>, and the colour after it.</summary>
    public static readonly int NkCmdRectFilledX = Field<short>("NkCommandRectFilled", "x");
    /// <inheritdoc cref="NkCmdRectFilledX"/>
    public static readonly int NkCmdRectFilledY = Field<short>("NkCommandRectFilled", "y");
    /// <inheritdoc cref="NkCmdRectFilledX"/>
    public static readonly int NkCmdRectFilledW = Field<ushort>("NkCommandRectFilled", "w");
    /// <inheritdoc cref="NkCmdRectFilledX"/>
    public static readonly int NkCmdRectFilledH = Field<ushort>("NkCommandRectFilled", "h");
    /// <inheritdoc cref="NkCmdRectFilledX"/>
    public static readonly int NkCmdRectFilledColor = Field<CX.Engine.NkColor>("NkCommandRectFilled", "color");
    /// <summary><c>NkCommandRectMultiColor</c>: its colours.</summary>
    public static readonly int NkCmdRectMultiLeft = Field<CX.Engine.NkColor>("NkCommandRectMultiColor", "left");
    /// <summary><c>NkCommandCircle</c>: its colour.</summary>
    public static readonly int NkCmdCircleColor = Field<CX.Engine.NkColor>("NkCommandCircle", "color");
    /// <summary><c>NkCommandCircleFilled</c>: its colour.</summary>
    public static readonly int NkCmdCircleFilledColor = Field<CX.Engine.NkColor>("NkCommandCircleFilled", "color");
    /// <summary><c>NkCommandArc</c>: its colour.</summary>
    public static readonly int NkCmdArcColor = Field<CX.Engine.NkColor>("NkCommandArc", "color");
    /// <summary><c>NkCommandArcFilled</c>: its colour.</summary>
    public static readonly int NkCmdArcFilledColor = Field<CX.Engine.NkColor>("NkCommandArcFilled", "color");
    /// <summary><c>NkCommandTriangle</c>: its colour.</summary>
    public static readonly int NkCmdTriangleColor = Field<CX.Engine.NkColor>("NkCommandTriangle", "color");
    /// <summary><c>NkCommandTriangleFilled</c>: its colour.</summary>
    public static readonly int NkCmdTriangleFilledColor = Field<CX.Engine.NkColor>("NkCommandTriangleFilled", "color");
    /// <summary><c>NkCommandPolygon</c>: its colour.</summary>
    public static readonly int NkCmdPolygonColor = Field<CX.Engine.NkColor>("NkCommandPolygon", "color");
    /// <summary><c>NkCommandPolygonFilled</c>: its colour.</summary>
    public static readonly int NkCmdPolygonFilledColor = Field<CX.Engine.NkColor>("NkCommandPolygonFilled", "color");
    /// <summary><c>NkCommandPolyline</c>: its colour.</summary>
    public static readonly int NkCmdPolylineColor = Field<CX.Engine.NkColor>("NkCommandPolyline", "color");
    /// <summary><c>NkCommandText</c>: its colours.</summary>
    public static readonly int NkCmdTextBackground = Field<CX.Engine.NkColor>("NkCommandText", "background");
    /// <summary><c>NkCommandImage</c>: its colour.</summary>
    public static readonly int NkCmdImageColor = Field<CX.Engine.NkColor>("NkCommandImage", "color");
}
