using System;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>The run queue: <c>Gob::PlayAnimation</c> @0x49a750 and the queue advance inside
/// <c>Gob::Animate</c> @0x49d8c0, ported onto the pool in <c>DbAnim.cs</c>.
/// <para>Index 0 is the NEWEST run and the advance walks <c>n-1</c> down to 0, so it writes its pose last and
/// dominates: <see cref="DbAnim.Control"/> lerps into the column the previous run left, with no pose buffer.</para></summary>
public static unsafe partial class DbAnim
{
    /// <summary>The engine's "no previous time" sentinel for a run that has not been stepped.</summary>
    private const float NoPrev = -1e7f;

    private static int Slot(int i, int k) => _block[i] * MaxRuns + k;

    /// <summary>Has every run on <paramref name="obj"/> finished blending in, so its light values are the
    /// animation's own and not a point on the ramp? A queued run is skipped; a fading-out run or one below
    /// full weight is not settled.</summary>
    public static bool Settled(DbId obj)
    {
        if (!SceneDb.Objects.Valid(obj)) return true;
        int i = obj.Index, n = _runN[i];
        for (int k = 0; k < n; k++)
        {
            int s = Slot(i, k);
            if (_rAnim[s] == null) continue;
            int fl = _rFlags[s];
            if ((fl & RunFlags.Queued) != 0) continue;
            if ((fl & RunFlags.FadeOut) != 0 || _rBlend[s] < 1f) return false;
        }
        return true;
    }

    /// <summary>Take a run block for this object, or -1 when the pool is empty (said once, counted; the play is
    /// refused rather than quietly dropped).</summary>
    private static int TakeBlock(int i)
    {
        if (_block[i] >= 0) return _block[i];
        int b;
        if (_blockHead >= 0) { b = _blockHead; _blockHead = _blockFree[b]; }
        else if (_blockHigh < MaxBlocks) b = _blockHigh++;
        else
        {
            _noBlock++;
            if (_noBlock == 1)
                _log?.Always($"DbAnim: all {MaxBlocks} animation run blocks are taken -- this object will not "
                           + "animate. Raise DbAnim.MaxBlocks; counted as REFUSED play(s) on [dbanim].");
            return -1;
        }
        _block[i] = b;
        _runN[i] = 0;
        return b;
    }

    private static void GiveBlock(int i)
    {
        int b = _block[i];
        if (b < 0) return;
        for (int k = 0; k < MaxRuns; k++) _rAnim[b * MaxRuns + k] = null;
        _blockFree[b] = _blockHead;
        _blockHead = b;
        _block[i] = -1;
        _runN[i] = 0;
    }

    private static void ClearQueue(int i)
    {
        _runN[i] = 0;
        GiveBlock(i);
    }

    /// <summary>Drop run <paramref name="k"/>, closing the gap so index 0 stays the newest
    /// (<c>CExoArrayList&lt;AnimRun*&gt;::Remove</c>, which shifts rather than swapping).
    /// <paramref name="count"/> is false on the reuse path, where the run is taken out and put back rather
    /// than dropped -- counting it there overstates the period line's drops.</summary>
    private static void Remove(int i, int k, bool count = true)
    {
        int n = _runN[i];
        if (k < 0 || k >= n) return;
        int b = _block[i] * MaxRuns;
        for (int j = k; j < n - 1; j++)
        {
            _rAnim[b + j] = _rAnim[b + j + 1];
            _rRate[b + j] = _rRate[b + j + 1]; _rPrev[b + j] = _rPrev[b + j + 1];
            _rTime[b + j] = _rTime[b + j + 1]; _rBlend[b + j] = _rBlend[b + j + 1];
            _rFade[b + j] = _rFade[b + j + 1]; _rPosScale[b + j] = _rPosScale[b + j + 1];
            _rFlags[b + j] = _rFlags[b + j + 1]; _rDone[b + j] = _rDone[b + j + 1];
        }
        _rAnim[b + n - 1] = null;
        _runN[i] = (byte)(n - 1);
        if (count) _dropped++;
        if (_runN[i] == 0) GiveBlock(i);
    }

