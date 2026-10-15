using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// Volumetric light beams, enumerated off the engine's bucket 15 and drawn at the Alloy graph slot (ADR 0011).
/// The population is <c>DrawBucketManager::GetFirstItem(Scene+0x208, 0xf, 0)</c> -- bucket <b>15</b>, which
/// <c>Scene::RenderDrawBucket</c> never touches -- read directly once a frame in engine context, so the table
/// cannot go stale. The model matrix is the IDENTITY: beam volumes are baked in world space.
/// </summary>
internal static unsafe partial class Beams
{
    /// <summary>The Aurora primitive id <c>PartBeam::Draw</c> hard-codes.</summary>
    private const int BeamPrim = 4;
    /// <summary><c>projectionWorldZClip</c>: the world z every back-facing beam vertex is extruded down to.
    /// <c>RenderBeams</c> @0x46add0 passes the shader-constant INDEX in <c>edi</c> with the value in xmm0, so
    /// a decompiler that drops the float argument reads it as a literal 1.0 -- which truncates every shaft
    /// ~21/|dir.z| units short and leaves it hanging in mid-air. The .rodata float is -20.0.</summary>
    private const float WorldZClip = -20f;
    /// <summary>The beam tint. <c>RenderBeams</c> builds <c>globalColor</c> as
    /// <c>Homogenize(DAT_01aab9e8, projectionalpha)</c>, and <c>DAT_01aab9e8</c> is written once by a static
    /// initialiser (<c>_INIT_14</c> @0x51739a) to {1,1,1} and by nothing else. The alpha is read live off
    /// <c>projectionalpha</c>, which IS a console variable.</summary>
    private const float TintRgb = 1f;
    /// <summary>Fallback alpha when <c>projectionalpha</c> does not resolve: the engine's daylight value.
    /// <c>CNWCArea</c>'s <c>SetAsDay</c>/<c>SetAsDusk</c> store 0.05 and <c>SetAsDawn</c>/<c>SetAsNight</c>
    /// store 0, so a larger fallback draws every shaft proud of vanilla's -- and draws them at night.</summary>
    private const float DefaultAlpha = 0.05f;
    private const uint GlUnsignedShort = 0x1403;   // not-an-offset: GL enum
    /// <summary>The engine attribute locations a beam VDO carries, from <c>GLRender::ConnectShaderData</c>'s
    /// bind table @0x4ce8e0: <c>vPos</c> at 0 and <c>fProjectionFrontal</c> at 10. That pair is why beams
    /// cannot ride <c>AlloyRenderer</c>'s VAO cache -- it harvests position/UV/normal and refuses a VDO
    /// with no UV stream, which is every beam.</summary>
    private const uint EngPos = 0, EngFrontal = 10;
    /// <summary>...and ours, which the Alloy VAO binds them at.</summary>
    private const uint LocPos = 0, LocFrontal = 1;
    /// <summary>Aurora blend factors <c>RenderBeams</c> sets: <c>SetBlendFunc(0, 2)</c> =
    /// (GL_SRC_ALPHA, GL_ONE).</summary>
    private const uint GlSrcAlpha = 0x0302, GlOne = 1;   // not-an-offset: GL blend-factor enums
    /// <summary>A handful of static volumes per area; past this a beam is left to the engine.</summary>
    private const int MaxBeams = 2048, MaxVdos = 512;
    /// <summary>The bucket <c>RenderBeams</c> asks the scene's <c>DrawBucketManager</c> for.</summary>
    private const int BeamBucket = 0xf;   // not-an-offset: a draw-bucket index, not a place in memory

    /// <summary>One enrolled beam. Rebuilt from the bucket every frame, so there is no liveness field: an
    /// entry exists exactly while the engine's own classification holds it.</summary>
    internal struct Beam
    {
        public IntPtr Vdo;
        public int Item, Vao;
        /// <summary>The database part this beam is, and its mesh-arena range. Both -1 on the
        /// engine path, where the geometry is a baked projection VDO instead.</summary>
        public int Part, Range;
    }

