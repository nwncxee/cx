using CX.Graphics;

namespace CX.Services.Nui;

internal static unsafe partial class NuiModelView
{
    private enum TexOp : byte { Make, Delete, Standin }

    /// <summary>One piece of GL work on a view's texture: make <see cref="View"/>'s, delete <see cref="Tex"/>, or write
    /// <see cref="Px"/> (<see cref="W"/> x <see cref="H"/> RGBA) into it.</summary>
    private readonly record struct TexJob(TexOp Op, ViewData? View, uint Tex, uint[]? Px, int W, int H);

    /// <summary>Run <paramref name="j"/> where the views draw: at once in the engine's context with Alloy off, where the
    /// engine-drawn doll renders; otherwise queued for <see cref="Pump"/> inside Alloy's GUI bracket. Cold.</summary>
    private static void Queue(ViewState s, TexJob j)
    {
        if (!CX.Alloy.Renderer.On) { Run(s, j); return; }
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

    private static void Run(ViewState s, TexJob j)
    {
        if (CX.Alloy.Renderer.OnVulkan) { RunVk(s, j); return; }
        if (s.Log == null || !Gl.Init(s.Log)) return;
        int prev;
        Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &prev);
        switch (j.Op)
        {
            case TexOp.Make:
                if (j.View is not { Gone: false } v) break;
                uint t;
                Gl.GenTextures(1, &t);
                Gl.BindTexture(Gl.TEXTURE_2D, t);
                Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.RGBA8, j.W, j.H, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, null);
                Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
                Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
                Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
                Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
                v.OwnTex = t;
                break;
            case TexOp.Delete:
                uint d = j.Tex;
                Gl.DeleteTextures(1, &d);
                break;
            case TexOp.Standin:
                Standin(j);
                break;
        }
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)prev);
    }

    /// <summary><see cref="Run"/> under Vulkan: a view's texture is a name the Vulkan GUI keys its own image by, so making
    /// one mints a name, a delete has nothing to free, and a stand-in is uploaded into that image.</summary>
    private static void RunVk(ViewState s, TexJob j)
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

    /// <summary>A stand-in's pixels into texture <c>j.Tex</c>, the unpack state put back as it was.</summary>
    private static void Standin(TexJob j)
    {
        if (j.Px == null || Gl.TexSubImage2D == null) return;
        int pbo, align, rowLen;
        Gl.GetIntegerv(Gl.PIXEL_UNPACK_BUFFER_BINDING, &pbo);
        Gl.GetIntegerv(Gl.UNPACK_ALIGNMENT, &align);
        Gl.GetIntegerv(Gl.UNPACK_ROW_LENGTH, &rowLen);
        Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, 0);
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 4);
        Gl.PixelStorei(Gl.UNPACK_ROW_LENGTH, 0);
        Gl.BindTexture(Gl.TEXTURE_2D, j.Tex);
        fixed (uint* p = j.Px) Gl.TexSubImage2D(Gl.TEXTURE_2D, 0, 0, 0, j.W, j.H, Gl.RGBA, Gl.UNSIGNED_BYTE, p);
        Gl.PixelStorei(Gl.UNPACK_ROW_LENGTH, rowLen);
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, align);
        Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, (uint)pbo);
    }
}