    /// <summary>Open a slot at <paramref name="k"/> (<c>Insert</c>). The oldest run is dropped when the queue is
    /// already full -- counted and said once.</summary>
    private static bool Insert(int i, int k)
    {
        if (TakeBlock(i) < 0) return false;
        int n = _runN[i];
        if (n >= MaxRuns)
        {
            _queueFull++;
            if (_queueFull == 1)
                _log?.Always($"DbAnim: an object stacked more than {MaxRuns} animation runs -- the OLDEST is "
                           + "dropped (its pose contribution is gone). Counted on [dbanim] as queue overflow(s).");
            n = MaxRuns - 1;
            _runN[i] = (byte)n;
        }
        int b = _block[i] * MaxRuns;
        for (int j = n; j > k; j--)
        {
            _rAnim[b + j] = _rAnim[b + j - 1];
            _rRate[b + j] = _rRate[b + j - 1]; _rPrev[b + j] = _rPrev[b + j - 1];
            _rTime[b + j] = _rTime[b + j - 1]; _rBlend[b + j] = _rBlend[b + j - 1];
            _rFade[b + j] = _rFade[b + j - 1]; _rPosScale[b + j] = _rPosScale[b + j - 1];
            _rFlags[b + j] = _rFlags[b + j - 1]; _rDone[b + j] = _rDone[b + j - 1];
        }
        _runN[i] = (byte)(n + 1);
        return true;
    }

    /// <summary><c>AnimRun::AnimRun</c> @0x497bf0: rate 1, flags 0, times 0, weight 1, fade 0, posScale 1.</summary>
    private static void Seat(int s, AnimDef anim)
    {
        _rAnim[s] = anim;
        _rRate[s] = 1f; _rPrev[s] = 0f; _rTime[s] = 0f;
        _rBlend[s] = 1f; _rFade[s] = 0f; _rPosScale[s] = 1f;
        _rFlags[s] = 0; _rDone[s] = 0;
    }

    /// <summary>The animation <paramref name="name"/> as this object resolves it: the add-in model's chain
    /// first, then its own, through the <c>ModelCache.Resolve</c> memo so a <c>PlayAnimation</c> costs no chain
    /// walk and no string compare after the first. <paramref name="root"/> is the chain that answered -- the
    /// model the pose is BOUND against -- and <paramref name="scale"/> the inherited animation scale.</summary>
    private static AnimDef? Find(int i, string name, out ModelDef? root, out float scale)
    {
        root = null;
        scale = 1f;
        ModelDef? addIn = _addIn[i];
        if (addIn != null)
        {
            ModelCache.AnimLookup a = ModelCache.Resolve(addIn, name);
            if (a.Anim != null) { root = addIn; scale = a.Scale; return a.Anim; }
        }
        ModelDef? m = SceneDb.ObjModel[i];
        if (m == null) return null;
        ModelCache.AnimLookup b = ModelCache.Resolve(m, name);
        if (b.Anim == null) return null;
        root = m;
        scale = b.Scale;
        return b.Anim;
    }

