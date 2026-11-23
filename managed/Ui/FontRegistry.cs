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
    /// <summary>The weight of regular letters: a font that says no weight is this one.</summary>
    public const int Regular = 400;
    /// <summary>The embedded DejaVuSans under its own reserved name: always loaded, never overridable from the
    /// cfg, because it is the chrome's icon source whatever <c>default</c> is bound to.</summary>
    public const string EmbeddedName = "embedded";
    /// <summary>The system's monospace face, the console's (<see cref="SystemMonospace"/>); <c>font.monospace=</c>
    /// in the cfg overrides it like any declared name.</summary>
    public const string MonospaceName = "monospace";
    private const string EmbeddedResource = "DejaVuSans";
    /// <summary>The built-in DejaVu Sans files CX ships, each with its weight and whether it slants: the faces
    /// <c>default</c> offers while no <c>font.default=</c> line rebinds it. The first is the embedded regular.</summary>
    private static readonly (string Resource, int Weight, bool Italic)[] BuiltIn =
    {
        (EmbeddedResource, Regular, false), ("DejaVuSans-Bold", 700, false), ("DejaVuSans-ExtraLight", 200, false),
        ("DejaVuSans-Oblique", Regular, true), ("DejaVuSans-BoldOblique", 700, true),
    };
    private const string Prefix = "font.";

    /// <summary>One declared typeface: where it came from and why it was refused, if it was.</summary>
    public sealed class Entry
    {
        public string Name = "", Source = "";
        public byte[]? Bytes;
        public string? Refusal;
        /// <summary>The font's index inside a collection; 0 for a single font.</summary>
        public int Index;
    }

    private static readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;
    private static ExtenderLog? _log;
    /// <summary>What <c>default</c> was bound to at load: the embedded font or the <c>font.default=</c> file.</summary>
    private static Entry? _boot;
    /// <summary>The installed fonts, scanned once on first ask (or handed in by <see cref="UseCatalog"/>).</summary>
    private static FontCatalog? _catalog;
    private static string[]? _names;
    /// <summary>The fonts a hak, the module or NWSync supply, while that content is loaded.</summary>
    private static FontCatalog _content = FontCatalog.Empty;
    /// <summary><see cref="_content"/> over the installed fonts, made on first ask after either changes.</summary>
    private static FontCatalog? _merged;
    /// <summary>Every Typeface row made, offered the new list whenever the fonts change; a row nothing holds drops out.</summary>
    private static readonly List<WeakReference<Knob<string>>> _lists = new();
    /// <summary>The entries of installed families' faces, by face name; a new catalog drops them.</summary>
    private static readonly Dictionary<string, Entry> _faces = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Each face name once made, so a frame that asks again allocates nothing; a new catalog or declaration drops them.</summary>
    private static readonly Dictionary<(string Family, int Weight, int Style), string> _keys = new();
    /// <summary>Separates a face name's family, weight and style: no family name holds it.</summary>
    private const char KeySep = '|';

    /// <summary>Every declared name, default first.</summary>
    public static IEnumerable<Entry> All { get { lock (_entries) return _entries.Values.OrderBy(e => e.Name != DefaultName).ThenBy(e => e.Name).ToArray(); } }

    /// <summary>The <c>ui.typeface</c> choices: <c>default</c>, then the base game's own families, sorted, then every other
    /// cfg-declared name and family, sorted; a cfg name shadows a family of the same name. Rebuilt whenever the fonts change.</summary>
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
            FontCatalog fonts = Catalog();
            foreach (string n in fonts.Families) set.Add(n);
            string[] game = fonts.GameFamilies.Where(set.Remove).ToArray();
            return _names = game.Prepend(DefaultName).Concat(set).ToArray();
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

    /// <summary>The installed fonts, read once, cold, on first ask: every font fontconfig lists, or the font folders
    /// walked where there is no fontconfig (Windows, Proton, a bare Linux).</summary>
    private static FontCatalog Installed()
        => _catalog ??= FontCatalog.Scan(Fontconfig("fc-list", "--format", FcListFormat), FontDirs(), d => _log?.Info($"FontRegistry: {d} not walked"));

    /// <summary>The fonts a name may draw: the loaded module's content laid over the installed fonts.</summary>
    private static FontCatalog Catalog() => _merged ??= _content.Over(Installed());

    /// <summary>The entry for <paramref name="name"/>: declared, or an installed font read on this first ask
    /// (a read failure is its refusal). Null for a name nobody knows.</summary>
    private static Entry? Find(string name)
    {
        lock (_entries)
        {
            if (_entries.TryGetValue(name, out Entry? e) || _faces.TryGetValue(name, out e)) return e;
            if (Settings.Raw(Prefix + name) is string declared && name != EmbeddedName) return _entries[name] = Declared(name, declared);
            if (name.StartsWith(DefaultName + KeySep, StringComparison.Ordinal)) return _faces[name] = Shipped(name, name[(DefaultName.Length + 1)..]);
            if (Face(name) is not (FontCatalog.Face face, int weight)) return null;
            e = new Entry { Name = name, Source = face.Path, Index = face.Index };
            try { e.Bytes = face.Bytes ?? File.ReadAllBytes(face.Path); } catch (Exception ex) { e.Refusal = ex.GetType().Name; }
            if (e.Bytes != null && face.Variable && weight != face.Weight)
            {
                if (VarFont.Instance(e.Bytes, face.Index, weight) is byte[] inst) { e.Bytes = inst; e.Index = 0; e.Source = $"{face.Path} at {weight}"; }
                else { e.Refusal = $"its weight {weight} could not be made"; e.Bytes = null; }
            }
            _faces[name] = e;
            _log?.Info(e.Refusal == null ? $"FontRegistry: {name} = {e.Source} ({e.Bytes!.Length} bytes)"
                                         : $"FontRegistry: {name} = {e.Source} REFUSED ({e.Refusal}); falls back to {DefaultName}");
            return e;
        }
    }

    /// <summary>The installed face a face name stands for, and the weight it draws at; null for any other name.</summary>
    private static (FontCatalog.Face, int)? Face(string name)
    {
        string[] p = name.Split(KeySep);
        if (p.Length != 3 || !int.TryParse(p[1], out int weight)) return null;
        return Catalog().Pick(p[0], weight, p[2] == "i");
    }

    /// <summary>Bind <c>default</c> to <paramref name="name"/> (<c>ui.typeface</c>); <c>default</c> itself restores
    /// the load-time binding. False, and nothing moves, for an unknown or refused name.</summary>
    public static bool Bind(string name)
    {
        Entry? e = name.Equals(DefaultName, StringComparison.OrdinalIgnoreCase) ? _boot : Find(name);
        if (e == null || e.Bytes == null || _boot == null) return false;
        lock (_entries)
            _entries[DefaultName] = ReferenceEquals(e, _boot) ? _boot
                                  : new Entry { Name = DefaultName, Source = $"{e.Source} (ui.typeface {e.Name})", Bytes = e.Bytes, Index = e.Index };
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
            _entries[name] = Declared(name, path);
        }
        if (!_entries.ContainsKey(MonospaceName))
        {
            var e = new Entry { Name = MonospaceName, Source = "system monospace" };
            if (SystemMonospace() is not var (mono, index)) e.Refusal = "none found";
            else
            {
                e.Source = index == 0 ? mono : $"{mono} font {index}";
                e.Index = index;
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

    /// <summary>The entry of a font CX ships, read from the image: one of the built-in DejaVu Sans files.</summary>
    private static Entry Shipped(string name, string resource)
    {
        var e = new Entry { Name = name, Source = $"embedded {resource}" };
        using Stream? s = typeof(FontRegistry).Assembly.GetManifestResourceStream(resource);
        if (s == null) e.Refusal = "resource missing from the image";
        else { e.Bytes = new byte[s.Length]; s.ReadExactly(e.Bytes); }
        return e;
    }

    /// <summary>The entry a <c>font.&lt;name&gt;=&lt;path&gt;</c> line declares: the file read whole, or the reason it was refused.</summary>
    private static Entry Declared(string name, string path)
    {
        var e = new Entry { Name = name, Source = path };
        string? full = Resolve(path);
        if (full == null) e.Refusal = "not found";
        else
        {
            e.Source = full;
            try { e.Bytes = File.ReadAllBytes(full); } catch (Exception ex) { e.Refusal = ex.GetType().Name; }
        }
        _log?.Info(e.Refusal == null ? $"FontRegistry: {name} = {e.Source} ({e.Bytes!.Length} bytes)"
                                     : $"FontRegistry: {name} = {e.Source} REFUSED ({e.Refusal}); falls back to {DefaultName}");
        return e;
    }

    /// <summary>Declares <paramref name="name"/> from bytes the caller read, such as a font the game's own resources hold.
    /// Replaces an earlier declaration of the name.</summary>
    public static void Declare(string name, byte[] bytes, string source)
    {
        lock (_entries) { _entries[name] = new Entry { Name = name, Source = source, Bytes = bytes }; _keys.Clear(); }
        _log?.Info($"FontRegistry: {name} = {source} ({bytes.Length} bytes)");
    }

    /// <summary>Forgets a name <see cref="Declare"/> declared; a self-check's stand-ins leave the player's registry as it was.</summary>
    internal static void Undeclare(string name)
    {
        lock (_entries) { _entries.Remove(name); _keys.Clear(); }
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
    private static (string Path, int Index)? SystemMonospace()
    {
        if (OperatingSystem.IsWindows())
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            foreach (string f in new[] { "CascadiaMono.ttf", "consola.ttf", "cour.ttf" })
                if (dir.Length > 0 && File.Exists(Path.Combine(dir, f))) return (Path.Combine(dir, f), 0);
            return null;
        }
        return Fontconfig("fc-match", "-f", FcMatchFormat, "monospace") is string answer ? ParseFcMatch(answer) : null;
    }

    /// <summary>fc-match's answer: the file and the font's index in it, so a font inside a collection is found.</summary>
    internal const string FcMatchFormat = "%{file}:%{index}";

    /// <summary>One file per line, every font fontconfig knows.</summary>
    internal const string FcListFormat = "%{file}\\n";

    /// <summary>What a fontconfig tool printed, or null when there is no fontconfig (Windows, Proton, a bare
    /// Linux), it failed, or it had not exited <see cref="FcWaitMs"/> after its output closed. Cold, once per question.</summary>
    private static string? Fontconfig(string tool, params string[] args)
    {
        if (OperatingSystem.IsWindows()) return null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(tool) { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            string text = p.StandardOutput.ReadToEnd();   // before the wait: a long list fills the pipe and the tool would block
            if (!p.WaitForExit(FcWaitMs)) { p.Kill(); return null; }
            return p.ExitCode == 0 ? text : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }   // no such tool
    }

    /// <summary>How long a fontconfig tool may linger after its output closed before CX walks the font folders itself.</summary>
    private const int FcWaitMs = 5000;

    /// <summary>A rooted path as given; a relative one beside the extender (the process directory, where nwmain
    /// sits next to the proxy DLL or the <c>.so</c>) and then in the user directory beside <c>cx.cfg</c> -- the
    /// form that survives Proton, where an absolute Linux path names nothing. Null when neither exists.</summary>
    private static string? Resolve(string path)
    {
        if (Path.IsPathRooted(path)) return File.Exists(path) ? path : null;
        string? exe = Path.GetDirectoryName(Environment.ProcessPath);
        if (exe != null && File.Exists(Path.Combine(exe, path))) return Path.GetFullPath(Path.Combine(exe, path));
        string user = EngineLink.ConfigPath(path);
        return File.Exists(user) ? user : null;
    }

    /// <summary>The style rows' values: as UI (an element's row only), upright, italic.</summary>
    public const int StyleAsUi = 0, StyleNormal = 1, StyleItalic = 2;

    /// <summary>Where a face name draws from: the file, the font's index in it, the weight it draws at, and whether it is italic.</summary>
    public readonly record struct Place(string Path, int Index, int Weight, bool Italic);

    /// <summary>Draw the loaded module's fonts from <paramref name="content"/> from now on; the content it replaces comes back.</summary>
    internal static FontCatalog UseContent(FontCatalog content)
    {
        FontCatalog was;
        lock (_entries)
        {
            was = _content;
            _content = content;
            Forget();
        }
        Offer();
        return was;
    }

    /// <summary>Drops every list, face and face name the old fonts made. Under the entries' lock.</summary>
    private static void Forget()
    {
        _merged = null;
        _names = null;
        _faces.Clear();
        _keys.Clear();
    }

    /// <summary>Makes <paramref name="row"/> a Typeface row: an open dropdown of <see cref="Names"/>, offered the new list
    /// whenever the fonts change, that a server may hold only at a listed name.</summary>
    internal static Knob<string> Lists(Knob<string> row)
    {
        row.HoldsListedOnly = true;
        lock (_lists) _lists.Add(new WeakReference<Knob<string>>(row));
        return row;
    }

    /// <summary>Offers <see cref="Names"/> to every Typeface row still alive.</summary>
    private static void Offer()
    {
        string[] names = Names;
        lock (_lists)
        {
            _lists.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (WeakReference<Knob<string>> w in _lists)
                if (w.TryGetTarget(out Knob<string>? k)) k.Offer(names, names);
        }
    }

    /// <summary>Draw installed fonts from <paramref name="catalog"/> from now on; the catalog it replaces comes back.</summary>
    internal static FontCatalog UseCatalog(FontCatalog catalog)
    {
        FontCatalog was;
        lock (_entries)
        {
            was = Installed();
            _catalog = catalog;
            Forget();
        }
        Offer();
        return was;
    }

    /// <summary>The registry name of <paramref name="family"/> at <paramref name="weight"/> and <paramref name="style"/>: a face
    /// name for an installed family, else <paramref name="family"/> itself -- a declared name, <c>default</c> or a font a hak
    /// supplies is one face, drawn as it is.</summary>
    public static string FaceKey(string family, int weight, int style)
    {
        lock (_entries)
        {
            if (_keys.TryGetValue((family, weight, style), out string? key)) return key;
            key = family == DefaultName ? BuiltInKey(weight, style)
                : _entries.ContainsKey(family) || Settings.Raw(Prefix + family) != null || !Catalog().Holds(family) ? family
                : $"{family}{KeySep}{weight}{KeySep}{(style == StyleItalic ? 'i' : 'n')}";
            _keys[(family, weight, style)] = key;
            return key;
        }
    }

    /// <summary>True when <paramref name="name"/> is an installed family, or the built-in one, so a weight or a style can pick its face.</summary>
    public static bool IsFamily(string name)
        => name == DefaultName ? BuiltInDefault : FaceKey(name, Regular, StyleNormal) != name;

    /// <summary>True while <c>default</c> is the built-in DejaVu Sans, which no <c>font.default=</c> line rebound.</summary>
    private static bool BuiltInDefault => Settings.Raw(Prefix + DefaultName) == null;

    /// <summary>The built-in face a weight and style pick: <c>default</c> itself for the regular, else its file's name.</summary>
    private static string BuiltInKey(int weight, int style)
    {
        if (!BuiltInDefault) return DefaultName;
        var styled = BuiltIn.Where(b => b.Italic == (style == StyleItalic)).ToArray();
        int at = FontCatalog.Nearest(styled.Select(b => b.Weight).ToArray(), weight);
        string resource = styled.First(b => b.Weight == at).Resource;
        return resource == EmbeddedResource ? DefaultName : $"{DefaultName}{KeySep}{resource}";
    }

    /// <summary>The family a row names: an empty or <c>default</c> typeface follows <c>ui.typeface</c>.</summary>
    public static string UiFamily(string typeface) => typeface.Length == 0 || typeface == DefaultName ? UiKnobs.Typeface.V : typeface;

    /// <summary>The face name of rows set over the UI's: a typeface <see cref="UiFamily"/> resolves, and a weight or style
    /// of 0 that follows <c>ui.weight</c> or <c>ui.style</c>.</summary>
    public static string UiFace(string typeface = "", int weight = 0, int style = 0)
        => FaceKey(UiFamily(typeface), weight > 0 ? weight : UiKnobs.FontWeight.V, style > 0 ? style : UiKnobs.FontStyle.V);

    /// <summary><see cref="RunFace"/> over the UI's own rows: a run of text in a card or on the main menu.</summary>
    public static string UiRunFace(int runWeight, int runStyle, bool runSlant, out bool slant)
        => RunFace(UiKnobs.Typeface.V, UiKnobs.FontWeight.V, UiKnobs.FontStyle.V, runWeight, runStyle, runSlant, out slant);

    /// <summary>Where <paramref name="name"/> draws from, an installed family's face or a declared name's file; null for a name nobody knows.</summary>
    public static Place? Where(string name)
    {
        if (Face(name) is (FontCatalog.Face f, int w)) return new Place(f.Path, f.Index, w, f.Italic);
        if (Find(name) is not { Bytes: byte[] b } e) return null;
        Sfnt.Info? info = Sfnt.Read(b, e.Index);
        return new Place(e.Source, e.Index, info?.Weight ?? Regular, info?.Italic ?? false);
    }

    /// <summary>The face a run of text draws with: its own weight and style when it asks (non-zero), else the window's.
    /// <paramref name="slant"/> is true when the run is slanted (<paramref name="runSlant"/>) and the face drawn is upright.</summary>
    public static string RunFace(string family, int weight, int style, int runWeight, int runStyle, bool runSlant, out bool slant)
    {
        string key = FaceKey(family, runWeight > 0 ? runWeight : weight, runStyle > 0 ? runStyle : style);
        slant = runSlant && Where(key) is not { Italic: true };
        return key;
    }

    /// <summary>The family, weight and style an older stored typeface (a font's full name) stands for; null when it names
    /// no installed font, or names a family already.</summary>
    public static (string Family, int Weight, int Style)? Migrate(string stored)
    {
        if (Catalog().Holds(stored) || Catalog().ByFullName(stored) is not FontCatalog.Face f) return null;
        return (f.Family, f.Weight, f.Italic ? StyleItalic : StyleNormal);
    }

    /// <summary>Rewrites an older stored typeface under <paramref name="typefaceKey"/> as its family, and writes its weight and
    /// style under the two keys beside it when the file sets none. Cold; before the rows bind.</summary>
    internal static void MigrateStored(string typefaceKey, string weightKey, string styleKey)
    {
        if (Settings.Raw(typefaceKey) is not string raw || Catalog().Holds(raw) || Migrate(raw) is not var (family, weight, style)) return;
        var sb = new StringBuilder($"{typefaceKey}={family}\n");
        if (Settings.Raw(weightKey) == null) sb.Append($"{weightKey}={weight}\n");
        if (Settings.Raw(styleKey) == null) sb.Append($"{styleKey}={(style == StyleItalic ? "Italic" : "Normal")}\n");
        Settings.Read(sb.ToString());
    }

    /// <summary>The file and the index in it of fontconfig's <c>%{file}:%{index}</c> answer; null for anything else.</summary>
    internal static (string Path, int Index)? ParseFcMatch(string output)
    {
        string s = output.Trim();
        int colon = s.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(s.AsSpan(colon + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index)) return null;
        string path = s[..colon];
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        return index < Sfnt.Count(bytes) && Sfnt.Read(bytes, index) is { Drawable: true } ? (path, index) : null;
    }

    /// <summary>The font's index inside a collection that backs <paramref name="name"/>; 0 for a single font or an unknown name.</summary>
    public static int Index(string name) => Find(name) is { Bytes: not null } e ? e.Index : 0;

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
            check.Expect(Sfnt.Read(emb, 0)?.FullName == "DejaVu Sans", $"the embedded font's full name reads back ({Sfnt.Read(emb, 0)?.FullName})");
        check.Expect(Sfnt.Count(Encoding.ASCII.GetBytes("not a font, not at all")) == 0, "a non-sfnt file holds no font");
        string[] names = Names;
        check.Expect(names.Length >= 1 && names[0] == DefaultName && names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length,
          $"the choices start with {DefaultName} and repeat nothing ({names.Length} names, {Catalog().Families.Length} installed families)");
        check.Expect(!names.Contains(EmbeddedName, StringComparer.OrdinalIgnoreCase), "the reserved embedded name is not a choice");
        byte[]? boot = Bytes(DefaultName);
        check.Expect(!Bind("no-such-face"), "binding an unknown name is refused");
        check.Expect(ReferenceEquals(Bytes(DefaultName), boot), "a refused bind leaves the default alone");
        check.Expect(Bind(DefaultName) && ReferenceEquals(Bytes(DefaultName), boot), "binding default keeps the load-time face");
        log.Info(check.Ok ? "FontRegistry.SelfCheck OK" : "FontRegistry.SelfCheck FAILED");
    }
}
