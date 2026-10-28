using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Content stages' ALLOY-NATIVE versions and the GPU resources they declare. Wherever Alloy hosts a content
/// stage <c>X</c> -- a sky's fragment stage, either half of a material pair, the post chain -- a chain file <c>cx_X</c>
/// runs in its place under <c>CX_NATIVE</c> and may declare images and compute passes on <c>#pragma cx_*</c> lines; one
/// that fails hosts <c>X</c>, out loud. Rules: <c>docs/shader-overrides.md</c>, "Native stages, images and passes".</summary>
internal static unsafe partial class StageResources
{
    /// <summary>The prefix a native stage's resref carries: <c>cx_</c> + the stage, which has to fit a resref
    /// (<see cref="ShaderResource.ResRefBytes"/>) -- a longer stage name simply cannot have one.</summary>
    internal const string Prefix = "cx_";

    /// <summary>The define every native stage is built with. The sky's host also takes it as "hand back linear HDR".</summary>
    internal const string NativeDefine = "CX_NATIVE 1";

    /// <summary>The compute wrapper a declared pass is hosted in (an override point), and the token its region's
    /// resref is written into before the splice -- the same shape as <see cref="SkyStage.ShdFs"/>.</summary>
    internal const string ShdPassCs = "cx_pass_cs";
    private const string CsToken = "$PASSCS$";

    /// <summary>Distinct native stages a session may declare. Past it a stage hosts its plain version, said once.</summary>
    internal const int MaxStages = 32;

    /// <summary>The declaration budgets, per stage. Past any of them the stage is refused rather than trimmed: a
    /// shader that silently got half the volume it asked for is a look nobody authored.</summary>
    internal const int MaxImages = 32, MaxPasses = 32, Max2dSide = 4096, Max3dSide = 256, MaxViewDiv = 8;
    /// <summary>Image memory across every live native stage, taken against the measured extents.</summary>
    internal const long MaxImageBytes = 512L << 20;
    /// <summary>A GLSL identifier's room in the declaration -- short enough that <c>&lt;name&gt;_prev</c> is
    /// still one too.</summary>
    internal const int MaxNameChars = 31;

    /// <summary>Image units, from 0: a draw program's stop below the lit stage's decal pair on 6 and 7, and a pass takes
    /// the GL floor of 8 -- every Alloy compute pass binds its own before it dispatches and hands them back at 0.</summary>
    internal const uint DrawImgUnits = 6, PassImgUnits = 8;

    /// <summary>What a declared image is sized by. <see cref="View"/> is the MAIN view's render size over a
    /// divisor, so a screen-space buffer follows the render scale and the window.</summary>
    private enum Shape { Tex2d, Tex3d, View }

    private sealed class Img
    {
        public string Name = "";
        public Shape Shape;
        /// <summary>The declared extent for <see cref="Shape.Tex2d"/>/<see cref="Shape.Tex3d"/>; for
        /// <see cref="Shape.View"/>, <see cref="Div"/> is declared and the extent is measured.</summary>
        public int W, H, D = 1, Div = 1;
        public uint Format;
        public int Bpp;
        public bool History, Mips, Clamp;
        /// <summary>Does a <c>frame</c> pass write this image's CURRENT half? Only such a history image's two
        /// halves swap: swapping one that nothing rewrites would alternate its name between the baked
        /// texture and a zeroed one, every frame, with nothing to see but the flicker.</summary>
        public bool Swap;
        /// <summary>A Vulkan draw program writes this image as a storage image, so the frame's head hands it over writable.</summary>
        public bool VkDrawStore;
        /// <summary>The texture units its two halves stay bound on, from <see cref="TextureSlots.ResourceBase"/>: a
        /// sampler of any program that reads the image is pointed there once, when that program is attached.</summary>
        public uint Unit, UnitPrev;
        /// <summary>The extent the live texture was made at, so a view resize is a fact and not a guess.</summary>
        public int MadeW, MadeH, MadeD;
    }

    /// <summary>One declared pass: a content compute stage hosted in <see cref="ShdPassCs"/>, dispatched over
    /// one image's extent. <see cref="Once"/> runs when the images are (re)made, the rest once per presented
    /// frame its stage is drawn.</summary>
    private sealed class Pass
    {
        public bool Once;
        public string Ref = "";
        /// <summary>The image name the declaration gave, and its index once the names are resolved.</summary>
        public string ImageRef = "";
        public int Image = -1;
        public int Gx = 1, Gy = 1, Gz = 1;
        public StageEnvLoc Env;
        public FrameLoc Frame;
        public ImageBind[] Binds = [], Samplers = [];
    }

