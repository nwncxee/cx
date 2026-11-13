using CX.Framework;

namespace CX.Services.NuiItems;

/// <summary>
/// The item surfaces of a server's NUI windows: the menu that replaces the server's own Item menu
/// (<see cref="ItemMenuService"/>) and the drag between its elements (<see cref="ItemDragService"/>). Both are policy
/// the server declares and events it hears back; neither invents an action of its own.
/// </summary>
public sealed class NuiItemsService : IExtenderService
{
    public void Initialize(ExtenderLog log, HookService hooks)
    {
        ItemMenuService.Init(log);
        ItemDragService.Init(log);
        ItemMenuSelfCheck.Run(log);
        ItemDragSelfCheck.Run(log);
        NuiItemStockRegressions.Run(log);
        NuiItemMenuApprovedExamples.Run(log);
        NuiItemDragApprovedExamples.Run(log);
        NuiItemDragRegressions.Run(log);
        NuiItemDragPickRegressions.Run(log);
        NuiItemKnobRegressions.Run(log);
        NuiItemTargetHeldApprovedExamples.Run(log);
    }
}
