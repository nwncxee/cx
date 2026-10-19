// CX-57: the Steam Audio mixer behind the engine's OpenAL (docs/audio.md). The engine keeps deciding WHAT
// plays and keeps its own AL sources; every positional (3D) one is muted in OpenAL and rendered here instead --
// HRTF or speaker panning, occlusion and transmission, simulated reflections and pathing through the area's own
// geometry -- into ONE OpenAL source fed by an AL_SOFT_callback_buffer callback. The muted engine source keeps
// playing, so every state/offset query the engine makes stays OpenAL's own answer.
//
// Threads: the AL shims run on whoever called AL (main thread, AsyncSoundStream); the render runs on OpenAL's
// mixer thread inside the bus callback and never calls AL; the simulation runs on CX's own "CX sound sim"
// thread and the pathing bake on "CX sound bake". One lock guards every table they share and is only ever held
// for copies. Steam Audio objects the render touches are created up front, so the render allocates nothing.
//
// C-style C++ like cxatlas.cpp: no exceptions, no RTTI, no C++ runtime (decltype is the only reason this is not C:
// it types the Steam Audio entry points resolved at run time).
#define AL_NO_PROTOTYPES
#define ALC_NO_PROTOTYPES
#include "AL/al.h"
#include "AL/alc.h"
#include "AL/alext.h"
#include "AL/efx.h"
#include "AL/efx-presets.h"
#include "phonon.h"
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
struct Lock
{
    SRWLOCK l;
    void on() { AcquireSRWLockExclusive(&l); }
    void off() { ReleaseSRWLockExclusive(&l); }
};
#define CXA_LOCK_INIT { SRWLOCK_INIT }
/// A wake-up for the render thread: at most one pending, so a burst of callbacks cannot make it spin.
struct Wake
{
    HANDLE h;
    void init() { h = CreateSemaphoreW(NULL, 0, 1, NULL); }
    void post() { ReleaseSemaphore(h, 1, NULL); }
    void wait(int ms) { WaitForSingleObject(h, (DWORD)ms); }
};
static DWORD WINAPI render_main_w(LPVOID);
static int start_thread() { HANDLE t = CreateThread(NULL, 0, render_main_w, NULL, 0, NULL); if (!t) return 0; SetThreadPriority(t, THREAD_PRIORITY_HIGHEST); CloseHandle(t); return 1; }
static void* cxa_sym(void* lib, const char* n) { return (void*)GetProcAddress((HMODULE)lib, n); }
/// Atomics as Interlocked ops, since MSVC has no __atomic builtins; each is a full barrier, at least the order a call site needs.
static int64_t cxa_ld(int64_t* p) { return InterlockedCompareExchange64(p, 0, 0); }
static unsigned cxa_ld(unsigned* p) { return (unsigned)InterlockedCompareExchange((volatile LONG*)p, 0, 0); }
static void cxa_st(int64_t* p, int64_t v) { InterlockedExchange64(p, v); }
static unsigned cxa_inc(unsigned* p) { return (unsigned)InterlockedIncrement((volatile LONG*)p); }
static int32_t cxa_inc(int32_t* p) { return (int32_t)InterlockedIncrement((volatile LONG*)p); }
static int32_t cxa_dec(int32_t* p) { return (int32_t)InterlockedDecrement((volatile LONG*)p); }
static int32_t cxa_ld(int32_t* p) { return (int32_t)InterlockedCompareExchange((volatile LONG*)p, 0, 0); }
static void cxa_st(int32_t* p, int32_t v) { InterlockedExchange((volatile LONG*)p, v); }
static void cxa_sleep_ms(int ms) { Sleep((DWORD)ms); }
static double cxa_now()
{
    LARGE_INTEGER f, c;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&c);
    return (double)c.QuadPart / (double)f.QuadPart;
}
#else
#include <dlfcn.h>
#include <pthread.h>
#include <semaphore.h>
#include <time.h>
struct Lock
{
    pthread_mutex_t m;
    void on() { pthread_mutex_lock(&m); }
    void off() { pthread_mutex_unlock(&m); }
};
#define CXA_LOCK_INIT { PTHREAD_MUTEX_INITIALIZER }
/// A wake-up for the render thread: at most one pending, so a burst of callbacks cannot make it spin.
struct Wake
{
    sem_t s;
    void init() { sem_init(&s, 0, 0); }
    void post() { int v = 0; sem_getvalue(&s, &v); if (v == 0) sem_post(&s); }
    void wait(int ms)
    {
        struct timespec t;
        clock_gettime(CLOCK_REALTIME, &t);
        t.tv_nsec += (long)ms * 1000000L;
        if (t.tv_nsec >= 1000000000L) { t.tv_sec++; t.tv_nsec -= 1000000000L; }
        sem_timedwait(&s, &t);
    }
};
static void* render_main_p(void*);
static int start_thread()
{
    pthread_t t;
    if (pthread_create(&t, NULL, render_main_p, NULL) != 0) return 0;
    pthread_setname_np(t, "CX sound render");
    pthread_detach(t);
    return 1;
}
static void* cxa_sym(void* lib, const char* n) { return dlsym(lib, n); }
/// Acquire load / release store / acq-rel increment (returns the new value).
template <class T> static T cxa_ld(T* p) { return __atomic_load_n(p, __ATOMIC_ACQUIRE); }
template <class T> static void cxa_st(T* p, T v) { __atomic_store_n(p, v, __ATOMIC_RELEASE); }
template <class T> static T cxa_inc(T* p) { return __atomic_add_fetch(p, 1, __ATOMIC_ACQ_REL); }
template <class T> static T cxa_dec(T* p) { return __atomic_sub_fetch(p, 1, __ATOMIC_ACQ_REL); }
static void cxa_sleep_ms(int ms)
{
    struct timespec t;
    t.tv_sec = ms / 1000;
    t.tv_nsec = (long)(ms % 1000) * 1000000L;
    nanosleep(&t, NULL);
}
static double cxa_now()
{
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (double)t.tv_sec + (double)t.tv_nsec * 1e-9;
}
#endif

// Static link (DirectPInvoke "cxaudio"): plain C linkage, nothing exported from the shipped image.
#define CX_EXPORT extern "C"

// ---- Steam Audio, resolved from the phonon library CX loads beside itself ----
#define CXA_IPL(X) \
    X(iplContextCreate) X(iplContextRelease) X(iplHRTFCreate) X(iplHRTFRelease) \
    X(iplAudioBufferAllocate) X(iplAudioBufferFree) \
    X(iplBinauralEffectCreate) X(iplBinauralEffectApply) X(iplBinauralEffectReset) \
    X(iplPanningEffectCreate) X(iplPanningEffectApply) X(iplPanningEffectReset) \
    X(iplDirectEffectCreate) X(iplDirectEffectApply) X(iplDirectEffectReset) \
    X(iplReflectionEffectCreate) X(iplReflectionEffectApply) X(iplReflectionEffectReset) \
    X(iplReflectionMixerCreate) X(iplReflectionMixerApply) X(iplReflectionMixerReset) \
    X(iplAmbisonicsDecodeEffectCreate) X(iplAmbisonicsDecodeEffectApply) \
    X(iplPathEffectCreate) X(iplPathEffectApply) X(iplPathEffectReset) \
    X(iplAirAbsorptionCalculate) \
    X(iplSceneCreate) X(iplSceneRelease) X(iplSceneRetain) X(iplSceneCommit) \
    X(iplStaticMeshCreate) X(iplStaticMeshRelease) X(iplStaticMeshAdd) \
    X(iplSimulatorCreate) X(iplSimulatorSetScene) X(iplSimulatorCommit) X(iplSimulatorSetSharedInputs) \
    X(iplSimulatorRunDirect) X(iplSimulatorRunReflections) X(iplSimulatorRunPathing) \
    X(iplSimulatorAddProbeBatch) X(iplSimulatorRemoveProbeBatch) \
    X(iplSourceCreate) X(iplSourceAdd) X(iplSourceSetInputs) X(iplSourceGetOutputs) \
    X(iplProbeArrayCreate) X(iplProbeArrayRelease) X(iplProbeArrayGenerateProbes) \
    X(iplProbeBatchCreate) X(iplProbeBatchRelease) X(iplProbeBatchAddProbeArray) X(iplProbeBatchCommit) \
    X(iplProbeBatchGetNumProbes) X(iplPathBakerBake) X(iplPathBakerCancelBake)
#define CXA_DECL(n) static decltype(&::n) p_##n;
CXA_IPL(CXA_DECL)
#undef CXA_DECL

// ---- OpenAL: the originals of what is hooked (the trampolines funchook hands back) and the rest by name ----
enum
{
    SH_BufferData, SH_DeleteBuffers, SH_Sourcef, SH_SourcePlay, SH_SourceStop, SH_SourcePause,
    SH_DeleteSources, SH_Listenerfv, SH_CreateContext, SH_MakeContextCurrent, SH_Count
};
static LPALBUFFERDATA o_alBufferData;
static LPALDELETEBUFFERS o_alDeleteBuffers;
static LPALSOURCEF o_alSourcef;
static LPALSOURCEPLAY o_alSourcePlay;
static LPALSOURCESTOP o_alSourceStop;
static LPALSOURCEPAUSE o_alSourcePause;
static LPALDELETESOURCES o_alDeleteSources;
static LPALLISTENERFV o_alListenerfv;
static LPALCCREATECONTEXT o_alcCreateContext;
static LPALCMAKECONTEXTCURRENT o_alcMakeContextCurrent;

#define CXA_AL(X) \
    X(LPALGETSOURCEI, alGetSourcei) X(LPALGETSOURCEF, alGetSourcef) X(LPALSOURCEI, alSourcei) \
    X(LPALSOURCE3I, alSource3i) X(LPALSOURCE3F, alSource3f) X(LPALGENSOURCES, alGenSources) \
    X(LPALGENBUFFERS, alGenBuffers) X(LPALGETSTRING, alGetString) X(LPALISEXTENSIONPRESENT, alIsExtensionPresent) \
    X(LPALLISTENERF, alListenerf) X(LPALGETINTEGER, alGetInteger) X(LPALGETERROR, alGetError) \
    X(LPALCGETSTRING, alcGetString) X(LPALCGETINTEGERV, alcGetIntegerv) X(LPALCGETCONTEXTSDEVICE, alcGetContextsDevice) \
    X(LPALCISEXTENSIONPRESENT, alcIsExtensionPresent) X(LPALCGETCURRENTCONTEXT, alcGetCurrentContext) X(LPALCRESETDEVICESOFT, alcResetDeviceSOFT) \
    X(LPALBUFFERCALLBACKSOFT, alBufferCallbackSOFT) X(LPALGETSTRINGISOFT, alGetStringiSOFT) \
    X(LPALGENEFFECTS, alGenEffects) X(LPALEFFECTI, alEffecti) X(LPALEFFECTF, alEffectf) X(LPALEFFECTFV, alEffectfv) \
    X(LPALGENAUXILIARYEFFECTSLOTS, alGenAuxiliaryEffectSlots) X(LPALAUXILIARYEFFECTSLOTI, alAuxiliaryEffectSloti) \
    X(LPALAUXILIARYEFFECTSLOTF, alAuxiliaryEffectSlotf)
#define CXA_DECL(t, n) static t p_##n;
CXA_AL(CXA_DECL)
#undef CXA_DECL

// ---- the shared contract with CX.Services.Sound.SoundCore (field for field) ----
/// Categories: ambient loops, footsteps, combat, spells, voices, everything else.
enum { CatAmbient, CatFootsteps, CatCombat, CatSpells, CatVoices, CatOther, CatCount };

struct CxaConfig
{
    int32_t enabled;       // route the engine's 3D voices through Steam Audio
    int32_t binaural;      // 1 = HRTF (headphones), 0 = panning to the device's speaker layout, -1 = what OpenAL
                           // detected (its own HRTF auto-enables on headphones); fixed by the first bus
    int32_t occlusion;     // 0 off, 1 occlusion, 2 occlusion + transmission
    int32_t reflections;   // 0 off, 1 room reverb, 2 per-source reflections for the nearest voices, 3 the area's EnvAudio preset
    int32_t pathing;       // sound bends around the geometry (baked per area)
    int32_t rays, bounces, order;
    float duration;        // simulated IR length, seconds
    float reverbGain;      // linear wet level
    float airScale;        // distance multiplier fed to air absorption; 0 = off
    int32_t curve;         // 0 the engine's linear 1 m..far, 1 inverse from the authored near radius
    float rolloff;
    float taper;           // fraction of the far radius where the fade to silence begins; >= 1 = none
    int32_t pitchVariance; // PlaybackVariance as pitch on one-shots
    float detune;          // looping voices: fixed +-fraction
    int32_t randomStart;   // looping voices start at a random sample
    float driftDb;         // looping voices: slow gain drift depth
    float normalize;       // 0..1: how far each sound moves toward its category's loudness target
    float catGainDb[CatCount];
    float catTargetDb[CatCount];
    int32_t dupCap;        // copies of the same sound kept audible, nearest first; 0 = unlimited
    float shelfDb;         // high shelf on looping ambients above 5 kHz
    float duckDb;          // ambient loops under combat
    int32_t fix3d;         // undo the engine's /255 (3D sits 6 dB under 2D)
    int32_t compressor;
};

/// What the engine's CExoSoundSourceInternal says about the 3D voice about to start (managed reads it).
struct CxaMeta
{
    float pos[3];
    float nearD, farD, variance;
    int32_t group, offset;
    int32_t is2d;          // a placed area-wide loop, rendered at its object (the engine's gain is /127, not /255)
};

struct CxaStats
{
    int32_t voices, blobs, slotRefused, captureRefused, busRate, busChannels, sceneTris, probes;
    int32_t bakeState;   // 0 none, 1 baking, 2 ready, 3 failed
    int32_t busOpen;
    int64_t frames;
    float renderMsAvg, renderMsMax, simMsAvg, reflMsAvg;
    float captureMsMax;  // the slowest capture on the engine's sound-start path since the last read
    int32_t underruns;   // callbacks OpenAL asked for more than the render had ready (each one a click)
};

// ---- pure helpers (SelfCheck covers each) ----
static inline float clampf(float x, float a, float b) { return x < a ? a : x > b ? b : x; }
static inline float db2lin(float db) { return powf(10.0f, db / 20.0f); }

/// NWN world (x east, y north, z up) -> Steam Audio (x right, y up, -z ahead): the same right-handed basis.
static inline IPLVector3 ipl_of(const float* n)
{
    IPLVector3 v = { n[0], n[2], -n[1] };
    return v;
}

/// The attenuation curve. 0 is what the engine asks OpenAL for (AL_LINEAR_DISTANCE_CLAMPED, reference 1 m, the
/// authored MinVolumeDist as the far end); 1 is -6 dB per doubling from the authored near radius (the
/// MaxVolumeDist that never reached AL), faded to silence over the last stretch of the far radius so a sound the
/// engine is about to cull does not pop out.
static float dist_gain(int curve, float d, float nearD, float farD, float rolloff, float taper)
{
    if (nearD <= 0.0f) nearD = 1.0f;
    if (farD <= 1.0f) farD = 1.0f + 1e-3f;
    if (curve == 0)
    {
        float dc = clampf(d, 1.0f, farD);
        return clampf(1.0f - (dc - 1.0f) / (farD - 1.0f), 0.0f, 1.0f);
    }
    float dc = d > nearD ? d : nearD;
    float g = nearD / (nearD + rolloff * (dc - nearD));
    float t0 = taper * farD;
    if (taper < 1.0f && d > t0 && farD > t0)
    {
        float x = clampf((d - t0) / (farD - t0), 0.0f, 1.0f);
        g *= 1.0f - x * x * (3.0f - 2.0f * x);
    }
    return clampf(g, 0.0f, 1.0f);
}

static inline uint32_t xs32(uint32_t* s)
{
    uint32_t x = *s;
    x ^= x << 13; x ^= x >> 17; x ^= x << 5;
    return *s = x ? x : 0x9e3779b9u;
}
static inline float urand(uint32_t* s) { return (float)(xs32(s) >> 8) * (1.0f / 16777216.0f); }

/// CExoSoundSourceInternal::Play's own draw (@0x679590), which the OpenAL backend computes and never uses: half
/// the time down by up to v/2, half the time up by up to v.
static float variance_pitch(float v, uint32_t* s)
{
    if (v <= 0.0f) return 1.0f;
    return (xs32(s) & 1) ? 1.0f + urand(s) * v : 1.0f - urand(s) * 0.5f * v;
}

