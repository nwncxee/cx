namespace CX.Services.Update;

/// <summary>A release notice: the release's version, its changelog lines, and each zip's SHA-256 by file name.</summary>
internal sealed record UpdateManifest(string Version, string[] Changes, IReadOnlyDictionary<string, string> Sums)
{
    /// <summary>The first line every notice opens with.</summary>
    public const string Header = "cx-update 1";

    /// <summary>Read a notice's text, or null when it is not one. Lines it does not know are skipped, so a later
    /// notice can say more without an older CX refusing it.</summary>
    public static UpdateManifest? Parse(string text)
    {
        string[] lines = text.Replace("\r", "").Split('\n');
        if (lines.Length == 0 || lines[0] != Header) return null;
        string? version = null;
        var changes = new List<string>();
        var sums = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string l in lines.AsSpan(1))
        {
            if (l.StartsWith("version ", StringComparison.Ordinal)) version ??= l[8..].Trim();
            else if (l.StartsWith("change ", StringComparison.Ordinal)) changes.Add(l[7..].Trim());
            else if (l.Split(' ') is ["sha256", { Length: 64 } sum, { Length: > 0 } zip] && sum.All(char.IsAsciiHexDigitLower)) sums.TryAdd(zip, sum);
        }
        return version != null && Parse3(version) != null ? new UpdateManifest(version, [.. changes], sums) : null;
    }

    /// <summary>The X.Y.Z a version string opens with (<c>v0.13.11-3-g27e6276a</c> is 0.13.11), or null.</summary>
    public static (int, int, int)? Parse3(string v)
    {
        string s = v.StartsWith('v') ? v[1..] : v;
        int dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        string[] p = s.Split('.');
        return p.Length == 3 && int.TryParse(p[0], out int a) && int.TryParse(p[1], out int b) && int.TryParse(p[2], out int c) ? (a, b, c) : null;
    }
}
