using System.Buffers.Binary;
using System.Text;

namespace CX.Ui;

/// <summary>The fonts a scan found, grouped into families by typographic family name and width, and the face a
/// family offers nearest a weight and a style.</summary>
public sealed class FontCatalog
{
    /// <summary>One font: a file, or one font inside a collection at <see cref="Index"/>. <see cref="Weight"/> is its
    /// OS/2 weight, a variable font's default; its weight axis runs <see cref="VarMin"/> to <see cref="VarMax"/>, both 0
    /// for a static font.</summary>
    public sealed record Face(string Family, int Weight, bool Italic, int Width, string Path, int Index, int VarMin, int VarMax)
    {
        public bool Variable => VarMax > VarMin;
    }

    /// <summary>A catalog of no fonts.</summary>
    public static readonly FontCatalog Empty = new(new List<Face>(), new Dictionary<string, Face>());

    /// <summary>The OS/2 width class of normal letters; any other width is a family of its own.</summary>
    private const int NormalWidth = 5;
    /// <summary>Words a family's own name uses for a width, kept in a family name that is not normal width.</summary>
    private static readonly string[] WidthWords = { "Condensed", "SemiCondensed", "Narrow", "Compressed", "Expanded", "Extended", "Wide" };
    /// <summary>The OS/2 width classes 1 to 9, named for a family whose name says no width of its own.</summary>
    private static readonly string[] WidthNames = { "", "UltraCondensed", "ExtraCondensed", "Condensed", "SemiCondensed", "", "SemiExpanded", "Expanded", "ExtraExpanded", "UltraExpanded" };
    private static readonly string[] Extensions = { ".ttf", ".otf", ".ttc", ".otc" };

    private readonly Dictionary<string, List<Face>> _families = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Face> _byFullName;

    private FontCatalog(List<Face> faces, Dictionary<string, Face> byFullName)
    {
        foreach (Face f in faces)
        {
            if (!_families.TryGetValue(f.Family, out List<Face>? l)) _families[f.Family] = l = new List<Face>();
            if (!l.Exists(o => o.Weight == f.Weight && o.Italic == f.Italic && o.Variable == f.Variable)) l.Add(f);
        }
        _byFullName = byFullName;
        Families = _families.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Every family, sorted, each once.</summary>
    public string[] Families { get; }

    /// <summary>True when <paramref name="family"/> is a family of this catalog.</summary>
    public bool Holds(string family) => _families.ContainsKey(family);

    /// <summary>The fonts <paramref name="fcList"/> names (one path per line, fontconfig's own list; null when there is
    /// no fontconfig), else the fonts under <paramref name="dirs"/>. Cold: it opens every font file it finds.</summary>
    public static FontCatalog Scan(string? fcList, IEnumerable<string> dirs)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        if (fcList != null)
            foreach (string line in fcList.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                paths.Add(line);
        else
        {
            var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 };
            foreach (string dir in dirs)
            {
                if (dir.Length == 0 || !Directory.Exists(dir)) continue;
                try { foreach (string f in Directory.EnumerateFiles(dir, "*", opt)) paths.Add(f); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // an unreadable folder offers nothing
            }
        }
        var faces = new List<Face>();
        var byFull = new Dictionary<string, Face>(StringComparer.OrdinalIgnoreCase);
        foreach (string p in paths)
        {
            if (!Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)) continue;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(p); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            int count = Sfnt.Count(bytes);
            for (int i = 0; i < count; i++)
                if (Sfnt.Read(bytes, i) is Sfnt.Info info && info.Drawable)
                {
                    var face = new Face(FamilyOf(info), info.Weight, info.Italic, info.Width, p, i, info.VarMin, info.VarMax);
                    faces.Add(face);
                    if (info.FullName != null) byFull.TryAdd(info.FullName, face);
                }
        }
        return new FontCatalog(faces, byFull);
    }