/// Unit direction from the listener to the source in the listener's frame (x right, y up, -z ahead), from NWN
/// world vectors. Straight ahead when the source sits on the listener.
static IPLVector3 rel_dir(const float* src, const float* lp, const float* at, const float* up)
{
    float d[3] = { src[0] - lp[0], src[1] - lp[1], src[2] - lp[2] };
    float r[3] = { at[1] * up[2] - at[2] * up[1], at[2] * up[0] - at[0] * up[2], at[0] * up[1] - at[1] * up[0] };
    float rl = sqrtf(r[0] * r[0] + r[1] * r[1] + r[2] * r[2]);
    IPLVector3 o = { 0.0f, 0.0f, -1.0f };
    if (rl < 1e-6f) return o;
    r[0] /= rl; r[1] /= rl; r[2] /= rl;
    float u[3] = { r[1] * at[2] - r[2] * at[1], r[2] * at[0] - r[0] * at[2], r[0] * at[1] - r[1] * at[0] };
    float ul = sqrtf(u[0] * u[0] + u[1] * u[1] + u[2] * u[2]);
    float al = sqrtf(at[0] * at[0] + at[1] * at[1] + at[2] * at[2]);
    if (ul < 1e-6f || al < 1e-6f) return o;
    float x = (d[0] * r[0] + d[1] * r[1] + d[2] * r[2]);
    float y = (d[0] * u[0] + d[1] * u[1] + d[2] * u[2]) / ul;
    float z = -(d[0] * at[0] + d[1] * at[1] + d[2] * at[2]) / al;
    float l = sqrtf(x * x + y * y + z * z);
    if (l < 1e-4f) return o;
    o.x = x / l; o.y = y / l; o.z = z / l;
    return o;
}

/// The listener as Steam Audio's coordinate space, orthonormalised.
static IPLCoordinateSpace3 listener_space(const float* lp, const float* at, const float* up)
{
    IPLCoordinateSpace3 c;
    float r[3] = { at[1] * up[2] - at[2] * up[1], at[2] * up[0] - at[0] * up[2], at[0] * up[1] - at[1] * up[0] };
    float rl = sqrtf(r[0] * r[0] + r[1] * r[1] + r[2] * r[2]);
    float al = sqrtf(at[0] * at[0] + at[1] * at[1] + at[2] * at[2]);
    if (rl < 1e-6f || al < 1e-6f)
    {
        static const float north[3] = { 0, 1, 0 }, zup[3] = { 0, 0, 1 };
        return listener_space(lp, north, zup);
    }
    float a[3] = { at[0] / al, at[1] / al, at[2] / al };
    r[0] /= rl; r[1] /= rl; r[2] /= rl;
    float u[3] = { r[1] * a[2] - r[2] * a[1], r[2] * a[0] - r[0] * a[2], r[0] * a[1] - r[1] * a[0] };
    c.right = ipl_of(r); c.up = ipl_of(u); c.ahead = ipl_of(a); c.origin = ipl_of(lp);
    return c;
}

/// Catmull-Rom between samples; a looping voice wraps, a one-shot reads silence past its ends.
static inline float sample_at(const float* p, int n, int loop, double pos)
{
    int i = (int)floor(pos);
    float t = (float)(pos - i);
    float s[4];
    for (int k = 0; k < 4; k++)
    {
        int j = i - 1 + k;
        if (loop) { j %= n; if (j < 0) j += n; s[k] = p[j]; }
        else s[k] = (j < 0 || j >= n) ? 0.0f : p[j];
    }
    return s[1] + 0.5f * t * (s[2] - s[0] + t * (2.0f * s[0] - 5.0f * s[1] + 4.0f * s[2] - s[3] + t * (3.0f * (s[1] - s[2]) + s[3] - s[0])));
}

struct Biquad { float b0, b1, b2, a1, a2, z1, z2; };

/// RBJ high shelf, slope 1.
static void shelf_design(Biquad* q, float fs, float f0, float gainDb)
{
    float A = powf(10.0f, gainDb / 40.0f), w = 2.0f * 3.14159265f * f0 / fs, c = cosf(w), s = sinf(w);
    float alpha = s / 2.0f * sqrtf(2.0f), sa = 2.0f * sqrtf(A) * alpha;
    float a0 = (A + 1) - (A - 1) * c + sa;
    q->b0 = A * ((A + 1) + (A - 1) * c + sa) / a0;
    q->b1 = -2 * A * ((A - 1) + (A + 1) * c) / a0;
    q->b2 = A * ((A + 1) + (A - 1) * c - sa) / a0;
    q->a1 = 2 * ((A - 1) - (A + 1) * c) / a0;
    q->a2 = ((A + 1) - (A - 1) * c - sa) / a0;
}
static inline float biquad(Biquad* q, float x)
{
    float y = q->b0 * x + q->z1;
    q->z1 = q->b1 * x - q->a1 * y + q->z2;
    q->z2 = q->b2 * x - q->a2 * y;
    return y;
}

static int cat_of(int group)
{
    switch (group)
    {
    case 2: case 3: case 4: case 5: return CatAmbient;
    case 16: case 17: case 18: return CatFootsteps;
    case 12: case 13: return CatCombat;
    case 10: case 11: return CatSpells;
    case 6: case 7: case 14: case 15: return CatVoices;
    default: return CatOther;
    }
}

/// prioritygroups.2da's chat rows, whose length the game reads for timing: never pitched.
static inline int pitch_exempt(int group) { return group == 6 || group == 7; }
/// prioritygroups.2da row 17, Player_Footsteps: PlayFootstep's group for the local player only (others take 16/18).
static const int GroupOwnSteps = 17;
/// The player's own footsteps sit under the ears: straight below, HRTF's notches colour them and place them
/// "somewhere", and up close the direct sound dominates the room. Half the spatial blend, half the room send.
static const float OwnStepsBlend = 0.5f, OwnStepsSend = 0.5f;

/// Gain toward a category's loudness target, a fraction <paramref name="strength"/> of the way, never more than 12 dB.
static inline float norm_db(float loudDb, float targetDb, float strength)
{
    if (loudDb <= -100.0f) return 0.0f;
    return clampf((targetDb - loudDb) * strength, -12.0f, 12.0f);
}

/// Two copies of one sound whose read positions are within <paramref name="tol"/> samples play in lockstep: their
/// waveforms add in amplitude (4 copies +12 dB). A loop's positions compare around the wrap.
static int lockstep(double a, double b, int frames, int loop, double tol)
{
    double d = fabs(a - b);
    if (loop && frames > 0)
    {
        d = fmod(d, (double)frames);
        if (d > frames - d) d = frames - d;
    }
    return d < tol;
}

// ---- captured PCM ----
struct Blob
{
    int refs, frames, rate;
    float loudDb;
    uint64_t key;
    float* pcm;   // mono, [-1, 1]
};

/// The engine's four one-shot formats (PlaySourceOn3DVoice's alBufferData calls) to mono float, with a gated
/// loudness (the mean power of the 50 ms blocks within 30 dB of the loudest, so a tail of silence does not read as
/// a quiet sound) and a content key (the same sound decodes to the same bytes every play). Null for anything else.
static Blob* blob_make(ALenum fmt, const void* data, int size, int rate)
{
    int ch, bits;
    switch (fmt)
    {
    case AL_FORMAT_MONO8: ch = 1; bits = 8; break;
    case AL_FORMAT_MONO16: ch = 1; bits = 16; break;
    case AL_FORMAT_STEREO8: ch = 2; bits = 8; break;
    case AL_FORMAT_STEREO16: ch = 2; bits = 16; break;
    default: return NULL;
    }
    int frames = size / (ch * bits / 8);
    if (!data || frames <= 0 || rate <= 0) return NULL;
    Blob* b = (Blob*)calloc(1, sizeof(Blob));
    float* p = (float*)malloc(sizeof(float) * (size_t)frames);
    if (!b || !p) { free(b); free(p); return NULL; }
    const uint8_t* u8 = (const uint8_t*)data;
    const int16_t* s16 = (const int16_t*)data;
    for (int i = 0; i < frames; i++)
    {
        float acc = 0.0f;
        for (int c = 0; c < ch; c++)
            acc += bits == 8 ? ((float)u8[i * ch + c] - 128.0f) / 128.0f : (float)s16[i * ch + c] / 32768.0f;
        p[i] = acc / (float)ch;
    }
    int block = rate / 20 > 0 ? rate / 20 : 1, nb = (frames + block - 1) / block;
    double peak = 0.0, sum = 0.0;
    int kept = 0;
    for (int pass = 0; pass < 2; pass++)
        for (int k = 0; k < nb; k++)
        {
            double e = 0.0;
            int end = (k + 1) * block < frames ? (k + 1) * block : frames;
            for (int i = k * block; i < end; i++) e += (double)p[i] * p[i];
            e /= (double)(end - k * block);
            if (pass == 0) { if (e > peak) peak = e; }
            else if (e >= peak * 0.001) { sum += e; kept++; }
        }
    b->loudDb = kept && sum > 0.0 ? (float)(10.0 * log10(sum / kept)) : -120.0f;
    uint64_t h = 14695981039346656037ULL ^ (uint64_t)frames ^ ((uint64_t)rate << 32);
    int step = frames / 64 > 0 ? frames / 64 : 1;
    for (int i = 0; i < frames; i += step)
    {
        uint32_t bitsv;
        memcpy(&bitsv, &p[i], 4);
        h = (h ^ bitsv) * 1099511628211ULL;
    }
    b->pcm = p; b->frames = frames; b->rate = rate; b->key = h; b->refs = 1;
    return b;
}
static void blob_unref(Blob* b)
{
    if (b && --b->refs == 0) { free(b->pcm); free(b); }
}

// ---- tables ----
/// The render runs this many frames (~32 ms at 48 kHz) ahead of OpenAL on its own thread: a slow frame is absorbed
/// instead of heard. It ran inside OpenAL's callback, where every wait on a lock or a swapped-out page was a click
/// (CX-57 live: 5 ms average, 27 ms peaks, for 0.3 ms of work).
enum { RingAhead = 3, RingFrames = 8 };
enum { MaxSlots = 64, MaxBlobs = 256, MaxRefl = 4, MaxOrder = 2, MaxAmbi = (MaxOrder + 1) * (MaxOrder + 1), MaxBusCh = 8 };
static const float MaxDuration = 2.0f;
/// The pathing bake's probe budget per area.
static const int MaxProbes = 1500;
/// The simulation traces from this far above a sound's position: the engine places a sound at its object's feet,
/// on the floor, where the floor (and a chest's own box) occluded it -- 35 dB off a sound in the open (CX-57 live).
static const float SimLift = 1.0f;

struct BlobRef { ALuint buf; Blob* b; };

/// One engine 3D voice as the engine drives it (main/stream threads write, render and sim read -- all under g_lk).
struct Slot
{
    ALuint src;            // 0 = free (unless busy)
    int busy;              // the render still holds resources for it (a tail ringing out)
    unsigned gen;          // bumps on every (re)start
    int paused, stopped;
    Blob* blob;
    int looping, offset, group, cat, is2d;
    float gain, pitch, detuneSign, nearD, farD;
    float pos[3];
    unsigned driftSeed;
    // the simulation's latest answer, stamped with the gen it was computed for
    unsigned simGen;
    float occlusion, transmission[3];
    int reflIdx;           // per-source reflections pool entry, -1 = the room send
    IPLReflectionEffectParams refl;
    int pathValid;
    float pathEq[3], pathSh[MaxAmbi];
};

/// The render's private half of a slot.
struct Voice
{
    unsigned gen;
    int playing, tailFrames, lastRefl;
    int fresh;             // its first frame: duplicate gains land at once, not over the smoothing
    Blob* blob;
    double phase;
    float gainSm, dupSm, lfoPhase, lfoRate, lpState;
    Biquad shelf;
    IPLDirectEffect direct;
    IPLBinauralEffect binaural;
    IPLPanningEffect panning;
    IPLPathEffect path;
};

static Lock g_lk = CXA_LOCK_INIT;
static BlobRef g_blobs[MaxBlobs];
static Slot g_slots[MaxSlots];
static Voice g_voices[MaxSlots];
static CxaConfig g_cfg;
static float g_lpos[3], g_lat[3] = { 0, 1, 0 }, g_lup[3] = { 0, 0, 1 };
static CxaStats g_stats;
static char g_err[256];
static char g_describe[512];
static uint32_t g_rngMain = 0x1234567u;
/// The exit latch (atomic, no lock): set once at the engine's sound teardown and never cleared. Every entry
/// point becomes a no-op and CX's own threads leave their loops, so nothing is inside Steam Audio or OpenAL
/// when the process finalizes its static destructors.
static int32_t g_stop;
/// How many of CX's own threads (render, sim, bake) are inside the core right now, for the shutdown wait.
static int32_t g_inside;
/// The render thread is running, so its wake-up is initialised and worth posting.
static int32_t g_renderLive;
/// How long cxa_shutdown waits for CX's threads to leave the core. A sim tick is ~1 ms and a render frame well
/// under that; a bake is the long one and is cancelled rather than waited out.
enum { ShutdownWaitMs = 500 };

static int g_testReq;   // the speaker test was asked for (under g_lk)
static float g_music;   // the area music's audible level, 0..1 (under g_lk)
static float g_rain = 1.0f;   // the reverb's wet scale while it rains (under g_lk)

/// The reflection handoff (both under g_lk). An IR the render convolves is the simulator's own memory, so the
/// render must be out of it while the scene is swapped: `freeze` stands its reflections down from the next frame,
/// `hold` says a frame is between its snapshot and its last reflection call.
static int g_reflFreeze, g_reflHold;

/// The 3D voice PlaySourceOn3DVoice is about to start, per thread: the engine starts one on the main thread
/// while AsyncSoundStream starts its streams, and only the former may take the pending meta.
static thread_local int t_pending;
static thread_local CxaMeta t_meta;

// ---- Steam Audio state (created once, at the first bus) ----
struct Sa
{
    int ready, rate, frame, busCh, binaural, order, ambi;
    IPLContext ctx;
    IPLHRTF hrtf;
    IPLAudioSettings as;
    IPLSpeakerLayout layout;
    IPLSimulator sim;
    // per voice: direct + pathing only -- a reflections-capable source carries ~12 MB of IR and energy buffers
    // (65 of them were 790 MB, CX-57 live), so reflections run on a MaxRefl pool that follows its assigned voices
    IPLSource src[MaxSlots], reflSrc[MaxRefl], room;
    IPLReflectionEffect refl[MaxRefl], roomRefl;
    IPLReflectionMixer mixer;
    IPLAmbisonicsDecodeEffect decode;
    IPLAudioBuffer in, dry, bus, tmp, amb, roomIn;
    float* ring;           // RingFrames rendered frames, interleaved; the render thread writes, the callback reads
    int64_t ringW, ringR;  // samples written / read, ever (atomics: one writer, one reader)
    int primed;            // the ring has been full once: an empty one after that is an underrun
    // simulation-thread state
    IPLScene scene;
    IPLStaticMesh mesh;
    IPLProbeBatch probes;
    double lastRefl, lastPath;
    IPLReflectionEffectParams roomParams;
    int roomValid;
    // render-thread state
    float duck, comp;
    int reflOwner[MaxRefl];
    IPLBinauralEffect testBin;
    IPLPanningEffect testPan;
    int testAt;            // samples into the speaker test, -1 = off
    uint32_t testSeed;
};
/// The speaker test: a noise burst from each of these, in the listener's frame (x right, -z ahead), one per second.
static const IPLVector3 TestDirs[4] = { { 0, 0, -1 }, { 1, 0, 0 }, { 0, 0, 1 }, { -1, 0, 0 } };
static Sa g_sa;

// scene / bake hand-offs (under g_lk)
static float* g_pendVerts;
static int32_t* g_pendTris;
static int g_pendNv, g_pendNt, g_pendSet, g_pendBake, g_pendMat;

