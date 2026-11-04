using System.Runtime.CompilerServices;

namespace CX.Alloy.Db;

/// <summary>Why a compose ran. <see cref="Other"/> is what no bracket names: a getter, an appended piece.</summary>
internal enum ComposeCause { Other, Stepped, DirtyList, PartMark, Child, Parent, Count }

/// <summary>The period's population counts for the animate step. Replaced whole by the period line.</summary>
internal sealed class AnimCensusState(int[] routedAt)
{
    /// <summary>The render frame each object last had a part pass the route's frustum test. Carried across the
    /// period reset, because a step reads the stamp the previous period may have written.</summary>
    public readonly int[] RoutedAt = routedAt;
    public ComposeCause Cause;
    public int WorldAnimates, PanelAnimates, WorldSteps, WorldStepsInView;
    public readonly int[] Composes = new int[(int)ComposeCause.Count];
}

public static unsafe partial class DbAnim
{
    /// <summary>A step counts as in view within this many frames of its object's last route: the game thread
    /// animates ahead of the render, so the newest route it can see is the previous frame's.</summary>
    private const int InViewFrames = 1;

    private static AnimCensusState _census = new(new int[SceneDb.MaxObjects]);

    /// <summary>A part of object <paramref name="i"/> passed the route in render frame <paramref name="seq"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void NoteRouted(int i, int seq) => _census.RoutedAt[i] = seq;

    /// <summary>Count one step of object <paramref name="i"/> in render frame <paramref name="seq"/>.</summary>
    private static void CensusStep(int i, int seq)
    {
        DbId scene = SceneDb.ObjScene[i];
        if (!SceneDb.Scenes.Valid(scene) || !SceneDb.SceneIsWorld[scene.Index]) return;
        _census.WorldSteps++;
        if (seq - _census.RoutedAt[i] <= InViewFrames) _census.WorldStepsInView++;
    }

    /// <summary>Compose object <paramref name="i"/> and credit the compose to <paramref name="cause"/>, restoring
    /// the outer cause because a compose places its children inside its own.</summary>
    private static void ComposeAs(ComposeCause cause, int i)
    {
        ComposeCause outer = _census.Cause;
        _census.Cause = cause;
        ComposeIndex(i);
        _census.Cause = outer;
    }

    /// <summary>The census clause of the period line, and its reset.</summary>
    private static string CensusPeriod()
    {
        AnimCensusState c = _census;
        _census = new AnimCensusState(c.RoutedAt);
        int[] by = c.Composes;
        return $" | census: {c.WorldAnimates} world/{c.PanelAnimates} panel animate(s), "
             + $"{c.WorldSteps} world step(s) ({c.WorldStepsInView} in view, {c.WorldSteps - c.WorldStepsInView} out), "
             + $"composes {by[(int)ComposeCause.Stepped]} stepped, {by[(int)ComposeCause.DirtyList]} dirty list, "
             + $"{by[(int)ComposeCause.PartMark]} part mark, {by[(int)ComposeCause.Child]} child, "
             + $"{by[(int)ComposeCause.Parent]} parent, {by[(int)ComposeCause.Other]} other";
    }
}
