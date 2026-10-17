using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The texture-unit contract, written down once: which sampler lives on which GL texture unit is
/// shared between this renderer and the <c>cx_*.shd</c> stages a content repo may override. A collision is
/// neither a compile error nor a log line -- two samplers of DIFFERENT types on one unit raise
/// INVALID_OPERATION and the draw vanishes; two of the same type read each other's texture.</summary>
internal static class TextureSlots
{
    /// <summary>Unit 0, the material diffuse -- and the unit every pass leaves current, because the colour
    /// loop rebinds diffuse textures there thousands of times a frame.</summary>
    internal const uint Diffuse = 0;
    /// <summary>The sun cascade array (<c>sampler2DArrayShadow</c>) and the local-light cube array
    /// (<c>samplerCubeArrayShadow</c>). Both are bound once at program link, not per frame: a sampler left at
    /// its default points at unit 0 alongside the diffuse, which is the type collision above.</summary>
    internal const uint SunShadow = 1, LocalShadow = 2;
    /// <summary>The material map block: <see cref="MapCount"/> consecutive units from here, in
    /// <see cref="MapSamplers"/> order.</summary>
    internal const uint MapBase = 3;
    /// <summary>How many units <see cref="MapBase"/> spans. Growing it walks into <see cref="Probe"/>.</summary>
    internal const int MapCount = 7;
    /// <summary>The post chain's two inputs at composite time... and, on the GRASS program, unit 10 is the
    /// displacement field instead. Deliberate and safe: they are different programs that never run in the
    /// same bracket, so the table records the reuse rather than burning a unit to avoid it.</summary>
    internal const uint PostHdr = 10, PostBloom = 11, GrassPush = 10;
    /// <summary>The dynamic-pool cube array (<c>samplerCubeArrayShadow</c>), sampled by every program that
    /// samples <see cref="LocalShadow"/>. Shares <see cref="PostBloom"/>'s unit on <see cref="GrassPush"/>'s
    /// argument: the post resolve never runs in the lit/water/grass/particle brackets, and 0..15 is the whole
    /// budget. Bound at each program's link -- left at default unit 0 it collides with the diffuse.</summary>
    internal const uint LocalShadowDyn = PostBloom;
    /// <summary>The sun's dynamic cascade array (<c>sampler2DArrayShadow</c>), sampled by every program that
    /// samples <see cref="SunShadow"/>. Shares <see cref="PostHdr"/>'s unit on <see cref="LocalShadowDyn"/>'s
    /// argument -- except on the GRASS program, where unit 10 is the displacement field, so grass binds it to
    /// <see cref="SnapshotColor"/>'s instead. The upload binds the array on BOTH units.</summary>
    internal const uint SunShadowDyn = PostHdr, SunShadowDynGrass = SnapshotColor;
    /// <summary>The environment probe cube -- above the whole map block for the type-collision reason.</summary>
    internal const uint Probe = 12;
    /// <summary>The owned depth at the resolve, which reads it to exempt the sky. <see cref="Probe"/>'s
    /// unit on <see cref="PostHdr"/>'s argument: the resolve is its own program and the probe is not bound there.</summary>
    internal const uint PostDepth = Probe;
    /// <summary>The aa stage's own passes, post stack only -- after the world pass, so the probe and snapshot
    /// units are free on the same argument as <see cref="PostHdr"/>: edges then weights on 12, the area
    /// table on 13, the search table on 14.</summary>
    internal const uint AaEdges = 12, AaBlend = 12, AaArea = 13, AaSearch = 14;
    /// <summary>The SCENE's environment map, which vanilla's water reflects. Shares
    /// <see cref="Probe"/>'s unit -- the water program stopped sampling the probe when the port replaced
    /// Alloy's own reflection with stock's, so the two are never live in one program. A sampler2D here and
    /// a samplerCube there, which is why it may not move onto a unit the lit program also binds.</summary>
    internal const uint WaterEnv = Probe;
    /// <summary>The GUI program's reflection of the same map, for a doll's env-mapped parts: the same unit, and the
    /// GUI program binds nothing else there.</summary>
    internal const uint GuiEnv = WaterEnv;
    /// <summary>The frame snapshot the water stage refracts through: colour then depth.</summary>
    internal const uint SnapshotColor = 13, SnapshotDepth = 14;
    /// <summary>The OIT resolve's accumulation pair. The snapshot's units: the resolve runs before the water
    /// copy is taken, and FrameSnapshot.Bind re-binds its own textures before any water draw.</summary>
    internal const uint OitAccum = SnapshotColor, OitReveal = SnapshotDepth;
    /// <summary>The opaque depth the linked-list append tests against, bound for the accumulate only --
    /// the reveal unit, which is free until the resolve rebinds it.</summary>
    internal const uint OitDepth = OitReveal;
    /// <summary>The frame copy the grass root blend reads. The last material-map unit (<see cref="MapBase"/>+6):
    /// the grass program binds no material maps, and every unit from <see cref="PostHdr"/> up is taken in
    /// that program by the light rig (<see cref="LocalShadowDyn"/> on 11 was the first attempt, and the blade
    /// sampled a shadow cube).</summary>
    internal const uint GrassGround = MapBase + 6;
    /// <summary>The screen-space visibility the lit stage reads. Shares <see cref="SnapshotColor"/>'s
    /// unit: the snapshot takes it at FbCopy and lit additive draws follow, so the lit program REBINDS it at
    /// every program bind (<see cref="SsaoPass.BindForDraw"/>) rather than trusting the pass's own bind.
    /// Both sampler2D; the grass program's dyn-sun array on the same unit is a different program.</summary>
    internal const uint Ssao = SnapshotColor;
    /// <summary>The filtered cover map (Weather.Esm) the lit stage's exposure reads. Shares <see cref="SnapshotDepth"/>'s
    /// unit on <see cref="Ssao"/>'s argument, re-bound at every lit program bind; the OIT accumulate takes
    /// the unit back (<see cref="OitDepth"/>) and the stage falls back to a hard compare under uOit.</summary>
    internal const uint CoverEsm = SnapshotDepth;
    /// <summary>The cover layer as plain depth, read by the SPLASH vertex stage to place each impact
    /// at its landing point. The map block's first unit on <see cref="GrassPush"/>'s argument -- the
    /// precipitation programs bind no material maps, and the splash program is the only thing in that
    /// bracket that binds a sampler2D below unit 10 at all.</summary>
    internal const uint CoverDepth = MapBase;
    /// <summary>The scene depth the soft-particle fade and the splash contact test read.</summary>
    internal const uint SceneDepth = 15;
    /// <summary>The four G-buffer lanes and the frame depth (ADR 0029), read by the DEFERRED COMPUTE program
    /// (<see cref="DeferredLights"/>) and by nothing else. The MAP BLOCK's units, on <see cref="CoverDepth"/>'s
    /// argument: the dispatch runs BETWEEN two draw passes, binds no material map of its own, and every draw
    /// program rebinds its whole slot set at the program switch. It still puts texture 0 back on all five.</summary>
    internal const uint Gbuf0 = MapBase, Gbuf1 = MapBase + 1, Gbuf2 = MapBase + 2, Gbuf3 = MapBase + 3,
                        GbufDepth = MapBase + 4;
    /// <summary>The per-pixel shadow visibility record (<see cref="DeferredShadows"/>), read by
    /// the deferred lighting dispatch alone. The map block's last two units, on <see cref="Gbuf0"/>'s argument
    /// and <see cref="GrassGround"/>'s -- the grass program is a later pass that rebinds its own, and this one
    /// puts texture 0 back on both.</summary>
    internal const uint ShadowVis = MapBase + 5, ShadowKey = MapBase + 6;
    /// <summary>The coverage layer's seed (the lit frame and its depth) and depth write-back (the layer's
    /// multisampled depth), read by <see cref="CoverageLayer"/>'s two full-screen programs alone. The map
    /// block's units, on <see cref="Gbuf0"/>'s argument: those programs run between draw passes and bind no
    /// material map, every draw program rebinds its slot set, and the layer puts texture 0 back on both.</summary>
    internal const uint CoverSeed = MapBase, CoverSeedDepth = MapBase + 1;
    /// <summary>The two shadow cube arrays again, through a sampler object with no compare mode, for the
    /// receiver's blocker search. Past the 0..15 budget on purpose: every unit below is spent, and every
    /// driver Alloy has met reports 32 per stage. The lib binds them by <c>layout(binding)</c>, so a
    /// strict-16 driver would fail the lit link -- which the limits line names.</summary>
    internal const uint LocalShadowRaw = 16, LocalShadowDynRaw = 17;
    /// <summary>The two sun cascade arrays again, through that same no-compare sampler, for the sun's own
    /// blocker search -- the HIGHEST units Alloy binds, taking the lit stage's claim to 0..30 of the 32 every
    /// driver it has met reports. Declared by <c>layout(binding)</c>, only where a stage asks for the soft
    /// sun filter, so no vertex stage spends a unit on them.</summary>
    // ponytail: the upgrade, when the lit stage next needs a unit, is the bindless handle the diffuse
    // already takes, or one cascade array holding the static and dynamic layers end to end.
    internal const uint SunShadowRaw = 29, SunShadowDynRaw = 30;
    /// <summary>R2: the area wetness map (<see cref="Weather"/>), read by the lit stage and the grass blade.
    /// Past the 0..15 budget for <see cref="LocalShadowRaw"/>'s reason -- every unit below is spent in the lit
    /// bracket -- and declared by <c>layout(binding)</c>, so no bind-time <c>glUniform1i</c> can miss it.</summary>
    internal const uint WetMap = 18;
    /// <summary>The SAME prepass depth, on the same unit, read by the LIT stage -- so a fragment can ask
    /// whether the visibility at its pixel was computed from ITS OWN surface. The prepass and the colour pass
    /// compute gl_Position character-for-character, so a prepassed fragment's gl_FragCoord.z equals the buffer
    /// exactly and anything else is another surface's occlusion. Free on this unit in the lit bracket.</summary>
    internal const uint SsaoDepth = SceneDepth;
    /// <summary>The engine's <c>solid_noise</c>, which vanilla's water shader samples as <c>texUnitNoise</c>.
    /// Shares <see cref="SceneDepth"/>'s unit on <see cref="GrassPush"/>'s argument, both sampler2D. NOT on
    /// the map block, and that is the whole reason the unit is written down: the colour loop's slot binds run
    /// for any batch that is not a merged bindless lit one, so a water draw overwrites units 3..9.</summary>
    internal const uint WaterNoise = SceneDepth;
    /// <summary>The hi-Z pyramid, read by the CULL DISPATCH only. Shares <see cref="Probe"/>'s unit: the
    /// dispatch is a compute program that runs BETWEEN two draw passes and samples nothing else, and every
    /// draw program rebinds its own probe at the program switch. It leaves texture 0 on the unit rather than
    /// its pyramid, so a consumer that did not rebind samples nothing. 0..15 is the whole budget.</summary>
    internal const uint HiZ = Probe;
    /// <summary>The frame's own depth texture, read by the two highlight overlays that draw over
    /// the FINISHED frame -- the glow quads' depth test and the trigger decal's world reconstruction.
    /// Shares <see cref="SceneDepth"/>'s unit: both are the last programs of the frame, past every pass that
    /// wants that unit for anything else.</summary>
    internal const uint HiliteDepth = SceneDepth;

