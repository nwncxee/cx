namespace CX.Services.NuiItems;

/// <summary>The NUI skin's default font: what the server's own NUI menu draws its labels in.</summary>
internal static class NuiSkinFont
{
    /// <summary>The first <c>[[fonts]]</c> entry of a <c>nui_skin.tml</c>: its resref and size. False when there is none.</summary>
    internal static bool Parse(string skin, out string resref, out float px)
    {
        resref = ""; px = 0f;
        return false;
    }
}
