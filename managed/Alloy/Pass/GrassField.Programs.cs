using System.Collections.Generic;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The pass's GL resources: the buffers the scatter and the draws share, the colour, depth and scatter
/// programs with their uniform locations, and the shader resrefs an override replaces.</summary>
internal static unsafe partial class GrassField
{
    /// <summary>Everything the GL context owns, in one object: <see cref="ContextLost"/> replaces it.</summary>
    private sealed class GrassGlState
    {
        public uint Vao, FaceBuf, PatchBuf, MatBuf, OutBuf, CmdBuf, StatBuf, Prog, ProgDepth;
        public uint ProgScatter, ProgBlade, ProgBladeDepth;
        /// <summary>The velocity arm of each path -- the same vertex text with CX_MOTION compiled in.
        /// 0 = that path keeps the camera's own vectors.</summary>
        public uint ProgMotion, ProgBladeMotion;
        /// <summary>The persistent map of <see cref="StatBuf"/>: the command counts copied after every draw, read
        /// by <see cref="Stats"/> a frame or more late without a sync.</summary>
        public uint* Stat;
        public Loc Loc, LocD, LocB, LocBD, LocM, LocBM;
        public AlloyRenderer.FeatLoc FeatLoc, FeatLocD, FeatLocB, FeatLocBD, FeatLocM, FeatLocBM;
        public ScatterLoc LocS;
        /// <summary>Two sampler objects because a texture with no mip chain is mipmap-INCOMPLETE under a mipmap
        /// min filter and samples black, so the filter follows the texture; a sampler leaves the engine's own sampling alone.</summary>
        public uint SampMip, SampFlat;
        public uint PushTex;
        /// <summary>Does the push texture hold what <see cref="_push"/> holds? False before the first upload,
        /// where a fresh texture's storage is undefined rather than neutral.</summary>
        public bool PushSynced;
        /// <summary>Pushers in the list the field currently holds (-1 = none that can be trusted).</summary>
        public int PushWasN = -1;
        /// <summary>The DEFAULT framebuffer's sample count, latched for the life of the context (0 = not yet
        /// read): the only multisampled surface the grass pass can land on.</summary>
        public int Fb0Samples;
    }

    private static GrassGlState _gl = new();

    internal struct Loc
    {
        public int Look, Segments, Width, Probe;
        public int WxMapXf, WxMapP;   // R2: the area wetness map's fit and look
        public int CoverVp, CoverLayer, WxEdge, WxCache;   // R5: the cover map's own shelter test, shared with the lit stage
        public int SkyVp, SkyTerm;   // the overhead sky view's fit and look terms, shared with the lit stage
        public int Frame, Vp, Eye, Fade, Tex, MatAmb, MatDif, Lit, WindGain, WindSpeed, Msaa, Edge, Lod, Shade, Viz, Tuft, VLit, Cast, CastWidth, RootAo, GroundTint, Ground, GroundOn, GroundViz, SoftEdge, Snow;
        public int PushTex, PushGain, PushWindow;
        public int PrevVp, PrevTime, Jitter, MSize;   // CX-48: the velocity arm's own four
        public int SunTex, SunDynTex, CubeTex, CubeCount, CubeDynTex;   // CX-43: the rest of the receiver rides CxPass
    }
    /// <summary>The scatter's uniforms.</summary>
    internal struct ScatterLoc { public int Planes, Eye, Lod, Fade, SnowThin, Clamp, PushTex, PushWindow, Near, Clump, CardKeep, BladeCap; }

    // ---- GL resources ------------------------------------------------------------------------

