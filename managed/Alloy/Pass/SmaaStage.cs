using System.IO;
using CX.Framework;

namespace CX.Alloy;

/// <summary>SMAA 1x as a post-stack stage (ADR 0020) on the resolved display-space frame, drawn when
/// <c>post.aa</c> picks it, at the preset <c>post.smaa</c> names: 1 MEDIUM, 2 HIGH, 3 ULTRA.</summary>
internal static partial class SmaaStage
{
    /// <summary>The preset compiled in (0 none, 1 MEDIUM, 2 HIGH, 3 ULTRA): the <c>$AA$</c> token
    /// <see cref="ShaderResource.Tokens"/> expands, so an override reads the same preset as the embedded text.</summary>
    internal static int Level { get; private set; }
    /// <summary>The three passes' resrefs (override points), same rules as every post-chain stage.</summary>
    internal const string ShdEdgeFs = "cx_smaaedge_fs", ShdWeightFs = "cx_smaawgt_fs", ShdBlendFs = "cx_smaablend_fs";
    /// <summary>The reference tables, RG8 160x560 and R8 64x16, as the repo ships them (Textures/*.h upstream).</summary>
    internal const int AreaW = 160, AreaH = 560, SearchW = 64, SearchH = 16;

    /// <summary>The stage's per-frame gate: <c>post.aa</c> picks SMAA and nothing of ours failed to build.</summary>
    internal static bool Active => !_vk.Failed && Post.Aa.V == Post.AaSmaa;
    internal static string PresetName(int level) => level switch { 3 => "ULTRA", 2 => "HIGH", 1 => "MEDIUM", _ => "off" };
    /// <summary>The /post and /knobs term: the preset, and whether the stage draws.</summary>
    internal static string Describe()
        => $"(SMAA {PresetName(Post.Smaa.V)}{(_vk.Failed ? ", FAILED" : Active ? "" : ", not picked")})";

    /// <summary>Embedded table <paramref name="resource"/>'s bytes, or null when it is missing or not <paramref name="len"/> long.</summary>
    private static byte[]? TableBytes(string resource, int len)
    {
        using Stream? s = typeof(SmaaStage).Assembly.GetManifestResourceStream(resource);
        if (s == null || s.Length != len) return null;
        byte[] bytes = new byte[len];
        s.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>Pass 2: blend weights from the edges, through the area and search tables.</summary>
    internal static string WeightFs => ShaderSource.Get("cx_smaawgt_f.glsl");

    /// <summary>Pass 3, the stack stage: the neighbourhood blend of uSrc by the weights. Display space in
    /// and out, like every stage; the incoming <c>c</c> is re-fetched bilinearly by the reference. Carries
    /// the shared defs for the library's sake only -- no preset macro reaches this pass, which is why a
    /// live preset change rebuilds the other two programs and not this one.</summary>
    internal static string BlendStageFs => ShaderSource.Get("cx_smaablend_f.glsl");

    /// <summary>Off-device: the presets are named, the three passes compose on the port, and the tables are in
    /// the build at their reference sizes. Oracles: hand-written preset names and the reference table sizes.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(PresetName(1) == "MEDIUM" && PresetName(2) == "HIGH" && PresetName(3) == "ULTRA", "post.smaa 1/2/3 -> MEDIUM/HIGH/ULTRA");
        foreach (string r in new[] { ShdEdgeFs, ShdWeightFs, ShdBlendFs })
            check.Expect(ShaderStages.Source(r).Contains("#define SMAA_GLSL_4 1") && ShaderStages.Source(r).Contains("float2 SMAALumaEdgeDetectionPS("),
              $"{r} carries the port + library");
        foreach (string r in new[] { ShdEdgeFs, ShdWeightFs })
            check.Expect(ShaderResource.Tokens(ShaderStages.Source(r)).Contains($"#define CX_AA_LEVEL {Level}\n"), $"{r} takes the preset from $AA$");
        check.Expect(ShaderContract.SlotSafe(ShaderStages.Source(ShdWeightFs), "uEdges", "uArea", "uSearch"), "the weight pass binds its three tables");
        using (Stream? a = typeof(SmaaStage).Assembly.GetManifestResourceStream("SmaaArea"))
        using (Stream? s = typeof(SmaaStage).Assembly.GetManifestResourceStream("SmaaSearch"))
            check.Expect(a?.Length == AreaW * AreaH * 2 && s?.Length == SearchW * SearchH, "the area and search tables are embedded at 160x560 RG8 and 64x16 R8");
        log.Info(check.Ok ? "SmaaStage.SelfCheck: passed" : "SmaaStage.SelfCheck: FAILED");
        return check.Ok;
    }
}
