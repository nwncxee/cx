using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Services.Anim;

/// <summary>
/// The analysed clips (<see cref="GaitClip"/>) by animation and the model the legs belong to: a clip is sampled through a
/// body's own leg chain the first time a locomoting body plays it, kept for as long as the animation lives, and its verdict
/// logged once. Scene-database bodies only: a vanilla session's clips are the engine's, with no keys to read.
/// </summary>
internal static class GaitClips
{
    /// <summary>The log, the clips by animation, and how many were found walks and how many not.</summary>
    private sealed class GaitClipsState
    {
        public ExtenderLog? Log;
        /// <summary>Per animation, its analysis on each leg model that played it: never evicted, so a pair is analysed
        /// once however many races and genders share the clip.</summary>
        public readonly ConditionalWeakTable<AnimDef, Dictionary<ModelDef, GaitClip>> Sets = new();
        public int Walks, Others;
    }

    private static readonly GaitClipsState _s = new();

    internal static void Init(ExtenderLog log) => _s.Log = log;

    /// <summary>Clips analysed so far: walks, and clips found not to be one.</summary>
    internal static (int Walks, int Others) Count => (_s.Walks, _s.Others);

    /// <summary>The analysis of <paramref name="anim"/> on the body whose legs are <paramref name="bones"/>, played at
    /// <paramref name="posScale"/>; analysed now only when <paramref name="analyse"/>, else null until it has been.
    /// Cold the first time for each pair (it samples the clip and allocates), a lookup after.</summary>
    internal static GaitClip? For(AnimDef anim, in PoseService.Bones bones, float posScale, bool analyse)
    {
        int rf = PoseRig.Index(bones.RFoot);
        if (!bones.Legs || (uint)rf >= (uint)SceneDb.PartModel.Length || SceneDb.PartModel[rf] is not { } model) return null;
        if (!_s.Sets.TryGetValue(anim, out Dictionary<ModelDef, GaitClip>? set))
        {
            if (!analyse) return null;
            set = new Dictionary<ModelDef, GaitClip>(ReferenceEqualityComparer.Instance);
            _s.Sets.Add(anim, set);
        }
        if (set.TryGetValue(model, out GaitClip? have)) return have;
        if (!analyse) return null;
        GaitClip c = Sample(anim, bones, posScale);
        set[model] = c;
        if (c.Walk) _s.Walks++; else _s.Others++;
        _s.Log?.Info(c.Walk
            ? $"Gait: {anim.Name} on {model.Name} is a walk -- {c.Speed:F2} units/s at rate 1, leg {c.LegLength:F2}, "
              + $"on the ground {c.DutyR:P0} right and {c.DutyL:P0} left{(c.Cyclic ? "" : ", one-shot")}"
            : $"Gait: {anim.Name} on {model.Name} is not a walk ({Why(c.Verdict)}) -- the engine paces and poses it");
        return c;
    }

    private static string Why(GaitVerdict v) => v switch
    {
        GaitVerdict.NoLength => "no length",
        GaitVerdict.OffTheGround => "the feet never reach the floor: a float",
        GaitVerdict.NotUpright => "the body is not carried on its legs: a swim or a crawl",
        GaitVerdict.NoStride => "the feet carry the body nowhere: a stand or a turn",
        _ => "the feet do not take turns on the ground",
    };

    /// <summary>The clip's ankles and hip midpoint through the body's own chain, at <see cref="GaitClip.Samples"/> + 1
    /// even times, and the right leg's length from its bones at the clip's start.</summary>
    private static GaitClip Sample(AnimDef anim, in PoseService.Bones bones, float posScale)
    {
        const int N = GaitClip.Samples;
        int rt = PoseRig.Index(bones.RThigh), rs = PoseRig.Index(bones.RShin), rf = PoseRig.Index(bones.RFoot);
        int lt = PoseRig.Index(bones.LThigh), lf = PoseRig.Index(bones.LFoot);
        var r = new Vector3[N + 1];
        var l = new Vector3[N + 1];
        var h = new Vector3[N + 1];
        for (int i = 0; i <= N; i++)
        {
            float t = anim.Length * i / N;
            r[i] = DbAnim.SampledPos(rf, anim, t, posScale);
            l[i] = DbAnim.SampledPos(lf, anim, t, posScale);
            h[i] = 0.5f * (DbAnim.SampledPos(rt, anim, t, posScale) + DbAnim.SampledPos(lt, anim, t, posScale));
        }
        Vector3 knee = DbAnim.SampledPos(rs, anim, 0f, posScale), thigh = DbAnim.SampledPos(rt, anim, 0f, posScale);
        float leg = Vector3.Distance(thigh, knee) + Vector3.Distance(knee, r[0]);
        return GaitClip.Analyse(r, l, h, leg, anim.Length);
    }
}
