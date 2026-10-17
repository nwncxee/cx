using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CX.Alloy;

/// <summary>
/// A CONTENT pair -- the <c>customShaderVS</c>/<c>customShaderFS</c> a <c>.mtr</c> names -- rewritten to run inside a lit
/// program of its own (<see cref="ContentPrograms"/>): the vertex half over Alloy's own vertex outputs, the fragment half
/// as the surface body and its edits after <c>ApplyStandardShader</c> on the lit colour. Pure text-to-text over a chain
/// reader; <c>cx_content_v.glsl</c> and <c>cx_content_f.glsl</c> are the environments the halves compile in.
/// </summary>
internal static partial class ContentStage
{
    /// <summary>How the engine's upload feeds a content uniform from a <c>.mtr</c> <c>parameter</c> line.</summary>
    internal enum ParamKind : byte
    {
        /// <summary>A <c>float</c>: a one-value <c>parameter float</c> line (<c>glUniform1f</c>).</summary>
        Float,
        /// <summary>An <c>int</c>: a <c>parameter int</c> line (<c>glUniform1i</c>).</summary>
        Int,
        /// <summary>A <c>bool</c>: either of the two scalar uploads.</summary>
        Bool,
        /// <summary>A <c>vec4</c>: a two-to-four-value <c>parameter float</c> line (<c>glUniform4fv</c>).</summary>
        Vec4,
    }

    /// <summary>One content uniform the content table feeds: its name as the shader spells it, how the engine would
    /// upload into it, and its lane in the row. A name both halves declare is one uniform in the engine's program,
    /// so it is one lane.</summary>
    internal readonly record struct Param(string Name, ParamKind Kind, int Lane);

    /// <summary>Lanes a pair's parameters may take in its content-table row, which is
    /// <see cref="Db.ContentTable.ContentLanes"/>. The whole corpus measured uses five at most.</summary>
    internal const int MaxLanes = Db.ContentTable.ContentLanes;

    /// <summary>What a translation produced. <see cref="Refused"/> non-null means the stage cannot run under Alloy
    /// and says why; everything else is then empty.</summary>
    internal sealed class Result
    {
        public string Host = "";
        /// <summary>The pair's lanes so far: a vertex half's own, a fragment half's on top of its vertex half's.</summary>
        public Param[] Params = [];
        /// <summary>Does the fragment half discard or write its own coverage? Its depth is then the stage's, not the
        /// cutout test the depth prepass makes.</summary>
        public bool Cuts;
        /// <summary>Does it read the engine's PLT scheme feed? Its records then carry the wearer's palette rows.</summary>
        public bool ReadsPlt;
        /// <summary>Does it read the engine's per-light arrays, i.e. light itself? They are filled per fragment from the
        /// light clusters, which only the forward program walks.</summary>
        public bool ReadsLights;
        /// <summary>Does the fragment half run code on the LIT colour -- after its <c>ApplyStandardShader()</c>, or all of
        /// it for a stage that never calls it? Only the forward program has a lit colour to hand it.</summary>
        public bool Post;
        /// <summary>Does the fragment half switch keyholing off (<c>#define KEYHOLING 0</c>)? Its parts never dissolve.</summary>
        public bool NoKeyhole;
        /// <summary>The varyings a vertex half declares of its own, by name: the fragment half reads them as written.</summary>
        public readonly HashSet<string> Outputs = new(StringComparer.Ordinal);
        /// <summary>What the translation could only approximate, one clause each, for the log.</summary>
        public readonly List<string> Notes = new();
        public string? Refused;
    }

    /// <summary>Includes the content environment stands in for and the resolver therefore never follows: the engine's
    /// interface (<c>inc_common</c>, <c>inc_uniforms</c>) and every piece of the forward pipeline <c>inc_standard</c> pulls
    /// in, which Alloy runs itself.</summary>
    private static readonly string[] Dropped =
    [
        "inc_common", "inc_uniforms", "inc_target", "inc_lighting", "inc_material", "inc_keyhole", "inc_fog",
        "inc_transform", "inc_envmap", "inc_random", "inc_tonemap", "inc_debug", "inc_displacement", "inc_framebuffer",
    ];

    /// <summary>Include nesting and count caps. The engine has neither, and no cycle guard; these turn a cycle into a
    /// refusal instead of a hang.</summary>
    private const int MaxDepth = 8, MaxIncludes = 64;

    /// <summary>The engine's 16-character resref field, which its include scanner caps the name at.</summary>
    private const int ResRefChars = 16;

