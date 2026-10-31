using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CX.Graphics.Rhi;

/// <summary>What a translated program's declaration is bound as.</summary>
internal enum VkBindKind : byte { Sampled, StorageImage, UniformBlock, StorageBlock }

/// <summary>One sampler, image or block a translated program declares: its name (the block name for a block), its
/// GLSL type (the block name again for a block), and the set and binding it lands on.</summary>
internal readonly record struct VkOpaque(string Name, string Type, uint Set, uint Binding, VkBindKind Kind);

/// <summary>One loose uniform gathered into <see cref="VkGlsl.LooseBlock"/>: its type, name and array suffix
/// (<c>""</c> or a literal <c>"[6]"</c>).</summary>
internal readonly record struct VkLoose(string Type, string Name, string Array)
{
    /// <summary>Elements the member holds: 1 for a scalar, the literal inside <see cref="Array"/>'s brackets otherwise.</summary>
    internal int Count => Array.Length == 0 ? 1 : int.Parse(Array.AsSpan(1, Array.Length - 2), System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A program's two stages as Vulkan GLSL, what they declare, and why not when <see cref="Error"/> is set.</summary>
internal sealed class VkProgramText
{
    public string Vs = "", Fs = "";
    /// <summary>The one stage of a COMPUTE translation (<see cref="VkGlsl.TranslateCompute"/>); empty otherwise.</summary>
    public string Cs = "";
    /// <summary><see cref="VkGlsl.LooseBlock"/>'s members, in declaration order: vertex stage first.</summary>
    public readonly List<VkLoose> Loose = new();
    /// <summary>Every sampler, image and block either stage declares, deduped by stage-independent identity.</summary>
    public readonly List<VkOpaque> Opaques = new();
    /// <summary>Loose uniforms that carried a GL initializer, as <c>name = text</c>: a block member has none, so
    /// the caller writes the value.</summary>
    public readonly List<string> Initializers = new();
    public string? Error;
}

/// <summary>GL 4.6 GLSL as Vulkan GLSL, for a program the Vulkan backend links from the text GL would compile. Loose
/// uniforms move into one std140 block both stages declare alike, the per-batch seven into a push block, bindless
/// constructions into an index into set 1's sampler array, varyings get locations by name, unbound samplers the
/// units the caller names, and GL's draw built-ins Vulkan's spellings. Pure text; cold.</summary>
internal static partial class VkGlsl
{
    /// <summary>The block a stage's loose uniforms are gathered into, on <c>UboSlots.Loose</c>.</summary>
    internal const string LooseBlock = "CxLoose";
    internal const uint LooseBinding = 4;

    /// <summary>The per-batch values, in the push block's order; each is four bytes, so member i sits at 4 * i.</summary>
    internal static readonly (string Type, string Name)[] Push =
    [
        ("int", "uDrawBase"), ("float", "uAlphaCut"), ("int", "uSsaoOn"), ("float", "uAoDirect"),
        ("int", "uDecal"), ("int", "uSolid"), ("int", "uOitGob"),
    ];

    /// <summary>The bindless sampler array's name, on <see cref="VulkanToolchain.BindlessSet"/> binding 0, and the
    /// function every index into it goes through.</summary>
    internal const string BindlessArray = "cxTex2D", BindlessAt = "cxTex2DAt";

    /// <summary>Descriptors the array holds: an index at or past it samples slot 0, the zero texel, rather than one
    /// the host never wrote. The backend sets it to what the device seats as it opens, and the number lands in the
    /// text, so a module compiled under one cap is never read back under another.</summary>
    internal static uint BindlessCap { get; set; } = 1;

    private static readonly Regex Header = new(
        @"^\s*(?:layout\s*\((?<lq>[^()]*)\)\s*)?(?<q>(?:(?:flat|smooth|noperspective|invariant|centroid|sample|precise|highp|mediump|lowp|readonly|writeonly|coherent|volatile|restrict)\s+)*)(?<s>uniform|in|out|buffer)\b(?<rest>.*)$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex LeadQual = new(@"^\s*(flat|smooth|noperspective|invariant|centroid|sample|precise|highp|mediump|lowp|readonly|writeonly|coherent|volatile|restrict)\s+");
    private static readonly Regex Declarator = new(@"^\s*(?<n>\w+)\s*(?<a>\[[^\]]*\])?\s*(?:=\s*(?<i>.+))?$", RegexOptions.Singleline);
    private static readonly Regex Builtin = new(@"^(bool|int|uint|float|double|[biud]?vec[234]|d?mat[234](x[234])?)$");
    private static readonly Regex Opaque = new(@"^[iu]?(sampler|image|texture)\w*$");
    private static readonly Regex BindingQ = new(@"\bbinding\s*=\s*(\w+)");
    private static readonly Regex SetQ = new(@"\bset\s*=\s*(\w+)");
    private static readonly Regex LocationQ = new(@"\blocation\s*=\s*(\w+)");
    private static readonly Regex Construct = new(@"\bsampler2D\s*\(");
    private static readonly Regex OtherConstruct = new(@"\b[iu]?sampler\w+\s*\(");
    private static readonly Regex BindlessExt = new(@"^[ \t]*#extension\s+GL_ARB_bindless_texture\b[^\n]*\n?", RegexOptions.Multiline);
    private static readonly Regex DrawId = new(@"\bgl_DrawID\b"), BaseInst = new(@"\bgl_BaseInstance\b"),
                                  BaseVert = new(@"\bgl_BaseVertex\b"), InstId = new(@"\bgl_InstanceID\b"),
                                  VertId = new(@"\bgl_VertexID\b");

    /// <summary>A statement at file scope: its span in the text, the text masked of comments, and the line's
    /// preprocessor nesting.</summary>
    private readonly record struct Stmt(int Start, int End, string Masked, char Term);

    private readonly record struct Decl(string LayoutInner, bool HasLayout, string Quals, string Storage, string Type,
                                        List<(string Name, string Array, string? Init)> Names);

    /// <summary>A fragment's GL window depth in rewritten content text: <c>gl_FragCoord.z</c> where the program keeps GL's
    /// depth, one minus it where the program draws reversed depth.</summary>
    internal const string GlFragZ = "cxGlFragZ";

    /// <summary>Both stages of one program as Vulkan GLSL. <paramref name="samplerUnits"/> names the binding of every
    /// sampler or image declared without one; a declaration it does not name is an error.</summary>
    internal static VkProgramText Translate(string vs, string fs, IReadOnlyDictionary<string, uint> samplerUnits,
                                            bool glDepth = true)
    {
        var r = new VkProgramText();
        try
        {
            string tv = Builtins(Bindless(vs, out bool bv, r), out bool dv, r);
            fs = fs.Replace(GlFragZ, glDepth ? "gl_FragCoord.z" : "(1.0 - gl_FragCoord.z)", StringComparison.Ordinal);
            string tf = Builtins(Bindless(fs, out bool bf, r), out bool df, r);
            if (r.Error != null) return r;
            List<Stmt> sv = Statements(tv), sf = Statements(tf);
            var looseIdx = new Dictionary<string, int>(StringComparer.Ordinal);
            Gather(sv, tv + "\n" + tf, r, looseIdx);
            Gather(sf, tv + "\n" + tf, r, looseIdx);
            if (r.Error != null) return r;
            var outLoc = new Dictionary<string, int>(StringComparer.Ordinal);
            r.Vs = Emit(tv, sv, true, bv, dv, samplerUnits, outLoc, r);
            r.Fs = Emit(tf, sf, false, bf, df, samplerUnits, outLoc, r);
            if (glDepth && r.Error == null) r.Vs = DepthToZeroOne(r.Vs, r);
        }
        catch (FormatException e) { r.Error = e.Message; }
        return r;
    }

    /// <summary>Every <c>sampler2D(handle)</c> construction as an index into the bindless array, the ARB extension
    /// line gone. A construction of any other sampler type is an error: set 1 carries 2D samplers only.</summary>
    private static string Bindless(string src, out bool used, VkProgramText r)
    {
        used = false;
        string s = BindlessExt.Replace(src, "");
        string mask = Mask(s);
        var sb = new StringBuilder(s.Length + 256);
        int at = 0;
        for (Match m = Construct.Match(mask); m.Success; m = Construct.Match(mask, at))
        {
            int open = m.Index + m.Length - 1, close = Close(mask, open);
            if (close < 0) throw new FormatException($"an unclosed sampler2D( construction at {m.Index}");
            sb.Append(s, at, m.Index - at).Append(BindlessArray).Append("[nonuniformEXT(").Append(BindlessAt).Append("((")
              .Append(s, open + 1, close - open - 1).Append(").x))]");
            at = close + 1;
            used = true;
        }
        sb.Append(s, at, s.Length - at);
        string o = sb.ToString();
        Match bad = OtherConstruct.Match(Mask(o));
        if (bad.Success) r.Error ??= $"a bindless construction of {bad.Value.TrimEnd('(', ' ')}: set {VulkanToolchain.BindlessSet} holds 2D samplers only";
        return o;
    }

    /// <summary>GL's draw and vertex built-ins in Vulkan's spellings. GL's instance id excludes the base instance and
    /// Vulkan's index includes it, so the base comes off.</summary>
    private static string Builtins(string src, out bool drawParams, VkProgramText r)
    {
        string s = DrawId.Replace(src, "gl_DrawIDARB");
        s = BaseInst.Replace(s, "gl_BaseInstanceARB");
        s = BaseVert.Replace(s, "gl_BaseVertexARB");
        s = InstId.Replace(s, "(gl_InstanceIndex - gl_BaseInstanceARB)");
        s = VertId.Replace(s, "gl_VertexIndex");
        drawParams = s.Contains("gl_DrawIDARB", StringComparison.Ordinal) || s.Contains("gl_BaseInstanceARB", StringComparison.Ordinal)
                  || s.Contains("gl_BaseVertexARB", StringComparison.Ordinal);
        return s;
    }

    /// <summary>The loose uniforms of one stage into <paramref name="r"/>'s union; a name declared twice must agree.</summary>
    private static void Gather(List<Stmt> stmts, string both, VkProgramText r, Dictionary<string, int> idx)
    {
        foreach (Stmt st in stmts)
        {
            if (!Parse(st, out Decl d) || d.Storage != "uniform" || st.Term != ';' || !IsLoose(d.Type)) continue;
            foreach (var (name, arr, init) in d.Names)
            {
                string a = arr.Length == 0 ? "" : $"[{ResolveInt(arr[1..^1], both) ?? throw new FormatException($"loose uniform {name}{arr}: the size does not resolve to a literal")}]";
                if (init != null) r.Initializers.Add($"{name} = {init.Trim()}");
                int p = Array.FindIndex(Push, x => x.Name == name);
                if (p >= 0)
                {
                    if (Push[p].Type != d.Type || a.Length > 0) r.Error ??= $"{name} is {d.Type}{a}, the push block's is {Push[p].Type}";
                    continue;
                }
                if (idx.TryGetValue(name, out int i))
                {
                    if (r.Loose[i].Type != d.Type || r.Loose[i].Array != a) r.Error ??= $"loose uniform {name} is declared as both {r.Loose[i].Type}{r.Loose[i].Array} and {d.Type}{a}";
                    continue;
                }
                if (!Builtin.IsMatch(d.Type)) r.Error ??= $"loose uniform {name} is of type {d.Type}, which a block cannot carry ahead of its declaration";
                idx[name] = r.Loose.Count;
                r.Loose.Add(new VkLoose(d.Type, name, a));
            }
        }
    }

    private static bool IsLoose(string type) => !Opaque.IsMatch(type) && type != "atomic_uint";

    /// <summary>One stage rewritten: loose uniforms out, the shared declarations in at the first file-scope line,
    /// bindings and locations added, extensions after <c>#version</c>.</summary>
    private static string Emit(string src, List<Stmt> stmts, bool vertex, bool bindless, bool drawParams,
                               IReadOnlyDictionary<string, uint> units, Dictionary<string, int> outLoc, VkProgramText r)
    {
        var edits = new List<(int Start, int End, string Text)>();
        var reserved = new HashSet<int>();
        var pending = new List<(Stmt St, Decl D)>();
        foreach (Stmt st in stmts)
        {
            if (!Parse(st, out Decl d)) continue;
            if (st.Term == '{')
            {
                if (d.Storage is "uniform" or "buffer") Block(st, d, r);
                continue;
            }
            switch (d.Storage)
            {
                case "uniform" when IsLoose(d.Type):
                    edits.Add((st.Start, st.End, ""));
                    break;
                case "uniform":
                    edits.Add((st.Start, st.End, OpaqueDecl(d, units, r)));
                    break;
                case "in" or "out" when d.Names.Count > 0 && (vertex ? d.Storage == "out" : true):
                    if (LocationQ.Match(d.LayoutInner) is { Success: true } lm)
                    {
                        int at = ResolveInt(lm.Groups[1].Value, src) ?? 0;
                        for (int k = 0; k < Slots(d.Type, d.Names[0].Array, src) * d.Names.Count; k++) reserved.Add(at + k);
                        if (vertex) foreach (var n in d.Names) outLoc[n.Name] = at;
                    }
                    else pending.Add((st, d));
                    break;
            }
        }
        int next = 0;
        int unlocatedOuts = 0;
        foreach (var (st, d) in pending) if (!vertex && d.Storage == "out") unlocatedOuts++;
        foreach (var (st, d) in pending)
        {
            var sb = new StringBuilder();
            foreach (var (name, arr, _) in d.Names)
            {
                int loc;
                if (!vertex && d.Storage == "out")
                {
                    if (unlocatedOuts > 1 || reserved.Contains(0)) { r.Error ??= $"fragment output {name} has no location and is not the stage's only output"; return src; }
                    loc = 0;
                }
                else if (!vertex && outLoc.TryGetValue(name, out int l)) loc = l;
                else
                {
                    int n = Slots(d.Type, arr, src);
                    while (Taken(reserved, next, n)) next++;
                    loc = next;
                    for (int k = 0; k < n; k++) reserved.Add(loc + k);
                    if (vertex) outLoc[name] = loc;
                }
                string lq = d.HasLayout ? d.LayoutInner + ", " : "";
                sb.Append($"layout({lq}location = {loc}) {d.Quals}{d.Storage} {d.Type} {name}{arr}; ");
            }
            edits.Add((st.Start, st.End, sb.ToString().TrimEnd()));
        }
        edits.Add((BodyStart(src), BodyStart(src), Shared(bindless, r)));
        edits.Sort((a, b) => a.Start != b.Start ? b.Start.CompareTo(a.Start) : (b.End - b.Start).CompareTo(a.End - a.Start));
        var o = new StringBuilder(src);
        foreach (var (s, e, t) in edits) o.Remove(s, e - s).Insert(s, t);
        var ext = new StringBuilder();
        if (bindless) ext.Append("#extension GL_EXT_nonuniform_qualifier : require\n");
        if (drawParams) ext.Append("#extension GL_ARB_shader_draw_parameters : require\n");
        return AfterVersion(o.ToString(), ext.ToString());
    }

    private static readonly Regex MainDecl = new(@"\bvoid\s+main\s*\(\s*(?:void)?\s*\)");

    /// <summary>The vertex stage's clip depth from GL's -w..w to Vulkan's 0..w, after its own main: every Alloy
    /// stage gets the same last step, so the prepass and colour stages still compute one expression, and the depth
    /// a fragment writes is the window depth GL's default range would have given it.</summary>
    private static string DepthToZeroOne(string vs, VkProgramText r)
    {
        Match m = MainDecl.Match(Mask(vs));
        if (!m.Success) { r.Error ??= "the vertex stage has no main to follow with the depth remap"; return vs; }
        return vs[..m.Index] + "void cxGlMain()" + vs[(m.Index + m.Length)..]
             + "\nvoid main()\n{\n    cxGlMain();\n    gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;\n}\n";
    }

    private static bool Taken(HashSet<int> used, int at, int n)
    {
        for (int k = 0; k < n; k++) if (used.Contains(at + k)) return true;
        return false;
    }

    /// <summary>The declarations every stage of the program carries alike: the bindless array, the push block and
    /// the loose block, at the first file-scope line.</summary>
    private static string Shared(bool bindless, VkProgramText r)
    {
        var sb = new StringBuilder("// VkGlsl: declarations shared by both stages\n");
        if (bindless)
            sb.Append($"layout(set = {VulkanToolchain.BindlessSet}, binding = 0) uniform sampler2D {BindlessArray}[];\n")
              .Append($"uint {BindlessAt}(uint i) {{ return i < {BindlessCap}u ? i : 0u; }}\n");
        sb.Append("layout(push_constant) uniform CxBatch {");
        foreach (var (t, n) in Push) sb.Append(' ').Append(t).Append(' ').Append(n).Append(';');
        sb.Append(" };\n");
        if (r.Loose.Count > 0)
        {
            // the block this writes is a declaration like any other: its host has to bind it
            AddOpaque(r, new VkOpaque(LooseBlock, LooseBlock, 0, LooseBinding, VkBindKind.UniformBlock));
            sb.Append($"layout(std140, binding = {LooseBinding}) uniform {LooseBlock} {{\n");
            foreach (VkLoose l in r.Loose) sb.Append("    ").Append(l.Type).Append(' ').Append(l.Name).Append(l.Array).Append(";\n");
            sb.Append("};\n");
        }
        return sb.ToString();
    }

    /// <summary>A uniform or storage block's binding into <paramref name="r"/>; one with none is an error.</summary>
    private static void Block(Stmt st, Decl d, VkProgramText r)
    {
        string name = d.Type;
        if (BindingQ.Match(d.LayoutInner) is not { Success: true } bm || !uint.TryParse(bm.Groups[1].Value, out uint b))
        {
            r.Error ??= $"block {name} has no literal binding";
            return;
        }
        uint set = SetQ.Match(d.LayoutInner) is { Success: true } sm && uint.TryParse(sm.Groups[1].Value, out uint sv) ? sv : 0;
        AddOpaque(r, new VkOpaque(name, name, set, b, d.Storage == "buffer" ? VkBindKind.StorageBlock : VkBindKind.UniformBlock));
    }

    /// <summary>A sampler or image declaration with every declarator on a binding of its own: the one it carries, else
    /// the caller's unit for its name.</summary>
    private static string OpaqueDecl(Decl d, IReadOnlyDictionary<string, uint> units, VkProgramText r)
    {
        bool image = d.Type.Contains("image", StringComparison.Ordinal);
        Match bm = BindingQ.Match(d.LayoutInner);
        uint set = SetQ.Match(d.LayoutInner) is { Success: true } sm && uint.TryParse(sm.Groups[1].Value, out uint sv) ? sv : 0;
        var sb = new StringBuilder();
        foreach (var (name, arr, _) in d.Names)
        {
            uint b;
            string inner;
            if (bm.Success)
            {
                if (!uint.TryParse(bm.Groups[1].Value, out b)) { r.Error ??= $"{name}'s binding {bm.Groups[1].Value} is not a literal"; return ""; }
                inner = d.LayoutInner;
            }
            else
            {
                if (!units.TryGetValue(name, out b)) { r.Error ??= $"{d.Type} {name} has no binding and the unit table does not name it"; return ""; }
                inner = d.HasLayout ? $"{d.LayoutInner}, binding = {b}" : $"binding = {b}";
            }
            AddOpaque(r, new VkOpaque(name, d.Type, set, b, image ? VkBindKind.StorageImage : VkBindKind.Sampled));
            sb.Append($"layout({inner}) {d.Quals}uniform {d.Type} {name}{arr}; ");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Every sampler, image and texture <paramref name="src"/> declares at file scope, in order: its type, name
    /// and the <c>binding</c> its layout names (null when it names none). Comments are not read.</summary>
    internal static List<(string Type, string Name, string? Binding)> Opaques(string src)
    {
        var list = new List<(string, string, string?)>();
        foreach (Stmt st in Statements(src))
        {
            if (st.Term != ';' || !Parse(st, out Decl d) || d.Storage != "uniform" || !Opaque.IsMatch(d.Type)) continue;
            Match bm = BindingQ.Match(d.LayoutInner);
            foreach (var (name, _, _) in d.Names) list.Add((d.Type, name, bm.Success ? bm.Groups[1].Value : null));
        }
        return list;
    }

    private static void AddOpaque(VkProgramText r, VkOpaque o)
    {
        if (!r.Opaques.Contains(o)) r.Opaques.Add(o);
    }

    /// <summary>Locations a varying of <paramref name="type"/> takes: a column per matrix column, times the array length.</summary>
    private static int Slots(string type, string arr, string src)
    {
        int cols = type.StartsWith("mat", StringComparison.Ordinal) || type.StartsWith("dmat", StringComparison.Ordinal)
            ? type[type.IndexOf("mat", StringComparison.Ordinal) + 3] - '0' : 1;
        int n = arr.Length == 0 ? 1 : ResolveInt(arr[1..^1], src) ?? 1;
        return cols * n;
    }

    /// <summary>A decimal literal, or a name a <c>#define</c> or <c>const int</c>/<c>const uint</c> in
    /// <paramref name="src"/> gives one; null otherwise.</summary>
    internal static int? ResolveInt(string expr, string src)
    {
        string e = expr.Trim().TrimEnd('u', 'U');
        if (int.TryParse(e, out int v)) return v;
        if (!Regex.IsMatch(e, @"^\w+$")) return null;
        Match m = Regex.Match(src, $@"(?:#define\s+{e}\s+(\d+)u?\b|const\s+u?int\s+{e}\s*=\s*(\d+)u?\s*[;,])");
        if (!m.Success) return null;
        return int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
    }

    /// <summary>Parse a file-scope declaration's header and declarators; false for any other statement.</summary>
    private static bool Parse(Stmt st, out Decl d)
    {
        d = default;
        Match m = Header.Match(st.Masked);
        if (!m.Success) return false;
        string quals = m.Groups["q"].Value;
        string rest = m.Groups["rest"].Value;
        for (Match q = LeadQual.Match(rest); q.Success; q = LeadQual.Match(rest))
        {
            quals += q.Groups[1].Value + " ";
            rest = rest[q.Length..];
        }
        rest = rest.Trim();
        string lq = m.Groups["lq"].Value.Trim();
        bool hasLayout = m.Groups["lq"].Success;
        var names = new List<(string, string, string?)>();
        if (st.Term == '{')
        {
            d = new Decl(lq, hasLayout, quals, m.Groups["s"].Value, rest.Trim(), names);
            return rest.Length > 0;
        }
        if (rest.Length == 0) { d = new Decl(lq, hasLayout, quals, m.Groups["s"].Value, "", names); return true; }
        int sp = 0;
        while (sp < rest.Length && (char.IsLetterOrDigit(rest[sp]) || rest[sp] == '_')) sp++;
        string type = rest[..sp];
        foreach (string part in SplitTop(rest[sp..]))
        {
            Match dm = Declarator.Match(part);
            if (!dm.Success) return false;
            names.Add((dm.Groups["n"].Value, dm.Groups["a"].Success ? dm.Groups["a"].Value.Replace(" ", "") : "",
                       dm.Groups["i"].Success ? dm.Groups["i"].Value : null));
        }
        d = new Decl(lq, hasLayout, quals, m.Groups["s"].Value, type, names);
        return type.Length > 0 && names.Count > 0;
    }

    /// <summary>Comma-separated parts, commas inside brackets or parentheses kept.</summary>
    private static List<string> SplitTop(string s)
    {
        var parts = new List<string>();
        int depth = 0, from = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == ',' && depth == 0) { parts.Add(s[from..i]); from = i + 1; }
        }
        parts.Add(s[from..]);
        return parts;
    }

    /// <summary>Every statement that starts a line at file scope -- outside braces and parentheses, not a
    /// preprocessor line -- and opens with a qualifier, from its line start to its terminator.</summary>
    private static List<Stmt> Statements(string src)
    {
        string mask = Mask(src);
        var list = new List<Stmt>();
        int brace = 0, paren = 0;
        int i = 0;
        while (i < mask.Length)
        {
            int eol = mask.IndexOf('\n', i);
            if (eol < 0) eol = mask.Length;
            string line = mask[i..eol];
            string t = line.TrimStart();
            if (t.StartsWith('#')) { i = eol + 1; continue; }
            if (brace == 0 && paren == 0 && StartsDecl(t))
            {
                int term = mask.IndexOfAny([';', '{'], i);
                if (term > 0)
                {
                    list.Add(new Stmt(i, term + 1, mask[i..term], mask[term]));
                    if (mask[term] == '{')
                    {
                        int close = CloseBrace(mask, term);
                        int semi = close < 0 ? -1 : mask.IndexOf(';', close);
                        if (semi > 0)
                        {
                            int nl = mask.IndexOf('\n', semi);
                            i = nl < 0 ? mask.Length : nl + 1;
                            continue;
                        }
                    }
                    else if (mask.IndexOf('\n', i) is int n && n >= 0 && term > n)
                    {
                        i = mask.IndexOf('\n', term) is int n2 && n2 >= 0 ? n2 + 1 : mask.Length;
                        continue;
                    }
                }
            }
            foreach (char c in line)
            {
                if (c == '{') brace++;
                else if (c == '}') brace--;
                else if (c == '(') paren++;
                else if (c == ')') paren--;
            }
            i = eol + 1;
        }
        return list;
    }

    private static bool StartsDecl(string t)
    {
        int sp = 0;
        while (sp < t.Length && (char.IsLetter(t[sp]) || t[sp] == '_')) sp++;
        return t[..sp] is "layout" or "uniform" or "in" or "out" or "buffer" or "flat" or "smooth" or "noperspective"
            or "centroid" or "sample" or "readonly" or "writeonly" or "coherent" or "volatile" or "restrict"
            or "highp" or "mediump" or "lowp";
    }

    /// <summary>The first line at file scope outside every preprocessor conditional that is not a preprocessor
    /// line: where a declaration both stages share can sit ahead of every use.</summary>
    private static int BodyStart(string src)
    {
        string mask = Mask(src);
        int depth = 0;
        for (int i = 0; i < mask.Length;)
        {
            int eol = mask.IndexOf('\n', i);
            if (eol < 0) eol = mask.Length;
            string t = mask[i..eol].Trim();
            if (t.StartsWith('#'))
            {
                string d = t[1..].TrimStart();
                if (d.StartsWith("if", StringComparison.Ordinal)) depth++;
                else if (d.StartsWith("endif", StringComparison.Ordinal)) depth--;
            }
            else if (t.Length > 0 && depth == 0) return i;
            i = eol + 1;
        }
        return src.Length;
    }

    /// <summary><paramref name="src"/> with every comment blanked to spaces, newlines kept, so offsets agree.</summary>
    internal static string Mask(string src)
    {
        var c = src.ToCharArray();
        for (int i = 0; i < c.Length - 1; i++)
        {
            if (c[i] == '/' && c[i + 1] == '/')
                while (i < c.Length && c[i] != '\n') c[i++] = ' ';
            else if (c[i] == '/' && c[i + 1] == '*')
            {
                c[i] = c[i + 1] = ' ';
                i += 2;
                while (i < c.Length - 1 && !(c[i] == '*' && c[i + 1] == '/')) { if (c[i] != '\n') c[i] = ' '; i++; }
                if (i < c.Length - 1) c[i] = c[i + 1] = ' ';
                i++;
            }
        }
        return new string(c);
    }

    private static int Close(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static int CloseBrace(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary><paramref name="lines"/> right after the <c>#version</c> line, where an <c>#extension</c> must sit.</summary>
    private static string AfterVersion(string src, string lines)
    {
        if (lines.Length == 0) return src;
        int v = src.IndexOf("#version", StringComparison.Ordinal);
        int e = v < 0 ? -1 : src.IndexOf('\n', v);
        return e < 0 ? lines + src : src[..(e + 1)] + lines + src[(e + 1)..];
    }
}
