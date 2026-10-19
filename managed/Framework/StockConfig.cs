using System;
using CX.Engine;

namespace CX.Framework;

/// <summary>
/// Read-only access to the player's STOCK settings -- <c>settings.tml</c> through the engine's own
/// <c>CExoConfig::Get&lt;T&gt;</c>, so Alloy can honour Game Options. Never writes, and never per frame (every
/// <c>Get</c> allocates engine-side): the graphics keys are one <see cref="Snapshot"/> taken at the world edges.
/// An unbound key answers the type's ZERO, so each key is paired with the type the engine binds it with.
/// </summary>
public static unsafe class StockConfig
{
    private static readonly IntPtr _exoBaseSym = Native.Sym(SymbolMap.ExoBase);

    /// <summary>Size of a <c>std::string</c> header on both ABIs (libstdc++ 8+8+16, MSVC 16+8+8).</summary>
    private const int StringHeader = 32;
    /// <summary>MSVC's inline capacity: a <c>res</c> above it says the first word is a heap pointer.</summary>
    private const ulong MsvcInlineCap = 15;
    /// <summary>Longest key this reader forges; every stock key is far shorter.</summary>
    private const int MaxKey = 96;

    /// <summary>The live <c>CExoConfig*</c>, or Zero before the engine has one that can answer: pre-main
    /// (<c>g_pExoBase</c> null), or between the config's ctor and its <c>Initialize</c>, when the working
    /// table is still null and a <c>Get(key, false)</c> would dereference it.</summary>
    private static CExoConfig Instance()
    {
        if (_exoBaseSym == IntPtr.Zero) return default;
        IntPtr exoBase = *(IntPtr*)_exoBaseSym;
        if (exoBase == IntPtr.Zero) return default;
        CExoConfig cfg = new CExoBase(exoBase).Config;
        if (cfg.IsNull) return default;
        bool initializing = cfg.Initializing != 0;
        if (!initializing && cfg.WorkingTable == IntPtr.Zero) return default;
        return cfg;
    }

    /// <summary>Write a <c>std::string</c> header for <paramref name="key"/> into <paramref name="hdr"/>
    /// (32 bytes) in the running platform's layout. <paramref name="chars"/> must hold the key bytes plus
    /// a NUL and outlive the call. Only pointer and length are ever read by the callee.</summary>
    private static void Forge(byte* hdr, byte* chars, int len)
    {
        ulong* w = (ulong*)hdr;
        if (OperatingSystem.IsWindows())
        {
            w[2] = (ulong)len;
            if (len <= (int)MsvcInlineCap)
            {
                for (int i = 0; i <= len; i++) hdr[i] = chars[i];   // inline: res stays at the inline cap
                w[3] = MsvcInlineCap;
            }
            else { w[0] = (ulong)chars; w[1] = 0; w[3] = (ulong)len; }   // res == len > 15: pointer form
            return;
        }
        w[0] = (ulong)chars; w[1] = (ulong)len; w[2] = 0; w[3] = 0;   // libstdc++: ptr, len; the buf/cap union is never read
    }

    /// <summary>Read a key bound as <c>bool</c>. False return = unreadable (engine not up, symbol
    /// unresolved), and <paramref name="v"/> is then untouched.</summary>
    public static bool TryGetBool(ReadOnlySpan<byte> key, ref bool v)
    {
        CExoConfig cfg = Instance();
        if (cfg.IsNull || EngineFn.ExoConfigGetBool == null || key.Length > MaxKey) return false;
        byte* chars = stackalloc byte[MaxKey + 1]; byte* hdr = stackalloc byte[StringHeader];
        key.CopyTo(new Span<byte>(chars, MaxKey)); chars[key.Length] = 0;
        Forge(hdr, chars, key.Length);
        v = cfg.ExoConfigGetBool(hdr, 0) != 0;
        return true;
    }

    /// <summary>Read a key bound as <c>long</c> (<c>__int64</c> on Windows -- the same instantiation).</summary>
    public static bool TryGetLong(ReadOnlySpan<byte> key, ref long v)
    {
        CExoConfig cfg = Instance();
        if (cfg.IsNull || EngineFn.ExoConfigGetLong == null || key.Length > MaxKey) return false;
        byte* chars = stackalloc byte[MaxKey + 1]; byte* hdr = stackalloc byte[StringHeader];
        key.CopyTo(new Span<byte>(chars, MaxKey)); chars[key.Length] = 0;
        Forge(hdr, chars, key.Length);
        v = cfg.ExoConfigGetLong(hdr, 0);
        return true;
    }

    /// <summary>Read a key bound as <c>double</c>.</summary>
    public static bool TryGetDouble(ReadOnlySpan<byte> key, ref double v)
    {
        CExoConfig cfg = Instance();
        if (cfg.IsNull || EngineFn.ExoConfigGetDouble == null || key.Length > MaxKey) return false;
        byte* chars = stackalloc byte[MaxKey + 1]; byte* hdr = stackalloc byte[StringHeader];
        key.CopyTo(new Span<byte>(chars, MaxKey)); chars[key.Length] = 0;
        Forge(hdr, chars, key.Length);
        v = cfg.ExoConfigGetDouble(hdr, 0);
        return true;
    }

    // ---- the graphics snapshot ------------------------------------------------------------------

    /// <summary>The stock graphics keys Alloy honours or reports, as last taken; every field keeps its stock default
    /// until a read succeeds, so a client that cannot be read gates nothing. Bound types from
    /// <c>DefineClientConfiguration</c>: <c>grass.mode</c> long {0 off, 1 fast, 2 full}, grass distance and gamma
    /// double, max lights and target fps long, and the bools -- enhanced lighting, HDR bloom, keyholing, aniso.</summary>
    public struct Snapshot
    {
        public long GrassMode;
        public double GrassDistance;
        public bool EnhancedLighting;
        public long MaxLights;
        public bool HdrBloom;
        public bool Keyholing;
        public double Gamma;
        public long TargetFps;
        public long Aniso;
        /// <summary>True once a read has succeeded against the live engine.</summary>
        public bool Taken;
    }