    /// <summary>The family a font belongs to: its typographic family name (id 16, else 1), plus its width when the
    /// letters are not normal width -- in its own words when its name says one, else the width class's.</summary>
    private static string FamilyOf(Sfnt.Info f)
    {
        string family = f.Typographic ?? f.Family ?? "";
        if (f.Width == NormalWidth || f.Width is < 1 or > 9) return family;
        string own = f.Family != null && f.Family.StartsWith(family, StringComparison.OrdinalIgnoreCase) ? f.Family[family.Length..] : "";
        string[] kept = own.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => WidthWords.Contains(w, StringComparer.OrdinalIgnoreCase)).ToArray();
        string width = kept.Length > 0 ? string.Join(' ', kept) : WidthNames[f.Width];
        return width.Length == 0 || family.EndsWith(width, StringComparison.OrdinalIgnoreCase) ? family : $"{family} {width}";
    }

    /// <summary>The face <paramref name="family"/> draws <paramref name="weight"/> and <paramref name="italic"/> with,
    /// and the weight it draws at: a variable face at the weight clamped to its axis, else the nearest static weight; an
    /// italic asked of a family with none is its upright letters. Null for a family the catalog does not hold.</summary>
    public (Face Face, int Weight)? Pick(string family, int weight, bool italic)
    {
        if (!_families.TryGetValue(family, out List<Face>? all)) return null;
        List<Face> styled = all.FindAll(f => f.Italic == italic);
        if (styled.Count == 0) styled = all.FindAll(f => !f.Italic);
        if (styled.Count == 0) styled = all;
        if (styled.Find(f => f.Variable) is Face v) return (v, Math.Clamp(weight, v.VarMin, v.VarMax));
        int at = Nearest(styled.ConvertAll(f => f.Weight), weight);
        return (styled.Find(f => f.Weight == at)!, at);
    }

    /// <summary>The face whose full name (name id 4) is <paramref name="fullName"/>, as an older CX stored a typeface.</summary>
    public Face? ByFullName(string fullName) => _byFullName.GetValueOrDefault(fullName);

    /// <summary>The weight of <paramref name="have"/> a web browser draws <paramref name="want"/> at: from 400 to 500 the
    /// weights up to 500 first, then lighter, then heavier; below 400 lighter first; above 500 heavier first.</summary>
    public static int Nearest(IReadOnlyList<int> have, int want)
    {
        if (have.Count == 0) return want;
        if (have.Contains(want)) return want;
        IEnumerable<int> lighter = have.Where(h => h < want).OrderByDescending(h => h), heavier = have.Where(h => h > want).Order();
        if (want >= 400 && want <= 500)
        {
            int[] upTo500 = heavier.Where(h => h <= 500).ToArray();
            if (upTo500.Length > 0) return upTo500[0];
            return lighter.Concat(heavier).First();
        }
        return want < 400 ? lighter.Concat(heavier).First() : heavier.Concat(lighter).First();
    }
}

/// <summary>What the font list reads from one sfnt font: names, OS/2 weight, width and style, its weight axis, and
/// whether stb_truetype can draw it. Three small reads per table, never the outlines.</summary>
internal static class Sfnt
{
    /// <summary>One font's facts; <see cref="Drawable"/> is false for CFF2 outlines, which stb_truetype cannot draw.</summary>
    internal sealed record Info(string? Family, string? Typographic, string? FullName, int Weight, int Width, bool Italic, int VarMin, int VarMax, bool Drawable);

    /// <summary>Tags as big-endian numbers.</summary>
    private const uint TrueType = 0x00010000, Otto = 0x4F54544F, TrueMac = 0x74727565, Ttcf = 0x74746366;   // not-an-offset: sfnt tags
    private const uint NameTag = 0x6E616D65, Os2Tag = 0x4F532F32, FvarTag = 0x66766172, Cff2Tag = 0x43464632, GlyfTag = 0x676C7966, CffTag = 0x43464620;   // not-an-offset: table tags
    private const uint WghtTag = 0x77676874;   // not-an-offset: the weight axis tag
    /// <summary>OS/2 fsSelection's italic and oblique bits.</summary>
    private const int ItalicBit = 1, ObliqueBit = 1 << 9;

