using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;

using static CX.Alloy.AlloyRenderer;

namespace CX.Alloy;

/// <summary>PROGRAM BUILD: link the Alloy stages -- fullbright, depth, lit and water -- from the embedded
/// text or a content override, and rebuild from the embedded text whenever an override fails a
/// <see cref="ShaderContract"/> predicate. Cold: once per program per context. An ADR 0014 module, not a
/// partial: it owns the program handles and uniform locations, reaching renderer state through static use.</summary>
internal static unsafe class Programs
{
    /// <summary>The program names and uniform locations every submitting partial reads. 4.6-side objects,
    /// touched only inside the bracket; the Ensure* below mint them, <see cref="AlloyRenderer.ContextLost"/>
    /// drops them. Constant initialisers only (see <see cref="AlloyRenderer.Counters"/>).</summary>
    internal static uint _prog46, _progLit46, _progDepth, _progDepthKey;
    /// <summary>The feature-aware depth stage: the lit program's OWN compiled vertex text (override or
    /// embedded, so its displacement is the colour pass's character for character) over an alpha-testing
    /// fragment stage. Takes the cutout class and the wind-displaced batches the plain prepass skips.</summary>
    internal static uint _progDepthFeat;
    internal static int _depthFeatVp = -1, _depthFeatBase = -1, _depthFeatTex = -1, _depthFeatCut = -1;
    internal static FeatLoc _depthFeatFeat;
    internal static bool _depthFeatFailed;
    /// <summary>The vertex source the lit / fullbright programs actually compiled with (an override wins).</summary>
    internal static string? _litVsSrc;
    /// <summary>The fragment text the lit program compiled with: what every variant of it is spliced from (the
    /// A2C mask, and whatever a diagnostic builds).</summary>
    internal static string? _litFsSrc;
    /// <summary>The spliced programs were built against a resource chain that is gone. Every program carrying
    /// a <c>// $VANILLA</c> region (lit, hosted lit, water, grass) compiles ONCE from whatever the chain held
    /// then, so without this a session keeps the first server's display hooks running on the next one. Set on
    /// the module-unload edge (outside the bracket), acted on by <see cref="DropSpliced"/> inside it.</summary>
    internal static bool _spliceStale;

    internal static int _depthVp = -1, _depthBase = -1;
    /// <summary>The dissolving batches' depth program (<see cref="DepthKeyVs"/>). Zero = it could not build,
    /// and those batches are excluded from the prepass instead -- slower, never wrong.</summary>
    internal static int _depthKeyVp = -1, _depthKeyBase = -1;
    internal static FeatLoc _depthKeyFeat;
    /// <summary>The depth prepass could not build (or glColorMask is missing) -- latched, and the colour pass
    /// simply shades every fragment, which is slower, never wrong.</summary>
    internal static bool _depthFailed;
    /// <summary>Every Alloy vertex stage that ran carries <see cref="ClipExpr"/> under an invariant
    /// qualifier. False = a content override rewrote one, and the prepass cannot re-test its depth.</summary>
    internal static bool _vsDepthSafe = true;
    internal static int _locVp = -1, _locBase = -1, _locTex = -1;
    /// <summary><c>uAlphaCut</c> on each colour program -- the engine's <c>fAlphaDiscardValue</c>, set per
    /// batch from the batch's class.</summary>
    internal static int _locCut = -1;
    /// <summary><c>uAlphaMip</c> on the fullbright and feature-depth programs (the lit ones carry it in <see cref="LitLoc"/>).</summary>
    internal static int _locAlphaMip = -1, _depthFeatMip = -1;

    internal static LitLoc _lit, _litH, _litL, _litD;
    /// <summary>The lit program WITH the OIT append block (OitPass.WithLists), or 0. Bound only for lit batches inside a
    /// linked-list bracket; every other lit draw keeps early-Z through <see cref="_progLit46"/>.</summary>
    internal static uint _progLitList;
    /// <summary>ADR 0029: the DEFERRED twin -- the same final text with <c>CX_DEFERRED</c> and
    /// <c>CX_GBUF_OUT</c> in, so the point-light walk and the film statics are compiled out and the G-buffer is
    /// written instead. 0 = no twin, which is what stands the deferred pass down. Bound for the opaque and
    /// cutout lit batches of the main view; every other lit draw keeps the forward loop.</summary>
    internal static uint _progLitDef;
    /// <summary>One variant of the lit program spliced from its final text, and its locations.</summary>
    private sealed class LitVariantState
    {
        public uint Prog;
        public LitLoc Loc;
    }
    /// <summary>The decal layer's writer (<see cref="DecalLayer"/>): the final text with <c>CX_DECAL_WRITE</c>,
    /// which stops at the albedo and writes it into the layer.</summary>
    private static LitVariantState _decal = new();
    /// <summary>The graded cutouts' fringe program: the final text with <c>CX_LIGHT_LITE</c>, the slim forward
    /// walk. No OIT block and no side effect, so it keeps early-Z under its <c>GL_LESS</c> test.</summary>
    private static LitVariantState _fringe = new();
    /// <summary>The decal writer, or 0 -- no writer, and decals stay in the blended pass.</summary>
    internal static uint ProgDecal => _decal.Prog;
    /// <summary>Its uniform locations, for the issue loop's program table.</summary>
    internal static ref LitLoc DecalLoc => ref _decal.Loc;
    /// <summary>Drop the decal writer's and the fringe program's names: deleted when <paramref name="delete"/> (a
    /// module change), forgotten when the context took it.</summary>
    internal static void ForgetVariants(bool delete)
    {
        if (delete && _decal.Prog != 0) Gl.DeleteProgram(_decal.Prog);
        if (delete && _fringe.Prog != 0) Gl.DeleteProgram(_fringe.Prog);
        _decal = new LitVariantState();
        _fringe = new LitVariantState();
    }
    /// <summary>The fringe program, or 0 -- no fringe, and every graded cutout draws whole at the 0.2 cut.</summary>
    internal static uint ProgFringe => _fringe.Prog;
    /// <summary>Its uniform locations, for the issue loop's program table.</summary>
    internal static ref LitLoc FringeLoc => ref _fringe.Loc;
    /// <summary>The lit program with <c>CX_A2C_MASK</c> -- the coverage layer's own sample mask
    /// (<c>alloy.a2cmask</c>). Built on the layer's first ask; 0 until then or after a refusal.</summary>
    internal static uint _progLitMask;
    internal static LitLoc _litM;
    internal static int _litMaskSamples = -1;
    private static bool _maskFailed;

