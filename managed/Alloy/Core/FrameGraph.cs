using CX.Framework;

namespace CX.Alloy;

/// <summary>The GL brackets (context enter/leave) the Alloy frame is allowed per frame. ADR 0011 costs
/// the graph at exactly two, so a third one appearing is a design change, not an implementation detail.
/// Named by role, not by anchor: the anchor moves where each bracket hangs off the engine.</summary>
internal enum Bracket
{
    /// <summary>World casters: sun cascades + local light cubes. <see cref="CasterPass.OnPassSlot"/>.</summary>
    Shadow,
    /// <summary>The Alloy colour pass, at the world view's draw (<c>AlloyRenderer.DbWorldPass</c>, reached
    /// through <c>SeamDraw.Pass</c>).</summary>
    Colour,
}

/// <summary>One pass of the Alloy frame. Declaration order IS ADR 0011's graph order -- new M3 passes are
/// inserted at their graph position, never appended for convenience.</summary>
internal enum PassId
{
    SunCascades,
    LocalLightCubes,
    /// <summary>The rain occlusion map -- one ortho depth view along the fall vector, baked from the
    /// still casters into the sun array's spare layer and held on its fit key (<see cref="CasterPass"/>).</summary>
    RainCover,
    /// <summary>The overhead sky view -- one ortho depth render of the still casters straight down, fitted
    /// to the area's extent and held until that geometry changes. <see cref="SkyView"/> bakes it into the
    /// map the lit stage scales the area ambient by.</summary>
    SkyMap,
    DepthPrepass,
    /// <summary>Screen-space ambient occlusion from the prepass depth, BEFORE the colour pass so the lit
    /// stage reads it as a lighting input (its ambient term) rather than a post stage darkening the finished
    /// frame the way vanilla's FB effect does (ADR 0021).</summary>
    AmbientOcclusion,
    OpaqueOwned,
    /// <summary>The per-area skybox, replayed from <see cref="SkyBox"/>'s capture of the engine's own draw.
    /// AFTER the opaque set, so the prepass depth rejects every sky pixel the world covered before it shades,
    /// and BEFORE the blending classes, cutout included, so anything seen against the sky composites over a
    /// finished one. Vanilla draws it between buckets 0 and 1, which gets only the second half right.</summary>
    SkyOwned,
    /// <summary>The cutout class -- the alpha discard, depth writes ON, no blending and no sort, which is the
    /// whole of its order-independence (<see cref="AlloyClasses.Blends"/> carries why the blend went away).
    /// Split out of <see cref="OpaqueOwned"/> to sit where the engine draws bucket 1, immediately after
    /// <c>RenderSkyBoxes</c>: unblended it is correct on either side of the sky, and this side is cheaper.</summary>
    CutoutOwned,
    /// <summary>The deferred point lights (<see cref="DeferredLights"/>, ADR 0029) -- one compute pass that
    /// reads the G-buffer the opaque and cutout passes wrote and adds the point lights into the HDR target.
    /// After both writers and BEFORE grass, whose root blend copies the lit frame under each blade.</summary>
    DeferredLights,
    /// <summary>The alpha-to-coverage layer (<see cref="CoverageLayer"/>) -- the cutout run's
    /// <c>NWN_ALPHA_MODE 2</c> tail, drawn forward into a multisampled copy of the lit frame and resolved
    /// back. After the deferred lights so the background its uncovered samples hold is final, and BEFORE
    /// grass and every pass that tests the depth it writes back.</summary>
    CoverageOwned,
    /// <summary>Alloy grass -- GPU-instanced blades over the engine's own bin geometry, replacing
    /// <c>Scene::DoBinBucket</c> outright. A cutout class, so no grass fragment is ever partially transparent
    /// AND depth-writing. After the opaque and cutout passes, whose colour its root blend copies under each
    /// blade, and BEFORE the blended class, which writes no depth and would be painted over.</summary>
    GrassOwned,
    /// <summary>Screen-space reflections (<see cref="Ssr"/>), deferred. After everything that writes a
    /// reflection record -- the opaque set, the sky, cutouts, grass -- and BEFORE the blended class: the trace
    /// reads the depth and colour of the frame so far, and a translucent surface drawn over a reflection must
    /// composite over the finished one. Blended surfaces keep the probe alone.</summary>
    Reflections,
    /// <summary>The blended class -- genuine alpha, sorted back-to-front per part, depth writes off. Ahead of
    /// the framebuffer copy the FB-sampling and water classes read, so one copy serves both classes instead of
    /// the engine's two (fade is a blended-class member). Its colour draw writes no depth under OIT, so the
    /// pass closes with a depth-only sweep over the same range (<c>BlendedDepthSweep</c>).</summary>
    BlendedOwned,
    /// <summary>The ONE framebuffer copy of the Alloy frame -- colour and depth of everything drawn so far,
    /// snapshotted into <see cref="FrameSnapshot"/>'s own pair of textures for the water stage to march.
    /// Behind it the opaque set, the sky and the blended class, fade included; ahead of it additive, which
    /// writes no depth -- effects drawn first would have water paint over the glow standing in front.</summary>
    FbCopy,
    /// <summary>Water, drawn from the copy above -- screen-space refraction and reflection with the IBL
    /// probe standing in wherever the march misses. Depth writes ON.</summary>
    WaterOwned,
    /// <summary>Additive last of the Alloy classes, as it is last of the engine's buckets. Unsorted --
    /// addition commutes -- and no depth write.</summary>
    AdditiveOwned,
    /// <summary>The volumetric light beams -- bucket 15, which the engine's own bucket walk never touches,
    /// replayed from <see cref="Beams"/>'s capture of <c>Scene::RenderBeams</c>. Vanilla's own placement:
    /// immediately after the additive bucket, before the particles. An additive class in every respect, in a
    /// pass of its own only because its geometry is extruded by a program of its own.</summary>
    BeamsOwned,
    /// <summary>Particles -- <c>Scene::DoEmitterBucket</c>'s emitters, rebuilt rather than replayed: the engine
    /// keeps spawn, movement and life, and the vertex build is owned. Three sub-orders inside the one pass --
    /// punch-through first (it writes depth), then the alpha-blended remainder back-to-front, then additive.
    /// The only class that declines PER ITEM, so a declined layer composites over an Alloy one regardless.</summary>
    ParticlesOwned,
    /// <summary>Procedural light halos (<see cref="Halos"/>): vanilla's lens flares, step 29, right after the
    /// particles. Additive, no depth write; occlusion is the shader's own depth read.</summary>
    HalosOwned,
    /// <summary>Rain and snow (<see cref="Weather"/>): the instanced precipitation field, after the halos.
    /// Alpha over, depth-tested, no depth write; cover is the vertex stage's read of the rain map.</summary>
    WeatherOwned,
    /// <summary>The per-area IBL probe capture. Last in the colour bracket by necessity -- it draws the same
    /// staged batches six more times and rebuilds the light binning per face, so it has to run after every
    /// on-screen pass that reads them. Skipped on all but the handful of frames
    /// <see cref="EnvProbe.Wanted"/> admits.</summary>
    ProbeCapture,
    /// <summary>Resolve the HDR colour target -- bloom, tonemap, and the frame's ONE encode -- into the
    /// engine's own attachment. Last because it consumes the whole frame's colour, and after the probe
    /// capture because the capture draws the staged batches again into its own framebuffer and so is not part
    /// of the image being resolved. It cannot stand down on its own: every Alloy stage writes linear.</summary>
    PostProcess,
    /// <summary>The display-space post stack -- the client's video options the engine ran as
    /// <c>Camera::DoFrameBufferEffects</c> (gamma first; the hosted chain plugs in here) over the resolved
    /// frame, last stage into the engine's target. Its stand-down is per stage and never reaches the frame:
    /// an empty stack is the resolve writing straight to the engine, exactly as before it existed.</summary>
    PostStack,
}