    /// <summary>One image a program reads or writes, resolved at build: where its uniform is, which stage and image it
    /// names, and its unit -- an image unit of the program's own for an image, the image's own texture unit for a
    /// sampler, which it points at for the program's life.</summary>
    private struct ImageBind
    {
        public int Loc;
        public int Stage, Image;
        public bool Prev;
        public uint Unit;
    }

    private sealed class Stage
    {
        public string Ref = "";
        public Img[] Images = [];
        public Pass[] Passes = [];
        public bool Failed;
        public string Reason = "";
        public bool Said;
        public bool Made;
        /// <summary>The main-view extent the view-sized images were made at: a change remakes them.</summary>
        public int MadeW, MadeH;
        public long Bytes;
        /// <summary>The last frame a host drew or staged it, the last frame its frame passes ran, and the last frame
        /// its history was garbage -- images (re)made, or its passes resumed after a gap.</summary>
        public int LastUse = int.MinValue, RanFrame = int.MinValue, FreshFrame = -1;
        /// <summary>Passes dispatched this period, and frames its passes ran with no history, for the <c>[alloy]</c> line.</summary>
        public int Runs, FreshRuns;
        /// <summary>The <c>cx_fog</c> texel -- the image index (-1: none declared) and its coordinates.</summary>
        public int FogImage = -1, FogX, FogY;
        public string FogRef = "";
        /// <summary>The <c>cx_overlay</c> image, -1 for none, and its name as declared.</summary>
        public int OverlayImage = -1;
        public string OverlayRef = "";
    }

    /// <summary>Every stage declaration plus the main view's frame, in one object: a device loss replaces it, so no
    /// unit or failure latch taken against the dead device survives.</summary>
    private sealed class ResourcesState
    {
        public readonly Stage?[] Stages = new Stage[MaxStages];
        public int Count;
        public bool SaidFull;
        /// <summary>The next texture unit an image half takes, and the driver's combined-unit count (0: not asked).</summary>
        public uint NextUnit = TextureSlots.ResourceBase;
        public int UnitLimit;
        /// <summary>The MAIN view's render size and camera, noted once per frame before anything is staged; the
        /// camera it noted the frame before, and that frame's number. Every pass and every program reads these.</summary>
        public int ViewW = 1, ViewH = 1, ViewSeq = int.MinValue;
        public bool HaveView;
        public Matrix4x4 InvVp = Matrix4x4.Identity, CurVp = Matrix4x4.Identity, PrevVp = Matrix4x4.Identity;
        public Vector3 Eye;
        /// <summary>The frame whose head last ran, so a frame runs its passes once.</summary>
        public int HeadSeq = int.MinValue;
        /// <summary>The one <c>cx_fog</c> texel and the one <c>cx_overlay</c> a module may have: the stage that
        /// declared each (-1: none), and the last colour read back, LINEAR.</summary>
        public int FogStage = -1, OverlayStage = -1;
        /// <summary>Why the texel cannot be read on this driver, said once; empty while it can.</summary>
        public string FogMiss = "";
        public int FogSeq;
        public Vector3 Fog;
        public bool HaveFog;
    }

    private static ResourcesState _gl = new();

    /// <summary>Device loss: every image is gone, and a failure latch taken against the old device is not
    /// evidence about the new one.</summary>
    internal static void Forget() => _gl = new ResourcesState();

    /// <summary>Module change (and the <c>alloy.native</c> edit): the declarations and the pass text came off THAT
    /// module's chain, so the images and programs have a module lifetime. Deletes the live names, then forgets --
    /// which is also the one place a refusal latch is cleared. The hosts drop their own programs beside it.</summary>
    internal static void DropAll()
    {
        var g = _gl;
        for (int i = 0; i < g.Count; i++) Release(g.Stages[i]);
        Forget();
    }

    private static void Release(Stage? s)
    {
        if (s == null) return;
        foreach (Img im in s.Images) { im.MadeW = 0; im.MadeH = 0; im.MadeD = 0; }
        s.Made = false;
    }

