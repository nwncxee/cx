using CX.Framework;

namespace CX.Ui;

public static unsafe partial class TextRig
{
    /// <summary>One textured, tinted batch over texture <paramref name="tid"/>: the skin pass's one draw per sheet.
    /// UVs follow <see cref="DrawIconTid"/>'s convention (engine textures are bottom-up: v = 1 is the image's top row).</summary>
    public static void DrawSkin(int tid, System.ReadOnlySpan<float> verts, int count, float alpha = 1f,
                                bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0)
    {
        if (tid == 0 || count == 0 || _sheet == null || _log == null || alpha <= 0f || ViewportW <= 0) return;
        Add(new UiCmd { Kind = UiDrawKind.Skin, Tex = (uint)tid, Sampler = true, Alpha = alpha < 1f ? alpha : 1f,
                        Clip = clip, Cx = cx, Cy = cy, Cw = cw, Ch = ch }, verts, count);
    }

    /// <summary>The texture a UI draw of resref <paramref name="resref"/> samples (<see cref="ResolveTid"/>) and its
    /// level-0 size, read off the engine's texture object -- the size every upload of it is made at. 0 when unresolved.</summary>
    public static int ResolveSized(string resref, out int w, out int h)
    {
        System.IntPtr tex = AurTex.FindByName(resref);
        AurTex.Size(tex, out w, out h);
        return Own(AurTex.Tid(tex));
    }
}