    private static bool EnsureBuffers()
    {
        if (_gl.Vao != 0) return true;
        EnsureCpu();

        // The card template: two crossed quads in blade-local space. x/y span [-0.5, 0.5] (one quad along x,
        // one along y), z runs 0 at the root to 1 at the tip, and the UVs are the 0..1 of one atlas cell.
        // Corner order per quad: (-half, root) (+half, root) (+half, tip) then (-half, root) (+half, tip)
        // (-half, tip). v runs 0 at the root to 1 at the tip, matching the engine's own corner order --
        // CreateTriangleGrass puts root corners at the atlas sub-rect origin and tip corners half a cell
        // above it, so a flipped v would show the blades upside down.
        // sized for the longest draw off this VAO (a near blade strip) though only the cards read it: an enabled
        // array shorter than the draw is out of bounds on a strict driver whether or not it is consumed
        const int templateCap = 2 * NearSegments + 1;
        float* t = stackalloc float[templateCap * 5];
        new Span<float>(t, templateCap * 5).Clear();
        int k = 0;
        for (int q = 0; q < 2; q++)
        {
            float ax = q == 0 ? 0.5f : 0f, ay = q == 0 ? 0f : 0.5f;
            for (int i = 0; i < 6; i++)
            {
                float side = Corner[i * 3], z = Corner[i * 3 + 1], u = Corner[i * 3 + 2];
                t[k++] = side * ax; t[k++] = side * ay; t[k++] = z; t[k++] = u; t[k++] = z;
            }
        }

        uint vao;
        uint* b = stackalloc uint[7];
        Gl.CreateVertexArrays(1, &vao);
        Gl.CreateBuffers(7, b);
        for (int i = 0; i < 7; i++)
            if (b[i] == 0) return _down.Fail(_log, "the grass buffers could not be created", "no grass draws");
        if (vao == 0) return _down.Fail(_log, "the grass buffers could not be created", "no grass draws");
        uint template = b[0];
        _gl.FaceBuf = b[1]; _gl.PatchBuf = b[2]; _gl.MatBuf = b[3]; _gl.OutBuf = b[4]; _gl.CmdBuf = b[5]; _gl.StatBuf = b[6];
        Gl.NamedBufferData(_gl.FaceBuf, (IntPtr)((long)MaxFaces * sizeof(GrassFace)), null, Gl.DYNAMIC_DRAW);
        Gl.NamedBufferData(_gl.CmdBuf, (IntPtr)(Cmds * 4 * sizeof(uint)), null, Gl.DYNAMIC_DRAW);
        Gl.ZeroBuffer(_gl.CmdBuf, Cmds * 4 * sizeof(uint));   // the shadow and motion draws read last frame's commands
        const uint flags = Gl.MAP_READ_BIT | Gl.MAP_PERSISTENT_BIT | Gl.MAP_COHERENT_BIT;
        Gl.NamedBufferStorage(_gl.StatBuf, (IntPtr)(Cmds * 4 * sizeof(uint)), null, flags);
        _gl.Stat = (uint*)Gl.MapNamedBufferRange(_gl.StatBuf, IntPtr.Zero, (IntPtr)(Cmds * 4 * sizeof(uint)), flags);

        // binding 0 = the template (attribs 0, 1); binding 1 = the scatter's output, read back as a divisor-1
        // instance stream (attribs 2..4): each indirect command's baseInstance is its material's region, so one
        // VAO serves every slot
        Gl.NamedBufferData(template, (IntPtr)(templateCap * 5 * sizeof(float)), t, Gl.STATIC_DRAW);
        Gl.VertexArrayVertexBuffer(vao, 0, template, IntPtr.Zero, 5 * sizeof(float));
        Gl.VertexArrayAttribFormat(vao, 0, 3, Gl.FLOAT, 0, 0);
        Gl.VertexArrayAttribFormat(vao, 1, 2, Gl.FLOAT, 0, 3 * sizeof(float));
        Gl.NamedBufferData(_gl.OutBuf, (IntPtr)((long)RecordCap * InstFloats * sizeof(float)), null, Gl.DYNAMIC_DRAW);
        Gl.VertexArrayVertexBuffer(vao, 1, _gl.OutBuf, IntPtr.Zero, InstFloats * sizeof(float));
        Gl.VertexArrayBindingDivisor(vao, 1, 1);
        for (uint a = 0; a < 5; a++)
        {
            if (a >= 2) Gl.VertexArrayAttribFormat(vao, a, 4, Gl.FLOAT, 0, (a - 2) * 4 * sizeof(float));
            Gl.VertexArrayAttribBinding(vao, a, a < 2 ? 0u : 1u);
            Gl.EnableVertexArrayAttrib(vao, a);
        }
        _gl.Vao = vao;

        // The displacement field. LINEAR so the push varies smoothly between texels (a nearest-sampled one
        // makes blades snap between neighbouring directions as a creature walks), CLAMP_TO_EDGE so a blade
        // just outside the window reads the border rather than wrapping to the far side of it.
        uint tex;
        Gl.CreateTextures(Gl.TEXTURE_2D, 1, &tex);
        if (tex != 0)
        {
            Gl.TextureStorage2D(tex, 1, Gl.RGBA8, PushRes, PushRes);
            RenderTarget.Clamp(tex, Gl.LINEAR);
            _gl.PushTex = tex;
        }
        return true;
    }

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

