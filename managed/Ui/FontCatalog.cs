using System.Buffers.Binary;
using System.Text;

namespace CX.Ui;

/// <summary>Where a font resource's winning copy lives: loose in <c>override/</c>, in the base game's own data, or in
/// a hak, the module or NWSync.</summary>
public enum FontSource { Loose, Game, Server }

/// <summary>The fonts a scan found, grouped into families by typographic family name and width, and the face a
/// family offers nearest a weight and a style.</summary>
public sealed class FontCatalog
{
    /// <summary>One font: a file, or one font inside a collection at <see cref="Index"/>. <see cref="Weight"/> is its
    /// OS/2 weight, a variable font's default; its weight axis runs <see cref="VarMin"/> to <see cref="VarMax"/>, both 0
    /// for a static font. A resource of the module's content carries its <see cref="Bytes"/>, and its resref as the path.</summary>
    public sealed record Face(string Family, int Weight, bool Italic, int Width, string Path, int Index, int VarMin, int VarMax, byte[]? Bytes = null)
    {
        public bool Variable => VarMax > VarMin;
    }

    /// <summary>A catalog of no fonts.</summary>
    public static readonly FontCatalog Empty = new(new List<Face>(), new Dictionary<string, Face>());

    /// <summary>The OS/2 width class of normal letters; any other width is a family of its own.</summary>
    internal const int NormalWidth = 5;
    /// <summary>Words a family's own name uses for a width, kept in a family name that is not normal width.</summary>
    private static readonly string[] WidthWords = { "Condensed", "SemiCondensed", "Narrow", "Compressed", "Expanded", "Extended", "Wide" };
    /// <summary>The OS/2 width classes 1 to 9, named for a family whose name says no width of its own.</summary>
    private static readonly string[] WidthNames = { "", "UltraCondensed", "ExtraCondensed", "Condensed", "SemiCondensed", "", "SemiExpanded", "Expanded", "ExtraExpanded", "UltraExpanded" };
    private static readonly string[] Extensions = { ".ttf", ".otf", ".ttc", ".otc" };

    private readonly List<Face> _faces;
    private readonly Dictionary<string, List<Face>> _families = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Face> _byFullName;

