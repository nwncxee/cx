using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>The grass pass's CPU half for the Vulkan frame: the same gate, bake and plan GL's draw runs, the tables
/// the pass sends, and the stages as Vulkan compiles them -- each record read from the scatter's output by instance,
/// since a Vulkan pipeline here takes one vertex stream and the scatter's is a second.</summary>
internal static unsafe partial class GrassField
{
    /// <summary>What the Vulkan copies hold: the resident faces sent since the last forget, the push field's
    /// generation (moved whenever <see cref="BuildPush"/> rewrites the field), and this frame's textures.</summary>
    private sealed class GrassVkState
    {
        public int FacesSent;
        public long PushGen;
        /// <summary>Pushers in the list the push field currently holds (-1 = none that can be trusted).</summary>
        public int PushWasN = -1;
        public readonly int[] Tids = new int[MaxGrassMaterials];
        public readonly bool[] Live = new bool[MaxGrassMaterials];
        /// <summary>The last <see cref="StageVk"/>'s plan, for the uniform writers.</summary>
        public FramePlan Plan;
    }

    private static GrassVkState _vkg = new();

    /// <summary>One Vulkan frame's grass as <see cref="StageVk"/> planned it: the visible patches (0 = nothing to draw),
    /// the scatter's split, one past the highest material, the two blade regions and the card slots.</summary>
    internal readonly record struct VkFrame(int Patches, int Split, int Mats, int BladeNear, int BladeFar, int Slots);

    /// <summary>A Vulkan program's locations, resolved against its block.</summary>
    internal sealed class VkLocs
    {
        internal Loc L;
        internal AlloyRenderer.FeatLoc F;
    }

    /// <summary>The two graphics stages: the cards and the procedural blades.</summary>
    internal enum VkStage { Cards, Blades }

    /// <summary>The indirect commands: blades near and far, then a card slot each; four words a command.</summary>
    internal const int VkCmds = Cmds;
    /// <summary>Bytes of the resident faces, the scatter's records, and the push field.</summary>
    internal static long VkFaceBytes => (long)MaxFaces * sizeof(GrassFace);
    internal const long VkRecordBytes = (long)RecordCap * InstFloats * sizeof(float);
    internal const int VkPushRes = PushRes;

    /// <summary>Gate, bake and plan the frame for the Vulkan pass as <see cref="Draw"/> does for GL. False hands the
    /// class back to the engine (switched off, the player's option off, stood down); true owns it this pass, with
    /// <c>Patches</c> 0 when nothing is drawn. <paramref name="bladeProg"/>: the blade program built on Vulkan.</summary>
    internal static bool StageVk(Matrix4x4* vp, bool bladeProg, out VkFrame f)
    {
        f = default;
        if (_off || !Grass.Enabled.V || _down.Failed || _binBucket.IsNull || *_grassEnabled == 0) return false;
        EnsureCpu();
        _drawnGen = AlloyRenderer.PassGen;
        if (Grass.Density.V <= 0f || Prepare(vp) == 0) return true;
        GrassVkState s = _vkg;
        int n;
        FramePlan p;
        fixed (int* tids = s.Tids)
        fixed (bool* live = s.Live)
            n = Plan(vp, bladeProg, tids, live, out p);
        if (n == 0) return true;
        s.Plan = p;
        f = new VkFrame(n, p.Split, Math.Max(p.Mats, 1), _bladeNear, _bladeFar, _slotCount);
        return true;
    }

    /// <summary>This frame's visible-patch table, two uvec4 a patch.</summary>
    internal static ReadOnlySpan<uint> VkPatches(int n) => new(_visTable, 0, n * 8);

    /// <summary>This frame's material table, <paramref name="mats"/> materials.</summary>
    internal static ReadOnlySpan<float> VkMats(int mats) => new(_matTable, 0, mats * MatVec4 * 4);

    /// <summary>The frame's indirect commands before the scatter appends.</summary>
    internal static void VkCommands(Span<uint> cmd)
    {
        fixed (uint* c = cmd) FillCommands(c);
    }

