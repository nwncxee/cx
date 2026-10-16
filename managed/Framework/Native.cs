using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;

namespace CX.Framework;

/// <summary>
/// Thin bridge to the client's own functions and data. The binary is unstripped and
/// (near-)fully exported, so we resolve engine symbols by mangled name and either call
/// them through their declared signature (<c>EngineFn</c>) or read globals through them.
/// </summary>
public static unsafe class Native
{
    /// <summary>
    /// Address of a symbol (function or data), or Zero if absent. Pass the LINUX mangled
    /// name; SymbolMap maps it to the running client. Unresolved -> Zero, so callers that
    /// null-check degrade gracefully on a client that doesn't expose the symbol.
    /// </summary>
    public static IntPtr Sym(string linux) => SymbolMap.Resolve(linux, null);

    /// <summary>
    /// Build a CExoString on the native heap from a C# string, as <see cref="EngineBytes"/> encodes it.
    /// The layout is opaque: we zero an over-sized buffer and let the client's own InitFromCharArray
    /// populate it. Caller MUST <see cref="FreeExoString"/>.
    /// </summary>
    public static IntPtr NewExoString(string s)
    {
        IntPtr exo = Marshal.AllocHGlobal(32);              // > sizeof(CExoString); zeroed below
        for (int i = 0; i < 32; i++) ((byte*)exo)[i] = 0;
        if (EngineFn.ExoStringInit == null) return exo;
        byte[] b = EngineBytes(s);
        fixed (byte* p = b) EngineFn.ExoStringInit(exo, p, (uint)b.Length);   // copies into its own buffer
        return exo;
    }