    /// <summary>The names a fragment half's <c>main</c> is split into, and a vertex half's renamed to.</summary>
    private const string PreFn = "CxContentMain", PostFn = "CxContentPost", VertexFn = "CxContentVertexMain";

    /// <summary>Translate content stage <paramref name="resref"/>, whose text and includes <paramref name="chain"/> reads
    /// off the resource chain. <paramref name="env"/> is the environment's text, <paramref name="vanillaDefs"/> the lit
    /// stage's own vanilla macros (a fragment half; empty for a vertex one) and <paramref name="ahead"/> the program's
    /// text ahead of the region. A fragment half takes its vertex half's translation as <paramref name="vertex"/>.</summary>
    internal static Result Translate(string resref, bool fragment, Func<string, string?> chain, string env, string vanillaDefs,
                                     string ahead, int buildVersion, int buildRevision, Result? vertex = null)
    {
        var r = new Result();
        string? stage = chain(resref);
        if (stage == null) { r.Refused = $"{resref}.shd is not in the resource chain"; return r; }
        int count = 0;
        string standard = fragment ? AlloyRenderer.ContentStandardFs : AlloyRenderer.ContentStandardVs;
        string? text = Resolve(stage, chain, standard, new List<string> { resref.ToLowerInvariant() }, 0, ref count, out string? missing);
        if (text == null) { r.Refused = missing; return r; }
        text = Directive.Replace(text, "");
        if (!MainFn.IsMatch(text)) { r.Refused = $"{resref}.shd has no void main() to run"; return r; }
        // the stage's OWN text, the stand-in left out: what its facts are read off
        count = 0;
        string flat = Comments.Replace(Resolve(stage, chain, "", new List<string> { resref.ToLowerInvariant() },
                                               0, ref count, out _) ?? text, " ");
        r.ReadsPlt = Regex.IsMatch(flat, @"\bPLTscheme\b");
        r.ReadsLights = fragment && LightArrays.IsMatch(flat);
        if (fragment)
        {
            r.Cuts = Regex.IsMatch(flat, @"\bdiscard\b|\bAlphaDiscard\s*\(") || WritesAlpha(flat) || OwnCoverage.IsMatch(flat);
            r.NoKeyhole = Keyholing(flat) == 0;
        }

        var supplied = Supplied(env, vanillaDefs, ahead);
        var undef = new List<string>();
        if (vertex != null)
            foreach (string o in vertex.Outputs)
                if (supplied.Remove(o)) undef.Add(o);
        text = MainFn.Replace(text, $"void {(fragment ? PreFn : VertexFn)}()");
        if (fragment) text = Regex.Replace(text, @"\bgl_FragColor\b", "FragmentColor");
        text = VanillaShader.PltSchemeDecl.Replace(text, "");
        text = Indexed(text, PltSchemeOpen, fragment ? "CxPltScheme(vDraw, " : "CxPltScheme(cxConDraw, ");
        text = BonesDecl.Replace(text, "");
        text = Indexed(text, BonesUse, "CxConBoneAt(");
        var paramList = new List<Param>(vertex?.Params ?? []);
        var macros = new List<string>();
        text = Declarations(text, fragment, supplied, vertex?.Outputs, paramList, macros, r);
        if (r.Refused != null) return r;
        r.Params = paramList.ToArray();
        if (fragment) text = SplitMain(text, r);
        if (r.Refused != null) return r;

        var guards = Guard.Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var own = VanillaShader.OwnDefine.Matches(text).Select(m => m.Groups[1].Value)
                               .Where(n => !guards.Contains(n)).Distinct().ToList();
        var fns = FnDef.Matches(Comments.Replace(text, " ")).Select(m => m.Groups[1].Value).Distinct().ToList();
        var envDefs = VanillaShader.OwnDefine.Matches(env).Select(m => m.Groups[1].Value).Distinct().ToList();
        var sb = new StringBuilder(text.Length + env.Length + 2048);
        sb.Append(fragment ? "// ---- content stage " : "// ---- content vertex stage ").Append(resref).Append('\n');
        sb.Append("#define BUILD_VERSION ").Append(buildVersion).Append('\n');
        sb.Append("#define BUILD_REVISION ").Append(buildRevision).Append('\n');
        foreach (string f in fns) sb.Append("#define CX_CON_HAS_").Append(f).Append('\n');
        sb.Append(env);
        if (!env.EndsWith('\n')) sb.Append('\n');
        foreach (string n in own.Concat(undef)) sb.Append("#undef ").Append(n).Append('\n');
        sb.Append(text).Append('\n');
        if (fragment) FragmentWrapper(sb, r); else VertexWrapper(sb);
        foreach (string n in own) sb.Append("#undef ").Append(n).Append('\n');
        foreach (string n in envDefs.Concat(macros).Concat(fns.Select(f => "CX_CON_HAS_" + f)))
            sb.Append("#undef ").Append(n).Append('\n');
        sb.Append("#undef BUILD_VERSION\n#undef BUILD_REVISION\n");
        if (fragment)
        {
            sb.Append(vanillaDefs);
            if (!vanillaDefs.EndsWith('\n')) sb.Append('\n');
            sb.Append("#define CX_CONTENT_PROGRAM 1\n");
            if (r.Post) sb.Append("#define CX_CONTENT_POST 1\n");
            sb.Append("bool CxHostSurface(int feat)\n{\n")
              .Append("    if (feat < CX_FEAT_CONTENT0) return false;\n")
              .Append("    CxContentBody();\n")
              .Append("    return true;\n}\n");
        }
        r.Host = sb.ToString();
        return r;
    }