    /// <summary>Can a stage have a native version at all? <see cref="Prefix"/> plus the stage name has to fit a
    /// resref, since the native stage is a file in a hak like any other.</summary>
    internal static bool Fits(string stage) => Prefix.Length + stage.Length <= ShaderResource.ResRefBytes;

    /// <summary>Is <paramref name="resref"/> one of ALLOY's own resrefs -- a stage or a library? A content stage
    /// called <c>sky_fs</c> would name <c>cx_sky_fs</c>, which is an override of Alloy's own wrapper, not a native
    /// stage of it. Cold: once per stage per module.</summary>
    internal static bool Reserved(string resref)
    {
        foreach ((string r, _) in ShaderStages.All)
            if (string.Equals(r, resref, StringComparison.OrdinalIgnoreCase)) return true;
        foreach ((string r, _) in ShaderSource.Libraries())
            if (string.Equals(r, resref, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The native stage Alloy hosts in place of content stage <paramref name="stage"/>, or null: none in the
    /// chain, <c>alloy.native</c> off, a name that cannot carry the prefix or one that is Alloy's own. Render thread,
    /// cold -- once per stage per module.</summary>
    internal static string? NativeOf(string stage)
    {
        if (!Renderer.Native.V || stage.Length == 0 || !Fits(stage)) return null;
        string r = Prefix + stage.ToLowerInvariant();
        return !Reserved(r) && VanillaShader.InChain(r) ? r : null;
    }

    /// <summary>Is this id a native stage that may run? A failed one is a stage whose host runs the plain one.</summary>
    internal static bool Live(int id) => At(id) is { Failed: false };

    private static Stage? At(int id)
    {
        var g = _gl;
        return (uint)id < (uint)g.Count ? g.Stages[id] : null;
    }

    /// <summary>A host drew or staged stage <paramref name="id"/> this frame (-1: none): its frame passes run at the
    /// head of the next one, and at this one's when it is staged ahead of it. Hot, no GL.</summary>
    internal static void Use(int id)
    {
        if (At(id) is { } s) s.LastUse = AlloyRenderer.FrameSeq;
    }

    /// <summary>The MAIN view's render size and unjittered camera, noted once per frame before anything is staged,
    /// so a probe face, a reflection and every pass see the camera the player is looking through. The camera of the
    /// frame before rides beside it; after a gap there is none, and the pair reads as standing still.</summary>
    internal static void NoteView(int* viewport, Matrix4x4* vpClean, Vector3 eye)
    {
        var g = _gl;
        int f = AlloyRenderer.FrameSeq;
        if (g.ViewSeq == f) return;
        g.PrevVp = Gap(g.ViewSeq, f) ? *vpClean : g.CurVp;
        g.CurVp = *vpClean;
        if (!Matrix4x4.Invert(*vpClean, out g.InvVp)) g.InvVp = Matrix4x4.Identity;
        g.ViewW = viewport[2] > 0 ? viewport[2] : 1;
        g.ViewH = viewport[3] > 0 ? viewport[3] : 1;
        g.HaveView = true;
        g.Eye = eye;
        g.ViewSeq = f;
    }

    /// <summary>Is frame <paramref name="now"/> the one after <paramref name="last"/>? Anything else -- including the
    /// first ever -- is a gap, and history across a gap is not one frame old.</summary>
    private static bool Gap(int last, int now) => last == int.MinValue || last != now - 1;

    /// <summary>The frame as program built from stages <paramref name="a"/> and <paramref name="b"/> reads it: fresh
    /// when either one's history is garbage this frame.</summary>
    private static StageFrame FrameFor(int a, int b)
    {
        var g = _gl;
        int f = AlloyRenderer.FrameSeq;
        bool fresh = At(a) is { } sa && sa.FreshFrame == f || At(b) is { } sb && sb.FreshFrame == f;
        return new StageFrame(g.InvVp, g.PrevVp, g.Eye, g.ViewW, g.ViewH, f, fresh);
    }

    /// <summary>Zeroed with the rest of the period report's counters.</summary>
    internal static void ResetPeriod()
    {
        var g = _gl;
        for (int i = 0; i < g.Count; i++) if (g.Stages[i] is { } s) { s.Runs = 0; s.FreshRuns = 0; }
    }

    /// <summary>Every native stage's clause on the period line: what it runs, or the reason it stood down -- "none
    /// shipped" and "one that would not build" are different facts and read differently.</summary>
    internal static string Stats()
    {
        var g = _gl;
        if (g.Count == 0) return "native stages: none";
        var sb = new System.Text.StringBuilder("native stages: ");
        for (int i = 0; i < g.Count; i++)
        {
            if (g.Stages[i] is not { } s) continue;
            if (i > 0) sb.Append("; ");
            if (s.Failed) { sb.Append(s.Ref).Append(" FELL BACK: ").Append(s.Reason); continue; }
            sb.Append(s.Ref).Append($" ({s.Passes.Length} pass(es), {s.Runs} ran this period, {s.FreshRuns} frame(s) with no history, "
                                     + $"{s.Images.Length} image(s), ")
              .Append($"{s.Bytes / (1024.0 * 1024.0):F1} MiB");
            if (g.FogStage == i)
                sb.Append(g.FogMiss.Length > 0 ? $", fog colour NOT READ: {g.FogMiss}"
                        : g.HaveFog ? $", fog colour ({g.Fog.X:F3},{g.Fog.Y:F3},{g.Fog.Z:F3}) from {s.FogRef}({s.FogX},{s.FogY})"
                        : $", fog colour from {s.FogRef}({s.FogX},{s.FogY}) not read back yet");
            if (g.OverlayStage == i) sb.Append(", overlay ").Append(s.OverlayRef);
            sb.Append(')');
        }
        return sb.ToString();
    }

    /// <summary>The colour the fog converges to, LINEAR, while the stage naming a <c>cx_fog</c> texel runs its
    /// passes and one has been read back. Staging path: no GL call.</summary>
    internal static bool FogColour(out Vector3 c)
    {
        var g = _gl;
        c = default;
        if (!g.HaveFog || At(g.FogStage) is not { Failed: false } s || s.RanFrame < AlloyRenderer.FrameSeq - 1) return false;
        c = g.Fog;
        return true;
    }

    /// <summary>Latch a failure: said once with the reason, the stage's GL objects released and its fog texel and
    /// overlay given up, and its host runs the plain stage from here on. The latch lives as long as this state object
    /// does -- a module change, a context loss or an <c>alloy.native</c> edit is what clears it.</summary>
    internal static void Fail(int id, string reason, ExtenderLog? log)
    {
        if (At(id) is not { } s) return;
        var g = _gl;
        s.Failed = true;
        s.Reason = reason;
        Release(s);
        if (g.FogStage == id) { g.FogStage = -1; g.HaveFog = false; }
        if (g.OverlayStage == id) g.OverlayStage = -1;
        if (s.Said) return;
        s.Said = true;
        log?.Always($"StageResources: {s.Ref}.shd is not run -- {reason}. Its host runs {s.Ref[Prefix.Length..]}.shd, "
                  + "the stage the engine runs, instead.");
    }

    /// <summary>Parse a native stage's own <c>#pragma cx_*</c> lines, take the budgets, give its images their units and
    /// build its passes. Always hands back an id (so the period line can name a refusal), -1 only when the table is
    /// full. The declarations are read from the stage's TOP-LEVEL text alone: an include is a library.</summary>
    internal static int Declare(string nativeRef, ExtenderLog? log)
    {
        var g = _gl;
        for (int i = 0; i < g.Count; i++)
            if (g.Stages[i] is { } have && string.Equals(have.Ref, nativeRef, StringComparison.Ordinal)) return i;
        if (g.Count >= MaxStages)
        {
            if (!g.SaidFull)
                log?.Always($"StageResources: more than {MaxStages} distinct native stages in one module -- {nativeRef}.shd "
                          + "and any later one are not run, and their hosts run the plain stages");
            g.SaidFull = true;
            return -1;
        }
        int id = g.Count++;
        var s = new Stage { Ref = nativeRef };
        g.Stages[id] = s;
        if (Reserved(nativeRef))
        { Fail(id, $"{nativeRef} is one of Alloy's own resrefs, not a native stage", log); return id; }
        string? text = VanillaShader.ChainText(nativeRef);
        if (text == null) { Fail(id, "the resource chain has no such file (or alloy.vanillabridge=0)", log); return id; }
        if (!ParseDecls(s, text, out string why) || !Claim(id, s, out why) || !Units(s, out why)) { Fail(id, why, log); return id; }
        // The fixed-size images are a build-time fact and are budgeted as one; the view-sized ones join at the make.
        if (!Budget(s, 0, 0, out _, out why)) { Fail(id, why, log); return id; }
        // Vulkan builds the passes at its frame's head, where the device is (VkBuildPasses)
        if (s.Images.Length > 0 || s.Passes.Length > 0)
            log?.Info($"StageResources: {nativeRef}.shd declares {s.Images.Length} image(s) and {s.Passes.Length} pass(es)");
        return id;
    }

    /// <summary>A module has one fog colour and one overlay: a stage naming either while another live stage holds it is
    /// refused, rather than one of the two being quietly ignored.</summary>
    private static bool Claim(int id, Stage s, out string why)
    {
        var g = _gl;
        why = "";
        if (s.FogImage >= 0 && At(g.FogStage) is { Failed: false } fog)
        { why = $"it names a cx_fog texel, and {fog.Ref} already does: a module has one fog colour"; return false; }
        if (s.OverlayImage >= 0 && At(g.OverlayStage) is { Failed: false } over)
        { why = $"it names a cx_overlay, and {over.Ref} already does: a frame has one overlay"; return false; }
        if (s.FogImage >= 0) { g.FogStage = id; g.HaveFog = false; }
        if (s.OverlayImage >= 0) g.OverlayStage = id;
        return true;
    }

    /// <summary>Each image half its own texture unit, above every unit Alloy claims: the textures stay bound there, so
    /// a program reads an image from whichever pass draws it with no bind of its own. Refused past the driver's
    /// combined-unit count, which GL 4.6 floors well above what one stage can declare.</summary>
    private static bool Units(Stage s, out string why)
    {
        var g = _gl;
        why = "";
        if (s.Images.Length == 0) return true;
        if (g.UnitLimit == 0)
        {
            g.UnitLimit = int.MaxValue;
        }
        uint next = g.NextUnit;
        foreach (Img im in s.Images)
        {
            im.Unit = next++;
            im.UnitPrev = im.History ? next++ : 0u;
        }
        if (next > (uint)g.UnitLimit)
        {
            why = $"its images would need texture units up to {next - 1}, past the {g.UnitLimit} this driver combines "
                + "(every native stage declared this module holds its own, a refused one included)";
            return false;
        }
        g.NextUnit = next;
        return true;
    }

    /// <summary>The declaration grammar, one line at a time. Every refusal names the line, because a typo in
    /// a pragma is invisible to the GLSL compiler -- the whole point of carrying the declarations there.</summary>
    private static bool ParseDecls(Stage s, string text, out string why)
    {
        var images = new List<Img>();
        var passes = new List<Pass>();
        string fogLine = "", overLine = "";
        why = "";
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("#pragma", StringComparison.Ordinal)) continue;
            string[] tok = line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length < 2 || !tok[1].StartsWith(Prefix, StringComparison.Ordinal)) continue;
            for (int i = 2; i < tok.Length; i++)
                if (!PlainToken(tok[i]))
                {
                    why = $"`{tok[i]}` on `{line}` is neither an identifier nor a decimal integer, and a GLSL "
                        + "preprocessor would refuse the line -- every token of a cx_ declaration is one or the other";
                    return false;
                }
            switch (tok[1])
            {
                case "cx_image":
                    if (!ParseImage(tok, images, out why)) return false;
                    break;
                case "cx_pass":
                    if (!ParsePass(tok, passes, out why)) return false;
                    break;
                case "cx_fog":
                    if (fogLine.Length > 0) { why = $"a second cx_fog, `{line}`: a stage names one fog colour"; return false; }
                    if (tok.Length != 5 || !Identifier(tok[2]) || !Coord(tok[3], out s.FogX) || !Coord(tok[4], out s.FogY))
                    { why = $"cx_fog takes an image name and a texel's <x> <y>: `{line}`"; return false; }
                    s.FogRef = tok[2];
                    fogLine = line;
                    break;
                case "cx_overlay":
                    if (overLine.Length > 0) { why = $"a second cx_overlay, `{line}`: a frame has one overlay"; return false; }
                    if (tok.Length != 3 || !Identifier(tok[2])) { why = $"cx_overlay takes one image name: `{line}`"; return false; }
                    s.OverlayRef = tok[2];
                    overLine = line;
                    break;
                default:
                    why = $"unknown declaration `{tok[1]}` on `{line}`";
                    return false;
            }
        }
        if (images.Count > MaxImages) { why = $"{images.Count} images declared, past the budget of {MaxImages}"; return false; }
        if (passes.Count > MaxPasses) { why = $"{passes.Count} passes declared, past the budget of {MaxPasses}"; return false; }
        foreach (Pass p in passes)
        {
            p.Image = images.FindIndex(im => string.Equals(im.Name, p.ImageRef, StringComparison.Ordinal));
            if (p.Image < 0)
            { why = $"pass {p.Ref} is dispatched over `{p.ImageRef}`, which no cx_image declares"; return false; }
        }
        if (fogLine.Length > 0 && !FogTexel(s, images, fogLine, out why)) return false;
        if (overLine.Length > 0)
        {
            s.OverlayImage = images.FindIndex(im => string.Equals(im.Name, s.OverlayRef, StringComparison.Ordinal));
            if (s.OverlayImage < 0) { why = $"`{overLine}` names `{s.OverlayRef}`, which no cx_image declares"; return false; }
            if (images[s.OverlayImage].Shape == Shape.Tex3d)
            { why = $"`{overLine}` names `{s.OverlayRef}`, a volume: the frame takes a 2d image"; return false; }
        }
        s.Images = images.ToArray();
        s.Passes = passes.ToArray();
        return true;
    }