/// An area's acoustic material: every scene triangle's absorption/scattering/transmission (Steam Audio's bands),
/// and how much longer and deeper its reverb runs than the knobs' (a hard, closed space rings past both).
struct AcMat { const char* name; IPLMaterial m; float tail, bounces; };
/// By cxa_scene's material index. 0 is phonon.h's "generic"; 1 its "brick"; 2 bare rock, barely absorbing low and
/// rough (scattering), its highs dulled as a real cave's are (a bright rock tail rang like sheet metal, live) --
/// no phonon.h preset is that: its "rock" is 0.13/0.20/0.24, a softer surface than brick.
static const AcMat g_acMats[] = {
    { "generic", { { 0.10f, 0.20f, 0.30f }, 0.05f, { 0.100f, 0.050f, 0.030f } }, 1.0f, 1.0f },
    { "masonry", { { 0.03f, 0.04f, 0.07f }, 0.05f, { 0.015f, 0.015f, 0.015f } }, 1.5f, 2.0f },
    { "cave rock", { { 0.03f, 0.06f, 0.15f }, 0.70f, { 0.015f, 0.002f, 0.001f } }, 2.0f, 3.0f },
};
enum { AcMatCount = (int)(sizeof g_acMats / sizeof g_acMats[0]) };
static int g_sceneMat;   // the installed scene's material (under g_lk)

/// The knobs as the installed scene's material stretches them: the reverb's length and bounce count.
static void ac_tune(CxaConfig& c)
{
    const AcMat& a = g_acMats[g_sceneMat];
    c.duration = fminf(c.duration * a.tail, MaxDuration);
    c.bounces = (int)((float)c.bounces * a.bounces);
}
static IPLScene g_bakeScene;     // retained, waiting for the bake thread
static IPLVector3 g_bakeMin, g_bakeMax;
static IPLProbeBatch g_bakeDone; // finished, waiting for the sim thread
static int g_baking;

// ---- the bus ----
static ALuint g_busSrc, g_busBuf, g_fxSlot, g_fxEffect;
static ALCcontext* g_busCtx;
static ALCdevice* g_busDev;
static unsigned g_busGen;
static int g_envRow = -1;
static ALint g_resampler = -1;   // AL_SOURCE_RESAMPLER_SOFT for the sources OpenAL still plays; -1 = its default

static void set_err(const char* m) { snprintf(g_err, sizeof g_err, "%s", m); }

static void IPLCALL sa_log(IPLLogLevel level, const char* message)
{
    if (level == IPL_LOGLEVEL_ERROR) set_err(message);
}

static IPLAudioBuffer abuf(int ch)
{
    IPLAudioBuffer b;
    memset(&b, 0, sizeof b);
    p_iplAudioBufferAllocate(g_sa.ctx, ch, g_sa.frame, &b);
    return b;
}

/// Everything the render and the simulation will ever use, made once. False (with g_err) if Steam Audio refuses.
static int sa_init(int rate, int busCh, int binaural)
{
    Sa& s = g_sa;
    s.rate = rate; s.frame = rate > 60000 ? 1024 : 512; s.busCh = busCh; s.binaural = binaural;
    s.order = g_cfg.order < 1 ? 1 : g_cfg.order > MaxOrder ? MaxOrder : g_cfg.order;
    s.ambi = (s.order + 1) * (s.order + 1);
    IPLContextSettings cs;
    memset(&cs, 0, sizeof cs);
    cs.version = STEAMAUDIO_VERSION;
    cs.logCallback = sa_log;
    cs.simdLevel = IPL_SIMDLEVEL_AVX2;
    if (p_iplContextCreate(&cs, &s.ctx) != IPL_STATUS_SUCCESS) { set_err("iplContextCreate failed (phonon version mismatch?)"); return 0; }
    s.as.samplingRate = rate; s.as.frameSize = s.frame;
    IPLHRTFSettings hs;
    memset(&hs, 0, sizeof hs);
    hs.type = IPL_HRTFTYPE_DEFAULT; hs.volume = 1.0f; hs.normType = IPL_HRTFNORMTYPE_NONE;
    if (p_iplHRTFCreate(s.ctx, &s.as, &hs, &s.hrtf) != IPL_STATUS_SUCCESS) { set_err("iplHRTFCreate failed"); return 0; }
    memset(&s.layout, 0, sizeof s.layout);
    s.layout.type = busCh == 8 ? IPL_SPEAKERLAYOUTTYPE_SURROUND_7_1 : busCh == 6 ? IPL_SPEAKERLAYOUTTYPE_SURROUND_5_1
                  : busCh == 4 ? IPL_SPEAKERLAYOUTTYPE_QUADRAPHONIC : IPL_SPEAKERLAYOUTTYPE_STEREO;
    int irSize = (int)ceilf(MaxDuration * (float)rate);
    for (int i = 0; i < MaxSlots; i++)
    {
        Voice& v = g_voices[i];
        IPLDirectEffectSettings ds = { 1 };
        IPLBinauralEffectSettings bs = { s.hrtf };
        IPLPanningEffectSettings ps = { s.layout };
        IPLPathEffectSettings pe;
        memset(&pe, 0, sizeof pe);
        pe.maxOrder = s.order; pe.spatialize = IPL_TRUE; pe.speakerLayout = s.layout; pe.hrtf = s.hrtf;
        if (p_iplDirectEffectCreate(s.ctx, &s.as, &ds, &v.direct) != IPL_STATUS_SUCCESS
            || p_iplBinauralEffectCreate(s.ctx, &s.as, &bs, &v.binaural) != IPL_STATUS_SUCCESS
            || p_iplPanningEffectCreate(s.ctx, &s.as, &ps, &v.panning) != IPL_STATUS_SUCCESS
            || p_iplPathEffectCreate(s.ctx, &s.as, &pe, &v.path) != IPL_STATUS_SUCCESS)
        { set_err("a per-voice Steam Audio effect failed to create"); return 0; }
    }
    IPLReflectionEffectSettings rs = { IPL_REFLECTIONEFFECTTYPE_CONVOLUTION, irSize, s.ambi };
    for (int i = 0; i < MaxRefl; i++)
        if (p_iplReflectionEffectCreate(s.ctx, &s.as, &rs, &s.refl[i]) != IPL_STATUS_SUCCESS) { set_err("iplReflectionEffectCreate failed"); return 0; }
    if (p_iplReflectionEffectCreate(s.ctx, &s.as, &rs, &s.roomRefl) != IPL_STATUS_SUCCESS
        || p_iplReflectionMixerCreate(s.ctx, &s.as, &rs, &s.mixer) != IPL_STATUS_SUCCESS)
    { set_err("the reflection mixer failed to create"); return 0; }
    IPLAmbisonicsDecodeEffectSettings dcs;
    memset(&dcs, 0, sizeof dcs);
    dcs.speakerLayout = s.layout; dcs.hrtf = s.hrtf; dcs.maxOrder = s.order;
    if (p_iplAmbisonicsDecodeEffectCreate(s.ctx, &s.as, &dcs, &s.decode) != IPL_STATUS_SUCCESS) { set_err("iplAmbisonicsDecodeEffectCreate failed"); return 0; }
    s.in = abuf(1); s.dry = abuf(1); s.roomIn = abuf(1); s.bus = abuf(busCh); s.tmp = abuf(busCh); s.amb = abuf(s.ambi);
    s.ring = (float*)calloc((size_t)s.frame * RingFrames * busCh, sizeof(float));
    if (!s.ring) { set_err("the render ring failed to allocate"); return 0; }
    IPLSimulationSettings ss;
    memset(&ss, 0, sizeof ss);
    ss.flags = (IPLSimulationFlags)(IPL_SIMULATIONFLAGS_DIRECT | IPL_SIMULATIONFLAGS_REFLECTIONS | IPL_SIMULATIONFLAGS_PATHING);
    ss.sceneType = IPL_SCENETYPE_DEFAULT;
    ss.reflectionType = IPL_REFLECTIONEFFECTTYPE_CONVOLUTION;
    ss.maxNumOcclusionSamples = 16;
    ss.maxNumRays = 8192;
    ss.numDiffuseSamples = 32;
    ss.maxDuration = MaxDuration;
    ss.maxOrder = s.order;
    // REFLECTION sources only -- phonon.h: "the maximum number of sources for which reflection simulations
    // will be run at any given time". The MaxSlots direct/pathing sources added below do not count against
    // it, so this is MaxRefl + 1 and not the 69 sources the simulator holds. Checked because the mismatch
    // reads like an overflow and is not one.
    ss.maxNumSources = MaxRefl + 1;
    ss.numThreads = 2;
    ss.rayBatchSize = 1;
    ss.numVisSamples = 4;
    ss.samplingRate = rate;
    ss.frameSize = s.frame;
    if (p_iplSimulatorCreate(s.ctx, &ss, &s.sim) != IPL_STATUS_SUCCESS) { set_err("iplSimulatorCreate failed"); return 0; }
    IPLSourceSettings srs = { (IPLSimulationFlags)(IPL_SIMULATIONFLAGS_DIRECT | IPL_SIMULATIONFLAGS_PATHING) };
    IPLSourceSettings rfs = { IPL_SIMULATIONFLAGS_REFLECTIONS };
    for (int i = 0; i < MaxSlots; i++)
    {
        if (p_iplSourceCreate(s.sim, &srs, &s.src[i]) != IPL_STATUS_SUCCESS) { set_err("iplSourceCreate failed"); return 0; }
        p_iplSourceAdd(s.src[i], s.sim);
    }
    for (int i = 0; i < MaxRefl; i++)
    {
        if (p_iplSourceCreate(s.sim, &rfs, &s.reflSrc[i]) != IPL_STATUS_SUCCESS) { set_err("iplSourceCreate failed"); return 0; }
        p_iplSourceAdd(s.reflSrc[i], s.sim);
    }
    if (p_iplSourceCreate(s.sim, &rfs, &s.room) != IPL_STATUS_SUCCESS) { set_err("iplSourceCreate failed"); return 0; }
    p_iplSourceAdd(s.room, s.sim);
    // ponytail: the reflection-source count and maxNumSources are the same expression three lines apart
    // (MaxRefl + 1); a named constant would pin them, but phonon's ceiling is for REFLECTION sources only and
    // that is the thing worth writing down, not a third spelling of the arithmetic.
    p_iplSimulatorCommit(s.sim);
    for (int i = 0; i < MaxRefl; i++) s.reflOwner[i] = -1;
    {
        IPLBinauralEffectSettings bs = { s.hrtf };
        IPLPanningEffectSettings ps = { s.layout };
        if (p_iplBinauralEffectCreate(s.ctx, &s.as, &bs, &s.testBin) != IPL_STATUS_SUCCESS
            || p_iplPanningEffectCreate(s.ctx, &s.as, &ps, &s.testPan) != IPL_STATUS_SUCCESS)
        { set_err("the speaker test's effects failed to create"); return 0; }
    }
    s.testAt = -1; s.testSeed = 0x2545f491u;
    s.duck = 1.0f; s.comp = 1.0f;
    s.ready = 1;
    return 1;
}

// ---- the render (OpenAL's mixer thread) ----
static IPLAudioBuffer view(IPLAudioBuffer b, int ch)
{
    b.numChannels = ch;
    return b;
}
static void zero(IPLAudioBuffer* b)
{
    for (int c = 0; c < b->numChannels; c++) memset(b->data[c], 0, sizeof(float) * (size_t)b->numSamples);
}
static void mix_into(IPLAudioBuffer* dst, const IPLAudioBuffer* src, float g)
{
    for (int c = 0; c < dst->numChannels && c < src->numChannels; c++)
        for (int n = 0; n < dst->numSamples; n++) dst->data[c][n] += src->data[c][n] * g;
}

