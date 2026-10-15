using System;

namespace CX.Framework;

/// <summary>Rows of <see cref="EngineLayout"/> for the nuklear draw commands the NUI passes scale and clip: each command's colours and, for the scissor and the filled rectangle, its box.</summary>
public static unsafe partial class EngineLayout
{
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
    /// <inheritdoc cref="NkCmdRectMultiLeft"/>
    public static readonly int NkCmdRectMultiTop = Field<CX.Engine.NkColor>("NkCommandRectMultiColor", "top");
    /// <inheritdoc cref="NkCmdRectMultiLeft"/>
    public static readonly int NkCmdRectMultiBottom = Field<CX.Engine.NkColor>("NkCommandRectMultiColor", "bottom");
    /// <inheritdoc cref="NkCmdRectMultiLeft"/>
    public static readonly int NkCmdRectMultiRight = Field<CX.Engine.NkColor>("NkCommandRectMultiColor", "right");
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
    /// <inheritdoc cref="NkCmdTextBackground"/>
    public static readonly int NkCmdTextForeground = Field<CX.Engine.NkColor>("NkCommandText", "foreground");
    /// <summary><c>NkCommandImage</c>: its colour.</summary>
    public static readonly int NkCmdImageColor = Field<CX.Engine.NkColor>("NkCommandImage", "color");
}
