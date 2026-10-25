using System.Collections.Generic;
using System.Text.RegularExpressions;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The shader-storage binding contract, written down once, like <see cref="TextureSlots"/>. A point past
/// the driver's <c>MAX_SHADER_STORAGE_BUFFER_BINDINGS</c> is a compile error that costs the whole program; two
/// buffers on one point are no error at all -- the reader gets the other buffer.</summary>
internal static class SsboSlots
{
    /// <summary>The whole budget: Intel's Windows GL reports 16 binding points, the smallest a supported driver
    /// reports (the GL 4.6 floor is 8).</summary>
    internal const uint Budget = 16;

    /// <summary>Persistent: bound once (at upload, or at the bracket's start) and read by later passes without a
    /// rebind, so each owns its point outright and nothing else may bind there.</summary>
    internal const uint Draw = 0, DrawIdx = 1, Bones = 2, TexH = 3, OitHeads = 4, OitNodes = 5, ViewTbl = 6, Plt = 7;
    /// <summary>The first shared point. Everything from here up is bound by its reader right before its own
    /// draw or dispatch, so a compute pass may take any of them in between.</summary>
    internal const uint Shared = 8;

    /// <summary>The light rig, rebound at every lighting program's bind (<see cref="AlloyLights.BindForDraw"/>,
    /// <see cref="LightmapBake.BindForDraw"/>): one program reads all six, so they are distinct.</summary>
    internal const uint Lights = 8, ClusterCount = 9, ClusterIdx = 10, Movers = 11, Pair = 12, Chart = 13;
    /// <summary>Above the rig, for a pass whose program also reads it.</summary>
    internal const uint LightStats = 14, DeferredStats = 14, PairStats = 14, GrassMats = 14,
                        ParticleRecs = 14, ParticleIdx = 15;
    /// <summary>Compute scratch that reads no rig.</summary>
    internal const uint ParticleSpans = 8, ParticleGroups = 9,
                        CullSrc = 8, CullSphere = 9, CullCmd = 10, CullOut = 11, CullCount = 12, CullKey = 13, CullRej = 14,
                        GatherSrc = 8, GatherCmd = 10, GatherCount = 11, CastSphere = 12,
                        GrassFaces = 8, GrassPatches = 9, GrassOut = 10, GrassCmd = 11,
                        HaloVis = 9;
    /// <summary>The probe's SH output, which its dispatch unbinds after: the particle index point, whose every
    /// reader rebinds it, so the unbind cannot strand a lighting program.</summary>
    internal const uint ProbeSh = 15;
    /// <summary>The content table (<see cref="Db.ContentTable"/>), on the same point: nothing else in the LIT bracket
    /// claims it, and every content program's bind -- colour, depth or caster -- rebinds it rather than trusting an
    /// earlier one, which is the rule for every tenant of this point.</summary>
    internal const uint Content = 15;
    /// <summary>Single-pass draws that read no rig.</summary>
    internal const uint Halos = 8, Glow = 8, Trig = 8, Gui = 8, BonesPrev = 8, DanglyPrev = 9;
    /// <summary>The 2D UI's command records, beside the GUI pass's own on <see cref="Gui"/> (a window replay sits between two of its
    /// runs); shares <see cref="DanglyPrev"/>'s point, whose reader binds before it draws.</summary>
    internal const uint UiCmds = 9;

    /// <summary>Every block name the GLSL declares, and the point C# binds it on.</summary>
    private static readonly Dictionary<string, uint> Blocks = new()
    {
        ["CxDrawBuf"] = Draw, ["CxDrawIdxBuf"] = DrawIdx, ["CxBoneBuf"] = Bones, ["CxTexH"] = TexH,
        ["CxOitHeads"] = OitHeads, ["CxOitNodes"] = OitNodes, ["CxBakeViews"] = ViewTbl, ["CxPltBuf"] = Plt,
        ["LightBuf"] = Lights, ["CxLightBuf"] = Lights, ["ClusterCount"] = ClusterCount, ["CxClusterCount"] = ClusterCount,
        ["ClusterIdx"] = ClusterIdx, ["CxClusterIdx"] = ClusterIdx, ["DynMovers"] = Movers, ["CxDynMovers"] = Movers,
        ["CxPairBuf"] = Pair, ["CxChartBuf"] = Chart,
        ["Stats"] = LightStats, ["CxDeferredStats"] = DeferredStats, ["CxPairStats"] = PairStats, ["CxGrassMats"] = GrassMats,
        ["CxParticleBuf"] = ParticleRecs, ["CxParticleIdx"] = ParticleIdx,
        ["CxSpanBuf"] = ParticleSpans, ["CxGroupBuf"] = ParticleGroups,
        ["CxCullSrc"] = CullSrc, ["CxCullSphere"] = CullSphere, ["CxCullCmd"] = CullCmd, ["CxCullOutIdx"] = CullOut,
        ["CxCullCount"] = CullCount, ["CxCullKey"] = CullKey, ["CxCullRej"] = CullRej,
        ["CxGatherSrc"] = GatherSrc, ["CxGatherCmd"] = GatherCmd, ["CxGatherCount"] = GatherCount, ["CxCastSphere"] = CastSphere,
        ["CxGrassFaces"] = GrassFaces, ["CxGrassPatches"] = GrassPatches, ["CxGrassOut"] = GrassOut, ["CxGrassCmd"] = GrassCmd,
        ["CxHaloBuf"] = Halos, ["CxHaloVis"] = HaloVis, ["CxHiliteGlowBuf"] = Glow, ["CxTrigBuf"] = Trig, ["CxGuiBuf"] = Gui,
        ["CxProbeShOut"] = ProbeSh, ["CxBonePrevBuf"] = BonesPrev, ["CxDanglyPrevBuf"] = DanglyPrev,
        ["CxContentBuf"] = Content, ["CxUiCmdBuf"] = UiCmds,
    };

    private static readonly Regex Decl = new(@"layout\(std430,\s*binding\s*=\s*(\d+)\)\s*(?:\w+\s+)*?buffer\s+(\w+)");

    /// <summary>Oracle: the GLSL text itself -- every embedded stage and library, includes spliced, plus the two
    /// stages and the bindless header composed in C#. Each declaration is under the budget, at its table point,
    /// and alone on that point within its stage; a persistent point holds only its own block.</summary>
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
                if (Blocks.TryGetValue(block, out uint want)) check.Expect(at == want, $"{name}: {block} on point {at}, SsboSlots binds {want}");
                else check.Expect(false, $"{name}: {block} has no row in SsboSlots");
                check.Expect(!claimed.TryGetValue(at, out string? other) || other == block,
                  $"{name}: {block} and {other} both claim point {at}");
                claimed[at] = block;
            }
        }
        check.Expect(decls >= Blocks.Count, $"only {decls} declarations found for {Blocks.Count} blocks -- the pattern stopped matching");
        var owner = new Dictionary<uint, string>();
        foreach ((string block, uint at) in Blocks)
            if (at < Shared && !owner.TryAdd(at, block))
                check.Expect(false, $"persistent point {at} is shared by {owner[at]} and {block}");
        log.Info(check.Ok ? $"SsboSlots.SelfCheck: passed ({decls} declarations under {Budget} points)" : "SsboSlots.SelfCheck: FAILED");
        return check.Ok;
    }
}
