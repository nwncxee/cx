using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;

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

    /// <summary>What the owned stages ask of the driver beyond its guaranteed floor: three vertex-stage SSBO
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

    /// <summary>Resolve one lit program's loose-block members. Called once per program at build, once per
    /// program and twin.</summary>
    internal static LitLoc LitLocs()
    {
        var L = new LitLoc();
        L.Vp = LooseBlock.Loc("uVP"); L.Base = LooseBlock.Loc("uDrawBase");
        L.Tex = LooseBlock.Loc("uTex");
        L.Cut = LooseBlock.Loc("uAlphaCut"); L.AlphaMip = LooseBlock.Loc("uAlphaMip");
        L.Viz = LooseBlock.Loc("uViz"); L.Oit = LooseBlock.Loc("uOit"); L.OitDim = LooseBlock.Loc("uOitDim"); L.OitGob = LooseBlock.Loc("uOitGob"); L.LightRecOn = LooseBlock.Loc("uLightRecOn");
        L.Decal = LooseBlock.Loc("uDecal"); L.DecalZ = LooseBlock.Loc("uDecalZ"); L.Solid = LooseBlock.Loc("uSolid");
        L.SunTex = LooseBlock.Loc("uSunShadow"); L.SunDynTex = LooseBlock.Loc("uSunDynShadow");
        L.CubeTex = LooseBlock.Loc("uLocalShadow"); L.CubeDynTex = LooseBlock.Loc("uLocalDynShadow");
        L.CubeCount = LooseBlock.Loc("uLocalCubes");
        L.Probe = LooseBlock.Loc("uProbe");
        L.Capture = LooseBlock.Loc("uCapture"); L.MirrorZ = LooseBlock.Loc("uMirrorZ");
        L.LmPages = LooseBlock.Loc("uLmPages"); L.LmMovers = LooseBlock.Loc("uLmMovers");
        L.Ssao = LooseBlock.Loc("uSsao"); L.SsaoOn = LooseBlock.Loc("uSsaoOn");
        L.SsaoHalf = LooseBlock.Loc("uSsaoHalf");
        L.LightOcc = LooseBlock.Loc("uLightOcc");
        L.Material = LooseBlock.Loc("uMaterial");
        L.AoDirect = LooseBlock.Loc("uAoDirect"); L.AoCutout = LooseBlock.Loc("uAoCutout");
        L.SkyVp = LooseBlock.Loc("uSkyVp"); L.SkyTerm = LooseBlock.Loc("uSkyTerm");
        L.Weather = LooseBlock.Loc("uWeather"); L.WeatherLook = LooseBlock.Loc("uWeatherLook");
        L.CoverVp = LooseBlock.Loc("uCoverVp"); L.CoverLayer = LooseBlock.Loc("uCoverLayer");
        L.SnowLook = LooseBlock.Loc("uSnowLook"); L.SnowLight = LooseBlock.Loc("uSnowLight"); L.CoverEsm = LooseBlock.Loc("uCoverEsmP"); L.SnowPom = LooseBlock.Loc("uSnowPom");
        L.WxMapXf = LooseBlock.Loc("uWxMapXf"); L.WxMapP = LooseBlock.Loc("uWxMapP");
        L.WxEdge = LooseBlock.Loc("uWxEdge"); L.WxRun = LooseBlock.Loc("uWxRun"); L.WxCache = LooseBlock.Loc("uWxCache");
        L.Feat = FeatLocs();
        return L;
    }

    /// <summary>The <see cref="FeatDecl"/> uniform locations of one program, in the order
    /// <see cref="UploadFeatureTerms"/> writes them.</summary>
    internal static FeatLoc FeatLocs() => WithScriptables(new FeatLoc
    {
        Time = LooseBlock.Loc("uTimeOfDay"),
        Wind = LooseBlock.Loc("uAreaWind"),
        Flags = LooseBlock.Loc("uAreaFlags"),
        Cam = LooseBlock.Loc("uCamPos"),
        CamFocus = LooseBlock.Loc("uCamFocus"),
        Player = LooseBlock.Loc("uPlayerPos"),
        Fog = LooseBlock.Loc("uFogParams"),
        FogColor = LooseBlock.Loc("uFogColor"),
        KhDia = LooseBlock.Loc("uKeyholeDia"),
        KhCam = LooseBlock.Loc("uKeyholeCam"),
        Pixel = LooseBlock.Loc("uPixel"),
        LodBias = LooseBlock.Loc("uLodBias"),
        Now = LooseBlock.Loc("uNow"),
        Eye = LooseBlock.Loc("uEyePos"),
        FogSky = LooseBlock.Loc("uFogSky"),
        FogSkyColor = LooseBlock.Loc("uFogSkyColor"),
        FogSun = LooseBlock.Loc("uFogSun"),
        FogSunColor = LooseBlock.Loc("uFogSunColor"),
        FogOrigin = LooseBlock.Loc("uFogOrigin"),
        VFog = LooseBlock.Loc("uVFog"),
        VFogVp = LooseBlock.Loc("uVFogVp"),
        // #346: the ENGINE's own uniform names, because the stage compiles the engine's own file. Renaming
        // any of these means editing spliced content.
        TgtShapeL = LooseBlock.Loc("targetShape"),
        TgtSelfL = LooseBlock.Loc("targetShapeOriginOnSelf"),
        TgtObjL = LooseBlock.Loc("targetObjectType"),
        TgtSizeXL = LooseBlock.Loc("targetShapeSizeX"),
        TgtSizeYL = LooseBlock.Loc("targetShapeSizeY"),
        TgtRangeL = LooseBlock.Loc("targetShapeSpellRange"),
        TgtColL = LooseBlock.Loc("targetShapeColour"),
        TgtWorldL = LooseBlock.Loc("targetWorldCoords"),
    });

    /// <summary>The scriptable block's locations, filled after the struct so the fixed buffers can be
    /// written. Cold -- once per program, at build.</summary>
    private static FeatLoc WithScriptables(FeatLoc loc)
    {
        for (int i = 0; i < ScriptableSlots; i++)
        {
            loc.SInt[i] = LooseBlock.Loc($"scriptableInt{i + 1}");
            loc.SFloat[i] = LooseBlock.Loc($"scriptableFloat{i + 1}");
            loc.SVec[i] = LooseBlock.Loc($"scriptableVec{i + 1}");
            if (loc.SInt[i] >= 0) loc.SIntLive |= 1 << i;
            if (loc.SFloat[i] >= 0) loc.SFloatLive |= 1 << i;
            if (loc.SVec[i] >= 0) loc.SVecLive |= 1 << i;
        }
        return loc;
    }
}