    /// <summary>The fragment half's wrappers: the surface body (the stage up to its <c>ApplyStandardShader()</c>, with the
    /// material globals read back) and, for a stage that edits the lit colour, the post body over that colour.</summary>
    private static void FragmentWrapper(StringBuilder sb, Result r)
    {
        sb.Append("void CxContentBody()\n{\n")
          .Append("    cxConUV = CxHostUV;\n")
          .Append("    cxConRow = CxContentRow(vDraw);\n");
        if (r.ReadsLights) sb.Append("    CxConFillLights();\n");
        sb.Append("    fEnvMapLevel = 0.0;\n")
          .Append("    fOcclusion = 1.0;\n")
          .Append("    ").Append(PreFn).Append("();\n")
          .Append("    fSpecularity = MATERIAL_SPECULARITY_VALUE;\n")
          .Append("    fRoughness = MATERIAL_ROUGHNESS_VALUE;\n")
          .Append("    fMetallicness = MATERIAL_METALLICNESS_VALUE;\n")
          .Append("    SpecularColor = MATERIAL_SPECULAR_COLOR_VALUE;\n}\n");
        if (!r.Post) return;
        // the engine hands the post body the colour ApplyStandardShader leaves: display space, and back again
        sb.Append("vec4 CxHostPost(vec4 c)\n{\n")
          .Append("    FragmentColor = vec4(RevertColorSpace(c.rgb), c.a);\n")
          .Append("    ").Append(PostFn).Append("();\n")
          .Append("    return vec4(ApplyColorSpace(FragmentColor.rgb), FragmentColor.a);\n}\n");
    }

    /// <summary>The vertex half's wrapper: the stock outputs seeded from Alloy's own, the stage run, and what it left read
    /// back into the lit stage's varyings -- the tangent turned by the record's UV rotation as Alloy's own is.</summary>
    private static void VertexWrapper(StringBuilder sb)
        => sb.Append("bool CxHostVertex(CxDraw d, int id, int bb)\n{\n")
             .Append("    cxConD = d;\n    cxConDraw = id;\n    cxConBone = bb;\n    cxConRow = CxContentRow(id);\n")
             .Append("    cxConMv = d.model;\n    cxConMv[3].xyz -= uEyePos;\n")
             .Append("    cxConMvp = cxVP * d.model;\n")
             .Append("    cxConProj = cxVP;\n    cxConProj[3] += cxVP * vec4(uEyePos, 0.0);\n")
             .Append("    vPosView = vWorld - uEyePos;\n    vPosWorld = vWorld;\n")
             .Append("    vVertexNormal = vNrm;\n    vVertexTangent = cxTanW;\n    fTextureHandedness = aTangent.w;\n")
             .Append("    vVertexTexCoords = vUV;\n")
             .Append("    ").Append(VertexFn).Append("();\n")
             .Append("    vWorld = vPosView + uEyePos;\n")
             .Append("    vNrm = vVertexNormal;\n")
             .Append("    vec3 b = cross(vVertexNormal, vVertexTangent) * fTextureHandedness;\n")
             .Append("    vTan = vec4(d.rot.x * vVertexTangent - d.rot.y * b, fTextureHandedness);\n")
             .Append("    vUV = vVertexTexCoords;\n")
             .Append("    vDepth = gl_Position.w;\n")
             .Append("    return true;\n}\n");