    private static bool ParseImage(string[] tok, List<Img> images, out string why)
    {
        why = "";
        if (tok.Length < 5)
        { why = $"cx_image needs a name, a shape and a format: `{string.Join(' ', tok)}`"; return false; }
        var im = new Img { Name = tok[2] };
        if (!Identifier(im.Name))
        { why = $"`{im.Name}` is not a GLSL identifier of at most {MaxNameChars} characters"; return false; }
        foreach (Img other in images)
            if (string.Equals(other.Name, im.Name, StringComparison.Ordinal))
            { why = $"two images are named `{im.Name}`"; return false; }
        // The shape's extent is separate DECIMAL tokens, never `tex3d:128x128x128`: a GLSL preprocessor
        // tokenises a #pragma line, and NVIDIA's refuses `128x128` as an integer with a type suffix -- the
        // declaration would cost the stage its own compile.
        int at;
        switch (tok[3])
        {
            case "tex2d":
                if (tok.Length < 7 || !Side(tok[4], Max2dSide, out im.W) || !Side(tok[5], Max2dSide, out im.H))
                { why = $"`tex2d` takes <W> <H>, each a decimal 1..{Max2dSide}: `{string.Join(' ', tok)}`"; return false; }
                im.Shape = Shape.Tex2d; im.D = 1; at = 6;
                break;
            case "tex3d":
                if (tok.Length < 8 || !Side(tok[4], Max3dSide, out im.W) || !Side(tok[5], Max3dSide, out im.H)
                    || !Side(tok[6], Max3dSide, out im.D))
                { why = $"`tex3d` takes <W> <H> <D>, each a decimal 1..{Max3dSide}: `{string.Join(' ', tok)}`"; return false; }
                im.Shape = Shape.Tex3d; at = 7;
                break;
            case "view":
                if (!Side(tok[4], MaxViewDiv, out im.Div))
                { why = $"`view` takes <N>, a decimal 1..{MaxViewDiv}: `{string.Join(' ', tok)}`"; return false; }
                im.Shape = Shape.View; im.D = 1; im.Clamp = true; at = 5;
                break;
            default:
                why = $"`{tok[3]}` is not tex2d, tex3d or view";
                return false;
        }
        if (at >= tok.Length || !ParseFormat(tok[at], out im.Format, out im.Bpp))
        { why = $"`{(at < tok.Length ? tok[at] : "")}` is not one of rgba8, rgba16f, rgba32f, rg16f, rg8, r16f, r32f, r8, r11f_g11f_b10f"; return false; }
        for (int i = at + 1; i < tok.Length; i++)
            switch (tok[i])
            {
                case "history": im.History = true; break;
                case "mips": im.Mips = true; break;
                case "clamp": im.Clamp = true; break;
                default: why = $"`{tok[i]}` is not one of history, mips, clamp"; return false;
            }
        images.Add(im);
        return true;
    }

