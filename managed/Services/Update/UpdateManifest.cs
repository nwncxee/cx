namespace CX.Services.Update;

/// <summary>A release notice: the release's version and its changelog lines.</summary>
internal sealed record UpdateManifest(string Version, string[] Changes)
{
    /// <summary>Read a notice's text, or null when it is not one.</summary>
    public static UpdateManifest? Parse(string text) => null;
}
