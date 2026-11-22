using CX.Framework;

namespace CX.Services.Hotkeys;

/// <summary>
/// The consent list and its persistence, a capability scoped PER SERVER by a client-minted token: on consent
/// the client mints one, records {token -> keys} and hands it over; a later login restores exactly that
/// server's keys when it presents the token back. A server granted none (including one replaying another's
/// list) arms nothing. Cfg is <see cref="Path"/>, SDL keycodes (SDLK, NOT Windows VK: f=102, F1=1073741882).
/// </summary>
public static class HotkeyStore
{
    /// <summary>The keys captured right now: what the server last asked for, narrowed to what the
    /// player has consented to. Read on the hot path; swapped atomically.</summary>
    public static volatile HashSet<int> Armed = new();

    /// <summary>
    /// What the player has consented to for the ACTIVE server - the persisted list, and the only
    /// thing a prompt is measured against. A server asking for fewer keys arms fewer keys; it does
    /// not un-consent the rest, or a character with no bindings would wipe the consent every other
    /// one relies on. Only the player shrinks this, through the manager window.
    /// </summary>
    public static volatile HashSet<int> Consented = new();

    // tokenHex -> that server's consented keys. Loaded from the cfg; the active set is one of these.
    private static readonly Dictionary<string, HashSet<int>> _sections = new();
    // The token presented by the current server this session, or null if none/unrecognised-and-unconsented.
    private static string? _activeToken;

    public static string Path => EngineLink.ConfigPath("cx-hotkeys.cfg");

    private static bool _loaded;

    /// <summary>
    /// Load the cfg on first real use, gated by <see cref="ConfigFile.Ready"/> -- the one place the pre-main
    /// deferral rule lives. This file's own reader stays hand-rolled: the format is sectioned
    /// (<c>[token=..]</c> headers), not the flat key=value the shared walker covers.
    /// </summary>
    private static void EnsureLoaded(ExtenderLog log)
    {
        if (_loaded || !ConfigFile.Ready) return;
        Load(log);
        _loaded = true;
    }

    public static void Load(ExtenderLog log)
    {
        _sections.Clear();
        int legacy = 0, malformed = 0;
        try
        {
            string readPath = ConfigFile.ReadPath("cx-hotkeys.cfg", log);
            if (File.Exists(readPath))
                legacy = Parse(File.ReadAllLines(readPath), _sections, out malformed);
        }
        catch (Exception e) { log.Info($"HotkeyStore: load failed: {e.Message}"); }

        // Startup publishes EMPTY -- nothing is armed until a login presents a recognised token.
        _activeToken = null;
        Armed = new HashSet<int>();
        Consented = new HashSet<int>();

        if (legacy > 0)
            log.Info($"HotkeyStore: dropped {legacy} legacy global armed key(s) -- consent is now per-server; " +
                     "reconnect to each server to re-consent (they re-push their key set on login)");
        if (malformed > 0)
            log.Always($"HotkeyStore: dropped {malformed} section(s) whose token is not one this client mints");
        log.Info($"HotkeyStore: {_sections.Count} server section(s) loaded from {Path}; none active until a token is presented");
    }

