using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Deferred ground decals: ahead of the opaque pass every ground decal lying on the prepassed surface
/// over-blends its albedo into a screen layer, and each surface drawn under the nearest decal (ground, cutout, wash
/// interior) folds that layer into its albedo, so surface and decal are lit once. The rest stays in the blended
/// pass: <c>cx_ownlit_head</c>'s <c>CxDecalOn</c> splits every decal fragment between the two on the same inputs.</summary>
internal static unsafe class DecalLayer
{
    /// <summary>Image units the lit stage reads the layer and its surface depth on (<c>cx_ownlit_head</c>). Above
    /// the three every compute pass binds, so no dispatch between the opaque and blended passes rebinds them.</summary>
    internal const uint ImgLayer = 6, ImgSurf = 7;

    /// <summary>What <see cref="Issue"/> does with a lit program's <c>uDecal</c> right now.</summary>
    internal const int ModeNone = 0, ModeFold = 1, ModeSkip = 2, ModeWrite = 3;

    /// <summary>Everything the GL context owns, plus the frame's latches and the period's counters, in one
    /// object: context loss replaces it, keeping only the log.</summary>
    private sealed class DecalState
    {
        public ExtenderLog? Log;
        public uint Fbo, Layer, Surf;
        public int W, H;
        public bool Failed;
        /// <summary>The layer holds this frame's decals, bound for reading: the opaque pass folds it and the
        /// blended pass drops what it took.</summary>
        public bool Live;
        public int Mode;
        /// <summary>The projection's M33 and M43, which turn a window depth back into a view depth.</summary>
        public float Z0, Z1;
        public long Frames, Groups;
        /// <summary>Recipes already named by <see cref="NoteRefused"/>, so each is logged once.</summary>
        public readonly int[] Refused = new int[RefusedMax];
        public int RefusedN;
    }

    /// <summary>The decal depth a pixel no decal reached clears to: farther than any surface, so none folds there.</summary>
    internal const float NoDecal = 1e30f;

    /// <summary>Flat see-through sheets <see cref="NoteRefused"/> names per session.</summary>
    private const int RefusedMax = 64;

    private static DecalState _s = new();

    internal static int Mode => _s.Mode;
    internal static float Z0 => _s.Z0;
    internal static float Z1 => _s.Z1;

    internal static void Attach(ExtenderLog log) => _s.Log = log;

    /// <summary>The terms <see cref="Z0"/> and <see cref="Z1"/> carry, published for the Vulkan frame, whose decal
    /// pass is its own and never reaches <see cref="Run"/>.</summary>
    internal static void SetCamera(in Matrix4x4 proj) { _s.Z0 = proj.M33; _s.Z1 = proj.M43; }

    /// <summary>Context loss: the names went with it.</summary>
    internal static void Forget() => _s = new DecalState { Log = _s.Log };

    internal static void NoteRefused(int recipe, AlloyClass cls, string why)
    {
        var s = _s;
        if (s.RefusedN >= RefusedMax) return;
        for (int k = 0; k < s.RefusedN; k++) if (s.Refused[k] == recipe) return;
        s.Refused[s.RefusedN++] = recipe;
        s.Log?.Info($"[decals] flat sheet '{Db.DrawRecipe.Model(recipe)?.Name}' (tex '{Db.DrawRecipe.TexName(recipe, 0)}') "
                  + $"draws as a {cls.ToString().ToLowerInvariant()} wash, not a ground decal: {why}");
    }

    internal static string Stats(int frames)
    {
        var s = _s;
        long f = s.Frames, g = s.Groups;
        s.Frames = 0; s.Groups = 0;
        return s.Failed ? "decals STOOD DOWN" : f == 0 ? "decals idle"
             : $"decals {(float)g / f:F1} batches/frame folded on {(float)f / System.Math.Max(frames, 1) * 100f:F0}% of frames";
    }

    /// <summary>Off-device: the C# image units are the ones the lit stage declares, and the stage's writer and its
    /// blended-pass drop ask the same <c>CxDecalOn</c>. Oracle: the embedded GLSL text.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DecalLayer.SelfCheck");
        string head = AlloyRenderer.OwnedLitFsHead, synth = ShaderSource.Get("cx_ownlit_synth.glsl");
        check.Expect(head.Contains($"layout(rgba16f, binding = {ImgLayer}) readonly uniform image2D uDecalLayer;"),
          "the albedo layer's image unit is the one cx_ownlit_head declares");
        check.Expect(head.Contains($"layout(rg32f, binding = {ImgSurf}) readonly uniform image2D uDecalSurf;"),
          "the surface depth's image unit is the one cx_ownlit_head declares");
        check.Expect(synth.Contains("oDecalSurf = vec2(z, vDepth);")
          && synth.Contains("CxDecalOn(gl_FragCoord.z, imageLoad(uDecalSurf, dp).g)")
          && synth.Contains("uDecal == 2 && CxDecalOn(imageLoad(uDecalSurf, ivec2(gl_FragCoord.xy)).r, vDepth)"),
          "the writer stores (prepass depth, decal depth); the blended skip reads the first and a folding surface the second");
        check.Expect(synth.Contains("!CxDecalOn(z, vDepth)) discard;")
          && synth.Contains("uDecal == 2 && CxDecalOn(imageLoad(uDecalSurf, ivec2(gl_FragCoord.xy)).r, vDepth)) discard;"),
          "the writer and the blended pass split decal fragments on the same test");
        check.Expect(synth.Contains($"uDecal == {ModeFold}") && synth.Contains($"uDecal == {ModeSkip}"),
          "the fold and skip modes are the values the stage tests");
        check.Expect(ShaderSource.Get("cx_ownlit_v.glsl").Contains("invariant gl_Position;")
          && synth.Contains("|| uSolid != 0 && s < CX_SOLID_ALPHA;"),
          "a split wash's interior and edge draws lay one depth, so GL_LESS drops exactly what the interior drew");
        check.Expect(synth.IndexOf("earlyA = texture(uTex, vUV, uLodBias).a;") is > 0 and var early
          && early < synth.IndexOf("CxSunShadow(") && early < synth.IndexOf("uNrmTex, uv, uLodBias).rg")
          && synth.Contains("if (CxCoverCut(coverage, d.rot.w, du1, du2, cutSize, cutMip)) discard;"),
          "a plain-diffuse texel a pass discards on coverage never pays for the normal map or the sun's shadow taps");
        check.Expect(synth.IndexOf("if (earlyA >= 0.0) texel.a = earlyA;") is > 0 and var reuse
          && reuse > synth.IndexOf("vec4 texel = ") && reuse < synth.IndexOf("float coverage = "),
          "the full cut tests the alpha sampled before the early discard, the one the depth prepass tested");
        log.Info(check.Ok ? "DecalLayer.SelfCheck: passed" : "DecalLayer.SelfCheck: FAILED");
    }
}
