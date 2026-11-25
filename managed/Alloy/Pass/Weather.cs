using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
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
    /// <summary>The next layer of the <c>CNWCArea::LoadArea</c> chain: the area edge.</summary>
    private static AreaLoadAreaFn* _loadSlot;
    /// <summary>The order Weather reads the area load in among the other chains on it; it only forwards.</summary>
    private const int LoadAreaPriority = 30;
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
    /// <summary>The share of one whole rain the far veil in the fog froxels carries: the area's density times the
    /// density dial, halved for snow as the lattice's own instance count is, and 0 wherever Alloy draws nothing.</summary>
    internal static float VeilShare => _off || _failed || !Enabled.V || FrameInputs.Interior
                                    || CurrentKind == Kind.None || Density <= 0f
        ? 0f : Math.Clamp(Density * DensityScale.V * (CurrentKind == Kind.Snow ? 0.5f : 1f), 0f, 1f);

    /// <summary>The area object the state belongs to, as of the last area load.</summary>
    private static IntPtr _area;
    private static int _sets;
    /// <summary>The density the log last printed, so the ramp logs per 10% step rather than per tick.</summary>
    private static float _logged;

    static Weather()
    {
        G = KnobGroup.Declare("weather", "Graphics", "World", master: "Weather",
            masterHelp: "Rain and snow drawn by CX: wind-leaning drops and flakes that stop under cover, and ground "
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
        _loadSlot = hooks.ChainAreaLoadArea(&OnLoadArea, LoadAreaPriority);
        if (_loadSlot == null) log.Always("Weather: CNWCArea::LoadArea did not chain -- the last area's snow, soak and prints follow the player into the next area until they melt");
        if (SymbolMap.Require(log, "Weather", SymbolMap.TileSetGet))
            _tileSetSlot = hooks.ChainTileSetGet(&OnGetTileSet, TileSetPriority);
        InstallDraw(log);
        ArmWetStep(log);
        log.Info($"Weather: armed and {(Enabled.V ? "ON" : "IDLE")} -- rain rows {RainRows.V}, snow rows {SnowRows.V}");
    }

    /// <summary>Silence = the master is on, Alloy can still draw, and the row is one Alloy draws; anything else
    /// (fog, a dry storm, an unknown row, a device Alloy ruled out) keeps its stock emitter, so nothing is lost.</summary>
    private static bool Silence(Kind k) => !_off && Enabled.V && k != Kind.None && AlloyGate.Capable;

    /// <summary>The area edge. The module keeps one area object and loads every area into it, so the edge is
    /// the load itself, never a new pointer; the new area's own weather edge runs inside the load, and the
    /// arrival clock starts once the load returns, however long it took.</summary>
    [UnmanagedCallersOnly]
    private static int OnLoadArea(IntPtr area, IntPtr camera, IntPtr loadscreen)
    {
        _area = area; Type = 0; Density = 0f; CurrentKind = Kind.None;
        ResetSurface();
        int ok = _loadSlot != null ? (*_loadSlot)(area, camera, loadscreen) : 0;
        _enterTicks = Stopwatch.GetTimestamp();
        return ok;
    }

    [UnmanagedCallersOnly]
    private static int OnSetWeather(IntPtr area, byte type, float density)
    {
        if (_origSet == null) return 0;
        if (_loadSlot == null) _area = area;
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

    /// <summary>A walk from a snowy area into a clear one, on the one area object the module reloads: the load
    /// leaves no snow, soak or print behind, and the clear area's stop ramp, which passes row 0 at full density,
    /// grows none. Oracle: hand-written zeros and the kind None.</summary>
    private static void AreaEdgeSelfCheck(Checker check)
    {
        IntPtr area = _area; byte type = Type; float dens = Density; Kind kind = CurrentKind;
        AreaLoadAreaFn* slot = _loadSlot; AreaSetWeatherFn set = _origSet; TrackTexel[]? track = _track;
        AreaLoadAreaFn next = &StubLoad;
        _loadSlot = &next; _origSet = &StubSet;
        AreaLoadAreaFn load = &OnLoadArea; AreaSetWeatherFn weather = &OnSetWeather;
        IntPtr one = (IntPtr)0x1000;   // not-an-offset: a stand-in area pointer the stubs never read through
        Vector2 at = new(5.03f, 7.01f), h = Vector2.UnitX;
        int lo = int.MaxValue, hi = int.MinValue;
        _area = one; weather(one, 2, 100f); _snow = 0.8f; _wet = 0.2f; _puddle = 0f;
        _track = new TrackTexel[TrackSize * TrackSize];
        Print(_track, at, h, 10f, 0.01f, at, ref lo, ref hi, in HumanSole);
        check.Expect(TrackAt(_track, at + h * ToeAt, 10f, 0.01f) > 0.5f, "the pressed print shows before the area load");
        int got = load(one, IntPtr.Zero, IntPtr.Zero);
        check.Expect(got == 7, $"the area load reaches the next layer and returns its answer ({got})");
        check.Expect(_snow == 0f && _wet == 0f && _puddle == 0f && TrackAt(_track, at + h * ToeAt, 10f, 0.01f) == 0f,
          $"loading the next area into the same area object leaves no snow, soak or print (snow {_snow:F2}, wet {_wet:F2})");
        weather(one, 0, 100f);
        check.Expect(CurrentKind == Kind.None && Type == 0, $"the clear area's stop ramp is no weather, not the last area's snow ({CurrentKind}, row {Type})");
        weather(one, 1, 100f); _wet = 0.9f; _puddle = 0.6f;
        load(one, IntPtr.Zero, IntPtr.Zero);
        weather(one, 0, 100f);
        check.Expect(_wet == 0f && _puddle == 0f && CurrentKind == Kind.None, $"a rainy area's soak and puddles stay behind it too (wet {_wet:F2}, puddle {_puddle:F2})");
        bool en = Enabled.V, arrive = Arrive.V; SurfaceTickState tick = _surfTick;
        Enabled.V = true; Arrive.V = true;
        foreach (bool slow in new[] { false, true })
        {
            next = slow ? &StubSlowLoad : &StubLoad; _snow = 0.4f; _surfTick = new SurfaceTickState();
            load(one, IntPtr.Zero, IntPtr.Zero); weather(one, 2, 5f);
            _surfTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 10; TickSurface(1);
            check.Expect(_snow == 1f && _wet == 0f,
              $"a snowy area loaded after another arrives settled, {(slow ? "after a minute-long load" : "after a quick load")} (snow {_snow:F2})");
        }
        IntPtr two = (IntPtr)0x2000;   // not-an-offset: a second stand-in area pointer
        load(two, IntPtr.Zero, IntPtr.Zero);
        check.Expect(_area == two, "the area load names the area a live master flip re-issues to");
        bool mapArrive = _mapArrive; int mapTicks = _mapTicksDone; var none = new Dictionary<string, int>();
        next = &StubLoad;
        load(one, IntPtr.Zero, IntPtr.Zero); weather(one, 1, 100f); WriteIntegrateVk(none, 0.1f, false);
        check.Expect(_mapArrive, "the wetness map waits for the arrival decision before it soaks in one step");
        _surfTick = new SurfaceTickState(); _surfTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 10; TickSurface(1);
        WriteIntegrateVk(none, 0.1f, false);
        check.Expect(!_mapArrive, "rain already falling at arrival soaks the wetness map in one step");
        load(one, IntPtr.Zero, IntPtr.Zero); weather(one, 0, 0f); _enterTicks -= Stopwatch.Frequency * 60; weather(one, 1, 100f);
        _surfTick = new SurfaceTickState(); _surfTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 10; TickSurface(1);
        check.Expect(!_mapArrive, "rain that starts a minute after arrival does not soak the wetness map in one step");
        load(one, IntPtr.Zero, IntPtr.Zero); weather(one, 2, 100f);
        _surfTick = new SurfaceTickState(); _surfTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 10; TickSurface(1);
        check.Expect(!_mapArrive, "arriving in snow spends the arrival, so later rain does not soak the wetness map in one step");
        _mapArrive = mapArrive; _mapTicksDone = mapTicks;
        _loadSlot = null; _area = IntPtr.Zero; weather(one, 0, 0f);
        check.Expect(_area == one, "with no area-load chain, the weather still names its area, so a live master flip finds it");
        Enabled.V = en; Arrive.V = arrive; _surfTick = tick; _surfTicks = 0;
        _loadSlot = slot; _origSet = set; _track = track;
        _area = area; Type = type; Density = dens; CurrentKind = kind; _wet = _snow = _puddle = 0f; _enterTicks = 0;
    }

    [UnmanagedCallersOnly] private static int StubLoad(IntPtr area, IntPtr camera, IntPtr loadscreen) => 7;
    /// <summary>A load that takes a minute: the arrival clock the reset started is a minute old when it returns.</summary>
    [UnmanagedCallersOnly]
    private static int StubSlowLoad(IntPtr area, IntPtr camera, IntPtr loadscreen)
    {
        _enterTicks -= Stopwatch.Frequency * 60;
        return 7;
    }
    [UnmanagedCallersOnly] private static int StubSet(IntPtr area, byte type, float density) => 1;

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
