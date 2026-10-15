using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary><b>Every engine object read a frame needs, read once on the GAME thread and handed across by
/// VALUE</b>: the current <c>CNWCArea</c>'s flags, extent and day/night palette, the engine's light-radius
/// multiplier, the scene's <c>WindManager</c>. Folded in <see cref="RenderSnapshot.Publish"/> under the lock
/// that IS the frame's happens-before edge. Fog is per VIEW, not here, and no field is a pointer to follow.</summary>
public struct FrameHoist
{
    /// <summary>The <c>CNWCArea*</c> this frame's terms were read from, or zero at the main menu and between
    /// modules. IDENTITY ONLY: compared against the previous frame's and printed, never dereferenced -- the
    /// renderer may be holding it after the area edge that freed it.</summary>
    public nint Area;
    /// <summary>The area chain answered: there is a module with a current area.</summary>
    public bool HaveArea;
    /// <summary><c>CNWCArea</c> flags bit 0 -- interior/underground, so no sun. <c>SceneIsWorld</c> is a
    /// different question (a tile grid) and answering with it lost every outdoor area its sun.</summary>
    public bool Interior;
    /// <summary>The area's tile grid in world units (<c>tileWidth/Height * TileSize</c>).</summary>
    public Vector2 Extent;
    /// <summary>The area is on its NIGHT palette.</summary>
    public bool Night;
    /// <summary>The palette for THIS frame's day/night state, already chosen: the ambient and diffuse the
    /// area light pair takes its colour from, and the fog colour the area would fog with. Resolved here so
    /// the renderer carries one triple each instead of a pair plus the switch.</summary>
    public Vector3 Ambient, Diffuse, FogColour;
    /// <summary>The fog AMOUNT the area last applied, the number the engine places the fog start with. 0 is no
    /// fog at all: the area's fog is switched off rather than drawn as the thin ramp vanilla leaves at its clip
    /// distance.</summary>
    public float FogAmount;
    /// <summary>The area has its own <c>Scene</c> (<c>CNWCArea+0x248</c>) -- the object the game writes every
    /// area-wide render setting into, and what <c>SetFog</c>/<c>SetFogAmount</c> refuse without.</summary>
    public bool HaveAreaScene;
    /// <summary>The fog RANGE off the area's own scene, which arrives there through a vtable call and so
    /// reaches a CX shell (<c>CNWCModule::AdjustCameraClipRange</c> -&gt; <c>SetFogRange</c>).</summary>
    public float AreaFogStart, AreaFogEnd;
    /// <summary>The engine's light-radius multiplier, global shader param 0x12
    /// (<c>SceneInputs.LatchAtten</c>'s return). A graphics setting the player can change, so it is read
    /// every frame rather than latched.</summary>
    public float RadiusMult;
    /// <summary>The engine's placeable-light registry count. The lightmap bake compares it to decide whether
    /// to re-walk the registry, and that walk is the game thread's.</summary>
    public int PlaceableLights;

    /// <summary>Read every term above off the engine, on the GAME thread. Hot (once per frame), allocates
    /// nothing, and every deref is guarded exactly as the engine guards it -- the area pointer is null at the
    /// main menu and between modules, and <c>.Scene</c> is a raw load off it, so asking an absent area for
    /// its scene reads address 0x248 and takes the process with it.</summary>
    internal static void Fold(ref FrameHoist h)
    {
        h.RadiusMult = SceneInputs.LatchAtten();
        CNWCArea a = SceneInputs.CurrentArea();
        h.Area = a.Ptr;
        h.HaveArea = !a.IsNull;
        if (a.IsNull)
        {
            h.Interior = false; h.Extent = default; h.Night = false;
            h.Ambient = default; h.Diffuse = default; h.FogColour = default; h.FogAmount = 0;
            h.HaveAreaScene = false; h.AreaFogStart = 0f; h.AreaFogEnd = 0f;
            h.PlaceableLights = 0;
        }
        else
        {
            h.Interior = (a.Flags & InteriorFlag) != 0;
            h.Extent = new Vector2(a.TileWidth * AreaBorder.TileSize, a.TileHeight * AreaBorder.TileSize);
            bool night = a.IsNight != 0;
            h.Night = night;
            h.Ambient = Triple(night ? a.NightAmbient : a.DayAmbient);
            h.Diffuse = Triple(night ? a.NightDiffuse : a.DayDiffuse);
            h.FogColour = Triple(night ? a.NightFog : a.DayFog);
            h.FogAmount = a.FogAmount;
            h.PlaceableLights = a.PlaceableLightCount;
            Scene sc = a.Scene;
            h.HaveAreaScene = !sc.IsNull;
            h.AreaFogStart = sc.IsNull ? 0f : sc.FogStart;
            h.AreaFogEnd = sc.IsNull ? 0f : sc.FogEnd;
        }
    }

    /// <summary><c>CNWCArea</c> flags bit 0: the area is interior/underground, so it has no sun.</summary>
    private const uint InteriorFlag = 1;

