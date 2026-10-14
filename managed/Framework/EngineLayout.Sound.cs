namespace CX.Framework;

/// <summary>The sound-teardown rows of <see cref="EngineLayout"/>: what CX writes to stand the engine's own
/// streaming thread down before its OpenAL context dies.</summary>
public static unsafe partial class EngineLayout
{
    // ================= CExoSoundStreamThread =================
    // Laid out per OS, so each half was read off its own three bodies. Linux (ctor @0x675630, dtor @0x67a440,
    // AsyncSoundStreamThreadProc @0x677730): a pthread_t, a pthread_cond_t and a pthread_mutex_t precede the
    // flag. Windows (name-exported, ctor rva 0x1da9d0, dtor rva 0x1daf10, thread proc rva 0x1db400): a thread
    // handle at +0 and a CRITICAL_SECTION at +0x10 precede it, and the proc reads the global's own +0x18.

    /// <summary>CExoSoundStreamThread -&gt; the int its detached thread loop spins on: zero means keep going.
    /// The constructor zeroes it and only the destructor -- an <c>atexit</c> that never joins the thread --
    /// writes 1, which is why CX writes it first.</summary>
    public static readonly int StreamThreadStop = Field<int>("CExoSoundStreamThread", "stop");
}