    /// <summary>The master PLT palette (256x1792 RGBA8), read by the lit stage to resolve a layered
    /// texture per instance. Past the 0..15 budget for <see cref="LocalShadowRaw"/>'s reason -- every unit
    /// below is spent in the lit bracket -- and declared with <c>layout(binding)</c> in <c>cx_plt.glsl</c>,
    /// so no bind-time <c>glUniform1i</c> can miss it.</summary>
    internal const uint PltPalette = 19;

    /// <summary>The sky-visibility map (<see cref="SkyView"/>), read by the lit stage: the HIGHEST unit
    /// Alloy claims. Declared behind <c>CX_SKY_VIEW</c>, which <see cref="Programs.WithoutSkyView"/> takes
    /// out of the stage on a driver reporting fewer than <see cref="Programs.SkyViewUnitsNeed"/> fragment
    /// units, exactly as the soft sun's two raw views are.</summary>
    internal const uint SkyMap = 31;

    /// <summary>The (static light, mover) pair depth atlas (<see cref="PairShadows"/>). Read by the debt
    /// dispatch and -- for an unbaked receiver whose light traded its dyn page for tiles -- by
    /// <c>CxLocalShadowQ</c>, so by every program that lights. Past the 0..15 budget for
    /// <see cref="LocalShadowRaw"/>'s reason and declared with <c>layout(binding)</c>.</summary>
    internal const uint PairMap = 28;

