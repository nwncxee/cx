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
    /// <summary>The lit stage's DEFERRED twin on the vanilla renderer's chain (ADR 0029): 0 = no twin, which is
    /// what keeps that chain's deferred pass -- and the pair tiles it takes its debt from -- stood down.</summary>
    internal static uint _progLitDef;
    /// <summary>The vertex source the lit / fullbright programs actually compiled with (an override wins).</summary>
    internal static string? _litVsSrc;
    /// <summary>The fragment text the lit program compiled with: what every variant of it is spliced from (the
    /// A2C mask, and whatever a diagnostic builds).</summary>
    internal static string? _litFsSrc;
    /// <summary>The spliced programs were built against a resource chain that is gone. Every program carrying
    /// a <c>// $VANILLA</c> region (lit, content pairs, water, grass) compiles ONCE from whatever the chain held
    /// then, so without this a session keeps the first server's display hooks running on the next one. Set on
    /// the module-unload edge (outside the bracket), acted on by <see cref="DropSpliced"/> inside it.</summary>
    internal static bool _spliceStale;

    /// <summary>Delete every program that splices content and clear its failure latch, so each rebuilds
    /// on its next use from the module now loaded. Inside the bracket, which is why the edge only sets the
    /// flag. The fullbright and depth programs splice nothing and stay.</summary>
    internal static void DropSpliced()
    {
        _spliceStale = false;
        // grass splices content too; its stages rebuild from the module now loaded
        // the native stages' images and passes first: the hosts below re-attach their programs to them as they rebuild
        StageResources.DropAll();
        PostStack.DropSpliced();
        SkyStage.DropPrograms();
        ContentPrograms.DropSpliced();
        _log?.Info("AlloyRenderer: module changed (or alloy.native was edited) -- the spliced programs (lit, content pairs, "
                 + "water, grass, post, sky) and the native stages' images and passes are dropped and rebuild from this "
                 + "module's resource chain");
    }

    /// <summary>The bindless decision for a Vulkan session, whose descriptor table is bindless on every device: the
    /// knob alone (<c>alloy.bindless off</c> still binds per batch), with no GL probe.</summary>
    internal static void DecideBindlessVk()
    {
        if (_bindlessProbed) return;
        _bindlessProbed = true;
        _bindless = Renderer.Bindless.Boot != 0;
        _log?.Info(_bindless ? "AlloyRenderer: Vulkan's bindless table armed -- lit batches merge across textures"
                             : "AlloyRenderer: bindless texturing vetoed (alloy.bindless off) -- per-batch texture binds on Vulkan");
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
    /// at build, once per program and twin.</summary>
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
        VFog = GlProgram.Loc(prog, "uVFog"),
        VFogVp = GlProgram.Loc(prog, "uVFogVp"),
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
        // #347: the shared vanilla environment's own terms are the CxEngine block, declared by every content
        // environment; a program that reads none of them has the block optimised out and owes it no bind.
        loc.Engine = GlProgram.UsesBlock(prog, "CxEngine");
        return loc;
    }
}
