using System.IO;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Anti-aliasing as a post-stack stage (ADR 0020). The client's anti-aliasing option sizes the
/// ENGINE's subframe renderbuffers (<c>Camera::RefreshRenderSubframeResources</c> @0x5a6610), which the Alloy
/// frame never lands in, so under Alloy the option was inert. Alloy answers it with SMAA 1x on the resolved
/// display-space frame, and the option's sample count picks the preset: 2 MEDIUM, 4 HIGH, 8 and 16 ULTRA.</summary>
internal static unsafe partial class SmaaStage
{
    /// <summary><c>g_nMSAA</c>, the option's live value.</summary>
    private static int* _msaa;
    /// <summary>The preset compiled in (0 none, 1 MEDIUM, 2 HIGH, 3 ULTRA): the <c>$AA$</c> token
    /// <see cref="ShaderResource.Tokens"/> expands, so an override reads the same preset as the embedded text.</summary>
    internal static int Level { get; private set; }
    /// <summary>The three passes' resrefs (override points), same rules as every post-chain stage.</summary>
    internal const string ShdEdgeFs = "cx_smaaedge_fs", ShdWeightFs = "cx_smaawgt_fs", ShdBlendFs = "cx_smaablend_fs";
    /// <summary>The reference tables, RG8 160x560 and R8 64x16, as the repo ships them (Textures/*.h upstream).</summary>
    internal const int AreaW = 160, AreaH = 560, SearchW = 64, SearchH = 16;

    /// <summary>From <see cref="PostStack.Attach"/>: resolve the option's global.</summary>
    internal static void Attach(ExtenderLog log)
    {
        _msaa = (int*)Native.Sym(SymbolMap.MsaaSamples);
        if (_msaa == null) log.Info("SmaaStage: g_nMSAA is unresolved -- the anti-aliasing option stays inert");
    }

    /// <summary>The option's sample count -> preset level; pure, so the self-check can assert it.</summary>
    internal static int LevelFor(int samples) => samples >= 8 ? 3 : samples >= 4 ? 2 : samples >= 2 ? 1 : 0;
    /// <summary>The option as set right now, in MSAA samples; 0 when the global is unresolved.</summary>
    internal static int Samples => _msaa == null ? 0 : *_msaa;
    /// <summary>The stage's per-frame gate: the option is on, nothing of ours failed to build, and TAA is
    /// off -- the two are exclusive, because TAA smooths what SMAA smooths and more.</summary>
    internal static bool Active => !_vk.Failed && LevelFor(Samples) > 0 && !PostChain.TaaOn;
    internal static string PresetName(int level) => level switch { 3 => "ULTRA", 2 => "HIGH", 1 => "MEDIUM", _ => "off" };
    /// <summary>The /post and /knobs term: option, preset, and whether the stage is dropped.</summary>
    internal static string Describe()
        => $"(aa option {Samples}x -> SMAA {PresetName(LevelFor(Samples))}{(_vk.Failed ? ", FAILED" : PostChain.TaaOn ? ", replaced by TAA" : "")})";

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

    /// <summary>Off-device: the option maps to the presets, the three passes compose on the port, and the
    /// tables are in the build at their reference sizes.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(LevelFor(0) == 0 && LevelFor(2) == 1 && LevelFor(4) == 2 && LevelFor(8) == 3 && LevelFor(16) == 3,
          "0/2/4/8/16 samples -> off/MEDIUM/HIGH/ULTRA/ULTRA");
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