    /// <summary>The HOSTED lit program's own A2C mask variant, in one owner: its program, its locations, its
    /// sample-count uniform, the text it is spliced from and the latched refusal. A hosted body whose coverage
    /// comes off its own map still gets the layer's per-primitive sample pattern through it.</summary>
    private sealed class HostMaskState
    {
        public uint Prog;
        public LitLoc Loc;
        public int Samples = -1;
        public bool Failed;
        /// <summary>The hosted program's final fragment text -- what the variant is spliced from.</summary>
        public string? Fs;
    }
    private static HostMaskState _hostMask = new();

    /// <summary>The hosted A2C mask program, or 0.</summary>
    internal static uint ProgLitHostMask => _hostMask.Prog;
    /// <summary>Its <c>uA2cSamples</c> location.</summary>
    internal static int HostMaskSamples => _hostMask.Samples;
    /// <summary>Its uniform locations, for the issue loop's program table.</summary>
    internal static ref LitLoc HostMaskLoc => ref _hostMask.Loc;
    /// <summary>The lit program WITH the hosted content bodies, or 0 (hosting off, no hosted feature, or
    /// its build failed -- hosted materials then draw the plain lit path through <see cref="_progLit46"/>).</summary>
    internal static uint _progLitHost;
    internal static int _locViz = -1;
    internal static int _locOit = -1, _locOitDim = -1, _locOitGob = -1;
    /// <summary>The fullbright stage's constant colour. White for every unlit draw but a fog-tinted
    /// sky part -- a GLSL uniform defaults to ZERO, so an unset one draws BLACK and every caller sets it.</summary>
    internal static int _locTint = -1;
    internal static int _locSkyFog;
    internal static bool _progFailed, _litFailed;
    internal static uint _progWater;
    internal static bool _waterFailed;
    internal static int _watVp, _watBase, _watTex, _watCut, _watViz, _watSunTex,
                       _watCubeTex, _watCubeCount, _watCubeDynTex, _watSunDynTex,
                       _watCapture, _watSplash, _watFbColor, _watFbDepth, _watNoise, _watEnvMap, _watEnvOk, _watNearFar,
                       _watView, _watProj;
    internal static FeatLoc _watFeat;
    internal static FeatLoc _locFeat;