    /// <summary>The grass stage splices content too; the module edge drops it for a rebuild.</summary>
    internal static void DropProgram()
    {
        foreach (uint p in (ReadOnlySpan<uint>)[_gl.Prog, _gl.ProgDepth, _gl.ProgScatter, _gl.ProgBlade,
                                               _gl.ProgBladeDepth, _gl.ProgMotion, _gl.ProgBladeMotion])
            if (p != 0) Gl.DeleteProgram(p);
        _gl.Prog = 0; _gl.ProgDepth = 0; _gl.ProgScatter = 0; _gl.ProgBlade = 0; _gl.ProgBladeDepth = 0;
        _gl.ProgMotion = 0; _gl.ProgBladeMotion = 0;
    }

    private static bool EnsureProgram()
    {
        if (_gl.Prog != 0) return true;
        if (_down.Failed) return false;
        string fs = Programs.WithSkyView(EmbeddedFs), vs = ShaderStages.Source(ShdGrassVs);
        uint prog = GlProgram.Graphics(ShdGrassVs, vs, ShdGrassFs, fs, _log,
                                       "GrassField instanced grass", out _, out string fsSrc);
        // #132: an override written before the linear flip still ends in CxRevert, links cleanly, and
        // gets encoded a second time by the resolve -- a washed-out lawn, silently. Rebuilt from the
        // embedded text rather than logged and shipped, like every other Alloy stage's guard.
        if (prog != 0 && !(ShaderContract.LinearSafe(fsSrc) && ShaderContract.SlotSafe(fsSrc, "uTex")))
        {
            Gl.DeleteProgram(prog);
            _log?.Info("GrassField: grass override still encodes to display space -- rebuilding from the "
                     + "embedded default");
            vs = GrassVs;
            prog = GlProgram.Graphics(vs, fs, _log, "GrassField instanced grass (embedded)", content: false);
        }
        if (prog == 0)
            return _down.Fail(_log, "the grass program is unavailable",
                              "grass falls back to the engine's own draw");
        _gl.Prog = prog;
        // The prepass program: the SAME vertex text (an override applies to both), the alpha-only stage.
        // Not fatal: without it the colour pass writes depth itself, the pre-#355 shape.
        _gl.ProgDepth = GlProgram.Graphics(vs, GrassDepthFs, _log, "GrassField grass depth prepass");
        if (_gl.ProgDepth == 0) _log?.Info("GrassField: the depth prepass program is unavailable -- grass draws single-pass");
        else
        {
            _gl.LocD = Locate(_gl.ProgDepth);
            _gl.FeatLocD = Programs.FeatLocs(_gl.ProgDepth);
            Gl.UseProgram(_gl.ProgDepth);
            Gl.Uniform1i(_gl.LocD.Tex, 0);
            // the per-vertex rig's shadow samplers live in the shared vertex text: unassigned they sit on unit 0
            // beside uTex and GL refuses every depth draw
            ShadowUnits(in _gl.LocD);
        }
        BuildMotion(vs);
        if (!EnsureScatter()) return false;
        _gl.Loc = Locate(prog);
        _gl.FeatLoc = Programs.FeatLocs(prog);
        // Same reason the lit program does it here: a sampler left at its default points at unit 0, and GL
        // refuses a draw whose samplers of different types share an image unit.
        Gl.UseProgram(prog);
        Gl.Uniform1i(_gl.Loc.Tex, 0);
        return EnsureProgramTail(prog);
    }