    /// <summary>The deferred pass's light record (<see cref="GBuffer.TexLight"/>), read by the lit stage's
    /// blended draws. Past the 0..15 budget for <see cref="LocalShadowRaw"/>'s reason and declared with
    /// <c>layout(binding)</c> in <c>cx_ownlit_head.glsl</c>; bound for the blended class by <see cref="Issue"/>.</summary>
    internal const uint LightRec = 26;

    /// <summary>The map samplers, in unit order from <see cref="MapBase"/>. This array IS the bind order the
    /// lit program's link-time <c>glUniform1i</c> loop walks -- there is no second copy of it.</summary>
    internal static readonly string[] MapSamplers =
        { "uNrmTex", "uSpecTex", "uRoughTex", "uHeightTex", "uIllumTex", "uFlowTex", "uAoTex" };

    /// <summary>Every texture unit a <c>layout(binding)</c> sampler declaration in <paramref name="src"/>
    /// claims, in source order and with repeats. Declarations behind a <c>#ifdef</c> count too: the text
    /// cannot say which the preprocessor keeps, so an exempt one has to be named by its caller.</summary>
    private static System.Collections.Generic.IEnumerable<uint> Claimed(string src)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                     src, @"layout\(binding\s*=\s*(\d+)\)\s*uniform\s+\w*sampler\w*\s+\w+\s*;"))
            yield return uint.Parse(m.Groups[1].Value);
    }

    /// <summary>The unit the source declares for the sampler named <paramref name="sampler"/>; 0 when it
    /// declares none, which fails every need it is compared with.</summary>
    private static uint Unit(string src, string sampler)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            src, @"layout\(binding\s*=\s*(\d+)\)\s*uniform\s+\w*sampler\w*\s+" + sampler + @"\s*;");
        return m.Success ? uint.Parse(m.Groups[1].Value) : 0;
    }

    /// <summary>The first unit two of those declarations both claim, named with both samplers; null when
    /// every unit is claimed once.</summary>
    private static string? DoubleClaimed(string src)
    {
        var seen = new System.Collections.Generic.Dictionary<string, string>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                     src, @"layout\(binding\s*=\s*(\d+)\)\s*uniform\s+\w*sampler\w*\s+(\w+)\s*;"))
        {
            string unit = m.Groups[1].Value, name = m.Groups[2].Value;
            if (seen.TryGetValue(unit, out string? first) && first != name) return $"{unit} is {first} and {name}";
            seen[unit] = name;
        }
        return null;
    }

    /// <summary>Assert the table against the GLSL that actually compiled: every slot's sampler must be
    /// declared by the stage that reads it, and the units must not overlap. False = do not arm -- a slot
    /// disagreement renders as dropped draws or a diffuse sampled as a normal map, neither of which says
    /// anything at pixel time, so the honest failure is to stand down and name the slot.</summary>
    internal static bool Check(ExtenderLog log)
    {
        var check = new Checker(log);

        // Each unit against the sampler declaration in the stage that consumes it -- the copy that is
        // independent of the consts above, which now all alias this table. Rename or drop a declaration and
        // glUniform1i binds a -1 location: a legal no-op that leaves the sampler on unit 0.
        check.Expect(AlloyRenderer.OwnedFs.Contains("uniform sampler2D uTex;")
          && AlloyRenderer.OwnedLitFsHead.Contains("uniform sampler2D uTex;")
          && GrassField.GrassFsHead.Contains("uniform sampler2D uTex;")
          && Particles.ParticleFsHead.Contains("uniform sampler2D uTex;"), $"{Diffuse} uTex (diffuse)");
        check.Expect(AlloyLights.SunShadowLib.Contains("uniform sampler2DArrayShadow uSunShadow;"),
          $"{SunShadow} uSunShadow");
        check.Expect(AlloyLights.LocalShadowLib.Contains("uniform samplerCubeArrayShadow uLocalShadow;"),
          $"{LocalShadow} uLocalShadow");
        check.Expect(AlloyLights.LocalShadowLib.Contains("uniform samplerCubeArrayShadow uLocalDynShadow;"),
          $"{LocalShadowDyn} uLocalDynShadow");
        check.Expect(AlloyLights.LocalShadowLib.Contains($"layout(binding = {LocalShadowRaw}) uniform samplerCubeArray uLocalShadowRaw;"),
          $"{LocalShadowRaw} uLocalShadowRaw");
        check.Expect(AlloyLights.LocalShadowLib.Contains($"layout(binding = {LocalShadowDynRaw}) uniform samplerCubeArray uLocalDynShadowRaw;"),
          $"{LocalShadowDynRaw} uLocalDynShadowRaw");
        check.Expect(AlloyLights.SunShadowLib.Contains($"layout(binding = {SunShadowRaw}) uniform sampler2DArray uSunShadowRaw;"),
          $"{SunShadowRaw} uSunShadowRaw");
        check.Expect(AlloyLights.SunShadowLib.Contains($"layout(binding = {SunShadowDynRaw}) uniform sampler2DArray uSunDynShadowRaw;"),
          $"{SunShadowDynRaw} uSunDynShadowRaw");
        // ...and the general form of the same question, asked of the composed lit stage rather than of a
        // hand-kept list of neighbours: no two sampler declarations may claim one unit. A list goes stale
        // (the sun's raw views were first written onto the weather shelter's and noise's units, which the
        // consts above said nothing about, and two sampler types on one unit is a draw that vanishes).
        string litDecls = ShaderSource.Get(AlloyRenderer.ShdLitFs + ".glsl");
        check.Expect(DoubleClaimed(litDecls) is null, $"two samplers claim one unit in {AlloyRenderer.ShdLitFs}: {DoubleClaimed(litDecls)}");
        // ...and the two unit BUDGETS the lit program is linked against. Every unit it claims is either under
        // the plain program's need or one of the soft sun's two on top -- except uPairMap, which only the
        // deferred shadow pass's CX_PAIR_RECV compiles in, so it is named here rather than counted.
        check.Expect(!litDecls.Contains("#define CX_PAIR_RECV"), $"{PairMap} uPairMap is exempt only while the lit stage never defines CX_PAIR_RECV");
        foreach (uint u in Claimed(litDecls))
            check.Expect(u < Programs.LitUnitsNeed || u == SunShadowRaw || u == SunShadowDynRaw || u == PairMap
                      || u == SkyMap,
              $"unit {u} is claimed in {AlloyRenderer.ShdLitFs} but sits above Programs.LitUnitsNeed ({Programs.LitUnitsNeed})");
        check.Expect(SkyView.Lib.Contains($"layout(binding = {SkyMap}) uniform sampler2D uSkyMap;")
          && litDecls.Contains($"layout(binding = {SkyMap}) uniform sampler2D uSkyMap;"), $"{SkyMap} uSkyMap (lit program)");
        check.Expect(litDecls.Contains(Programs.SkyViewDefine)
          && !Programs.WithoutSkyView(litDecls).Contains(Programs.SkyViewDefine),
          "the sky-visibility term is removable: the stage carries the define and Programs.WithoutSkyView takes it out");
        // The removable features' needs against the units the stage's source declares for their samplers.
        uint topSoft = Math.Max(Unit(litDecls, "uSunShadowRaw"), Unit(litDecls, "uSunDynShadowRaw"));
        uint topSky = Unit(litDecls, "uSkyMap");
        check.Expect((int)topSoft + 1 == Programs.SunSoftUnitsNeed,
          $"the soft sun declares up to unit {topSoft}, so its need is {topSoft + 1}; Programs says {Programs.SunSoftUnitsNeed}");
        check.Expect((int)topSky + 1 == Programs.SkyViewUnitsNeed && Programs.SunSoftUnitsNeed < Programs.SkyViewUnitsNeed,
          $"the sky view declares unit {topSky}, so its need is {topSky + 1}; Programs says {Programs.SkyViewUnitsNeed}");
        check.Expect(Programs.LitUnitsNeed < Programs.SunSoftUnitsNeed
          && ShaderSource.Get(AlloyRenderer.ShdLitFs + ".glsl").Contains(Programs.SunSoftDefine)
          && !Programs.WithoutSunSoft(litDecls).Contains(Programs.SunSoftDefine),
          "the soft sun filter is removable: the stage carries the define and Programs.WithoutSunSoft takes it out");
        check.Expect(Weather.MapLib.Contains($"layout(binding = {WetMap}) uniform sampler2D uWxMap;")
          && ShaderStages.Source(AlloyRenderer.ShdLitFs).Contains($"layout(binding = {WetMap}) uniform sampler2D uWxMap;")
          && GrassField.EmbeddedFs.Contains($"layout(binding = {WetMap}) uniform sampler2D uWxMap;"),
          $"{WetMap} uWxMap");
        check.Expect(AlloyLights.SunShadowLib.Contains("uniform sampler2DArrayShadow uSunDynShadow;"),
          $"{SunShadowDyn} uSunDynShadow");
        for (int i = 0; i < MapSamplers.Length; i++)
            check.Expect(AlloyRenderer.OwnedLitFsHead.Contains($"uniform sampler2D {MapSamplers[i]};"),
              $"{MapBase + i} {MapSamplers[i]}");
        check.Expect(PostChain.ResolveFs.Contains("uniform sampler2D uHdr;"), $"{PostHdr} uHdr");
        check.Expect(PostChain.ResolveFs.Contains("uniform sampler2D uBloom;"), $"{PostBloom} uBloom");
        check.Expect(PostChain.ResolveFs.Contains("uniform sampler2D uDepth;"), $"{PostDepth} uDepth");
        // #375: the post stack reuses the resolve's two units, after it and in the same bracket.
        check.Expect(PostStack.PostStageHead.Contains("uniform sampler2D uSrc;"), $"{PostHdr} uSrc (post stack)");
        check.Expect(PostStack.PostStageHead.Contains("uniform sampler2D uDepth;"), $"{PostBloom} uDepth (post stack)");
        check.Expect(SmaaStage.WeightFs.Contains("uniform sampler2D uEdges;"), $"{AaEdges} uEdges (aa weights)");
        check.Expect(SmaaStage.WeightFs.Contains("uniform sampler2D uArea;"), $"{AaArea} uArea (aa weights)");
        check.Expect(SmaaStage.WeightFs.Contains("uniform sampler2D uSearch;"), $"{AaSearch} uSearch (aa weights)");
        check.Expect(SmaaStage.BlendStageFs.Contains("uniform sampler2D uBlend;"), $"{AaBlend} uBlend (aa stage)");
        check.Expect(AlloyRenderer.OwnedLitFsHead.Contains("uniform sampler2D uSsao;"), $"{Ssao} uSsao (lit program)");
        check.Expect(AlloyRenderer.OwnedLitFsHead.Contains($"layout(binding = {LightRec}) uniform usampler2D uLightRec;"), $"{LightRec} uLightRec (lit program)");
        check.Expect(AlloyRenderer.OwnedLitFsHead.Contains("uniform sampler2D uSceneDepth;"), $"{SsaoDepth} uSceneDepth (lit program)");
        check.Expect(GrassField.GrassVs.Contains("uniform sampler2D uPushTex;"), $"{GrassPush} uPushTex (grass program)");
        check.Expect(GrassField.GrassFsHead.Contains("uniform sampler2D uGround;"), $"{GrassGround} uGround (grass program)");
        // #339: the water stage no longer samples the probe -- vanilla's reflection falls back to the
        // shaded surface, not to a cube -- so the lit stage is the only consumer left.
        check.Expect(AlloyRenderer.OwnedLitFsHead.Contains("uniform samplerCube uProbe;"), $"{Probe} uProbe");
        check.Expect(FrameSnapshot.FsHead.Contains("uniform sampler2D uNoise;"), $"{WaterNoise} uNoise (water program)");
        check.Expect(FrameSnapshot.FsHead.Contains("uniform sampler2D uEnvMap;"), $"{WaterEnv} uEnvMap (water program)");
        check.Expect(ShaderSource.Get("cx_gui_f.glsl").Contains($"layout(binding = {GuiEnv}) uniform sampler2D uEnvMap;"), $"{GuiEnv} uEnvMap (GUI program)");
        check.Expect(FrameSnapshot.FsHead.Contains("uniform sampler2D uFbColor;"), $"{SnapshotColor} uFbColor");
        check.Expect(FrameSnapshot.FsHead.Contains("uniform sampler2D uFbDepth;"), $"{SnapshotDepth} uFbDepth");
        check.Expect(AlloyRenderer.OitOutFs.Contains("uniform sampler2D uOitDepth;"), $"{OitDepth} uOitDepth (blended output)");
        check.Expect(AlloyRenderer.OitOutFs.Contains("uniform ivec2 uOitDim;") && AlloyRenderer.OitOutFs.Contains("uniform int uOitGob;"), "uOitDim/uOitGob are what OitPass.Upload and IssueColour write");
        check.Expect(Particles.ParticleFsHead.Contains("uniform sampler2D uSceneDepth;"), $"{SceneDepth} uSceneDepth");
        check.Expect(ShaderStages.Source(Halos.ShdHaloFs).Contains("uniform sampler2D uFlare;"), $"{Diffuse} uFlare (flare program)");
        check.Expect(Weather.SplashVs.Contains("uniform sampler2D uCoverDepth;"), $"{CoverDepth} uCoverDepth (splash program)");
        check.Expect(Weather.SplashVs.Contains("uniform sampler2D uSceneDepth;"), $"{SceneDepth} uSceneDepth (splash program)");
        // CX-188: the deferred pass declares its five by layout(binding), so no bind-time glUniform1i can miss
        // one -- but the units still have to be the ones C# binds, which is what this pins.
        string deferred = ShaderStages.Source(DeferredLights.ShdCs);
        check.Expect(deferred.Contains($"layout(binding = {Gbuf0}) uniform usampler2D uGb0;")
          && deferred.Contains($"layout(binding = {Gbuf1}) uniform usampler2D uGb1;")
          && deferred.Contains($"layout(binding = {Gbuf2}) uniform usampler2D uGb2;")
          && deferred.Contains($"layout(binding = {Gbuf3}) uniform sampler2D uGb3;")
          && deferred.Contains($"layout(binding = {GbufDepth}) uniform sampler2D uGbDepth;"),
          $"{Gbuf0}..{GbufDepth} uGb0..uGb3 + uGbDepth (deferred program)");
        check.Expect(deferred.Contains("uniform samplerCubeArrayShadow uLocalShadow;")
          && deferred.Contains("uniform samplerCubeArrayShadow uLocalDynShadow;"),
          "the deferred program composes the local-shadow receiver whose units it binds at link");
        // CX-188 step 3: the visibility record -- the lighting pass READS it on these two units, and the shadow
        // pass that WRITES it reads the same five G-buffer units the lighting pass does.
        check.Expect(deferred.Contains($"layout(binding = {ShadowVis}) uniform sampler2D uShVis;")
          && deferred.Contains($"layout(binding = {ShadowKey}) uniform usampler2D uShKey;"),
          $"{ShadowVis}/{ShadowKey} uShVis + uShKey (deferred program)");
        string defshadow = ShaderStages.Source(DeferredShadows.ShdCs);
        check.Expect(defshadow.Contains($"layout(binding = {Gbuf0}) uniform usampler2D uGb0;")
          && defshadow.Contains($"layout(binding = {GbufDepth}) uniform sampler2D uGbDepth;")
          && defshadow.Contains("uniform samplerCubeArrayShadow uLocalShadow;")
          && defshadow.Contains("uniform samplerCubeArrayShadow uLocalDynShadow;"),
          $"{Gbuf0}..{GbufDepth} uGb0 + uGbDepth and the cube arrays (deferred shadow program)");
        check.Expect(ShadowKey == GrassGround && ShadowVis > GbufDepth,
          $"{ShadowVis}/{ShadowKey} the record's units leave the map block");

        // ...and the units themselves. MapSamplers is what the lit program's bind loop walks, so a name added
        // there without a unit to put it on would silently land on the probe's.
        check.Expect(MapSamplers.Length == MapCount, $"MapSamplers has {MapSamplers.Length} names for {MapCount} units");
        check.Expect(MapBase > LocalShadow && MapBase + MapCount <= PostHdr, $"{MapBase}..{MapBase + MapCount - 1} map block overlaps its neighbours");
        check.Expect(PostHdr < PostBloom && PostBloom < Probe && Probe < SnapshotColor
          && SnapshotColor < SnapshotDepth && SnapshotDepth < SceneDepth, "the units above the map block are not distinct and ascending");
        // CX-117: the master palette's unit and the PLT row block's binding, against the GLSL that compiled.
        check.Expect(ShaderSource.Get("cx_plt.glsl").Contains($"layout(binding = {PltPalette}) uniform sampler2D uPltPal;"),
          $"{PltPalette} uPltPal (PLT master palette)");
        check.Expect(ShaderSource.Get("cx_plt.glsl").Contains($"layout(std430, binding = {PltColors.Binding}) readonly buffer CxPltBuf"),
          $"SSBO {PltColors.Binding} CxPltBuf (PLT rows per record)");
        // CX-223: the scheme feed's divisor is Material::ReplaceTexturePLT's literal 1792, which is the master
        // palette's height -- the one number the engine hands a custom shader instead of a row index.
        check.Expect(ShaderSource.Get("cx_plt.glsl").Contains($"#define CX_PLT_HEIGHT {PltPalettes.Height}.0")
          && ShaderSource.Get("cx_plt.glsl").Contains($"#define CX_PLT_ROWS     {PltColors.FlagRows}u"),
          $"cx_plt.glsl's scheme divisor is not the master palette's {PltPalettes.Height} rows, or its rows-only flag is not {PltColors.FlagRows}");
        check.Expect(ShaderSource.Get("cx_conbuf.glsl").Contains($"#define CX_SLOT_PAL0 {AlloyRenderer.PalBit0}"),
          $"cx_conbuf.glsl's first palette mask bit is not {AlloyRenderer.PalBit0}");
        check.Expect(ShaderSource.Get("cx_plt.glsl").Contains($"#define CX_PLT_WORDS    {PltColors.WordsPerRecord}")
          && ShaderSource.Get("cx_plt.glsl").Contains($"#define CX_PLT_ROWWORDS {PltColors.RowWords}")
          && ShaderSource.Get("cx_plt.glsl").Contains($"#define CX_PLT_LAYERS   {SceneDb.PltLayers}"),
          "cx_plt.glsl's record layout does not match PltColors");
        check.Expect(PltPalette != LocalShadowRaw && PltPalette != LocalShadowDynRaw && PltPalette != WetMap
          && PltPalette != LightmapBake.UnitA && PltPalette != LightmapBake.UnitB && PltPalette != LightmapBake.UnitC,
          $"{PltPalette} uPltPal collides with a unit above the 0..15 budget (16/17 local shadow, 18 wetness, "
          + $"{LightmapBake.UnitA}..{LightmapBake.UnitC} the lightmap bake)");
        // CX-188 step 4: the pair atlas and the pair table, against the GLSL every lighting program compiles.
        check.Expect(ShaderSource.Get("cx_pairshad.glsl").Contains($"layout(binding = {PairMap}) uniform sampler2DArrayShadow uPairMap;"),
          $"{PairMap} uPairMap (pair shadow atlas)");
        check.Expect(ShaderSource.Get("cx_pairbuf.glsl").Contains($"layout(std430, binding = {PairShadows.Binding}) readonly buffer CxPairBuf"),
          $"SSBO {PairShadows.Binding} CxPairBuf (this frame's pair tiles)");
        check.Expect(PairMap > PltPalette && PairMap != LightmapBake.UnitA && PairMap != LightmapBake.UnitB
          && PairMap != LightmapBake.UnitC, $"{PairMap} uPairMap collides with a unit above the 0..15 budget");
        return check.Ok;
    }
}