    private static readonly Beam[] _beams = new Beam[MaxBeams];
    private static int _count;

    // draw arguments resolved by Stage(), one slot per beam issued this pass. Allocated once: the draw path
    // is inside the colour bracket and allocates nothing.
    private static readonly int[] _sBeam = new int[MaxBeams], _sCount = new int[MaxBeams], _sBase = new int[MaxBeams];
    private static readonly nint[] _sOff = new nint[MaxBeams];

    // per-VDO VAO cache, ours alone: two attributes, harvested in the ENGINE context at capture and built in
    // the 4.6 one at draw (VAOs do not share across contexts; buffers do -- #70).

    private static ExtenderLog? _log;
    /// <summary>The model base, taken once per enrollment. Every beam shares it: the bucket-15 volumes are
    /// baked in one space and <c>RenderBeams</c> sets no per-part transform.</summary>
    private static Matrix4x4 _model = Matrix4x4.Identity;
    private static int* _enable;
    private static float* _alpha;
    private static uint* _primTable;
    private static bool _off, _failed;
    /// <summary>The player's own switch (<c>effects.beams</c>). Distinct from <see cref="_off"/>, which means
    /// the session cannot own beams at all: the hooks arm either way, so the toggle is live.</summary>
    private static bool Disabled => !Effects.Beams.V;
    private static int _enrolled, _refused, _drawn;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _primTable = (uint*)Native.Sym(SymbolMap.PrimTypeTranslationTable);
        _enable = (int*)Native.Sym(SymbolMap.EnableBeams);
        _alpha = (float*)Native.Sym(SymbolMap.ProjectionAlpha);
        if (_primTable == null)
        { _off = true; log.Info("Beams: prim table unresolved -- no light beams draw"); return; }
        // #261: no hooks. The population is the engine's bucket 15, read at the Alloy slot.
        log.Info("Beams: armed -- bucket-15 volumes enumerated and drawn by Alloy (/effects beams toggles)");
    }

    /// <summary>The same direction out of the database. <see cref="FrameInputs.SunDir"/> is already the
    /// direction the light TRAVELS, and <c>HasSun</c> carries the same two tests the engine's
    /// <c>GetShadowLights(1)</c> filter applied: the light reaches past
    /// <see cref="EngineLayout.DirectionalRange"/> and it points downward. An interior draws no shafts.</summary>
    private static bool DirectionDb(out Vector3 dir)
    {
        dir = default;
        if (!FrameInputs.HasSun) return false;
        Vector3 d = FrameInputs.SunDir;
        if (d.LengthSquared() <= 0f) return false;
        dir = Vector3.Normalize(d);
        return true;
    }

    /// <summary>The filter and the negation, apart from where the light came from: what the self-check pins.</summary>
    internal static bool DirectionOf(PartLight l, out Vector3 dir)
    {
        dir = default;
        if (l.IsNull) return false;
        if (l.Range < EngineLayout.DirectionalRange) return false;
        if (l.CastShadows == 0 || l.Kind != 0) return false;
        Vector3 d = l.Pos;
        if (d.LengthSquared() <= 0f) return false;
        dir = -Vector3.Normalize(d);
        return true;
    }

    /// <summary>The Alloy beam pass, right after the additive class and before the probe capture -- vanilla's
    /// own position (step 27, straight after bucket 4). Additive <c>(SRC_ALPHA, ONE)</c>, depth test on, depth
    /// mask off, exactly as <c>RenderBeams</c> sets it. Leaves GL_BLEND off and the depth mask on, which is
    /// the state the additive arm hands over and the probe capture after it needs.</summary>
    internal static void Draw(Matrix4x4* view, Matrix4x4* proj)
    {
        if (Disabled || _off || _count == 0 || Gl.DrawElementsBaseVertex == null) return;
        // The player's own switch, honoured the way grass honours "Grass Mode": beams off means the owned
        // pass draws nothing AND suppresses nothing, so the console variable keeps meaning what it says.
        if (_enable != null && *_enable == 0) return;
        // The frame's own sun (DbLights' area pick) is the only directional the database carries -- and an
        // interior has none, exactly as vanilla's own routine draws nothing there.
        if (!DirectionDb(out Vector3 dir) || !EnsureDbProgram()) return;
        // #261 retired the all-or-none rule: a beam that cannot be staged is skipped and its neighbours draw.
        int staged = 0;
        for (int i = 0; i < _count; i++)
            if (StageDb(i, staged)) _sBeam[staged++] = i;
            else _refused++;
        if (staged == 0) return;
        // Vanilla dusk writes (t - 1) * 0.05, a negative alpha; its magnitude is the (1 - t) * 0.05 that mirrors dawn.
        DrawDb(view, proj, in dir, _alpha != null ? MathF.Abs(*_alpha) : DefaultAlpha, staged);
        Gl.Disable(Gl.BLEND);
        Gl.DepthMask(1);
        _drawn += staged;
    }

    /// <summary>Context loss: drop every GL name (they mean nothing once the context is gone) and the
    /// captured world with them. <see cref="EnsureProgram"/> short-circuits on a non-zero <c>_prog</c> and
    /// the draw binds <c>_vVao</c> directly, so leaving these set makes the next context draw through a dead
    /// program and a dead VAO.</summary>
    internal static void ContextLost()
    {
        _progDb = 0; _dbFailed = false;
        _locDbModel = -1; _locDbView = -1; _locDbProj = -1; _locDbSrc = -1; _locDbZClip = -1; _locDbColor = -1;
        Forget();
    }

    /// <summary>Every captured identity is void: the parts belong to a Scene whose unload freed them.</summary>
    internal static void Forget()
    {
        _count = 0;
        _dbGen = -1; _dbScene = 0;
    }

    internal static void ResetPeriod()
    {
        _enrolled = 0; _refused = 0; _drawn = 0; _dbSkipped = 0;
    }

    internal static string Stats() => Disabled ? "beams off (/effects beams 0)" : _off ? "no beams (unavailable)"
        : $"beams {_count} captured from "
          + $"the scene database ({_owned} beaming parts in the scene)"
          + $", {_enrolled} enrolled, {_drawn} drawn"
          + (_refused == 0 ? "" : $", {_refused} REFUSED")
          + (_dbSkipped == 0 ? "" : $", {_dbSkipped} with NO RESIDENT GEOMETRY in the arena");

    /// <summary>Off-device: the two pieces of this file that are decisions rather than replays -- the light
    /// scan's predicate (miss it and the shafts point somewhere the volumes were never baked for) and the
    /// vertex stage's extrusion. Oracle for the second: the embedded GLSL text of both beam vertex stages,
    /// read through <see cref="ShaderSource"/>.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        int SceneBytes = EngineLayout.SceneLightManager + 8;   // a SIZE: the fake Scene must hold its manager slot
        int MgrBytes = EngineLayout.LmAreaDiffuse + 8;          // ...and the manager its area-diffuse slot
        int LightBytes = EngineLayout.LightDir + 12;            // ...and the light its position
        byte* scene = stackalloc byte[SceneBytes];
        for (int i = 0; i < SceneBytes; i++) scene[i] = 0;
        byte* mgr = stackalloc byte[MgrBytes];
        for (int i = 0; i < MgrBytes; i++) mgr[i] = 0;
        byte* b = stackalloc byte[LightBytes];
        for (int i = 0; i < LightBytes; i++) b[i] = 0;
        IntPtr s = (IntPtr)scene;
        var sc = new CX.Engine.Scene(s);
        sc.LightManager = new LightManager((IntPtr)mgr);
        new LightManager((IntPtr)mgr).AreaDiffuse = new PartLight((IntPtr)b);

        void Light(byte* l, float range, int live, int kind, float x, float y, float z)
        {
            var lv = new PartLight((IntPtr)l);
            lv.Range = range; lv.CastShadows = live; lv.Kind = kind;
            lv.Pos = new Vector3(x, y, z);
        }

        // SL5: the filter, over a light handed in directly -- the engine's own GetShadowLights(1) rule, which
        // is the whole of what the deleted Scene+0x60 walk applied.
        Light(b, 10f, 1, 0, 0f, 0f, 1f);
        check.Expect(!DirectionOf(new PartLight((IntPtr)b), out _), "a point light's range is too short to be the sun");
        Light(b, 100000f, 1, 0, 0f, 0f, 2f);
        check.Expect(DirectionOf(new PartLight((IntPtr)b), out Vector3 d) && d == new Vector3(0f, 0f, -1f),
          "the area directional past 1000 range wins, normalised and NEGATED");
        Light(b, 100000f, 0, 0, 0f, 0f, 2f);
        check.Expect(!DirectionOf(new PartLight((IntPtr)b), out _), "a light with the +0x74 flag clear is skipped");
        Light(b, 100000f, 1, 1, 0f, 0f, 2f);
        check.Expect(!DirectionOf(new PartLight((IntPtr)b), out _), "...and so is one whose +0x80 discriminant is non-zero");
        Light(b, 100000f, 1, 0, 0f, 0f, 0f);
        check.Expect(!DirectionOf(new PartLight((IntPtr)b), out _), "a zero direction is refused rather than normalised into a NaN");
        check.Expect(!DirectionOf(new PartLight(IntPtr.Zero), out _), "no area directional draws no beams -- which is every interior");

        // The vertex stage's extrusion, read out of the shader text: the frontal cap sits at the minimum
        // offset and a back-facing vertex extrudes until world z reaches the clip plane, with the minimum
        // as a floor. Getting the max() backwards inverts the shaft above the clip height, which is most of
        // an outdoor scene; losing the floor collapses the frontal cap onto the geometry it came from.
        foreach (string stage in new[] { "cx_beam_v.glsl", "cx_beamdb_v.glsl" })
        {
            string src = ShaderSource.Get(stage);
            check.Expect(src.Contains("const float CX_BEAM_MIN_OFFSET = 0.1;"),
              $"{stage} keeps the engine's fMinOffset of 0.1");
            check.Expect(src.Contains("float off = CX_BEAM_MIN_OFFSET;"),
              $"{stage} starts the frontal cap at the minimum offset");
            check.Expect(src.Contains("off = max(CX_BEAM_MIN_OFFSET, (uZClip - wp.z) / dir.z);"),
              $"{stage} extrudes a back-facing vertex down to uZClip, with the minimum as a max() floor");
            check.Expect(src.Contains("vp += off * (mat3(uView) * dir);"),
              $"{stage} displaces along the view-space extrusion direction");
        }

        log.Info(check.Ok ? "Beams.SelfCheck: passed" : "Beams.SelfCheck: FAILED");
    }

    // ---- shaders -----------------------------------------------------------------------------

    /// <summary>The vertex stage, ported from the client's own <c>vs_beamvol.shd</c>. Only the DIRECTIONAL
    /// branch survives: <c>RenderBeams</c> pushes <c>projectionSource.w = -1</c> unconditionally, so the
    /// point-light path is dead code at this call site. The fog term is dropped (ponytail: add it if a foggy
    /// exterior shows a shaft standing proud), and the stage carries no invariant clip expression.</summary>
    private static string BeamVs => ShaderSource.Get("cx_beam_v.glsl");

    /// <summary>The fragment stage: a flat tint, exactly as <c>fs_beamvol.shd</c> is once its fog term goes.
    /// No encode -- the Alloy colour target is LINEAR and the one whole-frame encode happens at the resolve;
    /// <c>uColor</c> is handed over already linearised.</summary>
    private static string BeamFs => ShaderSource.Get("cx_beam_f.glsl");
}
