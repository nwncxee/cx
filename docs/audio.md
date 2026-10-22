# Audio (CX-57)

The engine keeps deciding **what** plays; CX owns what its audio **does**. No audio file is touched:
stock and module `.wav`/`.bmu`/2DA stay byte-identical, and a player without CX hears what they always did.
Research behind every engine claim here: the research on CX-135. Packaging and licensing:
ADR [0027](adr/0027-audio-runtime-ships-beside-the-extender.md).

## The path

```
engine (main thread)                         CX
CExoSoundInternal::PlaySourceOn3DVoice  ──►  SoundService.OnPlay3D: near/far/variance/group/position -> cxa_begin3d
   alBufferData  ──────────────────────────► shim: PCM captured (mono float, gated loudness, content key)
   alSourcePlay  ──────────────────────────► shim: a voice slot; the AL source is muted (gain 0) and pitched
                                              like the voice, and keeps playing so every state/offset query the
                                              engine makes is OpenAL's own answer
CExoSoundSourceInternal::SetPosition  ────►  cxa_move (the stored vector: the engine forwards z negated)
alSourcef(AL_GAIN) / Stop / Pause / Delete ► shims keep the slot in step
alListenerfv  ─────────────────────────────► the listener
                                             │
OpenAL mixer thread ── bus callback: copies from a ring, nothing else (no lock, no Steam Audio)
"CX sound render" ── keeps the ring 3 frames (~32 ms) ahead (native/cxaudio.cpp, nothing allocated):
   per voice: resample + pitch -> gain (engine gain, category, loudness, duck, duplicates, drift) -> shelf/drift
   filters -> Steam Audio direct effect (distance curve, air, occlusion, transmission) -> HRTF or panning;
   reflections (per-source IR into the mixer, or a send to the listener-centred room IR); pathing where occluded
   -> one ambisonic decode for the reflected field -> bus compressor -> one AL_SOFT_callback_buffer source
"CX sound sim" thread ── Steam Audio simulator: occlusion/transmission every tick, reflections every 250 ms
                          (only while something plays), pathing every 500 ms, against the scene below; every trace
                          starts 1 m above the sound's position, which the engine puts at its object's feet
"CX sound bake" thread ─ probes on a 4 m floor grid + path bake, per area, in the background
render thread (frame) ── SoundScene: Alloy's static tiles + Static placeables -> the Steam Audio scene, once the
                          area's static set has held still for a second
```

An area change swaps that scene, and the IRs the render convolves are the **simulator's own memory**
(`iplSourceGetOutputs`), which `iplSimulatorCommit` reallocates. So every commit -- the scene swap and the probe
swap -- goes through the handoff in `refl_quiesce`/`refl_thaw`: the freeze and the cleared IR params are published
under the same lock the render snapshots its slots with, the swap then waits out the frame that may already be
inside `iplReflectionEffectApply`, and the reflection effects and the mixer are reset before reflections resume.
Without it the render went on convolving the released IRs until the new area published new ones -- the
intermittent libphonon SIGSEGV on area transitions (CX-57, 2026-10-06). It is not a renderer bug: the area
edge sends its empty scene whoever drew the frame.

