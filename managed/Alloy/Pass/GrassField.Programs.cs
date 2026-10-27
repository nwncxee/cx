using System.Collections.Generic;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The pass's shader side: the uniform locations each stage resolves, the CPU tables both halves of
/// the frame read, and the shader resrefs an override replaces.</summary>
internal static unsafe partial class GrassField
{
    internal struct Loc
    {
        public int Look, Segments, Width, Probe;
        public int WxMapXf, WxMapP;   // R2: the area wetness map's fit and look
        public int CoverVp, CoverLayer, WxEdge, WxCache;   // R5: the cover map's own shelter test, shared with the lit stage
        public int SkyVp, SkyTerm;   // the overhead sky view's fit and look terms, shared with the lit stage
        public int Frame, Vp, Eye, Fade, Tex, MatAmb, MatDif, Lit, WindGain, WindSpeed, Msaa, Edge, Lod, Shade, Viz, Tuft, VLit, Cast, CastWidth, RootAo, GroundTint, Ground, GroundOn, GroundViz, SoftEdge, Snow;
        public int PushTex, PushGain, PushWindow;
        public int SunTex, SunDynTex, CubeTex, CubeCount, CubeDynTex;   // CX-43: the rest of the receiver rides CxPass
    }
    /// <summary>The scatter's uniforms.</summary>
    internal struct ScatterLoc { public int Planes, Eye, Lod, Fade, SnowThin, Clamp, PushTex, PushWindow, Near, Clump, CardKeep, BladeCap; }

    /// <summary>The CPU halves both backends draw from: the resident faces and the push field, allocated once.</summary>
    private static void EnsureCpu()
    {
        _faces ??= new GrassFace[MaxFaces];
        if (_push != null) return;
        _push = new byte[PushRes * PushRes * PushChannels];
        _pushClear = new byte[_push.Length];
        for (int i = 0; i < _pushClear.Length; i += PushChannels)
        { _pushClear[i] = 128; _pushClear[i + 1] = 128; }
        // Neutral from the start: the rebuild only undoes the rectangle the last list wrote, so anything
        // it never wrote has to already be neutral rather than zero (which is a full push toward -x, -y).
        Array.Copy(_pushClear, _push, _push.Length);
    }

    /// <summary>The composed embedded fragment stage -- what an override REPLACES. Named so
    /// <c>AlloyRenderer.SelfCheck</c> can assert the embedded text satisfies the guards it is checked
    /// against, which it must, or every session rebuilds from it.</summary>
    internal static string EmbeddedFs => ShaderStages.Source(ShdGrassFs);
    /// <summary>...and the procedural blade's, for the suites that assert both carry one wetness path.</summary>
    internal static string EmbeddedBladeFs => ShaderStages.Source(ShdBladeFs);

    /// <summary>The scatter's locations in program <paramref name="c"/>.</summary>
    internal static ScatterLoc ScatterLocs(uint c)
        => new ScatterLoc
        {
            Planes = GlProgram.Loc(c, "uPlanes"), Eye = GlProgram.Loc(c, "uEyePos"), Lod = GlProgram.Loc(c, "uLod"),
            Fade = GlProgram.Loc(c, "uFade"), SnowThin = GlProgram.Loc(c, "uSnowThin"), Clamp = GlProgram.Loc(c, "uClamp"),
            PushTex = GlProgram.Loc(c, "uPushTex"), PushWindow = GlProgram.Loc(c, "uPushWindow"),
            Near = GlProgram.Loc(c, "uNear"), Clump = GlProgram.Loc(c, "uClump"), CardKeep = GlProgram.Loc(c, "uCardKeep"),
            BladeCap = GlProgram.Loc(c, "uBladeCap"),
        };

    internal static string ScatterCs => ShaderSource.Get("cx_grass_c.glsl");