/// One frame: slots copied in under the lock, then every voice rendered with none held.
static void render_frame(unsigned gen, float* dst)
{
    Sa& s = g_sa;
    double t0 = cxa_now();
    const int N = s.frame;
    CxaConfig cfg;
    float lp[3], lat[3], lup[3];
    static Slot snap[MaxSlots];
    g_lk.on();
    int stale = gen != g_busGen;
    cfg = g_cfg;
    ac_tune(cfg);
    if (g_testReq) { g_testReq = 0; s.testAt = 0; }
    float music = g_music;
    cfg.reverbGain *= g_rain;
    memcpy(lp, g_lpos, sizeof lp); memcpy(lat, g_lat, sizeof lat); memcpy(lup, g_lup, sizeof lup);
    for (int i = 0; i < MaxSlots; i++)
    {
        Slot& sl = g_slots[i];
        Voice& v = g_voices[i];
        snap[i] = sl;
        if (sl.src && sl.gen != v.gen)
        {
            v.gen = sl.gen;
            if (v.blob != sl.blob) { blob_unref(v.blob); v.blob = sl.blob; if (v.blob) v.blob->refs++; }
            v.playing = v.blob != NULL;
            v.phase = v.blob && v.blob->frames ? (double)(sl.offset % v.blob->frames) : 0.0;
            v.gainSm = 0.0f; v.dupSm = 1.0f; v.lpState = 0.0f; v.tailFrames = 0; v.fresh = 1;
            uint32_t r = sl.driftSeed | 1;
            v.lfoPhase = urand(&r) * 6.2831853f; v.lfoRate = 0.05f + urand(&r) * 0.15f;
            if (cfg.shelfDb != 0.0f) shelf_design(&v.shelf, (float)s.rate, 5000.0f, cfg.shelfDb);
            v.shelf.z1 = v.shelf.z2 = 0.0f;
            p_iplDirectEffectReset(v.direct); p_iplBinauralEffectReset(v.binaural);
            p_iplPanningEffectReset(v.panning); p_iplPathEffectReset(v.path);
        }
        if (!sl.src || sl.stopped) v.playing = 0;
    }
    // reflections are decided, and the room's IR taken, under the same lock the scene swap freezes them with:
    // past this point no frame can pick up an IR the swap is about to reallocate
    int reflMode = cfg.enabled && !stale && !g_reflFreeze && (cfg.reflections == 1 || cfg.reflections == 2) ? cfg.reflections : 0;
    int roomValid = reflMode >= 1 && s.roomValid;
    IPLReflectionEffectParams roomParams = s.roomParams;
    g_reflHold = reflMode >= 1;
    g_lk.off();

    zero(&s.bus);
    if (stale || !cfg.enabled)
    {
        for (int i = 0; i < MaxSlots; i++) { g_voices[i].playing = 0; g_voices[i].tailFrames = 0; }
    }

    // duplicates: the dupCap nearest copies of one sound stay, the rest fade out; copies in lockstep sum by power
    float dist[MaxSlots];
    double phase0[MaxSlots];
    for (int i = 0; i < MaxSlots; i++)
    {
        phase0[i] = g_voices[i].phase;
        const float* p = snap[i].pos;
        float dx = p[0] - lp[0], dy = p[1] - lp[1], dz = p[2] - lp[2];
        dist[i] = sqrtf(dx * dx + dy * dy + dz * dz);
    }
    int culled[MaxSlots];
    for (int i = 0; i < MaxSlots; i++)
    {
        Blob* b = g_voices[i].blob;
        int nearer = 0;
        if (cfg.dupCap > 0 && b && g_voices[i].playing)
            for (int j = 0; j < MaxSlots; j++)
                if (j != i && g_voices[j].playing && g_voices[j].blob && g_voices[j].blob->key == b->key
                    && (dist[j] < dist[i] || (dist[j] == dist[i] && j < i))) nearer++;
        culled[i] = cfg.dupCap > 0 && nearer >= cfg.dupCap;
    }
    // ambience steps back under combat, speech (prioritygroups' chat rows), and music as loud as the engine plays it
    int combat = 0;
    for (int i = 0; i < MaxSlots; i++)
        if (g_voices[i].playing && (snap[i].cat == CatCombat || pitch_exempt(snap[i].group))) combat = 1;
    float duckTarget = combat ? db2lin(cfg.duckDb) : db2lin(cfg.duckDb * music);
    float duckK = combat ? 1.0f - expf(-(float)N / (0.1f * s.rate)) : 1.0f - expf(-(float)N / (1.5f * s.rate));
    s.duck += (duckTarget - s.duck) * duckK;

    zero(&s.roomIn);
    for (int i = 0; i < MaxSlots; i++)
    {
        Voice& v = g_voices[i];
        const Slot& sl = snap[i];
        int live = v.playing || v.tailFrames > 0;
        if (!live) continue;
        Blob* b = v.blob;
        float dupTarget = 1.0f;
        if (b && v.playing)
        {
            int locked = 1;
            for (int j = 0; j < MaxSlots; j++)
                if (j != i && !culled[j] && g_voices[j].playing && g_voices[j].blob && g_voices[j].blob->key == b->key
                    && lockstep(phase0[i], phase0[j], b->frames, sl.looping, 0.01 * b->rate)) locked++;
            dupTarget = culled[i] ? 0.0f : 1.0f / sqrtf((float)locked);
        }
        v.dupSm = v.fresh ? dupTarget : v.dupSm + (dupTarget - v.dupSm) * (1.0f - expf(-(float)N / (0.25f * s.rate)));
        v.fresh = 0;
        float g = 0.0f;
        if (v.playing && b)
        {
            g = sl.gain * (cfg.fix3d && !sl.is2d ? 2.0f : 1.0f)
              * db2lin(cfg.catGainDb[sl.cat] + norm_db(b->loudDb, cfg.catTargetDb[sl.cat], cfg.normalize)) * v.dupSm;
            if (sl.cat == CatAmbient) g *= s.duck;
            if (sl.looping && cfg.driftDb > 0.0f) g *= db2lin(cfg.driftDb * sinf(v.lfoPhase));
            if (sl.paused) g = 0.0f;
        }
        v.lfoPhase += 6.2831853f * v.lfoRate * (float)N / (float)s.rate;
        if (v.lfoPhase > 6.2831853f) v.lfoPhase -= 6.2831853f;
        float* in = s.in.data[0];
        float g0 = v.gainSm, dg = (g - g0) / (float)N;
        if (v.playing && b && !sl.paused)
        {
            double step = (double)b->rate / (double)s.rate * (double)sl.pitch;
            for (int n = 0; n < N; n++)
            {
                in[n] = v.playing ? sample_at(b->pcm, b->frames, sl.looping, v.phase) * (g0 + dg * (float)n) : 0.0f;
                v.phase += step;
                if (v.phase >= (double)b->frames)
                {
                    if (sl.looping) v.phase = fmod(v.phase, (double)b->frames);
                    else v.playing = 0;
                }
            }
        }
        else memset(in, 0, sizeof(float) * (size_t)N);
        v.gainSm = g;
        if (sl.looping && sl.cat == CatAmbient && cfg.shelfDb != 0.0f)
            for (int n = 0; n < N; n++) in[n] = biquad(&v.shelf, in[n]);
        if (sl.looping && cfg.driftDb > 0.0f)
        {
            // cutoff wanders 8..16 kHz with the gain drift
            float fc = 11300.0f * powf(2.0f, 0.5f * sinf(v.lfoPhase * 0.7f));
            float a = 1.0f - expf(-6.2831853f * fc / (float)s.rate);
            for (int n = 0; n < N; n++) { v.lpState += a * (in[n] - v.lpState); in[n] = v.lpState; }
        }
        if (v.playing) v.tailFrames = (int)(cfg.duration * s.rate / N) + 2;
        else if (v.tailFrames > 0) v.tailFrames--;

        // direct path
        float d = dist[i];
        IPLDirectEffectParams dp;
        memset(&dp, 0, sizeof dp);
        dp.flags = (IPLDirectEffectFlags)(IPL_DIRECTEFFECTFLAGS_APPLYDISTANCEATTENUATION | IPL_DIRECTEFFECTFLAGS_APPLYAIRABSORPTION);
        dp.distanceAttenuation = dist_gain(cfg.curve, d, sl.nearD, sl.farD, cfg.rolloff, cfg.taper);
        dp.airAbsorption[0] = dp.airAbsorption[1] = dp.airAbsorption[2] = 1.0f;
        if (cfg.airScale > 0.0f && d > 0.01f)
        {
            IPLAirAbsorptionModel am;
            memset(&am, 0, sizeof am);
            am.type = IPL_AIRABSORPTIONTYPE_DEFAULT;
            float scaled[3] = { lp[0] + (sl.pos[0] - lp[0]) * cfg.airScale, lp[1] + (sl.pos[1] - lp[1]) * cfg.airScale, lp[2] + (sl.pos[2] - lp[2]) * cfg.airScale };
            p_iplAirAbsorptionCalculate(s.ctx, ipl_of(scaled), ipl_of(lp), &am, dp.airAbsorption);
        }
        dp.directivity = 1.0f;
        dp.occlusion = 1.0f;
        dp.transmission[0] = dp.transmission[1] = dp.transmission[2] = 1.0f;
        if (cfg.occlusion && sl.simGen == sl.gen)
        {
            dp.flags = (IPLDirectEffectFlags)(dp.flags | IPL_DIRECTEFFECTFLAGS_APPLYOCCLUSION);
            dp.occlusion = sl.occlusion;
            if (cfg.occlusion > 1)
            {
                dp.flags = (IPLDirectEffectFlags)(dp.flags | IPL_DIRECTEFFECTFLAGS_APPLYTRANSMISSION);
                dp.transmissionType = IPL_TRANSMISSIONTYPE_FREQDEPENDENT;
                memcpy(dp.transmission, sl.transmission, sizeof dp.transmission);
            }
        }
        p_iplDirectEffectApply(v.direct, &dp, &s.in, &s.dry);
        IPLVector3 dir = rel_dir(sl.pos, lp, lat, lup);
        IPLAudioBuffer out = view(s.tmp, s.busCh);
        if (s.binaural)
        {
            IPLBinauralEffectParams bp;
            memset(&bp, 0, sizeof bp);
            bp.direction = dir; bp.spatialBlend = sl.group == GroupOwnSteps ? OwnStepsBlend : 1.0f; bp.hrtf = s.hrtf;
            bp.interpolation = sl.looping ? IPL_HRTFINTERPOLATION_BILINEAR : IPL_HRTFINTERPOLATION_NEAREST;
            p_iplBinauralEffectApply(v.binaural, &bp, &s.dry, &out);
        }
        else
        {
            IPLPanningEffectParams pp = { dir };
            p_iplPanningEffectApply(v.panning, &pp, &s.dry, &out);
        }
        mix_into(&s.bus, &out, 1.0f);

        // reflections: an assigned per-source IR into the mixer, else a send to the room
        int ri = reflMode == 2 && sl.simGen == sl.gen ? sl.reflIdx : -1;
        if (ri >= 0 && ri < MaxRefl && sl.refl.ir)
        {
            if (s.reflOwner[ri] != i) { p_iplReflectionEffectReset(s.refl[ri]); s.reflOwner[ri] = i; }
            IPLReflectionEffectParams rp = sl.refl;
            rp.type = IPL_REFLECTIONEFFECTTYPE_CONVOLUTION;
            rp.numChannels = s.ambi;
            rp.irSize = (int)ceilf(cfg.duration * (float)s.rate);
            IPLAudioBuffer amb = view(s.amb, s.ambi);
            p_iplReflectionEffectApply(s.refl[ri], &rp, &s.in, &amb, s.mixer);
        }
        else if (reflMode >= 1)
        {
            float send = sqrtf(dist_gain(1, d, sl.nearD, sl.farD, cfg.rolloff, cfg.taper)) * (sl.group == GroupOwnSteps ? OwnStepsSend : 1.0f);
            for (int n = 0; n < N; n++) s.roomIn.data[0][n] += in[n] * send;
        }
        // pathing renders the direct path too while the source is in sight, so it only fills what occlusion took
        float pathMix = cfg.occlusion ? 1.0f - dp.occlusion : 0.0f;
        if (cfg.pathing && sl.pathValid && sl.simGen == sl.gen && pathMix > 0.001f)
        {
            IPLPathEffectParams pp;
            memset(&pp, 0, sizeof pp);
            memcpy(pp.eqCoeffs, sl.pathEq, sizeof pp.eqCoeffs);
            pp.shCoeffs = (IPLfloat32*)sl.pathSh;
            pp.order = s.order; pp.binaural = s.binaural ? IPL_TRUE : IPL_FALSE; pp.hrtf = s.hrtf;
            pp.listener = listener_space(lp, lat, lup);
            pp.normalizeEQ = IPL_TRUE;
            p_iplPathEffectApply(v.path, &pp, &s.in, &out);
            mix_into(&s.bus, &out, pathMix);
        }
    }

    // the reflected field: the room IR plus every per-source IR, one decode
    if (reflMode >= 1)
    {
        IPLAudioBuffer amb = view(s.amb, s.ambi);
        if (roomValid && roomParams.ir)
        {
            IPLReflectionEffectParams rp = roomParams;
            rp.type = IPL_REFLECTIONEFFECTTYPE_CONVOLUTION;
            rp.numChannels = s.ambi;
            rp.irSize = (int)ceilf(cfg.duration * (float)s.rate);
            p_iplReflectionEffectApply(s.roomRefl, &rp, &s.roomIn, &amb, s.mixer);
        }
        IPLReflectionEffectParams mp;
        memset(&mp, 0, sizeof mp);
        mp.type = IPL_REFLECTIONEFFECTTYPE_CONVOLUTION;
        mp.numChannels = s.ambi;
        mp.irSize = (int)ceilf(cfg.duration * (float)s.rate);
        p_iplReflectionMixerApply(s.mixer, &mp, &amb);
        IPLAmbisonicsDecodeEffectParams dcp;
        memset(&dcp, 0, sizeof dcp);
        dcp.order = s.order; dcp.hrtf = s.hrtf; dcp.orientation = listener_space(lp, lat, lup);
        dcp.binaural = s.binaural ? IPL_TRUE : IPL_FALSE;
        IPLAudioBuffer out = view(s.tmp, s.busCh);
        p_iplAmbisonicsDecodeEffectApply(s.decode, &dcp, &amb, &out);
        mix_into(&s.bus, &out, cfg.reverbGain);
    }

    if (s.testAt >= 0)
    {
        int seg = s.testAt / s.rate;
        if (seg >= 4) s.testAt = -1;
        else
        {
            float* in = s.in.data[0];
            for (int n = 0; n < N; n++)
            {
                int t = (s.testAt + n) % s.rate, on = (int)(0.7f * s.rate);
                float env = t < on ? fminf(1.0f, fminf((float)t, (float)(on - t)) / (0.01f * s.rate)) : 0.0f;
                in[n] = (urand(&s.testSeed) * 2.0f - 1.0f) * 0.25f * env;
            }
            IPLAudioBuffer out = view(s.tmp, s.busCh);
            if (s.binaural)
            {
                IPLBinauralEffectParams bp;
                memset(&bp, 0, sizeof bp);
                bp.direction = TestDirs[seg]; bp.spatialBlend = 1.0f; bp.hrtf = s.hrtf; bp.interpolation = IPL_HRTFINTERPOLATION_NEAREST;
                p_iplBinauralEffectApply(s.testBin, &bp, &s.in, &out);
            }
            else
            {
                IPLPanningEffectParams pp = { TestDirs[seg] };
                p_iplPanningEffectApply(s.testPan, &pp, &s.in, &out);
            }
            mix_into(&s.bus, &out, 1.0f);
            s.testAt += N;
        }
    }

    // a gentle bus compressor ahead of OpenAL's own output limiter: -12 dBFS, 3:1, 5 ms / 250 ms
    for (int n = 0; n < N; n++)
    {
        float peak = 0.0f;
        for (int c = 0; c < s.busCh; c++) { float a = fabsf(s.bus.data[c][n]); if (a > peak) peak = a; }
        float want = 1.0f;
        if (cfg.compressor && peak > 0.2512f) want = powf(peak / 0.2512f, 1.0f / 3.0f - 1.0f);
        float k = want < s.comp ? 1.0f - expf(-1.0f / (0.005f * s.rate)) : 1.0f - expf(-1.0f / (0.25f * s.rate));
        s.comp += (want - s.comp) * k;
        for (int c = 0; c < s.busCh; c++) dst[n * s.busCh + c] = s.bus.data[c][n] * s.comp;
    }

    double ms = (cxa_now() - t0) * 1000.0;
    g_lk.on();
    g_reflHold = 0;
    int live = 0;
    for (int i = 0; i < MaxSlots; i++)
    {
        Voice& v = g_voices[i];
        int active = v.playing || v.tailFrames > 0;
        if (v.playing) live++;
        g_slots[i].busy = active;
        if (!active && v.blob && (!g_slots[i].src || g_slots[i].blob != v.blob)) { blob_unref(v.blob); v.blob = NULL; }
    }
    g_stats.voices = live;
    g_stats.frames++;
    g_stats.renderMsAvg += ((float)ms - g_stats.renderMsAvg) * 0.01f;
    if ((float)ms > g_stats.renderMsMax) g_stats.renderMsMax = (float)ms;
    g_lk.off();
}

static Wake g_wake;

/// OpenAL's mixer thread: copies what the render thread has ready -- no lock, no Steam Audio, no waiting.
static ALsizei AL_APIENTRY bus_callback(ALvoid* user, ALvoid* data, ALsizei bytes) noexcept
{
    Sa& s = g_sa;
    float* o = (float*)data;
    int want = bytes / (int)(sizeof(float) * s.busCh), done = 0;
    if ((unsigned)(uintptr_t)user != cxa_ld(&g_busGen) || cxa_ld(&g_stop)) { memset(data, 0, (size_t)bytes); return bytes; }
    const int64_t cap = (int64_t)s.frame * RingFrames;
    int64_t r = s.ringR, w = cxa_ld(&s.ringW);
    while (done < want && r < w)
    {
        int at = (int)(r % cap), n = want - done;
        if (n > w - r) n = (int)(w - r);
        if (n > cap - at) n = (int)(cap - at);
        memcpy(o + (size_t)done * s.busCh, s.ring + (size_t)at * s.busCh, sizeof(float) * (size_t)n * s.busCh);
        done += n; r += n;
    }
    cxa_st(&s.ringR, r);
    if (done < want)
    {
        memset(o + (size_t)done * s.busCh, 0, sizeof(float) * (size_t)(want - done) * s.busCh);
        if (s.primed) cxa_inc(&g_stats.underruns);
    }
    g_wake.post();
    return bytes;
}

/// "CX sound render": keeps the ring RingAhead frames ahead of the callback, until the exit latch.
static void render_main()
{
    Sa& s = g_sa;
    const int64_t N = s.frame;
    while (!cxa_ld(&g_stop))
    {
        cxa_inc(&g_inside);
        unsigned gen = cxa_ld(&g_busGen);
        int64_t w = s.ringW;
        while (w - cxa_ld(&s.ringR) < RingAhead * N)
        {
            render_frame(gen, s.ring + (size_t)(w % (N * RingFrames)) * s.busCh);
            w += N;
            cxa_st(&s.ringW, w);
        }
        s.primed = 1;
        cxa_dec(&g_inside);
        g_wake.wait(5);
    }
}
#ifdef _WIN32
static DWORD WINAPI render_main_w(LPVOID) { render_main(); return 0; }
#else
static void* render_main_p(void*) { render_main(); return NULL; }
#endif