    /// <summary>The environment libraries' resrefs, bracketed in the region so a hak's <c>cx_content_f.shd</c> or
    /// <c>cx_content_v.shd</c> replaces one like any other Alloy library.</summary>
    internal const string EnvFsResref = "cx_content_f", EnvVsResref = "cx_content_v";

    /// <summary>The last translation of each half this module, by its region's key: what its program's lanes, prepass
    /// routing and log line come from. One owner, replaced whole on a module change.</summary>
    private sealed class RegionState
    {
        public readonly Dictionary<string, Result> Last = new(StringComparer.OrdinalIgnoreCase);
    }

    private static RegionState _regions = new();

    /// <summary>A region's key: the half and both resrefs, as the region marker names them.</summary>
    internal static string Key(bool fragment, string vs, string fs) => (fragment ? "fs " : "vs ") + vs + " " + fs;

    /// <summary>The <c>$VANILLA-CONTENT</c> region's body for <paramref name="fs"/> (or, <paramref name="fragment"/>
    /// false, <c>$VANILLA-CONTENT-VS</c>'s for <paramref name="vs"/>), from inside <see cref="VanillaShader.Splice"/>: an
    /// include an earlier region of the same program spliced resolves to nothing. Null when the stage is refused (the
    /// stub stays). <paramref name="ahead"/> is the program's text before the region.</summary>
    internal static string? Region(bool fragment, string vs, string fs, CX.Framework.ExtenderLog? log, string what,
                                   List<string> seen, string ahead)
    {
        (int v, int rev) = Build();
        Func<string, string?> chain = n => seen.Exists(s => s.Equals(n, StringComparison.OrdinalIgnoreCase)) ? "" : VanillaShader.ChainText(n);
        Result? vertex = fragment && vs.Length > 0 ? VertexHalf(vs, log, v, rev) : null;
        string resref = fragment ? fs : vs;
        Result r = Translate(resref, fragment, chain, Env(fragment, log), fragment ? AlloyRenderer.VanillaEnvDefs : "",
                             ahead, v, rev, vertex is { Refused: null } ? vertex : null);
        _regions.Last[Key(fragment, vs, fs)] = r;
        if (r.Refused != null)
        {
            log?.Info($"{what}: {resref}.shd cannot run under Alloy -- {r.Refused}");
            return null;
        }
        return r.Host;
    }

    /// <summary>A vertex half as its own region translates it, for its fragment half's lanes and varyings: against the
    /// lit vertex stage's text ahead of the region, as its own build sees it, since that decides the declarations it drops.</summary>
    private static Result VertexHalf(string vs, CX.Framework.ExtenderLog? log, int v, int rev)
    {
        string lit = CX.Alloy.Programs._litVsSrc ?? AlloyRenderer.OwnedLitVs;
        int at = lit.IndexOf(ShaderSource.LibMark + ContentPrograms.VsLib + "\n", StringComparison.Ordinal);
        return Translate(vs, false, VanillaShader.ChainText, Env(false, log), "", at < 0 ? lit : lit[..at], v, rev);
    }

    /// <summary>The environment text for a half, bracketed so a content override of it swaps in.</summary>
    private static string Env(bool fragment, CX.Framework.ExtenderLog? log)
    {
        string resref = fragment ? EnvFsResref : EnvVsResref;
        return ShaderSource.Content(ShaderSource.LibMark + resref + "\n" + (fragment ? AlloyRenderer.ContentEnvFs : AlloyRenderer.ContentEnvVs)
                                    + ShaderSource.EndMark + resref + "\n", log);
    }

    /// <summary>The last translation of a pair's half this module, or null before its first build.</summary>
    internal static Result? Last(bool fragment, string vs, string fs)
        => _regions.Last.TryGetValue(Key(fragment, vs, fs), out Result? r) ? r : null;

    /// <summary>A module change: the next build reads the new module's files.</summary>
    internal static void Forget() => _regions = new RegionState();

    /// <summary>The running client's <c>BUILD_VERSION</c> and <c>BUILD_REVISION</c> as the engine prologue states them,
    /// off the build file's name (<c>8193.37-17</c>); the build CX is pinned to.</summary>
    private static (int Version, int Revision) Build()
    {
        string name = CX.Framework.ClientBuilds.Current?.Name ?? "";
        Match m = BuildName.Match(name);
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : (0, 0);
    }

    private static readonly Regex BuildName = new(@"^(\d+)\.(\d+)", RegexOptions.Compiled);
}
