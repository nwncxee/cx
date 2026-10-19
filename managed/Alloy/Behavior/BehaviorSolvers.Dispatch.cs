using CX.Framework;

namespace CX.Alloy;

internal static unsafe partial class BehaviorSolvers
{
    /// <summary>The Linux symbol per class, index-aligned with <see cref="Db.DbBehaviorKind"/>.</summary>
    internal static readonly string[] Symbols =
    {
        SymbolMap.BehaviorControl, SymbolMap.AttachControl, SymbolMap.AttachTranslateControl,
        SymbolMap.AttachRotateControl, SymbolMap.AttachNoOrientationControl, SymbolMap.AttachGuiScaleControl,
        SymbolMap.CrossSceneAttachControl, SymbolMap.CrossSceneAttachOffsetControl, SymbolMap.SubwindowControl,
        SymbolMap.NavigateControl, SymbolMap.CameraFollowControl, SymbolMap.CameraLooseFollowControl,
        SymbolMap.CameraOnAStickControl,
    };
}