    /// <summary>Build the water variant, vertex and fragment only -- there is no tessellated sea. A failure
    /// disarms Alloy water for the session, and nothing walks behind it, so water is simply not drawn.</summary>
    internal static bool EnsureWaterProgram()
    {
        if (_progWater != 0) return true;
        if (_waterFailed) return false;
        if (!FrameSnapshot.On) { _waterFailed = true; return false; }
        string waterVs = ShaderStages.Source(ShdWaterVs), waterFs = ShaderStages.Source(ShdWaterFs);
        uint prog = GlProgram.Graphics(ShdWaterVs, waterVs, ShdWaterFs, waterFs, _log,
                                       "AlloyRenderer water program", out string wvs, out string wfs);
        // An override that declares the wrong per-draw record reads every draw out of a neighbour's slot --
        // the #120 failure, and silent. #132 adds the colour space to the same guard, for the same reason.
        if (prog != 0 && !(ShaderContract.RecordSafe(wvs) && ShaderContract.RecordSafe(wfs)
                           // #242: ...and reaches it through the slot map. An override that predates the
                           // persistent records declares exactly the right record and indexes it by DRAW.
                           && ShaderContract.SlotIndexSafe(wvs)
                           && ShaderContract.LinearSafe(wfs)
                           // #208: ...and the samplers this program binds units to. A dropped uNoise leaves
                           // the wave field reading unit 0, which is the material diffuse.
                           && ShaderContract.SlotSafe(wfs, "uTex", "uFbColor", "uFbDepth", "uNoise", "uEnvMap")))
        {
            Gl.DeleteProgram(prog);
            _log?.Info("AlloyRenderer: water override declares the wrong per-draw record or still " +
                       "encodes to display space -- rebuilding every water stage from the embedded default");
            prog = GlProgram.Graphics(FrameSnapshot.Vs, waterFs, _log, "AlloyRenderer water program (embedded)", content: false);
        }
        if (prog == 0)
        {
            _waterFailed = true;
            _log?.Info("AlloyRenderer: water program unavailable -- water is NOT DRAWN this session (#285: no engine walk behind it)");
            return false;
        }
        _progWater = prog;
        _watVp = GlProgram.Loc(prog, "uVP"); _watBase = GlProgram.Loc(prog, "uDrawBase");
        _watTex = GlProgram.Loc(prog, "uTex"); _watCut = GlProgram.Loc(prog, "uAlphaCut");
        _watViz = GlProgram.Loc(prog, "uViz");
        _watSunTex = GlProgram.Loc(prog, "uSunShadow"); _watSunDynTex = GlProgram.Loc(prog, "uSunDynShadow");
        _watCubeTex = GlProgram.Loc(prog, "uLocalShadow"); _watCubeDynTex = GlProgram.Loc(prog, "uLocalDynShadow");
        _watCubeCount = GlProgram.Loc(prog, "uLocalCubes");
        _watCapture = GlProgram.Loc(prog, "uCapture"); _watSplash = GlProgram.Loc(prog, "uSplash");
        _watFbColor = GlProgram.Loc(prog, "uFbColor"); _watFbDepth = GlProgram.Loc(prog, "uFbDepth");
        _watNoise = GlProgram.Loc(prog, "uNoise");
        _watEnvMap = GlProgram.Loc(prog, "uEnvMap"); _watEnvOk = GlProgram.Loc(prog, "uEnvOk");
        _watNearFar = GlProgram.Loc(prog, "uNearFar");
        _watView = GlProgram.Loc(prog, "uView"); _watProj = GlProgram.Loc(prog, "uProj");
        _watFeat = FeatLocs(prog);
        // Sampler units, bound once: a sampler left at its default points at unit 0, and two sampler types
        // on one image unit is INVALID_OPERATION -- the draw is dropped, silently as far as the frame knows.
        int prevProg; Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &prevProg);
        Gl.UseProgram(prog);
        Gl.Uniform1i(_watTex, 0);
        Gl.Uniform1i(_watSunTex, (int)SunUnit);
        Gl.Uniform1i(_watSunDynTex, (int)SunDynUnit);
        Gl.Uniform1i(_watCubeTex, (int)CubeUnit);
        Gl.Uniform1i(_watCubeDynTex, (int)CubeDynUnit);
        Gl.Uniform1i(_watFbColor, (int)FrameSnapshot.ColorUnit);
        Gl.Uniform1i(_watFbDepth, (int)FrameSnapshot.DepthUnit);
        Gl.Uniform1i(_watNoise, (int)FrameSnapshot.NoiseUnit);
        Gl.Uniform1i(_watEnvMap, (int)FrameSnapshot.EnvUnit);
        // linked mid-frame: left current, the frame's next validation runs against its empty shadow units (131222)
        Gl.UseProgram((uint)prevProg);
        _log?.Info("AlloyRenderer: water program ready, vanilla fswater (/water enabled 0 disables) -- " + FrameSnapshot.Describe());
        return true;
    }

    /// <summary>Throw away a program built from a stale content override and rebuild it from the embedded
    /// stages. Preferred over standing the pass down: the embedded source agrees with the record the CPU
    /// stages, so the frame is CORRECT and merely un-restyled, and the override's author gets a line naming
    /// the file rather than a black world to bisect. Cold: once per program per session.</summary>
    internal static uint RebuildEmbedded(uint stale, string vs, string fs, string what, ref string vsSrc)
    {
        Gl.DeleteProgram(stale);
        _log?.Info($"AlloyRenderer: a content .shd for the {what} program disagrees with the pass -- either " +
                   $"it declares a per-draw record that is not the {DrawFloats * sizeof(float)}-byte one being " +
                   "staged (#92 override vs #120's grown CxDraw), or it predates #126 and carries no " +
                   "uAlphaCut/per-draw alpha (which would render every cutout and blended surface solid). " +
                   "Ignoring it and building the embedded stages, which agree. Regenerate the override from " +
                   "the current embedded stage to get it back.");
        vsSrc = vs;
        return GlProgram.Graphics(vs, fs, _log, $"AlloyRenderer {what} program (embedded)", content: false);
    }

    /// <summary>Delete every program that splices content and clear its failure latch, so each rebuilds
    /// on its next use from the module now loaded. Inside the bracket, which is why the edge only sets the
    /// flag. The fullbright and depth programs splice nothing and stay.</summary>
    internal static void DropSpliced()
    {
        _spliceStale = false;
        if (_progLit46 != 0) Gl.DeleteProgram(_progLit46);
        if (_progLitHost != 0) Gl.DeleteProgram(_progLitHost);
        if (_progLitList != 0) Gl.DeleteProgram(_progLitList);
        if (_progLitDef != 0) Gl.DeleteProgram(_progLitDef);
        ForgetVariants(delete: true);
        if (_progLitMask != 0) Gl.DeleteProgram(_progLitMask);
        if (_hostMask.Prog != 0) Gl.DeleteProgram(_hostMask.Prog);
        _hostMask = new HostMaskState();
        _progLitMask = 0; _maskFailed = false;
        if (_progWater != 0) Gl.DeleteProgram(_progWater);
        _progLit46 = 0; _progLitHost = 0; _progLitList = 0; _progLitDef = 0; _progWater = 0; OitPass.ListsArmed = false;
        Ablation.Forget(delete: true);
        _litFailed = false; _waterFailed = false;
        GrassField.DropProgram();
        PostStack.DropSpliced();
        SkyStage.DropPrograms();
        _log?.Info("AlloyRenderer: module changed -- the spliced programs (lit, hosted lit, water, grass, post, sky) are dropped and rebuild from this module's resource chain");
    }

    internal static bool EnsureProgram()
    {
        if (_spliceStale) DropSpliced();
        if (_prog46 == 0 && !_progFailed)
        {
            // ponytail: one cheap stage keeps the lists block (no early-Z for unlit parts); twin it as the lit one if it ever shows
            string fbFs = OitPass.WithLists(ShaderStages.Source(ShdFullbrightFs));
            uint prog = GlProgram.Graphics(ShdFullbrightVs, ShaderStages.Source(ShdFullbrightVs),
                                           ShdFullbrightFs, fbFs,
                                           _log, "AlloyRenderer fullbright program",
                                           out string vsSrc, out string fsSrc);
            if (prog != 0 && !(ShaderContract.RecordSafe(vsSrc) && ShaderContract.RecordSafe(fsSrc) && ShaderContract.CutoutSafe(vsSrc, fsSrc)
                               && ShaderContract.SlotIndexSafe(vsSrc)   // #242
                               && ShaderContract.SkinSafe(vsSrc, _skinning) && ShaderContract.LinearSafe(fsSrc)))
            {
                prog = RebuildEmbedded(prog, OwnedVs, fbFs, "fullbright", ref vsSrc);
            }
            _vsDepthSafe &= ShaderContract.DepthSafe(vsSrc);
            // #263: programs build on first use, i.e. inside the first frame the verdict already froze on.
            // A silent latch here is a permanently bare world, so it is a hard failure with a reason.
            if (prog == 0) { _progFailed = true; AlloyGate.Fail("an Alloy program did not build"); return false; }
            _prog46 = prog;
            _locVp = GlProgram.Loc(prog, "uVP"); _locBase = GlProgram.Loc(prog, "uDrawBase");
            _locTex = GlProgram.Loc(prog, "uTex"); _locViz = GlProgram.Loc(prog, "uViz"); _locOit = GlProgram.Loc(prog, "uOit");
            _locOitDim = GlProgram.Loc(prog, "uOitDim"); _locOitGob = GlProgram.Loc(prog, "uOitGob");
            Gl.UseProgram(prog); Gl.Uniform1i(GlProgram.Loc(prog, "uOitDepth"), (int)TextureSlots.OitDepth);
            _locCut = GlProgram.Loc(prog, "uAlphaCut"); _locAlphaMip = GlProgram.Loc(prog, "uAlphaMip");
            _locTint = GlProgram.Loc(prog, "uTint");
            _locSkyFog = GlProgram.Loc(prog, "uSkyFog");
            _locFeat = FeatLocs(prog);
            _log?.Info("AlloyRenderer: fullbright program ready (4.6 context)");
        }
        if (_prog46 == 0) return false;
        if (_lighting && _progLit46 == 0 && !_litFailed) EnsureLitProgram();
        return true;
    }

    /// <summary>The feature-aware depth stage (cutout + wind-displaced batches). Built from the vertex text
    /// that actually compiled for the lit (else fullbright) program, so a content override's displacement
    /// is the one the prepass writes. Not fatal: without it those batches stay out, as before.</summary>
    internal static bool EnsureDepthFeatProgram()
    {
        if (_progDepthFeat != 0) return true;
        if (_depthFeatFailed || !_vsDepthSafe) return false;
        // the fullbright vs writes no vDraw, and DepthFeatFs indexes the record buffer by it: lit only
        string? vs = _litVsSrc;
        if (vs == null) return false;   // no lit program compiled yet: try again next frame
        // #153: lit batches merge across textures under bindless, so one bound uTex would alpha-test every
        // draw in the batch against the first draw's leaves; the stage samples the per-draw handle instead
        string fs = _bindless ? BindlessFs(DepthFeatFs) : DepthFeatFs;
        uint prog = GlProgram.Graphics(vs, fs, _log, "AlloyRenderer feature depth prepass program");
        if (prog == 0)
        {
            _depthFeatFailed = true;
            _log?.Info("AlloyRenderer: feature depth prepass program unavailable -- cutout and displaced batches stay out of the prepass");
            return false;
        }
        _progDepthFeat = prog;
        _depthFeatVp = GlProgram.Loc(prog, "uVP"); _depthFeatBase = GlProgram.Loc(prog, "uDrawBase");
        _depthFeatTex = GlProgram.Loc(prog, "uTex"); _depthFeatCut = GlProgram.Loc(prog, "uAlphaCut");
        _depthFeatMip = GlProgram.Loc(prog, "uAlphaMip");
        _depthFeatFeat = FeatLocs(prog);
        _log?.Info("AlloyRenderer: feature depth prepass ready -- cutout and wind-displaced batches write depth (/alloy prepassfeat 0 disables)");
        return true;
    }

    /// <summary>Build the depth-only program. A failure disarms the prepass for the session and the colour
    /// pass goes back to shading every fragment, which is slower, never wrong.</summary>
    internal static bool EnsureDepthProgram()
    {
        if (_progDepth != 0) return true;
        if (_depthFailed) return false;
        // A content vertex override (#92) replaces the stage wholesale, so what SelfCheck asserts about the
        // embedded constants is no longer what runs -- and a rewritten association loses colour-pass fragments
        // to LEQUAL as speckled holes with nothing in the log. Checked on the source that ACTUALLY compiled,
        // not on whether an override exists at all: every real deployment ships these shaders, and refusing
        // the pass on their mere presence would mean it never runs anywhere it matters.
        if (!_vsDepthSafe)
        {
            _depthFailed = true;
            _log?.Info("AlloyRenderer: a content vertex .shd does not carry `" + ClipExpr +
                       "` under `invariant gl_Position;` -- no depth prepass (update the override to match " +
                       "the embedded stage, or the colour pass would lose fragments to LEQUAL)");
            return false;
        }
        if (Gl.ColorMask == null)
        {
            _depthFailed = true;
            _log?.Info("AlloyRenderer: glColorMask unresolved -- no depth prepass");
            return false;
        }
        uint prog = GlProgram.Graphics(DepthVs, DepthFs, _log, "AlloyRenderer depth prepass program");
        if (prog == 0)
        {
            _depthFailed = true;
            _log?.Info("AlloyRenderer: depth prepass program unavailable -- the colour pass shades every fragment");
            return false;
        }
        _progDepth = prog;
        _depthVp = GlProgram.Loc(prog, "uVP"); _depthBase = GlProgram.Loc(prog, "uDrawBase");
        // #124's second stage. A failure here is NOT fatal to the prepass -- dissolving batches simply go
        // back to being left out of it, which is what they did before the fix.
        uint keyProg = GlProgram.Graphics(DepthKeyVs, DepthKeyFs, _log,
                                          "AlloyRenderer keyhole depth prepass program");
        if (keyProg != 0)
        {
            _progDepthKey = keyProg;
            _depthKeyVp = GlProgram.Loc(keyProg, "uVP");
            _depthKeyBase = GlProgram.Loc(keyProg, "uDrawBase");
            _depthKeyFeat = FeatLocs(keyProg);
        }
        _log?.Info("AlloyRenderer: depth prepass ready (/alloy prepass 0 disables)" +
                   (keyProg != 0 ? "" : " -- WITHOUT the keyhole stage, so dissolving batches stay out of it"));
        return true;
    }

    /// <summary>What the owned stages ask of the driver beyond the GL 4.6 floor: three vertex-stage SSBO
    /// blocks (draw, draw index, bones; floor 0) and the fragment sampler budget the lit stage declares. The
    /// bind sites spend units 0..15; past them the lit stage claims by <c>layout(binding)</c> up to the
    /// weather track map, so the honest need is that plus one.</summary>
    internal static readonly int LitUnitsNeed = (int)Weather.TrackUnit + 1;
    /// <summary>...and what the SOFT SUN filter asks on top: two more, for the raw cascade views, the highest
    /// units Alloy binds. A driver short of these keeps the lit program -- <see cref="WithoutSunSoft"/> takes
    /// the define out so the two samplers are never declared -- and the sun falls back to its fixed cross.</summary>
    internal static readonly int SunSoftUnitsNeed = (int)TextureSlots.SunShadowDynRaw + 1;
    private const int VsSsboNeed = 3;

    /// <summary>The define that compiles the sun's blocker search and disc into a stage. It lives in
    /// <c>cx_ownlit_head.glsl</c>, so a dumped stage and any override built from one carry it.</summary>
    internal const string SunSoftDefine = "#define CX_SUN_SOFT 1";

    /// <summary><paramref name="fs"/> with <see cref="SunSoftDefine"/> commented out: the soft sun path
    /// compiles to <c>CxSunTaps</c> alone and the two raw cascade samplers are not declared, which is what a
    /// driver short of <see cref="SunSoftUnitsNeed"/> units needs in order to link the lit program at all.</summary>
    internal static string WithoutSunSoft(string fs)
        => fs.Replace(SunSoftDefine, "// CX-49: the soft sun filter is OFF -- too few fragment texture units for its raw cascade views");

    /// <summary>...and what the SKY-VISIBILITY term asks: one more, the highest unit Alloy binds. A driver
    /// short of it keeps the lit program -- <see cref="WithoutSkyView"/> takes the define out so the map is
    /// never declared -- and the area ambient fills an alley as vanilla does.</summary>
    internal static readonly int SkyViewUnitsNeed = (int)TextureSlots.SkyMap + 1;

    /// <summary>The define that compiles the sky-visibility term into a stage. It lives in
    /// <c>cx_ownlit_head.glsl</c>, so a dumped stage and any override built from one carry it.</summary>
    internal const string SkyViewDefine = "#define CX_SKY_VIEW 1";

    /// <summary><paramref name="fs"/> with <see cref="SkyViewDefine"/> commented out: <c>CxSkyView</c>
    /// compiles to a constant 1 and <c>uSkyMap</c> is not declared, which is what a driver short of
    /// <see cref="SkyViewUnitsNeed"/> units needs in order to link the lit program at all.</summary>
    internal static string WithoutSkyView(string fs)
        => fs.Replace(SkyViewDefine, "// the sky-visibility term is OFF -- too few fragment texture units for its map");

    /// <summary><paramref name="fs"/> as this driver can link it: unchanged where the map's unit seats, and
    /// <see cref="WithoutSkyView"/>'d where it does not. Asked per program (the lit stage and both grass
    /// stages read the map), so the order the three are built in cannot decide the answer.</summary>
    internal static string WithSkyView(string fs)
    {
        int units = 0;
        Gl.GetIntegerv(Gl.MAX_TEXTURE_IMAGE_UNITS, &units);
        if (units >= SkyViewUnitsNeed) return fs;
        SkyView.NoTextureUnit();
        return WithoutSkyView(fs);
    }

    /// <summary>Build the lit variant. A failure disarms lit ownership for the session (and the next
    /// enrollment hands every lit part back to the engine) -- never a silent fallback to fullbright, which
    /// would look worse than vanilla.</summary>
    private static void EnsureLitProgram()
    {
        // #153: decide bindless BEFORE assembling text -- the transform must apply to the same source the
        // program is built from, and to nothing when the context cannot honour it.
        if (!_bindlessProbed)
        {
            _bindlessProbed = true;
            // Config bucket (ADR 0013): a capability gate that decides the lit stage's BUFFER LAYOUT, so it
            // is settled before the program is assembled and cannot be a command.
            // alloy.bindless=auto: on wherever the extension exists EXCEPT zink, whose bindless bookkeeping faults on
            // a worker thread once handles exist at all (five Zink crashes on 2026-09-05, with deletes, without
            // deletes, without non-resident calls); alloy.bindless=on forces it there, alloy.bindless=off vetoes it anywhere
            int want = Renderer.Bindless.Boot;
            bool zinkVeto = want < 0 && GlContextService.Zink;
            _bindless = want != 0 && !zinkVeto && HasBindless();
            if (zinkVeto) _log?.Info("AlloyRenderer: zink renderer -- bindless texturing stood down (alloy.bindless=on in cx.cfg forces it)");
            _log?.Info(_bindless
                ? "AlloyRenderer: ARB_bindless_texture armed -- lit batches merge across textures (alloy.bindless=off in cx.cfg reverts)"
                : "AlloyRenderer: bindless texturing unavailable or vetoed -- per-batch texture binds");
        }
        if (!_limitsProbed)
        {
            _limitsProbed = true;
            // The two limits every owned vertex stage exceeds the GL 4.6 floor on (0 vertex SSBO blocks,
            // 8 bindings): asked once, so a driver that cannot link says so by name rather than by log spam.
            int vsBlocks = 0, bindings = 0, units = 0;
            Gl.GetIntegerv(Gl.MAX_VERTEX_SHADER_STORAGE_BLOCKS, &vsBlocks);
            Gl.GetIntegerv(Gl.MAX_SHADER_STORAGE_BUFFER_BINDINGS, &bindings);
            Gl.GetIntegerv(Gl.MAX_TEXTURE_IMAGE_UNITS, &units);
            int needBind = (int)SsboSlots.Budget;
            bool short_ = vsBlocks < VsSsboNeed || bindings < needBind || units < LitUnitsNeed;
            _log?.Info($"AlloyRenderer: driver limits -- vertex SSBO blocks {vsBlocks} (needs {VsSsboNeed}, GL 4.6 floor 0), " +
                       $"SSBO bindings {bindings} (needs {needBind}, floor 8), fragment texture units {units} " +
                       $"(needs {LitUnitsNeed}, {SunSoftUnitsNeed} for the sun's blocker search, floor 16)" +
                       (short_ ? " -- BELOW ALLOY'S NEED: expect link failures on this driver" : ""));
        }
        string litFs = ShaderStages.Source(ShdLitFs);
        if (_bindless) litFs = BindlessFs(litFs);
        // CX-49: the sun's two raw cascade views are the highest units Alloy binds, and a layout(binding)
        // past MAX_TEXTURE_IMAGE_UNITS is a LINK error -- which would cost the whole lit program, not just
        // the search. So the define comes out on a driver that cannot seat them, and the sun keeps its fixed
        // cross. A content override of the stage carries its own copy of the define and is not rewritten
        // here, exactly as the bindless rewrite below is not.
        int litUnits = 0;
        Gl.GetIntegerv(Gl.MAX_TEXTURE_IMAGE_UNITS, &litUnits);
        if (litUnits < SunSoftUnitsNeed)
        {
            litFs = WithoutSunSoft(litFs);
            _log?.Info($"AlloyRenderer: the sun's blocker search is OFF -- {litUnits} fragment texture units, "
                     + $"needs {SunSoftUnitsNeed}; sun shadows take the fixed four-tap cross (point-light cubes are unaffected)");
        }
        // ...and the sky-visibility map is the one unit above those two, by the same rule.
        litFs = WithSkyView(litFs);
        string litVs = ShaderStages.Source(ShdLitVs);
        bool hosted = VanillaShader.HostArmed && System.Linq.Enumerable.Any(ShaderFeatures.FsNames, n => n.Length > 0);
        GlProgram.Texts(ShdLitVs, litVs, ShdLitFs, litFs, null, "", out string pv, out string pf);
        var guess = Variants(pf);
        using var prefetched = GlProgram.Prefetch([(pv, pf, false), (pv, guess.List, false), (pv, guess.Def, false),
                                                   (pv, guess.Decal, false), (pv, guess.Fringe, false), (pv, hosted ? pf : null, true)], _log);
        // #350: from the hosted regions' STUBS -- the program every lit batch draws with carries no content
        // body; the second build below carries them for the batches that need them.
        uint prog = GlProgram.Graphics(ShdLitVs, litVs, ShdLitFs, litFs,
                                       _log, "AlloyRenderer lit program",
                                       out string vsSrc, out string fsSrc, host: false);
        string finalFs = fsSrc;
        if (prog != 0 && !(ShaderContract.RecordSafe(vsSrc) && ShaderContract.RecordSafe(fsSrc) && ShaderContract.CutoutSafe(vsSrc, fsSrc)
                           && ShaderContract.SlotIndexSafe(vsSrc)   // #242
                           && ShaderContract.SkinSafe(vsSrc, _skinning) && ShaderContract.LinearSafe(fsSrc)
                           && ShaderContract.ChartSafe(vsSrc, fsSrc)   // #674
                           // #208: ...and the sampler declarations the unit table binds against, which only
                           // the compiled source can answer for. Bound-sampler check, so bindless skips it.
                           && (_bindless || ShaderContract.LitSlotSafe(fsSrc))
                           && (!_bindless || fsSrc.Contains("CxTexH"))))
        {
            prog = RebuildEmbedded(prog, OwnedLitVs, litFs, "lit", ref vsSrc);
            finalFs = litFs;
        }
        // a content override without the handle block compiled fine but still samples bound units: the
        // C# side must keep binding for it, so the whole bindless path stands down for the session
        if (prog != 0 && _bindless && !fsSrc.Contains("CxTexH") && !litFs.Contains("CxTexH"))
        {
            _bindless = false;
            _log?.Info("AlloyRenderer: lit stage carries no handle block -- bindless stands down, binds resume");
        }
        _vsDepthSafe &= ShaderContract.DepthSafe(vsSrc);
        if (prog != 0) _litVsSrc = vsSrc;
        if (prog == 0)
        {
            _litFailed = true;
            _log?.Info("AlloyRenderer: the lit program is unavailable -- every lit part is skipped, which is an "
                     + "empty world; nothing draws behind the refusal");
            return;
        }
        _progLit46 = prog;
        _lit = LitLocs(prog);
        _litFsSrc = finalFs;
        // The OIT-lists twin: the same text that just compiled, plus the append block, bound only inside a mode-2
        // bracket. Verified on the text because a content override may carry no block at all.
        var (listFs, defFs, decalFs, fringeFs) = Variants(finalFs);
        uint pl = listFs != null ? GlProgram.Graphics(vsSrc, listFs, _log, "AlloyRenderer lit program (OIT lists)", host: false) : 0;
        _progLitList = pl;
        if (pl != 0) _litL = LitLocs(pl);
        else _log?.Info("AlloyRenderer: lit OIT-lists twin unavailable -- /alloy oit 2 resolves the weighted way");
        OitPass.ListsArmed = pl != 0;
        // CX-188 (ADR 0029): the DEFERRED twin, from the same final text. A content override of cx_ownlit_fs
        // (or of a library inside it) that never calls CxGbufWrite cannot be one -- there is no half-deferred
        // frame, so the whole pass stands down with the override named.
        _progLitDef = 0;
        if (defFs == null)
            _log?.Info($"AlloyRenderer: the lit fragment stage in use never calls CxGbufWrite -- a content " +
                       $"{ShdLitFs}.shd (or a library override inside it) predates CX-188, so DEFERRED POINT LIGHTS " +
                       "ARE OFF for the session and every light draws through the forward loop. Regenerate the " +
                       "override from the current embedded stage to get them back.");
        else
        {
            uint pd = GlProgram.Graphics(vsSrc, defFs, _log, "AlloyRenderer lit program (deferred twin)", host: false);
            _progLitDef = pd;
            if (pd != 0) _litD = LitLocs(pd);
            else _log?.Info("AlloyRenderer: lit deferred twin did not build -- deferred point lights are off for the session");
        }
        // CX-273: the decal layer's writer, from the same final text. Optional: without it decals blend as before.
        ForgetVariants(delete: true);
        _decal.Prog = GlProgram.Graphics(vsSrc, decalFs, _log, "AlloyRenderer lit program (decal layer)", host: false);
        if (_decal.Prog != 0) _decal.Loc = LitLocs(_decal.Prog);
        else _log?.Info("AlloyRenderer: the decal layer's writer did not build -- ground decals stay in the blended pass");
        // CX-294: the graded cutouts' fringe, from the same final text. Optional: without it they draw whole.
        _fringe.Prog = GlProgram.Graphics(vsSrc, fringeFs, _log, "AlloyRenderer lit program (fringe)", host: false);
        if (_fringe.Prog != 0) _fringe.Loc = LitLocs(_fringe.Prog);
        else _log?.Info("AlloyRenderer: the fringe program did not build -- graded cutouts draw whole, hard-edged at the 0.2 cut");
        // #350: the SECOND lit program, the one with the content bodies in. Same text, hosted regions spliced
        // instead of stubbed; same contracts, because it draws the same records. Optional twice over: alloy.hostfs=0,
        // no hosted feature, or a failed build all leave it 0 and hosted materials on the plain path.
        _progLitHost = 0;
        if (hosted)
        {
            uint ph = GlProgram.Graphics(ShdLitVs, litVs, ShdLitFs, litFs,
                                         _log, "AlloyRenderer hosted lit program", out _, out string hfs, host: true);
            if (ph != 0 && !(ShaderContract.RecordSafe(hfs) && ShaderContract.LinearSafe(hfs)
                             && (!_bindless || hfs.Contains("CxTexH"))))
            {
                Gl.DeleteProgram(ph); ph = 0;
                _log?.Info("AlloyRenderer: hosted lit program failed its contracts -- hosted materials draw the plain lit path");
            }
            if (ph == 0) _log?.Info("AlloyRenderer: hosted lit program unavailable -- hosted materials draw the plain lit path");
            else { _progLitHost = ph; _litH = LitLocs(ph); _hostMask.Fs = hfs; }
        }
        _log?.Info("AlloyRenderer: lit program ready (4.6 context) -- forward except the point lights, which the "
                 + "deferred twin and DeferredLights add");
    }

    /// <summary>The lit variants' fragment texts from the lit stage's final text: the OIT-lists twin, the deferred
    /// twin, the decal layer's writer and the fringe, null where that text cannot carry the variant.</summary>
    private static (string? List, string? Def, string Decal, string Fringe) Variants(string fs)
    {
        string list = OitPass.WithLists(fs), def = WithDeferred(fs);
        return (list.Contains("atomicAdd(uOitCount") ? list : null, def.Contains("CxGbufWrite(g)") ? def : null,
                ShaderStages.WithDefine(fs, "CX_DECAL_WRITE 1"), ShaderStages.WithDefine(fs, "CX_LIGHT_LITE 1"));
    }

    /// <summary>Build the hosted A2C mask program from the HOSTED program's own final text. False = no
    /// hosted program, or the variant refused (latched, logged once): a hosted coverage batch then draws
    /// through the plain hosted program and takes hardware alpha-to-coverage.</summary>
    internal static bool EnsureHostMaskProgram()
    {
        HostMaskState h = _hostMask;
        if (h.Prog != 0) return true;
        if (h.Failed || h.Fs == null || _litVsSrc == null) return false;
        h.Prog = GlProgram.Graphics(_litVsSrc, ShaderStages.WithDefine(h.Fs, "CX_A2C_MASK 1"),
                                    _log, "AlloyRenderer hosted lit program (A2C mask)", host: false);
        if (h.Prog == 0)
        {
            h.Failed = true;
            _log?.Info("AlloyRenderer: the hosted A2C mask variant did not build -- a hosted coverage batch "
                     + "draws through the plain hosted program and takes hardware coverage");
            return false;
        }
        h.Loc = LitLocs(h.Prog);
        h.Samples = GlProgram.Loc(h.Prog, "uA2cSamples");
        return true;
    }

    /// <summary>Build <see cref="_progLitMask"/> from the lit program's own final text. False = no lit
    /// program yet, or the variant refused (latched, logged once): the layer falls back to hardware coverage.</summary>
    internal static bool EnsureMaskProgram()
    {
        if (_progLitMask != 0) return true;
        if (_maskFailed || _litFsSrc == null || _litVsSrc == null) return false;
        _progLitMask = GlProgram.Graphics(_litVsSrc, ShaderStages.WithDefine(_litFsSrc, "CX_A2C_MASK 1"), _log,
                                          "AlloyRenderer lit program (A2C mask)", host: false);
        if (_progLitMask == 0)
        {
            _maskFailed = true;
            _log?.Info("AlloyRenderer: the A2C mask variant did not build -- alloy.a2cmask stands down, hardware coverage draws the layer");
            return false;
        }
        _litM = LitLocs(_progLitMask);
        _litMaskSamples = GlProgram.Loc(_progLitMask, "uA2cSamples");
        return true;
    }

    /// <summary>The defines that compile a lit fragment stage into the deferred twin, spliced after its
    /// <c>#version</c> line: the walk's call sites out (<c>CX_DEFERRED</c>), the G-buffer outputs in
    /// (<c>CX_GBUF_OUT</c>). <c>check-shaders.py</c> splices the identical pair.</summary>
    internal static string WithDeferred(string fs)
    {
        int v = fs.IndexOf("#version", StringComparison.Ordinal);
        if (v < 0) return fs;
        return fs.Insert(fs.IndexOf('\n', v) + 1, "#define CX_DEFERRED 1\n#define CX_GBUF_OUT 1\n");
    }

    /// <summary>Resolve one lit program's locations and bind its fixed sampler units. Called once per program
    /// at build, so twice where a hosted twin is built.</summary>
    internal static LitLoc LitLocs(uint prog)
    {
        var L = new LitLoc();
        L.Vp = GlProgram.Loc(prog, "uVP"); L.Base = GlProgram.Loc(prog, "uDrawBase");
        L.Tex = GlProgram.Loc(prog, "uTex");
        L.Cut = GlProgram.Loc(prog, "uAlphaCut"); L.AlphaMip = GlProgram.Loc(prog, "uAlphaMip");
        L.Viz = GlProgram.Loc(prog, "uViz"); L.Oit = GlProgram.Loc(prog, "uOit"); L.OitDim = GlProgram.Loc(prog, "uOitDim"); L.OitGob = GlProgram.Loc(prog, "uOitGob"); L.LightRecOn = GlProgram.Loc(prog, "uLightRecOn");
        L.Decal = GlProgram.Loc(prog, "uDecal"); L.DecalZ = GlProgram.Loc(prog, "uDecalZ"); L.Solid = GlProgram.Loc(prog, "uSolid");
        L.SunTex = GlProgram.Loc(prog, "uSunShadow"); L.SunDynTex = GlProgram.Loc(prog, "uSunDynShadow");
        L.CubeTex = GlProgram.Loc(prog, "uLocalShadow"); L.CubeDynTex = GlProgram.Loc(prog, "uLocalDynShadow");
        L.CubeCount = GlProgram.Loc(prog, "uLocalCubes");
        L.Probe = GlProgram.Loc(prog, "uProbe");
        L.Capture = GlProgram.Loc(prog, "uCapture"); L.MirrorZ = GlProgram.Loc(prog, "uMirrorZ");
        L.LmPages = GlProgram.Loc(prog, "uLmPages"); L.LmMovers = GlProgram.Loc(prog, "uLmMovers");
        L.Ssao = GlProgram.Loc(prog, "uSsao"); L.SsaoOn = GlProgram.Loc(prog, "uSsaoOn");
        L.SsaoHalf = GlProgram.Loc(prog, "uSsaoHalf");
        L.LightOcc = GlProgram.Loc(prog, "uLightOcc");
        L.Material = GlProgram.Loc(prog, "uMaterial");
        L.AoDirect = GlProgram.Loc(prog, "uAoDirect"); L.AoCutout = GlProgram.Loc(prog, "uAoCutout");
        L.SkyVp = GlProgram.Loc(prog, "uSkyVp"); L.SkyTerm = GlProgram.Loc(prog, "uSkyTerm");
        L.Weather = GlProgram.Loc(prog, "uWeather"); L.WeatherLook = GlProgram.Loc(prog, "uWeatherLook");
        L.CoverVp = GlProgram.Loc(prog, "uCoverVp"); L.CoverLayer = GlProgram.Loc(prog, "uCoverLayer");
        L.SnowLook = GlProgram.Loc(prog, "uSnowLook"); L.SnowLight = GlProgram.Loc(prog, "uSnowLight"); L.CoverEsm = GlProgram.Loc(prog, "uCoverEsmP"); L.SnowPom = GlProgram.Loc(prog, "uSnowPom");
        L.WxMapXf = GlProgram.Loc(prog, "uWxMapXf"); L.WxMapP = GlProgram.Loc(prog, "uWxMapP");
        L.WxEdge = GlProgram.Loc(prog, "uWxEdge"); L.WxRun = GlProgram.Loc(prog, "uWxRun"); L.WxCache = GlProgram.Loc(prog, "uWxCache");
        L.Feat = FeatLocs(prog);
        // Bind the two shadow samplers to their units ONCE, here, and not only on the frames a map is fresh.
        // A sampler uniform defaults to unit 0 -- the same unit uTex lives on -- and GL forbids two samplers
        // of different types pointing at one image unit: the draw raises INVALID_OPERATION and is dropped.
        // The fresh path alone is not enough, because a session that never renders a map (an interior start,
        // shadow.cubes=0) never reaches it, and then EVERY Alloy lit batch silently vanishes.
        Gl.UseProgram(prog);
        Gl.Uniform1i(L.SunTex, (int)SunUnit);
        Gl.Uniform1i(L.SunDynTex, (int)SunDynUnit);
        Gl.Uniform1i(L.CubeTex, (int)CubeUnit);
        Gl.Uniform1i(L.CubeDynTex, (int)CubeDynUnit);
        Gl.Uniform1i(L.Probe, (int)EnvProbe.ProbeUnit);
        Gl.Uniform1i(GlProgram.Loc(prog, "uOitDepth"), (int)TextureSlots.OitDepth);
        Gl.Uniform1i(GlProgram.Loc(prog, "uCoverEsm"), (int)TextureSlots.CoverEsm);
        Gl.Uniform1i(L.Ssao, (int)TextureSlots.Ssao);
        Gl.Uniform1i(GlProgram.Loc(prog, "uSceneDepth"), (int)TextureSlots.SsaoDepth);
        // #120's five map samplers, for the same reason: left at their default they would all point at unit
        // 0 and read the diffuse as a normal map on every part that declares one.
        for (int i = 0; i < MapSlots; i++)
            Gl.Uniform1i(GlProgram.Loc(prog, TextureSlots.MapSamplers[i]), (int)PbrUnit0 + i);
        return L;
    }

    /// <summary>The <see cref="FeatDecl"/> uniform locations of one program, in the order
    /// <see cref="UploadFeatureTerms"/> writes them.</summary>
    internal static FeatLoc FeatLocs(uint prog) => WithScriptables(new FeatLoc
    {
        Time = GlProgram.Loc(prog, "uTimeOfDay"),
        Wind = GlProgram.Loc(prog, "uAreaWind"),
        Flags = GlProgram.Loc(prog, "uAreaFlags"),
        Cam = GlProgram.Loc(prog, "uCamPos"),
        CamFocus = GlProgram.Loc(prog, "uCamFocus"),
        Player = GlProgram.Loc(prog, "uPlayerPos"),
        Fog = GlProgram.Loc(prog, "uFogParams"),
        FogColor = GlProgram.Loc(prog, "uFogColor"),
        KhDia = GlProgram.Loc(prog, "uKeyholeDia"),
        KhCam = GlProgram.Loc(prog, "uKeyholeCam"),
        Pixel = GlProgram.Loc(prog, "uPixel"),
        LodBias = GlProgram.Loc(prog, "uLodBias"),
        Now = GlProgram.Loc(prog, "uNow"),
        Eye = GlProgram.Loc(prog, "uEyePos"),
        FogSky = GlProgram.Loc(prog, "uFogSky"),
        FogSkyColor = GlProgram.Loc(prog, "uFogSkyColor"),
        FogSun = GlProgram.Loc(prog, "uFogSun"),
        FogSunColor = GlProgram.Loc(prog, "uFogSunColor"),
        FogOrigin = GlProgram.Loc(prog, "uFogOrigin"),
        // #346: the ENGINE's own uniform names, because the stage compiles the engine's own file. Renaming
        // any of these means editing spliced content, which is the drift the bridge exists to end.
        TgtShapeL = GlProgram.Loc(prog, "targetShape"),
        TgtSelfL = GlProgram.Loc(prog, "targetShapeOriginOnSelf"),
        TgtObjL = GlProgram.Loc(prog, "targetObjectType"),
        TgtSizeXL = GlProgram.Loc(prog, "targetShapeSizeX"),
        TgtSizeYL = GlProgram.Loc(prog, "targetShapeSizeY"),
        TgtRangeL = GlProgram.Loc(prog, "targetShapeSpellRange"),
        TgtColL = GlProgram.Loc(prog, "targetShapeColour"),
        TgtWorldL = GlProgram.Loc(prog, "targetWorldCoords"),
        // #347: the shared vanilla environment. Same rule as above -- the engine's names, because the code
        // that reads them is the engine's.
        ProjL = GlProgram.Loc(prog, "m_proj"),
    }, prog);

    /// <summary>The scriptable block's locations, filled after the struct so the fixed buffers can be
    /// written. Cold -- once per program, at build.</summary>
    private static FeatLoc WithScriptables(FeatLoc loc, uint prog)
    {
        for (int i = 0; i < ScriptableSlots; i++)
        {
            loc.SInt[i] = GlProgram.Loc(prog, $"scriptableInt{i + 1}");
            loc.SFloat[i] = GlProgram.Loc(prog, $"scriptableFloat{i + 1}");
            loc.SVec[i] = GlProgram.Loc(prog, $"scriptableVec{i + 1}");
            if (loc.SInt[i] >= 0) loc.SIntLive |= 1 << i;
            if (loc.SFloat[i] >= 0) loc.SFloatLive |= 1 << i;
            if (loc.SVec[i] >= 0) loc.SVecLive |= 1 << i;
        }
        return loc;
    }
}