    private FontCatalog(List<Face> faces, Dictionary<string, Face> byFullName)
    {
        _faces = faces;
        foreach (Face f in faces)
        {
            if (!_families.TryGetValue(f.Family, out List<Face>? l)) _families[f.Family] = l = new List<Face>();
            l.Add(f);
        }
        _byFullName = byFullName;
        Families = _families.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Every family, sorted, each once.</summary>
    public string[] Families { get; }

    /// <summary>True when <paramref name="family"/> is a family of this catalog.</summary>
    public bool Holds(string family) => _families.ContainsKey(family);

    /// <summary>The fonts <paramref name="fcList"/> names (one path per line, fontconfig's own list; null when there is
    /// no fontconfig), else the fonts under <paramref name="dirs"/>; <paramref name="skipped"/> hears each folder it could not
    /// walk. Cold: it opens every font file it finds.</summary>
    public static FontCatalog Scan(string? fcList, IEnumerable<string> dirs, Action<string>? skipped = null)
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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped?.Invoke($"{dir} ({ex.GetType().Name})"); }
            }
        }
        var faces = new List<Face>();
        var byFull = new Dictionary<string, Face>(StringComparer.OrdinalIgnoreCase);
        foreach (string p in paths)
        {
            if (!Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)) continue;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(p); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            Add(faces, byFull, p, bytes, keep: false);
        }
        return new FontCatalog(faces, byFull);
    }

    /// <summary>Each drawable font in <paramref name="bytes"/>, read from <paramref name="path"/>, onto the lists; with
    /// <paramref name="keep"/> each face holds the bytes, for a font no file holds.</summary>
    private static void Add(List<Face> faces, Dictionary<string, Face> byFull, string path, byte[] bytes, bool keep)
    {
        int count = Sfnt.Count(bytes);
        for (int i = 0; i < count; i++)
            if (Sfnt.Read(bytes, i) is Sfnt.Info info && info.Drawable)
            {
                var face = new Face(FamilyOf(info), info.Weight, info.Italic, info.Width, path, i, info.VarMin, info.VarMax, keep ? bytes : null);
                faces.Add(face);
                if (info.FullName != null) byFull.TryAdd(info.FullName, face);
            }
    }

    /// <summary>The fonts of the loaded module's content: each resource a hak, the module or NWSync supplies
    /// (<see cref="FontSource.Server"/>), read from its own bytes; a loose override file or the base game's own is left out.</summary>
    public static FontCatalog FromContent(IEnumerable<(string ResRef, FontSource Source, byte[] Bytes)> resources)
    {
        var faces = new List<Face>();
        var byFull = new Dictionary<string, Face>(StringComparer.OrdinalIgnoreCase);
        foreach ((string resref, FontSource source, byte[] bytes) in resources)
            if (source == FontSource.Server) Add(faces, byFull, resref + ".ttf", bytes, keep: true);
        return new FontCatalog(faces, byFull);
    }

    /// <summary>This catalog laid over <paramref name="under"/>: a family both hold is this one's alone.</summary>
    public FontCatalog Over(FontCatalog under)
    {
        var faces = new List<Face>(_faces);
        faces.AddRange(under._faces.Where(f => !_families.ContainsKey(f.Family)));
        var byFull = new Dictionary<string, Face>(_byFullName, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, f) in under._byFullName) byFull.TryAdd(name, f);
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
    /// <summary>One font's facts; <see cref="Drawable"/> is false for a font with neither <c>glyf</c> nor <c>CFF </c>
    /// outlines, such as a CFF2 font, which stb_truetype cannot draw.</summary>
    internal sealed record Info(string? Family, string? Typographic, string? FullName, int Weight, int Width, bool Italic, int VarMin, int VarMax, bool Drawable);

    /// <summary>Tags as big-endian numbers.</summary>
    private const uint TrueType = 0x00010000, Otto = 0x4F54544F, TrueMac = 0x74727565, Ttcf = 0x74746366;   // not-an-offset: sfnt tags
    /// <summary>The weight axis's tag in <c>fvar</c>.</summary>
    internal const string WeightAxis = "wght";
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

    /// <summary>Font <paramref name="index"/>'s table directory: each table's tag, offset into the file, and length.</summary>
    internal static Dictionary<string, (int Off, int Len)> Tables(byte[] b, int index)
    {
        int at = Offset(b, index);
        var tables = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        for (int i = 0, n = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at + 4)); i < n; i++)
        {
            int rec = at + 12 + i * 16;
            tables[Encoding.ASCII.GetString(b, rec, 4)] = ((int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(rec + 8)), (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(rec + 12)));
        }
        return tables;
    }

    /// <summary>The facts of font <paramref name="index"/> in <paramref name="b"/>, or null when it is no readable font.</summary>
    internal static Info? Read(byte[] b, int index)
    {
        try
        {
            Dictionary<string, (int Off, int Len)> tables = Tables(b, index);
            if (!tables.TryGetValue("name", out var nameTable)) return null;
            int name = nameTable.Off;
            int os2 = tables.TryGetValue("OS/2", out var o) ? o.Off : -1, fvar = tables.TryGetValue("fvar", out var v) ? v.Off : -1;
            bool outlines = tables.ContainsKey("glyf") || tables.ContainsKey("CFF ");
            int weight = FontRegistry.Regular, width = FontCatalog.NormalWidth, sel = 0;
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
                    if (Encoding.ASCII.GetString(b, rec, 4) != WeightAxis) continue;
                    vmin = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(rec + 4)) >> 16;
                    vmax = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(rec + 12)) >> 16;
                }
            }
            ReadOnlySpan<byte> t = b.AsSpan(name);
            return new Info(Name(t, 1), Name(t, 16), Name(t, 4), weight, width, (sel & (ItalicBit | ObliqueBit)) != 0, vmin, vmax, outlines);
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
