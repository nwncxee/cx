using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>The gate's whole state: the view a world walk holds objects against, the lights that throw shadows
/// into it, and the tree fact it asks.</summary>
internal sealed class ViewGateState
{
    /// <summary>Near, far and the four sides, inward-facing.</summary>
    public readonly CullPlane[] Planes = new CullPlane[6];
    /// <summary>The sun's travel over the longest shadow the cascades bake. Zero with no sun.</summary>
    public Vector3 SunSweep;
    /// <summary>The first <see cref="LampCount"/> lamps whose lit sphere reaches the view.</summary>
    public readonly Vector3[] LampPos = new Vector3[CasterPass.MaxCubes];
    /// <inheritdoc cref="LampPos"/>
    public readonly float[] LampReach = new float[CasterPass.MaxCubes];
    public int LampCount;
    public bool On;
    /// <summary>The object holds a light part, scanned once per part range.</summary>
    public readonly bool[] HasLight = new bool[SceneDb.MaxObjects];
}

/// <summary><c>Gob::Animate</c> @0x59d8c0's gate: past the behaviour solve, an object out of view with a model not
/// effect-classified advances no clock and fires no event. Alloy's view is the frustum, not the engine's BSP flag,
/// widened by the shadows and moving lights it carries in. Off with <c>alloy.animdist</c> 0. ponytail: <c>Gob+0xf0</c>
/// also exempts an object and has no writer but the constructor in the dump, so nothing here reads it.</summary>
public static unsafe partial class DbAnim
{
    private static ViewGateState _view = new();

    /// <summary>Latch the view from the frame's camera and sun for this world walk. No camera stands the gate down.</summary>
    private static void ViewBegin()
    {
        _view.On = false;
        if (!FrameInputs.HasCamera || !FrameInputs.HasPerspective) return;
        ViewLatch(FrameInputs.CameraPos, FrameInputs.SceneCamFwd, FrameInputs.CamFovy, FrameInputs.CamAspect,
                  FrameInputs.CamNear, FrameInputs.CamFar, FrameInputs.SunDir,
                  FrameInputs.HasSun && !FrameInputs.Interior, Shadows.Dist.V);
        ViewGatherLamps();
    }

    /// <summary>Hold objects against a camera at <paramref name="pos"/> (angles in degrees) and a sun travelling
    /// <paramref name="sun"/>, whose shadows run <paramref name="longestShadow"/> at most. Clears the lamps.</summary>
    private static void ViewLatch(Vector3 pos, Vector3 fwd, float fovy, float aspect, float near, float far,
                                  Vector3 sun, bool hasSun, float longestShadow)
    {
        ShadowMath.ReceiverPlanes(pos, fwd, fovy, aspect, near, far, 0f, _view.Planes);
        _view.SunSweep = hasSun ? sun * longestShadow : Vector3.Zero;
        _view.LampCount = 0;
        _view.On = true;
    }

    /// <summary>Take the lamps that can shade the view, by the test the cube pass claims a slot with. ponytail:
    /// the first ones past the cap and not the nearest; ranking them as the cube pass does is the upgrade if an
    /// area holds more lit lamps in view than it has cube slots.</summary>
    private static void ViewGatherLamps()
    {
        ViewGateState g = _view;
        g.LampCount = 0;
        for (int i = 0; i < FrameInputs.LocalLightCount && g.LampCount < g.LampPos.Length; i++)
        {
            Vector3 p = FrameInputs.LocalLightPos[i];
            float reach = FrameInputs.LocalLightRange[i];
            if (!ShadowMath.ShadowReaches(g.Planes, p, reach, p, reach)) continue;
            g.LampPos[g.LampCount] = p;
            g.LampReach[g.LampCount++] = reach;
        }
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
        return !ReachesView(new Vector3(s.X, s.Y, s.Z), s.W);
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

    /// <summary>Can a sphere, or a shadow the sun or a lit lamp casts of it, land in the view? Conservative, as
    /// the gather's own test is: a false here casts on no visible pixel.</summary>
    private static bool ReachesView(Vector3 c, float r)
    {
        ViewGateState g = _view;
        if (ShadowMath.ShadowReaches(g.Planes, c, r, c + g.SunSweep, r)) return true;
        for (int k = 0; k < g.LampCount; k++)
        {
            float touch = g.LampReach[k] + r;
            if ((c - g.LampPos[k]).LengthSquared() < touch * touch
                && ShadowMath.CubeShadowReaches(g.Planes, c, r, g.LampPos[k], g.LampReach[k])) return true;
        }
        return false;
    }
}