// ---- EnvAudio -> EFX preset (soundeax.2da's rows, joined to efx-presets.h by label) ----
struct Preset { const char* label; EFXEAXREVERBPROPERTIES p; };
#define P(n) { #n, EFX_REVERB_PRESET_##n },
/// soundeax.2da's 113 rows in the stock row order (base_2da.bif), each joined to the efx-presets.h macro OF THE
/// SAME LABEL -- the two tables do not share an order (CASTLE_LARGEROOM/CASTLE_LONGPASSAGE are swapped) and one
/// spelling differs (the 2DA's CARPETTEDHALLWAY). ponytail: a module that overrides soundeax.2da is not read; the
/// engine never loads the table, so reading it would mean a resman read of our own.
static const Preset g_presets[] = {
    P(GENERIC) P(PADDEDCELL) P(ROOM) P(BATHROOM) P(LIVINGROOM) P(STONEROOM) P(AUDITORIUM) P(CONCERTHALL) P(CAVE) P(ARENA)
    P(HANGAR) { "CARPETTEDHALLWAY", EFX_REVERB_PRESET_CARPETEDHALLWAY }, P(HALLWAY) P(STONECORRIDOR) P(ALLEY) P(FOREST)
    P(CITY) P(MOUNTAINS) P(QUARRY) P(PLAIN) P(PARKINGLOT) P(SEWERPIPE) P(UNDERWATER) P(DRUGGED) P(DIZZY) P(PSYCHOTIC)
    P(CASTLE_SMALLROOM) P(CASTLE_SHORTPASSAGE) P(CASTLE_MEDIUMROOM) P(CASTLE_LONGPASSAGE) P(CASTLE_LARGEROOM)
    P(CASTLE_HALL) P(CASTLE_CUPBOARD) P(CASTLE_COURTYARD) P(CASTLE_ALCOVE) P(FACTORY_ALCOVE) P(FACTORY_SHORTPASSAGE)
    P(FACTORY_MEDIUMROOM) P(FACTORY_LONGPASSAGE) P(FACTORY_LARGEROOM) P(FACTORY_HALL) P(FACTORY_CUPBOARD)
    P(FACTORY_COURTYARD) P(FACTORY_SMALLROOM) P(ICEPALACE_ALCOVE) P(ICEPALACE_SHORTPASSAGE) P(ICEPALACE_MEDIUMROOM)
    P(ICEPALACE_LONGPASSAGE) P(ICEPALACE_LARGEROOM) P(ICEPALACE_HALL) P(ICEPALACE_CUPBOARD) P(ICEPALACE_COURTYARD)
    P(ICEPALACE_SMALLROOM) P(SPACESTATION_ALCOVE) P(SPACESTATION_MEDIUMROOM) P(SPACESTATION_SHORTPASSAGE)
    P(SPACESTATION_LONGPASSAGE) P(SPACESTATION_LARGEROOM) P(SPACESTATION_HALL) P(SPACESTATION_CUPBOARD)
    P(SPACESTATION_SMALLROOM) P(WOODEN_ALCOVE) P(WOODEN_SHORTPASSAGE) P(WOODEN_MEDIUMROOM) P(WOODEN_LONGPASSAGE)
    P(WOODEN_LARGEROOM) P(WOODEN_HALL) P(WOODEN_CUPBOARD) P(WOODEN_SMALLROOM) P(WOODEN_COURTYARD)
    P(SPORT_EMPTYSTADIUM) P(SPORT_SQUASHCOURT) P(SPORT_SMALLSWIMMINGPOOL) P(SPORT_LARGESWIMMINGPOOL) P(SPORT_GYMNASIUM)
    P(SPORT_FULLSTADIUM) P(SPORT_STADIUMTANNOY) P(PREFAB_WORKSHOP) P(PREFAB_SCHOOLROOM) P(PREFAB_PRACTISEROOM)
    P(PREFAB_OUTHOUSE) P(PREFAB_CARAVAN) P(DOME_TOMB) P(DOME_SAINTPAULS) P(PIPE_SMALL) P(PIPE_LONGTHIN) P(PIPE_LARGE)
    P(PIPE_RESONANT) P(OUTDOORS_BACKYARD) P(OUTDOORS_ROLLINGPLAINS) P(OUTDOORS_DEEPCANYON) P(OUTDOORS_CREEK)
    P(OUTDOORS_VALLEY) P(MOOD_HEAVEN) P(MOOD_HELL) P(MOOD_MEMORY) P(DRIVING_COMMENTATOR) P(DRIVING_PITGARAGE)
    P(DRIVING_INCAR_RACER) P(DRIVING_INCAR_SPORTS) P(DRIVING_INCAR_LUXURY) P(DRIVING_FULLGRANDSTAND)
    P(DRIVING_EMPTYGRANDSTAND) P(DRIVING_TUNNEL) P(CITY_STREETS) P(CITY_SUBWAY) P(CITY_MUSEUM) P(CITY_LIBRARY)
    P(CITY_UNDERPASS) P(CITY_ABANDONED) P(DUSTYROOM) P(CHAPEL) P(SMALLWATERROOM)
};
#undef P
enum { PresetCount = (int)(sizeof g_presets / sizeof g_presets[0]) };

static int preset_row(const char* label)
{
    for (int i = 0; i < PresetCount; i++) if (strcmp(g_presets[i].label, label) == 0) return i;
    return -1;
}

/// Loads the EnvAudio row into the bus's reverb slot; the send is live only while reverb is "preset".
/// Needs a current context: every call it makes after the engine drops its own logs AL_INVALID_OPERATION.
static void fx_apply()
{
    if (!g_fxSlot || !p_alEffecti || !p_alcGetCurrentContext || !p_alcGetCurrentContext()) return;
    int on = g_cfg.enabled && g_cfg.reflections == 3 && g_envRow >= 0 && g_envRow < PresetCount;
    if (on)
    {
        const EFXEAXREVERBPROPERTIES& r = g_presets[g_envRow].p;
        p_alEffecti(g_fxEffect, AL_EFFECT_TYPE, AL_EFFECT_EAXREVERB);
        p_alEffectf(g_fxEffect, AL_EAXREVERB_DENSITY, r.flDensity);
        p_alEffectf(g_fxEffect, AL_EAXREVERB_DIFFUSION, r.flDiffusion);
        p_alEffectf(g_fxEffect, AL_EAXREVERB_GAIN, r.flGain);
        p_alEffectf(g_fxEffect, AL_EAXREVERB_GAINHF, r.flGainHF);
        p_alEffectf(g_fxEffect, AL_EAXREVERB_GAINLF, r.flGainLF);
        p_alEffectf(g_fxEffect, AL_EAXREVERB_DECAY_TIME, r.flDecayTime);
        p_alEffectf(g_fxEffect, AL_EAXREVERB_DECAY_HFRATIO, r.flDecayHFRatio);
        p_alEffectf(g_fxEffect, AL_EAXREVERB_DECAY_LFRATIO, r.flDecayLFRatio);
        p_alEffectf(g_fxEffect, AL_EAXREVERB_REFLECTIONS_GAIN, r.flReflectionsGain);
        p_alEffectf(g_fxEffect, AL_EAXREVERB_REFLECTIONS_DELAY, r.flReflectionsDelay);
        p_alEffectfv(g_fxEffect, AL_EAXREVERB_REFLECTIONS_PAN, r.flReflectionsPan);
        p_alEffectf(g_fxEffect, AL_EAXREVERB_LATE_REVERB_GAIN, r.flLateReverbGain);
        p_alEffectf(g_fxEffect, AL_EAXREVERB_LATE_REVERB_DELAY, r.flLateReverbDelay);
        p_alEffectfv(g_fxEffect, AL_EAXREVERB_LATE_REVERB_PAN, r.flLateReverbPan);
        p_alEffectf(g_fxEffect, AL_EAXREVERB_ECHO_TIME, r.flEchoTime);
        p_alEffectf(g_fxEffect, AL_EAXREVERB_ECHO_DEPTH, r.flEchoDepth);
        p_alEffectf(g_fxEffect, AL_EAXREVERB_MODULATION_TIME, r.flModulationTime);
        p_alEffectf(g_fxEffect, AL_EAXREVERB_MODULATION_DEPTH, r.flModulationDepth);
        p_alEffectf(g_fxEffect, AL_EAXREVERB_AIR_ABSORPTION_GAINHF, r.flAirAbsorptionGainHF);
        p_alEffectf(g_fxEffect, AL_EAXREVERB_HFREFERENCE, r.flHFReference);
        p_alEffectf(g_fxEffect, AL_EAXREVERB_LFREFERENCE, r.flLFReference);
        p_alEffectf(g_fxEffect, AL_EAXREVERB_ROOM_ROLLOFF_FACTOR, r.flRoomRolloffFactor);
        p_alEffecti(g_fxEffect, AL_EAXREVERB_DECAY_HFLIMIT, r.iDecayHFLimit);
        p_alAuxiliaryEffectSloti(g_fxSlot, AL_EFFECTSLOT_EFFECT, (ALint)g_fxEffect);
        p_alAuxiliaryEffectSlotf(g_fxSlot, AL_EFFECTSLOT_GAIN, g_cfg.reverbGain * g_rain);
    }
    if (g_busSrc) p_alSource3i(g_busSrc, AL_AUXILIARY_SEND_FILTER, on ? (ALint)g_fxSlot : AL_EFFECTSLOT_NULL, 0, AL_FILTER_NULL);
}

// ---- the bus: one OpenAL source per context, fed by the render ----
/// The device's speaker count for panning; 2 for HRTF. ALC_OUTPUT_MODE_SOFT says what OpenAL is driving.
static int device_channels(ALCdevice* dev)
{
    ALCint mode = 0;
    p_alcGetIntegerv(dev, ALC_OUTPUT_MODE_SOFT, 1, &mode);
    switch (mode)
    {
    case ALC_QUAD_SOFT: return 4;
    case ALC_SURROUND_5_1_SOFT: return 6;
    case ALC_SURROUND_6_1_SOFT: return 6;
    case ALC_SURROUND_7_1_SOFT: return 8;
    default: return 2;
    }
}

static int g_busFailed;
static void bus_open(ALCcontext* ctx)
{
    if (!ctx || ctx == g_busCtx || !p_alBufferCallbackSOFT || g_busFailed || cxa_ld(&g_stop)) return;
    g_busFailed = 1;   // latched until the bus is up: a refusal is said once, not retried every frame
    ALCdevice* dev = p_alcGetContextsDevice(ctx);
    if (!p_alIsExtensionPresent("AL_SOFT_callback_buffer") || !p_alIsExtensionPresent("AL_EXT_FLOAT32")
        || !p_alIsExtensionPresent("AL_SOFT_direct_channels_remix"))
    { set_err("this OpenAL lacks AL_SOFT_callback_buffer / AL_EXT_FLOAT32 / AL_SOFT_direct_channels_remix -- not CX's pinned build"); return; }
    ALCint rate = 0;
    p_alcGetIntegerv(dev, ALC_FREQUENCY, 1, &rate);
    if (rate <= 0) rate = 48000;
    int binaural = g_cfg.binaural;
    if (binaural < 0)
    {
        ALCint hrtf = 0;
        p_alcGetIntegerv(dev, ALC_HRTF_SOFT, 1, &hrtf);
        binaural = hrtf == ALC_TRUE;
    }
    int ch = binaural ? 2 : device_channels(dev);
    if (!g_sa.ready)
    {
        if (!sa_init(rate, ch, binaural)) return;
        g_wake.init();
        if (!start_thread()) { g_sa.ready = 0; set_err("the render thread failed to start"); return; }
        cxa_st(&g_renderLive, 1);
    }
    ALenum fmt = g_sa.busCh == 8 ? AL_FORMAT_71CHN32 : g_sa.busCh == 6 ? AL_FORMAT_51CHN32
               : g_sa.busCh == 4 ? AL_FORMAT_QUAD32 : AL_FORMAT_STEREO_FLOAT32;
    g_lk.on();
    unsigned gen = cxa_inc(&g_busGen);
    g_lk.off();
    p_alGenBuffers(1, &g_busBuf);
    p_alBufferCallbackSOFT(g_busBuf, fmt, g_sa.rate, bus_callback, (ALvoid*)(uintptr_t)gen);
    p_alGenSources(1, &g_busSrc);
    p_alSourcei(g_busSrc, AL_BUFFER, (ALint)g_busBuf);
    p_alSourcei(g_busSrc, AL_SOURCE_RELATIVE, AL_TRUE);
    p_alSource3f(g_busSrc, AL_POSITION, 0.0f, 0.0f, 0.0f);
    p_alSourcei(g_busSrc, AL_DIRECT_CHANNELS_SOFT, AL_REMIX_UNMATCHED_SOFT);
    p_alSourcei(g_busSrc, AL_SOURCE_SPATIALIZE_SOFT, AL_FALSE);
    o_alSourcef(g_busSrc, AL_GAIN, 1.0f);
    if (p_alGenAuxiliaryEffectSlots && p_alcIsExtensionPresent(dev, "ALC_EXT_EFX"))
    {
        p_alGenAuxiliaryEffectSlots(1, &g_fxSlot);
        p_alGenEffects(1, &g_fxEffect);
    }
    fx_apply();
    o_alSourcePlay(g_busSrc);
    int err = p_alGetError();
    g_busCtx = ctx; g_busDev = dev;
    ALCint hrtfOn = 0, mode = 0;
    p_alcGetIntegerv(dev, ALC_HRTF_SOFT, 1, &hrtfOn);
    p_alcGetIntegerv(dev, ALC_OUTPUT_MODE_SOFT, 1, &mode);
    const ALCchar* name = p_alcGetString(dev, ALC_ALL_DEVICES_SPECIFIER);
    const ALchar* ver = p_alGetString(AL_VERSION);
    const ALchar* rend = p_alGetString(AL_RENDERER);
    snprintf(g_describe, sizeof g_describe, "device '%s', AL_VERSION '%s', renderer '%s', %d Hz, OpenAL HRTF %s, output mode 0x%x; "
             "Steam Audio %s bus, %d channel(s), frame %d",
             name ? name : "?", ver ? ver : "?", rend ? rend : "?", g_sa.rate, hrtfOn == ALC_TRUE ? "on" : "off", (unsigned)mode,
             g_sa.binaural ? "HRTF (binaural)" : "panning", g_sa.busCh, g_sa.frame);
    g_lk.on();
    g_stats.busOpen = err == AL_NO_ERROR;
    g_stats.busRate = g_sa.rate; g_stats.busChannels = g_sa.busCh;
    g_lk.off();
    if (err != AL_NO_ERROR) set_err("the bus source refused to start (alGetError after alSourcePlay)");
    else g_busFailed = 0;
}

// ---- the shims (hooked over the pinned OpenAL's exports) ----
static int slot_of(ALuint src)
{
    if (!src) return -1;
    for (int i = 0; i < MaxSlots; i++) if (g_slots[i].src == src) return i;
    return -1;
}

static void AL_APIENTRY sh_alBufferData(ALuint buf, ALenum fmt, const ALvoid* data, ALsizei size, ALsizei freq) noexcept
{
    if (t_pending && g_sa.ready && g_cfg.enabled)
    {
        double t0 = cxa_now();
        Blob* b = blob_make(fmt, data, size, freq);
        float ms = (float)((cxa_now() - t0) * 1000.0);
        g_lk.on();
        if (ms > g_stats.captureMsMax) g_stats.captureMsMax = ms;
        int free_ = -1;
        for (int i = 0; i < MaxBlobs; i++)
        {
            if (g_blobs[i].buf == buf) { blob_unref(g_blobs[i].b); g_blobs[i].buf = 0; g_blobs[i].b = NULL; }
            if (!g_blobs[i].buf && free_ < 0) free_ = i;
        }
        if (b && free_ >= 0) { g_blobs[free_].buf = buf; g_blobs[free_].b = b; g_stats.blobs++; }
        else { blob_unref(b); g_stats.captureRefused++; }
        g_lk.off();
    }
    o_alBufferData(buf, fmt, data, size, freq);
}

static void AL_APIENTRY sh_alDeleteBuffers(ALsizei n, const ALuint* bufs) noexcept
{
    g_lk.on();
    for (int k = 0; k < n; k++)
        for (int i = 0; i < MaxBlobs; i++)
            if (bufs[k] && g_blobs[i].buf == bufs[k])
            {
                blob_unref(g_blobs[i].b); g_blobs[i].buf = 0; g_blobs[i].b = NULL; g_stats.blobs--;
            }
    g_lk.off();
    o_alDeleteBuffers(n, bufs);
}

static void AL_APIENTRY sh_alSourcef(ALuint src, ALenum param, ALfloat value) noexcept
{
    if (param == AL_GAIN)
    {
        g_lk.on();
        int i = slot_of(src);
        if (i >= 0) g_slots[i].gain = value;
        g_lk.off();
        if (i >= 0) value = 0.0f;
    }
    o_alSourcef(src, param, value);
}

