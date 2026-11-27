using System;
using System.Numerics;
using CX.Services.Camera;
using CX.Framework;

namespace CX.Services.Interact;

/// <summary>Edges of the Transition rule no approved example reaches, pressed through <see cref="InteractInput.KeyDown"/>
/// on a world double. Oracle: the object ids and distances written here, and the double's own outline geometry.</summary>
internal static class TransitionEdges
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TransitionEdges.SelfCheck");
        try
        {
            {
                var w = InteractWorldDouble.Install();
                uint exit = w.Transition(1.5f, 8f);
                w.Press('F');
                Vector3[] o = w.Of(exit).Outline!;
                float off = w.Walks.Count == 1 ? Vector3.Distance(w.Walks[0], w.Nearest(o)) : -1f;
                check.Expect(w.Walks.Count == 1 && Inside(o, w.Walks[0]) && MathF.Abs(off - InteractCore.Inset) < 0.01f,
                    $"the walk heads just inside the trigger's nearest edge: {off:0.00}m from it, {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                uint door = w.Door(1.5f, leadsAway: true);
                w.Press('F');
                check.Expect(w.Did(door) && w.Walks.Count == 0,
                    $"a closed transition door gets its default action, which opens it, not a walk of Interact's own: {w.Walks.Count} walk(s), {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                uint door = w.Door(2f, open: true, leadsAway: true);
                w.Change(door, t => { t.Pos = new Vector3(2f, 0f, 0f); return t; });
                w.Press('F');
                check.Expect(w.Did(door), $"a transition exactly 2m away is in reach: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                uint exit = w.Transition(1.5f, 8f);
                Vector3 q = w.Nearest(w.Of(exit).Outline!), d = Vector3.Normalize(q - w.Player), p = new(-d.Y, d.X, 0f), beyond = q + d * 2f;
                w.Wall(beyond - p, beyond + p);
                w.Press('F');
                check.Expect(w.Did(exit), $"a wall inside the trigger, past its nearest edge, does not hide it: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.TransitionKey.Set("T");
                w.Door(1.5f, open: true);
                bool took = w.Press('T');
                check.Expect(took && w.Acts.Count == 0, $"a door that leads nowhere is no transition: took {took}, {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(1f);
                w.Press('F');
                check.Expect(w.Did(bag) && w.Walks.Count == 0, $"a body bag gets its default action, not a walk: {w.Walks.Count} walk(s), {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                uint exit = w.Transition(0f, 1f);
                w.Press('F');
                bool real = w.Walks.Count == 1 && float.IsFinite(w.Walks[0].X) && float.IsFinite(w.Walks[0].Y);
                check.Expect(w.Did(exit) && real, $"standing on a trigger's edge, the walk heads for a real point: {(w.Walks.Count == 1 ? w.Walks[0].ToString() : "no walk")}, {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.TransitionKey.Set("T");
                uint door = w.Door(5f, leadsAway: true);
                TabTarget.Enabled.Set(true); w.Lock = door;
                w.Press('T');
                check.Expect(w.Acts.Count == 0, $"a locked transition 5m away is out of the Transition key's reach: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                uint foe = w.Creature(3f, Attitude.Hostile, talks: false); w.Transition(1.5f);
                TabTarget.Enabled.Set(true); w.Lock = foe;
                w.Press('F');
                check.Expect(w.Acts.Count == 0, $"a locked hostile is not acted on, nor the transition beside it: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                uint body = w.Corpse(3f, lootable: false, attitude: Attitude.Friend);
                TabTarget.Enabled.Set(true); w.Lock = body;
                w.Press('F');
                check.Expect(w.Acts.Count == 0, $"a locked corpse that cannot be looted is not talked to: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                uint friend = w.Creature(1.8f); uint exit = w.Transition(1.5f);
                TabTarget.Enabled.Set(true); w.Lock = friend; w.Crosshair = exit;
                w.Press('F');
                check.Expect(w.Did(exit), $"the transition under the crosshair beats the lock: {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                uint exit = w.Transition(1.8f, 8f);
                w.Change(exit, t => { Vector3[] o = t.Outline!; t.Outline = new[] { o[1], o[2], o[3], o[0] }; return t; });
                w.Press('F');
                check.Expect(w.Did(exit), $"a trigger whose nearest edge is listed last is measured by it: {w.Seen}");
            }
        }
        finally
        {
            TabTarget.Enabled.Reset();
            InteractWorldDouble.Restore();
        }
        log.Info(check.Ok ? "TransitionEdges.SelfCheck: passed" : "TransitionEdges.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>Whether <paramref name="p"/> lies strictly inside the convex outline <paramref name="o"/>.</summary>
    private static bool Inside(Vector3[] o, Vector3 p)
    {
        int sign = 0;
        for (int i = 0; i < o.Length; i++)
        {
            Vector3 a = o[i], b = o[(i + 1) % o.Length];
            float cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            int s = cross > 0f ? 1 : cross < 0f ? -1 : 0;
            if (s == 0 || (sign != 0 && s != sign)) return false;
            sign = s;
        }
        return true;
    }
}
