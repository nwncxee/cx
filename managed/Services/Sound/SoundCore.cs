using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Services.Sound;

/// <summary>The native Steam Audio mixer (native/cxaudio.cpp), linked in (DirectPInvoke "cxaudio"). The
/// structs mirror the C ones field for field.</summary>
internal static unsafe class SoundCore
{
    private const string Lib = "cxaudio";

    /// <summary>The shimmed OpenAL exports, in the core's SH_* order.</summary>
    internal static readonly string[] Shimmed =
    {
        "alBufferData", "alDeleteBuffers", "alSourcef", "alSourcePlay", "alSourceStop", "alSourcePause",
        "alDeleteSources", "alListenerfv", "alcCreateContext", "alcMakeContextCurrent",
    };

    internal const int Categories = 6;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Config
    {
        public int Enabled, Binaural, Occlusion, Reflections, Pathing, Rays, Bounces, Order;
        public float Duration, ReverbGain, AirScale;
        public int Curve;
        public float Rolloff, Taper;
        public int PitchVariance;
        public float Detune;
        public int RandomStart;
        public float DriftDb, Normalize;
        public fixed float CatGainDb[Categories];
        public fixed float CatTargetDb[Categories];
        public int DupCap;
        public float ShelfDb, DuckDb;
        public int Fix3d, Compressor;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Meta
    {
        public float X, Y, Z, Near, Far, Variance;
        public int Group, Offset, Is2d;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Stats
    {
        public int Voices, Blobs, SlotRefused, CaptureRefused, BusRate, BusChannels, SceneTris, Probes, BakeState, BusOpen;
        public long Frames;
        public float RenderMsAvg, RenderMsMax, SimMsAvg, ReflMsAvg, CaptureMsMax;
        public int Underruns;
    }

    [DllImport(Lib)] internal static extern int cxa_bind(IntPtr phonon, IntPtr openal);
    [DllImport(Lib)] internal static extern IntPtr cxa_shim(int which);
    [DllImport(Lib)] internal static extern void cxa_set_original(int which, IntPtr fn);
    [DllImport(Lib)] internal static extern void cxa_config(Config* c);
    [DllImport(Lib)] internal static extern void cxa_device(int hrtf, int limiter, float master, int channels);
    [DllImport(Lib)] internal static extern int cxa_test_tone();
    [DllImport(Lib)] internal static extern void cxa_music(float level);
    [DllImport(Lib)] internal static extern void cxa_rain(float wet);
    [DllImport(Lib)] internal static extern int cxa_al_live();
    [DllImport(Lib)] internal static extern int cxa_stopped();
    [DllImport(Lib)] internal static extern int cxa_shutdown();
    [DllImport(Lib)] internal static extern int cxa_resampler(byte* name);
    [DllImport(Lib)] internal static extern void cxa_attach();
    [DllImport(Lib)] internal static extern void cxa_begin3d(Meta* m);
    [DllImport(Lib)] internal static extern void cxa_end3d();
    [DllImport(Lib)] internal static extern void cxa_move(uint src, float x, float y, float z);
    [DllImport(Lib)] internal static extern void cxa_environment(int row);
    [DllImport(Lib)] internal static extern int cxa_preset_row(byte* label);
    [DllImport(Lib)] internal static extern int cxa_scene(float* verts, int nv, int* tris, int nt, int bake, int material);
    [DllImport(Lib)] internal static extern void cxa_sim_tick();
    [DllImport(Lib)] internal static extern int cxa_bake_tick();
    [DllImport(Lib)] internal static extern void cxa_stats(Stats* s);
    [DllImport(Lib)] internal static extern int cxa_error(byte* buf, int len);
    [DllImport(Lib)] internal static extern int cxa_describe(byte* buf, int len);
    [DllImport(Lib)] internal static extern int cxa_ready();
    [DllImport(Lib)] internal static extern int cxa_selfcheck(byte* msg, int len);

    /// <summary>The core's last error, or "".</summary>
    internal static string Error()
    {
        byte* b = stackalloc byte[256];
        int n = cxa_error(b, 256);
        return n <= 0 ? "" : new string((sbyte*)b, 0, n);
    }

    /// <summary>What OpenAL says it is, once the bus is open: device, version, output mode.</summary>
    internal static string Describe()
    {
        byte* b = stackalloc byte[512];
        int n = cxa_describe(b, 512);
        return n <= 0 ? "" : new string((sbyte*)b, 0, n);
    }

    /// <summary>The core's pure pieces (distance curve, the engine's variance draw, axes and the listener frame,
    /// resampler, shelf, capture, the EnvAudio join, the reverb mode an authored EnvAudio picks) -- headless, no
    /// library loaded. Oracle: hand-written mode numbers.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        byte* b = stackalloc byte[256];
        int ok = cxa_selfcheck(b, 256);
        string msg = new string((sbyte*)b);
        var check = new Checker(log);
        check.Expect(SoundService.ReverbMode(1, 11) == 3 && SoundService.ReverbMode(2, 11) == 3, "an authored EnvAudio overrides the simulated reverb");
        check.Expect(SoundService.ReverbMode(1, 0) == 1 && SoundService.ReverbMode(2, 0) == 2, "an unset EnvAudio keeps the simulation");
        check.Expect(SoundService.ReverbMode(0, 11) == 0 && SoundService.ReverbMode(3, 0) == 3, "Off and Area preset are the player's");
        if (!check.Ok) ok = 0;
        log.Info(ok != 0 ? "SoundCore.SelfCheck: passed" : $"SoundCore.SelfCheck: FAILED -- {msg}");
    }
}
