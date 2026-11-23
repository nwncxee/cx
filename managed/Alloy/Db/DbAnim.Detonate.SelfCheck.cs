using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>The event window, opened to the emitter simulation's detonate examples, which play an animation's
/// events at an object and read what its Explosion emitters do next.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>Fire <paramref name="anim"/>'s events in the forward window (<paramref name="from"/>,
    /// <paramref name="to"/>] at <paramref name="obj"/>, as <c>AnimateEvents</c> does. The period counters and
    /// said-once flags are the session's, so they are put back after.</summary>
    internal static bool FireEventsForCheck(DbId obj, AnimDef anim, float from, float to)
    {
        EventPathState keep = _events;
        _events = new EventPathState { StormSaid = true, NoFacadeSaid = true, NamesFullSaid = true };
        try { return FireEvents(obj, anim, from, to, false); }
        finally { _events = keep; }
    }
}
