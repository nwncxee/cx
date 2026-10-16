using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The SERVICE -- hook install, the frame anchor, the pass-generation clock, the iterator's
/// suppression answer and the end-of-frame report. Alloy draws the whole 3D world (ADR 0016); its lit program
/// is forward except for point lights, which a G-buffer twin and <see cref="DeferredLights"/> add (ADR 0029).
/// The frame path lives in the siblings: <c>.Table</c>, <c>.Enroll</c>, <c>.Frame</c>, <c>.Batches</c>.</summary>
public sealed unsafe partial class AlloyRenderer : IExtenderService
{
    internal const int LogEvery = 900;
    /// <summary>Frames before the FIRST period line. The 900-frame cadence is right for steady-state cost,
    /// but it is also the only proof this pass runs at all -- and at 900 a short session (or one that ends at
    /// a module unload) produces no line, which reads as "never armed" rather than "not yet". Report once
    /// early, then settle into the long cadence.</summary>
    internal const int FirstLogAt = 120;
    /// <summary>Ownership capacity; the surplus is not drawn, there being no engine walk behind a refusal. A
    /// ceiling that should not be reachable rather than a budget to tune: ~29x the parts a large stock area
    /// carries, ~400 bytes per slot (62.5MB resident). WHICH parts spill is a first-come-first-served hash
    /// walk, so ponytail: an area past even this flickers, and the fix is a deterministic near-to-far spill.</summary>
    internal const int MaxOwned = 163840, MaxVdos = 4096;
    /// <summary>Batch-sort texture keys: distinct diffuse textures the sort can separate, and the
    /// open-addressed slot table that interns them (power of two, ~2x MaxMats for a 0.5 load factor). Overflow
    /// shares the last key, which costs batch size and nothing else -- the live flush key is the texture id --
    /// but a saturated table makes that key a bucket every unseen texture shares, costing the sort its point.</summary>
    internal const int MaxMats = 16384, MatHashBits = 15, MatHash = 1 << MatHashBits;
    /// <summary>Floats per CxDraw record: std430 <c>mat4 + vec4 rot + vec4 amb + vec4 dif + vec4 emi + vec4
    /// pbr + vec4 spc + vec4 feat + mat4 prev + vec4 ext0..ext2</c> = 288 bytes, whose alignment (16) already
    /// divides the size, so the array stride is 288 with no padding.</summary>
    internal const int DrawFloats = 72;
    /// <summary>ADR 0026: where the three material-extension vec4s (<c>ext0..ext2</c>) start -- after
    /// <c>prev</c>, so no older field moved.</summary>
    internal const int ExtFloat = 60;
    /// <summary>ext2.z, a lane ADR 0026 leaves spare -- <c>_recArrive</c>, the diffuse's arrival time.</summary>
    internal const int ArriveFloat = ExtFloat + 10;
    /// <summary>ext2.w, the tile's fog-of-war term as HIDDEN (<c>1 - reveal</c>), so that <b>0 means
    /// explored</b>. The polarity is the whole point: a spare lane is zero in every record that does not write
    /// it, and carrying REVEAL made an unwritten 0 read as "never seen" -- the lit stage fogged the world to
    /// black.</summary>
    internal const int RevealFloat = ExtFloat + 11;
    /// <summary>Where <c>prev</c>, the previous frame's model matrix, starts in the record.</summary>
    internal const int PrevFloat = 44;
    /// <summary>Record slots the persistent buffer can hold -- one per ownership slot, plus the sky's reserved
    /// block immediately above them. The sky's parts are not in the ownership table (they are captured off the
    /// engine's matrix stack) and are restaged every pass, so they take a fixed block at the top.</summary>
    internal const int MaxRecords = MaxOwned + MaxSkyDraws;
    /// <summary>SSBO binding point of the per-draw block. Must match <c>DrawBufDecl</c>'s literal.</summary>
    internal const uint BindDraw = SsboSlots.Draw;
    /// <summary>SSBO binding of the Alloy pass's bone block; must match <c>BoneBufDecl</c>'s literal.</summary>
    internal const uint BindBones = SsboSlots.Bones;
    /// <summary>Last frame's bone block, read by the mover sweep alone.</summary>
    internal const uint BindBonesPrev = SsboSlots.BonesPrev;
    /// <summary>SSBO binding of the per-draw texture-handle block (8 uvec2 per record).</summary>
    internal const uint BindTexH = SsboSlots.TexH;
    /// <summary>SSBO binding of the per-DRAW index array -- draw n of this pass names the record slot it
    /// reads. Must match <c>DrawBufDecl</c>'s literal.</summary>
    internal const uint BindDrawIdx = SsboSlots.DrawIdx;
    /// <summary>Bones per skinned part, fixed by the engine: <c>GLRender::m_bones</c> is a 64 x mat4 static
    /// array and the vertex stream's indices address it directly. The matrices already sit in GLSL's
    /// column-major order (see <see cref="SymbolMap.GlRenderBones"/>), so a snapshot is a straight memcpy.
    /// </summary>
    internal const int BoneCount = 64, BoneFloats = BoneCount * 16;
    /// <summary>Skinned parts posed per frame, INITIAL capacity: 4KB each, so 512 slots is 2MB and covers a
    /// crowd. Not a ceiling -- the stage doubles rather than drop a creature, and the high-water mark is
    /// reported. <see cref="CasterPass"/> keeps it as a hard cap: a shadow that does not bake is a missing
    /// shadow, not a missing creature.</summary>
    internal const int MaxBoneSlots = 512;
    /// <summary>A parameter list longer than this is not a .mtr, it is a bad pointer -- refuse it rather than
    /// walk off into the heap. Real materials declare a handful.</summary>
    internal const int MaxParams = 64;
    /// <summary>Map slot 6 of the block, the ambient-occlusion map -- a baked visibility term in .r, sampled
    /// straight onto the ambient, sourced from <c>.mtr</c> <see cref="AoTexture"/> (texture11) past the
    /// engine's own vocabulary so it cannot collide with a community model's texture7. Probed on EVERY shared
    /// material, since no shader feature announces it.</summary>
    internal const int AoSlot = 6, MapSlots = TextureSlots.MapCount;
    /// <summary>The <c>.mtr</c> line the AO map is declared on -- the first extender-owned slot
    /// (<see cref="MaterialExtraSlots.First"/>). A stock client parses and drops it.</summary>
    internal const int AoTexture = MaterialExtraSlots.First;
    /// <summary>The AO slot's MASK bit -- 1 &lt;&lt; 7, deliberately NOT <c>1 &lt;&lt; AoSlot</c>. Every other
    /// slot's bit is its index, but bit 6 is <see cref="EnvSlot"/>'s and the lit stage is content-overridable:
    /// a hak shipped against an older build still reads 64 as "env-mapped", so taking 64 for AO would render
    /// every env material shiny-and-unreflective with nothing in the log.</summary>
    internal const int AoBit = 1 << 7;
    /// <summary>The record takes no shadow (<c>CX_SLOT_NOSHADOW</c>): an untextured blended sheet such as a door's
    /// <c>sam</c> transition sheen. Binds nothing.</summary>
    internal const int NoShadowBit = 1 << 8;
    /// <summary>The four PALETTE slots: <c>.mtr</c> <see cref="PalTexture0"/>..<c>texture10</c>, the engine's
    /// own last four material stages (it pins <c>texUnit7</c>..<c>texUnit10</c> to units 7..10 at link). Alloy
    /// has no unit left for them -- the lit stage spends 0..30 -- so they reach their one reader, the palette
    /// tint's hosted stage, as bindless handles in <see cref="Db.TintTerms"/>' row.</summary>
    internal const int PalSlots = 4, PalTexture0 = 7;
    /// <summary>The first palette slot's MASK bit; the other three follow it. Above
    /// <see cref="NoShadowBit"/>, and nowhere near <see cref="EnvSlot"/>'s frozen 64.</summary>
    internal const int PalBit0 = 1 << 9;
    /// <summary>Mask bit of palette slot <paramref name="k"/> (<c>texture7</c> is 0).</summary>
    internal static int PalSlotBit(int k) => PalBit0 << k;
    /// <summary>This material's COVERAGE is map slot 1's alpha, never the diffuse's -- what the content
    /// fragment stages <see cref="ShaderFeatures.CoverNrmFs"/> names discard on. Binds nothing: the bit only
    /// says which channel the cutout test reads, and slot 1 is bound already for its normal.</summary>
    internal const int CoverNrmBit = 1 << 13;
    /// <summary>Texture unit slot 1 binds on; the rest follow it. Units 0/1/2 are diffuse, sun cascades and
    /// the local cube array, and <see cref="EnvProbe.ProbeUnit"/> sits above all of them -- SelfCheck holds
    /// that ordering, because a samplerCube sharing a unit with a sampler2D kills every lit draw in silence.</summary>
    internal const uint PbrUnit0 = TextureSlots.MapBase;
    /// <summary>The env-map bit of the same mask (<c>Material::HasEnvMap</c>, mat+0x40). One past the bindable
    /// texture slots, and unlike them it binds nothing -- it only tells the fragment stage that this is legacy
    /// "shiny" content whose diffuse alpha is a reflectivity mask. FROZEN at 64: shipped
    /// <c>cx_ownlit_fs.shd</c> overrides carry a copy, so a new slot takes the next free bit, never this one.</summary>
    internal const int EnvSlot = 64;
    internal const uint GlUnsignedShort = 0x1403;   // not-an-offset: GL enum
    /// <summary>The GL blend factors <see cref="Issue.BlendGl"/> maps the Aurora ones onto.</summary>
    internal const uint GlZero = 0, GlOne = 1, GlSrcColor = 0x0300, GlSrcAlpha = 0x0302,   // not-an-offset: GL blend-factor enums
                       GlOneMinusSrcAlpha = 0x0303, GlDstColor = 0x0306, GlOneMinusDstAlpha = 0x0305;   // not-an-offset: as above
    /// <summary>Engine attribute locations, fixed for every engine program (Ghidra
    /// <c>GLRender::ConnectShaderData</c>: one glBindAttribLocation table before every link -- 0 vPos,
    /// 1 vTcIn, 2 vColor, 7 vNormal). They say which engine VAO slot holds each stream.</summary>
    internal const uint EngPos = 0, EngUv = 1, EngNormal = 7;
    /// <summary>And OUR locations for the same two streams, which the skinned vertex stages declare. Same
    /// numbers as the engine's by choice, not by necessity -- the Alloy VAO binds them itself.</summary>
    internal const uint LocBoneIdx = 8, LocBoneW = 9;
    /// <summary>Our own program's attribute locations. VAOs are ours, so the engine's locations only tell us
    /// which buffer/offset holds each stream -- we re-bind them wherever the program wants them.</summary>
    internal const uint LocPos = 0, LocUv = 1, LocNrm = 2;
    /// <summary>The mesh arena's two packed words: a tangent frame (<c>INT_2_10_10_10_REV</c>, xyz plus the
    /// handedness in the 2-bit lane) and vertex paint (normalized RGBA8). Location 3 is the lightmap chart
    /// (<see cref="MeshChart.LocChartUv"/>), so these take the next two free slots.</summary>
    internal const uint LocTangent = 4, LocPaint = 5;

