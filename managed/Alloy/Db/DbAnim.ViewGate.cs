using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>The gate's whole state: the view a world walk holds objects against and the tree fact it asks.</summary>
internal sealed class ViewGateState
{
    /// <summary>Near, far and the four sides, inward-facing.</summary>
    public readonly CullPlane[] Planes = new CullPlane[6];
    /// <summary>Where the sun lands an object's shadow, per metre of the object's height.</summary>
    public Vector3 ShadowPerHeight;
    public bool On;
    /// <summary>The object holds a light part, scanned once per part range.</summary>
    public readonly bool[] HasLight = new bool[SceneDb.MaxObjects];
}

/// <summary><c>Gob::Animate</c> @0x59d8c0's gate: past the behaviour solve, an object out of view with a model not
/// effect-classified advances no clock and fires no event. Alloy's view is the frustum, not the engine's BSP flag,
/// widened by the lights and shadows it carries in. Off with <c>alloy.animdist</c> 0. ponytail: <c>Gob+0xf0</c>
/// also exempts an object and has no writer but the constructor in the dump, so nothing here reads it.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>How many object heights a low sun's shadow runs, at most: the sun's own stretch past this is a
    /// shadow too faint to read.</summary>
    private const float LongestShadowHeights = 4f;
    /// <summary>How far a lamp's shadow of an object reaches, in sphere radii past the sphere. ponytail: a flat
    /// estimate for every lamp; the cube pass's own fit is the upgrade if a lamp shadow is seen to freeze.</summary>
    private const float LampShadowRadii = 2f;

    private static ViewGateState _view = new();

    /// <summary>Latch the view from the frame's camera and sun for this world walk. No camera stands the gate down.</summary>
    private static void ViewBegin()
    {
        _view.On = false;
        if (!FrameInputs.HasCamera || !FrameInputs.HasPerspective) return;
        ViewLatch(FrameInputs.CameraPos, FrameInputs.SceneCamFwd, FrameInputs.CamFovy, FrameInputs.CamAspect,
                  FrameInputs.CamNear, FrameInputs.CamFar, FrameInputs.SunDir, FrameInputs.HasSun);
    }

    private static void ViewLatch(Vector3 pos, Vector3 fwd, float fovy, float aspect, float near, float far,
                                  Vector3 sun, bool hasSun)
    {
        ShadowMath.ReceiverPlanes(pos, fwd, fovy, aspect, near, far, 0f, _view.Planes);
        _view.ShadowPerHeight = hasSun && sun.Z < 0f ? ShadowPerHeight(sun) : Vector3.Zero;
        _view.On = true;
    }

    /// <summary>The sun's travel, scaled so it falls one metre, with its run along the ground capped.</summary>
    private static Vector3 ShadowPerHeight(Vector3 sun)
    {
        Vector3 s = sun / -sun.Z;
        float run = new Vector2(s.X, s.Y).Length();
        if (run <= LongestShadowHeights) return s;
        float cut = LongestShadowHeights / run;
        return new Vector3(s.X * cut, s.Y * cut, -1f);
    }

    /// <summary>Does object <paramref name="i"/> sit this step out? Attached objects ask their run root, so a
    /// weapon stays on its body's frame. An object owing its first pose of a run, or carrying a moving light,
    /// always steps. Hot, once per stepped member.</summary>
    private static bool OffView(int i)
    {
        ViewGateState g = _view;
        if (!g.On || (_flags[i] & ObjAnim.PoseOwed) != 0) return false;
        DbId scene = SceneDb.ObjScene[i];
        if (!SceneDb.Scenes.Valid(scene) || !SceneDb.SceneIsWorld[scene.Index]) return false;
        ModelDef? m = SceneDb.ObjModel[i];
        if (m == null || (m.Classification & HitTest.ClassEffect) != 0) return false;
                if (g.HasLight[i] && LightMoves(i)) return false;
        DbId root = SceneDb.ObjRunRoot[i];
        Vector4 s = SceneDb.ObjSphere[SceneDb.Objects.Valid(root) ? root.Index : i];
        if (!(s.W > 0f)) return false;
        return !ReachesView(new Vector3(s.X, s.Y, s.Z), s.W, g.Planes, g.ShadowPerHeight);
    }

    /// <summary>Does a part of object <paramref name="i"/> keep its light moving? ponytail: asks no reach, so a
    /// flickering lamp steps wherever it stands; its light's radius is the upgrade if lamps hold the saving down.</summary>
    private static bool LightMoves(int i)
    {
        int first = SceneDb.ObjPartFirst[i], n = SceneDb.ObjPartCount[i];
        for (int k = 0; k < n; k++)
            if (DbInputs.IsLight(first + k) && LitMoving(first + k)) return true;
        return false;
    }

    /// <summary>Can a sphere, or the shadow it casts, land in the view? False only when it and its shadow's far
    /// end both clear one plane. A plane's clearance is the sphere and a lamp's shadow of it. Pure.</summary>
    internal static bool ReachesView(Vector3 centre, float radius, ReadOnlySpan<CullPlane> planes, Vector3 shadowPerHeight)
    {
        float clear = radius * (1f + LampShadowRadii);
        Vector3 tip = centre + shadowPerHeight * (2f * radius);
        foreach (ref readonly CullPlane p in planes)
            if (p.SignedDistance(centre) < -clear && p.SignedDistance(tip) < -clear) return false;
        return true;
    }
}