/// <summary>
/// The Alloy frame's pass list, declared once instead of implied by the order statements happen to appear in
/// (ADR 0011). Each bracket site walks this and switches on the id, so adding a pass is one entry here plus
/// one arm there. Wall-clock order too: the shadow bracket fires at the pass slot, ahead of the colour bracket
/// that samples it; <see cref="RspShim"/> records what stays outside Alloy.
/// </summary>
internal static class FrameGraph
{
    /// <summary>ADR 0011 §"The graph", in order, each with the bracket it runs inside.</summary>
    internal static readonly (PassId Id, Bracket Bracket)[] Passes =
    {
        (PassId.SunCascades,     Bracket.Shadow),
        (PassId.LocalLightCubes, Bracket.Shadow),
        (PassId.RainCover,       Bracket.Shadow),
        (PassId.SkyMap,          Bracket.Shadow),
        (PassId.DepthPrepass,    Bracket.Colour),
        (PassId.AmbientOcclusion, Bracket.Colour),
        (PassId.OpaqueOwned,     Bracket.Colour),
        (PassId.SkyOwned,        Bracket.Colour),
        (PassId.CutoutOwned,     Bracket.Colour),
        (PassId.DeferredLights,  Bracket.Colour),
        (PassId.CoverageOwned,   Bracket.Colour),
        (PassId.GrassOwned,      Bracket.Colour),
        (PassId.Reflections,     Bracket.Colour),
        (PassId.BlendedOwned,    Bracket.Colour),
        (PassId.FbCopy,          Bracket.Colour),
        (PassId.WaterOwned,      Bracket.Colour),
        (PassId.AdditiveOwned,   Bracket.Colour),
        (PassId.BeamsOwned,      Bracket.Colour),
        (PassId.ParticlesOwned,  Bracket.Colour),
        (PassId.HalosOwned,      Bracket.Colour),
        (PassId.WeatherOwned,    Bracket.Colour),
        (PassId.ProbeCapture,    Bracket.Colour),
        (PassId.PostProcess,     Bracket.Colour),
        (PassId.PostStack,       Bracket.Colour),
    };

