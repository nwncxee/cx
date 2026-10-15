using System;

namespace CX.Framework;

/// <summary>Rows of <see cref="EngineLayout"/> for nuklear's own structs and the item's embedded object, read by the NUI services.</summary>
public static unsafe partial class EngineLayout
{
    /// <summary>The <c>CNWCObject</c> inside a <c>CNWCItem</c>: what the game-object array registers.</summary>
    public static readonly int ItemAsObject = Field<CX.Engine.CNWCObject>("CNWCItem", "asObject", embedded: true);
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
}