    /// <summary>The scatter every grass draw reads (without it the pass stands down), and the procedural
    /// blade pair, colour and cascade depth off one vertex text (without it every atlas draws as cards).</summary>
    private static bool EnsureScatter()
    {
        string bvs = ShaderStages.Source(ShdBladeVs), bfs = Programs.WithSkyView(ShaderStages.Source(ShdBladeFs));
        uint b = GlProgram.Graphics(ShdBladeVs, bvs, ShdBladeFs, bfs, _log, "GrassField procedural blades", out string bvsSrc, out string bfsSrc);
        if (b != 0 && !ShaderContract.LinearSafe(bfsSrc))
        {
            Gl.DeleteProgram(b);
            _log?.Info("GrassField: blade override still encodes to display space -- rebuilding from the embedded default");
            bvsSrc = bvs;
            b = GlProgram.Graphics(bvs, bfs, _log, "GrassField procedural blades (embedded)", content: false);
        }
        _gl.ProgBlade = b;
        if (b == 0) _log?.Info("GrassField: the blade program is unavailable -- every grass atlas draws as cards");
        else
        {
            _gl.LocB = Locate(b);
            _gl.FeatLocB = Programs.FeatLocs(b);
            Gl.UseProgram(b);
            ShadowUnits(in _gl.LocB);
            _gl.ProgBladeDepth = GlProgram.Graphics(bvsSrc, ShaderSource.Get("cx_gblade_df.glsl"), _log, "GrassField blade cascade depth");
            if (_gl.ProgBladeDepth != 0)
            {
                _gl.LocBD = Locate(_gl.ProgBladeDepth);
                _gl.FeatLocBD = Programs.FeatLocs(_gl.ProgBladeDepth);
                Gl.UseProgram(_gl.ProgBladeDepth);
                ShadowUnits(in _gl.LocBD);
            }
            BuildBladeMotion(bvsSrc);
        }
        uint c = GlProgram.Compute(ScatterCs, _log, "GrassField scatter");
        if (c == 0) return _down.Fail(_log, "the grass scatter program is unavailable", "grass falls back to the engine's own draw");
        _gl.ProgScatter = c;
        _gl.LocS = ScatterLocs(c);
        return true;
    }

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

    /// <summary>The card path's velocity program: the SAME vertex text the colour pass draws with (an
    /// override's own sway included) and the depth stage's coverage decision, both with their velocity arm
    /// compiled in. Refused unless that text reconstructs last frame's blade -- a stage without it writes
    /// nothing and leaves the camera's field standing over every card.</summary>
    private static void BuildMotion(string vs)
    {
        if (!ShaderContract.SwaySafe(vs))
        { _log?.Info("GrassField: the grass vertex stage carries no previous blade -- cards keep the camera's vectors"); return; }
        _gl.ProgMotion = GlProgram.Graphics(ShaderStages.WithDefine(vs, MotionDefine), ShaderStages.WithDefine(GrassDepthFs, MotionDefine), _log, "GrassField grass motion vectors");
        if (_gl.ProgMotion == 0) { _log?.Info("GrassField: the card velocity program is unavailable -- cards keep the camera's vectors"); return; }
        _gl.LocM = Locate(_gl.ProgMotion);
        _gl.FeatLocM = Programs.FeatLocs(_gl.ProgMotion);
        Gl.UseProgram(_gl.ProgMotion);
        Gl.Uniform1i(_gl.LocM.Tex, 0);
        ShadowUnits(in _gl.LocM);
    }