    /// <summary>The bytes the engine would hold for <paramref name="s"/>: the client stores codepage bytes, never
    /// UTF-8, so typed text goes through the same <c>Encoding::FromUTF8</c> transcode and lands on the wire as
    /// vanilla would have sent it. A module <c>encoding.2da</c>'s reverse table can miss 0x80-0x9f; ponytail:
    /// decoding engine text with <c>Encoding::ToUTF8</c> on ingest removes that byte-space entirely.</summary>
    public static byte[] EngineBytes(string s)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(s);
        if (EngineFn.EncodingFromUtf8 == null || EngineFn.OperatorDelete == null) return utf8;
        byte* str = stackalloc byte[32];                    // sret std::string, both CRTs' sizeof
        fixed (byte* c = NulTerminated(utf8)) EngineFn.EncodingFromUtf8(str, c, 0);
        var (buf, len, heap) = StdString(str);
        byte[] r = new ReadOnlySpan<byte>((byte*)buf, (int)len).ToArray();
        if (heap != IntPtr.Zero) EngineFn.OperatorDelete(heap);
        return r;
    }

    private static byte[] NulTerminated(byte[] b)
    {
        byte[] c = new byte[b.Length + 1];
        b.CopyTo(c, 0);
        return c;
    }

    /// <summary>Read a by-value <c>std::string</c> the engine returned. libstdc++: <c>{ptr@0, len@8, sso@0x10}</c>,
    /// heap iff ptr is not the inline buffer; MSVC: <c>{sso|ptr@0, len@0x10, cap@0x18}</c>, heap iff cap >= 16.
    /// <c>Heap</c> is the block for <see cref="SymbolMap.OperatorDelete"/>, which on MSVC differs from
    /// <c>Buf</c> at 4096+ bytes: the raw pointer is parked at <c>ptr[-1]</c> there.</summary>
    internal static (IntPtr Buf, uint Len, IntPtr Heap) StdString(byte* s)
    {
        if (OperatingSystem.IsWindows())
        {
            ulong cap = *(ulong*)(s + 0x18);
            if (cap < 16) return ((IntPtr)s, (uint)*(ulong*)(s + 0x10), IntPtr.Zero);
            byte* p = *(byte**)s;
            return ((IntPtr)p, (uint)*(ulong*)(s + 0x10), (IntPtr)(cap + 1 >= 4096 ? ((byte**)p)[-1] : p));
        }
        byte* q = *(byte**)s;
        return ((IntPtr)q, (uint)*(ulong*)(s + 8), q == s + 0x10 ? IntPtr.Zero : (IntPtr)q);
    }

    /// <summary>Longest CExoString <see cref="ReadExo"/> trusts; a length past this is a stale or foreign pointer.</summary>
    private const uint MaxExoLen = 4096;

    /// <summary>Decode a <c>CExoString</c> {char* buf@0; uint len@8} to a string: Latin1 keeps colour bytes intact.
    /// Copies out immediately, never holds the engine char*. Allocates, so COLD paths only. A Win64 by-value arg
    /// must be read BEFORE forwarding -- the callee destroys it.</summary>
    public static string ReadExo(IntPtr exo)
    {
        if (exo == IntPtr.Zero) return "";
        byte* buf = *(byte**)exo;
        uint len = *(uint*)(exo + 8);
        return buf == null || len == 0 || len > MaxExoLen ? "" : Encoding.Latin1.GetString(buf, (int)len);
    }

    /// <summary>Engine text read byte-per-char (<see cref="ReadExo"/>) as Unicode for DISPLAY: each 0x80..0xff char
    /// through <c>Encoding::SingleCharToUTF8(c, 0)</c> -- the same map vanilla's TTF atlas draws with (client
    /// codepage). Run it after colour parsing (tag bytes are raw); keep the byte-space string for anything sent back.</summary>
    public static string EngineText(string s)
    {
        var fn = EngineFn.EncodingSingleCharToUtf8;
        if (fn == null) return s;
        int i = 0;
        while (i < s.Length && (s[i] < 0x80 || s[i] > 0xff)) i++;
        if (i == s.Length) return s;
        char[] r = s.ToCharArray();
        for (; i < r.Length; i++)
            if (r[i] is >= (char)0x80 and <= (char)0xff) r[i] = (char)fn((byte)r[i], 0);
        return new string(r);
    }

    /// <summary>Typed or pasted text as a vanilla field holds and shows it: <c>FromUTF8</c> on the way in (what the
    /// SDL_TEXTINPUT and Ctrl+V handlers run), the atlas map on the way out -- "łódź" reads "?ód?" on cp1252.</summary>
    public static string EngineTyped(string s) => EngineText(Encoding.Latin1.GetString(EngineBytes(s)));

    /// <summary>A strref's text for DISPLAY as <c>CTlkTable::GetSimpleString</c> resolves it -- base TLK, a module's
    /// custom TLK, a server override -- or null while no TLK is loaded (pre-main, the stub host). COLD paths only.</summary>
    public static string? TlkText(uint strref)
    {
        IntPtr sym = Sym(SymbolMap.TlkTable);
        IntPtr tlk = sym == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)sym;
        if (tlk == IntPtr.Zero || EngineFn.ExoStringDtor == null) return null;
        byte* exo = stackalloc byte[32];                    // > sizeof(CExoString): slack for the sret callee
        NativeMemory.Clear(exo, 32);
        new CTlkTable(tlk).TlkGetSimpleString(exo, strref);
        string text = ReadExo((IntPtr)exo);
        EngineFn.ExoStringDtor((IntPtr)exo);
        return EngineText(text);
    }

    /// <summary>The std::string and CExoString readers against hand-built layouts -- the stub host has no engine to call.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        byte* s = stackalloc byte[32];
        var check = new Checker(log);
        byte* text = stackalloc byte[3]; text[0] = (byte)'a'; text[1] = 0xC7; text[2] = (byte)'z';
        *(byte**)s = text; *(uint*)(s + 8) = 3;
        check.Expect(ReadExo((IntPtr)s) == "a\u00C7z", "exo latin1 byte-for-byte");
        *(uint*)(s + 8) = MaxExoLen + 1;
        check.Expect(ReadExo((IntPtr)s) == "" && ReadExo(IntPtr.Zero) == "", "exo over-cap and null read empty");
        if (OperatingSystem.IsWindows())
        {
            s[0] = (byte)'a'; s[1] = (byte)'b'; *(ulong*)(s + 0x10) = 2; *(ulong*)(s + 0x18) = 15;
            var (buf, len, heap) = StdString(s);
            check.Expect(buf == (IntPtr)s && len == 2 && heap == IntPtr.Zero, "msvc sso");
            *(byte**)s = s + 8; *(ulong*)(s + 0x10) = 20; *(ulong*)(s + 0x18) = 31;
            (buf, len, heap) = StdString(s);
            check.Expect(buf == (IntPtr)(s + 8) && len == 20 && heap == buf, "msvc heap");
            byte* big = stackalloc byte[16];
            *(byte**)big = s; *(byte**)s = big + 8; *(ulong*)(s + 0x18) = 4095;
            (buf, len, heap) = StdString(s);
            check.Expect(buf == (IntPtr)(big + 8) && heap == (IntPtr)s, "msvc big block un-adjusts via ptr[-1]");
        }
        else
        {
            *(byte**)s = s + 0x10; *(ulong*)(s + 8) = 2; s[0x10] = (byte)'a'; s[0x11] = (byte)'b';
            var (buf, len, heap) = StdString(s);
            check.Expect(buf == (IntPtr)(s + 0x10) && len == 2 && heap == IntPtr.Zero, "libstdc++ sso");
            *(byte**)s = s + 8; *(ulong*)(s + 8) = 20;
            (buf, len, heap) = StdString(s);
            check.Expect(buf == (IntPtr)(s + 8) && len == 20 && heap == buf, "libstdc++ heap");
        }
        check.Expect(Encoding.Latin1.GetString(EngineBytes("abc")) == "abc", "ascii passes through");
        log.Info(check.Ok ? "Native.SelfCheck: passed" : "Native.SelfCheck: FAILED");
    }

    /// <summary>Run the engine's CExoString dtor on a string the ENGINE allocated (frees the buffer, leaves the
    /// 16-byte shell). For a Win64 by-value arg a skipped callee would otherwise leak.</summary>
    public static void DestroyExoString(IntPtr exo)
    {
        if (exo == IntPtr.Zero || EngineFn.ExoStringDtor == null) return;
        EngineFn.ExoStringDtor(exo);
        *(long*)exo = 0; *(long*)(exo + 8) = 0;   // the engine's own destroy-and-null shape; callers reuse the shell
    }

    public static void FreeExoString(IntPtr exo)
    {
        if (exo == IntPtr.Zero) return;
        if (EngineFn.ExoStringDtor != null) EngineFn.ExoStringDtor(exo);                // frees the internal buffer
        Marshal.FreeHGlobal(exo);
    }

    private static readonly IntPtr _exoBaseSym = Sym(SymbolMap.ExoBase);
    private static string? _userDir;

    /// <summary>
    /// The client's own resolved user directory -- whatever <c>SetUpUserDirectory</c> produced, so it honours
    /// <c>-userdirectory</c>, per-OS rules and the <c>.N</c> instance suffix for free. Read live from
    /// <c>g_pExoBase</c> (<see cref="EngineLayout.ExoBaseUserDir"/>), null before the engine sets that up (our
    /// loader runs pre-main) or headless. Cached on first success; the engine char* is never held.
    /// </summary>
    public static string? UserDirectory()
    {
        if (_userDir != null) return _userDir;
        if (_exoBaseSym == IntPtr.Zero) return null;
        IntPtr exoBase = *(IntPtr*)_exoBaseSym;             // g_pExoBase's value: the CExoBase*
        if (exoBase == IntPtr.Zero || !EngineLayout.KeyOk) return null;   // pre-main, or an unknown build's rows are 0
        byte* p = (byte*)new CExoBase(exoBase).UserDir;
        string s = p == null ? "" : Marshal.PtrToStringUTF8((IntPtr)p) ?? "";
        if (s.Length == 0) return null;
        _userDir = s;
        return s;
    }

    /// <summary>
    /// Absolute path for a CX config file in the client's user directory (next to nwn.ini/settings.tml).
    /// Falls back to <see cref="DefaultUserDirectory"/> when the engine dir is unresolvable -- pre-main, or
    /// headless under run-demo.
    /// </summary>
    public static string ConfigPath(string fileName) =>
        System.IO.Path.Combine(UserDirectory() ?? DefaultUserDirectory(), fileName);

    /// <summary>
    /// Where the client would put its user directory with no <c>-userdirectory</c> override: SDL's pref path,
    /// <c>$XDG_DATA_HOME</c> or <c>~/.local/share</c>, plus the app name -- read off the client's own SDL block
    /// rather than assumed, so it agrees with <see cref="UserDirectory"/>. This is the PRE-MAIN answer every boot
    /// knob is read through, and the launch line is askable there, so <c>-userdirectory</c> still wins.
    /// </summary>
    public static string DefaultUserDirectory()
    {
        string? launched = LaunchUserDirectory();
        if (launched != null) return launched;
        if (OperatingSystem.IsWindows())
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Neverwinter Nights");
        return System.IO.Path.Combine(HostEnv.DataHome, "Neverwinter Nights");
    }

    /// <summary>The <c>-userdirectory</c> the client was launched with, or null. Read from the command line, the one
    /// thing available before the engine has resolved anything: without it a client launched with the switch reads
    /// its BOOT knobs from the default directory while the engine uses the one it was given. Case-insensitive and
    /// LAST occurrence wins, as the engine's own <c>SetUpUserDirectory</c> parse is; a valueless switch is ignored.</summary>
    private static string? LaunchUserDirectory()
    {
        if (_launchDirTried) return _launchDir;
        _launchDirTried = true;
        string[] args;
        try { args = Environment.GetCommandLineArgs(); } catch { return null; }
        for (int i = args.Length - 2; i >= 0; i--)
            if (string.Equals(args[i], "-userdirectory", StringComparison.OrdinalIgnoreCase)
                && args[i + 1].Length > 0)
            { _launchDir = args[i + 1]; break; }
        return _launchDir;
    }

    /// <inheritdoc cref="LaunchUserDirectory"/>
    private static string? _launchDir;
    /// <inheritdoc cref="LaunchUserDirectory"/>
    private static bool _launchDirTried;
}
