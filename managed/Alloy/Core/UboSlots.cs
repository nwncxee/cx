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
    /// draw or dispatch, so two passes that never run inside one another may share a point.</summary>
    internal const uint Shared = 2;

    /// <summary>A pass's own terms, on the first shared point: one block per pass, read by every program that
    /// pass runs and rebound before each of its dispatches.</summary>
    internal const uint Cull = 2, Deferred = 2, Ssao = 2;

    /// <summary>Every block name the GLSL declares, and the point C# binds it on.</summary>
    private static readonly Dictionary<string, uint> Blocks = new()
    {
        ["CxPass"] = Pass, ["CxProbeSh"] = ProbeSh, ["CxCull"] = Cull, ["CxDeferred"] = Deferred,
        ["CxSsao"] = Ssao,
    };

    private static readonly Regex Decl = new(@"layout\(std140,\s*binding\s*=\s*(\d+)\)\s*uniform\s+(\w+)");

    /// <summary>Oracle: the GLSL text itself -- every embedded stage and library with its includes spliced,
    /// plus the stages composed in C#. Each declaration is under the budget, at its table point, and alone on
    /// that point within its stage; a persistent point holds only its own block; and every block's members
    /// land where the struct that stages them puts theirs.</summary>
    internal static bool Check(ExtenderLog log)
    {
        var check = new Checker(log);
        int decls = 0;
        foreach ((string name, string src) in ShaderSource.EveryStage())
        {
            var claimed = new Dictionary<uint, string>();
            foreach (Match m in Decl.Matches(src))
            {
                decls++;
                uint at = uint.Parse(m.Groups[1].Value);
                string block = m.Groups[2].Value;
                check.Expect(at < Budget, $"{name}: {block} on point {at}, past the budget of {Budget}");
                if (Blocks.TryGetValue(block, out uint want)) check.Expect(at == want, $"{name}: {block} on point {at}, UboSlots binds {want}");
                else check.Expect(false, $"{name}: {block} has no row in UboSlots");
                check.Expect(!claimed.TryGetValue(at, out string? other) || other == block,
                  $"{name}: {block} and {other} both claim point {at}");
                claimed[at] = block;
            }
            check.Expect(claimed.Count <= Budget, $"{name} declares {claimed.Count} blocks, past the per-stage budget of {Budget}");
        }
        check.Expect(decls >= Blocks.Count, $"only {decls} declarations found for {Blocks.Count} blocks -- the pattern stopped matching");
        CullBlock.Pin(check);
        DeferredBlock.Pin(check);
        SsaoBlock.Pin(check);
        // ...and the override guard's own predicate: a stage that kept a member as a loose uniform is refused,
        // the shipped stages pass, and a stage that names no member of any block is left alone.
        check.Expect(!ShaderContract.BlocksSafe("#version 460 core\nuniform int uDirs;\nvoid main() { }"),
          "a stage declaring uDirs loose is refused as a pre-block override");
        check.Expect(!ShaderContract.BlocksSafe(ShaderSource.Get("cx_cullblk.glsl").Replace("int uDrawN;", "int uDrawnN;", System.StringComparison.Ordinal)),
          "...and so is a block whose body is not the library's");
        check.Expect(ShaderContract.BlocksSafe(ShaderSource.Get("cx_ssao_f.glsl"))
          && ShaderContract.BlocksSafe(ShaderSource.Get("cx_cull_c.glsl"))
          && ShaderContract.BlocksSafe(ShaderSource.Get("cx_deferred_cs.glsl")),
          "the shipped stages that read a block pass the guard");
        check.Expect(ShaderContract.BlocksSafe("#version 460 core\nvoid main() { }"), "a stage that reads no block is untouched");
        var owner = new Dictionary<uint, string>();
        foreach ((string block, uint at) in Blocks)
            if (at < Shared && !owner.TryAdd(at, block))
                check.Expect(false, $"persistent point {at} is shared by {owner[at]} and {block}");
        log.Info(check.Ok ? $"UboSlots.SelfCheck: passed ({decls} declarations under {Budget} points)" : "UboSlots.SelfCheck: FAILED");
        return check.Ok;
    }
}