    /// <summary>
    /// Read the cfg's lines into <paramref name="into"/>. A section whose header is not a token
    /// <see cref="IsToken"/> accepts is dropped with its keys. Returns the bare keycodes before any header.
    /// </summary>
    private static int Parse(IEnumerable<string> lines, Dictionary<string, HashSet<int>> into, out int malformed)
    {
        int legacy = 0;
        malformed = 0;
        HashSet<int>? cur = null;   // null until the first valid [token=] header, and under a refused one
        bool inSection = false;
        foreach (string raw in lines)
        {
            string line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("[token=") && line.EndsWith("]"))
            {
                string tok = line[7..^1].Trim().ToLowerInvariant();
                inSection = true;
                if (IsToken(tok)) cur = into[tok] = new HashSet<int>();
                else { cur = null; malformed++; }
            }
            else if (int.TryParse(line, out int vk))
            {
                if (cur != null) cur.Add(vk);
                else if (!inSection) legacy++;   // a bare keycode before any token section == old global format
            }
        }
        return legacy;
    }

    /// <summary>True for exactly what <see cref="Mint"/> makes: 32 lowercase hex characters. Anything
    /// else is never looked up, adopted or written.</summary>
    public static bool IsToken(string tok)
    {
        if (tok.Length != TokenLength) return false;
        foreach (char ch in tok)
            if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))) return false;
        return true;
    }

    /// <summary>A token's length in hex characters: 128 bits.</summary>
    private const int TokenLength = 32;

    /// <summary>A new login: forget the previous server's consent. Armed goes empty (fail-closed) until a recognised token is presented.</summary>
    public static void BeginSession()
    {
        _activeToken = null;
        Armed = new HashSet<int>();
        Consented = new HashSet<int>();
    }

    /// <summary>
    /// The server presented `tokenHex` (proof of prior consent). If recognised, restore that server's
    /// consented keys; otherwise arm nothing and keep no token, so a later Grant mints a fresh one: the
    /// client never adopts a string the server chose. Returns true if already known.
    /// </summary>
    public static bool Activate(string tokenHex, ExtenderLog log)
    {
        EnsureLoaded(log);
        string tok = tokenHex.Trim().ToLowerInvariant();
        bool known = IsToken(tok) && _sections.ContainsKey(tok);
        _activeToken = known ? tok : null;
        Consented = known ? new HashSet<int>(_sections[tok]) : new HashSet<int>();
        Armed = new HashSet<int>(Consented);
        log.Info(known
            ? $"HotkeyStore: token recognised -- restored {Consented.Count} consented key(s)"
            : IsToken(tok)
                ? "HotkeyStore: token not recognised -- armed set empty, awaiting fresh consent"
                : "HotkeyStore: presented token is malformed -- ignored, armed set empty");
        return known;
    }

    /// <summary>
    /// Add player-consented keys to the ACTIVE server's set and persist. Only called after an explicit
    /// OK in the consent modal. With no recognised token this session, mint one so the consent has a
    /// receipt to hand back. Returns the active token (the caller sends it to the server).
    /// </summary>
    public static string Grant(HashSet<int> keys, ExtenderLog log)
    {
        EnsureLoaded(log);
        if (_activeToken == null)
        {
            _activeToken = Mint();
            log.Info("HotkeyStore: no server token this session -- minted a fresh consent token");
        }
        if (!_sections.TryGetValue(_activeToken, out HashSet<int>? set))
            set = _sections[_activeToken] = new HashSet<int>();
        set.UnionWith(keys);

        Consented = new HashSet<int>(set);
        var armed = new HashSet<int>(Armed);
        armed.UnionWith(keys);
        Armed = armed;
        Save(log);
        log.Info($"HotkeyStore: player armed {keys.Count} new key(s) for this server; {set.Count} total");
        return _activeToken;
    }

    /// <summary>
    /// Replace the ACTIVE server's consent wholesale and persist - the player revoking, through the
    /// panel's revoke row. A server may not reach this: its proposals go through
    /// <see cref="Capture"/>, which never writes the file. No active token -> no-op.
    /// </summary>
    public static void Set(HashSet<int> keys, ExtenderLog log)
    {
        EnsureLoaded(log);
        if (_activeToken == null) return;
        _sections[_activeToken] = new HashSet<int>(keys);
        Consented = new HashSet<int>(keys);
        Armed = new HashSet<int>(keys);
        Save(log);
    }

    /// <summary>
    /// Forget every server (spec §4.5): every token section wiped and the file rewritten empty, the active
    /// server's session state emptied with them. No packet -- the caller sends today's Revoke-all first when in
    /// session. The active token is kept, so a fresh consent this session lands under the token the server holds.
    /// </summary>
    public static void Forget(ExtenderLog log)
    {
        EnsureLoaded(log);
        _sections.Clear();
        Consented = new HashSet<int>();
        Armed = new HashSet<int>();
        Save(log);
        log.Info("HotkeyStore: forgot every server -- every token section wiped");
    }

    /// <summary>
    /// What the server last asked to capture, already narrowed to consented keys by the caller.
    /// Session state only: nothing here changes what the player has consented to.
    /// </summary>
    public static void Capture(HashSet<int> keys)
    {
        Armed = new HashSet<int>(keys);
    }

    /// <summary>
    /// A fresh token: a v4 GUID (122 bits from the OS CSPRNG) as 32 lowercase hex chars. Not
    /// <c>RandomNumberGenerator</c>: on Linux it loads OpenSSL, which the client's own exported OpenSSL breaks.
    /// </summary>
    private static string Mint() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// The leak-critical invariants: an ungranted or malformed token restores nothing and is never adopted,
    /// a cfg header that is not a minted token is dropped, minted tokens are well-formed and unpredictable.
    /// Oracles: hand-written tokens and cfg text below. Its probe logins log to a tap, never to cx.log.
    /// </summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        var heard = new List<string>();
        var ear = new ExtenderLog { Tap = heard.Add };
        string ghost = new string('f', TokenLength);
        check.Expect(!Activate(ghost, ear) && Armed.Count == 0 && Consented.Count == 0,
                     "an ungranted token restored keys");
        check.Expect(_activeToken == null, "an ungranted token was adopted as the storage key");

        check.Expect(!Activate("x]\n102\n[token=y", ear) && _activeToken == null && Armed.Count == 0,
                     "a token carrying a newline was adopted");
        check.Expect(heard.Count == 2 && heard[0].Contains("not recognised") && heard[1].Contains("malformed"),
                     $"the probe logins said {string.Join(" | ", heard)}; expected not-recognised then malformed");
        check.Expect(!IsToken("0123456789abcdef0123456789abcde") && !IsToken("0123456789abcdef0123456789abcdeg")
                     && !IsToken("0123456789ABCDEF0123456789ABCDEF") && IsToken("0123456789abcdef0123456789abcdef"),
                     "the token shape accepts the wrong strings");

        var parsed = new Dictionary<string, HashSet<int>>();
        int legacy = Parse(new[]
        {
            "[token=x]", "102",
            "[token=0123456789abcdef0123456789abcdef]", "1073741882",
            "[token=y]", "103",
        }, parsed, out int malformed);
        check.Expect(parsed.Count == 1 && parsed.TryGetValue("0123456789abcdef0123456789abcdef", out var only)
                     && only.Count == 1 && only.Contains(1073741882), "a malformed cfg section was read as consent");
        check.Expect(malformed == 2 && legacy == 0, $"malformed={malformed} legacy={legacy}, expected 2 and 0");

        BeginSession();
        check.Expect(Armed.Count == 0 && Consented.Count == 0 && _activeToken == null, "a fresh session is not empty");
        string t1 = Mint(), t2 = Mint();
        check.Expect(IsToken(t1) && IsToken(t2) && t1 != t2, "a minted token is malformed or repeated");
        if (check.Ok) log.Info("HotkeyStore.SelfCheck passed: consent fails closed");
    }

    public static void Save(ExtenderLog log)
    {
        // Same deferral rule as Load. NOT retryable: the callers are one-shot
        // consent actions, so a grant made while the user dir is unresolved persists only in memory. Reachable
        // only if consent ever moves off the in-game modal, which runs long past pre-main.
        if (!ConfigFile.Ready) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var lines = new List<string>
            {
                "# CX hotkeys -- per-server consent. One [token=<hex>] section per server;",
                "# one SDL keycode per line under it. Deleting a section revokes that server's consent.",
            };
            foreach (var (tok, keys) in _sections)
            {
                if (!IsToken(tok)) continue;
                lines.Add("");
                lines.Add($"[token={tok}]");
                foreach (int code in keys) lines.Add(code.ToString());
            }
            File.WriteAllLines(Path, lines);
        }
        catch (Exception e) { log.Info($"HotkeyStore: save failed: {e.Message}"); }
    }
}