    /// <summary>The faces baked since the Vulkan copy was last sent, and where they go in it; marked sent.</summary>
    internal static ReadOnlySpan<byte> VkNewFaces(out long at)
    {
        GrassVkState s = _vkg;
        at = (long)s.FacesSent * sizeof(GrassFace);
        if (_faces == null || _faceCount <= s.FacesSent) return default;
        ReadOnlySpan<byte> b = MemoryMarshal.AsBytes(_faces.AsSpan(s.FacesSent, _faceCount - s.FacesSent));
        s.FacesSent = _faceCount;
        return b;
    }

    /// <summary>The push field when it moved since generation <paramref name="sent"/>, which it then advances to.</summary>
    internal static ReadOnlySpan<byte> VkPush(ref long sent)
    {
        if (_push == null || sent == _vkg.PushGen) return default;
        sent = _vkg.PushGen;
        return _push;
    }

    /// <summary>Card slot <paramref name="slot"/>'s atlas (GL name, 0 when not uploaded yet) and its material's colours.</summary>
    internal static int VkSlot(int slot, out Vector3 dif, out Vector3 amb)
    {
        int mat = _slotMat[slot];
        CAurGrassInfo info = GrassInfo(mat);
        dif = default; amb = default;
        if (info.IsNull) return 0;
        dif = info.Diffuse; amb = info.Ambient;
        return _vkg.Tids[mat];
    }

    /// <summary>The scatter's terms into the open capture, <paramref name="clamp"/> its pass switch.</summary>
    internal static void VkScatterTerms(in ScatterLoc l, Matrix4x4* vp, int clamp)
    {
        Span<Vector4> planes = stackalloc Vector4[6];
        Frustum(in *vp, planes);
        FramePlan p = _vkg.Plan;
        Vector3 eye = p.Eye;
        float* pw = stackalloc float[3] { _pushOriginX, _pushOriginY, _pushPerUnit / PushRes };
        ScatterTerms(in l, planes, &eye, p.Near, p.Far, pw);
        LooseBlock.Set(l.Clamp, clamp);
    }

    /// <summary>A colour stage's terms into the open capture, as <see cref="Issue"/> sets them for GL with no prepass;
    /// the cards' own three when <paramref name="cards"/>. The pass block is staged by the light rig's bind inside.</summary>
    internal static void VkColourTerms(VkLocs v, Matrix4x4* vp, int* viewport, float near, float far, bool lightingOn,
                                       bool ground, bool cards)
    {
        FramePlan p = _vkg.Plan;
        Vector3 eye = p.Eye;
        float* pw = stackalloc float[3] { _pushOriginX, _pushOriginY, _pushPerUnit / PushRes };
        ColourUniforms(in v.L, in v.F, vp, &eye, p.Near, p.Far, pw, viewport, near, far, lightingOn, ground);
        if (!cards) return;
        LooseBlock.Set(v.L.Msaa, 0);
        LooseBlock.Set(v.L.SoftEdge, 0);
        LooseBlock.Set(v.L.GroundViz, Grass.GViz.V);
    }

    /// <summary>The strip pairs of blade draw <paramref name="far"/>.</summary>
    internal static int VkSegments(bool far) => far ? FarSegments : NearSegments;

    /// <summary>Does this frame take the ground copy: the root tint, or any blade?</summary>
    internal static bool VkWantsGround(in VkFrame f) => Grass.GTint.V > 0f || f.BladeNear + f.BladeFar > 0;

    /// <summary>Stage <paramref name="k"/>'s two texts as GL's build makes them -- the overridable pair, rebuilt from the
    /// embedded text when an override still encodes to display space -- before the compile-time splice. The sky view
    /// stays in: a Vulkan stage has the units GL's <c>WithSkyView</c> checks for.</summary>
    internal static (string Vs, string Fs) VkTexts(VkStage k, ExtenderLog? log)
    {
        string what = $"GrassField Vulkan {k}";
        if (k == VkStage.Blades)
        {
            string bvs = ShaderStages.Source(ShdBladeVs), bfs = ShaderStages.Source(ShdBladeFs);
            ShaderTexts.Pair(ShdBladeVs, bvs, ShdBladeFs, bfs, log, what, out string v, out string f);
            return ShaderContract.LinearSafe(f) ? (v, f) : (bvs, bfs);
        }
        string fs = EmbeddedFs, vs = ShaderStages.Source(ShdGrassVs);
        ShaderTexts.Pair(ShdGrassVs, vs, ShdGrassFs, fs, log, what, out string gv, out string gf);
        if (!(ShaderContract.LinearSafe(gf) && ShaderContract.SlotSafe(gf, "uTex"))) { gv = GrassVs; gf = fs; }
        return (gv, gf);
    }

