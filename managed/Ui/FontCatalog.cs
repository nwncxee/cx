namespace CX.Ui;

/// <summary>The fonts a scan found, grouped into families by typographic family name and width, and the face a
/// family offers nearest a weight and a style.</summary>
public sealed class FontCatalog
{
    /// <summary>One font: a file, or one font inside a collection at <see cref="Index"/>. A variable font's weight
    /// axis runs <see cref="VarMin"/> to <see cref="VarMax"/>; both are 0 for a static font.</summary>
    public sealed record Face(string Family, int Weight, bool Italic, int Width, string Path, int Index, int VarMin, int VarMax);

    /// <summary>A catalog of no fonts.</summary>
    public static readonly FontCatalog Empty = new();

    /// <summary>Every family, sorted, each once.</summary>
    public string[] Families => [];

    /// <summary>The fonts <paramref name="fcList"/> names (one path per line, fontconfig's own list; null when there is
    /// no fontconfig), else the fonts under <paramref name="dirs"/>.</summary>
    public static FontCatalog Scan(string? fcList, IEnumerable<string> dirs) => Empty;

    /// <summary>The face <paramref name="family"/> draws <paramref name="weight"/> and <paramref name="italic"/> with,
    /// and the weight it draws at; null for a family the catalog does not hold.</summary>
    public (Face Face, int Weight)? Pick(string family, int weight, bool italic) => null;

    /// <summary>The weight of <paramref name="have"/> a web browser draws <paramref name="want"/> at.</summary>
    public static int Nearest(IReadOnlyList<int> have, int want) => want;
}