    /// <summary>The fonts in <paramref name="b"/>: a collection's count, 1 for a single font, 0 for anything else.</summary>
    internal static int Count(byte[] b)
    {
        if (b.Length < 12) return 0;
        uint tag = BinaryPrimitives.ReadUInt32BigEndian(b);
        if (tag == Ttcf) return (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(8)), 256);
        return tag is TrueType or Otto or TrueMac ? 1 : 0;
    }

    /// <summary>Where font <paramref name="index"/> of <paramref name="b"/> starts: its offset table's position.</summary>
    internal static int Offset(byte[] b, int index)
        => BinaryPrimitives.ReadUInt32BigEndian(b) == Ttcf ? (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(12 + index * 4)) : 0;

    /// <summary>The facts of font <paramref name="index"/> in <paramref name="b"/>, or null when it is no readable font.</summary>
    internal static Info? Read(byte[] b, int index)
    {
        try
        {
            int at = Offset(b, index);
            int n = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at + 4));
            int name = -1, os2 = -1, fvar = -1;
            bool cff2 = false, outlines = false;
            for (int i = 0; i < n; i++)
            {
                int rec = at + 12 + i * 16;
                uint tag = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(rec));
                int off = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(rec + 8));
                if (tag == NameTag) name = off;
                else if (tag == Os2Tag) os2 = off;
                else if (tag == FvarTag) fvar = off;
                else if (tag == Cff2Tag) cff2 = true;
                else if (tag is GlyfTag or CffTag) outlines = true;
            }
            if (name < 0) return null;
            int weight = 400, width = 5, sel = 0;
            if (os2 >= 0)
            {
                weight = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(os2 + 4));
                width = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(os2 + 6));
                sel = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(os2 + 62));
            }
            int vmin = 0, vmax = 0;
            if (fvar >= 0)
            {
                int axes = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(fvar + 4)) + fvar, count = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(fvar + 8));
                int size = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(fvar + 10));
                for (int a = 0; a < count; a++)
                {
                    int rec = axes + a * size;
                    if (BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(rec)) != WghtTag) continue;
                    vmin = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(rec + 4)) >> 16;
                    vmax = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(rec + 12)) >> 16;
                }
            }
            ReadOnlySpan<byte> t = b.AsSpan(name);
            string? style = Name(t, 17) ?? Name(t, 2);
            bool italic = (sel & (ItalicBit | ObliqueBit)) != 0 || (style != null && (style.Contains("Italic") || style.Contains("Oblique")));
            return new Info(Name(t, 1), Name(t, 16), Name(t, 4), weight, width, italic, vmin, vmax, outlines && !cff2);
        }
        catch (ArgumentOutOfRangeException) { return null; }   // a table directory pointing past the file
    }

    /// <summary>Name record <paramref name="id"/>: Windows English first, then any Windows, Unicode, and Mac Roman.</summary>
    internal static string? Name(ReadOnlySpan<byte> t, int id)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(t[2..]), strOff = BinaryPrimitives.ReadUInt16BigEndian(t[4..]);
        string? win = null, uni = null, mac = null;
        for (int i = 0; i < count; i++)
        {
            int r = 6 + i * 12;
            if (r + 12 > t.Length) break;
            ReadOnlySpan<byte> rec = t.Slice(r, 12);
            int plat = BinaryPrimitives.ReadUInt16BigEndian(rec), enc = BinaryPrimitives.ReadUInt16BigEndian(rec[2..]);
            int lang = BinaryPrimitives.ReadUInt16BigEndian(rec[4..]), rid = BinaryPrimitives.ReadUInt16BigEndian(rec[6..]);
            int len = BinaryPrimitives.ReadUInt16BigEndian(rec[8..]), off = BinaryPrimitives.ReadUInt16BigEndian(rec[10..]);
            if (rid != id || len == 0) continue;
            int s = strOff + off;
            if (s + len > t.Length) continue;
            if (plat == 3 && (enc is 1 or 0) && (win == null || lang == 0x409)) { win = Encoding.BigEndianUnicode.GetString(t.Slice(s, len)); if (lang == 0x409) break; }   // not-an-offset: US English
            else if (plat == 0) uni ??= Encoding.BigEndianUnicode.GetString(t.Slice(s, len));
            else if (plat == 1 && enc == 0) mac ??= Encoding.Latin1.GetString(t.Slice(s, len));
        }
        string? name = (win ?? uni ?? mac)?.Trim();
        return string.IsNullOrEmpty(name) || name.Contains('\0') ? null : name;
    }
}