    /// <summary><c>Gob::PlayAnimation</c> @0x49a750, transcribed.</summary>
    private static int PlayAnimationCore(DbId obj, string? name, float speed, int flags, float fadeTime)
    {
        if (!SceneDb.Objects.Valid(obj)) return 0;
        int i = obj.Index;

        // A null, empty or literal "NULL" name is "stop everything", and it answers 1.
        if (string.IsNullOrEmpty(name) || string.Equals(name, "NULL", StringComparison.OrdinalIgnoreCase))
        {
            ClearQueue(i);
            Moved(i);
            SceneDb.Objects.Mark(i, Dirty.Anim);
            return 1;
        }

        int reuse = -1;
        if ((flags & RunFlags.NoReuse) == 0)
        {
            for (int k = _runN[i] - 1; k >= 0; k--)
            {
                AnimDef? a = _rAnim[Slot(i, k)];
                if (a == null || !string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if ((flags & RunFlags.FadeNow) != 0)
                {
                    // "Fade the one that is already playing" rather than playing it again.
                    _rFlags[Slot(i, k)] |= RunFlags.FadeNow | RunFlags.FadeOut;
                    return 1;
                }
                reuse = k;
                break;
            }
        }
        if (reuse < 0 && string.Equals(name, "current", StringComparison.OrdinalIgnoreCase))
            reuse = CurrentIndex(i);

        // The engine does not rebuild a reused run: it takes the same AnimRun out of the list and puts it back
        // at the new index, so its TIME and WEIGHT survive. Carried by value here, because the pool slot it
        // lived in is about to be closed up.
        AnimDef? reused = null;
        float reTime = 0f, reBlend = 1f, reFade = 0f;
        if (reuse >= 0)
        {
            int rs = Slot(i, reuse);
            reused = _rAnim[rs];
            reTime = _rTime[rs];
            reBlend = _rBlend[rs];
            reFade = _rFade[rs];
            Remove(i, reuse, count: false);
            if ((flags & RunFlags.EndOnly) != 0)
            {
                Lifecycle(obj, DonePrefix, reused);
                return 1;
            }
        }
        else if ((flags & RunFlags.EndOnly) != 0) return 0;

        AnimDef? anim = Find(i, name, out _, out float animScale);
        if (anim == null && reused == null) return 0;
        // The name no longer resolves but a run of it exists: the engine still takes a scale, and takes it
        // for a NULL animation -- which is the whole chain's product, not 1.
        if (anim == null)
        {
            anim = reused!;
            animScale = ModelCache.FindAnimScale(_addIn[i] ?? SceneDb.ObjModel[i], null);
        }

        float fade = MathF.Min(fadeTime < 0f ? 0f : fadeTime, anim.Length);

        int idx;
        if ((flags & RunFlags.AddIn) != 0) idx = 0;
        else
        {
            idx = 0;
            while (idx < _runN[i])
            {
                int fl = _rFlags[Slot(i, idx)];
                if ((fl & RunFlags.AddIn) == 0
                    && ((flags & RunFlags.Queued) != 0 || (fl & RunFlags.Queued) == 0)) break;
                idx++;
            }
        }
        if (!Insert(i, idx)) return 0;
        int s = Slot(i, idx);
        Seat(s, anim);

        // The engine re-inserts the SAME AnimRun: its time, its weight AND its cross-fade time survive. A
        // zeroed fade time would snap the weight to 1 in one frame and evict the run below it a frame early,
        // which reads as a pop on every re-play of the animation already playing.
        if (reused != null) { _rTime[s] = reTime; _rBlend[s] = reBlend; _rFade[s] = reFade; }
        else
        {
            // The starting time, in the engine's order: the sign of the speed picks an end, and a non-zero
            // fade time then overrides it outright.
            if (speed == 0f) _rTime[s] = 0f;
            else if (speed < 0f) _rTime[s] = anim.Length + 1e-5f;
            else _rTime[s] = -1e-5f;   // not-an-offset: the engine's 0xb727c5ac, one step before key 0
            if (fade != 0f) _rTime[s] = fade;
            if ((flags & RunFlags.NoFadeIn) == 0) { _rBlend[s] = 0f; _rFade[s] = anim.TransTime; }
        }

        _rPosScale[s] = animScale;
        _rDone[s] = 0;
        _rFlags[s] = flags;
        _rRate[s] = _speed[i] * speed;
        _rPrev[s] = _rTime[s];
        if ((flags & RunFlags.Trace) != 0) _traced++;

        Moved(i);
        SceneDb.Objects.Mark(i, Dirty.Anim);

        if ((flags & RunFlags.InheritTime) == 0)
        {
            if ((flags & (RunFlags.AddIn | RunFlags.Queued)) != 0) return 1;
            // Everything older than the new run is finished outright, each announcing itself first -- and
            // each announcement is game code that can reshape the queue, so the new run's own slot is
            // re-derived after every one of them.
            while (_runN[i] > idx + 1)
            {
                AnimDef? old = _rAnim[Slot(i, idx + 1)];
                Remove(i, idx + 1);
                Lifecycle(obj, DonePrefix, old);
                if (!Still(obj, idx, anim, out s)) return 1;
            }
        }
        else
        {
            if (idx + 1 < _runN[i]) { _rTime[s] = _rTime[Slot(i, idx + 1)]; return 1; }
            if ((flags & (RunFlags.AddIn | RunFlags.Queued)) != 0) return 1;
        }

        Lifecycle(obj, StartPrefix, anim);
        return 1;
    }

    /// <summary>The run at the given index has its "start pending" bit: clear it and announce the start, as the
    /// engine does once per step before the advance. False = the object died under the event.</summary>
    private static bool StartQueued(DbId obj, int k)
    {
        int i = obj.Index;
        if (k < 0 || k >= _runN[i]) return true;
        int s = Slot(i, k);
        if ((_rFlags[s] & RunFlags.Queued) == 0) return true;
        _rFlags[s] &= ~RunFlags.Queued;
        Lifecycle(obj, StartPrefix, _rAnim[s]);
        // The object surviving is the caller's contract; the QUEUE may well have been reshaped, which is why
        // every caller re-derives its own slot afterwards.
        return SceneDb.Objects.Valid(obj);
    }

    /// <summary>
    /// Is run <paramref name="k"/> still the run this iteration started on, and is its slot still addressable?
    /// <para>Every event call below runs GAME code, and a handler may call <c>PlayAnimation</c> (which inserts
    /// at or below this index and SHIFTS the queue), clear the queue (giving the run block back), or destroy
    /// the object. The run index is POSITIONAL, so after any of those the slot addresses a different run.</para>
    /// </summary>
    private static bool Still(DbId obj, int k, AnimDef anim, out int s)
    {
        s = -1;
        if (!SceneDb.Objects.Valid(obj)) return false;
        int i = obj.Index;
        if (_block[i] < 0 || k >= _runN[i]) return false;
        s = Slot(i, k);
        return ReferenceEquals(_rAnim[s], anim);
    }

    /// <summary>
    /// The queue advance, oldest run to newest (index <c>n-1</c> down to 0). Ported term for term from
    /// <c>AnimRuntime.Advance</c>, which is itself the transcription of <c>Gob::Animate</c>'s run loop: the
    /// time step, the loop wrap, the fade-out ladder, the weight ramp, then the pose and the keyframe events.
    /// False = the object died under an event.
    /// </summary>
    private static bool Advance(DbId obj, float dt, int n)
    {
        int i = obj.Index;
        for (int k = n - 1; k >= 0; k--)
        {
            if (k >= _runN[i]) continue;
            int s = Slot(i, k);
            AnimDef? anim = _rAnim[s];
            if (anim == null) continue;
            int fl = _rFlags[s];
            if ((fl & RunFlags.Queued) != 0) continue;
            float rate = _rRate[s];
            _rPrev[s] = _rTime[s];
            float t = _rTime[s] + dt * rate;
            _rTime[s] = t;
            float w = 0f;

            if ((fl & RunFlags.FadeOut) == 0)
            {
                if (t < 0f && rate <= 0f)
                {
                    if ((fl & RunFlags.Loop) != 0)
                    {
                        Lifecycle(obj, LoopPrefix, anim);
                        if (!SceneDb.Objects.Valid(obj)) return false;
                        if (!Still(obj, k, anim, out s)) continue;
                        t = _rTime[s]; fl = _rFlags[s];
                    }
                    t += anim.Length;
                    if (t < 0f) { _rTime[s] = 0f; t = 0f; } else _rTime[s] = t;
                }
                float len = anim.Length;
                if (t <= len || _rRate[s] < 0f) w = _rBlend[s];
                else
                {
                    if ((fl & RunFlags.Loop) != 0)
                    {
                        Lifecycle(obj, LoopPrefix, anim);
                        if (!SceneDb.Objects.Valid(obj)) return false;
                        if (!Still(obj, k, anim, out s)) continue;
                        t = _rTime[s]; fl = _rFlags[s]; len = anim.Length;
                    }
                    w = _rBlend[s];
                    _rTime[s] = t - len;
                    if (t - len > anim.Length) _rTime[s] = anim.Length;
                }
            }
            else if (_rDone[s] != 0)
            {
                // Removed FIRST, then the event -- so a handler that walks the queue cannot see the run it is
                // being told about.
                Remove(i, k);
                Lifecycle(obj, DonePrefix, anim);
                if (!SceneDb.Objects.Valid(obj)) return false;
                continue;
            }
            else
            {
                bool ramp = false;
                if (t >= 0f || rate > 0f)
                {
                    if (t > anim.Length && rate >= 0f) _rTime[s] = anim.Length;
                    else if ((fl & RunFlags.FadeNow) == 0) { w = _rBlend[s]; ramp = true; }
                }
                else _rTime[s] = 0f;

                if (!ramp)
                {
                    if (k > 0 && !StartQueued(obj, k - 1)) return false;
                    if (!Still(obj, k, anim, out s)) continue;
                    fl = _rFlags[s];
                    float ft = _rFade[s];
                    w = _rBlend[s] - (ft <= 0f ? 2f : (dt + dt) / ft);
                    _rBlend[s] = w;
                    if (!(w > 0f))
                    {
                        _rBlend[s] = 0f;
                        _rDone[s] = 1;
                        // A zero fade time is "no fade": the run plays at full weight for this one frame and
                        // is removed on the next visit.
                        if (ft == 0f) { _rBlend[s] = 1f; w = 1f; } else w = 0f;
                    }
                }
            }

            // The weight ramp. Reached from every path above, including the fade-out one while its weight is
            // still positive -- which is why the effective fade-out step is dt/fadeTime and not 2*dt/fadeTime.
            if (_rDone[s] == 0 && w < 1f)
            {
                float step = _rFade[s] > 0f ? dt / _rFade[s] : 1f;
                w += step;
                if (w <= 1f) { _rBlend[s] = w; _crossfades++; }
                else
                {
                    _rBlend[s] = 1f;
                    w = 1f;
                    if ((fl & RunFlags.AddIn) == 0)
                        while (_runN[i] > k + 1) Remove(i, k + 1);
                    fl = _rFlags[s];
                    w = _rBlend[s];
                }
            }

            // The FIELD, not the rate cached at the top: an event fired above may have changed it.
            bool reverse = _rRate[s] < 0f;
            Pose(obj, anim, s, Weight(i, w));
            float from = _rPrev[s], to = _rTime[s];
            if (!FireEvents(obj, anim, from, to, reverse)) return false;
            _runs++;
            Moved(i);
            SceneDb.Objects.Mark(i, Dirty.Anim);
            if (!SceneDb.Objects.Valid(obj)) return false;
        }
        return true;
    }

    /// <summary>The crossfade weight remap (the knob lives with <c>CrossfadeService</c>), for objects outside a GUI
    /// panel's scene. A panel replays its state animation every update and settles over many tiny linear weights
    /// (the party bar's locator arrow), which smoothstep squares to nothing.</summary>
    private static float Weight(int i, float w)
    {
        DbId sc = SceneDb.ObjScene[i];
        return SceneDb.Scenes.Valid(sc) && !SceneDb.SceneIsWorld[sc.Index] ? w : CX.Services.Anim.CrossfadeService.Weight(w);
    }

    /// <summary><c>CurrentAnimation</c> @0x499ac0: the first run in queue order whose flags carry neither
    /// <see cref="RunFlags.AddIn"/> nor <see cref="RunFlags.Queued"/> (the engine's one <c>&amp; 0x28</c>
    /// test), or -1. A fading-out run still counts -- it is the one playing, and
    /// <c>CNWCObject::GetCurrentAnimation</c> reads this, so an add-in overlay must not shadow it.</summary>
    private static int CurrentIndex(int i)
    {
        for (int k = 0; k < _runN[i]; k++)
            if ((_rFlags[Slot(i, k)] & (RunFlags.AddIn | RunFlags.Queued)) == 0) return k;
        return -1;
    }

    /// <inheritdoc cref="CurrentIndex"/>
    private static string? CurrentName(DbId obj)
    {
        if (!SceneDb.Objects.Valid(obj)) return null;
        int i = obj.Index;
        int k = CurrentIndex(i);
        return k < 0 ? null : _rAnim[Slot(i, k)]?.Name;
    }

    /// <summary>
    /// <c>Gob::GetAnimation(name, time, length)</c> @0x499eb0: answers the RUN, not the animation -- non-null
    /// only while <paramref name="name"/> is in the queue, else <paramref name="time"/> is <b>-1</b> and the handle NULL.
    /// <para>Load-bearing: <c>GetOverlayAnimationPlaying</c> @0xad1490 tests <c>0 &lt; time</c>, so writing
    /// LENGTH there makes <c>CNWCObject::SetAnimation</c> @0x8e4bc0 call <c>ShowEquippedItems(null)</c>.</para>
    /// </summary>
    private static nint GetAnimationCore(DbId obj, string? name, out float time, out float length)
    {
        time = -1f; length = -1f;
        if (!SceneDb.Objects.Valid(obj) || string.IsNullOrEmpty(name)) return 0;
        int i = obj.Index;
        for (int k = 0; k < _runN[i]; k++)
        {
            int s = Slot(i, k);
            AnimDef? a = _rAnim[s];
            if (a == null || !string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            time = _rTime[s];
            length = a.Length;
            return Token(a);
        }
        // Not playing. The length still answers when the chain resolves the name, which is what the engine's
        // own second half does, and the null handle is the answer the caller branches on.
        AnimDef? found = Find(i, name, out _, out _);
        if (found != null) length = found.Length;
        return 0;
    }

    private static int LoadAddIn(DbId obj, string resref)
    {
        if (!SceneDb.Objects.Valid(obj) || string.IsNullOrEmpty(resref)) return 0;
        ModelDef? m = ModelCache.LoadAddInAnimations(resref);
        if (m == null) return 0;
        RemoveAddIn(obj);
        _addIn[obj.Index] = m;
        return 1;
    }

    private static void RemoveAddIn(DbId obj)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        ModelDef? m = _addIn[obj.Index];
        if (m == null) return;
        _addIn[obj.Index] = null;
        ModelCache.Release(m);
    }
}
