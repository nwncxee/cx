using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The one framebuffer copy (<see cref="PassId.FbCopy"/>) and the water class (<see cref="PassId.WaterOwned"/>) on
/// Vulkan: the scene colour and depth snapshotted into a pair of their own, then the water batches drawn from them
/// through the same stage GL compiles. A refused snapshot or program stands the class down, which is the hole Vulkan
/// drew before this existed.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The water class's resources on one device, replaced whole with the device, plus this frame's camera
    /// (the stage marches in view space) and the snapshot's readiness.</summary>
    private sealed class VkWaterState
    {
        public VkBackend? Dev;
        public VkProg? Prog;
        /// <summary>The snapshot pair: the scene colour and depth as everything up to the copy left them.</summary>
        public ImageId Col, Dep;
        public int W, H;
        public bool Failed, Logged, NoiseMissed;
        /// <summary>The snapshot holds this frame's scene and the water binds may read it.</summary>
        public bool Ready;
        /// <summary>This frame's env map is on Vulkan, so <c>uEnvOk</c> is honest.</summary>
        public bool EnvLive;
        public SamplerId Snap, Noise;
        public CommandList? Side;
        /// <summary>This frame's view and projection, latched at the frame's head: the Vulkan frame carries no
        /// matrices past its view-projection.</summary>
        public Matrix4x4 View, Proj;
        /// <summary>The stage's own locations, past the ones a plain program's build resolves.</summary>
        public int ViewLoc = -1, ProjLoc = -1, NearFar = -1, EnvOk = -1, CubeCount = -1, Splash = -1;
    }

    private static VkWaterState _vkWater = new();

    /// <summary>The frame's camera for the Vulkan water stage, latched where GL's water pass takes it from its
    /// own arguments.</summary>
    internal static void VkFrameCamera(in Matrix4x4 view, in Matrix4x4 proj)
    {
        _vkWater.View = view;
        _vkWater.Proj = proj;
    }

    /// <summary>A snapshot or engine texture the water stage reads, or the null id when the class is not drawing:
    /// every other stage on Vulkan reads these slots as the stand-in of their type.</summary>
    private static ImageId VkWaterTex(string name)
    {
        VkWaterState w = _vkWater;
        if (!w.Ready) return default;
        return name switch
        {
            "uFbColor" => w.Col,
            "uFbDepth" => w.Dep,
            "uNoise" => VkTextures.Image(FrameSnapshot.NoiseTid),
            _ => w.EnvLive ? VkTextures.Image(AlloyEnvMap.Tid()) : default,
        };
    }

    /// <summary>The sampler a water read takes: the snapshot pair filters linearly, as GL's copy does, and the
    /// engine's noise repeats, as the pass's own sampler object makes it.</summary>
    private static SamplerId VkWaterSampler(string name)
        => name == "uNoise" ? _vkWater.Noise : _vkWater.Snap;

    /// <summary>Does <paramref name="name"/> name one of the water stage's own reads?</summary>
    private static bool VkIsWaterTex(string name)
        => name is "uFbColor" or "uFbDepth" or "uNoise" or "uEnvMap";

    /// <summary>The scene colour and depth into the snapshot pair, recorded between the blended pass and the water
    /// one as <see cref="PassId.FbCopy"/> sits. False (logged) = no snapshot, so the class sits out this frame.</summary>
    private static bool VkWaterCopy(VkBackend vk, CommandList cl, int w, int h, ExtenderLog log)
    {
        VkWaterState s = _vkWater;
        if (s.Dev != vk) _vkWater = s = new VkWaterState { Dev = vk, View = s.View, Proj = s.Proj };
        s.Ready = false;
        if (s.Failed || !FrameSnapshot.Usable || !_waterArmed) return false;
        if (!VkWaterEnsure(vk, s, w, h, log)) return false;
        // the wave field IS the engine's noise texture at four scales, so a stand-in is a different sea rather
        // than a slightly wrong one: the class sits out instead, named
        if (VkTextures.Image(FrameSnapshot.NoiseTid).IsNull)
        {
            if (!s.NoiseMissed)
            {
                s.NoiseMissed = true;
                log.Always("AlloyRenderer: the Vulkan water class sits out -- the engine's noise texture is not "
                         + "mirrored on Vulkan, and without it the wave field has no amplitude");
            }
            return false;
        }
        VkColourState c = _vkc;
        var from = new ImageRegion(0, 0, 0, 0);
        cl.CopyImage(new CopyImageCmd(c.Hdr, from, s.Col, from, w, h, 1));
        cl.CopyImage(new CopyImageCmd(c.Depth, from, s.Dep, from, w, h, 1));
        s.EnvLive = !VkTextures.Image(AlloyEnvMap.Tid()).IsNull;
        s.Ready = true;
        if (!s.Logged)
        {
            s.Logged = true;
            log.Always($"AlloyRenderer: the Vulkan water class is LIVE at {w}x{h} -- the one framebuffer copy, then "
                     + $"screen-space refraction and reflection off it{(s.EnvLive ? "" : " (the scene has no env map)")}");
        }
        return true;
    }

    /// <summary>The snapshot pair, the stage and its two samplers, made once per device and resized with the view.
    /// False = the class stood down (logged), for the session when the stage refused.</summary>
    private static bool VkWaterEnsure(VkBackend vk, VkWaterState s, int w, int h, ExtenderLog log)
    {
        if (w <= 0 || h <= 0) return false;
        if (s.Prog == null)
        {
            s.Side = new CommandList("vulkan water", WaterArenaBytes, log);
            s.Snap = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.None, Wrap.Clamp));
            s.Noise = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.None, Wrap.Repeat));
            s.Prog = VkBuild(vk, VkPrograms.Id.Water, log);
            if (s.Prog == null || s.Snap.IsNull || s.Noise.IsNull)
            {
                s.Failed = true;
                log.Always("AlloyRenderer: the Vulkan water stage did not build (logged above) -- the water class is "
                         + "NOT DRAWN this session, as it was before the stage ported");
                return false;
            }
            VkWaterLocs(s.Prog, s);
        }
        if (s.W == w && s.H == h && !s.Col.IsNull) return true;
        vk.Destroy(s.Col);
        vk.Destroy(s.Dep);
        s.Col = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, w, h, 1, 1, 1, 1,
                                             ImageUse.Sampled | ImageUse.CopyDst, "vk water colour copy"));
        s.Dep = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.D32F, w, h, 1, 1, 1, 1,
                                             ImageUse.Sampled | ImageUse.CopyDst, "vk water depth copy"));
        s.W = w; s.H = h;
        if (s.Col.IsNull || s.Dep.IsNull)
        {
            log.Always($"AlloyRenderer: the Vulkan water snapshot at {w}x{h} was REFUSED (logged above) -- the water "
                     + "class is not drawn at this size");
            return false;
        }
        return true;
    }

    /// <summary>Room for the class's binds and draws; a growth is one allocation and one log line.</summary>
    private const int WaterArenaBytes = 1 << 18;

    /// <summary>The stage's own uniforms, resolved against its block under the shim's name for it.</summary>
    private static void VkWaterLocs(VkProg p, VkWaterState s)
    {
        uint name = VkUniformShim.ProgramBase + (uint)VkPrograms.Id.Water;
        VkUniformShim.Resolve(p.Names, () =>
        {
            s.ViewLoc = GlProgram.Loc(name, "uView");
            s.ProjLoc = GlProgram.Loc(name, "uProj");
            s.NearFar = GlProgram.Loc(name, "uNearFar");
            s.EnvOk = GlProgram.Loc(name, "uEnvOk");
            s.Splash = GlProgram.Loc(name, "uSplash");
            s.CubeCount = GlProgram.Loc(name, "uLocalCubes");
        });
    }

    /// <summary>The water stage's terms into the open capture, as <c>Issue</c>'s water arm sets them: this pass's
    /// own view and projection (the march is in view space), the near/far it linearises depth with, the env switch
    /// and the shared pass block. Inside <see cref="VkBindProgram"/>'s capture.</summary>
    private static void VkWaterTerms(VkProg p, in Matrix4x4 vp, int* viewport, float near, float far)
    {
        VkWaterState s = _vkWater;
        fixed (Matrix4x4* m = &vp) VkUniformShim.Set(p.Vp, new ReadOnlySpan<float>(m, 16));
        fixed (Matrix4x4* m = &s.View) VkUniformShim.Set(s.ViewLoc, new ReadOnlySpan<float>(m, 16));
        fixed (Matrix4x4* m = &s.Proj) VkUniformShim.Set(s.ProjLoc, new ReadOnlySpan<float>(m, 16));
        VkUniformShim.Set(s.NearFar, [near, far]);
        VkUniformShim.Set(p.Viz, _capturing ? 0 : _lightViz);
        VkUniformShim.Set(s.EnvOk, s.EnvLive ? 1 : 0);
        VkUniformShim.Set(s.Splash, Weather.SplashRate(_capturing));
        UploadFeatureTerms(p.F, viewport);
        BindPass(s.CubeCount, viewport, near, far);
    }

    /// <summary>The water class into <paramref name="side"/>, as <c>Issue</c> draws it: depth writes on, the
    /// material's own blend pair, and the stage bound once. False only on a refused bind.</summary>
    private static bool VkWaterIssue(CommandList side, ref VkBound b, int from, int to, in Matrix4x4 vp, int* viewport,
                                     float near, float far, bool indirect)
    {
        VkWaterState s = _vkWater;
        VkColourState c = _vkc;
        if (!s.Ready || s.Prog == null) return true;
        BufferId slots = indirect ? CullPassVk.Slots(CullPassVk.PhaseColour) : c.Slots;
        var push = new VkBatchPush();
        for (int g = from; g < to; g++)
        {
            byte cls = _gCls[g];
            if (cls != (byte)AlloyClass.Water) { c.Skipped += _gLen[g]; continue; }
            bool blend = AlloyClasses.Blends(cls);
            int blendKey = blend ? 1 + Math.Min((int)_gSrc[g], 7) * 8 + Math.Min((int)_gDst[g], 7) : 0;
            bool wash = _gWash[g] != 0;
            push.AlphaCut = AlloyClasses.Discard(cls);
            if (!VkBatch(side, ref b, s.Prog, g, VkTargets.Forward, blendKey, new DepthState(true, true, CompareOp.LessEqual),
                         VkRaster(g, wash), slots, _gTex[g], ref push, vp, viewport, near, far, CullPassVk.PhaseColour, indirect))
                continue;
            Counters._batches++;
            Counters._draws += _gLen[g];
            Counters._drawsByClass[cls] += _gLen[g];
        }
        return true;
    }

    /// <summary>The copy's readers and the class's draw are done: the snapshot is no longer bound.</summary>
    private static void VkWaterDone() => _vkWater.Ready = false;

    /// <summary>Off-device: the snapshot pair matches the scene targets it is copied from, and the stage reads the
    /// pair with <c>texture()</c>, which is why both samplers filter linearly as GL's copy does. Oracles: the
    /// formats <c>VkEnsure</c> gives the scene targets, written again here, and the embedded GLSL text.</summary>
    internal static void VkWaterSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.VkWaterSelfCheck");
        string head = ShaderSource.Get("cx_water_fhead.glsl"), main = ShaderSource.Get("cx_water_fmain.glsl");
        check.Expect(head.Contains("uniform sampler2D uFbColor;") && head.Contains("uniform sampler2D uFbDepth;"),
            "the stage reads the snapshot pair as two 2D samplers");
        check.Expect(main.Contains("texture(uFbColor, uv).rgb") && main.Contains("CxMarchZ(uFbDepth, uNearFar, uv)"),
            "...with texture(), not texelFetch, so both halves are filtered and the copy's sampler must be linear");
        check.Expect(head.Contains("uniform sampler2D uNoise;") && head.Contains("uniform int uEnvOk;"),
            "the wave field is the engine's noise texture and the env term is a switch, not a black texel");
        check.Expect(Weather.RippleLib.Contains("uniform float uSplash;"),
            "the rain impact rings on Vulkan: the water's rate is set and the ripple tile is bound by name");
        log.Info(check.Ok ? "AlloyRenderer.VkWaterSelfCheck: passed" : "AlloyRenderer.VkWaterSelfCheck: FAILED");
    }
}