    internal static ExtenderLog? _log;
    private static bool _armed;
    internal static MatGetTextureFn _getTexture;
    private static uint* _primTable;
    private static byte* _mtxStacks;

    /// <summary>Pass generation: bumped at every RenderSinglePass entry (main view, reflection subframes,
    /// each is its own pass). An Alloy draw stamps it into AlloyCensus's OwnGen column; the iterator
    /// suppresses a part only when the stamp matches the pass it is walking.</summary>
    internal static int PassGen;
    /// <summary>Did the Alloy world block take THIS pass? The sky reads it to tell the main world pass from a
    /// reflection subframe -- the same <c>RenderSkyBoxes</c> routine under a mirrored camera, where capturing
    /// would position next frame's dome at the reflection eye -- and so whether its capture window may open.</summary>
    internal static bool DrewThisPass => _drawnPass == PassGen;

    private static bool _prepassFeat => Renderer.PrepassFeat.V;
    /// <summary><c>/alloy prepass 0</c>: skip the prepass, which is the A/B for its own acceptance number.</summary>
    private static bool _depthPrepass => Renderer.Prepass.V;
    /// <summary><c>/alloy depthequal 0</c>: prepassed opaque/cutout batches draw LEQUAL with depth writes, as
    /// before -- the A/B for <see cref="Issue._matchDepth"/>.</summary>
    private static bool _depthEqual => Renderer.DepthEqual.V;
    /// <summary>The lit program's uniform locations, ONE struct per program: the lit stage is built twice --
    /// <see cref="Programs._progLit46"/> from the hosted regions' stubs, which every ordinary lit batch draws
    /// with, and <see cref="Programs._progLitHost"/> with the content bodies in, bound only for a batch whose
    /// feature <see cref="ShaderFeatures.Hosted"/> names. A content override leaves newer terms at -1.</summary>
    internal struct LitLoc
    {
        public int Oit, OitDim, OitGob, LightRecOn;
        public int Decal, DecalZ;   // CX-273: the decal layer's mode and the projection terms its depth test reads
        public int Solid;           // a wash's solid interior (Issue._solidPass)
        public int AlphaMip;   // CX-191
        public int Tex, Vp, Base, Cut, Viz, Ssao, SsaoOn, SsaoHalf, AoDirect, AoCutout,
                   SunTex, SunDynTex, CubeTex, CubeCount, CubeDynTex,   // CX-43: the rest of the receiver and the light curve ride CxPass
                   Probe, Capture, MirrorZ,          // MirrorZ: CX-51 the planar mirror's plane
                   Weather, WeatherLook, CoverVp, CoverLayer, SnowLook, SnowLight, SnowPom, CoverEsm,
                   WxMapXf, WxMapP,                    // R2: the area wetness map's fit and look
                   WxEdge, WxRun, WxCache,             // R5: the shelter kernel, the run-off / shoreline terms, the shelter cache flag
                   LmPages, LmMovers,                  // #674
                   LightOcc,                           // light.hidden
                   SkyVp, SkyTerm,                     // CX-290: the overhead sky view's fit and look terms
                   Material;                           // ADR 0026: light.brdf / ambientdir / specaa
        public FeatLoc Feat;
    }
    /// <summary>light.hidden: the roughness under which a surface's highlight is a mirror image of the source
    /// (a puddle 0.12; wet ground's 0.35 is not) and takes the source's shadow whatever the cube cap says; a rougher
    /// lobe keeps the lightmap's, which only the pages have exact. Faded in from half of it (CxGlossW).</summary>
    internal const float GlossyRough = 0.25f;
    /// <summary>THIS pass's eye, from the inverse of the view matrix the pass is drawing with. Never
    /// <c>FrameInputs.CameraPos</c> -- that belongs to whichever pass set a view last -- and taken per pass
    /// rather than from the scene latch, so a reflection subframe reflects from its own camera.</summary>
    private static Vector3 _eyePos;
    /// <summary>This pass's camera, for anything drawing inside the colour bracket with its own program.
    /// Latched by <see cref="DrawOwned"/> from the inverted view matrix -- never <c>CurrentCamera</c>, which
    /// at present time is the UI frame's camera.</summary>
    internal static Vector3 EyePos => _eyePos;

