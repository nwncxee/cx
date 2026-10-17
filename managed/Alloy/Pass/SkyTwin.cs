using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>A content sky stage's ALLOY-NATIVE TWIN: where the chain answers for <c>cx_&lt;stage&gt;</c>, that
/// file runs in the dome's place under <c>CX_SKY_LINEAR</c> (linear HDR, not clamped display colour) and may
/// declare images and compute passes with <c>#pragma cx_sky_image</c> / <c>#pragma cx_sky_pass</c>. Anything
/// that fails hosts the ORIGINAL stage, out loud. Rules: <c>docs/shader-overrides.md</c>, "Sky twins".</summary>
internal static unsafe partial class SkyTwin
{
    /// <summary>The compute wrapper a declared pass is hosted in (an override point), and the token its
    /// region's resref is written into before the splice -- the same shape as <see cref="SkyStage.ShdFs"/>.</summary>
    internal const string ShdPassCs = "cx_skypass_cs";
    private const string CsToken = "$SKYCS$";

    /// <summary>The define that puts <c>cx_sky_fs</c>'s linear-HDR tail in: no clamp to one, no display
    /// encode, NaN still guarded. A twin is built with it and a stock stage without it.</summary>
    internal const string LinearDefine = "CX_SKY_LINEAR 1";

    /// <summary>The prefix a twin's resref carries. <c>cx_</c> + the stage, which has to fit a resref
    /// (<see cref="ShaderResource.ResRefBytes"/>) -- a longer stage name simply cannot have a twin.</summary>
    internal const string Prefix = "cx_";

    /// <summary>One twin per hosted sky stage, so <see cref="SkyStage"/>'s own program ceiling.</summary>
    private const int MaxTwins = 4;

    /// <summary>The declaration budgets. Past any of them the twin is refused rather than trimmed: a sky that
    /// silently got half the volume it asked for is a look nobody authored.</summary>
    internal const int MaxImages = 12, MaxPasses = 10, Max2dSide = 4096, Max3dSide = 256, MaxViewDiv = 8;
    internal const long MaxImageBytes = 256L << 20;
    /// <summary>A GLSL identifier's room in the declaration -- short enough that <c>&lt;name&gt;_prev</c> is
    /// still one too.</summary>
    internal const int MaxNameChars = 31;

    /// <summary>The texture units a twin's samplers take, from here: 4..9 the material map block the colour
    /// loop rebinds per batch, 10/11 the dynamic shadow arrays <see cref="AlloyRenderer.BindPass"/>
    /// republishes at every program switch, 12 the probe beside it, 13 the AO/snapshot unit its own pass
    /// rebinds -- so each is rebound before its next read. <see cref="Unbind"/> puts them back regardless.</summary>
    internal const uint TexUnit0 = TextureSlots.MapBase + 1, TexUnitCount = 10;
    /// <summary>The image units, from 0 -- the block every Alloy compute pass borrows between two draw passes
    /// and hands back at 0.</summary>
    internal const uint ImgUnitCount = 8;