/// A start: a mapped voice restarts (or resumes); the engine's 3D start becomes a voice here and its AL source
/// goes silent; anything else passes, with the chosen resampler.
static void AL_APIENTRY sh_alSourcePlay(ALuint src) noexcept
{
    g_lk.on();
    int i = slot_of(src);
    if (i >= 0)
    {
        Slot& sl = g_slots[i];
        int resume = sl.paused && !sl.stopped;
        if (resume) sl.paused = 0;
        else { sl.gen++; sl.paused = 0; sl.stopped = 0; sl.offset = 0; }
        g_lk.off();
        if (!resume)
        {
            // a restart (CExoSoundSourceInternal::Play's replay) re-sets AL_LOOPING just before it
            ALint loop = 0;
            p_alGetSourcei(src, AL_LOOPING, &loop);
            g_lk.on();
            if (g_slots[i].src == src) g_slots[i].looping = loop != 0;
            g_lk.off();
        }
        o_alSourcePlay(src);
        return;
    }
    g_lk.off();
    if (t_pending && g_sa.ready && g_cfg.enabled && g_stats.busOpen)
    {
        ALint buf = 0, loop = 0;
        p_alGetSourcei(src, AL_BUFFER, &buf);
        p_alGetSourcei(src, AL_LOOPING, &loop);
        ALfloat gain = 1.0f;
        p_alGetSourcef(src, AL_GAIN, &gain);
        float pitch = 1.0f;
        int offset = t_meta.offset;
        g_lk.on();
        Blob* b = NULL;
        for (int k = 0; k < MaxBlobs; k++) if (buf && g_blobs[k].buf == (ALuint)buf) { b = g_blobs[k].b; break; }
        int fs = -1;
        if (b) for (int k = 0; k < MaxSlots; k++) if (!g_slots[k].src && !g_slots[k].busy) { fs = k; break; }
        if (b && fs >= 0)
        {
            Slot& sl = g_slots[fs];
            unsigned gen = sl.gen + 1;
            memset(&sl, 0, sizeof sl);
            sl.src = src; sl.gen = gen; sl.blob = b; b->refs++;
            sl.looping = loop != 0; sl.group = t_meta.group; sl.cat = cat_of(t_meta.group); sl.is2d = t_meta.is2d;
            sl.gain = gain; sl.nearD = t_meta.nearD; sl.farD = t_meta.farD;
            memcpy(sl.pos, t_meta.pos, sizeof sl.pos);
            sl.reflIdx = -1;
            sl.driftSeed = xs32(&g_rngMain);
            if (sl.looping)
            {
                if (g_cfg.detune > 0.0f) pitch = 1.0f + (urand(&g_rngMain) * 2.0f - 1.0f) * g_cfg.detune;
                if (g_cfg.randomStart && offset == 0 && b->frames > 0) offset = (int)(xs32(&g_rngMain) % (uint32_t)b->frames);
            }
            else if (g_cfg.pitchVariance && !pitch_exempt(sl.group)) pitch = variance_pitch(t_meta.variance, &g_rngMain);
            sl.pitch = pitch; sl.offset = offset;
            g_lk.off();
            o_alSourcef(src, AL_GAIN, 0.0f);
            if (pitch != 1.0f) o_alSourcef(src, AL_PITCH, pitch);
            if (offset != t_meta.offset) p_alSourcei(src, AL_SAMPLE_OFFSET, offset);
            o_alSourcePlay(src);
            return;
        }
        if (!b) g_stats.captureRefused++; else g_stats.slotRefused++;
        g_lk.off();
    }
    if (g_resampler >= 0) p_alSourcei(src, AL_SOURCE_RESAMPLER_SOFT, g_resampler);
    o_alSourcePlay(src);
}

static void AL_APIENTRY sh_alSourceStop(ALuint src) noexcept
{
    g_lk.on();
    int i = slot_of(src);
    if (i >= 0) g_slots[i].stopped = 1;
    g_lk.off();
    o_alSourceStop(src);
}

static void AL_APIENTRY sh_alSourcePause(ALuint src) noexcept
{
    g_lk.on();
    int i = slot_of(src);
    if (i >= 0) g_slots[i].paused = 1;
    g_lk.off();
    o_alSourcePause(src);
}

static void AL_APIENTRY sh_alDeleteSources(ALsizei n, const ALuint* srcs) noexcept
{
    g_lk.on();
    for (int k = 0; k < n; k++)
    {
        int i = slot_of(srcs[k]);
        if (i < 0) continue;
        blob_unref(g_slots[i].blob);
        g_slots[i].blob = NULL; g_slots[i].src = 0; g_slots[i].stopped = 1;
    }
    g_lk.off();
    o_alDeleteSources(n, srcs);
}

static void AL_APIENTRY sh_alListenerfv(ALenum param, const ALfloat* v) noexcept
{
    if (v && (param == AL_POSITION || param == AL_ORIENTATION))
    {
        g_lk.on();
        if (param == AL_POSITION) memcpy(g_lpos, v, sizeof g_lpos);
        else { memcpy(g_lat, v, sizeof g_lat); memcpy(g_lup, v + 3, sizeof g_lup); }
        g_lk.off();
    }
    o_alListenerfv(param, v);
}

/// The engine's NULL attribute list, replaced by CX's: HRTF and the output limiter are device-level choices the
/// engine never makes, and a context made with them needs no reset afterwards.
static ALCint g_devHrtf = ALC_DONT_CARE_SOFT, g_devLimiter = ALC_TRUE, g_devMode = ALC_ANY_SOFT;
static ALCcontext* ALC_APIENTRY sh_alcCreateContext(ALCdevice* dev, const ALCint* attrs) noexcept
{
    if (attrs) return o_alcCreateContext(dev, attrs);
    ALCint a[] = { ALC_HRTF_SOFT, g_devHrtf, ALC_OUTPUT_LIMITER_SOFT, g_devLimiter, ALC_OUTPUT_MODE_SOFT, g_devMode, 0 };
    return o_alcCreateContext(dev, a);
}

static ALCboolean ALC_APIENTRY sh_alcMakeContextCurrent(ALCcontext* ctx) noexcept
{
    ALCboolean r = o_alcMakeContextCurrent(ctx);
    if (r && ctx && ctx != g_busCtx && g_cfg.enabled) bus_open(ctx);
    return r;
}

// ---- the simulation (CX's "CX sound sim" thread) ----

/// How long a scene swap waits for the render to leave its reflection section before it goes ahead anyway.
static const double QuiesceSeconds = 2.0;

/// Takes every simulator-owned IR out of the render's hands, for a caller about to commit the simulator.
/// iplSimulatorCommit reallocates the reflection state those IRs live in (phonon.h: it "cannot be called while
/// any simulation is running", and the handles it hands out do not outlive it), and the render convolves them on
/// its own thread -- so the freeze stops the next frame from picking one up, the cleared params stop every frame
/// after it, and the wait lets the frame that may already be inside iplReflectionEffectApply finish.
static void refl_quiesce(void)
{
    Sa& s = g_sa;
    g_lk.on();
    g_reflFreeze = 1;
    for (int i = 0; i < MaxSlots; i++) { g_slots[i].reflIdx = -1; memset(&g_slots[i].refl, 0, sizeof g_slots[i].refl); }
    s.roomValid = 0;
    memset(&s.roomParams, 0, sizeof s.roomParams);
    int held = g_reflHold;
    g_lk.off();
    double t0 = cxa_now();
    while (held)
    {
        cxa_sleep_ms(1);
        g_lk.on();
        held = g_reflHold;
        g_lk.off();
        if (held && cxa_now() - t0 > QuiesceSeconds)
        {
            set_err("the sound render did not hand its reflections back for the scene swap");
            break;
        }
    }
}

/// Lets the render have reflections again, on the committed scene: every effect is reset first, since each holds
/// the partitioned state of an IR that no longer exists.
static void refl_thaw(void)
{
    Sa& s = g_sa;
    for (int i = 0; i < MaxRefl; i++) { p_iplReflectionEffectReset(s.refl[i]); s.reflOwner[i] = -1; }
    p_iplReflectionEffectReset(s.roomRefl);
    p_iplReflectionMixerReset(s.mixer);
    g_lk.on();
    g_reflFreeze = 0;
    g_lk.off();
}

static void scene_install(float* verts, int nv, int32_t* tris, int nt, int bake, int matIdx)
{
    Sa& s = g_sa;
    IPLScene scene = NULL;
    IPLStaticMesh mesh = NULL;
    if (nv > 0 && nt > 0)
    {
        IPLSceneSettings ss;
        memset(&ss, 0, sizeof ss);
        ss.type = IPL_SCENETYPE_DEFAULT;
        IPLVector3* v = (IPLVector3*)malloc(sizeof(IPLVector3) * (size_t)nv);
        int32_t* mi = (int32_t*)calloc((size_t)nt, sizeof(int32_t));
        // one material for the area: tiles carry no acoustic material of their own
        IPLMaterial mat = g_acMats[matIdx].m;
        if (v && mi && p_iplSceneCreate(s.ctx, &ss, &scene) == IPL_STATUS_SUCCESS)
        {
            IPLVector3 lo = { 1e30f, 1e30f, 1e30f }, hi = { -1e30f, -1e30f, -1e30f };
            for (int i = 0; i < nv; i++)
            {
                v[i] = ipl_of(verts + i * 3);
                lo.x = fminf(lo.x, v[i].x); lo.y = fminf(lo.y, v[i].y); lo.z = fminf(lo.z, v[i].z);
                hi.x = fmaxf(hi.x, v[i].x); hi.y = fmaxf(hi.y, v[i].y); hi.z = fmaxf(hi.z, v[i].z);
            }
            IPLStaticMeshSettings ms;
            memset(&ms, 0, sizeof ms);
            ms.numVertices = nv; ms.numTriangles = nt; ms.numMaterials = 1;
            ms.vertices = v; ms.triangles = (IPLTriangle*)tris; ms.materialIndices = mi; ms.materials = &mat;
            if (p_iplStaticMeshCreate(scene, &ms, &mesh) == IPL_STATUS_SUCCESS)
            {
                p_iplStaticMeshAdd(mesh, scene);
                p_iplSceneCommit(scene);
                // one bake per area: a re-send (late placeables) keeps the probes the first one is baking
                if (bake)
                {
                    g_lk.on();
                    if (g_bakeScene) p_iplSceneRelease(&g_bakeScene);
                    g_bakeScene = p_iplSceneRetain(scene);
                    g_bakeMin = lo; g_bakeMax = hi;
                    if (g_baking) p_iplPathBakerCancelBake(s.ctx);
                    g_lk.off();
                }
            }
            else p_iplSceneRelease(&scene);
        }
        free(v); free(mi);
    }
    // UNCONDITIONALLY, and before the release below: an area change sends an empty scene first
    // (SoundScene's area edge calls cxa_scene(NULL, 0, NULL, 0, 1, 0)), and skipping the call there left the
    // simulator naming a scene this function then released -- the simulator's own reference is the only thing
    // that kept it alive, which is not an invariant to lean on.
    refl_quiesce();
    p_iplSimulatorSetScene(s.sim, scene);
    if (bake && s.probes) { p_iplSimulatorRemoveProbeBatch(s.sim, s.probes); p_iplProbeBatchRelease(&s.probes); }
    p_iplSimulatorCommit(s.sim);
    if (s.mesh) p_iplStaticMeshRelease(&s.mesh);
    if (s.scene) p_iplSceneRelease(&s.scene);
    s.scene = scene; s.mesh = mesh;
    refl_thaw();
    g_lk.on();
    g_stats.sceneTris = scene ? nt : 0;
    g_sceneMat = scene ? matIdx : 0;
    if (bake) { g_stats.probes = 0; g_stats.bakeState = 0; }
    g_lk.off();
}

static IPLDeviationModel g_deviation = { IPL_DEVIATIONTYPE_DEFAULT, NULL, NULL };

static void sim_tick(void)
{
    Sa& s = g_sa;
    if (!s.ready) return;
    double t0 = cxa_now();
    static Slot snap[MaxSlots];
    CxaConfig cfg;
    float lp[3], lat[3], lup[3];
    float* pv = NULL; int32_t* pt = NULL; int nv = 0, nt = 0, set = 0, bake = 0, mat = 0;
    IPLProbeBatch done = NULL;
    g_lk.on();
    cfg = g_cfg;
    ac_tune(cfg);
    memcpy(lp, g_lpos, sizeof lp); memcpy(lat, g_lat, sizeof lat); memcpy(lup, g_lup, sizeof lup);
    memcpy(snap, g_slots, sizeof snap);
    if (g_pendSet) { pv = g_pendVerts; pt = g_pendTris; nv = g_pendNv; nt = g_pendNt; bake = g_pendBake; mat = g_pendMat; set = 1; g_pendVerts = NULL; g_pendTris = NULL; g_pendSet = 0; }
    if (g_bakeDone) { done = g_bakeDone; g_bakeDone = NULL; }
    g_lk.off();
    // master off stops the feature, not just the mix: the pending scene is dropped and an installed one torn
    // down, so no Steam Audio work (and no acoustic scene) outlives the toggle (ADR 0025)
    if (!cfg.enabled)
    {
        free(pv); free(pt);
        if (done) p_iplProbeBatchRelease(&done);
        if (s.scene) scene_install(NULL, 0, NULL, 0, 1, 0);
        return;
    }
    if (set) { scene_install(pv, nv, pt, nt, bake, mat); free(pv); free(pt); }
    if (done)
    {
        if (s.scene && cfg.pathing)
        {
            refl_quiesce();
            if (s.probes) { p_iplSimulatorRemoveProbeBatch(s.sim, s.probes); p_iplProbeBatchRelease(&s.probes); }
            s.probes = done;
            p_iplSimulatorAddProbeBatch(s.sim, s.probes);
            p_iplSimulatorCommit(s.sim);
            refl_thaw();
        }
        else p_iplProbeBatchRelease(&done);
    }
    int haveScene = s.scene != NULL;

    // the nearest playing voices own the per-source reflection pool; the rest share the room
    int order[MaxSlots], nOrd = 0;
    float dist[MaxSlots];
    for (int i = 0; i < MaxSlots; i++)
    {
        float dx = snap[i].pos[0] - lp[0], dy = snap[i].pos[1] - lp[1], dz = snap[i].pos[2] - lp[2];
        dist[i] = sqrtf(dx * dx + dy * dy + dz * dz);
        if (snap[i].src && !snap[i].stopped) order[nOrd++] = i;
    }
    for (int a = 1; a < nOrd; a++)
        for (int b = a; b > 0 && dist[order[b]] < dist[order[b - 1]]; b--) { int t = order[b]; order[b] = order[b - 1]; order[b - 1] = t; }
    int reflIdx[MaxSlots];
    for (int i = 0; i < MaxSlots; i++) reflIdx[i] = -1;
    if (cfg.reflections == 2 && haveScene)
    {
        int used[MaxRefl] = { 0 };
        int n = nOrd < MaxRefl ? nOrd : MaxRefl;
        for (int k = 0; k < n; k++)
            if (snap[order[k]].reflIdx >= 0 && snap[order[k]].reflIdx < MaxRefl && !used[snap[order[k]].reflIdx])
            { reflIdx[order[k]] = snap[order[k]].reflIdx; used[snap[order[k]].reflIdx] = 1; }
        for (int k = 0; k < n; k++)
            if (reflIdx[order[k]] < 0)
                for (int r = 0; r < MaxRefl; r++) if (!used[r]) { reflIdx[order[k]] = r; used[r] = 1; break; }
    }

    IPLSimulationSharedInputs sh;
    memset(&sh, 0, sizeof sh);
    sh.listener = listener_space(lp, lat, lup);
    sh.numRays = cfg.rays; sh.numBounces = cfg.bounces; sh.duration = cfg.duration < MaxDuration ? cfg.duration : MaxDuration;
    sh.order = s.order; sh.irradianceMinDistance = 1.0f;
    p_iplSimulatorSetSharedInputs(s.sim, (IPLSimulationFlags)(IPL_SIMULATIONFLAGS_DIRECT | IPL_SIMULATIONFLAGS_REFLECTIONS | IPL_SIMULATIONFLAGS_PATHING), &sh);
    int wantDirect = haveScene && cfg.occlusion, anyPath = 0;
    for (int i = 0; i < MaxSlots; i++)
    {
        const Slot& sl = snap[i];
        IPLSimulationInputs in;
        memset(&in, 0, sizeof in);
        int live = sl.src && !sl.stopped;
        int flags = 0;
        if (live && wantDirect) flags |= IPL_SIMULATIONFLAGS_DIRECT;
        if (live && cfg.pathing && s.probes) { flags |= IPL_SIMULATIONFLAGS_PATHING; anyPath = 1; }
        in.flags = (IPLSimulationFlags)flags;
        in.directFlags = (IPLDirectSimulationFlags)(wantDirect ? (IPL_DIRECTSIMULATIONFLAGS_OCCLUSION | (cfg.occlusion > 1 ? IPL_DIRECTSIMULATIONFLAGS_TRANSMISSION : 0)) : 0);
        float lifted[3] = { sl.pos[0], sl.pos[1], sl.pos[2] + SimLift };
        in.source.origin = ipl_of(lifted);
        in.source.ahead.z = -1.0f; in.source.up.y = 1.0f; in.source.right.x = 1.0f;
        in.distanceAttenuationModel.type = IPL_DISTANCEATTENUATIONTYPE_INVERSEDISTANCE;
        in.distanceAttenuationModel.minDistance = sl.nearD > 0.0f ? sl.nearD : 1.0f;
        in.airAbsorptionModel.type = IPL_AIRABSORPTIONTYPE_DEFAULT;
        in.occlusionType = IPL_OCCLUSIONTYPE_VOLUMETRIC;
        in.occlusionRadius = 0.75f;
        in.numOcclusionSamples = 16;
        in.numTransmissionRays = 3;
        in.reverbScale[0] = in.reverbScale[1] = in.reverbScale[2] = 1.0f;
        in.pathingProbes = s.probes;
        in.visRadius = 0.5f; in.visThreshold = 0.1f; in.visRange = 20.0f;
        in.pathingOrder = s.order;
        in.enableValidation = IPL_TRUE; in.findAlternatePaths = IPL_TRUE;
        in.deviationModel = &g_deviation;
        p_iplSourceSetInputs(s.src[i], (IPLSimulationFlags)(IPL_SIMULATIONFLAGS_DIRECT | IPL_SIMULATIONFLAGS_PATHING), &in);
        if (reflIdx[i] >= 0)
        {
            in.flags = live ? IPL_SIMULATIONFLAGS_REFLECTIONS : (IPLSimulationFlags)0;
            p_iplSourceSetInputs(s.reflSrc[reflIdx[i]], IPL_SIMULATIONFLAGS_REFLECTIONS, &in);
        }
    }
    for (int r = 0; r < MaxRefl; r++)
    {
        int owned = 0;
        for (int i = 0; i < MaxSlots; i++) if (reflIdx[i] == r) owned = 1;
        if (owned) continue;
        IPLSimulationInputs in;
        memset(&in, 0, sizeof in);
        in.deviationModel = &g_deviation;
        p_iplSourceSetInputs(s.reflSrc[r], IPL_SIMULATIONFLAGS_REFLECTIONS, &in);
    }
    int room = (cfg.reflections == 1 || cfg.reflections == 2) && haveScene && nOrd > 0;
    {
        IPLSimulationInputs in;
        memset(&in, 0, sizeof in);
        in.flags = room ? IPL_SIMULATIONFLAGS_REFLECTIONS : (IPLSimulationFlags)0;
        in.source.origin = ipl_of(lp);
        in.source.ahead.z = -1.0f; in.source.up.y = 1.0f; in.source.right.x = 1.0f;
        in.distanceAttenuationModel.type = IPL_DISTANCEATTENUATIONTYPE_DEFAULT;
        in.airAbsorptionModel.type = IPL_AIRABSORPTIONTYPE_DEFAULT;
        in.reverbScale[0] = in.reverbScale[1] = in.reverbScale[2] = 1.0f;
        in.deviationModel = &g_deviation;
        p_iplSourceSetInputs(s.room, IPL_SIMULATIONFLAGS_REFLECTIONS, &in);
    }
    if (wantDirect) p_iplSimulatorRunDirect(s.sim);
    double now = cxa_now(), r0 = now;
    int ranRefl = 0, ranPath = 0;
    if ((room || cfg.reflections == 2) && haveScene && nOrd > 0 && now - s.lastRefl > 0.25) { p_iplSimulatorRunReflections(s.sim); s.lastRefl = now; ranRefl = 1; }
    if (anyPath && now - s.lastPath > 0.5) { p_iplSimulatorRunPathing(s.sim); s.lastPath = now; ranPath = 1; }
    double reflMs = (cxa_now() - r0) * 1000.0;

    static IPLSimulationOutputs outs[MaxSlots];
    for (int i = 0; i < MaxSlots; i++)
        if (snap[i].src && !snap[i].stopped)
        {
            p_iplSourceGetOutputs(s.src[i], (IPLSimulationFlags)(IPL_SIMULATIONFLAGS_DIRECT | IPL_SIMULATIONFLAGS_PATHING), &outs[i]);
            if (reflIdx[i] >= 0) p_iplSourceGetOutputs(s.reflSrc[reflIdx[i]], IPL_SIMULATIONFLAGS_REFLECTIONS, &outs[i]);
        }
    IPLSimulationOutputs roomOut;
    memset(&roomOut, 0, sizeof roomOut);
    if (room) p_iplSourceGetOutputs(s.room, IPL_SIMULATIONFLAGS_REFLECTIONS, &roomOut);
    double ms = (cxa_now() - t0) * 1000.0;
    g_lk.on();
    for (int i = 0; i < MaxSlots; i++)
    {
        Slot& sl = g_slots[i];
        if (!snap[i].src || sl.src != snap[i].src || sl.gen != snap[i].gen) continue;
        sl.simGen = sl.gen;
        sl.occlusion = wantDirect ? outs[i].direct.occlusion : 1.0f;
        memcpy(sl.transmission, outs[i].direct.transmission, sizeof sl.transmission);
        sl.reflIdx = reflIdx[i];
        if (reflIdx[i] >= 0) sl.refl = outs[i].reflections;
        sl.pathValid = cfg.pathing && s.probes && outs[i].pathing.shCoeffs;
        if (sl.pathValid)
        {
            memcpy(sl.pathEq, outs[i].pathing.eqCoeffs, sizeof sl.pathEq);
            memcpy(sl.pathSh, outs[i].pathing.shCoeffs, sizeof(float) * (size_t)s.ambi);
        }
    }
    s.roomValid = room;
    if (room) s.roomParams = roomOut.reflections;
    g_stats.simMsAvg += ((float)ms - g_stats.simMsAvg) * 0.05f;
    if (ranRefl || ranPath) g_stats.reflMsAvg += ((float)reflMs - g_stats.reflMsAvg) * 0.2f;
    g_lk.off();
}

