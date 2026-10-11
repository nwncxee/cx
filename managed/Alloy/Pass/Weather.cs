using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Alloy weather: the client's weather state, captured at the engine's own entry points, and the
/// vanilla emitter silenced at its source -- with the master on, the <c>CNWCArea::SetWeather(type, density)</c>
/// hook forwards density 0 (the engine's own "no weather" path) and keeps the real value here, and off it
/// forwards untouched. Rain vs snow is the weathertypes.2da row's Model, read as two text knobs.</summary>
internal static unsafe partial class Weather
{
    internal enum Kind : byte { None, Rain, Snow }

    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled;
    internal static readonly Knob<float> Puddles;
    internal static readonly Knob<string> RainRows, SnowRows, SnowSkip;
    /// <summary>Chained, not installed: the model prefetch is the other consumer of this symbol. The
    /// priority only fixes the order the two read it in; both forward.</summary>
    private const int TileSetPriority = 100;
    private static TileSetGetFn* _tileSetSlot;
    private static string _tileset = "";
    /// <summary>The current area's tileset is on the no-cover list: its tiles carry their own snow.</summary>
    internal static bool TilesetSkipsSnow;

    private static ExtenderLog? _log;
    private static AreaSetWeatherFn _origSet;
    private static AreaStartWeatherFn _origStart;
    private static AreaStopWeatherFn _origStop;
    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);
    private static bool _off;
    /// <summary>Row -> kind, 32 rows: weathertypes.2da has 3 stock rows; content adds more (11 measured).</summary>
    private static readonly Kind[] _kinds = new Kind[32];

    /// <summary>The engine's weather as of the last SetWeather: the 2DA row, its kind, and density 0..1.
    /// Type is the last NON-ZERO row: the down ramp passes the engine's current type, which is the 0 the
    /// silence left behind.</summary>
    internal static byte Type;
    internal static Kind CurrentKind;
    internal static float Density;
    /// <summary>The area the state belongs to; a SetWeather from another area resets it.</summary>
    private static IntPtr _area;
    private static int _sets;
    /// <summary>The density the log last printed, so the ramp logs per 10% step rather than per tick.</summary>
    private static float _logged;

    static Weather()
    {
        G = KnobGroup.Declare("weather", "Graphics", "World", master: "Weather",
            masterHelp: "Rain and snow drawn by CX: wind-leaning streaks and flakes that stop under cover, and ground "
                      + "that wets, puddles and snows over. Off, the game's own rain and snow sheets fall instead.",
            masterChanged: MasterChanged, order: 342, title: "Weather");
        Enabled = G.Master!.Ladder(Quality.Preset, Quality.Classic(false, true));
        G.Status = Describe;
        DeclareKnobs();
        DeclareCoverKnobs();
        DeclareShelterKnobs();
        DeclareSurfaceKnobs();
        G.Section("Surfaces");
        Puddles = G.Slider("puddles", "Puddles", 0f, 1f, 0.05f, 0.5f, tier: Tier.Advanced,
                           help: "How much of level ground pools in the rain: low is a few small pools in the dips, high is broad sheets. "
                               + "A server may set it for its own areas.");
        DeclareSplashKnobs();
        DeclareTrackKnobs();
        DeclareWetStepKnob();
        DeclareMapKnobs();
        G.Section("");
        RainRows = G.Text("rainrows", "Rain rows", "1,3,6,9", Tier.ChatOnly,
                          help: "weathertypes.2da rows drawn as rain (stock: 1).", changed: ParseRows);
        SnowRows = G.Text("snowrows", "Snow rows", "2,4,5,10", Tier.ChatOnly,
                          help: "weathertypes.2da rows drawn as snow (stock: 2).", changed: ParseRows);
        SnowSkip = G.Text("snowskip", "Tilesets without snow cover", "", Tier.ChatOnly,
                          help: "Comma-separated tileset resrefs whose own textures already carry snow: their tiles take no cover (objects and ground still do). Empty = none.",
                          changed: RecheckTileset);
        ParseRows();
    }

    private static void RecheckTileset()
    {
        bool skip = false;
        foreach (string s in SnowSkip.V.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (s.Equals(_tileset, StringComparison.OrdinalIgnoreCase)) { skip = true; break; }
        if (skip != TilesetSkipsSnow) _log?.Info($"Weather: tileset {_tileset} {(skip ? "carries its own snow -- no cover on its tiles" : "takes snow cover")}");
        TilesetSkipsSnow = skip;
    }

    /// <summary>The area load's ask for its tileset, by name. Read BEFORE forwarding (the copy may not
    /// outlive the call); cold, so the string is fine.</summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnGetTileSet(IntPtr mgr, IntPtr resref)
    {
        int n = 0; byte* p = (byte*)resref;
        while (n < 16 && p[n] != 0) n++;
        _tileset = System.Text.Encoding.ASCII.GetString(p, n);
        RecheckTileset();
        return _tileSetSlot != null ? (*_tileSetSlot)(mgr, resref) : IntPtr.Zero;
    }

    private static void ParseRows()
    {
        Array.Clear(_kinds);
        Fill(RainRows.V, Kind.Rain);
        Fill(SnowRows.V, Kind.Snow);
        static void Fill(string rows, Kind k)
        {
            foreach (string t in rows.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(t, out int r) && r > 0 && r < 32) _kinds[r] = k;
        }
    }

    internal static Kind KindOf(byte type) => type < _kinds.Length ? _kinds[type] : Kind.None;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!SymbolMap.Require(log, "Weather", SymbolMap.AreaSetWeather, SymbolMap.AppManager, SymbolMap.GetModule))
        { _off = true; return; }
        _origSet = hooks.InstallAreaSetWeather(&OnSetWeather);
        if (_origSet == null) { _off = true; log.Info("Weather: SetWeather did not hook -- weather stays vanilla"); return; }
        _origStart = hooks.InstallAreaStartWeather(&OnStartWeather);
        _origStop = hooks.InstallAreaStopWeather(&OnStopWeather);
        if (SymbolMap.Require(log, "Weather", SymbolMap.TileSetGet))
            _tileSetSlot = hooks.ChainTileSetGet(&OnGetTileSet, TileSetPriority);
        InstallDraw(log);
        ArmWetStep(log);
        log.Info($"Weather: armed and {(Enabled.V ? "ON" : "IDLE")} -- rain rows {RainRows.V}, snow rows {SnowRows.V}");
    }

    /// <summary>Silence = the master is on, Alloy can still draw, and the row is one Alloy draws; anything else
    /// (fog, a dry storm, an unknown row, a device Alloy ruled out) keeps its stock emitter, so nothing is lost.</summary>
    private static bool Silence(Kind k) => !_off && Enabled.V && k != Kind.None && AlloyGate.Capable;

    /// <summary>LoadArea fires one of the two edges for every area, so a new <c>this</c> here is the area
    /// transition: the old area's weather and its soak go with it.</summary>
    private static void Enter(IntPtr area)
    {
        if (area == _area) return;
        _area = area; Type = 0; Density = 0f; CurrentKind = Kind.None;
        ResetSurface();
    }

    [UnmanagedCallersOnly]
    private static int OnSetWeather(IntPtr area, byte type, float density)
    {
        if (_origSet == null) return 0;
        Enter(area);
        if (type != 0) Type = type;
        else if (density <= 0f) Type = 0;
        float d = Math.Clamp(density, 0f, 100f) / 100f;
        Kind k = KindOf(Type);
        // The stop edge is density reaching 0 from above; the ramp's first tick is a 0 too, and is not one.
        bool stop = d <= 0f && Density > 0f;
        if (stop || k != CurrentKind || MathF.Abs(d - _logged) >= 0.1f)
        {
            _log?.Info($"Weather: {(stop ? "STOP " : "")}row {Type} ({k}) density {d * 100f:F0}%{(Silence(k) ? " -- stock emitter silenced" : "")}");
            _logged = d;
        }
        Density = d; CurrentKind = k; _sets++;
        return _origSet(area, type, Silence(k) ? 0f : density);
    }

    [UnmanagedCallersOnly]
    private static void OnStartWeather(IntPtr area, byte type)
    {
        Enter(area);
        _log?.Info($"Weather: START row {type} ({KindOf(type)}) -- ramping up over 6 s");
        if (_origStart != null) _origStart(area, type);
    }

    [UnmanagedCallersOnly]
    private static void OnStopWeather(IntPtr area, byte unused)
    {
        Enter(area);
        _log?.Info($"Weather: STOP requested -- ramping down over 6 s");
        if (_origStop != null) _origStop(area, unused);
    }

    /// <summary>A live master flip re-issues the engine's own SetWeather to the current area with the captured
    /// state, so off brings the stock emitter back mid-storm and on takes it away, in the same area.</summary>
    private static void MasterChanged()
    {
        if (_off || _origSet == null || Type == 0 || Density <= 0f) return;
        IntPtr mgr = _appMgr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)_appMgr;
        IntPtr app = mgr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)mgr;
        CNWCModule mod = app == IntPtr.Zero ? default : new CClientExoApp(app).GetModule();
        IntPtr area = mod.IsNull ? IntPtr.Zero : mod.CurrentArea.Ptr;
        if (area == IntPtr.Zero || area != _area) return;
        // Through the hook, so the capture and the silence decision are the one code path.
        AreaSetWeatherFn set = &OnSetWeather;
        set(area, Type, Density * 100f);
    }

    internal static string Describe() => _off ? "UNAVAILABLE"
        : $"{(Enabled.V ? "on" : "off")} row {Type} {CurrentKind} density {Density * 100f:F0}% ({_sets} sets){DescribeDraw()}{SurfaceStats()}";
}
