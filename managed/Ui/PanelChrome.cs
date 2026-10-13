namespace CX.Ui;

/// <summary>The current <see cref="Theme"/> colours by role, and the fill / outline primitives every flat window draws
/// with. Brought into scope with <c>using static</c>.</summary>
internal static class PanelChrome
{
    internal static uint PanelBg => Theme.Rgb(Role.PanelBg);
    internal static uint Border => Theme.Rgb(Role.Border);
    internal static uint HeaderBg => Theme.Rgb(Role.HeaderBg);
    internal static uint CatBg => Theme.Rgb(Role.CatBg);
    internal static uint SubBg => Theme.Rgb(Role.SubBg);
    internal static uint ColBorder => Theme.Rgb(Role.ColBorder);
    internal static uint NavOn => Theme.Rgb(Role.NavOn);
    internal static uint Accent => Theme.Rgb(Role.Accent);
    internal static uint Accent2 => Theme.Rgb(Role.Accent2);
    internal static uint Input => Theme.Rgb(Role.Input);
    internal static uint Selected => Theme.Rgb(Role.Selected);
    internal static uint Tx => Theme.Rgb(Role.Tx);
    internal static uint Tx2 => Theme.Rgb(Role.Tx2);
    internal static uint Tx3 => Theme.Rgb(Role.Tx3);
    internal static uint Grip => Theme.Rgb(Role.Grip);
    internal static uint Hi => Theme.Rgb(Role.Hi);
    internal static uint ToggleOff => Theme.Rgb(Role.ToggleOff);
    internal static uint Frame => Theme.Rgb(Role.Frame);
    internal static uint Frame2 => Theme.Rgb(Role.Frame2);
    internal static uint Track => Theme.Rgb(Role.Track);
    internal static uint ListBg => Theme.Rgb(Role.ListBg);
    internal static uint MasterBg => Theme.Rgb(Role.MasterBg);
    internal static uint StripBg => Theme.Rgb(Role.StripBg);
    internal static uint StripBorder => Theme.Rgb(Role.StripBorder);
    internal static uint Warn => Theme.Rgb(Role.Warn);
    internal static uint Off => Theme.Rgb(Role.Off);
    internal static uint BadgeFrame => Theme.Rgb(Role.BadgeFrame);
    internal static uint Ok => Theme.Rgb(Role.Ok);

    internal static (float r, float g, float b, float a) C(uint rgb, float a = 1f)
        => (((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

    internal static void Fill(VertexBatch b, float x0, float y0, float x1, float y1, uint rgb, float a = 1f)
    {
        var c = C(rgb, a);
        b.Rect(x0, y0, x1, y1, c.r, c.g, c.b, c.a);
    }

    /// <summary>A 1 px frame just inside the rect.</summary>
    internal static void Outline(VertexBatch b, float x0, float y0, float x1, float y1, uint rgb, float a = 1f)
    {
        Fill(b, x0, y0, x1, y0 + 1f, rgb, a); Fill(b, x0, y1 - 1f, x1, y1, rgb, a);
        Fill(b, x0, y0, x0 + 1f, y1, rgb, a); Fill(b, x1 - 1f, y0, x1, y1, rgb, a);
    }
}
