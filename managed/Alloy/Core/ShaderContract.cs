using System;
using System.Linq;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

using static AlloyRenderer;

/// <summary>The Alloy pass's shader-contract predicates: pure string-to-bool over the embedded GLSL -- no GL,
/// no frame state, no engine pointer -- so the suite is callable and checkable off-device. Each assertion
/// covers a desync that fails SILENTLY as a misrender rather than an error (the per-draw SSBO binding and
/// stride, the cutout spelling, the clip expression, the bindless rewrite), which is why it gates arming.</summary>
internal static class ShaderContract
{
    /// <summary>A vertex stage whose depth the prepass may re-test: it computes <see cref="ClipExpr"/> and
    /// declares it invariant. The embedded stages satisfy this by construction (SelfCheck asserts it); a
    /// content override has to say so in its own text.</summary>
    public static bool DepthSafe(string vs)
        => vs.Contains(ClipExpr) && vs.Contains("invariant gl_Position;");

    /// <summary>Does this stage's text declare the per-draw record the CPU is staging? The layout is a
    /// contract between <see cref="AlloyRenderer.DrawFloats"/> and whatever GLSL actually compiled, and a
    /// content override is a copy of that GLSL that goes stale the moment the record grows: a short
    /// declaration reads each draw's matrix and colours out of its NEIGHBOUR's record. Counted, not matched.</summary>
    public static bool RecordSafe(string src) => RecordSafe(src, "CxDraw", AlloyRenderer.DrawFloats);

    /// <summary>The same count, for any per-draw record a C# stride stages. <paramref name="floats"/> is the
    /// whole stride, counted by FIELD SIZE: a vec4-only count is blind to an appended <c>mat4</c>, and a stale
    /// override then reads a 44-float stride against a 60-float buffer.</summary>
    public static bool RecordSafe(string src, string structName, int floats)
    {
        int at = src.IndexOf("struct " + structName, StringComparison.Ordinal);
        if (at < 0) return true;                        // a stage that never declares it cannot disagree
        int end = src.IndexOf('}', at);
        string decl = end < 0 ? src[at..] : src[at..end];
        return 16 * (decl.Split("mat4").Length - 1) + 4 * (decl.Split("vec4").Length - 1) == floats;
    }

    /// <summary>Does this grass vertex stage reconstruct last frame's blade? An override without it links
    /// CLEANLY, writes nothing to the velocity outs and leaves the camera's field standing over every blade
    /// -- exactly the smear the sweep exists to remove. Named terms, so an override may restyle the sway as
    /// long as it says where last frame's is.</summary>
    public static bool SwaySafe(string vs)
        => vs.Contains("CxGrassSway(CxPrevClock() * uWindSpeed, phase)") && vs.Contains("vPrev = uPrevVP");

    /// <summary>Does this stage pair carry the cutout and the per-draw alpha? An override without them links
    /// CLEANLY -- the record did not grow, the clip expression did not change, and <c>glUniform1f</c> on a -1
    /// location is a legal no-op -- and ships a world with no alpha discard and a constant 1.0 alpha: grass as
    /// opaque quads, windows as walls. Named terms, so an override may restyle around them, not drop them.</summary>
    public static bool CutoutSafe(string vs, string fs)
        // The probe is a SUBSTRING of the whole stage text, COMMENTS INCLUDED: a stage that merely mentions
        // e.g. CasterPass's env threshold by name takes the vAlpha arm on the strength of the comment and
        // fails against a vertex stage that forwards nothing. Say "the caster's env threshold", not its
        // identifier, anywhere inside a stage string.
        => fs.Contains("uAlphaCut") && (fs.Contains("vAlpha") ? vs.Contains("vAlpha") : fs.Contains("rot.w"));

    /// <summary><see cref="VanillaShader.HostText"/> over a two-line stage, for the assertions above.
    /// Null only if the rewrite found no main, which the assertions then trip on.</summary>
    private static string HostSample => VanillaShader.HostText(
        "#define LIGHTING 1\n#define FOO 2\nuniform sampler2D texUnit6;\nvoid main ()\n{\n    gl_FragColor = FragmentColor;\n}\n",
        "x", null, "selfcheck", new System.Collections.Generic.List<string>()) ?? "";

    /// <summary>Does this VERTEX stage carry the skinning branch? Same trap as <see cref="CutoutSafe"/>: an
    /// override without it links perfectly (the bone base rides in <c>feat.y</c>, the missing bone attributes
    /// are simply disabled) and renders every skinned robe in its BIND POSE, silently. On a server the HAK
    /// wins over a local override directory, so it is live content that breaks. Checked only when armed.</summary>
    public static bool SkinSafe(string vs, bool skinning) => !skinning || vs.Contains("CX_FEAT_SKIN");

    /// <summary>Does this FRAGMENT stage write LINEAR? One ending in <c>CxRevert(...)</c> links perfectly and
    /// writes display-space values into a target the resolve then encodes a SECOND time -- the frame is washed
    /// out end to end and nothing says so. The answer is to rebuild from the embedded text rather than log and
    /// carry on, because on a server the HAK wins over a local override directory.</summary>
    public static bool LinearSafe(string fs) => !fs.Contains("CxRevert") && !fs.Contains("CxSoftClip");

    /// <summary>The same guard from the other end, for the ONE stage that encodes: the resolve. It must encode
    /// and must not decode first -- an override opening with <c>CxLinear</c> on an already-linear target comes
    /// out a gamma dark, one that dropped the encode comes out washed out, and both link cleanly and say
    /// nothing. Named terms: the tonemap and the dither may be restyled, the colour-space decision may not.</summary>
    public static bool ResolveSafe(string fs) => !fs.Contains("CxLinear") && fs.Contains("CxEncode");

    /// <summary>The guard for a <see cref="PostStack"/> stage, which runs AFTER the resolve on a
    /// display-space image and hands one on: it must neither encode (a second encode, a gamma too bright) nor
    /// decode (the frame goes linear and a gamma dark). Named terms, like the two above.</summary>
    public static bool DisplaySafe(string fs) => !fs.Contains("CxLinear") && !fs.Contains("CxEncode");

    /// <summary>Does this FRAGMENT stage still declare the samplers the C# side binds units to? An override
    /// that renamed or dropped one links perfectly and leaves that sampler on unit 0 beside the diffuse: two
    /// of one type read each other's texture, two of DIFFERENT types are <c>INVALID_OPERATION</c> and every
    /// lit draw is dropped. Bound samplers only (meaningless under bindless); the names are the CALLER's.</summary>
    public static bool SlotSafe(string fs, params string[] samplers)
    {
        foreach (string s in samplers)
            if (!fs.Contains($"uniform sampler2D {s};") && !fs.Contains($"uniform samplerCube {s};"))
                return false;
        return true;
    }

    /// <summary>A stage that derives a draw index must reach its record THROUGH the slot map: the records are
    /// indexed by ownership slot and the draw order is not that order, so a stage still spelling
    /// <c>uDraw[uDrawBase + gl_DrawID]</c> reads a NEIGHBOUR's model matrix and textures while passing every
    /// other guard here. Stages with no <c>gl_DrawID</c> -- every fragment stage -- pass trivially.</summary>
    public static bool SlotIndexSafe(string src)
        => !src.Contains("gl_DrawID") || src.Contains("CxSlot(uDrawBase + gl_DrawID)");

    /// <summary>A fragment stage reading the chart UV needs a vertex stage that forwards it -- a stale
    /// <c>cx_ownlit_vs</c> override beside the embedded fragment stage leaves <c>vChart</c> unwritten (a link
    /// error on strict drivers, undefined reads on the rest). A fragment override that never reads it is fine
    /// either way: it simply never reads the lightmap.</summary>
    public static bool ChartSafe(string vs, string fs) => !fs.Contains("vChart") || vs.Contains("vChart");