    /// <summary>The samplers the grass stages declare without a binding, on the units GL's build gives them.</summary>
    internal static readonly IReadOnlyDictionary<string, uint> VkUnits = new Dictionary<string, uint>(VkPrograms.Units)
    {
        ["uPushTex"] = PushUnit, ["uGround"] = TextureSlots.GrassGround, ["uSunDynShadow"] = TextureSlots.SunShadowDynGrass,
    };

    private static readonly Regex VkAttrib = new(@"layout\(location = [0-4]\) in vec[234] (aPos|aUV|iPos|iParam|iNrm);[^\n]*\n");

    /// <summary><paramref name="vs"/> with its vertex inputs replaced: the three instance vec4 read from the scatter's
    /// records at the instance index (its region's first record plus the instance), the card template's position and
    /// cell uv from the same corner table <see cref="EnsureBuffers"/> uploads for GL.</summary>
    internal static string VkInstanced(string vs)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"layout(std430, binding = {SsboSlots.GrassInst}) readonly buffer CxGrassInst {{ vec4 uGrassInst[]; }};\n");
        sb.Append($"#define iPos uGrassInst[gl_InstanceIndex * {InstFloats / 4}]\n");
        sb.Append($"#define iParam uGrassInst[gl_InstanceIndex * {InstFloats / 4} + 1]\n");
        sb.Append($"#define iNrm uGrassInst[gl_InstanceIndex * {InstFloats / 4} + 2]\n");
        sb.Append("const vec3 cxGrassTpl[6] = vec3[6](");
        for (int i = 0; i < 6; i++)
            sb.Append(System.FormattableString.Invariant($"{(i > 0 ? ", " : "")}vec3({Corner[i * 3]:0.0}, {Corner[i * 3 + 1]:0.0}, {Corner[i * 3 + 2]:0.0})"));
        sb.Append(");\n");
        sb.Append("#define aPos (cxGrassTpl[gl_VertexID % 6].x * (gl_VertexID < 6 ? vec3(0.5, 0.0, 0.0) : vec3(0.0, 0.5, 0.0)) "
                + "+ vec3(0.0, 0.0, cxGrassTpl[gl_VertexID % 6].y))\n");
        sb.Append("#define aUV vec2(cxGrassTpl[gl_VertexID % 6].z, cxGrassTpl[gl_VertexID % 6].y)\n");
        bool first = true;
        return VkAttrib.Replace(vs, _ =>
        {
            if (!first) return "";
            first = false;
            return sb.ToString();
        });
    }

    /// <summary>Off-device: the Vulkan vertex text keeps no vertex input and reads every attribute GL's VAO feeds.
    /// Oracle: the embedded GLSL text, whose declarations are matched and whose reads are kept.</summary>
    internal static void VkSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "GrassField.VkSelfCheck");
        string cards = VkInstanced(ShaderStages.Source(ShdGrassVs)), blades = VkInstanced(ShaderStages.Source(ShdBladeVs));
        check.Expect(!cards.Contains(" in vec4 iPos;") && !cards.Contains(" in vec3 aPos;") && !cards.Contains(" in vec2 aUV;"),
            "the card stage declares no vertex input on Vulkan");
        check.Expect(!blades.Contains(" in vec4 iNrm;") && blades.Contains("#define iNrm uGrassInst[gl_InstanceIndex * 3 + 2]"),
            "the blade stage reads its record by instance");
        check.Expect(cards.Contains("vec3(-1.0, 0.0, 0.0), vec3(1.0, 0.0, 1.0), vec3(1.0, 1.0, 1.0)"),
            "the template's first three corners as (side, z, u)");
        check.Expect(cards.Contains("vCellUv = aUV;") && blades.Contains("vec3 root = iPos.xyz;"), "the reads stay as GL wrote them");
        log.Info(check.Ok ? "GrassField.VkSelfCheck: passed" : "GrassField.VkSelfCheck: FAILED");
    }
}
