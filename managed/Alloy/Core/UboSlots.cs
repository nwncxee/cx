using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The uniform-block binding contract, written down once, like <see cref="SsboSlots"/>. Every
/// non-opaque uniform Alloy sets lives in one of these blocks, so the GLSL is what Vulkan accepts
/// and a program switch costs no uniform traffic.</summary>
internal static class UboSlots
{
    /// <summary>The whole budget. A binding index must be under the driver's
    /// <c>MAX_UNIFORM_BUFFER_BINDINGS</c> and no stage may declare more blocks than its
    /// <c>MAX_*_UNIFORM_BLOCKS</c>; 12 is the smaller floor, Vulkan's guaranteed
    /// <c>maxPerStageDescriptorUniformBuffers</c>, so one number covers both.</summary>
    internal const uint Budget = 12;

    /// <summary>Persistent: bound once per pass and read by every program in it without a rebind, so each
    /// owns its point outright and nothing else may bind there.</summary>
    internal const uint Pass = 0, ProbeSh = 1;

    /// <summary>The first shared point. Everything from here up is bound by its own pass right before its
    /// draw or dispatch, so two passes that never run inside one another may share a point -- provided no one
    /// PROGRAM links stages from both, which is what <see cref="Blocks"/>'s text list holds.</summary>
    internal const uint Shared = 2;

    /// <summary>A pass's own terms, on the first shared point: one block per pass, read by every program that
    /// pass runs and rebound before each of its dispatches.</summary>
    internal const uint Cull = 2, Deferred = 2, Ssao = 2;

    /// <summary>Every block name the GLSL declares, the point C# binds it on, and -- on a shared point -- every
    /// text allowed to declare it. The list is the one net for the hazard a text-at-a-time scan cannot see:
    /// two blocks on one point inside one program both source that point's buffer, and nothing in the GLSL
    /// says which texts a program pairs.</summary>
    private static readonly Dictionary<string, (uint At, string[] Texts)> Blocks = new()
    {
        ["CxPass"] = (Pass, []),
        ["CxProbeSh"] = (ProbeSh, []),
        ["CxCull"] = (Cull, ["cx_cullblk.glsl", "cx_cull_c.glsl", "cx_cullsort_c.glsl"]),
        ["CxDeferred"] = (Deferred, ["cx_defblk.glsl", "cx_deferred_cs.glsl"]),
        ["CxSsao"] = (Ssao, ["cx_ssaolib.glsl", "cx_ssao_f.glsl", "cx_ssao_fs.glsl", "cx_ssaoblur_f.glsl",
                             "cx_ssaoblur_fs.glsl", "cx_ssaotemp_f.glsl"]),
    };

    private static readonly Regex Decl = new(@"layout\(std140,\s*binding\s*=\s*(\d+)\)\s*uniform\s+(\w+)");

    /// <summary>Oracle: the GLSL text itself -- every embedded stage and library with its includes spliced,
    /// plus the stages composed in C#. Each declaration is under the budget, at its table point, and alone on
    /// that point within its own text; a shared point's block comes only from the texts listed for it; a
    /// persistent point holds only its own block; and members land where the staging struct puts them.</summary>
    internal static bool Check(ExtenderLog log)
    {
        var check = new Checker(log);
        int decls = 0;
        var declared = new HashSet<(string Block, string Text)>();
        foreach ((string name, string src) in ShaderSource.EveryStage())
        {
            var claimed = new Dictionary<uint, string>();
            foreach (Match m in Decl.Matches(src))
            {
                decls++;
                uint at = uint.Parse(m.Groups[1].Value);
                string block = m.Groups[2].Value;
                declared.Add((block, name));
                check.Expect(at < Budget, $"{name}: {block} on point {at}, past the budget of {Budget}");
                if (Blocks.TryGetValue(block, out (uint At, string[] Texts) row))
                {
                    check.Expect(at == row.At, $"{name}: {block} on point {at}, UboSlots binds {row.At}");
                    check.Expect(at < Shared || Array.IndexOf(row.Texts, name) >= 0,
                      $"{name}: {block} is on shared point {at} and UboSlots does not list this text for it -- "
                    + "a program pairing it with another block's stage would read that pass's bytes");
                }
                else check.Expect(false, $"{name}: {block} has no row in UboSlots");
                check.Expect(!claimed.TryGetValue(at, out string? other) || other == block,
                  $"{name}: {block} and {other} both claim point {at}");
                claimed[at] = block;
            }
            check.Expect(claimed.Count <= Budget, $"{name} declares {claimed.Count} blocks, past the per-stage budget of {Budget}");
        }
        check.Expect(decls >= Blocks.Count, $"only {decls} declarations found for {Blocks.Count} blocks -- the pattern stopped matching");
        foreach ((string block, (uint _, string[] texts)) in Blocks)
            foreach (string text in texts)
                check.Expect(declared.Contains((block, text)), $"UboSlots lists {text} for {block}, which no longer declares it");
        CullBlock.Pin(check);
        DeferredBlock.Pin(check);
        SsaoBlock.Pin(check);
        // ...and the override guard's own predicate, against hand-written texts: an override of a stage that
        // reads the block is refused for a member left loose, the SAME text is left alone as an override of a
        // stage whose member was always loose, a stale block body is refused, and the shipped stages pass.
        string def = ShaderSource.Get("cx_deferred_cs.glsl");
        string loose = "#version 460 core\nuniform mat4 uInvVp;\nvoid main() { }";
        check.Expect(!ShaderContract.BlocksSafe(def, loose), "a cx_deferred_cs override declaring uInvVp loose is refused");
        check.Expect(ShaderContract.BlocksSafe(ShaderSource.Get("cx_defshadow_cs.glsl"), loose),
          "...and the same text passes for cx_defshadow_cs, whose own uInvVp was never a block member");
        check.Expect(!ShaderContract.BlocksSafe(def, def.Replace("int uTiles;", "int uTileN;", StringComparison.Ordinal)),
          "...and so is a block whose body is not the library's");
        foreach (string stage in new[] { "cx_ssao_f.glsl", "cx_ssaoblur_f.glsl", "cx_ssaotemp_f.glsl",
                                         "cx_cull_c.glsl", "cx_cullsort_c.glsl", "cx_deferred_cs.glsl" })
            check.Expect(ShaderContract.BlocksSafe(ShaderSource.Get(stage), ShaderSource.Get(stage)),
              $"the shipped {stage} passes the guard");
        check.Expect(ShaderContract.BlocksSafe(def, "#version 460 core\nvoid main() { }"), "a stage that reads no block is untouched");
        var owner = new Dictionary<uint, string>();
        foreach ((string block, (uint at, string[] _)) in Blocks)
            if (at < Shared && !owner.TryAdd(at, block))
                check.Expect(false, $"persistent point {at} is shared by {owner[at]} and {block}");
        log.Info(check.Ok ? $"UboSlots.SelfCheck: passed ({decls} declarations under {Budget} points)" : "UboSlots.SelfCheck: FAILED");
        return check.Ok;
    }
}
