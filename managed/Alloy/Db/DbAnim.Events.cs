using System;
using System.Runtime.InteropServices;
using CX.Alloy.Mdl;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The event path: <c>AnimateEvents</c> @0x497f30's window and the three lifecycle names, raised at
/// the object's own callback list through <c>Gob::DoEvent</c> @0x49d760 exactly as the engine does -- footsteps,
/// hit sounds, weapon swaps, detonation and the blur all ride on it.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>Raise <paramref name="name"/> at the object's callbacks. The list is the ENGINE's own, on the
    /// object's facade, so this hands the event to seam-obj's <c>Gob::DoEvent</c> entry -- which carries the
    /// <c>UpdateScenes</c> propagation as well as the dispatch. An object the game holds no facade for has no
    /// callbacks either, so the event reaches nobody, counted on <c>[dbanim]</c>.</summary>
    private static void EventFire(DbId obj, byte* name, nint data)
    {
        if (!SceneDb.Objects.Valid(obj) || name == null) return;
        // A handler may play an animation, whose eviction announces a `done` to the same handler, which plays
        // another... The engine has that shape too and answers it with a stack overflow; a named refusal is
        // the better answer, and 16 is far past anything content does on purpose.
        if (_eventDepth >= MaxEventDepth)
        {
            _eventStorms++;
            if (!_eventStormSaid)
            {
                _eventStormSaid = true;
                _log?.Always($"DbAnim: an animation event handler re-entered the event path {MaxEventDepth} "
                           + "deep (a handler that plays an animation whose own events it hears) -- the chain "
                           + "is REFUSED here rather than overflowing the stack. Counted on [dbanim].");
            }
            return;
        }
        int i = obj.Index;
        _eventDepth++;
        try
        {
            // THE one list: the game registers through Gob::RegisterCallBack, seam-obj's slot 11, which keeps
            // the engine's own CExoArrayList<CallBack*> at the shell's +0x460. An event delivered to a list of
            // ours would reach nobody.
            nint shell = SceneDb.ObjFacade[i];
            if (shell == 0) { _noFacade++; return; }
            _fired++;
            CX.Alloy.Seam.ObjectSlots.FireEvent(shell, (nint)name, data);
        }
        finally { _eventDepth--; }
    }

    /// <summary>How deep a handler may re-enter the event path, and how often it has been refused.</summary>
    private const int MaxEventDepth = 16;
    private static int _eventDepth, _eventStorms, _noFacade;
    /// <summary>Events handed to a callback list this period, and ones whose object had no facade.</summary>
    private static int _fired;
    private static bool _eventStormSaid;

    // ================= the animation's own events =================

    /// <summary>
    /// <c>AnimateEvents</c> @0x497f30: every event of the run's animation in this step's window, which is
    /// HALF-OPEN at the low end -- forward <c>(prev, now]</c>, reverse <c>[now, prev)</c> -- with a looping
    /// wrap firing the cycle's TAIL then its head, in the animation's own array order rather than by time.
    /// <para>Every advanced run fires, whatever its blend weight; a handler may destroy the object, so the walk re-checks.</para>
    /// </summary>
    private static bool FireEvents(DbId obj, AnimDef anim, float from, float to, bool reverse)
    {
        AnimEvent[] ev = anim.Events;
        if (ev.Length == 0) return true;
        AnimNames? names = Names(anim);
        if (names == null) return true;
        if (!reverse)
        {
            if (from > to)
            {
                // The wrap, as the engine's tail recursion does it: the tail of the cycle, then the head.
                if (!FireWindow(obj, ev, names, from, Far, false)) return false;
                from = Near;
            }
            return FireWindow(obj, ev, names, from, to, false);
        }
        if (from < to)
        {
            if (!FireWindow(obj, ev, names, from, Near, true)) return false;
            from = Far;
        }
        return FireWindow(obj, ev, names, from, to, true);
    }

    /// <summary>The engine's "past either end" sentinels, used verbatim so a wrapped window splits at the same
    /// places.</summary>
    private const float Far = 1e9f, Near = -1e9f;

    private static bool FireWindow(DbId obj, AnimEvent[] ev, AnimNames names, float from, float to, bool reverse)
    {
        if (!reverse)
        {
            for (int i = 0; i < ev.Length; i++)
            {
                float at = ev[i].Time;
                if (!(at <= to && from < at)) continue;
                EventFire(obj, names.Event(i), 0);
                if (!SceneDb.Objects.Valid(obj)) return false;
            }
            return true;
        }
        for (int i = ev.Length - 1; i >= 0; i--)
        {
            float at = ev[i].Time;
            if (!(to <= at && at < from)) continue;
            EventFire(obj, names.Event(i), 0);
            if (!SceneDb.Objects.Valid(obj)) return false;
        }
        return true;
    }

    // ================= the three lifecycle events =================

    private const int StartPrefix = 0, LoopPrefix = 1, DonePrefix = 2;
    private static int _starts, _loops, _dones;

    /// <summary>One lifecycle event: <c>"start"</c>, <c>"loop"</c> or <c>"done"</c> followed by the animation's
    /// name, as the engine's <c>sprintf</c> builds it. The three names are built once per animation and kept
    /// with it, because this is per RUN per frame on every animating creature.</summary>
    private static void Lifecycle(DbId obj, int which, AnimDef? anim)
    {
        if (anim == null || !SceneDb.Objects.Valid(obj)) return;
        switch (which)
        {
            case StartPrefix: _starts++; break;
            case LoopPrefix: _loops++; break;
            default: _dones++; break;
        }
        AnimNames? names = Names(anim);
        if (names == null) return;
        EventFire(obj, names.Life(which), 0);
    }

    /// <summary>The native, NUL-terminated names one animation needs: its three lifecycle names and one per
    /// keyed event. Built on the animation's first event and held with it, so the event path allocates nothing
    /// per frame and the <c>char*</c> a handler receives is stable for as long as the animation is.</summary>
    private sealed class AnimNames
    {
        private readonly nint _life0, _life1, _life2, _token;
        private readonly nint[] _events;

        internal AnimNames(AnimDef anim)
        {
            _life0 = Make("start" + anim.Name);
            _life1 = Make("loop" + anim.Name);
            _life2 = Make("done" + anim.Name);
            _events = new nint[anim.Events.Length];
            for (int i = 0; i < _events.Length; i++) _events[i] = Make(anim.Events[i].Name ?? "");
            // The opaque non-null `void*` Gob::GetAnimation returns. ZEROED and a whole page-ish block, so a
            // holder that dereferences it reads zeros instead of faulting or reading something else's bytes.
            _token = (nint)NativeMemory.AllocZeroed(TokenSize);
        }

        /// <summary>The engine's <c>Animation</c> is 0xe8 bytes; the token is sized past it so a reader that
        /// treats it as one stays inside the block.</summary>
        private const int TokenSize = 0x100;   // not-an-offset: a size, past the engine Animation's 0xe8

        internal byte* Life(int which) => (byte*)(which == StartPrefix ? _life0 : which == LoopPrefix ? _life1 : _life2);
        internal byte* Event(int i) => (byte*)_events[i];
        internal nint Token => _token;

        // NO FINALIZER, deliberately. The game holds the Gob::GetAnimation token as an opaque void* for as
        // long as it likes, and a handler may still be holding the char* it was called with; both outlive the
        // AnimDef the model cache evicts. Freeing them from a finalizer -- on the finalizer thread, at a
        // moment nothing here chooses -- is a use-after-free in game code that would read as a random crash.
        // These are session-lived interns instead: bounded by the distinct animations a session ever plays
        // (about 400 bytes each), counted, and said once past the ceiling.

        /// <summary>The engine's own bound: a lifecycle name is built in a 0x40-byte <c>sprintf</c> buffer and
        /// an event name is a 0x20-byte inline array, so neither can be longer than the engine would deliver.
        /// Single-byte, which is what the client's text codepage is (<c>Encoding::FromUTF8</c>).</summary>
        private static nint Make(string s)
        {
            int n = Math.Min(s.Length, NameMax - 1);
            byte* p = (byte*)NativeMemory.AllocZeroed((nuint)(n + 1));
            for (int i = 0; i < n; i++) p[i] = (byte)s[i];
            return (nint)p;
        }
    }

    /// <summary>A STRONG table, for the reason above: the native names and the token must outlive the
    /// <see cref="AnimDef"/>, because the game holds them. Keyed on the animation's identity so two models'
    /// same-named animations keep their own event names.</summary>
    private static readonly System.Collections.Generic.Dictionary<AnimDef, AnimNames> _names =
        new(ReferenceEqualityComparer.Instance);
    /// <summary>Distinct animations whose names may be interned. Past this the lifecycle and keyframe events
    /// of a new animation cannot be named, so they do not fire -- said once, counted, never silent. A long
    /// session plays a few hundred.</summary>
    private const int MaxAnimNames = 8192;
    private static bool _namesFullSaid;
    private static int _namesRefused;

    private static AnimNames? Names(AnimDef anim)
    {
        if (_names.TryGetValue(anim, out AnimNames? got)) return got;
        if (_names.Count >= MaxAnimNames)
        {
            _namesRefused++;
            if (!_namesFullSaid)
            {
                _namesFullSaid = true;
                _log?.Always($"DbAnim: {MaxAnimNames} distinct animations have interned their event names -- "
                           + "a further animation's events and start/loop/done will NOT fire. Raise "
                           + "DbAnim.MaxAnimNames; counted on [dbanim].");
            }
            return null;
        }
        var made = new AnimNames(anim);
        _names[anim] = made;
        return made;
    }

    /// <summary>The opaque token <c>Gob::GetAnimation</c> answers with for this animation.</summary>
    private static nint Token(AnimDef anim) => Names(anim)?.Token ?? 0;

    /// <summary>The event path's period numbers.</summary>
    internal static string EventPeriod()
        => $"{_fired} callback(s) fired, {_starts} start, {_loops} loop, {_dones} done"
         + (_noFacade == 0 ? "" : $", {_noFacade} raised at an object with no facade (no listener)")
         + (_namesRefused == 0 ? "" : $", {_namesRefused} animation(s) past the name-intern ceiling (events NOT FIRED)")
         + (_eventStorms == 0 ? "" : $", {_eventStorms} event chain(s) REFUSED past depth {MaxEventDepth}");
}