    /// <summary>Off-device (run-demo.sh): the list schedules every declared pass exactly once, in ADR 0011's
    /// order, and costs two brackets. Ids ascending is what ties the walk to the ADR -- the enum carries the
    /// graph order, so a pass appended out of position fails here instead of rendering wrong. Returns false to
    /// keep the Alloy frame from arming at all -- a mis-ordered graph is a misrender, not a bad dial.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        // ...and every declared bracket is one the frame actually opens: a third Bracket member is the design
        // change ADR 0011 costs at two, so it fails here rather than quietly becoming a third Enter/Leave.
        bool ok = Passes.Length == System.Enum.GetValues<PassId>().Length
                  && System.Enum.GetValues<Bracket>().Length == 2;
        int brackets = 0;
        for (int i = 0; i < Passes.Length; i++)
        {
            if (i > 0)
            {
                if ((int)Passes[i].Id <= (int)Passes[i - 1].Id) ok = false;             // ADR order
                if (Passes[i].Bracket < Passes[i - 1].Bracket) ok = false;              // brackets never re-open
                if (Passes[i].Bracket != Passes[i - 1].Bracket) brackets++;
            }
            else brackets++;
        }
        if (brackets != 2) ok = false;
        // #375: the post GPU bracket opens at PostProcess and closes at PostStack, so they must be adjacent --
        // a pass slotted between them would be timed as post and run on a half-finished frame.
        for (int i = 1; i < Passes.Length; i++)
            if (Passes[i].Id == PassId.PostStack && Passes[i - 1].Id != PassId.PostProcess) ok = false;
        log.Info(ok ? $"FrameGraph.SelfCheck: passed ({Passes.Length} passes, {brackets} brackets)"
                    : $"FrameGraph.SelfCheck: FAILED ({Passes.Length} passes, {brackets} brackets)");
        return ok;
    }
}