    /// <inheritdoc cref="BuildMotion"/>
    private static void BuildBladeMotion(string bvs)
    {
        if (!ShaderContract.SwaySafe(bvs))
        { _log?.Info("GrassField: the blade vertex stage carries no previous blade -- blades keep the camera's vectors"); return; }
        _gl.ProgBladeMotion = GlProgram.Graphics(ShaderStages.WithDefine(bvs, MotionDefine), ShaderStages.WithDefine(BladeDepthFs, MotionDefine), _log, "GrassField blade motion vectors");
        if (_gl.ProgBladeMotion == 0) { _log?.Info("GrassField: the blade velocity program is unavailable -- blades keep the camera's vectors"); return; }
        _gl.LocBM = Locate(_gl.ProgBladeMotion);
        _gl.FeatLocBM = Programs.FeatLocs(_gl.ProgBladeMotion);
        Gl.UseProgram(_gl.ProgBladeMotion);
        ShadowUnits(in _gl.LocBM);
    }

    /// <summary>The define that compiles a stage's velocity arm in, spliced by <c>ShaderStages.WithDefine</c>.
    /// <c>check-shaders.py</c> makes the same splice, so both arms are link-checked.</summary>
    internal const string MotionDefine = "CX_MOTION 1";

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
            PrevVp = GlProgram.Loc(prog, "uPrevVP"), PrevTime = GlProgram.Loc(prog, "uPrevTimeOfDay"),
            Jitter = GlProgram.Loc(prog, "uJitter"), MSize = GlProgram.Loc(prog, "uSize"),
            SunTex = GlProgram.Loc(prog, "uSunShadow"), SunDynTex = GlProgram.Loc(prog, "uSunDynShadow"),
            CubeTex = GlProgram.Loc(prog, "uLocalShadow"), CubeDynTex = GlProgram.Loc(prog, "uLocalDynShadow"),
            CubeCount = GlProgram.Loc(prog, "uLocalCubes"),
            WxMapXf = GlProgram.Loc(prog, "uWxMapXf"), WxMapP = GlProgram.Loc(prog, "uWxMapP"),
            CoverVp = GlProgram.Loc(prog, "uCoverVp"), CoverLayer = GlProgram.Loc(prog, "uCoverLayer"), WxEdge = GlProgram.Loc(prog, "uWxEdge"), WxCache = GlProgram.Loc(prog, "uWxCache"),
            SkyVp = GlProgram.Loc(prog, "uSkyVp"), SkyTerm = GlProgram.Loc(prog, "uSkyTerm"),
        };

    private static bool EnsureProgramTail(uint prog)
    {
        ShadowUnits(in _gl.Loc);
        _log?.Info("GrassField: instanced grass program ready");
        return true;
    }

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

    /// <summary>The depth prepass stage: the colour stage's coverage decision -- alpha cutout, distance
    /// dither, keyhole -- and nothing else, so the depth it writes is exactly the set of fragments the colour
    /// pass keeps, and early-Z discards the hidden blades before the light and shadow taps. Its alpha output
    /// still feeds alpha-to-coverage where the target is multisampled.</summary>
    internal static string GrassDepthFs => ShaderSource.Get("cx_grassdepth_f.glsl");

    /// <summary>The blade cascade-depth stage, which is the blade VELOCITY stage under CX_MOTION.</summary>
    internal static string BladeDepthFs => ShaderSource.Get("cx_gblade_df.glsl");

    /// <summary>The sway library both vertex paths include -- one text for this frame's blade and last
    /// frame's -- and the two vertex mains, for the suite that asserts both carry it.</summary>
    internal static string SwayGlsl => ShaderSource.Get("cx_gsway.glsl");
    /// <inheritdoc cref="SwayGlsl"/>
    internal static string GrassVmain => ShaderSource.Get("cx_grass_vmain.glsl");
    /// <inheritdoc cref="SwayGlsl"/>
    internal static string BladeVmain => ShaderSource.Get("cx_gblade_vmain.glsl");
}