    /// <summary>The fog texel has to be one a readback can name: inside a <c>tex2d</c> image the stage declares.
    /// A view-sized image changes extent under it and a volume has no single texel to mean "the colour".</summary>
    private static bool FogTexel(Stage s, List<Img> images, string line, out string why)
    {
        why = "";
        s.FogImage = images.FindIndex(im => string.Equals(im.Name, s.FogRef, StringComparison.Ordinal));
        if (s.FogImage < 0) { why = $"`{line}` names `{s.FogRef}`, which no cx_image declares"; return false; }
        Img fog = images[s.FogImage];
        if (fog.Shape != Shape.Tex2d) { why = $"`{line}` names `{fog.Name}`, which is not a tex2d image"; return false; }
        if (s.FogX >= fog.W || s.FogY >= fog.H)
        { why = $"`{line}` is outside `{fog.Name}`, which is {fog.W}x{fog.H}"; return false; }
        return true;
    }

    private static bool ParsePass(string[] tok, List<Pass> passes, out string why)
    {
        why = "";
        if (tok.Length != 5)
        { why = $"cx_pass needs once|frame, a resref and an image: `{string.Join(' ', tok)}`"; return false; }
        bool once = tok[2] == "once";
        if (!once && tok[2] != "frame") { why = $"`{tok[2]}` is neither once nor frame"; return false; }
        if (tok[3].Length == 0 || tok[3].Length > ShaderResource.ResRefBytes)
        { why = $"`{tok[3]}` is not a resref of at most {ShaderResource.ResRefBytes} characters"; return false; }
        if (!Identifier(tok[4])) { why = $"`{tok[4]}` is not an image name"; return false; }
        passes.Add(new Pass { Once = once, Ref = tok[3], ImageRef = tok[4] });
        return true;
    }

