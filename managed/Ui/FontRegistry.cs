using System.Buffers.Binary;
using System.Text;
using CX.Framework;

namespace CX.Ui;

/// <summary>
/// The typefaces the UI may ask for by short name: <c>font.&lt;name&gt;=&lt;path&gt;</c> in <c>cx.cfg</c>, plus
/// every font installed on the machine under its full name from the <c>name</c> table. <c>default</c> is the
/// embedded DejaVuSans unless <c>font.default=</c> in the file or <c>ui.typeface</c> (<see cref="Bind"/>) rebinds
/// it. A name that fails to load is a logged refusal resolving to the default face, never a missing-text frame.
/// </summary>
public static class FontRegistry
{
    public const string DefaultName = "default";
    /// <summary>The embedded DejaVuSans under its own reserved name: always loaded, never overridable from the
    /// cfg, because it is the chrome's icon source whatever <c>default</c> is bound to.</summary>
    public const string EmbeddedName = "embedded";
    /// <summary>The system's monospace face, the console's (<see cref="SystemMonospace"/>); <c>font.monospace=</c>
    /// in the cfg overrides it like any declared name.</summary>
    public const string MonospaceName = "monospace";
    private const string EmbeddedResource = "DejaVuSans";
    private const string Prefix = "font.";

    /// <summary>One declared typeface: where it came from and why it was refused, if it was.</summary>
    public sealed class Entry
    {
        public string Name = "", Source = "";
        public byte[]? Bytes;
        public string? Refusal;
    }

    private static readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;
    private static ExtenderLog? _log;
    /// <summary>What <c>default</c> was bound to at load: the embedded font or the <c>font.default=</c> file.</summary>
    private static Entry? _boot;
    /// <summary>Installed fonts by full name -> path, scanned once on first ask.</summary>
    private static Dictionary<string, string>? _system;
    private static string[]? _names;

    /// <summary>Every declared name, default first.</summary>
    public static IEnumerable<Entry> All { get { lock (_entries) return _entries.Values.OrderBy(e => e.Name != DefaultName).ThenBy(e => e.Name).ToArray(); } }