Everything 2D the engine plays -- interface, music, ambience streams, dialog, movies -- stays OpenAL's,
except a **placed non-Positional loop** (group 2, `Looping_AreaWide_Ambients`): the engine never range-culls a
2D source, so such a loop plays area-wide at one volume. `sound.placeloops` renders it at its object with the
builder's min/max distances instead (the anti-scry case in the CX-57 comments). It is **off by default**: area-wide rain is commonly exactly such a
loop on purpose (non-positional, volume 127, the toolset's default 1/10 m), and on it faded out 10 m from the
object -- nothing in the data tells the two apart.

Rain soaks up echoes: while the engine's weather is a rain row, the reverb (room, per-sound and area preset alike)
drops by `sound.rainreverb` (-9 dB) scaled by the rain's density, so it follows the 6 s ramps. The density is
Alloy's `Weather` hook, which is installed wherever Alloy can arm; without it the reverb stays dry-weather.

Footsteps on contact (`anim.footsteps`, `managed/Services/Anim/Footfall.cs`): the engine keys walk/run
footsteps to the model's `snd_Footstep` animation events, and a clip that carries them keeps them. A walk or run
clip with none, on a body the stride warp (`anim.stride`, Alloy only) plants, calls the engine's own `PlayFootstep`
on each frame a foot lands by the clip's own contacts; two steps closer than 0.12 s are one. The player's own footsteps
(`prioritygroups.2da` row 17, `Player_Footsteps`) take half the HRTF spatial blend and half the room send: straight
below the ears, full HRTF coloured them and placed them "somewhere else", and the cave tail swamped them.

## Music and ambience crossfade

The engine has one stream per channel (`CNWCAmbientSound`, `CNWCArea+0x2d8`: music `+0x188`, ambient bed
`+0x190`) and starts a new track only once the old one has faded out, so every change was fade-out, silence,
fade-in. `CExoStreamingSoundSource::FadeAndStop` (name-exported on both OSes, main thread) is every track change's
fade-out. `SoundStreams` swaps the playing Internal into a spare handle CX owns, where it finishes the fade, and
hands the engine's handle the spare's idle Internal: the engine's own retry then starts the new track at once, over a
fade-in CX arms with `StartFade`. The engine keys every stream list by the Internal, so the swap is two pointer
writes; a finished spare is `Reset` to free the file it holds. The low-sound "truncated" mode (one stream for both)
is left alone.

## The pinned OpenAL

OpenAL Soft 1.25.2 on both OSes (`tools/deps.json`). Linux: `cx-launch` preloads `libopenal.so.1`, which
satisfies the client's `NEEDED`. Windows: `version.dll`'s attach redirects `nwmain.exe`'s `OpenAL32.dll`
imports to `cx_openal.dll` (`native/bootstrap_win.c`). `SoundService` checks the engine really is bound to it
and says so on screen if not (CX-45). The first bus logs the device:
`sound: OpenAL device '...', AL_VERSION '1.1 ALSOFT 1.25.2', ...` -- the step-1 acceptance line.

The engine's `alcCreateContext(dev, NULL)` gets CX's attributes instead (HRTF per `sound.output`, the output
limiter); `sound.master` is the listener gain the engine never sets; `sound.resampler` picks OpenAL's resampler
for what it still plays.

## Exit

Quitting is the one moment the engine tears its audio down while threads are still inside it.
`CClientExoAppInternal::StopServices` deletes `g_pExoSound`; that destructor runs
`CExoSoundInternal::ShutDown` (name-exported on both OSes), which frees every voice and then calls
`alcDestroyContext`. `main` then returns rather than calling `exit`, so the `atexit` phase comes afterwards --
and nothing in that path stops `g_ExoSoundStreamThread`, a **detached** thread that loops `AsyncUpdate` every
10 ms. Its stop flag is written only by its own `atexit` destructor, which then frees the mutex, the condvar and
the very stream list the thread is walking, without joining it. In between, every `AsyncUpdate` makes AL calls
with no current context, and the engine's crash reporter drains its log ring without its mutex while that thread
floods it -- a wedged `nwmain` at quit.

So CX hooks `ShutDown` and, before calling the original, does three things. Its two crossfade spares leave the
engine's stream lists (`CExoStreamingSoundSourceInternal::Stop`, which removes an Internal from both the stream
thread's list and the sound's; `Reset` would instead build a fresh Internal off the object being destroyed).
The stream thread's stop flag (`EngineLayout.StreamThreadStop`: +0x70 on Linux, +0x18 on Windows -- the class is
laid out per OS) is set to 1, and the main thread sleeps 20 ms, two of that thread's cycles, which is all a
detached thread with no join handle offers. And CX's own audio latches off through `cxa_shutdown`: it cancels a
running path bake, wakes the render thread and waits, bounded, for the render, sim and bake threads to leave the
core. Past that latch every `cxa_*` entry point is a no-op, so nothing of CX's is inside Steam Audio or OpenAL
while the process finalizes. One line records it, whatever the log level:
`sound: engine sound teardown -- AL live=..., stream-thread stop flag=..., CX spares parked=.../...`.

## Knobs