// ---- the pathing bake (CX's "CX sound bake" thread) ----
static void IPLCALL bake_progress(IPLfloat32 p, void* u) { (void)p; (void)u; }

/// "CX sound sim", every 15 ms: one simulation tick, unless the exit latch is set. The in-core count is what
/// cxa_shutdown waits on, so the thread is never inside Steam Audio once the call returns.
CX_EXPORT void cxa_sim_tick(void)
{
    if (cxa_ld(&g_stop)) return;
    cxa_inc(&g_inside);
    sim_tick();
    cxa_dec(&g_inside);
}

/// One bake if one is waiting: probes on a 4 m floor grid over the area, paths between every pair in sight.
/// 1 when it did work, 0 when idle.
static int32_t bake_tick(void)
{
    Sa& s = g_sa;
    if (!s.ready) return 0;
    IPLScene scene = NULL;
    IPLVector3 lo = { 0, 0, 0 }, hi = { 0, 0, 0 };
    int pathing;
    g_lk.on();
    pathing = g_cfg.pathing && g_cfg.enabled;
    if (g_bakeScene && pathing) { scene = g_bakeScene; g_bakeScene = NULL; lo = g_bakeMin; hi = g_bakeMax; g_baking = 1; g_stats.bakeState = 1; }
    g_lk.off();
    if (!scene) return 0;
    IPLProbeArray arr = NULL;
    IPLProbeBatch batch = NULL;
    int ok = 0, n = 0;
    if (p_iplProbeArrayCreate(s.ctx, &arr) == IPL_STATUS_SUCCESS && p_iplProbeBatchCreate(s.ctx, &batch) == IPL_STATUS_SUCCESS)
    {
        IPLProbeGenerationParams gp;
        memset(&gp, 0, sizeof gp);
        gp.type = IPL_PROBEGENERATIONTYPE_UNIFORMFLOOR;
        // at most ~MaxProbes over the floor: a 32x32-tile area at 4 m is ~6400 probes, and the path bake's memory
        // and time grow with pairs in range -- the unbounded bake held gigabytes and never finished (CX-57 live)
        float floorArea = (hi.x - lo.x) * (hi.z - lo.z);
        gp.spacing = fmaxf(4.0f, sqrtf(floorArea / (float)MaxProbes)); gp.height = 1.6f;
        // the unit cube centred on the origin, scaled and moved onto the scene's bounds (+2 m of headroom)
        gp.transform.elements[0][0] = hi.x - lo.x + 1.0f; gp.transform.elements[0][3] = (hi.x + lo.x) * 0.5f;
        gp.transform.elements[1][1] = hi.y - lo.y + 4.0f; gp.transform.elements[1][3] = (hi.y + lo.y) * 0.5f + 2.0f;
        gp.transform.elements[2][2] = hi.z - lo.z + 1.0f; gp.transform.elements[2][3] = (hi.z + lo.z) * 0.5f;
        gp.transform.elements[3][3] = 1.0f;
        p_iplProbeArrayGenerateProbes(arr, scene, &gp);
        p_iplProbeBatchAddProbeArray(batch, arr);
        p_iplProbeBatchCommit(batch);
        n = p_iplProbeBatchGetNumProbes(batch);
        if (n > 0)
        {
            IPLPathBakeParams bp;
            memset(&bp, 0, sizeof bp);
            bp.scene = scene; bp.probeBatch = batch;
            bp.identifier.type = IPL_BAKEDDATATYPE_PATHING;
            bp.identifier.variation = IPL_BAKEDDATAVARIATION_DYNAMIC;
            bp.numSamples = 1; bp.radius = 0.5f; bp.threshold = 0.1f; bp.visRange = 20.0f; bp.pathRange = 30.0f;
            bp.numThreads = 1;
            p_iplPathBakerBake(s.ctx, &bp, bake_progress, NULL);
            ok = 1;
        }
    }
    if (arr) p_iplProbeArrayRelease(&arr);
    p_iplSceneRelease(&scene);
    g_lk.on();
    int superseded = g_bakeScene != NULL;
    g_baking = 0;
    if (ok && !superseded)
    {
        if (g_bakeDone) p_iplProbeBatchRelease(&g_bakeDone);
        g_bakeDone = batch; batch = NULL;
        g_stats.probes = n; g_stats.bakeState = 2;
    }
    else if (!superseded) g_stats.bakeState = n == 0 ? 3 : 0;
    g_lk.off();
    if (batch) p_iplProbeBatchRelease(&batch);
    return 1;
}

/// "CX sound bake": one bake pass, unless the exit latch is set. <see cref="cxa_sim_tick"/> on the count.
CX_EXPORT int32_t cxa_bake_tick(void)
{
    if (cxa_ld(&g_stop)) return 0;
    cxa_inc(&g_inside);
    int32_t did = bake_tick();
    cxa_dec(&g_inside);
    return did;
}

// ---- the managed surface ----
/// Resolves every entry point from the phonon and OpenAL libraries managed loaded. 0 = ok, else the index of the
/// first missing symbol + 1 (and its name in cxa_error).
CX_EXPORT int32_t cxa_bind(void* phonon, void* openal)
{
    int k = 0;
#define CXA_RES(n) k++; if (!(p_##n = (decltype(p_##n))cxa_sym(phonon, #n))) { set_err("phonon lacks " #n); return k; }
    CXA_IPL(CXA_RES)
#undef CXA_RES
#define CXA_RES(t, n) k++; if (!(p_##n = (t)cxa_sym(openal, #n))) { set_err("OpenAL lacks " #n); return k; }
    CXA_AL(CXA_RES)
#undef CXA_RES
    return 0;
}

/// The shim that stands in for OpenAL export <paramref name="which"/> (SH_* order), and where funchook put the
/// original.
CX_EXPORT void* cxa_shim(int32_t which)
{
    switch (which)
    {
    case SH_BufferData: return (void*)sh_alBufferData;
    case SH_DeleteBuffers: return (void*)sh_alDeleteBuffers;
    case SH_Sourcef: return (void*)sh_alSourcef;
    case SH_SourcePlay: return (void*)sh_alSourcePlay;
    case SH_SourceStop: return (void*)sh_alSourceStop;
    case SH_SourcePause: return (void*)sh_alSourcePause;
    case SH_DeleteSources: return (void*)sh_alDeleteSources;
    case SH_Listenerfv: return (void*)sh_alListenerfv;
    case SH_CreateContext: return (void*)sh_alcCreateContext;
    case SH_MakeContextCurrent: return (void*)sh_alcMakeContextCurrent;
    default: return NULL;
    }
}
CX_EXPORT void cxa_set_original(int32_t which, void* fn)
{
    switch (which)
    {
    case SH_BufferData: o_alBufferData = (LPALBUFFERDATA)fn; break;
    case SH_DeleteBuffers: o_alDeleteBuffers = (LPALDELETEBUFFERS)fn; break;
    case SH_Sourcef: o_alSourcef = (LPALSOURCEF)fn; break;
    case SH_SourcePlay: o_alSourcePlay = (LPALSOURCEPLAY)fn; break;
    case SH_SourceStop: o_alSourceStop = (LPALSOURCESTOP)fn; break;
    case SH_SourcePause: o_alSourcePause = (LPALSOURCEPAUSE)fn; break;
    case SH_DeleteSources: o_alDeleteSources = (LPALDELETESOURCES)fn; break;
    case SH_Listenerfv: o_alListenerfv = (LPALLISTENERFV)fn; break;
    case SH_CreateContext: o_alcCreateContext = (LPALCCREATECONTEXT)fn; break;
    case SH_MakeContextCurrent: o_alcMakeContextCurrent = (LPALCMAKECONTEXTCURRENT)fn; break;
    default: break;
    }
}

/// The knobs. Live, except what the first bus fixed (binaural vs panning, the ambisonic order).
CX_EXPORT void cxa_config(const CxaConfig* c)
{
    ALuint back[MaxSlots];
    float gain[MaxSlots];
    int n = 0;
    if (cxa_ld(&g_stop)) return;
    g_lk.on();
    int off = g_cfg.enabled && !c->enabled;
    g_cfg = *c;
    // switched off live: every voice this path owns goes back to OpenAL at the engine's gain, and plays on vanilla
    if (off)
        for (int i = 0; i < MaxSlots; i++)
            if (g_slots[i].src)
            {
                back[n] = g_slots[i].src; gain[n++] = g_slots[i].gain;
                blob_unref(g_slots[i].blob);
                g_slots[i].blob = NULL; g_slots[i].src = 0; g_slots[i].stopped = 1;
            }
    g_lk.off();
    for (int i = 0; i < n; i++) { o_alSourcef(back[i], AL_GAIN, gain[i]); o_alSourcef(back[i], AL_PITCH, 1.0f); }
    fx_apply();
}

/// The resampler OpenAL uses for the sources it still plays, by its exact AL_RESAMPLER_NAME_SOFT; "" = OpenAL's
/// default. Returns the index chosen, -1 for the default, -2 when the name is not offered (the default stays).
/// Needs a current context.
CX_EXPORT int32_t cxa_resampler(const char* name)
{
    g_resampler = -1;
    if (!name || !*name || !p_alGetStringiSOFT || !p_alcGetCurrentContext() || cxa_ld(&g_stop)) return -1;
    ALint n = p_alGetInteger(AL_NUM_RESAMPLERS_SOFT);
    for (ALint i = 0; i < n; i++)
    {
        const ALchar* r = p_alGetStringiSOFT(AL_RESAMPLER_NAME_SOFT, i);
        if (r && strcmp(r, name) == 0) { g_resampler = i; return i; }
    }
    return -2;
}

/// Opens the bus on the context the engine already made current -- the one CX's makeCurrent hook missed when the
/// engine's sound init ran before the hooks were in. Main thread.
CX_EXPORT void cxa_attach(void)
{
    if (p_alcGetCurrentContext && g_cfg.enabled && !cxa_ld(&g_stop)) bus_open(p_alcGetCurrentContext());
}

/// The speaker layout asked of OpenAL by channel count; 0 = what the system reports.
static ALCint output_mode(int channels)
{
    switch (channels)
    {
    case 2: return ALC_STEREO_BASIC_SOFT;
    case 4: return ALC_QUAD_SOFT;
    case 6: return ALC_SURROUND_5_1_SOFT;
    case 8: return ALC_SURROUND_7_1_SOFT;
    default: return ALC_ANY_SOFT;
    }
}

/// Device-level choices: taken by the next context the engine makes, and by the live device through
/// alcResetDeviceSOFT when they changed; the listener gain whenever a context exists. Main thread.
/// <paramref name="channels"/>: the speaker layout (0 = the system's); the bus fixes its own at the first open.
/// The device and the context it reaches for are only valid while OpenAL has a current context.
CX_EXPORT void cxa_device(int32_t hrtf, int32_t limiter, float master, int32_t channels)
{
    if (cxa_ld(&g_stop)) return;
    ALCint h = hrtf < 0 ? ALC_DONT_CARE_SOFT : hrtf ? ALC_TRUE : ALC_FALSE, l = limiter ? ALC_TRUE : ALC_FALSE;
    ALCint m = output_mode(channels);
    int changed = h != g_devHrtf || l != g_devLimiter || m != g_devMode;
    g_devHrtf = h; g_devLimiter = l; g_devMode = m;
    int live = p_alcGetCurrentContext && p_alcGetCurrentContext() != NULL;
    if (changed && live && g_busDev && p_alcResetDeviceSOFT)
    {
        ALCint a[] = { ALC_HRTF_SOFT, g_devHrtf, ALC_OUTPUT_LIMITER_SOFT, g_devLimiter, ALC_OUTPUT_MODE_SOFT, g_devMode, 0 };
        p_alcResetDeviceSOFT(g_busDev, a);
    }
    if (live && g_busCtx && p_alListenerf) p_alListenerf(AL_GAIN, master);
}