    /// <summary>A declaration token the GLSL preprocessor will not choke on: an identifier or a decimal
    /// integer, and nothing else. NVIDIA tokenises the <c>#pragma</c> line, so `tex3d:128x128x128` is a
    /// compile error in the stage itself, which is why the grammar spends a token per number.</summary>
    private static bool PlainToken(string s)
    {
        if (s.Length == 0) return false;
        bool digits = char.IsAsciiDigit(s[0]);
        if (!digits && !char.IsAsciiLetter(s[0]) && s[0] != '_') return false;
        foreach (char c in s)
        {
            if (digits ? !char.IsAsciiDigit(c) : !char.IsAsciiLetterOrDigit(c) && c != '_') return false;
        }
        return true;
    }

    /// <summary>A decimal extent inside its budget -- <see cref="PlainToken"/>'s integer arm, bounded.</summary>
    private static bool Side(string s, int max, out int v)
    {
        v = 0;
        return PlainToken(s) && char.IsAsciiDigit(s[0]) && int.TryParse(s, out v) && v >= 1 && v <= max;
    }

    /// <summary>A texel coordinate: <see cref="PlainToken"/>'s integer arm, 0 up to the largest 2d side.</summary>
    private static bool Coord(string s, out int v)
    {
        v = 0;
        return PlainToken(s) && char.IsAsciiDigit(s[0]) && int.TryParse(s, out v) && v >= 0 && v < Max2dSide;
    }

    /// <summary>A GLSL identifier short enough to carry a <c>_prev</c> half, and not in the reserved
    /// <c>gl_</c> space -- <see cref="PlainToken"/>'s identifier arm, bounded.</summary>
    private static bool Identifier(string s)
        => PlainToken(s) && !char.IsAsciiDigit(s[0]) && s.Length <= MaxNameChars
        && !s.StartsWith("gl_", StringComparison.Ordinal);

    private static bool ParseFormat(string s, out uint fmt, out int bpp)
    {
        (fmt, bpp) = s switch
        {
            "rgba8" => (EngineTokens.RGBA8, 4),
            "rgba16f" => (EngineTokens.RGBA16F, 8),
            "rgba32f" => (EngineTokens.RGBA32F, 16),
            "rg16f" => (EngineTokens.RG16F, 4),
            "rg8" => (EngineTokens.RG8, 2),
            "r16f" => (EngineTokens.R16F, 2),
            "r32f" => (EngineTokens.R32F, 4),
            "r8" => (EngineTokens.R8, 1),
            "r11f_g11f_b10f" => (EngineTokens.R11F_G11F_B10F, 4),
            _ => (0u, 0),
        };
        return bpp != 0;
    }
}