`/settings` -> Audio -> Sound, or `/knobs`. Every row is one declaration in
`managed/Services/Sound/SoundKnobs.cs`. Master off: every sound plays as in the stock game (live -- voices CX
owned are handed back to OpenAL at the engine's gain), and the feature stops rather than only going quiet --
no Steam Audio work on either thread, the acoustic scene torn down and its gather stood down, sent again when
the toggle comes back (ADR 0025). The threads themselves stay parked: the render keeps the ring filled with
silence.

## Verified, and not

Headless (`native/cxaudio.cpp`'s SelfCheck in `run-demo.sh`): the distance curves, the engine's variance draw,
the NWN -> Steam Audio axes and the listener frame, the resampler, the shelf, capture and loudness, the EnvAudio
join by label, and the exit latch (after `cxa_shutdown` the entry points that would change state do nothing,
and the same calls land once it is cleared). `SoundCore.SelfCheck` adds, managed, the reverb mode an authored
`EnvAudio` picks.

Native harness (Steam Audio + the pinned OpenAL rendering to OpenAL Soft's wave backend, a 20 x 20 x 6 m room
with a wall, broadband noise at 3-8 m, 2026-09-27): HRTF gives 12 dB interaural difference at 90 degrees and is
symmetric left/right; panning is hard left/right; the source behind the wall drops from -26 dB (distance only)
to silence with occlusion alone and -51 dB with generic-material transmission; pathing adds ~4 dB around the
wall; the room reverb dominates behind the wall. Render cost 0.3 ms per 10.7 ms frame for one voice, reflection
simulation ~3 ms per run. The funchook path over the pinned OpenAL's exports was driven the way `nwmain` drives
it: the engine's source muted, its volume changes intercepted, a 2D source passing through untouched.

**Not yet heard in the game.** The acceptance is by ear, on both OSes: the anti-scry A/B from the CX-57
comments (next to the object, 10 m away, behind a wall), then each feature's knob A/B.

## Open

- Ducking follows combat, positional speech (prioritygroups' chat rows) and the area music's set volume. Creature
  VO is a one-shot, not a stream: the engine builds every stream as Type 0, so there is no dialog stream to watch.
- The ambient bed's fade-in may be cut short: `PlayAmbientSound(1)` sets the stream's volume just before it plays
  (unverified whether that cancels the armed fade). Its fade-out always overlaps.
- The two reserved 2D voices stay reserved: releasing them patches `GetFree2DVoice`'s bound, and raising the
  budget (`sound.voices2d`) buys more for nothing (research C4).
- The builder's soundscape wins: an area whose `EnvAudio` is set (any row but 0) plays that `soundeax.2da` preset
  under Room and Every sound too, in place of Steam Audio's simulated echoes -- a carpeted 1x1 room must not ring
  like the marble hall its one generic material simulates. Occlusion, transmission and pathing stay simulated;
  the preset is one wet send on the mixed bus, so it loses the simulation's per-voice distance-scaled send and the
  player's halved footstep send. A hook that lands after the area's `StartSounds` (Windows' late init) misses the
  row until the next area.
- An area whose `EnvAudio` is row 0 (GENERIC) keeps the simulation, and Area preset gives it the interior/exterior
  fallback: an area that never set the field reads 0 too, so a builder cannot pick GENERIC on purpose.
- One acoustic material per area, from its ARE flags as the engine's own `GetIsAreaNatural`/`GetIsAreaAboveGround`
  read them: natural and enclosed = **cave rock** (absorption 0.03/0.06/0.15, scattering 0.7 -- 0.05 in the highs rang metallic; the tail 2x, bounces
  3x), artificial underground = **masonry** (phonon.h's brick; 1.5x, 2x), anything else **generic**. The scene
  log line names it. ponytail: per-surface materials want a texture or `surfacemat.2da` walkmesh join.
- The reverb tail lasts only as far as the rays' bounces carry it (harness, a closed 20 x 20 x 6 m box, 2026-09-27):
  4 bounces cut generic off by 400 ms and cave rock by 700 ms, so every space sounded like a small room. At the
  16-bounce default: generic rings ~0.7 s, masonry ~1.5 s, cave rock to the 2 s IR cap; reflection simulation
  4.8 / 8.2 / 12.6 ms per run (every 250 ms, sim thread).
- The pathing bake is per area, in the background, not cached on disk.
- `soundeax.2da` is joined in its stock row order; a module that overrides it is not read (the engine never
  loads the table).
- Steam Audio's default ray tracer; Embree is the upgrade if reflection simulation measures slow.
- Cost, as first measured live (2026-09-27, a 438k-triangle area): per-source reflections for 8 voices overran the
  audio thread (12-14 ms per 10.7 ms frame) and the unbounded pathing bake held gigabytes without finishing. Now:
  reverb defaults to the one room IR, per-source reflections cover the 4 nearest voices, pathing is opt-in with a
  1500-probe budget and one bake per area, triangles under 0.25 m² are dropped, and only 5 reflection-capable
  simulator sources exist (each carries ~12 MB of IR buffers; one per voice was 790 MB).