    /// <summary>The PC's world position, straight off the engine's own <c>m_shaderExposedRuntimeData</c> --
    /// the same value its shaders read as <c>playerPosition</c>. Zero before the struct resolves, which for
    /// the grass displacement window just means it stays at the origin until it does.</summary>
    internal static Vector3 PlayerPos => _runtime == null ? default : Rt.PlayerPos;
    /// <summary>The module calendar the runtime block carries, what the shaders read as <c>moduleHour</c> and
    /// friends. Zero before the struct resolves.</summary>
    internal static int ModuleHour => _runtime == null ? 0 : Rt.ModuleHour;
    internal static int ModuleDay => _runtime == null ? 0 : Rt.ModuleDay;
    internal static int ModuleMonth => _runtime == null ? 0 : Rt.ModuleMonth;
    internal static float ModuleTimeIntoTransition => _runtime == null ? 0f : Rt.ModuleTimeIntoTransition;
    /// <summary>The presented-frame counter, what a hosted content chain gets as <c>sceneCurrentFrame</c>.</summary>
    internal static int FrameSeq => _frameSeq;
    /// <summary>The forward class's light-source view: the one viz mode the deferred pass keeps running for.</summary>
    internal const int VizLightFrom = 52;
    /// <summary>Highest mode <c>/alloy viz N</c> accepts; each mode is documented where it branches, in
    /// <see cref="OwnedLitFsMain"/> and the water stage. Raise it when a stage adds one, or the new mode is
    /// silently clamped at the parse and the screenshot is of the old view.
    /// <see cref="FrameSnapshot.SelfCheck"/> asserts the water stage's highest view against this.</summary>
    internal const int LightVizMax = 54;   // 54 = the CX-294 core/fringe split (green core, magenta fringe at its coverage, grey other cutouts); 53 = the CX-273 decal layer folded into each surface (white = all decal); 52 = a forward fragment's light source (green deferred record, yellow baked walk, red unbaked walk); 51 = the hot-light path per fragment (red tested, green ray hit); 40 = the #493 vis-ID target (IdTarget.VizMode); 41/42/45/46 = SSR trust, the reflection, the probe behind it and the mirrored draw (CX-51); 43/44 = the #674 lightmap term and its texel grid; 47..50 = CX-117's PLT forensics
    internal static int _lightViz => Renderer.Viz.V;
    /// <summary><c>/alloy aotest 1</c>: bind every lit part's own DIFFUSE texture on the AO slot and set the
    /// bit, so the whole AO path -- mask, unit, sampler, ambient multiply -- runs end to end on stock content.
    /// The frame looks WRONG on purpose (a diffuse's red channel is not an occlusion map); what it proves is
    /// that a material which does declare one will be sampled.</summary>
    private static bool _aoTest => Renderer.AoTest.V;
    /// <summary>This draw is going into the <see cref="EnvProbe"/> cube, not onto the screen. The lit and
    /// water stages read it, so a capture never samples the probe -- which is an attachment of the framebuffer
    /// being drawn into. It says nothing about COLOUR SPACE: the screen target is linear too, so the capture
    /// and the frame want the same values.</summary>
    internal static bool _capturing;
    /// <summary>The capture in progress is the mirrored draw of the planar reflection, not the probe:
    /// creatures stay in and every program writes its alpha (see <see cref="PlanarPass"/>).</summary>
    internal static bool _planar;
    /// <summary>The planar mirror's world z while <see cref="_planar"/> is set: its draw drops what lies under it.</summary>
    internal static float _mirrorZ;
    internal static bool Capturing => _capturing;
    /// <summary>GL_BLEND as the pass last set it, so <see cref="Issue.IssueBatches"/>' per-batch toggle starts from
    /// the truth instead of assuming.</summary>
    internal static bool _blendOn;
    /// <summary>Does the area declare an environment map at all? Read off the enrolled Scene each pass.
    /// <c>RenderFlat</c> @0x450eb0 binds <c>currentscene+0x1e8</c> to the env sampler only where that field is
    /// set, and most areas leave it null -- so where it is, ambient specular is a term vanilla does not have
    /// and only genuinely env-mapped materials get the probe.</summary>
    private static bool _sceneEnv;
    /// <summary>The driver has no glMultiDrawElementsBaseVertex or no glBindBufferBase, so batched submission
    /// cannot run at all. Latched, and EndFrame disarms on it by name rather than drawing nothing forever
    /// while the iterator keeps suppressing parts.</summary>
    private static bool _batchUnavailable;
    /// <summary><c>/alloy light 0</c> forces every Alloy part back to the fullbright variant (the lighting A/B).</summary>
    internal static bool _lighting => Renderer.Light.V;
    /// <summary>RenderInterface::IsEnabled -- queried for cap 5 (LIGHTING) once per Alloy pass.</summary>
    private static RiIsEnabledFn _isEnabled;
    /// <summary>The AuroraEnableEnums value for lighting. <c>MatStatic</c>/<c>RenderFlat</c> both branch on
    /// it, so a pass that runs with it off wants the flat variant for everything.</summary>
    internal const int CapLighting = 5;

