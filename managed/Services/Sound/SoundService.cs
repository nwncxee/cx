using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using CX.Alloy;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Sound;

/// <summary>
/// The engine keeps deciding what plays; CX owns what its audio does. The pinned OpenAL Soft (libopenal.so.1 /
/// cx_openal.dll, beside the extender) is the engine's OpenAL, and every positional voice it starts is rendered by
/// Steam Audio in <c>native/cxaudio.cpp</c> -- panning, the authored radii, occlusion, reflections, pathing and the
/// playback fixes (docs/audio.md). Nothing managed runs on the audio thread; the main-thread hooks allocate nothing.
/// </summary>
public sealed unsafe class SoundService : IExtenderService
{
    private static ExtenderLog? _log;
    private static bool _armed, _described, _resamplerPushed;
    private static SoundPlayOn3DVoiceFn _play;
    private static SoundPlayOn2DVoiceFn _play2d;
    private static SoundSourceSetPositionFn _setPos;
    private static SoundSetEnvironmentFn _setEnv;
    private static SoundInitializeFn _init;
    private static int _rowInterior = -1, _rowExterior = -1;
    /// <summary>The area's EnvAudio row as authored: 0 = never set, -1 = no area yet.</summary>
    private static int _lastEnv = -1;
    private static long _statsAt;
    /// <summary>Starts that fell back to the stock path already said on screen: once, not every 10 s.</summary>
    private static bool _saidRefused;
    /// <summary>A stand-down said before any text surface existed, repeated on screen at the first frame that has one.</summary>
    private static string? _failure;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _ = SoundKnobs.G;
        SoundCore.SelfCheck(log);
        // armed whatever else stands down: the engine floods its log with contextless AL calls at exit on its own
        SoundExit.Install(log, hooks);
        FrameRegistry.Global.Register(FramePhase.PostPresent, 40, Tick, "SoundService", log);
        if (!SymbolMap.Require(log, "sound", SymbolMap.SoundPlayOn3DVoice, SymbolMap.SoundPlayOn2DVoice, SymbolMap.SoundSourceSetPosition,
                               SymbolMap.SoundSetEnvironment, SymbolMap.SoundInitialize, SymbolMap.StreamFadeAndStop, SymbolMap.StreamCtor,
                               SymbolMap.StreamReset, SymbolMap.StreamStartFade, SymbolMap.StreamIsPlaying, SymbolMap.StreamStop,
                               SymbolMap.StreamGetVolume, SymbolMap.StreamSetLooping)
            || !EngineLayout.Require(log, "sound", "CExoSoundSourceInternal", "C3DVoice", "C2DVoice", "CNWCAmbientSound", "CExoStreamingSoundSource"))
            return;
        if (!Libraries(out IntPtr openal, out IntPtr phonon, out string why))
        {
            Fail(why);
            return;
        }
        int bind = SoundCore.cxa_bind(phonon, openal);
        if (bind != 0)
        {
            Fail($"the audio libraries are not the pinned builds ({SoundCore.Error()})");
            return;
        }
        for (int i = 0; i < SoundCore.Shimmed.Length; i++)
        {
            IntPtr target = NativeLibrary.GetExport(openal, SoundCore.Shimmed[i]);
            IntPtr tramp = hooks.InstallAt(target, SoundCore.cxa_shim(i), "OpenAL " + SoundCore.Shimmed[i]);
            if (tramp == IntPtr.Zero)
            {
                // the ones already prepared commit anyway: with the originals set they pass straight through
                Fail($"could not hook {SoundCore.Shimmed[i]}");
                return;
            }
            SoundCore.cxa_set_original(i, tramp);
        }
        _play = hooks.InstallSoundPlayOn3DVoice(&OnPlay3D);
        _play2d = hooks.InstallSoundPlayOn2DVoice(&OnPlay2D);
        _setPos = hooks.InstallSoundSourceSetPosition(&OnSetPosition);
        _setEnv = hooks.InstallSoundSetEnvironment(&OnSetEnvironment);
        _init = hooks.InstallSoundInitialize(&OnInitialize);
        if (_play == null || _play2d == null || _setPos == null || _setEnv == null || _init == null || !SoundStreams.Install(hooks))
        {
            Fail("an exosound hook did not install");
            return;
        }
        fixed (byte* a = "CASTLE_MEDIUMROOM\0"u8) _rowInterior = SoundCore.cxa_preset_row(a);
        fixed (byte* b = "PLAIN\0"u8) _rowExterior = SoundCore.cxa_preset_row(b);
        _armed = true;
        Push();
        PushDevice();
        new Thread(SimLoop) { IsBackground = true, Name = "CX sound sim" }.Start();
        new Thread(BakeLoop) { IsBackground = true, Name = "CX sound bake" }.Start();
        SoundScene.Arm(log);
        log.Info("sound: armed -- positional voices render through Steam Audio 4.8.1 on OpenAL Soft 1.25.2 (docs/audio.md)");
    }

    /// <summary>Said on screen and in the log, and the whole path stands down -- the game keeps its own audio.</summary>
    private static void Fail(string why)
    {
        _failure = $"CX sound is off: {why}. The game plays its stock audio.";
        Notice.Show(_log, _failure);
        _armed = false;
    }

    /// <summary>The pinned OpenAL the game is actually bound to, and Steam Audio beside it.</summary>
    private static bool Libraries(out IntPtr openal, out IntPtr phonon, out string why)
    {
        openal = phonon = IntPtr.Zero;
        string dir;
        if (OperatingSystem.IsWindows())
        {
            int patched = cx_openal_redirect(out openal, out IntPtr w);
            if (patched <= 0)
            {
                why = $"cx_openal.dll could not take over from the game's OpenAL32.dll ({Marshal.PtrToStringAnsi(w)})";
                return false;
            }
            char* path = stackalloc char[1024];
            uint n = GetModuleFileNameW(openal, path, 1024);
            dir = Path.GetDirectoryName(new string(path, 0, (int)n)) ?? "";
            _log?.Info($"sound: {patched} OpenAL32.dll import(s) of nwmain.exe redirected to cx_openal.dll");
        }
        else
        {
            string? managed = Environment.GetEnvironmentVariable("CX_MANAGED");
            dir = managed == null ? "" : Path.GetDirectoryName(Path.GetFullPath(managed)) ?? "";
            if (!NativeLibrary.TryLoad(Path.Combine(dir, "libopenal.so.1"), out openal))
            {
                why = "libopenal.so.1 is missing beside the extender (reinstall the CX archive)";
                return false;
            }
            // ld.so hands nwmain the first libopenal.so.1 in the global scope: ours only if cx-launch preloaded it
            IntPtr engine = dlsym(IntPtr.Zero, "alcOpenDevice"u8);
            if (engine != NativeLibrary.GetExport(openal, "alcOpenDevice"))
            {
                why = "the game is bound to a system OpenAL, not CX's (start it through the current cx-launch)";
                return false;
            }
        }
        string ph = Path.Combine(dir, OperatingSystem.IsWindows() ? "phonon.dll" : "libphonon.so");
        if (!NativeLibrary.TryLoad(ph, out phonon))
        {
            why = $"Steam Audio ({Path.GetFileName(ph)}) is missing beside the extender (reinstall the CX archive)";
            return false;
        }
        why = "";
        return true;
    }

    /// <summary>The knobs, whole, to the core. Main thread (a knob's set, or an area's EnvAudio), and cheap.</summary>
    internal static void Push()
    {
        if (!_armed) return;
        SoundCore.Config c = default;
        c.Enabled = SoundKnobs.Enabled.V ? 1 : 0;
        c.Binaural = SoundKnobs.Output.Boot switch { SoundKnobs.OutHeadphones => 1, SoundKnobs.OutSpeakers => 0, _ => -1 };
        c.Occlusion = SoundKnobs.Occlusion.V;
        c.Reflections = ReverbMode(SoundKnobs.Reverb.V, _lastEnv);
        c.Pathing = SoundKnobs.Pathing.V ? 1 : 0;
        c.Rays = SoundKnobs.Rays.V;
        c.Bounces = SoundKnobs.Bounces.V;
        c.Order = SoundKnobs.Order.Boot;
        c.Duration = SoundKnobs.Duration.V;
        c.ReverbGain = MathF.Pow(10f, SoundKnobs.ReverbDb.V / 20f);
        c.AirScale = SoundKnobs.Air.V;
        c.Curve = SoundKnobs.Curve.V;
        c.Rolloff = SoundKnobs.Rolloff.V;
        c.Taper = SoundKnobs.Taper.V;
        c.PitchVariance = SoundKnobs.PitchVariance.V ? 1 : 0;
        c.Detune = SoundKnobs.Detune.V;
        c.RandomStart = SoundKnobs.RandomStart.V ? 1 : 0;
        c.DriftDb = SoundKnobs.DriftDb.V;
        c.Normalize = SoundKnobs.Normalize.V;
        Knob<float>[] cats = { SoundKnobs.Ambient, SoundKnobs.Footsteps, SoundKnobs.Combat, SoundKnobs.Spells, SoundKnobs.Voices, SoundKnobs.Other };
        for (int i = 0; i < SoundCore.Categories; i++)
        {
            c.CatGainDb[i] = cats[i].V;
            c.CatTargetDb[i] = SoundKnobs.Targets[i];
        }
        c.DupCap = SoundKnobs.DupCap.V;
        c.ShelfDb = SoundKnobs.ShelfDb.V;
        c.DuckDb = SoundKnobs.DuckDb.V;
        c.Fix3d = SoundKnobs.Fix3d.V ? 1 : 0;
        c.Compressor = SoundKnobs.Compressor.V ? 1 : 0;
        SoundCore.cxa_config(&c);
    }

    /// <summary>The master toggle: the mix, the device and the resampler all follow it.</summary>
    internal static void Master()
    {
        Push();
        PushDevice();
        PushResampler();
    }

    /// <summary>Device-level: OpenAL's own HRTF (off on speakers, on for headphones, its detection otherwise), the
    /// output limiter, and the listener gain the engine never sets.</summary>
    internal static void PushDevice()
    {
        if (!_armed) return;
        bool on = SoundKnobs.Enabled.V;
        int hrtf = !on ? -1 : SoundKnobs.Output.Boot switch { SoundKnobs.OutHeadphones => 1, SoundKnobs.OutSpeakers => 0, _ => -1 };
        int layout = on && SoundKnobs.Output.Boot != SoundKnobs.OutHeadphones ? SoundKnobs.Layout.Boot : 0;
        SoundCore.cxa_device(hrtf, !on || SoundKnobs.Limiter.V ? 1 : 0, on ? SoundKnobs.Master.V : 1f, layout);
    }

    /// <summary>The speaker test's button: ahead, right, behind, left, through the same path every sound takes.</summary>
    internal static void TestTone()
    {
        if (!_armed || SoundCore.cxa_test_tone() == 0)
            Notice.Show(_log, "CX sound: the speaker test needs CX sound on and the game's audio running");
        else
            Notice.Show(_log, "CX sound: speaker test -- ahead, right, behind, left");
    }

    /// <summary>The resampler for what OpenAL still plays itself -- needs the engine's context, so it waits for the bus.</summary>
    internal static void PushResampler()
    {
        if (!_armed || SoundCore.cxa_ready() == 0) return;
        string name = SoundKnobs.Enabled.V ? SoundKnobs.Resampler.V : "";
        byte[] z = System.Text.Encoding.ASCII.GetBytes(name + "\0");
        int r;
        fixed (byte* p = z) r = SoundCore.cxa_resampler(p);
        if (r == -2) _log?.Always($"sound: this OpenAL offers no resampler named '{name}' -- its default stays");
        _resamplerPushed = true;
    }

    [UnmanagedCallersOnly]
    private static void OnPlay3D(IntPtr sound, IntPtr source, IntPtr voice)
    {
        SoundCore.Meta m = MetaOf(new CExoSoundSourceInternal(source), 0);
        SoundCore.cxa_begin3d(&m);
        _play(sound, source, voice);
        SoundCore.cxa_end3d();
    }

    /// <summary>prioritygroups.2da's Looping_AreaWide_Ambients: the group a non-Positional placed loop plays in.</summary>
    private const byte AreaWideLoop = 2;

    /// <summary>A placed non-Positional loop becomes a positional voice at its object, if the knob says so; every
    /// other 2D start (interface, one-shots, scripted global sounds) is left to OpenAL.</summary>
    [UnmanagedCallersOnly]
    private static void OnPlay2D(IntPtr sound, IntPtr source, IntPtr voice)
    {
        var src = new CExoSoundSourceInternal(source);
        if (!SoundKnobs.PlaceLoops.V || src.Group != AreaWideLoop)
        {
            _play2d(sound, source, voice);
            return;
        }
        SoundCore.Meta m = MetaOf(src, 1);
        SoundCore.cxa_begin3d(&m);
        _play2d(sound, source, voice);
        SoundCore.cxa_end3d();
    }

    private static SoundCore.Meta MetaOf(CExoSoundSourceInternal src, int is2d)
    {
        SoundCore.Meta m;
        Vec3 p = src.Position;
        m.X = p.X; m.Y = p.Y; m.Z = p.Z;
        m.Near = src.NearDist; m.Far = src.FarDist; m.Variance = src.Variance;
        m.Group = src.Group; m.Offset = src.SampleOffset; m.Is2d = is2d;
        return m;
    }

    [UnmanagedCallersOnly]
    private static void OnSetPosition(IntPtr source, VectorArg pos)
    {
        Vec3 p = pos.Value;   // before calling on: on Win64 the callee owns (and may clobber) the copy
        var src = new CExoSoundSourceInternal(source);
        C3DVoice v3 = src.Voice3d;
        C2DVoice v2 = src.Voice2d;
        uint al = !v3.IsNull ? v3.Source : !v2.IsNull ? v2.Source : 0;
        _setPos(source, pos);
        if (al != 0) SoundCore.cxa_move(al, p.X, p.Y, p.Z);
    }

    /// <summary>The area's EnvAudio row, for the preset reverb. Row 0 is also what an area that never set the
    /// field carries (6 of 7 areas in one measured module), so 0 falls back on the interior flag.</summary>
    [UnmanagedCallersOnly]
    private static void OnSetEnvironment(IntPtr sound, byte row)
    {
        _setEnv(sound, row);
        int r = row;
        if (r == 0) r = AreaInterior() ? _rowInterior : _rowExterior;
        bool changed = (_lastEnv > 0) != (row > 0);
        _lastEnv = row;
        SoundCore.cxa_environment(r);
        if (changed) Push();
    }

    /// <summary>The reverb mode the core runs: an area whose builder set its EnvAudio gets that preset instead of
    /// the simulated room or per-sound echoes; Off and Area preset stay the player's choice.</summary>
    internal static int ReverbMode(int knob, int envRow) => envRow > 0 && (knob is 1 or 2) ? 3 : knob;

    /// <summary>The voice budget, raised before the engine sizes its voice arrays (it clamps to 8..64 itself).</summary>
    [UnmanagedCallersOnly]
    private static int OnInitialize(IntPtr sound, IntPtr provider, byte n2d, byte n3d)
    {
        if (SoundKnobs.Enabled.Boot)
        {
            n2d = (byte)SoundKnobs.Voices2d.Boot;
            n3d = (byte)SoundKnobs.Voices3d.Boot;
        }
        _log?.Info($"sound: the engine's voice budget -- {n2d} interface + {n3d} positional");
        return _init(sound, provider, n2d, n3d);
    }

    private static bool AreaInterior()
    {
        CNWCArea area = SoundScene.CurrentArea();
        return !area.IsNull && (area.Flags & 1) != 0;
    }

    /// <summary>End of frame: the late bus (the engine's sound init can beat the hooks on Windows), the one-time
    /// device line, the acoustic scene, and a stats line every 10 s.</summary>
    /// <summary>The reverb's wet scale last pushed for rain.</summary>
    private static float _rainWet = 1f;

    /// <summary>Rain soaks up echoes: the reverb drops by <see cref="SoundKnobs.RainDb"/> scaled by the engine's rain
    /// density (<see cref="Weather"/>'s hook, installed wherever Alloy can arm, drawing the rain or not).</summary>
    private static void PushRain()
    {
        float d = Weather.CurrentKind == Weather.Kind.Rain ? Weather.Density : 0f;
        float wet = MathF.Pow(10f, SoundKnobs.RainDb.V * d / 20f);
        if (MathF.Abs(wet - _rainWet) < 0.01f || SoundCore.cxa_al_live() == 0) return;
        _rainWet = wet;
        SoundCore.cxa_rain(wet);
    }

    private static void Tick()
    {
        if (_failure != null && Notice.Sink != null)
        {
            Notice.Sink(_failure);
            _failure = null;
        }
        if (!_armed) return;
        if (SoundKnobs.Enabled.V) SoundCore.cxa_attach();
        if (!_described && SoundCore.cxa_ready() != 0)
        {
            _described = true;
            PushDevice();   // the listener gain needs the engine's context, which exists from here
            string d = SoundCore.Describe(), e = SoundCore.Error();
            _log?.Always($"sound: OpenAL {d}");
            if (e.Length > 0) Notice.Show(_log, $"CX sound: {e}");
            if (!d.Contains("ALSOFT 1.25.2"))
                Notice.Show(_log, "CX sound: the game's OpenAL is not the pinned OpenAL Soft 1.25.2 -- positional audio may misbehave");
        }
        if (!_resamplerPushed) PushResampler();
        SoundScene.Tick();
        SoundStreams.Tick();
        PushRain();
        long now = Stopwatch.GetTimestamp();
        if (now - _statsAt < 10 * Stopwatch.Frequency) return;
        _statsAt = now;
        SoundCore.Stats s;
        SoundCore.cxa_stats(&s);
        if (!_saidRefused && s.SlotRefused + s.CaptureRefused > 0)
        {
            _saidRefused = true;
            Notice.Show(_log, $"CX sound: {s.SlotRefused + s.CaptureRefused} positional sound(s) played the stock way -- "
                            + (s.SlotRefused > 0 ? "more sounds at once than CX's 64 voices" : "a sound format CX does not read")
                            + " (see [sound] in cx.log)");
        }
        _log?.Info($"[sound] voices {s.Voices}, captured {s.Blobs}, refused {s.SlotRefused} slot / {s.CaptureRefused} capture; "
                 + $"capture max {s.CaptureMsMax:F2} ms; "
                 + $"render {s.RenderMsAvg:F2} ms/frame (max {s.RenderMsMax:F2}, {s.Underruns} underruns), sim {s.SimMsAvg:F2} ms, reflections {s.ReflMsAvg:F1} ms; "
                 + $"scene {s.SceneTris} tris, {s.Probes} probes (bake {s.BakeState}); bus {(s.BusOpen != 0 ? $"{s.BusRate} Hz x{s.BusChannels}" : "closed")}; "
                 + $"authored env row {_lastEnv}, reverb mode {ReverbMode(SoundKnobs.Reverb.V, _lastEnv)}");
    }

    /// <summary>Both loops end at the core's exit latch, so neither is inside Steam Audio while the process
    /// finalizes (<c>cxa_shutdown</c> waits them out).</summary>
    private static void SimLoop()
    {
        while (SoundCore.cxa_stopped() == 0)
        {
            SoundCore.cxa_sim_tick();
            Thread.Sleep(15);
        }
    }

    /// <inheritdoc cref="SimLoop"/>
    private static void BakeLoop()
    {
        while (SoundCore.cxa_stopped() == 0)
            if (SoundCore.cxa_bake_tick() == 0) Thread.Sleep(250);
    }

    [DllImport("cxboot")] private static extern int cx_openal_redirect(out IntPtr module, out IntPtr why);
    [DllImport("kernel32")] private static extern uint GetModuleFileNameW(IntPtr module, char* path, uint size);
    [DllImport("libc")] private static extern IntPtr dlsym(IntPtr handle, byte* name);

    private static IntPtr dlsym(IntPtr handle, ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return dlsym(handle, p);
    }
}