    /// <summary>Does a stage that reads the pass terms read them from the <c>CxPass</c> block
    /// <see cref="AlloyRenderer.BindPass"/> fills? An override still declaring
    /// <c>uniform vec4 uLocalLight[64];</c> links cleanly and reads zeros -- a world with no local shadows and
    /// nothing in the log -- and one declaring a stale block reads every later member at the wrong offset.</summary>
    public static bool PassSafe(string src)
    {
        string? got = PassMembers(src);
        if (got != null) return got == PassMembers(ShaderSource.Get("cx_pass.glsl"));
        // every stage that read the pass terms before the block declared one of these three loose (the cube,
        // cascade and area libraries); the cluster build's own loose uGrid / uZ are not pass terms
        return !HasIdent(src, "uLocalLight") && !HasIdent(src, "uSunVp") && !HasIdent(src, "uAreaAmbient");
    }

    /// <summary>Does <paramref name="name"/> occur in <paramref name="src"/> as a whole identifier?</summary>
    private static bool HasIdent(string src, string name)
    {
        for (int at = src.IndexOf(name, StringComparison.Ordinal); at >= 0; at = src.IndexOf(name, at + 1, StringComparison.Ordinal))
        {
            int e = at + name.Length;
            if ((at == 0 || !IsIdent(src[at - 1])) && (e == src.Length || !IsIdent(src[e]))) return true;
        }
        return false;
    }

    private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Every stage head that sets <c>CX_LIGHT_LITE</c> (ADR 0029) -- the forward consumers of the
    /// point-light walk, vertex and fragment both.</summary>
    private static readonly string[] LightLiteHeads =
    {
        "cx_grass_vhead.glsl", "cx_grass_fhead.glsl", "cx_gblade_vhead.glsl", "cx_gblade_fhead.glsl",
        "cx_water_fhead.glsl", "cx_part_vhead.glsl", "cx_part_fhead.glsl", "cx_wx_vhead.glsl", "cx_wx_sphead.glsl",
    };

    /// <summary>...and that each of them still sets it. A head that lost the line renders identically and
    /// silently compiles the baked receiver's whole walk back into a blade of grass.</summary>
    private static bool LightLiteSet => LightLiteHeads.All(h => ShaderSource.Get(h).Contains("#define CX_LIGHT_LITE 1"));