    internal static Loc Locate(uint prog)
        => new Loc
        {
            Vp = GlProgram.Loc(prog, "uVP"), Eye = GlProgram.Loc(prog, "uEyePos"), Frame = GlProgram.Loc(prog, "uFrame"),
            Fade = GlProgram.Loc(prog, "uFade"), Tex = GlProgram.Loc(prog, "uTex"),
            Lod = GlProgram.Loc(prog, "uLod"), Shade = GlProgram.Loc(prog, "uShade"), Viz = GlProgram.Loc(prog, "uViz"),
            Look = GlProgram.Loc(prog, "uLook"), Probe = GlProgram.Loc(prog, "uProbe"), Segments = GlProgram.Loc(prog, "uSegments"), Width = GlProgram.Loc(prog, "uWidth"),
            Tuft = GlProgram.Loc(prog, "uTuft"), VLit = GlProgram.Loc(prog, "uVLit"), Cast = GlProgram.Loc(prog, "uCast"), CastWidth = GlProgram.Loc(prog, "uCastWidth"), Snow = GlProgram.Loc(prog, "uSnow"),
            RootAo = GlProgram.Loc(prog, "uRootAo"), GroundTint = GlProgram.Loc(prog, "uGroundTint"), Ground = GlProgram.Loc(prog, "uGround"), GroundOn = GlProgram.Loc(prog, "uGroundOn"), GroundViz = GlProgram.Loc(prog, "uGroundViz"), SoftEdge = GlProgram.Loc(prog, "uSoftEdge"),
            MatAmb = GlProgram.Loc(prog, "uMatAmbient"), MatDif = GlProgram.Loc(prog, "uMatDiffuse"),
            Lit = GlProgram.Loc(prog, "uLit"), WindGain = GlProgram.Loc(prog, "uWindGain"),
            WindSpeed = GlProgram.Loc(prog, "uWindSpeed"),
            Msaa = GlProgram.Loc(prog, "uMsaa"), Edge = GlProgram.Loc(prog, "uEdge"),
            PushTex = GlProgram.Loc(prog, "uPushTex"), PushGain = GlProgram.Loc(prog, "uPushGain"),
            PushWindow = GlProgram.Loc(prog, "uPushWindow"),
            SunTex = GlProgram.Loc(prog, "uSunShadow"), SunDynTex = GlProgram.Loc(prog, "uSunDynShadow"),
            CubeTex = GlProgram.Loc(prog, "uLocalShadow"), CubeDynTex = GlProgram.Loc(prog, "uLocalDynShadow"),
            CubeCount = GlProgram.Loc(prog, "uLocalCubes"),
            WxMapXf = GlProgram.Loc(prog, "uWxMapXf"), WxMapP = GlProgram.Loc(prog, "uWxMapP"),
            CoverVp = GlProgram.Loc(prog, "uCoverVp"), CoverLayer = GlProgram.Loc(prog, "uCoverLayer"), WxEdge = GlProgram.Loc(prog, "uWxEdge"), WxCache = GlProgram.Loc(prog, "uWxCache"),
            SkyVp = GlProgram.Loc(prog, "uSkyVp"), SkyTerm = GlProgram.Loc(prog, "uSkyTerm"),
        };

    // ---- shaders -----------------------------------------------------------------------------

    internal const string ShdGrassVs = "cx_grass_vs", ShdGrassFs = "cx_grass_fs";
    internal const string ShdBladeVs = "cx_gblade_vs", ShdBladeFs = "cx_gblade_fs";

    /// <summary>The vertex stage: one crossed-quad blade per instance, rotated, scaled, and swayed. The sway
    /// is root-anchored (<c>z^2</c>, so the base never leaves the ground) and driven by the engine's own
    /// <c>areaGlobalWind</c> plus the per-blade bend jitter, which is what stops a field moving as one sheet.
    /// It DISPLACES, which is why grass is out of the world depth prepass and carries no invariant clip.</summary>
    internal static string GrassVs => ShaderSource.Get("cx_grass_v.glsl");

    internal static string GrassFsHead => ShaderSource.Get("cx_grass_fhead.glsl");

    internal static string BladeFsHead => ShaderSource.Get("cx_gblade_fhead.glsl");

    /// <summary>The fragment stage. Two discards and no blending at all, which is the whole replacement:
    /// vanilla's fade rides in the alpha channel its cutout reads, so a blade in the fade band is
    /// semi-transparent and depth-writing and deletes whatever stands behind it. Here the fade is a
    /// SCREEN-DOOR dither reaching zero exactly at the engine's own far radius, so there is no pop either.</summary>
    internal static string GrassFsMain => ShaderSource.Get("cx_grass_fmain.glsl");

    /// <summary>The sway library both vertex paths include -- one text for this frame's blade and last
    /// frame's -- and the two vertex mains, for the suite that asserts both carry it.</summary>
    internal static string SwayGlsl => ShaderSource.Get("cx_gsway.glsl");
    /// <inheritdoc cref="SwayGlsl"/>
    internal static string GrassVmain => ShaderSource.Get("cx_grass_vmain.glsl");
    /// <inheritdoc cref="SwayGlsl"/>
    internal static string BladeVmain => ShaderSource.Get("cx_gblade_vmain.glsl");
}
