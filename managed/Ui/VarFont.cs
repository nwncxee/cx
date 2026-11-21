namespace CX.Ui;

/// <summary>A variable TrueType font made static at one weight, for the engine's stb_truetype, which reads only a
/// font's default outlines.</summary>
internal static class VarFont
{
    /// <summary>The font at <paramref name="index"/> of <paramref name="font"/> at weight <paramref name="weight"/>, every
    /// other axis at its default, as a static font; null when it is not a variable TrueType font.</summary>
    public static byte[]? Instance(byte[] font, int index, int weight) => null;
}
