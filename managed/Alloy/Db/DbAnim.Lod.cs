using System;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>Animation-and-LOD distance work over DB objects: the level each object shows, from the engine's
/// own two thresholds plus CX's hysteresis (<see cref="ChooseLod"/>). Every alternate's parts are already
/// records in ONE part range tagged with <see cref="SceneDb.PartLodTree"/>, so a swap is a byte and both sides
/// are live at the instant it flips. The hysteresis is kept, because popping on a boundary is vanilla's bug.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>Per object: the two LOD distances the game set (<c>Gob+0x444</c>/<c>+0x448</c>) and whether its
    /// part range holds more than one alternate.</summary>
    private static readonly float[] _lodNear = new float[SceneDb.MaxObjects];
    private static readonly float[] _lodFar = new float[SceneDb.MaxObjects];
    private static readonly byte[] _lodLevels = new byte[SceneDb.MaxObjects];

    private static float* _lodScale;
    private static bool _lodResolved;
    private static int _lodSwaps, _lodHeld, _lodSettled;

    /// <summary>Bumped on every LOD swap, monotonically. A swap changes which of an object's two live
    /// alternates the renderer draws, so it can change which seated slot the draw order carries -- and it is
    /// not a slot-set edge (both records stay live), so <c>DbSetGen</c> and the order COUNT can both stand
    /// still across one. A reader keyed on the ordered set needs this in its key, not a period counter.</summary>
    internal static int LodGen;

    /// <summary>How many objects have an alternate at all. Zero means the whole choice is skipped, which is
    /// also what keeps a GUI scene and the self-checks out of it.</summary>
    private static int _lodWanted;

    /// <summary>The object's index is being (re)used: forget its LOD set, or a recycled index inherits
    /// <c>_lodLevels >= 2</c>, the choice writes <see cref="SceneDb.ObjLodShown"/> 1 or 2 for an object whose
    /// parts are all alternate 0, and the feed then refuses every one of them -- an INVISIBLE object with
    /// nothing logged anywhere.</summary>
    private static void LodForget(int i)
    {
        if (_lodLevels[i] >= 2) _lodWanted--;
        _lodLevels[i] = 0;
        SceneDb.ObjLodShown[i] = 0;
    }

    private static void LodModels(DbId obj, ModelDef? level1, ModelDef? level2)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        byte was = _lodLevels[obj.Index];
        byte now = (byte)(level2 != null ? 3 : level1 != null ? 2 : 1);
        _lodLevels[obj.Index] = now;
        if (was < 2 && now >= 2) _lodWanted++;
        else if (was >= 2 && now < 2) _lodWanted--;
    }

    private static void LodDistances(DbId obj, float near, float far)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        _lodNear[obj.Index] = near;
        _lodFar[obj.Index] = far;
    }

    /// <summary>The engine's two LOD globals, resolved once. They are plain <c>.bss</c> values the config
    /// writes, and they exist in DB mode too -- the engine is loaded, it just owns no scene. Without them the
    /// choice stands down to "always level 0", which is said once.</summary>
    private static void LodResolve()
    {
        if (_lodResolved) return;
        _lodResolved = true;
        _lodScale = (float*)Native.Sym(SymbolMap.LodScaleFactor);
        if (_lodScale == null)
            _log?.Always("DbAnim: " + SymbolMap.LodScaleFactor + " did not resolve -- .lod distances are taken at "
                       + "scale 1, not the player's LOD scale setting");
    }

    /// <summary>Load and switch <c>.lod</c> alternates. CX's own knob, not <c>g_bEnableLOD</c>: the engine's
    /// option is a player toggle for a feature nobody wants off, so only <c>alloy.authoredlod</c> in cx.cfg stands it down.</summary>
    internal static bool LodEnabled => Renderer.AuthoredLod.V;

    /// <summary>The eye the last choice was taken from, and the object-table dirty count it saw. The bands
    /// only move when the camera moves or an object does, so a settled frame costs the scan nothing -- the
    /// same change-driven rule the rest of the build follows.</summary>
    private static Vector3 _lodEye = new(float.NaN);
    private static long _lodDirtyAt = -1;
    /// <summary>How far the eye may drift before the bands are re-tested. A tenth of a unit is well inside the
    /// hysteresis band, so nothing can pop because of it.</summary>
    private const float LodEyeEpsilon = 0.1f;

    /// <summary>Choose the level for every object of the scene that has one. Once per animate, after the
    /// composes, so the distance is taken against the placement the frame will draw.</summary>
    private static void LodChoose(int sceneIndex)
    {
        if (_lodWanted <= 0) return;
        // The eye of the view the frame is being built for -- SceneFeed's camera terms, the one place the
        // placement of the player's view reaches this side. No camera yet: the bands cannot be measured, so
        // every object holds the level it has.
        if (!FrameInputs.HasCamera) { _lodSettled++; return; }
        Vector3 eye = FrameInputs.CameraPos;
        long dirty = SceneDb.Objects.DirtyMark;
        bool eyeMoved = !((_lodEye - eye).LengthSquared() <= LodEyeEpsilon * LodEyeEpsilon);
        if (!eyeMoved && dirty == _lodDirtyAt) { _lodSettled++; return; }
        _lodEye = eye;
        _lodDirtyAt = dirty;
        LodResolve();
        bool enabled = LodEnabled;
        // ponytail: the engine's scale-factor setting still sets the distances; unresolved or unset reads as 1
        float scale = _lodScale != null && *_lodScale > 0f ? *_lodScale : 1f;
        float hyst = Renderer.LodHyst.V;
        int high = SceneDb.Objects.High;
        for (int i = 1; i < high; i++)
        {
            if (!SceneDb.Objects.LiveAt(i)) continue;
            if (_lodLevels[i] < 2) continue;
            if (SceneDb.ObjScene[i].Index != sceneIndex) continue;
            Vector4 s = SceneDb.ObjSphere[i];
            float d2 = (new Vector3(s.X, s.Y, s.Z) - eye).LengthSquared();
            int cur = SceneDb.ObjLodShown[i];
            int want = ChooseLod(d2, _lodNear[i], _lodFar[i], cur, enabled, scale, hyst,
                                     hasAlternate: true);
            if (want > _lodLevels[i] - 1) want = _lodLevels[i] - 1;
            if (!enabled) want = 0;   // switched off mid-session: back to the authored model, not frozen where it was
            if (want == cur) { _lodHeld++; continue; }
            SceneDb.ObjLodShown[i] = (byte)want;
            _lodSwaps++; LodGen++;
            // Both alternates' records are live, so the only thing the renderer has to learn is which one to
            // draw -- an object edge, which the feed folds into its parts.
            SceneDb.Objects.Mark(i, Dirty.Tree);
        }
    }

    /// <summary>The LOD layer's period numbers. A swap count that tracks camera movement and a held count that
    /// dominates it is the hysteresis working.</summary>
    internal static string LodPeriod()
    {
        string s = $"{_lodSwaps} LOD swap(s), {_lodHeld} held, {_lodSettled} frame(s) skipped (nothing moved), {_lodWanted} object(s) with .lod alternates";
        _lodSwaps = 0; _lodHeld = 0; _lodSettled = 0;
        return s;
    }

    /// <summary>The level CX wants, from the engine's own two thresholds plus hysteresis. Pure.
    /// <para><paramref name="hyst"/> is a fraction of DISTANCE, so it enters squared: rising past a boundary
    /// needs <c>d2 &gt; T (1+h)^2</c>, falling back needs <c>d2 &lt; T / (1+h)^2</c>, and in between the level
    /// holds. At most one level per call, matching the engine's own single-step branches.</para></summary>
    internal static int ChooseLod(float d2, float near, float far, int cur, bool enabled, float scaleFactor,
                                  float hyst, bool hasAlternate)
    {
        // The engine's own stand-downs, in its order: no LOD, no alternate root, no distance yet.
        if (!enabled || !hasAlternate || d2 < 0f || !(scaleFactor > 0f)) return cur;
        if (cur < 0 || cur > 2) return cur;
        float t1 = near * scaleFactor, t2 = far * scaleFactor;
        float t1sq = t1 * t1, t2sq = t2 * t2;
        int plain = d2 > t2sq ? 2 : d2 > t1sq ? 1 : 0;
        if (plain == cur) return cur;
        float up = (1f + hyst) * (1f + hyst);
        if (plain > cur)
        {
            float bound = cur == 0 ? t1sq : t2sq;
            return d2 > bound * up ? cur + 1 : cur;
        }
        float down = cur == 1 ? t1sq : t2sq;
        return d2 * up < down ? cur - 1 : cur;
    }
}
