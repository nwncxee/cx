using System;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The part tree's self-checks as one suite, which leaves the live reference hook and model resolver
/// where it found them. Oracle: two probe delegates installed here, which each check must hand back.</summary>
internal static partial class DbSelfCheck
{
    private static readonly Func<string, ModelDef?> ProbeResolve = _ => null;
    private static readonly Action<DbId, int, string> ProbeReference = (_, _, _) => { };

    /// <summary>The two part-tree hooks <see cref="Reset"/> replaces, saved by each check that calls it.</summary>
    private readonly record struct TreeHooks(Func<string, ModelDef?> Resolve, Action<DbId, int, string>? Reference)
    {
        internal static TreeHooks Save() => new(PartTree.ResolveModel, PartTree.OnReference);

        internal void Restore()
        {
            PartTree.ResolveModel = Resolve;
            PartTree.OnReference = Reference;
        }
    }

    /// <summary>Runs every part-tree self-check, then checks none of them left a hook of its own installed.</summary>
    internal static bool Suite(ExtenderLog log)
    {
        var check = new Checker(log, "DbSelfCheck.LiveHooks.SelfCheck");
        Func<string, ModelDef?> liveResolve = PartTree.ResolveModel;
        Action<DbId, int, string>? liveReference = PartTree.OnReference;
        PartTree.ResolveModel = ProbeResolve;
        PartTree.OnReference = ProbeReference;
        bool ok;
        try
        {
            ok = Run(log);
            ok &= MaterialResetApprovedExamplesSelfCheck(log);
            ok &= MaterialResetHoldsTodaySelfCheck(log);
            ok &= ModelEvictionApprovedExamplesSelfCheck(log);
            ok &= RecipeTableApprovedExamplesSelfCheck(log);
            ok &= RecipeTableRoomSelfCheck(log);
            check.Expect(PartTree.ResolveModel == ProbeResolve && PartTree.OnReference == ProbeReference,
                         "a part-tree self-check left its own reference hook or model resolver installed, so the live session instances no reference child");
        }
        finally
        {
            PartTree.ResolveModel = liveResolve;
            PartTree.OnReference = liveReference;
        }
        log.Info(check.Ok ? "DbSelfCheck.LiveHooks.SelfCheck: passed" : "DbSelfCheck.LiveHooks.SelfCheck: FAILED");
        return ok && check.Ok;
    }
}