    /// <summary>The <c>ui.typeface</c> choices: <c>default</c>, then every cfg-declared name and every installed font's
    /// full name, sorted; a cfg name shadows an installed one of the same name. Built once, before the knob binds.</summary>
    public static string[] Names
    {
        get
        {
            if (_names != null) return _names;
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string key, string path) in Settings.Prefixed(Prefix))
            {
                string n = key.Substring(Prefix.Length);
                if (n.Length > 0 && !n.Equals(DefaultName, StringComparison.OrdinalIgnoreCase) && !n.Equals(EmbeddedName, StringComparison.OrdinalIgnoreCase)
                    && Resolve(path) != null) set.Add(n);   // a declared file that is not there is no choice
            }
            foreach (string n in SystemFonts().Keys) set.Add(n);
            var names = new string[set.Count + 1];
            names[0] = DefaultName;
            set.CopyTo(names, 1);
            return _names = names;
        }
    }

    /// <summary>The OS font directories: Linux's fontconfig defaults, Windows' system and per-user folders
    /// (under Proton the process is the Windows build, so it walks the prefix's <c>C:\windows\Fonts</c>).</summary>
    private static IEnumerable<string> FontDirs()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
            yield break;
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string data = HostEnv.DataHome;
        yield return "/usr/share/fonts";
        yield return "/usr/local/share/fonts";
        yield return Path.Combine(data, "fonts");
        yield return Path.Combine(home, ".fonts");
    }

    /// <summary>Walk the font directories once for <c>.ttf</c>/<c>.otf</c> files stbtt can open (collections are
    /// skipped: the bake inits at offset 0). Cold, first-name-wins on a duplicate full name.</summary>
    private static Dictionary<string, string> SystemFonts()
    {
        if (_system != null) return _system;
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 };
        foreach (string dir in FontDirs())
        {
            if (dir.Length == 0 || !Directory.Exists(dir)) continue;
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*", opt))
                {
                    string ext = Path.GetExtension(f);
                    if (!ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".otf", StringComparison.OrdinalIgnoreCase)) continue;
                    string? name = FullName(f);
                    if (name != null) found.TryAdd(name, f);
                }
            }
            catch (Exception ex) { _log?.Info($"FontRegistry: {dir} not walked ({ex.GetType().Name})"); }
        }
        return _system = found;
    }

    /// <summary>The font's full name (name id 4; Windows/Unicode English first, the Mac Roman record as the
    /// fallback) read through the table directory with three small reads, or null for anything that is not one
    /// sfnt font. Internal for the self-check.</summary>
    internal static string? FullName(string path)
    {
        try { using FileStream f = File.OpenRead(path); return FullName(f); }
        catch (Exception) { return null; }
    }

    internal static string? FullName(Stream f)
    {
        try
        {
            Span<byte> hdr = stackalloc byte[12];
            if (f.Read(hdr) < 12) return null;
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            if (tag != 0x00010000 && tag != 0x4F54544F && tag != 0x74727565) return null;   // 1.0 / 'OTTO' / 'true'
            int n = BinaryPrimitives.ReadUInt16BigEndian(hdr.Slice(4));
            if (n == 0 || n > 64) return null;
            var dir = new byte[n * 16];
            if (f.Read(dir) < dir.Length) return null;
            for (int i = 0; i < n; i++)
            {
                ReadOnlySpan<byte> rec = dir.AsSpan(i * 16, 16);
                if (BinaryPrimitives.ReadUInt32BigEndian(rec) != 0x6E616D65) continue;   // 'name'
                uint off = BinaryPrimitives.ReadUInt32BigEndian(rec.Slice(8)), len = BinaryPrimitives.ReadUInt32BigEndian(rec.Slice(12));
                if (len < 6 || len > 1 << 20 || off + len > (ulong)f.Length) return null;
                f.Seek(off, SeekOrigin.Begin);
                var t = new byte[len];
                return f.Read(t) < t.Length ? null : NameRecord(t, 4);
            }
        }
        catch (Exception) { }
        return null;
    }

    private static string? NameRecord(byte[] t, int wantId)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(2)), strOff = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(4));
        string? win = null, uni = null, mac = null;
        for (int i = 0; i < count; i++)
        {
            int r = 6 + i * 12;
            if (r + 12 > t.Length) break;
            ReadOnlySpan<byte> rec = t.AsSpan(r, 12);
            int plat = BinaryPrimitives.ReadUInt16BigEndian(rec), enc = BinaryPrimitives.ReadUInt16BigEndian(rec.Slice(2));
            int lang = BinaryPrimitives.ReadUInt16BigEndian(rec.Slice(4)), id = BinaryPrimitives.ReadUInt16BigEndian(rec.Slice(6));
            int len = BinaryPrimitives.ReadUInt16BigEndian(rec.Slice(8)), off = BinaryPrimitives.ReadUInt16BigEndian(rec.Slice(10));
            if (id != wantId || len == 0) continue;
            int s = strOff + off;
            if (s + len > t.Length) continue;
            if (plat == 3 && (enc == 1 || enc == 0) && (win == null || lang == 0x409)) { win = Encoding.BigEndianUnicode.GetString(t, s, len); if (lang == 0x409) break; }
            else if (plat == 0) uni ??= Encoding.BigEndianUnicode.GetString(t, s, len);
            else if (plat == 1 && enc == 0) mac ??= Encoding.Latin1.GetString(t, s, len);
        }
        string? name = (win ?? uni ?? mac)?.Trim();
        return string.IsNullOrEmpty(name) || name.Contains('\0') ? null : name;
    }

    /// <summary>The entry for <paramref name="name"/>: declared, or an installed font read on this first ask
    /// (a read failure is its refusal). Null for a name nobody knows.</summary>
    private static Entry? Find(string name)
    {
        lock (_entries)
        {
            if (_entries.TryGetValue(name, out Entry? e)) return e;
            if (!SystemFonts().TryGetValue(name, out string? path)) return null;
            e = new Entry { Name = name, Source = path };
            try { e.Bytes = File.ReadAllBytes(path); } catch (Exception ex) { e.Refusal = ex.GetType().Name; }
            _entries[name] = e;
            _log?.Info(e.Refusal == null ? $"FontRegistry: {name} = {e.Source} ({e.Bytes!.Length} bytes)"
                                         : $"FontRegistry: {name} = {e.Source} REFUSED ({e.Refusal}); falls back to {DefaultName}");
            return e;
        }
    }

    /// <summary>Bind <c>default</c> to <paramref name="name"/> (<c>ui.typeface</c>); <c>default</c> itself restores
    /// the load-time binding. False, and nothing moves, for an unknown or refused name.</summary>
    public static bool Bind(string name)
    {
        Entry? e = name.Equals(DefaultName, StringComparison.OrdinalIgnoreCase) ? _boot : Find(name);
        if (e == null || e.Bytes == null || _boot == null) return false;
        lock (_entries)
            _entries[DefaultName] = ReferenceEquals(e, _boot) ? _boot
                                  : new Entry { Name = DefaultName, Source = $"{e.Source} (ui.typeface {e.Name})", Bytes = e.Bytes };
        return true;
    }

    /// <summary>Read <c>font.*</c> once (cold) and load every file; the embedded default stands in when unset or refused.</summary>
    public static void Load(ExtenderLog log)
    {
        if (_loaded) return;
        _loaded = true;
        _log = log;
        foreach ((string key, string path) in Settings.Prefixed(Prefix))
        {
            string name = key.Substring(Prefix.Length);
            if (name.Length == 0) continue;
            if (name.Equals(EmbeddedName, StringComparison.OrdinalIgnoreCase)) { log.Info($"FontRegistry: {EmbeddedName} is reserved for the embedded font; {key} ignored"); continue; }
            var e = new Entry { Name = name, Source = path };
            string? full = Resolve(path);
            if (full == null) e.Refusal = "not found";
            else
            {
                e.Source = full;
                try { e.Bytes = File.ReadAllBytes(full); } catch (Exception ex) { e.Refusal = ex.GetType().Name; }
            }
            _entries[name] = e;
            log.Info(e.Refusal == null ? $"FontRegistry: {name} = {e.Source} ({e.Bytes!.Length} bytes)"
                                       : $"FontRegistry: {name} = {e.Source} REFUSED ({e.Refusal}); falls back to {DefaultName}");
        }
        if (!_entries.ContainsKey(MonospaceName))
        {
            var e = new Entry { Name = MonospaceName, Source = "system monospace" };
            string? mono = SystemMonospace();
            if (mono == null) e.Refusal = "none found";
            else
            {
                e.Source = mono;
                try { e.Bytes = File.ReadAllBytes(mono); } catch (Exception ex) { e.Refusal = ex.GetType().Name; }
            }
            _entries[MonospaceName] = e;
            log.Info(e.Refusal == null ? $"FontRegistry: {MonospaceName} = {e.Source} ({e.Bytes!.Length} bytes)"
                                       : $"FontRegistry: {MonospaceName} REFUSED ({e.Refusal}); falls back to {DefaultName}");
        }
        var emb = new Entry { Name = EmbeddedName, Source = "embedded " + EmbeddedResource };
        using (Stream? font = typeof(FontRegistry).Assembly.GetManifestResourceStream(EmbeddedResource))
        {
            if (font == null) emb.Refusal = "resource missing from the image";
            else { emb.Bytes = new byte[font.Length]; font.ReadExactly(emb.Bytes); }
        }
        _entries[EmbeddedName] = emb;
        if (!_entries.TryGetValue(DefaultName, out Entry? d) || d.Refusal != null)
        {
            // The default shares the embedded array: Bake tells "default IS the embedded font" by reference.
            var e = new Entry { Name = DefaultName, Source = emb.Source, Bytes = emb.Bytes, Refusal = emb.Refusal };
            if (d != null) { log.Info($"FontRegistry: {DefaultName} override refused ({d.Refusal}); using the embedded font"); e.Source = $"{e.Source} ({d.Source} REFUSED: {d.Refusal})"; }
            _entries[DefaultName] = e;
        }
        _boot = _entries[DefaultName];
    }

    /// <summary>Bytes for <paramref name="name"/>, or the default's when unknown or refused. Null only when the
    /// embedded default itself is missing from the image.</summary>
    public static byte[]? Bytes(string name)
    {
        if (Find(name) is { Bytes: not null } e) return e.Bytes;
        lock (_entries) return _entries.TryGetValue(DefaultName, out Entry? d) ? d.Bytes : null;
    }

    /// <summary>The name that actually backs <paramref name="name"/>: itself when loaded, else the default.</summary>
    public static string Effective(string name) => Find(name) is { Bytes: not null } e ? e.Name : DefaultName;

    /// <summary>The OS's own default monospace file: fontconfig's <c>monospace</c> alias on Linux (what a terminal
    /// that follows the desktop uses), and on Windows -- which has no such setting -- the console hosts' defaults,
    /// Cascadia Mono then Consolas, then Courier New. Only a single <c>.ttf</c>/<c>.otf</c>: the bake cannot open a
    /// collection. Cold, once, from <see cref="Load"/>.</summary>
    private static string? SystemMonospace()
    {
        if (OperatingSystem.IsWindows())
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            foreach (string f in new[] { "CascadiaMono.ttf", "consola.ttf", "cour.ttf" })
                if (dir.Length > 0 && File.Exists(Path.Combine(dir, f))) return Path.Combine(dir, f);
            return null;
        }
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("fc-match", "-f %{file} monospace")
            { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            string path = p.StandardOutput.ReadToEnd().Trim();
            if (!p.WaitForExit(2000)) { p.Kill(); return null; }
            string ext = Path.GetExtension(path);
            bool single = ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) || ext.Equals(".otf", StringComparison.OrdinalIgnoreCase);
            return single && File.Exists(path) ? path : null;
        }
        catch (Exception) { return null; }   // no fontconfig: the console keeps the UI face
    }

    /// <summary>A rooted path as given; a relative one beside the extender (the process directory, where nwmain
    /// sits next to the proxy DLL or the <c>.so</c>) and then in the user directory beside <c>cx.cfg</c> -- the
    /// form that survives Proton, where an absolute Linux path names nothing. Null when neither exists.</summary>
    private static string? Resolve(string path)
    {
        if (Path.IsPathRooted(path)) return File.Exists(path) ? path : null;
        string? exe = Path.GetDirectoryName(Environment.ProcessPath);
        if (exe != null && File.Exists(Path.Combine(exe, path))) return Path.GetFullPath(Path.Combine(exe, path));
        string user = Native.ConfigPath(path);
        return File.Exists(user) ? user : null;
    }

    /// <summary>What <c>/knobs</c> prints: each name, its source, and its state.</summary>
    public static string Report(Func<string, int> facesOf)
    {
        var sb = new System.Text.StringBuilder("fonts:");
        foreach (Entry e in All)
            sb.Append($"\n  {e.Name} = {e.Source}")
              .Append(e.Refusal == null ? $" ({facesOf(e.Name)} face(s))" : $" REFUSED: {e.Refusal} -> {DefaultName}");
        return sb.ToString();
    }

    /// <summary>Off-device: unknown names fall to the default, a rooted missing path is refused.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        Load(log);
        check.Expect(_entries.ContainsKey(DefaultName), "default is always declared");
        check.Expect(Effective("no-such-face") == DefaultName, "an unknown name resolves to the default");
        check.Expect(Resolve("/nonexistent/cx-selfcheck.ttf") == null, "a rooted missing path is refused");
        check.Expect(Bytes("no-such-face") == Bytes(DefaultName), "unknown-name bytes are the default's");
        check.Expect(_entries.ContainsKey(EmbeddedName), "the embedded font is always declared");

        // the name-table read on the embedded DejaVu, the choice list, and the default rebinding.
        if (Bytes(EmbeddedName) is byte[] emb)
            check.Expect(FullName(new MemoryStream(emb)) == "DejaVu Sans", $"the embedded font's full name reads back ({FullName(new MemoryStream(emb))})");
        check.Expect(FullName(new MemoryStream(Encoding.ASCII.GetBytes("not a font, not at all"))) == null, "a non-sfnt stream has no name");
        string[] names = Names;
        check.Expect(names.Length >= 1 && names[0] == DefaultName && names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length,
          $"the choices start with {DefaultName} and repeat nothing ({names.Length} names, {SystemFonts().Count} installed)");
        check.Expect(!names.Contains(EmbeddedName, StringComparer.OrdinalIgnoreCase), "the reserved embedded name is not a choice");
        byte[]? boot = Bytes(DefaultName);
        check.Expect(!Bind("no-such-face"), "binding an unknown name is refused");
        check.Expect(ReferenceEquals(Bytes(DefaultName), boot), "a refused bind leaves the default alone");
        check.Expect(Bind(DefaultName) && ReferenceEquals(Bytes(DefaultName), boot), "binding default keeps the load-time face");
        log.Info(check.Ok ? "FontRegistry.SelfCheck OK" : "FontRegistry.SelfCheck FAILED");
    }
}