    /// <summary>What a declared image is sized by. <see cref="View"/> is the MAIN view's render size over a
    /// divisor, so a twin's screen-space buffer follows the render scale and the window.</summary>
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
        public uint Tex, Prev;
        /// <summary>The extent the live texture was made at, so a view resize is a fact and not a guess.</summary>
        public int MadeW, MadeH, MadeD;
    }

    /// <summary>One declared pass: a content compute stage hosted in <see cref="ShdPassCs"/>, dispatched over
    /// one image's extent. <see cref="Once"/> runs when the images are (re)made, the rest once per presented
    /// frame.</summary>
    private sealed class Pass
    {
        public bool Once;
        public string Ref = "";
        /// <summary>The image name the declaration gave, and its index once the names are resolved.</summary>
        public string ImageRef = "";
        public int Image = -1;
        public uint Prog;
        public int Gx = 1, Gy = 1, Gz = 1;
        public SkyEnvLoc Env;
        public Bind[] Binds = Array.Empty<Bind>();
    }

    /// <summary>One image bound into one program, resolved at build: where its uniform is, which unit it was
    /// given, and whether that uniform is a sampler or an image. Per frame this is a bind and nothing else.</summary>
    private struct Bind
    {
        public int Loc;
        public int Image;
        public bool Prev, IsImage;
        public uint Unit;
    }

    private sealed class Twin
    {
        public string Ref = "";
        public Img[] Images = Array.Empty<Img>();
        public Pass[] Passes = Array.Empty<Pass>();
        /// <summary>The dome's own fragment program's bindings; empty until <see cref="Locate"/> runs.</summary>
        public Bind[] Draw = Array.Empty<Bind>();
        public bool Failed;
        public string Reason = "";
        public bool Said;
        public bool Made;
        /// <summary>The main-view extent the view-sized images were made at: a change remakes them.</summary>
        public int MadeW, MadeH;
        public long Bytes;
        /// <summary>The frame its images were last (re)made, and the frame its pass sequence last ran -- so a
        /// frame drawing one twin into several views runs its passes once.</summary>
        public int FreshFrame = -1, RanFrame = -1;
        /// <summary>Passes dispatched this period, for the <c>[alloy]</c> line.</summary>
        public int Runs;
        /// <summary>The <c>cx_sky_fog</c> texel -- the image index (-1: none declared) and its coordinates --
        /// and the last colour read back from it, LINEAR, which the fog converges to.</summary>
        public int FogImage = -1, FogX, FogY;
        public string FogRef = "";
        public GpuReadback FogRead;
        /// <summary>Why the texel cannot be read on this driver, said once; empty while it can.</summary>
        public string FogMiss = "";
        public int FogSeq;
        public Vector3 Fog;
        public bool HaveFog;
    }

    /// <summary>Everything the GL context owns plus the sky's own frame, in one object: context loss replaces
    /// it, so no program name, texture name or failure latch taken against the dead context survives.</summary>
    private sealed class SkyTwinState
    {
        public readonly Twin?[] Twins = new Twin[MaxTwins];
        public int Count;
        /// <summary>The MAIN view's render size and camera, noted once per frame before the sky is staged.
        /// Every pass of a frame -- and a probe face's draw -- reads this one camera.</summary>
        public int ViewW = 1, ViewH = 1;
        public bool HaveView;
        public Matrix4x4 InvVp = Matrix4x4.Identity, CurVp = Matrix4x4.Identity;
        /// <summary>What the stage reads as <c>cxSkyPrevVp</c>, and the camera recorded at the last pass
        /// sequence that it is rolled from -- so a frame's passes and its draws read one pair.</summary>
        public Matrix4x4 PrevVp = Matrix4x4.Identity, PassVp = Matrix4x4.Identity;
        public bool HaveVp;
        public Vector3 Eye;
        /// <summary>The frame the camera pair was last rolled -- ONCE per presented frame, however many twins
        /// or views the frame draws -- and the frame whose history is invalid because the roll before it was
        /// not the frame before (a gap: indoors, <c>effects.sky 0</c>, a loading screen).</summary>
        public int RollSeq = -1, GapSeq = -1;
        /// <summary>Which units the sky's programs have bound since the last <see cref="Unbind"/>, so the
        /// restore touches exactly those and nothing else.</summary>
        public uint TexMask, ImgMask;
    }

    private static SkyTwinState _gl = new();

    /// <summary>Context loss: every GL name is gone, and a failure latch taken against the old context is not
    /// evidence about the new one.</summary>
    internal static void Forget() => _gl = new SkyTwinState();

    /// <summary>Module change (and the <c>effects.skytwin</c> edit): the declarations and the pass text came
    /// off THAT module's chain, so the images and programs have a module lifetime. Deletes the live names,
    /// then forgets -- which is also the one place a refusal latch is cleared.</summary>
    internal static void DropAll()
    {
        var g = _gl;
        for (int i = 0; i < g.Count; i++) Release(g.Twins[i]);
        Forget();
    }

    private static void Release(Twin? t)
    {
        if (t == null) return;
        foreach (Img im in t.Images)
        {
            uint a = im.Tex, b = im.Prev;
            if (a != 0) Gl.DeleteTextures(1, &a);
            if (b != 0) Gl.DeleteTextures(1, &b);
            im.Tex = 0; im.Prev = 0; im.MadeW = 0; im.MadeH = 0; im.MadeD = 0;
        }
        foreach (Pass p in t.Passes) { if (p.Prog != 0) Gl.DeleteProgram(p.Prog); p.Prog = 0; }
        t.FogRead.Release();
        t.Made = false; t.HaveFog = false;
    }

    /// <summary>Is there a live twin at all? The question the frame asks before it walks the staged sky.</summary>
    internal static bool Any
    {
        get
        {
            var g = _gl;
            for (int i = 0; i < g.Count; i++) if (g.Twins[i] is { Failed: false }) return true;
            return false;
        }
    }

    /// <summary>Can a stage have a twin at all? <see cref="Prefix"/> plus the stage name has to fit a resref,
    /// since the twin is a file in a hak like any other.</summary>
    internal static bool Fits(string stage) => Prefix.Length + stage.Length <= ShaderResource.ResRefBytes;

    /// <summary>Is <paramref name="twinRef"/> one of ALLOY's own resrefs -- a stage or a library? A content
    /// stage called <c>sky_fs</c> would name <c>cx_sky_fs</c> as its twin, and hosting a hak's override of
    /// that stage would splice the wrapper into itself. Cold: once per stage per module.</summary>
    internal static bool Reserved(string twinRef)
    {
        foreach ((string r, _) in ShaderStages.All)
            if (string.Equals(r, twinRef, StringComparison.OrdinalIgnoreCase)) return true;
        foreach ((string r, _) in ShaderSource.Libraries())
            if (string.Equals(r, twinRef, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Is this id a twin that may run? A failed one is a twin whose stage is hosted plainly.</summary>
    internal static bool Live(int id) => At(id) is { Failed: false };

    /// <summary>May a draw hosted by this twin go ahead? -1 is "not a twin", which always may; a twin whose
    /// images could not be made this frame may not, and the dome sits the frame out rather than sampling
    /// nothing. Hot: once per sky program per pass.</summary>
    internal static bool Usable(int id) => id < 0 || At(id) is { Failed: false, Made: true };

    private static Twin? At(int id)
    {
        var g = _gl;
        return (uint)id < (uint)g.Count ? g.Twins[id] : null;
    }

    /// <summary>The MAIN view's render size and unjittered camera, noted once per frame before the sky is
    /// staged, so a probe face and every compute pass see the camera the player is looking through.</summary>
    internal static void NoteView(int* viewport, Matrix4x4* vpClean, Vector3 eye)
    {
        var g = _gl;
        g.ViewW = viewport[2] > 0 ? viewport[2] : 1;
        g.ViewH = viewport[3] > 0 ? viewport[3] : 1;
        g.HaveView = true;
        g.Eye = eye;
        g.CurVp = *vpClean;
        if (!Matrix4x4.Invert(*vpClean, out g.InvVp)) g.InvVp = Matrix4x4.Identity;
        // The first frame has no history: a stage differencing the two cameras must see no motion, not a
        // step from the identity.
        if (!g.HaveVp) { g.PrevVp = g.PassVp = *vpClean; g.HaveVp = true; }
    }

    /// <summary>Roll the camera pair, once per presented frame however many twins or views the frame draws.
    /// A roll that does not follow the previous frame's is a GAP -- the sky did not draw for a while -- and
    /// reprojecting against a camera from minutes ago is worse than not reprojecting, so that frame's history
    /// reads invalid and the pair reads as standing still.</summary>
    private static void Roll()
    {
        var g = _gl;
        int f = AlloyRenderer.FrameSeq;
        if (g.RollSeq == f) return;
        bool gap = Gap(g.RollSeq, f);
        g.PrevVp = gap ? g.CurVp : g.PassVp;
        g.PassVp = g.CurVp;
        g.GapSeq = gap ? f : -1;
        g.RollSeq = f;
    }

    /// <summary>Is frame <paramref name="now"/> the one after the frame <paramref name="last"/> the camera
    /// pair was rolled at? Anything else -- including the first ever -- is a gap, and a gap's history is not
    /// one frame old.</summary>
    private static bool Gap(int last, int now) => last < 0 || last != now - 1;

    /// <summary>The sky's own frame as one record, for the program <paramref name="id"/> hosts: -1 is a stage
    /// run plainly, whose <c>cxSky*</c> locations are all -1 anyway. <c>Fresh</c> is THIS twin's -- a remake
    /// of one twin's images says nothing about another's -- or the whole frame's, after a gap.</summary>
    internal static SkyFrame FrameFor(int id) => FrameFor(At(id));

    private static SkyFrame FrameFor(Twin? t)
    {
        var g = _gl;
        int f = AlloyRenderer.FrameSeq;
        bool fresh = g.GapSeq == f || (t != null && t.FreshFrame == f);
        return new SkyFrame(g.InvVp, g.PrevVp, g.Eye, g.ViewW, g.ViewH, f, fresh);
    }

    /// <summary>Zeroed with the rest of the period report's counters.</summary>
    internal static void ResetPeriod()
    {
        var g = _gl;
        for (int i = 0; i < g.Count; i++) if (g.Twins[i] is { } t) t.Runs = 0;
    }

    /// <summary>This twin's clause on the sky's stats line: hosted with what it is running, or hosted plainly
    /// with the reason its twin stood down. "no twin shipped" and "a twin that would not build" are
    /// different facts and read differently.</summary>
    internal static string Note(int id)
    {
        if (At(id) is not { } t) return "";
        if (t.Failed) return $", twin {t.Ref} FELL BACK: {t.Reason}";
        string fog = t.FogImage < 0 ? "no fog colour"
                   : t.FogMiss.Length > 0 ? $"fog colour NOT READ: {t.FogMiss}"
                   : t.HaveFog ? $"fog colour ({t.Fog.X:F3},{t.Fog.Y:F3},{t.Fog.Z:F3}) from {t.FogRef}({t.FogX},{t.FogY})"
                   : $"fog colour from {t.FogRef}({t.FogX},{t.FogY}) not read back yet";
        return $", twin {t.Ref} hosted ({t.Passes.Length} pass(es), {t.Runs} ran this period, "
             + $"{t.Images.Length} image(s), {t.Bytes / (1024.0 * 1024.0):F1} MiB, {fog})";
    }

    /// <summary>The colour twin <paramref name="id"/> says its horizon shows, LINEAR, once one has been read
    /// back -- what the fog converges to in place of the author's flat colour. Staging path: no GL call.</summary>
    internal static bool FogColour(int id, out Vector3 c)
    {
        c = default;
        if (At(id) is not { Failed: false, HaveFog: true } t) return false;
        c = t.Fog;
        return true;
    }

    /// <summary>Latch a failure: said once with the reason, the GL objects released, and the stage hosted
    /// plainly from here on. The latch lives as long as this state object does, so a refusal the frame
    /// discovers is taken ONCE -- a module change, a context loss or an <c>effects.skytwin</c> edit is what
    /// clears it.</summary>
    internal static void Fail(int id, string reason, ExtenderLog? log)
    {
        if (At(id) is not { } t) return;
        t.Failed = true;
        t.Reason = reason;
        Release(t);
        if (t.Said) return;
        t.Said = true;
        log?.Always($"SkyTwin: {t.Ref}.shd is not run -- {reason}. The sky hosts the stage the engine runs "
                  + "instead, so the module's own sky is what draws.");
    }

    /// <summary>Parse the twin's own <c>#pragma cx_sky_*</c> lines and take the declaration budgets. Always
    /// hands back an id (so the stats line can name a refusal), -1 only when the table is full. The
    /// declarations are read from the twin's TOP-LEVEL text alone: an include is a library, not a place to
    /// declare the sky's resources.</summary>
    internal static int Declare(string twinRef, ExtenderLog? log)
    {
        var g = _gl;
        for (int i = 0; i < g.Count; i++)
            if (g.Twins[i] is { } have && string.Equals(have.Ref, twinRef, StringComparison.Ordinal)) return i;
        if (g.Count >= MaxTwins)
        {
            log?.Always($"SkyTwin: more than {MaxTwins} distinct sky twins in one session -- {twinRef}.shd is "
                      + "not run and its stage is hosted plainly");
            return -1;
        }
        int id = g.Count++;
        var t = new Twin { Ref = twinRef };
        g.Twins[id] = t;
        if (Reserved(twinRef))
        { Fail(id, $"{twinRef} is one of Alloy's own resrefs, not a content stage's twin", log); return id; }
        string? text = VanillaShader.ChainText(twinRef);
        if (text == null) { Fail(id, "the resource chain has no such file (or alloy.vanillabridge=0)", log); return id; }
        if (!ParseDecls(t, text, out string why)) { Fail(id, why, log); return id; }
        // The memory budget, as far as the declarations themselves say: every fixed-size image exactly, and
        // the view-sized ones against whatever main view has been seen so far. Remake re-takes it with the
        // measured sizes, but a declaration that cannot fit is a build-time fact and is refused as one.
        if (!Budget(t, g.HaveView ? g.ViewW : 0, g.HaveView ? g.ViewH : 0, out _, out why))
        { Fail(id, why, log); return id; }
        log?.Info($"SkyTwin: {twinRef}.shd declares {t.Images.Length} image(s) and {t.Passes.Length} pass(es)");
        return id;
    }

    /// <summary>The declaration grammar, one line at a time. Every refusal names the line, because a typo in
    /// a pragma is invisible to the GLSL compiler -- the whole point of carrying the declarations there.</summary>
    private static bool ParseDecls(Twin t, string text, out string why)
    {
        var images = new System.Collections.Generic.List<Img>();
        var passes = new System.Collections.Generic.List<Pass>();
        string fogLine = "";
        why = "";
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("#pragma", StringComparison.Ordinal)) continue;
            string[] tok = line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length < 2 || !tok[1].StartsWith("cx_sky", StringComparison.Ordinal)) continue;
            for (int i = 2; i < tok.Length; i++)
                if (!PlainToken(tok[i]))
                {
                    why = $"`{tok[i]}` on `{line}` is neither an identifier nor a decimal integer, and a GLSL "
                        + "preprocessor would refuse the line -- every token of a cx_sky declaration is one or the other";
                    return false;
                }
            if (tok[1] == "cx_sky_image")
            {
                if (!ParseImage(tok, images, out why)) return false;
            }
            else if (tok[1] == "cx_sky_pass")
            {
                if (!ParsePass(tok, passes, out why)) return false;
            }
            else if (tok[1] == "cx_sky_fog")
            {
                if (fogLine.Length > 0) { why = $"a second cx_sky_fog, `{line}`: a sky has one horizon"; return false; }
                if (tok.Length != 5 || !Identifier(tok[2]) || !Coord(tok[3], out t.FogX) || !Coord(tok[4], out t.FogY))
                { why = $"cx_sky_fog takes an image name and a texel's <x> <y>: `{line}`"; return false; }
                t.FogRef = tok[2];
                fogLine = line;
            }
            else { why = $"unknown declaration `{tok[1]}` on `{line}`"; return false; }
        }
        if (images.Count > MaxImages) { why = $"{images.Count} images declared, past the budget of {MaxImages}"; return false; }
        if (passes.Count > MaxPasses) { why = $"{passes.Count} passes declared, past the budget of {MaxPasses}"; return false; }
        foreach (Pass p in passes)
        {
            for (int i = 0; i < images.Count; i++)
                if (string.Equals(images[i].Name, p.ImageRef, StringComparison.Ordinal)) { p.Image = i; break; }
            if (p.Image < 0)
            { why = $"pass {p.Ref} is dispatched over `{p.ImageRef}`, which no cx_sky_image declares"; return false; }
        }
        if (fogLine.Length > 0 && !FogTexel(t, images, fogLine, out why)) return false;
        t.Images = images.ToArray();
        t.Passes = passes.ToArray();
        return true;
    }

    private static bool ParseImage(string[] tok, System.Collections.Generic.List<Img> images, out string why)
    {
        why = "";
        if (tok.Length < 5)
        { why = $"cx_sky_image needs a name, a shape and a format: `{string.Join(' ', tok)}`"; return false; }
        var im = new Img { Name = tok[2] };
        if (!Identifier(im.Name))
        { why = $"`{im.Name}` is not a GLSL identifier of at most {MaxNameChars} characters"; return false; }
        foreach (Img other in images)
            if (string.Equals(other.Name, im.Name, StringComparison.Ordinal))
            { why = $"two images are named `{im.Name}`"; return false; }
        // The shape's extent is separate DECIMAL tokens, never `tex3d:128x128x128`: a GLSL preprocessor
        // tokenises a #pragma line, and NVIDIA's refuses `128x128` as an integer with a type suffix -- the
        // declaration would cost the twin its own compile.
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

    /// <summary>The fog texel has to be one a readback can name: inside a <c>tex2d</c> image the twin declares.
    /// A view-sized image changes extent under it and a volume has no single horizon texel.</summary>
    private static bool FogTexel(Twin t, System.Collections.Generic.List<Img> images, string line, out string why)
    {
        why = "";
        t.FogImage = images.FindIndex(im => string.Equals(im.Name, t.FogRef, StringComparison.Ordinal));
        if (t.FogImage < 0) { why = $"`{line}` names `{t.FogRef}`, which no cx_sky_image declares"; return false; }
        Img fog = images[t.FogImage];
        if (fog.Shape != Shape.Tex2d) { why = $"`{line}` names `{fog.Name}`, which is not a tex2d image"; return false; }
        if (t.FogX >= fog.W || t.FogY >= fog.H)
        { why = $"`{line}` is outside `{fog.Name}`, which is {fog.W}x{fog.H}"; return false; }
        return true;
    }

    private static bool ParsePass(string[] tok, System.Collections.Generic.List<Pass> passes, out string why)
    {
        why = "";
        if (tok.Length != 5)
        { why = $"cx_sky_pass needs once|frame, a resref and an image: `{string.Join(' ', tok)}`"; return false; }
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
    /// compile error in the twin itself, which is why the grammar spends a token per number.</summary>
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

    /// <summary>A GLSL identifier short enough to carry a <c>_prev</c> twin, and not in the reserved
    /// <c>gl_</c> space -- <see cref="PlainToken"/>'s identifier arm, bounded.</summary>
    private static bool Identifier(string s)
        => PlainToken(s) && !char.IsAsciiDigit(s[0]) && s.Length <= MaxNameChars
        && !s.StartsWith("gl_", StringComparison.Ordinal);

    private static bool ParseFormat(string s, out uint fmt, out int bpp)
    {
        (fmt, bpp) = s switch
        {
            "rgba8" => (Gl.RGBA8, 4),
            "rgba16f" => (Gl.RGBA16F, 8),
            "rgba32f" => (Gl.RGBA32F, 16),
            "rg16f" => (Gl.RG16F, 4),
            "rg8" => (Gl.RG8, 2),
            "r16f" => (Gl.R16F, 2),
            "r32f" => (Gl.R32F, 4),
            "r8" => (Gl.R8, 1),
            "r11f_g11f_b10f" => (Gl.R11F_G11F_B10F, 4),
            _ => (0u, 0),
        };
        return bpp != 0;
    }
}