    /// <summary>The body of <paramref name="src"/>'s <c>CxPass</c> declaration from its layout qualifier to
    /// the closing brace, comments and whitespace dropped; null when it declares none.</summary>
    private static string? PassMembers(string src)
    {
        int at = src.IndexOf("uniform CxPass", StringComparison.Ordinal);
        if (at < 0) return null;
        int start = src.LastIndexOf('\n', at) + 1, end = src.IndexOf("};", at, StringComparison.Ordinal);
        var sb = new System.Text.StringBuilder();
        foreach (string line in src[start..(end < 0 ? src.Length : end)].Split('\n'))
        {
            int c = line.IndexOf("//", StringComparison.Ordinal);
            foreach (char ch in c < 0 ? line : line[..c]) if (!char.IsWhiteSpace(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>The <c>CxPass</c> contract, off-device. <see cref="AlloyRenderer"/>'s <c>Pb*</c> offsets
    /// against a std140 layout computed from the GLSL declaration itself -- a member moved in the file and not
    /// in C# stages every term after it into its neighbour, silently -- then every embedded stage through
    /// <see cref="PassSafe"/>, and the predicate against a stale override, a reordered block and a restyled one.</summary>
    internal static bool PassCheck(ExtenderLog log)
    {
        var bad = new System.Collections.Generic.List<string>();
        string decl = ShaderSource.Get("cx_pass.glsl");
        string members = PassMembers(decl) ?? "";
        if (!members.StartsWith($"layout(std140,binding={AlloyRenderer.PassBinding})uniformCxPass{{", StringComparison.Ordinal))
            bad.Add("binding");
        int end = 0;
        var pinned = new[] {
            ("uLocalLight", AlloyRenderer.PbLight), ("uLocalDynPage", AlloyRenderer.PbDynPage), ("uLocalFade", AlloyRenderer.PbFade),
            ("uSunVp", AlloyRenderer.PbSunVp), ("uSunTexel", AlloyRenderer.PbSunTexel), ("uSunBias", AlloyRenderer.PbSunBias),
            ("uSunDyn", AlloyRenderer.PbSunDyn), ("uSunRadius", AlloyRenderer.PbSunRadius), ("uLocalSoft", AlloyRenderer.PbLocalSoft),
            ("uLocalPcss", AlloyRenderer.PbLocalPcss), ("uSunFocus", AlloyRenderer.PbSunFocus), ("uSunCascades", AlloyRenderer.PbSunCascades),
            ("uAreaAmbient", AlloyRenderer.PbAmbient), ("uLocalFloor", AlloyRenderer.PbLocalFloor), ("uAreaDiffuse", AlloyRenderer.PbDiffuse),
            ("uProbeMips", AlloyRenderer.PbProbeMips), ("uAreaDir", AlloyRenderer.PbDir), ("uProbeGain", AlloyRenderer.PbProbeGain),
            ("uLocalPlane", AlloyRenderer.PbLocalPlane), ("uSceneEnv", AlloyRenderer.PbSceneEnv),
            ("uTile", AlloyRenderer.PbTile), ("uGrid", AlloyRenderer.PbGrid), ("uReach", AlloyRenderer.PbReach),
            ("uFalloff", AlloyRenderer.PbFalloff), ("uWrap", AlloyRenderer.PbWrap), ("uZ", AlloyRenderer.PbZ),
            ("uAtten", AlloyRenderer.PbAtten), ("uHot", AlloyRenderer.PbHot), ("uSss", AlloyRenderer.PbSss),
            ("uWrapN", AlloyRenderer.PbWrapN), ("uDayDim", AlloyRenderer.PbDayDim), ("uRelevance", AlloyRenderer.PbRelevance),
            ("uBounds", AlloyRenderer.PbBounds), ("uShBudget", AlloyRenderer.PbShBudget),
            ("uLightFloor", AlloyRenderer.PbLightFloor), ("uFullModel", AlloyRenderer.PbFullModel),
            ("uSunPenum", AlloyRenderer.PbSunPenum), ("uSearchTaps", AlloyRenderer.PbSearchTaps) };
        foreach ((string name, int off) in pinned)
        {
            int at = Std140Offset(members, name, out int size);
            if (at != off) bad.Add($"{name} at {at}, C# stages {off}");
            end = Math.Max(end, at + size);
        }
        if (end > AlloyRenderer.PassBytes || members.Split(';').Length - 1 != pinned.Length) bad.Add($"the block is {end}B / has a member C# does not stage");
        foreach (string res in typeof(ShaderContract).Assembly.GetManifestResourceNames())
        {
            if (!res.EndsWith(".glsl", StringComparison.Ordinal)) continue;
            string src = ShaderSource.Get(res);
            if (src.Contains("#version") && !PassSafe(src)) bad.Add(res);
        }
        string stale = "#version 460 core\nuniform vec4 uLocalLight[64];\nvoid main() { }\n";
        string restyled = "// restyled\n" + decl.Replace("    vec4 uLocalLight[64];", "vec4   uLocalLight[64];  // moved comment");
        string swapped = decl.Replace("    vec3 uAreaAmbient;", "    vec3 uAreaXAmbient;").Replace("    vec3 uAreaDiffuse;", "    vec3 uAreaAmbient;");
        if (PassSafe(stale) || !PassSafe(restyled) || PassSafe(swapped) || !PassSafe("uniform ivec3 uGrid;\nvoid main() { }") || !PassSafe(decl))
            bad.Add("the predicate itself");
        if (bad.Count > 0) log.Info("ShaderContract.PassCheck: FAILED -- " + string.Join(", ", bad));
        return bad.Count == 0;
    }

    /// <summary>std140 byte offset of <paramref name="name"/> in a <see cref="PassMembers"/> body, and its size;
    /// -1 when absent. Covers the types the block uses: scalars, vec2-4, mat4 and arrays of them.</summary>
    private static int Std140Offset(string members, string name, out int size)
    {
        int off = 0;
        foreach (string m in members[(members.IndexOf('{') + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int n = m.IndexOf(name, StringComparison.Ordinal);
            int u = 0;
            while (u < m.Length - 1 && !(m[u] == 'u' && char.IsUpper(m[u + 1]))) u++;   // the name: its first u<Upper>
            string type = m[..u];
            (int align, int bytes) = type switch
            {
                "float" or "int" => (4, 4), "vec2" => (8, 8), "vec3" or "ivec3" => (16, 12), "vec4" => (16, 16), "mat4" => (16, 64),
                _ => (-1, 0),
            };
            if (align < 0) { size = 0; return -2; }   // a type this calculator does not know fails the pin, never passes it
            int lb = m.IndexOf('[');
            if (lb >= 0) { int count = int.Parse(m[(lb + 1)..m.IndexOf(']')]); align = 16; bytes = ((bytes + 15) & ~15) * count; }
            off = (off + align - 1) & ~(align - 1);
            if (n >= 0 && (m.Length == n + name.Length || m[n + name.Length] == '[')) { size = bytes; return off; }
            off += bytes;
        }
        size = 0;
        return -1;
    }

    /// <summary>The lit stage's whole sampler set: the diffuse, the map ladder and the probe cube.</summary>
    public static bool LitSlotSafe(string fs)
        => SlotSafe(fs, "uTex") && SlotSafe(fs, TextureSlots.MapSamplers) && SlotSafe(fs, "uProbe");

    /// <summary>Headless check of the one desync that fails SILENTLY: the per-draw SSBO's binding point and
    /// record stride live twice, as C# constants driving the staging writes and as literal GLSL in
    /// <see cref="AlloyRenderer.DrawBufDecl"/>. Verified against glslang reflection: model@0 rot@64 amb@80
    /// dif@96 emi@112 pbr@128 spc@144, array stride 160. False = do not arm; a log line would ship it.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        // #208: run FIRST and unconditionally -- it names the slot that disagreed, which the && chain below
        // would swallow.
        bool slots = TextureSlots.Check(log);
        bool ssbo = SsboSlots.Check(log);
        bool pass = PassCheck(log);
        // the record's field list, counted by field size: the 288-byte stride staged above (#392 and ADR 0026 grew it)
        bool ok = slots && ssbo && pass && DrawBufDecl.Contains($"binding = {BindDraw})")
                  && RecordSafe(DrawBufDecl) && DrawBufDecl.Contains("vec4 feat; mat4 prev; vec4 ext0; vec4 ext1; vec4 ext2; }")
                  && DrawBufDecl.Contains("vec4 emi") && DrawBufDecl.Contains("vec4 spc")
                  && DrawBufDecl.Contains("vec4 feat")
                  // #123: the feature ids are a contract between the ShaderFeature enum (staged into
                  // d.feat.x) and the GLSL that switches on it. Drift renders the wrong effect on the wrong
                  // content -- roofs swaying, plants dropping 500m -- with nothing in the log.
                  && FeatDecl.Contains($"CX_FEAT_WINDGRASS {(int)ShaderFeature.WindGrass}")
                  && FeatDecl.Contains($"CX_FEAT_WINDLEAF  {(int)ShaderFeature.WindLeaf}")
                  && FeatDecl.Contains($"CX_FEAT_WINDPLANT {(int)ShaderFeature.WindPlant}")
                  && FeatDecl.Contains($"CX_FEAT_ROOFKN    {(int)ShaderFeature.RoofKn}")
                  && FeatDecl.Contains($"CX_FEAT_ROOFKNM   {(int)ShaderFeature.RoofKnM}")
                  && FeatDecl.Contains($"CX_FEAT_FLOWMAP   {(int)ShaderFeature.Flowmap}")
                  && FeatDecl.Contains($"CX_FEAT_SKIN      {(int)ShaderFeature.Skin}")
                  && FeatDecl.Contains($"CX_FEAT_NOIRWET   {(int)ShaderFeature.NoirWet}")
                  && FeatDecl.Contains($"CX_FEAT_NOIRGLOSS {(int)ShaderFeature.NoirGloss}")
                  && FeatDecl.Contains($"CX_FEAT_GROUNDWEATHER {(int)ShaderFeature.GroundWeather}")
                  // #137: the skinning half. The binding point and the two attribute locations live twice
                  // (C# consts, literal GLSL) and neither disagreement is visible -- a wrong binding reads an
                  // empty block and collapses the mesh to the origin, a wrong location reads whatever stream
                  // the engine left at 8/9. The weight guards are bounds protection on an SSBO index, not a
                  // micro-optimisation, so their absence is checked as well.
                  && BoneVs.Contains($"binding = {BindBones})")
                  && BoneVs.Contains($"layout(location = {LocBoneIdx}) in vec4 aBoneIdx;")
                  && BoneVs.Contains($"layout(location = {LocBoneW}) in vec4 aBoneWeight;")
                  && BoneVs.Contains("aBoneWeight.w > 0.0")
                  && BoneVs.Contains("int(d.feat.x) == CX_FEAT_SKIN ? int(d.feat.y) : -1")
                  && BoneFloats * sizeof(float) == 4096
                  // ...and both stages take the skinned arm BEFORE the displaced test, which is what keeps
                  // the literal clip expression below reachable for every part that is still in the prepass.
                  && OwnedVs.Contains("if (bb >= 0) gl_Position = uVP * d.model * pos;")
                  && OwnedLitVs.Contains("if (bb >= 0) gl_Position = uVP * d.model * pos;")
                  // ...which is also the token SkinSafe reads to spot a pre-#137 content override. It has to
                  // be in the COMPOSED stage, not merely in BoneVs, or the guard passes on text that never
                  // reached the program. #262: the DEPTH stages carry it too now, and that is the pairing
                  // that lets the skinmesh class back into the prepass -- a skinned batch whose prepass had
                  // no bone arm would write a BIND-POSE silhouette and LEQUAL would punch the cloak full of
                  // holes, so the two must move together or not at all.
                  && OwnedVs.Contains("CX_FEAT_SKIN") && OwnedLitVs.Contains("CX_FEAT_SKIN")
                  && DepthVs.Contains("CX_FEAT_SKIN") && DepthKeyVs.Contains("CX_FEAT_SKIN")
                  // ...and the assigning statement is the SAME LITERAL in all four stages, which is the form
                  // of `invariant gl_Position` this renderer keeps everywhere else. An equivalent expression
                  // tree would probably do; "probably" is not what #119 is for.
                  && DepthVs.Contains("if (bb >= 0) gl_Position = uVP * d.model * pos;")
                  && DepthKeyVs.Contains("if (bb >= 0) gl_Position = uVP * d.model * pos;")
                  // The keyhole prepass discards on vWorld, so it must be the SKINNED world position or the
                  // two passes punch different apertures -- silent LEQUAL culling behind a dissolving cloak.
                  && DepthKeyVs.Contains("vWorld = (d.model * pos).xyz;")
                  && !ShaderFeatures.OutOfPrepass((int)ShaderFeature.Skin)
                  && !ShaderFeatures.Displaces((int)ShaderFeature.Skin)
                  && ShaderFeatures.OutOfPrepass((int)ShaderFeature.WindGrass)
                  && !ShaderFeatures.OutOfPrepass((int)ShaderFeature.Flowmap)
                  // ...and the CPU-side exclusions that go with them: no name here, no switch there.
                  && ShaderFeatures.VsNames.Length == 12
                  // #348: the two look features, and the properties that keep them out of the wind forks'
                  // special cases -- neither displaces, so neither leaves the depth prepass.
                  && ShaderFeatures.VsNames[(int)ShaderFeature.NoirWet] == "vs_noirwet"
                  && ShaderFeatures.VsNames[(int)ShaderFeature.NoirGloss] == "vs_noirgloss"
                  && ShaderFeatures.VsNames[(int)ShaderFeature.GroundWeather] == "td_ground_vs"
                  && !ShaderFeatures.Displaces((int)ShaderFeature.NoirWet)
                  && !ShaderFeatures.Displaces((int)ShaderFeature.GroundWeather)
                  // #556: the weather ground reads slot 6 for its snow, so it must enrol it -- without
                  // this the snow overlay samples an unbound unit and mixes toward black.
                  && ShaderFeatures.ReadsAuxSlot((int)ShaderFeature.GroundWeather)
                  && ShaderFeatures.ReadsAuxSlot((int)ShaderFeature.Flowmap)
                  && !ShaderFeatures.ReadsAuxSlot((int)ShaderFeature.NoirWet)
                  && !ShaderFeatures.OutOfPrepass((int)ShaderFeature.NoirGloss)
                  && ShaderFeatures.Displaces((int)ShaderFeature.WindGrass)
                  && ShaderFeatures.Displaces((int)ShaderFeature.RoofKnM)
                  && !ShaderFeatures.Displaces((int)ShaderFeature.Flowmap)
                  && !ShaderFeatures.Displaces((int)ShaderFeature.NoKeyhole)
                  && ShaderFeatures.NoKeyholing((int)ShaderFeature.NoKeyhole)
                  && ShaderFeatures.NoKeyholing((int)ShaderFeature.RoofKn)
                  && ShaderFeatures.NoKeyholing((int)ShaderFeature.Flowmap)
                  && !ShaderFeatures.NoKeyholing((int)ShaderFeature.RoofKnM)
                  && !ShaderFeatures.NoKeyholing((int)ShaderFeature.WindPlant)
                  // ...and the GLSL predicate picking the displaced clip expression mirrors Displaces(). If it
                  // ever says "any feature" again, nokeyhole and flowmap sit in the prepass computing a clip
                  // expression the prepass never compiled -- the #119 hole, with nothing in the log.
                  && FeatVs.Contains("bool CxDisplaces(int feat) { return feat >= CX_FEAT_WINDGRASS "
                                     + "&& feat <= CX_FEAT_ROOFKNM; }")
                  && OwnedLitVs.Contains("if (!CxDisplaces(int(d.feat.x))) " + ClipExpr)
                  && OwnedVs.Contains("if (!CxDisplaces(int(d.feat.x))) " + ClipExpr)
                  // ...and the map slots must stay BELOW the probe's unit. A samplerCube and a sampler2D
                  // pointed at one image unit is INVALID_OPERATION: every lit batch is dropped, and the log
                  // says nothing at all. #123 grew the slots into exactly that collision once.
                  && PbrUnit0 + MapSlots <= EnvProbe.ProbeUnit
                  // #146: the AO map is one more slot on that same ladder, and its GLSL bit is a HAND-WRITTEN
                  // copy of AoSlot/EnvSlot. Drift there is silent twice over: an AO bit that reads as the env
                  // bit turns every AO material legacy-shiny, and a lost multiply just renders unoccluded --
                  // which is what shipped content looks like today, so nothing would look wrong.
                  && OwnedLitFsHead.Contains($"CX_SLOT_AO = {AoBit};")
                  && OwnedLitFsHead.Contains($"CX_SLOT_ENV = {EnvSlot};")
                  && OwnedLitFsMain.Contains("vec3 orm = (slots & CX_SLOT_AO) != 0 ? texture(uAoTex, uv, uLodBias).rgb : vec3(1.0);")
                  && OwnedLitFsMain.Contains("occl *= orm.r;")
                  && OwnedLitFsMain.Contains("float ambDir = mix(0.15, 1.0, sunRaw);")
                  && OwnedLitFsMain.Contains("* ambShape * occl")
                  && OwnedVs.Contains("uDrawBase + gl_DrawID") && OwnedLitVs.Contains("uDrawBase + gl_DrawID")
                  // #242: ...and every one of them reaches its record THROUGH the draw-to-slot index. The
                  // records are persistent and indexed by ownership slot now; a stage that still indexed by
                  // draw would read a neighbour's record -- the wrong matrix and the wrong textures on every
                  // part, which is a misrender with nothing in the log. The binding lives twice (this const,
                  // that literal) for the same reason BindDraw does.
                  && DrawBufDecl.Contains($"binding = {BindDrawIdx})")
                  && DrawBufDecl.Contains("int CxSlot(int draw) { return uDrawIdx[draw]; }")
                  && OwnedVs.Contains("uDraw[CxSlot(uDrawBase + gl_DrawID)]")
                  && DepthVs.Contains("int id = CxSlot(uDrawBase + gl_DrawID);")
                  && OwnedLitVs.Contains("int id = CxSlot(uDrawBase + gl_DrawID);")
                  && DepthKeyVs.Contains("int id = CxSlot(uDrawBase + gl_DrawID);")
                  && FrameSnapshot.Vs.Contains("int id = CxSlot(uDrawBase + gl_DrawID);")
                  // ...and the guard that says the same thing about a stage this file never sees. It must
                  // refuse the pre-#242 spelling, which is the one an override written yesterday carries.
                  && SlotIndexSafe(OwnedVs) && SlotIndexSafe(OwnedLitVs) && SlotIndexSafe(FrameSnapshot.Vs)
                  && SlotIndexSafe(OwnedFs) && SlotIndexSafe(FrameSnapshot.Fs)
                  && !SlotIndexSafe("CxDraw d = uDraw[uDrawBase + gl_DrawID];")
                  // #119: the prepass depth is only re-testable by the colour pass while all three stages
                  // compute the SAME clip expression and declare it invariant. Drift either and lit
                  // surfaces lose fragments to LEQUAL -- a hole, with nothing in the log.
                  && OwnedVs.Contains(ClipExpr) && OwnedLitVs.Contains(ClipExpr) && DepthVs.Contains(ClipExpr)
                  && OwnedVs.Contains("invariant gl_Position;") && OwnedLitVs.Contains("invariant gl_Position;")
                  && DepthVs.Contains("invariant gl_Position;")
                  // #124: ...and the keyhole depth stage is a FOURTH copy of the same contract. It writes the
                  // depth a dissolving batch is tested against, so drift here is the #119 hole again.
                  && DepthKeyVs.Contains(ClipExpr) && DepthKeyVs.Contains("invariant gl_Position;")
                  // #102/#132: the lit stage lights in LINEAR and now STAYS there -- the frame's one encode
                  // is PostChain's resolve. Both halves are silent when they drift: a stage that stopped
                  // linearising its texel renders washed out, one that re-encoded renders twice-encoded, and
                  // neither says anything. So assert both, including the absence.
                  && OwnedLitFsMain.Contains("CxLinear(texel.rgb)")
                  && !OwnedLitFsMain.Contains("CxRevert")
                  && !AlloyLights.FragmentLib.Contains("CxRevert")
                  // #120: the lobe the whole ticket exists to restore, and the one-walk light function it
                  // rides in. A stage that lost either still compiles and still renders -- flat, exactly as
                  // it did before.
                  && AlloyLights.FragmentLib.Contains("float CxSpecLobe(")
                  && AlloyLights.FragmentLib.Contains("float CxSpecBase(")
                  && OwnedLitFsMain.Contains("sunSpec * CxLoopSpec(CxSpecLobe(N, V, uAreaDir")
                  // ...fed the RAW dot. #143's wrap is diffuse-only; a lobe on the wrapped dot lights the
                  // side of a glossy surface the sun cannot reach, and only at uWrapN > 0 -- invisible at
                  // the default.
                  && OwnedLitFsMain.Contains("vec3 sunSpec = uAreaDiffuse * (sunRaw * sunVis);")
                  // ...and the base modifier is applied exactly once per lobe: ADR 0026 moved it INTO
                  // CxLoopSpec (distributed over the sum, the same product), so a second copy outside is
                  // silent and wrong by exactly itself.
                  && OwnedLitFsMain.Contains("cxSpecScale = (uMaterial.x > 0.5 ? vec3(1.0) : specColor) * CxSpecBase(NoV, rough)")
                  && !OwnedLitFsMain.Contains("* (specColor * CxSpecBase(")
                  && AlloyLights.FragmentLib.Contains("vec3 spc = dif * CX_LOOP_SPEC(lobe, N, V, L, spread);")
                  && MaterialLib.Contains("vec3 s = lobe * cxSpecScale;")
                  && OwnedLitFsHead.Contains("#define CX_LIT_MATERIAL 1")
                  && AlloyLights.FragmentLib.Contains("vec3 CxClusteredLight(")
                  // CX-188 step 5: the walk is bracketed into a slim half for the forward consumers and the
                  // full one for everything else, the forward opaque lit stage included -- the A/B at
                  // light.deferred 0 is only an A/B while this program keeps the walk it is compared against.
                  && AlloyLights.FragmentLib.Contains("#ifdef CX_LIGHT_LITE")
                  && !OwnedLitFsHead.Contains("CX_LIGHT_LITE") && LightLiteSet
                  // #143: the wrapped-diffuse helper, and the sun actually going THROUGH it --
                  // a lit tail that keeps the raw dot is the punctual terminator back with no other sign.
                  && AlloyLights.FragmentLib.Contains("float CxWrap(float ndl, float w)")
                  && AlloyLights.FragmentLib.Contains("max(s, uWrapN)")
                  && OwnedLitFsMain.Contains("CxWrap(dot(N, uAreaDir), uWrapN)")
                  // ...while the ambient hemisphere keeps the RAW dot -- that line is inc_lighting.shd:679
                  // verbatim and wrapping it would drift the Alloy pass off the engine's own ambient.
                  && OwnedLitFsMain.Contains("mix(0.15, 1.0, sunRaw)")
                  // ...and the fullbright stage must DECODE, unconditionally: #132 made the target linear,
                  // so a stage that passed its display-space texel through would be wrong by a gamma
                  // wherever the engine is not drawing beside it.
                  && OwnedFs.Contains("vec3($SRGB$)") && OwnedFs.Contains("t.rgb * uTint") && !OwnedFs.Contains("uCapture")
                  // #121: the ambient-specular term, and the capture pairing that keeps the probe linear.
                  // A stage that lost the sample still renders (flat, as before #121); one that kept the
                  // sample but lost the capture branch samples the cube it is drawing into -- undefined --
                  // and stores display-space values a mip filter then darkens. Both are silent.
                  && OwnedLitFsMain.Contains("CxProbeFetch(uProbe, vWorld, R, sqrt(rough)")
                  && OwnedLitFsMain.Contains("uCapture != 1 && uProbeMips > 0.0")
                  && OwnedLitFsMain.Contains("if (int(d.feat.x) == CX_FEAT_SKIN || d.feat.w < 0.0) discard;")
                  && OwnedLitFsMain.Contains("frag = vec4(lit, 1.0 / max(distance(vWorld, uEyePos), 1e-3));")
                  // ...and CX-51's record: the weight the probe term took is the one Ssr swaps the screen in
                  // under -- R4 widened it to the stronger of the base weight and the water film's, at the
                  // roughness the film's own lobe has where it dominates (both collapse to the base dry), less the
                  // lit legacy env share, which is not the probe and has no screen to swap in
                  && OwnedLitFsMain.Contains("CxReflOut(N, mix(rough, cxCoatRough, wxFilm), max(reflW * (1.0 - envMix), reflFilm)")
                  // #124/#132: the dissolve and the fog, and the order they apply in. Both are LINEAR now --
                  // #124 welded the engine's keyhole-then-encode-then-fog order and #132 unwelded it, so the
                  // whole tail is one linear expression and the encode is the resolve's. Swap the two and
                  // the world still renders, just with fog that dissolves and a border that does not darken;
                  // nothing says so in the log.
                  && KeyFogFs.Contains("float CxKeyhole(") && KeyFogFs.Contains("vec3 CxFog(")
                  // CX-120: the lit stage fogs through the REVEAL form, whose fourth argument is the per-draw
                  // fog-of-war term in the record's last spare lane. Pinned together: a stage that fogged
                  // without it would draw every unexplored tile fully lit (vanilla hides them), and a lane
                  // index that drifted from the C# stride would fog by whatever the neighbouring field held.
                  && OwnedLitFsMain.Contains("CxFogReveal(lit * kh, vWorld, vDepth, d.ext2.w)")
                  // ...and the lane is HIDDEN, not reveal: the stage must early-out on <= 0, which is what
                  // makes an unwritten record (every engine-mode record) fog exactly as it always did.
                  && KeyFogFs.Contains("if (hidden <= 0.0) return CxFogMix(")
                  && KeyFogFs.Contains("float CxFogRevealFactor(") && AlloyRenderer.RevealFloat == AlloyRenderer.ExtFloat + 11
                  // #341: the fog target is direction-dependent, so the WORLD position is an argument and
                  // not a convenience -- a stage that passed the wrong point fogs toward the sky over
                  // somewhere else. And the ramp stays the area author's: the factor is still the engine's
                  // linear lerp between fogParams.y and its reciprocal span, with the height layer as a
                  // MULTIPLIER on it rather than a second curve. What the ramp is measured ALONG is the one
                  // thing that changed: radial from the player when uFogOrigin.w says so, else view depth --
                  // and CullPass measures the same way (its own SelfCheck), or the cut shows.
                  && FogFs.Contains("float d = uFogOrigin.w > 0.0 ? distance(world, uFogOrigin.xyz) : depth;")
                  && FogFs.Contains("float f = (d - uFogParams.y) * uFogParams.w;")
                  && FogFs.Contains("f *= exp(-max(world.z - uFogSky.z, 0.0) * uFogSky.y);")
                  // ...and the sky and sun pulls are scaled by the fog itself, so a lightly fogged surface
                  // keeps the colour the area builder set and only a saturated one converges on the sky.
                  && FogFs.Contains("* smoothstep(-0.35, 0.0, dir.z) * f;")
                  // #139: ...and the two terms whose loss is silent. A stage that dropped the wrap or the
                  // soft cube taps just renders harder -- nothing about either fails to compile or shows in
                  // the log.
                  && AlloyLights.FragmentLib.Contains("float s = uWrap <= 0.0 ? 0.0 : clamp(uWrap * uFalloff.y")
                  // #140: the engine's own range-normalised curve. A stage that kept the old absolute
                  // 1/d^n plateau still renders -- blinding at a metre, black at five -- and says nothing,
                  // so the shape is asserted rather than trusted to stay put.
                  && AlloyLights.FragmentLib.Contains("att = uFalloff.x * fade / (at.x + at.y * f2);")
                  // #674: the per-light body is ONE function the loop and the bake stage both run, the static
                  // bit decodes off the record's w, and a baked receiver skips a static light there -- a loop
                  // that lost the skip lights every baked surface twice, brighter and with nothing in the log.
                  && AlloyLights.FragmentLib.Contains("bool CxPointLight(vec4 pr, vec3 c0, vec3 worldPos, vec3 N,")
                  && AlloyLights.FragmentLib.Contains("bool isStatic = w >= 128;") && AlloyLights.FragmentLib.Contains("int slot = CxLightSlot(w);")
                  // CX-56: the bake-set index rides above the static bit (C# encodes it, GLSL decodes it) and a
                  // surface's pick names page C's bytes by it -- a skew darkens under a mover by another light's share
                  && AlloyLights.FragmentLib.Contains("const int CX_LIGHT_SET = 512;") && AlloyLights.LightSetStride == 512f
                  && LightmapBake.Channels == 4 && LightmapBake.ChartBufDecl.Contains("struct CxChart { vec4 rec; vec4 pick; };")
                  && LightmapBake.BakeFs.Contains("oC = uBakeChan < 4 ? vec4(equal(ivec4(uBakeChan), ivec4(0, 1, 2, 3)))")
                  && LightmapBake.EncCs.Contains("c = clamp(sc / max(dot(sa.rgb, CX_LUM), 1e-6), 0.0, 1.0);")
                  && LightmapBake.EncCs.Contains("const vec3 CX_LUM = vec3(0.2126, 0.7152, 0.0722);")
                  && LightmapBake.ChartBufDecl.Contains($"layout(binding = {LightmapBake.UnitC}) uniform sampler2DArray uLmC;")
                  && AlloyLights.FragmentLib.Contains("bool inPages = isStatic && baked > 0.5;")
                  && AlloyLights.FragmentLib.Contains("if (inPages && !hot && !mirror) continue;")
                  && AlloyLights.FragmentLib.Contains("CX_LIGHT_STATIC = 256.0;") && AlloyLights.LightStatic == 256f
                  && LightmapBake.BakeFs.Contains("CxPointLight(uBakeLight, uBakeColor, vWorld, N, c, L, att, f2, ndlRaw, ndl)")
                  && LightmapBake.BakeFs.Contains("vec3 unsh = CxPointDiffuse(c, att, ndlRaw, ndl);") && LightmapBake.BakeFs.Contains("vec3 lit = unsh * mix(uLocalFloor, 1.0, sh);")
                  && OwnedLitFsMain.Contains("bool baked = CxLightmap(chart.rec, vChart, lmIrr, lmDir);")
                  && OwnedLitFsMain.Contains("lm *= (1.0 - min(lmSub.x / max(dot(lm, vec3(0.2126, 0.7152, 0.0722)), 1e-5) + lmSub.y, 1.0)) * uDayDim;")
                  // the daylight ratio: a loop or baked term that lost it keeps every lamp at night strength at noon
                  && AlloyLights.FragmentLib.Contains("return acc * uDayDim;")
                  && OwnedLitFsHead.Contains(LightmapBake.ChartBufDecl) && OwnedLitVs.Contains("vChart = aChart;")
                  && LightmapBake.ChartBufDecl.Contains($"binding = {LightmapBake.BindChart})")
                  // the live receiver widens a mover's static half to the lightmap texel the bake did: one density
                  && MeshChart.AtlasTexelsPerUnit == 8f && LightmapBake.ChartBufDecl.Contains("1.0 / (8.0 * rec.w)")
                  // #142: the near-field term, and the guard that makes an unset uHot mean "off". A stage
                  // that lost it renders every light flat at the gain the BODY wants -- which is the exact
                  // symptom the term exists to fix, and says nothing.
                  // CX-188 budget: cxLod is light.fullmodel's weight, 1 at every caller but the walk's faint tail
                  && AlloyLights.FragmentLib.Contains("att += cxLod * uHot.x * fade * t * t / at.x;")
                  && AlloyLights.FragmentLib.Contains("max(1.0 - f2 / (uHot.y * uHot.y), 0.0)")
                  && AlloyLights.FragmentLib.Contains("f2 = dd / rr;")
                  // ...and the fallback pair, so a program that never gets uAtten falls back instead of
                  // dividing every light by zero and rendering the world white.
                  && AlloyLights.FragmentLib.Contains("uAtten.x > 0.0 ? uAtten : vec2(1.0, 1.0)")
                  && AlloyLights.LocalShadowLib.Contains("if (onMover > 0.5 && m < uLocalSoft.y) return 1.0;")
                  // ...and every one of them must read "off" from the ZERO a program that never sets the
                  // uniform leaves behind, or a stale content override (#92) does something wrong rather
                  // than something old. This is the whole reason none of them rides in uFalloff's spare lane.
                  && AlloyLights.LocalShadowLib.Contains("if (!soft || uLocalSoft.x <= 0.0 || (uLocalSoft.z > 0.0 && texel * uLocalSoft.x < viewDepth * uLocalSoft.z)) return mix(1.0, v, uLocalFade[slot]);")
                  // ...and the dissolve has to reach the fragment stage at all: it rides in rot.z, which was
                  // a dead float, so a stage reading the wrong lane dissolves nothing and says nothing.
                  && OwnedLitFsMain.Contains("CxKeyhole(d.rot.z, vWorld, vDepth)")
                  // ...and the FULLBRIGHT stage must have NEITHER. Every unlit fragment stage in
                  // base_shaders.bif compiles FOG 0 and KEYHOLING 0 -- fst/fst_sm/fsglu (textured) and
                  // fs/fstc (untextured) alike -- so an unlit part that fogged or dissolved here would be
                  // doing to a part something vanilla never does to it. Asserted rather than commented:
                  // both are one line to add back and neither would look obviously wrong. What it DOES
                  // carry is the fog's mix alone, for the sky's horizon band (SceneFog.SkyBand) -- an
                  // elevation term, never the distance ramp.
                  && !OwnedFs.Contains("CxKeyhole") && !OwnedFs.Contains("CxFogFactor") && !OwnedFs.Contains("CxFog(")
                  && OwnedFs.Contains("if (uSkyFog > 0.0 && uFogParams.x != 0.0)")
                  // ...and the prepass punches the SAME aperture the colour pass will. Both stages call the
                  // one CxKeyhole out of KeyFogFs, so they cannot drift by a constant -- but a stage that
                  // lost the call writes depth over fragments the colour pass then discards, and the room
                  // behind the wall goes dark with nothing in the log.
                  && DepthKeyFs.Contains("float CxKeyhole(")
                  && DepthKeyFs.Contains("CxKeyhole(uDraw[vDraw].rot.z, vWorld, vDepth)")
                  && DepthFeatFs.Contains("CxKeyhole(uDraw[vDraw].rot.z, vWorld, vDepth)")
                  // #126: the cutout, in both colour stages -- and spelled the engine's way, so a stage that
                  // kept the uniform but lost the >= 0.0 guard would discard every fragment of the OPAQUE
                  // set (which passes -1.0) and render an empty world.
                  // CX-191: through the mip-level compensation, the same call in the feature prepass so its
                  // depth and the GL_EQUAL colour draw keep the same holes
                  && OwnedFs.Contains("if (uAlphaCut >= 0.0 && CxAlphaMip(t.a, du1, du2, vec2(textureSize(uTex, 0)), uLodBias) <= uAlphaCut) discard;")
                  && OwnedLitFsMain.Contains("if (uAlphaCut >= 0.0 && CxAlphaMip(coverage, du1, du2, vec2(textureSize(uTex, 0)), uLodBias) <= uAlphaCut) discard;")
                  && DepthFeatFs.Contains("if (CxAlphaMip(texture(uTex, vUV, uLodBias).a, du1, du2, vec2(textureSize(uTex, 0)), uLodBias) * mul <= uAlphaCut) discard;")
                  // #294: and COVERAGE is what the lit stage cuts and blends on, never the raw texel alpha.
                  // On an env-mapped material that channel is reflectivity (it becomes envLevel), and the
                  // engine spends it once: inc_standard.shd:289 multiplies only .rgb through there. A stage
                  // that loses this line spends it twice and every reflective surface draws as a ghost --
                  // which is invisible in a screenshot of anything that is not shiny, hence the guard.
                  && OwnedLitFsMain.Contains("float coverage = (slots & CX_SLOT_ENV) != 0 ? 1.0 : texel.a;")
                  // ...and the per-draw alpha reaching the framebuffer, which is the whole of the blended
                  // class: a stage writing a constant 1.0 there renders every transparent surface solid.
                  && OwnedFs.Contains("t.a * vAlpha") && OwnedVs.Contains("vAlpha = d.rot.w;")
                  && OwnedLitFsMain.Contains("coverage * d.rot.w")
                  // ...and the embedded stages themselves satisfy the override guard, so a wrong guard fails
                  // here rather than rebuilding every program from the embedded text every session (which
                  // would look like the override silently not applying).
                  && CutoutSafe(OwnedVs, OwnedFs)
                  && CutoutSafe(OwnedLitVs, OwnedLitFsHead + OwnedLitFsMain)
                  // #132: ...and the same for the colour-space guard, over every stage it polices. A guard
                  // the embedded text itself trips rebuilds every program from that same text, every
                  // session -- an infinite "your override is stale" that no override caused.
                  && LinearSafe(OwnedFs) && LinearSafe(OwnedLitFsHead + OwnedLitFsMain)
                  // #153: the bindless rewrite must produce a complete swap on the embedded lit stage --
                  // extension + handle block in, every bound-sampler declaration out -- and must leave
                  // foreign text (no known decls) untouched rather than half-rewritten.
                  && BindlessFs(OwnedLitFsHead + MaterialLib).Contains("CxTexH")
                  && BindlessFs(OwnedLitFsHead + MaterialLib).Contains("GL_ARB_bindless_texture")
                  && !BindlessFs(OwnedLitFsHead + MaterialLib).Contains("uniform sampler2D uTex;")
                  && !BindlessFs(OwnedLitFsHead + MaterialLib).Contains("uniform sampler2D uAoTex;")
                  && BindlessFs("void main() { }") == "void main() { }"
                  && LinearSafe(FrameSnapshot.Fs) && LinearSafe(GrassField.EmbeddedFs)
                  // #161: the record guard reaches every stage that declares the record, and the embedded
                  // text passes it, for the same reason as above: a guard its own defaults trip rebuilds
                  // from them every session.
                  && RecordSafe(FrameSnapshot.Vs) && RecordSafe(FrameSnapshot.Fs)
                  // ...and it REFUSES a record that disagrees. A predicate that only ever returns true is
                  // precisely the silent failure it was written to prevent, and nothing else would notice.
                  && !RecordSafe("struct CxDraw { mat4 model; vec4 rot; };")
                  // #392: ...and the pre-#392 record, which counts the same vec4s and differs only by the
                  // trailing mat4 -- the growth a vec4-only count could not see.
                  && !RecordSafe("struct CxDraw { mat4 model; vec4 rot; vec4 amb; vec4 dif; vec4 emi; vec4 pbr; vec4 spc; vec4 feat; };")
                  && RecordSafe("void main() { }")   // a stage that declares no record cannot disagree
                  // #161: the resolve's inverted colour-space guard, both directions. It is the frame's one
                  // encode, so it must hold on the embedded text and must catch a stage that decodes first
                  // (pre-#132) or performs no encode at all -- each of which washes out the whole frame.
                  && ResolveSafe(PostChain.ResolveFs)
                  && !ResolveSafe(PostChain.ResolveFs.Replace("CxEncode", "CxLinear"))
                  && !ResolveSafe(PostChain.ResolveFs.Replace("CxEncode", "CxPassthrough"))
                  // ...and the bloom chain runs in the same linear space every other Alloy stage writes.
                  && LinearSafe(PostChain.BloomDownFs) && LinearSafe(PostChain.BloomUpFs)
                  // ...and the four text-matching guards REFUSE, not just accept. A predicate that only ever
                  // returns true is the silent failure it was written to prevent, and the embedded text can
                  // never exercise the refusing direction on its own.
                  && !LinearSafe("void main() { frag = CxRevert(c); }")
                  && !LinearSafe("void main() { frag = CxSoftClip(c); }")
                  && !CutoutSafe(OwnedVs, OwnedFs.Replace("uAlphaCut", "uNothing"))
                  && !CutoutSafe(OwnedVs.Replace("vAlpha", "vNothing"), OwnedFs)
                  && ChartSafe(OwnedLitVs, OwnedLitFsHead + OwnedLitFsMain) && !ChartSafe(OwnedLitVs.Replace("vChart", "vNothing"), OwnedLitFsHead)
                  // SkinSafe's refusing direction is NOT assertable here: it short-circuits on _skinning,
                  // which Initialize sets AFTER this runs, so the predicate is unconditionally true at
                  // SelfCheck time. Its guard site is covered instead by the CutoutSafe pair beside it.
                  // #208: the slot guard, both directions. The embedded lit stage must pass it (a guard its
                  // own text trips rebuilds from that text every session), and a stage with one sampler
                  // renamed must fail -- which is the only case it exists for.
                  && LitSlotSafe(OwnedLitFsHead)
                  && !LitSlotSafe(OwnedLitFsHead.Replace("uniform sampler2D uAoTex;", "uniform sampler2D uNope;"))
                  && !LitSlotSafe(OwnedLitFsHead.Replace("uniform samplerCube uProbe;", ""))
                  // ...and the same over every other overridable stage that binds a unit, so the embedded
                  // text of each passes the guard its own override will be held to.
                  && SlotSafe(FrameSnapshot.FsHead, "uTex", "uFbColor", "uFbDepth", "uNoise", "uEnvMap")
                  && SlotSafe(PostChain.ResolveFs, "uHdr", "uBloom")
                  && SlotSafe(GrassField.GrassFsHead, "uTex")
                  && !SlotSafe(PostChain.ResolveFs, "uNotASampler")
                  && !DepthSafe(OwnedVs.Replace("invariant gl_Position;", ""))
                  && !DepthSafe(OwnedVs.Replace(ClipExpr, "gl_Position = uVP * (d.model * vec4(aPos, 1.0));"))
                  // ...and the ownable list is the DRAW list: nothing else renders the world since #264, so
                  // a bucket missing here is a class that goes dark rather than one that stays vanilla.
                  && AlloyCensus.OwnableBucket(0) && AlloyCensus.OwnableBucket(1)
                  && AlloyCensus.OwnableBucket(4) && AlloyCensus.OwnableBucket(7)
                  && AlloyCensus.OwnableBucket(8)
                  // #134: the dynamic gob pair joins, and joins TOGETHER -- the engine walks 2 then 3, and
                  // owning one without the other splits one instance's geometry across two renderers.
                  && AlloyCensus.OwnableBucket(2) && AlloyCensus.OwnableBucket(3)
                  // CX-90: 5, the late class, joins. `sample_framebuffer 1` is a draw-ORDER keyword and
                  // nothing else, so its parts are classified off their material like any other and the
                  // residue in the bucket is `volumetric`, refused on the material.
                  // #129: 6 is deliberately NOT asserted here. OwnableBucket evaluates it AS
                  // FrameSnapshot.Usable, so any check of the two against each other is a tautology -- it would
                  // read as cover and catch nothing. What actually governs the water draw is _waterArmed
                  // (Usable && lightingOn && the program), which is structural and not assertable here.
                  && AlloyCensus.OwnableBucket(AlloyCensus.LateBucket)
                  // ...and the list still REFUSES: bucket 15 is the beams', drawn by Beams from its own
                  // capture and never from the part table, so a predicate that said yes to everything would
                  // be caught here rather than by a doubled draw.
                  && !AlloyCensus.OwnableBucket(15)
                  // ...and the keyword precedence behind it, both directions (CX-90).
                  && !AlloyCensus.RefusedByLateKeywords(AlloyCensus.SampleFbLate, false, false)
                  && !AlloyCensus.RefusedByLateKeywords(0, false, false)
                  && !AlloyCensus.RefusedByLateKeywords(AlloyCensus.SampleFbWater, false, true)
                  && AlloyCensus.RefusedByLateKeywords(0, true, false)
                  && AlloyCensus.RefusedByLateKeywords(AlloyCensus.SampleFbLate, true, false)
                  && AlloyCensus.RefusedByLateKeywords(AlloyCensus.SampleFbWater, false, false)
                  && AlloyCensus.RefusedByLateKeywords(3, false, false)
                  // #92: a resref past the CResRef limit can never name a resource, so an override
                  // dropped under it would go silently unread.
                  && ShdFullbrightVs.Length <= ShaderResource.ResRefBytes
                  && ShdFullbrightFs.Length <= ShaderResource.ResRefBytes
                  && ShdLitVs.Length <= ShaderResource.ResRefBytes
                  && ShdLitFs.Length <= ShaderResource.ResRefBytes
                  && ShdWaterVs.Length <= ShaderResource.ResRefBytes
                  && ShdWaterFs.Length <= ShaderResource.ResRefBytes
                  // CX-93: the targeting shape is ALLOY's, not a spliced stock include -- the library declares
                  // the shape uniforms itself, computes the weight and lays it on. Asserted here rather than
                  // discovered as a compile failure, because that failure stands the LIT program down and
                  // reverts the world to engine drawing. The absence of a splice is part of the contract: a
                  // $VANILLA region back in this file is the engine dependency this ticket removed.
                  && !VanillaShader.HasSplice(TargetFs)
                  && TargetFs.Contains("float CxTargetStrength()")
                  && TargetFs.Contains("void CxTargetApply(inout vec4")
                  && TargetFs.Contains("uniform int targetShape;")
                  && TargetFs.Contains("CxDisplayOverlays")
                  // #347/#348: the CONTENT hook region, and the two signatures that are its whole contract.
                  // A missing one is not a missing feature -- it is a lit program that does not link, which
                  // takes the world with it.
                  && VanillaShader.HasSplice(HookFs)
                  && HookFs.Contains("vec3 CxDisplayHook(vec3 c)")
                  // #350: the surface hook is GONE -- a material's own FS is hosted instead. Its
                  // reappearance would be the content contract this ticket deleted coming back.
                  && !HookFs.Contains("CxSurfaceHook")
                  // ...and the hosted regions: one per FsNames entry, each with its stub and its dispatcher
                  // arm, and no region the table does not name. Drift here is a feature that silently draws
                  // the plain lit path, or a link error that takes the world with it.
                  && VanillaShader.HasHost(HostFs)
                  && HostFs.Contains("bool CxHostSurface(int feat)")
                  && ShaderFeatures.FsNames.Length == ShaderFeatures.VsNames.Length
                  && ShaderFeatures.FsNames.All(n => n.Length == 0
                         || (HostFs.Contains("// $VANILLA-HOST " + n + "\n") && HostFs.Contains("void CxHost_" + n + "() { }")
                             && HostFs.Contains("CxHost_" + n + "();")))
                  && HostFs.Split("// $VANILLA-HOST ").Length - 1 == ShaderFeatures.FsNames.Count(n => n.Length > 0)
                  && OwnedLitFsMain.Contains("CxHostSurface(feat))")
                  // ...and what a hosted body reads and writes, under the engine's own names.
                  && VanillaEnvFs.Contains("vec4 FragmentColor;")
                  && VanillaEnvFs.Contains("#define MATERIAL_ROUGHNESS_VALUE fRoughness")
                  && VanillaEnvFs.Contains("vec3 SpecularColor;")
                  && VanillaStandardFs.Contains("void SetupStandardShaderInputs() { FragmentColor *= CxHostTexel; }")
                  && VanillaEnvFs.Contains("vec4 CxHostTexel;")
                  && OwnedLitFsMain.Contains("CxHostTexel = vec4(albedo, coverage);")
                  && VanillaStandardFs.Contains("void ApplyStandardShader() { }")
                  && VanillaStandardFs.Contains("#include \"inc_scriptable\"")
                  // ...and the rewrite itself, on a synthetic stage: main renamed, gl_FragColor redirected,
                  // the stage's own defines scoped, an environment-supplied declaration dropped.
                  && HostSample.Contains("void CxHostMain_x()")
                  && HostSample.Contains("FragmentColor = FragmentColor;")
                  && !HostSample.Contains("gl_FragColor")
                  && HostSample.Contains("#undef LIGHTING\n#undef FOO\n")
                  && HostSample.Split("#undef FOO").Length == 3
                  && !HostSample.Contains("uniform sampler2D")
                  && HostSample.Contains("void CxHost_x()")
                  && HostSample.Contains("fRoughness = MATERIAL_ROUGHNESS_VALUE;")
                  && HostSample.EndsWith(VanillaEnvDefs)
                  // ...and the environment they share, ahead of all three. Declared twice (once per file)
                  // it would be a compile error, declared nowhere a link error; this is the one copy.
                  && VanillaEnvFs.Contains("#define CX_ALLOY")
                  && VanillaEnvFs.Contains("uniform mat4 m_proj")
                  // #551: the environment must NOT declare the scriptable block. Every spliced file
                  // includes inc_scriptable for it and the bridge resolves that include, so a declaration
                  // here is a redefinition -- which fails the whole lit program, not just the feature.
                  && !VanillaEnvFs.Contains("uniform float scriptableFloat")
                  && !VanillaEnvFs.Contains("uniform int scriptableInt")
                  && VanillaEnvFs.Contains("float CxVisionLight()")
                  // #347: the stage identity a spliced file branches on. Asserted because its absence is
                  // SILENT -- an undefined SHADER_TYPE makes `#if SHADER_TYPE == 2` read 0, so a content
                  // file's fragment half preprocesses away and its hook becomes an empty function that
                  // links and does nothing. That is how racial vision was inert here.
                  && VanillaEnvFs.Contains("#define SHADER_TYPE 2")
                  // #348: what a content file's own normal work writes through. inc_ripple's TiltNormal is
                  // written against a VIEW-space normal and these two matrices; Alloy's is world-space, so
                  // they are identity and the conversion collapses to the tilt the helper performs anyway.
                  && VanillaEnvFs.Contains("vec3 vFragmentNormal")
                  && VanillaEnvFs.Contains("const mat4 m_view =")
                  && VanillaEnvFs.Contains("const mat4 m_view_inv =")
                  // ...and the marker surgery itself is lossless: with nothing to substitute, splicing must
                  // give back exactly what it was handed. An unterminated marker or an off-by-one in the
                  // region bounds silently eats GLSL, which is the one failure mode of string surgery.
                  && VanillaShader.Splice(HookFs, null, "selfcheck", false) == HookFs
                  && VanillaShader.Splice(HostFs, null, "selfcheck", false) == HostFs;
        log.Info(ok
            ? "ShaderContract.SelfCheck: passed"
            : $"ShaderContract.SelfCheck: FAILED -- {(slots ? "" : "a texture slot disagreed (named above), ")}" +
              $"the per-draw SSBO contract (binding {BindDraw}, " +
              $"stride {DrawFloats * sizeof(float)}B) or the #102 linear/display-space pairing drifted; " +
              "standing down rather than drawing from the wrong records");
        return ok;
    }
}