    // lifecycle
    private static int _tableGen = -1, _sinceEnroll;
    private static readonly EdgeState _was = new();

    /// <summary>The <see cref="Counters._cutBy"/> labels, by index.</summary>
    private static readonly string[] CutTerms = { "vao", "feat", "dissolve", "blend", "cull", "prim", "lit", "tex" };
    private static bool CutCensusSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(CutTerms.Length == Counters.CutTermCount, "CutTerms labels every _cutBy slot");
        return check.Ok;
    }
    private static string CutCensus()
    {
        var sb = new System.Text.StringBuilder();   // cold: the period line
        for (int t = 0; t < CutTerms.Length; t++)
            sb.Append(t == 0 ? "" : ", ").Append(CutTerms[t]).Append(' ').Append(((float)Counters._cutBy[t] / Counters._frames).ToString("F0"));
        return sb.ToString();
    }
    /// <summary>Name a diffuse the bindless guard turned away, once per distinct resref.</summary>
    private static void TexSkipDiag(bool owned, int tid, TextureRef aur)
    {
        if (Diag._texSkipDiagLeft <= 0) return;
        string nm = aur.ResolveName();
        if (!Diag._texSkipSeen.Add(nm)) return;
        Diag._texSkipDiagLeft--;
        _log?.Info($"AlloyRenderer: [texskip] \"{nm}\" tid={tid} owned={owned} -- "
                   + (owned ? "Alloy owns it but GetTextureHandleARB minted nothing"
                            : "never reached the upload funnel, so it is BORROWED and bindless will not vouch for it")
                   + "; #285: every lit part using it is NOT DRAWN (bindless=0 in cx.cfg is the A/B)");
    }
    /// <summary>Monotonic presented-frame counter: the light SSBO uploads once per frame, not once per pass.</summary>
    private static int _frameSeq;

    private static bool _takeOverflow;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        // #165: ahead of every stand-down and every check below -- /alloy reports the renderer for a
        // misrendering session, so it must exist even in a session whose renderer never armed.
        RenderCommands.Register();
        // Up here with the other unconditional checks, ahead of every stand-down: the scenery predicate and
        // the record contract every path submits from are ours and depend on no engine symbol, so a build
        // that never arms still reports whether the arithmetic it ships is right.
        StaticSelfCheck(log);
        CullPass.Init(log);
        StreamRing.Init(log);
        // #170: ahead of every stand-down, matching the host's unconditional calls. The verdict must
        // outlive a renderer that never armed -- /alloy is the report it drives.
        AlloyGate.Arm(log);
        PresentTarget.Attach(log);
        TriggerHighlight.Attach(log);
        HighlightGlow.Attach(log);
        // #263: the config half of the verdict is the FIRST stand-down, ahead of every Attach and every
        // Install. Steps 4-7 replace engine paths rather than watching them -- Alloy performs the pool
        // upload, mints the texture handles, forces PLT compositing to the CPU -- so arming them in a
        // session the engine is going to render is not a wasted hook, it is a broken world. Restart-only
        // (ADR 0018's amendment to ADR 0010) is exactly what buys the two worlds never being warm at once.
        if (AlloyGate.OptedOut)
        { log.Info("AlloyRenderer: stood down (alloy.enabled=0) -- the engine owns every resource this session"); return; }
        // CX-120: ahead of every stand-down below, because the feed's own refusals have to have a log to be loud
        // in even in a session that arms nothing.
        DbFeed.Attach(log);
        // The recipe table's one lifetime edge: without this subscription nothing retires a recipe, so the
        // model cache can never free a model and the texture cache can never evict a slot. It had no caller.
        DrawRecipe.Attach();
        DbEmitterChannels.Attach();   // seam-anim's emitter tracks land somewhere instead of being counted
        DbArmPass(log);
        FrameRegistry.Global.Register(FramePhase.PostPresent, 20, EndFrame, "AlloyRenderer.endFrame", log);
        // #175: the whole pass roll-call, in one walk over one declared table. Attach is ahead of every check
        // because #129's asserts that the ownable-bucket list AGREES with whether Alloy water can draw -- and
        // water.enabled off is exactly the case where it must, so water's knobs have to be read first.
        RenderPasses.Register(log);
        PassRegistry.SelfCheck(log);
        PassRegistry.Global.Attach(log);
        // #211: the retired-texture deletes need a context, not a draw. Hung on the health bracket so they
        // still happen while Alloy is stood down (main menu, the disarm latch).
        // #212: and the geometry deletes with them, for exactly the same reason -- the colour bracket's own
        // Drain never runs in those states, so a revoke there would hold the whole owned set for the session.
        GlContextService.InBracket = static () =>
        {
            OwnedTextures.Drain(); SampleVram();
            LightmapBake.ReleaseIdle();
            // CX-117: the registry's uploads and bindless mints, the master palette's upload and the
            // per-record PLT rows -- all of them belong in this context and nowhere else, and all of them run
            // in BOTH modes. The registry is not DB mode's alone (CX-119's bitmap font atlases acquire from it
            // on an engine-scene session, and gating the drain left them loading for ever); the row block must
            // be bound either way because cx_plt.glsl is part of the lit stage either way and an unbound
            // shader-storage binding is an undefined read; and the palette upload is a no-op until something
            // has loaded a slot. Each is two queue tests when there is nothing to do.
            Db.TexRegistry.Drain();
            Db.PltColors.Upload();
            Db.TintTerms.Upload();
            Db.PltPalettes.Create();
            // CX-117's GPU check: idle unless alloy.pltverify armed it, and it runs in BOTH modes -- what it
            // measures is the shipped GLSL, which does not care which scene layer produced the record.
            if (Db.PltVerify.On) Db.PltGpuCheck.Tick();
        };
        // #117: ahead of every stand-down -- a broken pass order is a misrender in BOTH bracket sites, so the
        // one service that happens to host the check must not be able to opt the check out.
        if (!FrameGraph.SelfCheck(log)) return;
        // #126: log-only. A wrong classification is a wrong-looking frame, not a wrong-drawing one -- the
        // one-owner invariant is upheld by the bucket list, which the pass checks assert against.
        AlloyClasses.SelfCheck(log);
        // #215: here rather than at the shim's own install, which a client without the engine symbols never
        // reaches -- the two things it checks are ours and are wrong in the same way on every client.
        RspShim.SelfCheck(log);
        // CX-85: the ported light-fade integrator, up here for the same reason -- pure arithmetic, wrong in
        // the same way on every client, and the shim's install is not reached without the engine symbols.
        ShaderStages.SelfCheck(log);
        ShaderSource.SelfCheck(log);
        TriggerHighlight.SelfCheck(log);
        HighlightGlow.SelfCheck(log);
        GuiCapture.SelfCheck(log);
        GuiModelSolve.SelfCheck(log);
        Quality.SelfCheck(log);
        GuiStream.SelfCheck(log);
        NuiFlush.SelfCheck(log);
        // ADR 0012: the dump is how a customizer gets a current starting point. Ahead of every stand-down
        // below, so a session that owns nothing can still produce one.
        ShaderStages.DumpIfConfigured(log);
        // Every pass's own check. Advisory by convention -- the checks that GATE arming are the contract ones
        // below, which the passes do not own.
        PassRegistry.Global.RunSelfChecks(log);
        // #179: CX_OWNDRAW is gone rather than moved -- the vanilla setting is the same switch, and it
        // stands the shadow and residency sides down too (#263).
        // never arm on a broken contract: the misrender either predicts must not ship
        // #242: the split joins that chain for the same reason -- a record that stopped carrying a
        // per-frame field renders stale state and says nothing at all.
        // #571: the stock-settings reader forges a std::string for the engine; a header packed wrong is a
        // crash on the first world frame, not a misrender, so it joins the gating chain.
        StockConfig.Attach(log);
        if (!CutCensusSelfCheck(log) || !SortSelfCheck(log) || !CullSortSelfCheck(log) || !SplitSelfCheck(log) || !CasterBoundSelfCheck(log) || !MtrParams.SelfCheck(log) || !ChaseSelfCheck(log) || !KeyholeBoxSelfCheck(log) || !SphereRectSelfCheck(log)
            || !AreaGrid.SelfCheck(log)
            || !DbSelfChecks(log)
            || !Issue.RunsSelfCheck(log) || !StreamRing.SelfCheck(log)
            || !ShaderContract.SelfCheck(log)
            || !StockConfig.SelfCheck(log)) return;
        // NOT in the chain above: those two guard what reaches the screen, and this one guards a debug dump.
        // A regression in the canonicaliser should cost /alloy framedump, not drop every user back to vanilla.
        FrameDump.SelfCheck(log);
        // #128: same rule. A wrong gob offset draws a creature at the wrong alpha and a leaky refused-gob set
        // half-owns one -- both misrenders, neither of which errors.
        if (!AlloyCensus.SelfCheck(log)) return;
        // CX-89: same rule, and the failure is worse than a misrender -- a wrong door fade freezes Gob+0x10c and
        // Gob::HitCheck then refuses the click. A failed check keeps the engine's own FadeDoors by keeping the
        // whole Alloy path down.
        if (!DoorFade.SelfCheck(log)) return;
        if (PlaceableHold.SelfCheck(log)) PlaceableHold.Install(log, hooks);   // failed: placeables fade as vanilla
        _getTexture = EngineFn.MatGetTexture;
        _primTable = (uint*)Native.Sym(SymbolMap.PrimTypeTranslationTable);
        if (CycleTextures.SelfCheck(log)) CycleTextures.Attach(log, hooks);   // #331; a failed check freezes cycles, not Alloy
        Db.SunGlide.Install(log, hooks);
        _mtxStacks = (byte*)Native.Sym(SymbolMap.MAurMtxStack);
        _runtime = (byte*)Native.Sym(SymbolMap.GlShaderRuntimeData);
        if (_runtime == null)
            log.Info("AlloyRenderer: GLRender::m_shaderExposedRuntimeData unresolved -- #123's feature " +
                     "switches stay inert (no wind sway, roofs where authored), and #124's camera dissolve " +
                     "with them -- uKeyholeCam reads this struct too");
        _shaderConstF = (float*)Native.Sym(SymbolMap.GlGlobalShaderConstantsF);
        _shaderConstI = (int*)Native.Sym(SymbolMap.GlGlobalShaderConstantsI);
        _shaderConstV = (float*)Native.Sym(SymbolMap.GlGlobalShaderConstantsV);
        _shaderConstV4 = (Vector4*)Native.Sym(SymbolMap.GlGlobalShaderConstantsV4);
        // #346: the targeting shape degrades the same way the dissolve does -- unresolved leaves uTgtMode
        // at the "nothing to draw" default, which is a missing overlay, not a misrender.
        if (_shaderConstI == null || _shaderConstV == null || _shaderConstV4 == null)
            log.Info("AlloyRenderer: GLRender::m_globalShaderConstants{i,v,v4} unresolved -- the spell " +
                     "targeting shape stays undrawn on Alloy parts");
        // #124 degrades one term at a time and says which: no fog block leaves Alloy parts unfogged (the
        // pre-#124 look), no constants leaves the dissolve aperture at zero, i.e. no dissolve. Neither is a
        // misrender, and neither is worth standing the pass down for.
        // The dissolve needs BOTH the diameter pair and the runtime struct's camera terms: without the
        // latter uKeyholeCam stays (0,0), the fragment offset never goes negative, and the aperture never
        // opens. Naming only one of them would send the next reader to the wrong symbol.
        if (_shaderConstF == null || _runtime == null)
            log.Info("AlloyRenderer: " +
                     (_shaderConstF == null ? "GLRender::m_globalShaderConstantsf" : "GLRender::m_shaderExposedRuntimeData") +
                     " unresolved -- #124's camera dissolve stays off on Alloy parts");
        if (_getTexture == null || !AurTex.Ready || _primTable == null || _mtxStacks == null)
        { log.Info("AlloyRenderer: engine symbols unresolved -- Alloy world pass unavailable"); return; }

        // #336: the highlight SOURCE. Cold -- it fires on a mouseover edge or a VFX starting, never per
        // frame -- and it is the only place the channel that produced a highlight is visible.
        Highlight._origRefreshHilite = hooks.InstallRefreshHilite(&Highlight.OnRefreshHilite);
        if (Highlight._origRefreshHilite == null)
            log.Info("AlloyRenderer: RefreshHilite unhooked -- no highlight claims a source, so every"
                     + " highlight keeps the engine glow and the owned outline draws nothing");
        // the item-level vertex-dirty signal
        _commitSlot = hooks.ChainVdoCommitItemDynamicData(&OnCommitItemDynamicData, CommitPriority);
        if (_commitSlot == null) log.Info("AlloyRenderer: CommitItemDynamicData unhooked -- vertex-dirty stays per pool buffer");
        // #137: the skinmesh class. Config bucket (ADR 0013): a capability gate that decides what the vertex
        // stages are BUILT from, so it cannot move mid-session and is not a command. The pose math is
        // DbAnim's, checked by its own self-check.
        _skinning = Renderer.OwnSkin.Boot;
        _isEnabled = EngineFn.RiIsEnabled;
        OccludeInit(log);   // #537
        // Hook install is its own walk, and it stays HERE rather than joining Attach above: a pass that hooks
        // the engine on a client where the Alloy renderer cannot arm would be suppressing draws nothing
        // replaces. Everything above this line has already returned in that case.
        PassRegistry.Global.Install(log, hooks);
        // #215: last, and after PassRegistry -- the shim calls through hook sites every pass above it owns
        // (the beam window, the emitter suppression, the bucket gate), so they have to be installed first.
        RspShim.Install(log, hooks);
        _armed = true;
        // CX-95: after the shim, whose two funnel hooks are where a GUI scene's draws are captured.
        if (_armed && RspShim.Armed) GuiPass.Install(log, hooks);
        // After GuiPass, so the nk_sdl_render chain runs panels (40), CX windows (20), NUI (1) with Alloy off.
        if (_armed && RspShim.Armed) NuiFlush.Install(log, hooks);
        if (_armed) log.Info(_skinning
            ? "AlloyRenderer: #262 skinmeshes owned -- posed per frame from engine animation state, no draw re-entry (alloy.ownskin=0 in cx.cfg disables)"
            : "AlloyRenderer: skinmeshes stay engine-drawn (cloaks, robes, tails)");
        log.Info(_armed
            ? "AlloyRenderer: armed -- Alloy world pass at the CX-89 frame anchor, called from the shim (alloy.enabled=0 in cx.cfg disables)"
            : "AlloyRenderer: the RenderSinglePass hook failed -- Alloy world pass unavailable");
    }

    /// <summary>Boot worker count for the stage: six, xtul's call. Live-verified 2026-09-05 on 12 cores: audit
    /// 0/0/0/0/0, Build 2,6 -> 1,9ms. A woken worker spins up to 4ms, so on a machine with fewer than eight cores
    /// this oversubscribes; <c>/alloy threads N</c> is the dial.</summary>
    internal const int DefThreads = 6;
    /// <summary>Does the prepass run its keyhole sweep? See <c>/alloy prepasskey</c>.</summary>
    private static bool _prepassKey => Renderer.PrepassKey.V;

    /// <summary><c>scriptableInt1..16</c> and <c>scriptableFloat1..16</c> out of the engine's own constant
    /// tables, exactly what <c>UploadVanillaEnv</c> writes. Cold; the input thread reading ints the render
    /// thread also only reads.</summary>
    internal static unsafe string DescribeScriptables()
    {
        if (_shaderConstI == null || _shaderConstF == null) return "scriptables: no table";
        var sb = new System.Text.StringBuilder("scriptableInt");
        for (int i = 0; i < ScriptableSlots; i++) sb.Append(' ').Append(i + 1).Append('=').Append(_shaderConstI[SIntBase + i]);
        sb.Append("  scriptableFloat");
        for (int i = 0; i < ScriptableSlots; i++) sb.Append(' ').Append(i + 1).Append('=').Append(_shaderConstF[SFloatBase + i].ToString("F2"));
        return sb.ToString();
    }

    /// <summary>The <c>alloy.consts</c> button: the scriptable block as the client table holds it NOW -- the
    /// numbers a hosted content stage gates on (snow, wet, rain).</summary>
    internal static void LogScriptables() => _log?.Info("AlloyRenderer: " + DescribeScriptables());

    /// <summary>The terms <see cref="AlloyGate"/> reads out of this class. <c>_armed</c> is "the hooks
    /// installed"; the capability pair is "a program built and the multidraw entry points exist", both of
    /// which latch and are deliberately never cleared by a context loss (see <see cref="ContextLost"/>).</summary>
    internal static bool ArmedForFrame => _armed;
    internal static bool DrawCapable => !Programs._progFailed && !_batchUnavailable;

    /// <summary>Inside the world view's colour pass (<see cref="DbWorldPass"/>), which is the only place
    /// Alloy draws: nothing of Alloy's runs inside the engine's own RenderSinglePass.</summary>
    private static bool _inColourPass;
    /// <summary>Scene the ownership table was enrolled from. The Alloy block draws only into a bucket walk of
    /// THIS Scene, which is what makes one-owner true by construction rather than by the colour-pass gate
    /// alone. An area transition that replaces the Scene leaves it stale, so nothing draws until the next
    /// enrollment picks up the new one -- which the walk's Scene check triggers on the same frame.</summary>
    internal static IntPtr _enrollScene;
    private static int _drawnPass = -1;   // pass whose Alloy block already ran (once per pass)

    /// <summary>Ownable parts the last enrollment turned away for want of a slot. Non-zero means the area is
    /// past <see cref="MaxOwned"/>, and there is no vanilla walk to spill to: a spilled part is INVISIBLE.
    /// The first number to read when geometry is missing.</summary>
    private static int _capacitySpill;

    /// <summary>The capacity clause of the [alloy] line -- silent when there is room, loud when there is not,
    /// because "4082/5631" only reads as a bug if you happen to know what MaxOwned is.</summary>
    internal static string CapacityNote()
        => _capacitySpill == 0 ? "" :
           $" (AT CAPACITY: {_capacitySpill} ownable parts spilled, MaxOwned={MaxOwned} -- since #264 nothing "
         + "else draws the world, so those parts are NOT RENDERED AT ALL, and the spill set re-picks itself "
         + "every enrollment, so which geometry is missing changes as you watch)";

    /// <summary>This pass's context-loss slot. Every GL NAME here belongs to the 4.6 context, so nothing is
    /// deleted -- the objects went with it -- and the names are dropped so each Ensure rebuilds rather than
    /// short-circuiting on a handle and drawing through a dead one. <c>Programs._progFailed</c> and
    /// <c>_litFailed</c> are NOT cleared: a stage that could not build is a fact about the source.</summary>
    internal static void ContextLost()
    {
        Programs._prog46 = 0; Programs._progLit46 = 0; Programs._progLitHost = 0; Programs._progLitList = 0; Programs._progLitDef = 0; Programs.ForgetVariants(delete: false); Programs._progLitMask = 0; OitPass.ListsArmed = false; Programs._progDepth = 0; Programs._progDepthKey = 0; Programs._progDepthFeat = 0; Programs._progWater = 0;
        Ablation.Forget(delete: false);
        Highlight.HighlightContextLost();
        TriggerHighlight.ContextLost();
        HighlightGlow.ContextLost();
        _drawBuf = 0; _texHBuf = 0; _boneBuf = 0;
        MotionContextLost();
        DbFeed.ContextLost();   // R1-30: the feed's own GL-backed tables
        // #242: ...and the persistent records went with the buffers that held them. Both halves matter -- the
        // capacity so the next upload reallocates rather than writing into a dead name, and the epoch so
        // every record is rebuilt rather than assumed still on the GPU.
        Issue.ContextLost(); StreamRing.ContextLost(); _recCapGpu = 0; InvalidateRecords();
        // ...and the VAO cache. RevokeAll marks the slots stale, and EnsureVao DELETES a stale non-zero name
        // before rebuilding -- which on a name minted by a context that no longer exists is a delete aimed at
        // whatever the next context recycles that number for.
        // #136: the engine-layout VAO cache goes with it. Same rule, same reason -- a container object is
        // not shared, so every name in it belonged to the context that just went.
        Programs._waterFailed = false;
        // #257: and so is the target latch -- every name in it belongs to the context that just went.
        ForgetTarget();
        DepthResource.Revoke();
        CullPass.ContextLost();
        PresentTarget.ContextLost();   // #456: names only -- the images died with the context
        GuiPass.ContextLost();
        _nullSunShadow = 0; _nullCubeShadow = 0;
        BlendProfile.ContextLost();
        PassContextLost();
    }

    /// <summary>Frame tick from the present (after the bucket walks). Module unload, a PartTable
    /// wipe and a lost 4.6 context all invalidate every recorded identity wholesale.</summary>
    public static void EndFrame()
    {
        // CX-109: the per-present frame edge the world-camera sighting is stamped against. HERE rather than in
        // GuiPass, which only installs when the GUI capture arms -- a sighting that never expires would leave
        // a freed camera readable after a module unload.
        AlloyCamera.NewFrame();
        BenchTick();
        // CX-110: and the wind's own, which ages its gusts on a frame that never reached a world
        // Scene::Render (the main menu) -- see AlloyWind's remarks.
        AlloyWind.NewFrame();
        if (!_armed) return;
        _frameSeq++;
        // Disarming must never be silent: the [alloy] period line is the only evidence this pass runs at
        // all, so its ABSENCE has to name a reason rather than read as "no news". RevokeAll only speaks when
        // something was owned, which is exactly not the case when the program failed before the first enroll.
        if (Programs._progFailed || _batchUnavailable)
        {
            RevokeAll(Programs._progFailed ? "program failed" : "multidraw unavailable");
            _armed = false;
            _log?.Info("AlloyRenderer: DISARMED (" + (Programs._progFailed
                           ? "program build failed"
                           : "glMultiDrawElementsBaseVertex or glBindBufferBase missing") +
                       ") -- the Alloy world pass is off for this session and no [alloy] lines will follow");
            return;
        }
        bool lost = _was.Available && !GlContextService.Available;
        _was.Available = GlContextService.Available;
        if (!GlContextService.Available) RevokeAll("4.6 context lost");
        // On the edge only: the owners' reset allocates, and the context stays lost for the session.
        if (lost) PassRegistry.Global.ContextLost();
        bool inModule = GameGate.InModule;
        if (_was.InModule && !inModule) { RevokeAll("module unload"); Programs._spliceStale = true; OwnedTextures.ModuleUnloaded(); StockConfig.ModuleUnloaded(); AlloyEnvMap.ModuleUnloaded(); DbFeed.ModuleEdge(_log); }
        _was.InModule = inModule;
        // #571: the stock graphics snapshot is retaken per world scene (boot, every area), and a toggle the player
        // flips off after the decision froze has nothing behind it (exclusive residency): the gamma precedent's Fail.
        if (inModule && !DbPassScene.IsNull)
        {
            StockConfig.OnScene((IntPtr)DbPassScene.Value);
            if (AlloyGate.Alloy && !StockConfig.EnhancedLighting)
                AlloyGate.Fail("Enhanced Lighting was turned off in Game Options (graphics.lighting.enhanced) and Alloy has no vertex-lit path");
        }

        // the overflow counter lives in a GL buffer, so its readback has to happen inside the 4.6 bracket:
        // arm it one frame ahead of the log so the value the log prints is this period's
        int period = Diag._loggedOnce ? LogEvery : FirstLogAt;
        if (Counters._frames == period - 2) _takeOverflow = true;
        if (++Counters._frames < period) return;
        Diag._loggedOnce = true;
        PeriodReport();
        CullPass.ResetStats();
        Counters._frames = 0; Counters._draws = 0; Counters._batches = 0; Counters._revokes = 0; Counters._enrolls = 0;
        Counters._twoSidedDraws = 0; Counters._cullCuts = 0; Array.Clear(Counters._cutBy);
        Counters._drawsGob = 0;
        Counters._noDiffuseDraws = 0; Counters._skipTexNoId = 0; Counters._skipTexNoHandle = 0; Counters._skipTexNotOwned = 0;
        Counters._nullTexDraws = 0;
        Counters._skinDrawn = 0; Counters._skinNoPose = 0; Counters._boneHigh = 0;
        SkyBox.ResetPeriod();
        GrassField.ResetPeriod();
        Beams.ResetPeriod();
        Halos.ResetPeriod();
        Weather.ResetPeriod();
        Particles.ResetPeriod();
        EmitterSim.ResetPeriod();
        Counters._watchSubmitted = 0;
        Counters._skipNoLight = Counters._skipIdentity = Counters._skipNoMdl = Counters._skipTex = Counters._skipRange = Counters._skipVao = 0; Counters._skipLod = 0;
        Counters._skipForeignScene = 0;
        ResetChasePeriod();
        Counters._ownDissolve = 0; Counters._ownFade = 0; Counters._skipFogTile = Counters._skipFogGob = 0;
        Counters._recWritten = 0; Counters._recStable = 0; PassBinds = 0; PassUploads = 0; PassBindsBorrowed = 0; Issue.WholeUploads = 0; Issue.WholeBytes = 0;
        Counters._nrmDeclared = Counters._nrmBound = 0;
        Array.Clear(Counters._drawsByClass);
        Counters._clusterOverflow = 0;
        Counters._prepassDraws = 0; Counters._prepassSkipped = 0; Counters._prepassKeyed = 0; Counters._prepassFeatDraws = 0; _qSamples.Reset(); _qPartSamples.Reset(); _qBlendSamples.Reset(); _qBlendPrims.Reset(); _qBlendFrags.Reset(); _blendGroups = 0; _blendFrames = 0;
        for (int i = 0; i < _qTerm.Length; i++) _qTerm[i].Reset(); _spanSum = 0; _spanN = 0;
    }

    /// <summary>R1-24: the scene database's checks. They GATE the renderer: a wrong answer is the whole world
    /// drawn wrong.</summary>
    private static bool DbSelfChecks(ExtenderLog log)
        => DbGrid.SelfCheck(log) & DbFeed.SelfCheck(log) & DbInputs.SelfCheck(log) & DbGui.SelfCheck(log)
           & DbLights.SelfCheck(log) & DbSky.SelfCheck(log) & DbEmitterChannels.SelfCheck(log)
           & DbKeyhole.SelfCheck(log) & Db.FrameHoist.SelfCheck(log) & Db.SunGlide.SelfCheck(log);

    internal static bool _waterArmed;
    /// <summary>The engine's water draw bucket (<c>Scene::RenderSinglePass</c> @0x46e5f0's "Water" scope).
    /// One name, read by the ownable-bucket list and by the suppression that has to agree with it.</summary>
    internal const int WaterBucket = 6;
    internal static Matrix4x4 _waterView, _waterProj;
    internal static float _waterNear = 1f, _waterFar = 100f;

    /// <summary>One uniform location per scriptable slot of one kind.</summary>
    [System.Runtime.CompilerServices.InlineArray(ScriptableSlots)]
    internal struct ScriptableLocs { private int _e; }

    /// <summary>Every location the shared feature terms upload to, the eye and the four fog-target terms
    /// included: the fog colour is direction-dependent, so every stage that fogs uploads the same five values
    /// through <see cref="UploadFog"/>.</summary>
    internal unsafe struct FeatLoc { internal int Time, Wind, Flags, Cam, CamFocus, Player, Fog, FogColor, KhDia, KhCam,
                                       Pixel, Eye, FogSky, FogSkyColor, FogSun, FogSunColor, FogOrigin, LodBias,
                                       TgtShapeL, TgtSelfL, TgtObjL, TgtSizeXL, TgtSizeYL, TgtRangeL,
                                       TgtColL, TgtWorldL, ProjL, Now;
        /// <summary>The WHOLE scriptable block, by location. Picking entries one feature at a time was a
        /// standing bug: a content shader that starts reading a new slot compiles, links and reads zero --
        /// a feature that silently does nothing under Alloy and works under the engine. Filled at program
        /// build; a slot the stage does not use is -1, which GL ignores.</summary>
        internal ScriptableLocs SInt;
        internal ScriptableLocs SFloat;
        internal ScriptableLocs SVec;
        /// <summary>Bit i set when slot i of that kind resolved to a live location, so the upload walks only
        /// what the program declares instead of 48 calls, nearly all of them to -1.</summary>
        internal int SIntLive, SFloatLive, SVecLive;
    }
    /// <summary><c>GLRender::m_shaderExposedRuntimeData</c>, the struct the engine's own SetUniforms uploads
    /// these five from. Null (symbol unresolved) leaves the wind still and the roofs wherever they were
    /// authored, which is a stock look rather than a misrender.</summary>
    private static byte* _runtime;

    /// <summary>The same struct as a typed view; every reader below null-checks <see cref="_runtime"/> first,
    /// and the view itself is one pointer. <c>CamFocus</c> (+0x90) is the camera's PIVOT, as the engine's own
    /// stages read it under the name <c>cameraFocus</c> -- not the camera POSITION (<c>CamPos</c>, +0x64): in
    /// chase cam the pivot sits on the controlled character, which content measures a sight range against.</summary>
    internal static ShaderExposedRuntimeData Rt => new((IntPtr)_runtime);
    /// <summary>Is <see cref="Rt"/> readable at all? The null check every reader of it owes, for a caller
    /// outside this class (the sky stage uploads eight of the struct's scalars).</summary>
    internal static bool HasRuntime => _runtime != null;
    /// <summary>The keyhole diameter pair inside <see cref="SymbolMap.GlGlobalShaderConstantsF"/>. Stored
    /// unordered -- SetUniforms uploads min as <c>keyholeDiameterMin</c> and max as
    /// <c>keyholeDiameterMax</c>, so the ordering happens here too.</summary>
    internal const int KeyholeDiaA = 21, KeyholeDiaB = 22;
    private static float* _shaderConstF;

    /// <summary>The spell-targeting shape's own slice of the same three tables, indices read off the
    /// <c>SetUniforms</c>/<c>ConnectShaderData</c> pairing and confirmed against the setters
    /// (<c>SpellTargeting::UpdateSpellTargetingStaticData</c> @0x958790, and
    /// <c>CClientExoAppInternal::MainLoop</c> @0x658c13 for the object type and world point).</summary>
    internal const int TgtShape = 27, TgtOnSelf = 32, TgtObjType = 35,
                       TgtSizeX = 40, TgtSizeY = 41, TgtRange = 42, TgtColour = 16;
    /// <summary>The scriptable block is 16 of each, and the engine's own tables hold them at these bases:
    /// <c>scriptableInt(n)</c> is <c>m_globalShaderConstantsi[10 + n]</c>, <c>scriptableFloat(n)</c> is
    /// <c>m_globalShaderConstantsf[23 + n]</c> and <c>scriptableVec(n)</c> is
    /// <c>m_globalShaderConstantsv4[n - 1]</c>. NWScript's own 48 uniforms are these three sixteens.</summary>
    internal const int ScriptableSlots = 16, SIntBase = 11, SFloatBase = 24, SVecBase = 0;

    /// <summary>This pass's PROJECTION, latched where the frame reads it off the engine's matrix stack, for
    /// the spliced stock files that ask for <c>m_proj</c>. Per pass, not per frame: the probe cube renders
    /// under its own, and a stale one would tell a stock file it was drawing 2D UI.</summary>
    private static Matrix4x4 _projMtx = Matrix4x4.Identity;
    private static int* _shaderConstI;
    private static float* _shaderConstV;
    private static Vector4* _shaderConstV4;

    /// <summary>Stand-in area ambient for the frames before the engine's first index -1 light bind. Grey, not
    /// black: a missing capture must not paint the world dark.</summary>
    internal const float DefaultAmbient = 0.25f;

    /// <summary>Texture units the shadow maps bind on. Unit 0 is the part's diffuse and gets rebound per
    /// batch, so the maps live above it and are bound once per pass.</summary>
    internal const uint SunUnit = TextureSlots.SunShadow, CubeUnit = TextureSlots.LocalShadow,
                        CubeDynUnit = TextureSlots.LocalShadowDyn,
                        SunDynUnit = TextureSlots.SunShadowDyn, SunDynUnitGrass = TextureSlots.SunShadowDynGrass;
    /// <summary>Shadow depth bias in WORLD units, sent per cascade as a fraction of that cascade's published
    /// depth range so it stays a constant world distance whatever the fit. Small because the receiver's normal
    /// offset carries most of the acne fight; <c>/shadow csmbias</c> tunes it.</summary>
    private static float _sunWorldBias => Shadows.CsmBias.V;
}
