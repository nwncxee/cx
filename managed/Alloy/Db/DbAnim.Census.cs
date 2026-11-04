namespace CX.Alloy.Db;

/// <summary>Why a compose ran. <see cref="Other"/> is what no bracket names: a getter, an appended piece.</summary>
internal enum ComposeCause { Other, Stepped, DirtyList, PartMark, Child, Parent, Count }

/// <summary>The period's population counts for the animate step. Replaced whole by the period line.</summary>
internal sealed class AnimCensusState
{
    public ComposeCause Cause;
    public int WorldAnimates, PanelAnimates, WorldSteps, WorldStepsHeld;
    public readonly int[] Composes = new int[(int)ComposeCause.Count];
}

public static unsafe partial class DbAnim
{
    private static AnimCensusState _census = new();

    /// <summary>Count one step of object <paramref name="i"/>.</summary>
    private static void CensusStep(int i)
    {
        DbId scene = SceneDb.ObjScene[i];
        if (SceneDb.Scenes.Valid(scene) && SceneDb.SceneIsWorld[scene.Index]) _census.WorldSteps++;
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
        _census = new AnimCensusState();
        int[] by = c.Composes;
        return $" | census: {c.WorldAnimates} world/{c.PanelAnimates} panel animate(s), "
             + $"{c.WorldSteps} world step(s) ({c.WorldStepsHeld} held out of view), "
             + $"composes {by[(int)ComposeCause.Stepped]} stepped, {by[(int)ComposeCause.DirtyList]} dirty list, "
             + $"{by[(int)ComposeCause.PartMark]} part mark, {by[(int)ComposeCause.Child]} child, "
             + $"{by[(int)ComposeCause.Parent]} parent, {by[(int)ComposeCause.Other]} other";
    }
}