/// The speaker test: a burst from ahead, right, behind and left of the listener. 0 when no bus plays it.
CX_EXPORT int32_t cxa_test_tone(void)
{
    if (cxa_ld(&g_stop)) return 0;
    g_lk.on();
    int ok = g_stats.busOpen && g_cfg.enabled;
    if (ok) g_testReq = 1;
    g_lk.off();
    return ok;
}

/// 1 while OpenAL has a current context AND CX's audio has not been stood down: the engine's stream calls check
/// AL errors, and every one made after the engine drops its context at exit logs AL_INVALID_OPERATION.
CX_EXPORT int32_t cxa_al_live(void)
{
    return !cxa_ld(&g_stop) && p_alcGetCurrentContext && p_alcGetCurrentContext() != NULL;
}

/// 1 once the exit latch is set: CX's own loops read it to leave.
CX_EXPORT int32_t cxa_stopped(void) { return cxa_ld(&g_stop); }

/// The engine is tearing its sound down (CExoSoundInternal::ShutDown, before alcDestroyContext): CX's audio
/// stands down for good. Latches every entry point off, cancels a running path bake and waits, bounded, for
/// CX's own threads to leave the core -- so nothing of ours is inside Steam Audio or OpenAL while the process
/// finalizes. 1 when they all left, 0 when the wait ran out. Main thread, once.
CX_EXPORT int32_t cxa_shutdown(void)
{
    cxa_st(&g_stop, 1);
    g_lk.on();
    int baking = g_baking && g_sa.ctx && p_iplPathBakerCancelBake;
    if (baking) p_iplPathBakerCancelBake(g_sa.ctx);
    g_lk.off();
    if (cxa_ld(&g_renderLive)) g_wake.post();
    for (int i = 0; i < ShutdownWaitMs && cxa_ld(&g_inside) > 0; i++) cxa_sleep_ms(1);
    return cxa_ld(&g_inside) == 0;
}

/// The area music's audible level (0 silent .. 1 full), for the ambience duck. Main thread, per frame.
CX_EXPORT void cxa_music(float level)
{
    if (cxa_ld(&g_stop)) return;
    g_lk.on();
    g_music = clampf(level, 0.0f, 1.0f);
    g_lk.off();
}

/// The reverb's wet scale while it rains (1 dry .. 0): rain soaks up echoes. Main thread, on change.
CX_EXPORT void cxa_rain(float wet)
{
    if (cxa_ld(&g_stop)) return;
    g_lk.on();
    g_rain = clampf(wet, 0.0f, 1.0f);
    g_lk.off();
    fx_apply();
}

/// The engine's 3D start, bracketed by the PlaySourceOn3DVoice hook on the thread that makes it.
CX_EXPORT void cxa_begin3d(const CxaMeta* m) { if (cxa_ld(&g_stop)) return; t_meta = *m; t_pending = 1; }
CX_EXPORT void cxa_end3d(void) { t_pending = 0; }

/// CExoSoundSourceInternal::SetPosition for a playing 3D voice: the world position, not the z-negated one the
/// engine forwards to AL on this path.
CX_EXPORT void cxa_move(uint32_t src, float x, float y, float z)
{
    if (cxa_ld(&g_stop)) return;
    g_lk.on();
    int i = slot_of(src);
    if (i >= 0) { g_slots[i].pos[0] = x; g_slots[i].pos[1] = y; g_slots[i].pos[2] = z; }
    g_lk.off();
}

/// soundeax.2da's row for the area (CExoSound::SetEnvironment); -1 clears.
CX_EXPORT void cxa_environment(int32_t row)
{
    if (cxa_ld(&g_stop)) return;
    g_envRow = row;
    fx_apply();
}
CX_EXPORT int32_t cxa_preset_row(const char* label) { return preset_row(label); }

/// The area's static geometry, NWN world space, copied. The sim thread swaps it in at its next tick; 0 triangles
/// clears the scene. <paramref name="bake"/>: the area's first send, which (re)starts the pathing bake.
CX_EXPORT int32_t cxa_scene(const float* verts, int32_t nv, const int32_t* tris, int32_t nt, int32_t bake, int32_t material)
{
    float* v = NULL;
    int32_t* t = NULL;
    if (cxa_ld(&g_stop)) return 0;
    if (nv > 0 && nt > 0)
    {
        v = (float*)malloc(sizeof(float) * 3 * (size_t)nv);
        t = (int32_t*)malloc(sizeof(int32_t) * 3 * (size_t)nt);
        if (!v || !t) { free(v); free(t); return 0; }
        memcpy(v, verts, sizeof(float) * 3 * (size_t)nv);
        memcpy(t, tris, sizeof(int32_t) * 3 * (size_t)nt);
    }
    g_lk.on();
    free(g_pendVerts); free(g_pendTris);
    // bake is sticky while a set waits: a send that overtakes the area edge's must not swallow the edge's
    // probe teardown, or the new area paths through the last one's probes
    g_pendBake = g_pendSet ? (g_pendBake || bake) : bake;
    g_pendVerts = v; g_pendTris = t; g_pendNv = v ? nv : 0; g_pendNt = t ? nt : 0; g_pendSet = 1;
    g_pendMat = material >= 0 && material < AcMatCount ? material : 0;
    g_lk.off();
    return 1;
}

CX_EXPORT void cxa_stats(CxaStats* out)
{
    g_lk.on();
    *out = g_stats;
    g_stats.renderMsMax = 0.0f;
    g_stats.captureMsMax = 0.0f;
    g_lk.off();
}

CX_EXPORT int32_t cxa_error(char* buf, int32_t len)
{
    if (!buf || len <= 0) return 0;
    snprintf(buf, (size_t)len, "%s", g_err);
    return (int32_t)strlen(buf);
}

CX_EXPORT int32_t cxa_ready(void) { return !cxa_ld(&g_stop) && g_sa.ready; }

/// What OpenAL says it is, filled when the bus opens; "" before.
CX_EXPORT int32_t cxa_describe(char* buf, int32_t len)
{
    if (!buf || len <= 0) return 0;
    snprintf(buf, (size_t)len, "%s", g_describe);
    return (int32_t)strlen(buf);
}

// ---- SelfCheck: the pure pieces, headless ----
#define CXA_EXPECT(c, what) do { if (!(c)) { snprintf(msg, (size_t)len, "%s", what); return 0; } } while (0)
static int near_(float a, float b, float e) { return fabsf(a - b) <= e; }

/// 1 when every check passes; else 0 with the failing check's name in <paramref name="msg"/>.
CX_EXPORT int32_t cxa_selfcheck(char* msg, int32_t len)
{
    // the vanilla curve is the engine's AL_LINEAR_DISTANCE_CLAMPED from 1 m
    CXA_EXPECT(near_(dist_gain(0, 0.5f, 3, 30, 1, 1), 1.0f, 1e-5f), "vanilla curve full inside 1 m");
    CXA_EXPECT(near_(dist_gain(0, 15.0f, 3, 30, 1, 1), 1.0f - 14.0f / 29.0f, 1e-5f), "vanilla curve linear");
    CXA_EXPECT(dist_gain(0, 40.0f, 3, 30, 1, 1) == 0.0f, "vanilla curve silent past far");
    // the natural curve: flat to the authored near radius, -6 dB per doubling after
    CXA_EXPECT(near_(dist_gain(1, 2.0f, 3, 30, 1, 1), 1.0f, 1e-5f), "natural curve flat inside near");
    CXA_EXPECT(near_(dist_gain(1, 6.0f, 3, 30, 1, 1), 0.5f, 1e-5f), "natural curve halves at twice near");
    CXA_EXPECT(near_(dist_gain(1, 15.0f, 3, 30, 1, 1), 0.2f, 1e-5f), "anti-scry case: 15 m of a 3 m sound is -14 dB");
    CXA_EXPECT(dist_gain(1, 30.0f, 3, 30, 1, 0.75f) == 0.0f, "taper reaches silence at far");
    CXA_EXPECT(near_(dist_gain(1, 22.0f, 3, 30, 1, 0.75f), dist_gain(1, 22.0f, 3, 30, 1, 1), 1e-6f), "no taper before its start");
    // the engine's variance draw: [1 - v/2, 1 + v]
    uint32_t rng = 7;
    float lo = 9, hi = 0;
    for (int i = 0; i < 20000; i++) { float p = variance_pitch(0.3f, &rng); lo = fminf(lo, p); hi = fmaxf(hi, p); }
    CXA_EXPECT(lo >= 0.85f - 1e-4f && lo < 0.86f && hi <= 1.3f + 1e-4f && hi > 1.29f, "variance pitch spans the engine's range");
    CXA_EXPECT(variance_pitch(0.0f, &rng) == 1.0f, "no variance, no pitch");
    // NWN -> Steam Audio axes, and the listener frame: facing north, east is right, north is ahead, up is up
    float n[3] = { 1, 2, 3 };
    IPLVector3 v = ipl_of(n);
    CXA_EXPECT(v.x == 1 && v.y == 3 && v.z == -2, "nwn->ipl axes");
    float lp[3] = { 10, 10, 0 }, at[3] = { 0, 1, 0 }, up[3] = { 0, 0, 1 };
    float east[3] = { 20, 10, 0 }, north[3] = { 10, 20, 0 }, above[3] = { 10, 10, 5 };
    IPLVector3 d = rel_dir(east, lp, at, up);
    CXA_EXPECT(near_(d.x, 1, 1e-5f) && near_(d.y, 0, 1e-5f) && near_(d.z, 0, 1e-5f), "east of a north-facing listener is right");
    d = rel_dir(north, lp, at, up);
    CXA_EXPECT(near_(d.z, -1, 1e-5f), "north of a north-facing listener is ahead");
    d = rel_dir(above, lp, at, up);
    CXA_EXPECT(near_(d.y, 1, 1e-5f), "above is up");
    float at2[3] = { 1, 0, 0 };   // facing east, the northern source is on the left
    d = rel_dir(north, lp, at2, up);
    CXA_EXPECT(near_(d.x, -1, 1e-5f), "facing east, north is left");
    IPLCoordinateSpace3 cs = listener_space(lp, at, up);
    CXA_EXPECT(near_(cs.right.x, 1, 1e-5f) && near_(cs.up.y, 1, 1e-5f) && near_(cs.ahead.z, -1, 1e-5f), "listener space basis");
    // the resampler reproduces a line exactly and wraps a loop
    float ramp[8] = { 0, 1, 2, 3, 4, 5, 6, 7 };
    CXA_EXPECT(near_(sample_at(ramp, 8, 0, 3.5), 3.5f, 1e-5f), "cubic reproduces a line");
    CXA_EXPECT(near_(sample_at(ramp, 8, 1, 8.0), 0.0f, 1e-5f), "loop wraps to the start");
    CXA_EXPECT(sample_at(ramp, 8, 0, 20.0) == 0.0f, "one-shot is silent past its end");
    // the shelf leaves DC alone and moves Nyquist by its gain
    Biquad q;
    shelf_design(&q, 48000.0f, 5000.0f, -6.0f);
    CXA_EXPECT(near_((q.b0 + q.b1 + q.b2) / (1 + q.a1 + q.a2), 1.0f, 1e-3f), "shelf DC gain 1");
    CXA_EXPECT(near_((q.b0 - q.b1 + q.b2) / (1 - q.a1 + q.a2), db2lin(-6.0f), 1e-3f), "shelf Nyquist gain");
    // capture: 16-bit stereo averages to mono, a sound is louder than its own tail of silence
    int16_t pcm[400];
    for (int i = 0; i < 200; i++) { pcm[i * 2] = 16384; pcm[i * 2 + 1] = 0; }
    Blob* b = blob_make(AL_FORMAT_STEREO16, pcm, sizeof pcm, 1000);
    CXA_EXPECT(b && b->frames == 200 && near_(b->pcm[0], 0.25f, 1e-5f), "stereo16 -> mono");
    CXA_EXPECT(near_(b->loudDb, 20.0f * log10f(0.25f), 0.01f), "gated loudness of a constant");
    uint64_t k1 = b->key;
    blob_unref(b);
    b = blob_make(AL_FORMAT_STEREO16, pcm, sizeof pcm, 1000);
    CXA_EXPECT(b && b->key == k1, "the same sound keys the same");
    blob_unref(b);
    uint8_t m8[4] = { 128, 255, 0, 128 };
    b = blob_make(AL_FORMAT_MONO8, m8, 4, 8000);
    CXA_EXPECT(b && near_(b->pcm[0], 0.0f, 1e-6f) && b->pcm[2] == -1.0f, "mono8 is unsigned");
    blob_unref(b);
    CXA_EXPECT(blob_make(AL_FORMAT_MONO_FLOAT32, pcm, 8, 1000) == NULL, "unknown formats are not captured");
    // loudness toward the target, capped
    CXA_EXPECT(near_(norm_db(-30, -20, 0.5f), 5.0f, 1e-5f) && near_(norm_db(-60, -20, 1.0f), 12.0f, 1e-5f), "normalisation step and cap");
    // copies in lockstep: by read position, around a loop's wrap, not for a one-shot
    CXA_EXPECT(lockstep(100, 150, 1000, 0, 100) && !lockstep(100, 400, 1000, 0, 100), "lockstep by position");
    CXA_EXPECT(lockstep(990, 20, 1000, 1, 100) && !lockstep(990, 20, 1000, 0, 100), "a loop's lockstep wraps");
    CXA_EXPECT(cat_of(3) == CatAmbient && cat_of(17) == CatFootsteps && cat_of(13) == CatCombat && cat_of(21) == CatOther, "categories");
    CXA_EXPECT(pitch_exempt(6) && pitch_exempt(7) && !pitch_exempt(13), "chat is never pitched");
    // the EnvAudio join is by label
    CXA_EXPECT(PresetCount == 113, "113 soundeax rows");
    CXA_EXPECT(preset_row("CASTLE_LONGPASSAGE") == 29 && preset_row("CASTLE_LARGEROOM") == 30, "the 2DA's order, not the header's");
    CXA_EXPECT(preset_row("CARPETTEDHALLWAY") == 11 && g_presets[11].p.flDecayTime == 0.3f, "the 2DA's spelling reaches the header's preset");
    // the scene swap's handoff: every simulator-owned IR leaves the render's hands, and the freeze keeps the
    // next frame from taking one (nothing here touches Steam Audio, so it runs headless)
    g_slots[3].reflIdx = 2;
    g_slots[3].refl.ir = (IPLReflectionEffectIR)(uintptr_t)1;
    g_sa.roomValid = 1;
    g_sa.roomParams.ir = (IPLReflectionEffectIR)(uintptr_t)1;
    g_reflHold = 0;
    refl_quiesce();
    CXA_EXPECT(g_reflFreeze && g_slots[3].reflIdx < 0 && !g_slots[3].refl.ir && !g_sa.roomValid && !g_sa.roomParams.ir,
               "a scene swap takes every simulator IR out of the render's hands");
    g_reflFreeze = 0;
    // the exit latch: once it is set, an entry point that would otherwise change state does nothing. Oracle:
    // the two values written here by hand, which the unlatched calls below then overwrite. Runs before
    // cxa_bind, so nothing is loaded, no thread is up, and the latch is the selfcheck's own to set and clear.
    g_envRow = 7;
    g_music = 0.37f;
    cxa_st(&g_stop, 1);
    cxa_environment(42);
    cxa_music(1.0f);
    CXA_EXPECT(cxa_stopped() && g_envRow == 7 && g_music == 0.37f && cxa_al_live() == 0 && cxa_ready() == 0
               && cxa_bake_tick() == 0 && cxa_test_tone() == 0, "the exit latch makes every entry point a no-op");
    cxa_st(&g_stop, 0);
    cxa_environment(-1);
    cxa_music(0.0f);
    CXA_EXPECT(!cxa_stopped() && g_envRow == -1 && g_music == 0.0f, "unlatched, the same two calls land");
    snprintf(msg, (size_t)len, "ok");
    return 1;
}