    private static Snapshot _snap = Defaults;
    private static IntPtr _scene;
    private static ExtenderLog? _log;

    /// <summary>The stock defaults of the four toggles, read off their bind sites in
    /// <c>DefineClientConfiguration</c> (grass 2, enhanced 1, bloom 1, keyholing 1). The numerics start at
    /// zero: nothing gates on them, and a default nobody measured is a guess.</summary>
    private static Snapshot Defaults => new()
    { GrassMode = 2, EnhancedLighting = true, HdrBloom = true, Keyholing = true };

    /// <summary>The stock "Enhanced Lighting" toggle, taken now if it never was -- the Alloy verdict asks
    /// before the first world scene reaches <see cref="OnScene"/>.</summary>
    public static bool EnhancedLighting
    {
        get { if (!_snap.Taken) Refresh("first verdict"); return _snap.EnhancedLighting; }
    }

    /// <summary>The stock "HDR / Bloom" toggle, same lazy rule.</summary>
    public static bool HdrBloom
    {
        get { if (!_snap.Taken) Refresh("first verdict"); return _snap.HdrBloom; }
    }

    /// <summary>The stock anisotropic filtering level, same lazy rule.</summary>
    public static long Aniso
    {
        get { if (!_snap.Taken) Refresh("first texture"); return _snap.Aniso; }
    }

    /// <summary>Where the snapshot lines go. Once, from the host that owns the world edge.</summary>
    public static void Attach(ExtenderLog log) => _log = log;

    /// <summary>The world edge: called once per world frame with the Scene being rendered; a new pointer
    /// is a new area (and the first is boot), which is when the snapshot is retaken. One compare otherwise.</summary>
    public static void OnScene(IntPtr scene)
    {
        if (scene == _scene) return;
        _scene = scene;
        Refresh("area edge");
    }

    /// <summary>The module edge: the next module's first Scene may reuse this one's address, and the player
    /// may have been in Game Options at the main menu in between, so the pointer latch is dropped.</summary>
    public static void ModuleUnloaded() => _scene = IntPtr.Zero;

    /// <summary>Retake every key. Cold: eight engine calls, one log line naming what the player has set --
    /// the "what are their Game Options" question a bug report used to need settings.tml for.</summary>
    public static void Refresh(string why)
    {
        Snapshot s = _snap.Taken ? _snap : Defaults;
        bool ok = TryGetLong("graphics.grass.mode"u8, ref s.GrassMode);
        if (!ok) return;
        TryGetDouble("graphics.grass.render-distance"u8, ref s.GrassDistance);
        TryGetBool("graphics.lighting.enhanced"u8, ref s.EnhancedLighting);
        TryGetLong("graphics.lighting.max-lights"u8, ref s.MaxLights);
        TryGetBool("graphics.fbo.hdr-bloom.enabled"u8, ref s.HdrBloom);
        TryGetBool("graphics.keyholing.enabled"u8, ref s.Keyholing);
        TryGetDouble("graphics.gamma"u8, ref s.Gamma);
        TryGetLong("graphics.framelimiter.foreground.target-fps"u8, ref s.TargetFps);
        TryGetLong("graphics.video.anisotropic-filtering.mode"u8, ref s.Aniso);
        s.Taken = true;
        _snap = s;
        _log?.Info($"[stock] {why}: grass {s.GrassMode} dist {s.GrassDistance:0.#} enhanced {(s.EnhancedLighting ? 1 : 0)} " +
                   $"maxlights {s.MaxLights} bloom {(s.HdrBloom ? 1 : 0)} keyhole {(s.Keyholing ? 1 : 0)} " +
                   $"gamma {s.Gamma:0.00} fps {s.TargetFps} aniso {s.Aniso}");
    }

    /// <summary>Off-device: the forged header packs the way each callee reads it. Linux: word 0 the pointer,
    /// word 1 the length. Windows: size at +0x10, res at +0x18 above 15 with the pointer at +0 for a long
    /// key, and the bytes inline with res = 15 for a short one. Asserted on the packer directly, since the
    /// platform branch it takes is the host's.</summary>
    public static bool SelfCheck(ExtenderLog log)
    {
        byte* chars = stackalloc byte[MaxKey + 1]; byte* hdr = stackalloc byte[StringHeader];
        ReadOnlySpan<byte> key = "graphics.grass.mode"u8;
        key.CopyTo(new Span<byte>(chars, MaxKey)); chars[key.Length] = 0;
        Forge(hdr, chars, key.Length);
        ulong* w = (ulong*)hdr;
        bool ok = OperatingSystem.IsWindows()
            ? w[0] == (ulong)chars && w[2] == (ulong)key.Length && w[3] > MsvcInlineCap
            : w[0] == (ulong)chars && w[1] == (ulong)key.Length;
        if (OperatingSystem.IsWindows())
        {
            ReadOnlySpan<byte> shortKey = "ui.scale"u8;
            shortKey.CopyTo(new Span<byte>(chars, MaxKey)); chars[shortKey.Length] = 0;
            Forge(hdr, chars, shortKey.Length);
            ok &= hdr[0] == (byte)'u' && hdr[shortKey.Length] == 0 && w[2] == (ulong)shortKey.Length && w[3] == MsvcInlineCap;
        }
        log.Info(ok ? "StockConfig.SelfCheck: passed" : "StockConfig.SelfCheck: FAILED -- the forged std::string header does not pack as the callee reads it");
        return ok;
    }
}
