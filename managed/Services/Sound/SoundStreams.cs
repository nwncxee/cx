using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Sound;

/// <summary>Music and ambience crossfade. The engine has one stream per channel and starts the next track only once
/// the last has faded out, so a change is fade-out, silence, fade-in; at the fade-out (<c>FadeAndStop</c>) the playing
/// Internal is swapped into a spare handle CX owns to finish its fade and the engine's handle gets the spare's idle
/// one, so its retry starts the new track at once. Every stream list is keyed by the Internal. Main thread.</summary>
internal static unsafe class SoundStreams
{
    private const int Music = 0, Bed = 1;
    /// <summary>StartFade's type 1: ramp and keep playing.</summary>
    private const int FadeRamp = 1;
    private static StreamFadeAndStopFn _fade;
    /// <summary>CX's spare handle per channel (8 bytes each, never freed), made on first use.</summary>
    private static readonly IntPtr[] _spare = new IntPtr[2];
    /// <summary>The spare holds a faded-out track's Internal, which keeps the whole file resident until reset.</summary>
    private static readonly bool[] _parked = new bool[2];

    internal static bool Install(HookService hooks)
    {
        _fade = hooks.InstallStreamFadeAndStop(&OnFadeAndStop);
        return _fade != null;
    }

    private static int Channel(IntPtr handle, CNWCAmbientSound amb)
    {
        if (amb.IsNull || amb.Truncated != 0) return -1;
        return handle == amb.Music.Ptr ? Music : handle == amb.Ambient.Ptr ? Bed : -1;
    }

    private static CNWCAmbientSound Ambience()
    {
        CNWCArea area = SoundScene.CurrentArea();
        return area.IsNull ? default : area.AmbientSound;
    }

    [UnmanagedCallersOnly]
    private static void OnFadeAndStop(IntPtr handle, uint ms)
    {
        var src = new CExoStreamingSoundSource(handle);
        int ch = ms == 0 || !SoundKnobs.Enabled.V || !SoundKnobs.Crossfade.V ? -1 : Channel(handle, Ambience());
        if (ch < 0 || SoundCore.cxa_al_live() == 0 || src.StreamIsPlaying() == 0 || !Spare(ch))
        {
            _fade(handle, ms);
            return;
        }
        var spare = new CExoStreamingSoundSource(_spare[ch]);
        if (spare.StreamIsPlaying() != 0) spare.StreamStop();
        byte vol = src.StreamGetVolume();
        (spare.Internal, src.Internal) = (src.Internal, spare.Internal);
        _fade(spare.Ptr, ms);
        _parked[ch] = true;
        src.StreamSetLooping(ch == Bed ? 1 : 0);
        src.StreamStartFade(FadeRamp, ms, 0, vol);
    }

    /// <summary>The channel's spare, constructed as the engine constructs its own (Type 0). False when the engine
    /// made no Internal (sound disabled).</summary>
    private static bool Spare(int ch)
    {
        if (_spare[ch] == IntPtr.Zero)
        {
            _spare[ch] = (IntPtr)NativeMemory.AllocZeroed(8);
            new CExoStreamingSoundSource(_spare[ch]).StreamCtor(0);
        }
        return new CExoStreamingSoundSource(_spare[ch]).Internal != IntPtr.Zero;
    }

    /// <summary>At the engine's sound teardown: every spare's Internal leaves the engine's stream lists, so none
    /// of CX's is in the AsyncSoundStream thread's list when that list is freed. Returns how many were taken out,
    /// of the spares CX ever made (<paramref name="made"/>).</summary>
    internal static int Park(out int made)
    {
        made = 0;
        int parked = 0;
        if (EngineFn.StreamStop == null) return 0;
        for (int ch = 0; ch < 2; ch++)
        {
            if (_spare[ch] == IntPtr.Zero) continue;
            made++;
            _parked[ch] = false;
            var src = new CExoStreamingSoundSource(_spare[ch]);
            if (src.Internal == IntPtr.Zero) continue;
            // Stop, not Reset: Reset would build a fresh Internal off the CExoSoundInternal being destroyed
            src.StreamStop();
            parked++;
        }
        return parked;
    }

    /// <summary>Per frame: a finished fade's Internal is reset (freeing its file), and the music level goes to the duck.</summary>
    internal static void Tick()
    {
        if (SoundCore.cxa_al_live() == 0) return;
        for (int ch = 0; ch < 2; ch++)
            if (_parked[ch] && new CExoStreamingSoundSource(_spare[ch]).StreamIsPlaying() == 0)
            {
                new CExoStreamingSoundSource(_spare[ch]).StreamReset(0);
                _parked[ch] = false;
            }
        CNWCAmbientSound amb = Ambience();
        CExoStreamingSoundSource music = amb.IsNull ? default : amb.Music;
        float level = music.IsNull || music.StreamIsPlaying() == 0 ? 0f : music.StreamGetVolume() / 127f;
        SoundCore.cxa_music(level);
    }
}
