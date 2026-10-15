using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// The content's own full-screen post chain -- the engine's <c>fsfbpostpr</c> program, framebuffer effect 2 of
/// <c>Camera::DoFrameBufferEffects</c> @0x4a8eb0 -- hosted as one stage of the display-space stack, which is
/// what puts a content repo's grade, noir, sky rays and status overlay back. A <c>// $VANILLA inc_postpr</c>
/// region takes the resource chain's copy at build; the stub between the markers is a pass-through.
/// </summary>
internal static unsafe class FbPostStage
{
    /// <summary>The stage's resref (an override point), same rules as every stack stage.</summary>
    internal const string ShdFs = "cx_fbpost_fs";
    /// <summary><c>Camera::GetIsFBEffectEnabled(2)</c> @0x5a6530: bit 2 of <c>m_nFramebufferEffectsEnabled</c>.
    /// <c>ConfigureFBEffectPostProcessing</c> @0x66ad40 sets it from a LONG read at CClientOptions+0x118, i.e.
    /// option mask != 0 OR <c>run-shader-even-with-no-effects</c> (+0x11c) -- so it is the engine's own
    /// answer to "does the post program run this session".</summary>
    internal const uint EffectPost = 1u << 2;
    /// <summary>The option bits stripped from the hosted text -- ALL of them. DOF 0x02 and dynamic contrast
    /// 0x04 have vertex halves this fullscreen triangle does not run; gamma 0x10, toon 0x20, sharpen 0x01 and
    /// vibrance 0x08 are stages of Alloy's own. With the mask at 0 not one stock <c>inc_postpr_*</c> include
    /// compiles inside an Alloy program: what is left is whatever the CONTENT put in its <c>inc_postpr</c>.</summary>
    internal const int MaskStripped = 0x3f;   // not-an-offset: every inc_postpr.shd option bit
    /// <summary><c>GetGlobalShaderParami(7)</c>: the client's shader quality option, the preamble's
    /// <c>SHADER_QUALITY_MODE</c> (the same read <see cref="AlloyLights"/> makes for the subsurface term);
    /// the content's <c>inc_config</c> branches on it.</summary>
    internal const uint QualityParam = 7;   // not-an-offset: engine shader-param index

    private static ExtenderLog? _log;
    private static Matrix4x4 _view = Matrix4x4.Identity;
    private static float _near = 0.1f, _far = 100f;
    /// <summary>Everything the GL context owns, in one object: context loss replaces it, so no name or
    /// uniform location from the dead context survives as a field nobody remembered to zero. The build
    /// tokens ride here because they describe the program that holds those locations.</summary>
    private sealed class FbPostState
    {
        public uint Prog, Sampler;
        public AlloyRenderer.FeatLoc Feat;
        public int LocView, LocPixel, LocClip, LocFrame, LocHour, LocMonth, LocDay, LocTransition, LocPlayer,
                   LocFbColor, LocFbDepth;
        public int BuildMask = -1, BuildQuality = -1;
    }

    private static FbPostState _gl = new();

    /// <summary>What the text is built against: expanded into the source as <c>$POSTMASK$</c> and <c>$SQM$</c>
    /// by <see cref="ShaderResource.Tokens"/>. -1 = not built yet.</summary>
    internal static int BuildMask { get => _gl.BuildMask; private set => _gl.BuildMask = value; }
    internal static int BuildQuality { get => _gl.BuildQuality; private set => _gl.BuildQuality = value; }

    internal static void Attach(ExtenderLog log) => _log = log;
    /// <summary>Context loss: every GL name is gone.</summary>
    internal static void Forget() { _gl = new FbPostState(); }
    /// <summary>The program is dropped (module change, option change); the sampler object outlives it.</summary>
    internal static void DropProgram() { _gl.Prog = 0; BuildMask = -1; BuildQuality = -1; }

    /// <summary>The bits the hosted text carries, of the engine's mask.</summary>
    internal static int HostedMask(int mask) => mask & ~MaskStripped;
    /// <summary>Does the built program disagree with the options now? The stack drops and rebuilds it.</summary>
    internal static bool Stale(int mask, int quality) => BuildMask >= 0 && (HostedMask(mask) != BuildMask || quality != BuildQuality);
    /// <summary>Pin the tokens ahead of a build.</summary>
    internal static void Arm(int mask, int quality) { BuildMask = HostedMask(mask); BuildQuality = quality; }

    /// <summary>From the pass, beside the toon stage's projection: the camera the content's sun and lens
    /// maths project through (<c>m_view</c>, and the near/far the depth decode needs).</summary>
    internal static void SetCamera(Matrix4x4* view, Matrix4x4* proj)
    {
        _view = *view;
        AlloyLights.TryPerspective(*proj, out _, out _, out _near, out _far);
    }

