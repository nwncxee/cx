namespace CX.Services.Nui;

internal static unsafe partial class NuiModelView
{
    private enum TexOp : byte { Make, Delete, Standin }

    /// <summary>One piece of texture work on a view: mint <see cref="View"/>'s name, drop <see cref="Tex"/>, or write
    /// <see cref="Px"/> (<see cref="W"/> x <see cref="H"/> RGBA) into it.</summary>
    private readonly record struct TexJob(TexOp Op, ViewData? View, uint Tex, uint[]? Px, int W, int H);

    /// <summary>Queue <paramref name="j"/> for <see cref="Pump"/> inside Alloy's GUI bracket, where the views draw. Cold.</summary>
    private static void Queue(ViewState s, TexJob j)
    {
        lock (s.Jobs) { s.Jobs.Add(j); s.JobN = s.Jobs.Count; }
    }

    /// <summary>The queued texture work, from inside Alloy's GUI bracket, ahead of the layers that draw into the
    /// textures. One volatile read when nothing is queued.</summary>
    internal static void Pump()
    {
        ViewState s = _s;
        if (s.JobN == 0) return;
        lock (s.Jobs)
        {
            for (int i = 0; i < s.Jobs.Count; i++) Run(s, s.Jobs[i]);
            s.Jobs.Clear();
            s.JobN = 0;
        }
    }

    /// <summary>A view's texture is a name the Vulkan GUI keys its own image by, so making one mints a name, a delete has
    /// nothing to free, and a stand-in is uploaded into that image.</summary>
    private static void Run(ViewState s, TexJob j)
    {
        switch (j.Op)
        {
            case TexOp.Make:
                if (j.View is { Gone: false } v) v.OwnTex = CX.Alloy.VkTextures.NewName();
                break;
            case TexOp.Standin:
                if (j.Px != null && s.Log != null) CX.Alloy.GuiPassVk.ViewStandin(j.Tex, j.Px, j.W, j.H, s.Log);
                break;
        }
    }
}