    private static Vector3 Triple(Span<float> c) => new(c[0], c[1], c[2]);

    /// <summary>One view's fog, resolved on the GAME thread against the same two sources
    /// <c>SceneFeed.FogRead</c> consulted inside the draw: the AREA's own palette first (the game writes the
    /// fog words through <c>CNWCArea::SetFog</c>, which emits <c>FuncInterp("mainscene.fog*")</c> TEXT and so
    /// never reaches a DB column), then the scene SHELL's words, then the scene columns.</summary>
    public struct ViewFog
    {
        /// <summary>Which source answered: 1 the area, 2 the scene columns, 3 the scene shell. On the log
        /// line, because a wrong fog in a player's session has to name its source from cx.log.</summary>
        public int Path;
        /// <summary><see cref="Path"/> for the area's own scene: the one source whose range outlives its fog.</summary>
        public const int AreaPath = 1;
        public bool On;
        public float Start, End;
        public Vector3 Colour;
    }

    /// <summary>Resolve <paramref name="scene"/>'s fog, with <paramref name="h"/>'s already-folded area
    /// terms. Game thread: it reads the engine scene shell.</summary>
    internal static void FoldFog(DbId scene, in FrameHoist h, out ViewFog fog)
    {
        fog = default;
        // The AREA's own scene first, for the reason SceneFeed.FogRead gives: the fog words land in whatever
        // object sits at CNWCArea+0x248, so reading them there needs no FindScene lookup by name.
        if (h.HaveArea && h.HaveAreaScene)
        {
            fog.Path = ViewFog.AreaPath;
            fog.On = h.FogAmount != 0;
            fog.Start = h.AreaFogStart;
            fog.End = h.AreaFogEnd;
            fog.Colour = h.FogColour;
            return;
        }
        if (!SceneDb.Scenes.Valid(scene)) return;
        int i = scene.Index;
        nint shell = SceneDb.SceneFacade[i];
        if (shell == 0)
        {
            fog.Path = 2;
            fog.On = SceneDb.SceneFogOn[i];
            if (!fog.On) return;
            fog.Start = SceneDb.SceneFogNear[i];
            fog.End = SceneDb.SceneFogFar[i];
            fog.Colour = SceneDb.SceneFogColor[i];
            return;
        }
        fog.Path = 3;
        var sc = new Scene(shell);
        fog.On = sc.FogEnable != 0;
        if (!fog.On) return;
        fog.Start = sc.FogStart;
        fog.End = sc.FogEnd;
        fog.Colour = sc.FogColor;
    }

    /// <summary>Off-device: the fold must be a pure function of the engine state and must survive the absent
    /// area -- the main menu and every moment between modules, where a deref would take the process with it
    /// rather than return a wrong colour.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "FrameHoist.SelfCheck");
        var h = new FrameHoist
        {
            Area = 0x1234, HaveArea = true, Night = true, Interior = true,
            Ambient = Vector3.One, FogColour = new Vector3(0.2f, 0.3f, 0.4f),
            HaveAreaScene = true, AreaFogStart = 30f, AreaFogEnd = 45f, FogAmount = 10,
        };
        FoldFog(default, in h, out ViewFog f);
        check.Expect(f.Path == 1 && f.On && f.Start == 30f && f.End == 45f && f.Colour == h.FogColour,
                     "an area with its own scene fogs from the area's palette and its own range");
        h.FogAmount = 0;
        FoldFog(default, in h, out f);
        check.Expect(f.Path == 1 && !f.On && f.Start == 30f && f.End == 45f && f.Colour == new Vector3(0.2f, 0.3f, 0.4f),
                     "fog amount 0 turns the area's fog off and still hands over its range, which fog of war ramps on");
        h.FogAmount = 10;
        h.HaveAreaScene = false;
        FoldFog(default, in h, out f);
        check.Expect(f.Path == 0 && !f.On,
                     "an invalid scene with no area scene fogs from nothing rather than reading a shell at 0");
        h.HaveArea = false;
        FoldFog(default, in h, out f);
        check.Expect(f.Path == 0 && !f.On, "no area is not a fog source either");

        // The real fold, with no engine behind it: this is the main-menu / between-modules state, and the one
        // the guards exist for. It must answer, not fault, and must answer with nothing rather than garbage.
        var z = new FrameHoist { Area = 0x1234, HaveArea = true, Interior = true, Night = true };
        Fold(ref z);
        check.Expect(!z.HaveArea && z.Area == 0 && !z.Interior && !z.Night && z.Extent == default,
                     "with no module the fold clears every area term instead of keeping the last one");
        check.Expect(z.RadiusMult > 0f, "the radius multiplier never folds to zero -- it multiplies every light's reach");
        log.Info(check.Ok ? "FrameHoist.SelfCheck: passed"
                          : "FrameHoist.SelfCheck: FAILED -- the frame's engine reads are not safely hoisted, so "
                          + "alloy.renderthread=1 would read a CNWCArea the game thread is simulating");
        return check.Ok;
    }
}