    /// <summary>From <see cref="PostStack.Run"/>, ahead of the stage draw: every engine name the chain reads,
    /// from Alloy's values, onto this program. <c>sceneCurrentFrame</c> is Alloy's presented-frame counter, not
    /// the engine's scene counter -- the chain only ever animates on it. Hot. Also binds a LINEAR sampler on
    /// the source unit (the stack's textures are NEAREST), which Run unbinds after the draw.</summary>
    internal static void Prepare(uint src, uint prog, int* viewport)
    {
        var g = _gl;
        if (prog != g.Prog)
        {
            g.Prog = prog;
            g.Feat = Programs.FeatLocs(prog);
            g.LocView = GlProgram.Loc(prog, "m_view"); g.LocPixel = GlProgram.Loc(prog, "pixelParams");
            g.LocClip = GlProgram.Loc(prog, "clipParams"); g.LocFrame = GlProgram.Loc(prog, "sceneCurrentFrame");
            g.LocHour = GlProgram.Loc(prog, "moduleHour"); g.LocMonth = GlProgram.Loc(prog, "moduleMonth");
            g.LocDay = GlProgram.Loc(prog, "moduleDay"); g.LocTransition = GlProgram.Loc(prog, "moduleTimeIntoTransition");
            g.LocPlayer = GlProgram.Loc(prog, "uPlayerPos");
            g.LocFbColor = GlProgram.Loc(prog, "texFBColor"); g.LocFbDepth = GlProgram.Loc(prog, "texFBDepth");
        }
        Gl.UseProgram(prog);
        Gl.Uniform1i(g.LocFbColor, (int)TextureSlots.PostHdr);
        Gl.Uniform1i(g.LocFbDepth, (int)TextureSlots.PostBloom);
        AlloyRenderer.UploadVanillaEnv(in g.Feat);
        fixed (Matrix4x4* v = &_view) Gl.UniformMatrix4fv(g.LocView, 1, 0, (float*)v);
        float w = viewport[2] > 0 ? viewport[2] : 1f, h = viewport[3] > 0 ? viewport[3] : 1f;
        Gl.Uniform3f(g.LocPixel, 1f / w, 1f / h, w / h);
        // clipParams = (near, far, 1 - near/far): inc_common.shd's own triple
        Gl.Uniform3f(g.LocClip, _near, _far, 1f - _near / MathF.Max(_far, 1e-6f));
        Gl.Uniform1i(g.LocFrame, AlloyRenderer.FrameSeq);
        Vector3 p = AlloyRenderer.PlayerPos;
        Gl.Uniform3f(g.LocPlayer, p.X, p.Y, p.Z);
        Gl.Uniform1i(g.LocHour, AlloyRenderer.ModuleHour);
        Gl.Uniform1i(g.LocMonth, AlloyRenderer.ModuleMonth);
        Gl.Uniform1i(g.LocDay, AlloyRenderer.ModuleDay);
        Gl.Uniform1f(g.LocTransition, AlloyRenderer.ModuleTimeIntoTransition);
        if (g.Sampler == 0) g.Sampler = Gl.LinearClampSampler();
        if (g.Sampler != 0) Gl.BindSampler(TextureSlots.PostHdr, g.Sampler);
    }

    /// <summary>The stage text: the environment the chain reads (inc_common/inc_uniforms as the fragment half
    /// of a post program sees them, under the engine's names), then the chain itself off the resource chain.
    /// <c>inc_postpr</c> defines <c>POSTPROCESSING</c>/<c>LIGHTING</c>/<c>FOG</c> for itself, so none are set
    /// here; <c>inc_lighting</c>/<c>inc_fog</c> are the engine's forward pipeline and are skipped.</summary>
    internal static string FbPostFs => ShaderSource.Get("cx_fbpost_f.glsl");

    /// <summary>Off-device: the mask stripping is what the doc says, the gate is effect 2's bit, and the text
    /// neither claims the chain's own defines nor loses its stub.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        // CX-93: the point of the whole half. Whatever the chain answers with, no option bit reaches it, so
        // every stock inc_postpr_* include preprocesses away and only the content's own passes are compiled.
        check.Expect(HostedMask(0x3f) == 0,
          "every one of inc_postpr.shd's six option bits is stripped before the hosted text: not one stock post "
          + "include compiles here");
        check.Expect(!FbPostFs.Contains("#define LIGHTING") && !FbPostFs.Contains("#define FOG") && !FbPostFs.Contains("#define POSTPROCESSING 1"),
          "inc_postpr's own defines are left to it");
        check.Expect(FbPostFs.Contains("// $VANILLA inc_postpr\n") && FbPostFs.Contains("vec4 FS_PostProcess() {"), "the region and its stub");
        check.Expect(FbPostFs.Contains("$POSTMASK$") && FbPostFs.Contains("$SQM$"), "the option tokens are in the text");
        check.Expect(!ShaderResource.Tokens("$POSTMASK$ $SQM$").Contains('$'), "the tokens expand");
        check.Expect(!Stale(0, 0), "an unbuilt stage is never stale");
        Arm(MaskStripped, 2);
        check.Expect(BuildMask == 0 && !Stale(MaskStripped, 2) && !Stale(0, 2) && Stale(MaskStripped, 1),
          "stale = the quality moved; an option bit no longer rebuilds, because none of them reaches the text");
        BuildMask = -1; BuildQuality = -1;
        return check.Ok;
    }
}
