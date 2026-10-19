using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>The API the Aurora seam calls. Every member mirrors one <c>CAurObject</c>/<c>CAurScene</c> slot
/// that moves records on the game thread, so a slot implementation in <c>Seam/ObjectSlots*.cs</c> is a handle
/// resolve plus one call here. Signatures follow the engine's (tools/seam/aurora-seam-vtables.tsv), with a
/// <see cref="DbId"/> for <c>this</c>. Game thread only, allocation-free, every setter marks its own record.</summary>
public static unsafe partial class DbAnim
{
    // ---- CAurObject slot 7: PlayAnimation(char const*, float, int, float) ----

    /// <summary><c>Gob::PlayAnimation</c> @0x49a750. <paramref name="flags"/> are the engine's own
    /// <see cref="RunFlags"/> bits as the game passes them; <paramref name="speed"/> multiplies the object's
    /// animation-speed lerp, and <paramref name="fadeTime"/> is clamped to [0, length]. A null, empty or
    /// <c>"NULL"</c> name clears the queue. Returns the engine's int: 0 = nothing was played.</summary>
    public static int PlayAnimation(DbId obj, string? anim, float speed, int flags, float fadeTime)
        => PlayAnimationCore(obj, anim, speed, flags, fadeTime);

    /// <summary><c>Gob::GetAnimation</c> (slot 8): the RUN playing <paramref name="anim"/>, or null. The two
    /// out-params are the engine's, <b>in its order</b>: <paramref name="time"/> is -1 when nothing is playing
    /// it, <paramref name="length"/> -1 when the name does not resolve at all. The handle is an opaque
    /// non-null token -- a zeroed native block, never to be dereferenced as an engine <c>Animation*</c>.</summary>
    public static nint GetAnimation(DbId obj, string? anim, out float time, out float length)
        => GetAnimationCore(obj, anim, out time, out length);

    /// <summary><c>Gob::LoadAddInAnimations</c> (slot 9): the second animation source searched ahead of the
    /// object's own model (the engine's <c>Gob+0x80</c>). 0 = the model would not load.</summary>
    public static int LoadAddInAnimations(DbId obj, string? resref) => LoadAddIn(obj, resref);

    /// <summary><c>Gob::RemoveAddInAnimations</c> (slot 10).</summary>
    public static void RemoveAddInAnimations(DbId obj) => RemoveAddIn(obj);

    /// <summary><c>Gob::IsAnimating</c> (slot 77). The four setters behind the gates are one entry,
    /// <c>SetAnimationsEnabled</c> in <c>DbAnim.Seam.cs</c>.</summary>
    public static bool IsAnimating(DbId obj) => Flag(obj, ObjAnim.Animating);

    /// <summary><c>Gob::SetSynchronizeAnimations</c> (slot 78): while set, the object's run queue takes its
    /// times from the object it is attached to rather than advancing on its own -- what keeps a head, a cloak
    /// and a held weapon on the same frame as the body.</summary>
    public static void SetSynchronizeAnimations(DbId obj, bool on) => SetSync(obj, on);

    /// <summary>The object's visual-transform scale (the engine's <c>Gob+0x1c0</c> <c>LerpFloat</c>), which an
    /// ATTACHED object multiplies into its own composed scale -- the one placement term <c>Part::GetWorld</c>
    /// does not carry.</summary>
    public static void SetVisualScale(DbId obj, float scale, float overSeconds)
        => SetVisScale(obj, scale, overSeconds);

    /// <summary>The name of the run that is currently dominating the queue, or null (the engine's
    /// <c>CurrentAnimation</c>, which <c>PlayAnimation("current", ...)</c> reuses).</summary>
    public static string? CurrentAnimation(DbId obj) => CurrentName(obj);

    /// <summary>The object was handed a script visual transform: from now on its compose places it by the
    /// adjusted getters, as <c>Scene::AddPartsToDrawBuckets</c> @0x5751d0 places a gob.</summary>
    public static void SetVisualTransformed(DbId obj)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        _vt[obj.Index] = true;
        Moved(obj.Index);
    }

    // ---- CAurObject slots 74/75: the look-at run ----

    // ---- the event path (Gob-only slots 11 / 83 / 84) ----

    /// <summary><c>Gob::RegisterCallBack</c> @0x497db0 (slot 11). seam-obj's slot 11 owns the LIVE list
    /// (<c>CallbackList</c>, the engine's own array at the shell's +0x460); this entry is the off-device one
    /// the self-checks use. <paramref name="fn"/> is a <c>void (*)(CAurObject*, char const*, void*)</c>, and
    /// the returned <c>CallBack*</c> is a real 0x30-byte block laid out as the engine's. Null when full.</summary>
    public static nint RegisterCallBack(DbId obj, nint fn, byte* name, nint data)
        => CbRegister(obj, fn, name, data);

    /// <summary><c>Gob::DeregisterCallback</c> @0x497e80 (slot 83).</summary>
    public static void DeregisterCallback(DbId obj, nint cb) => CbDeregister(obj, cb);

    // ---- CAurScene: the drive points ----

    /// <summary><c>Scene::IsAnimating</c> (Scene-only slot 81).</summary>
    public static bool IsSceneAnimating(DbId scene) => SceneGate(scene);

    // ---- lifetime (seam-obj calls these from object creation and retirement) ----

    /// <summary>The object record exists and has its part range: seat its animation state. Idempotent.</summary>
    public static void ObjectCreated(DbId obj) => OnCreate(obj);

    /// <summary>The game destroyed the object: drop its run queue, its callbacks, its look-at and its pose
    /// scratch. Called BEFORE the record's slot is freed, so the columns are still readable.</summary>
    public static void ObjectRetired(DbId obj) => OnRetire(obj);

    /// <summary>The object's part tree was replaced (a model or LOD change): every cached per-animation node
    /// binding for it is re-taken on the next run.</summary>
    public static void TreeChanged(DbId obj) => OnTreeChanged(obj);

    // ---- LOD (Scene/GobLod.cs over DB objects) ----

    /// <summary>The object's LOD model set, as <c>NewCAurObject</c> resolved it (seam-obj): level 0 is
    /// <see cref="SceneDb.ObjModel"/>, and these are levels 1 and 2. Null for a model with no LOD siblings.</summary>
    public static void SetLodModels(DbId obj, ModelDef? level1, ModelDef? level2)
        => LodModels(obj, level1, level2);

    /// <summary>The object's two LOD distances (the engine's <c>Gob+0x444</c>/<c>+0x448</c>).</summary>
    public static void SetLodDistances(DbId obj, float near, float far) => LodDistances(obj, near, far);

    // ---- pose outputs the renderer reads (seam-feed) ----

    /// <summary>Part <paramref name="part"/>'s 64-bone palette, written into <paramref name="dst"/>
    /// (<c>64 * 16</c> floats, column-major affine per bone -- see <c>Db/DbPose.LAYOUT.md</c>). False = the
    /// part is not skinned or its bind pose is missing, and it must not be drawn skinned.</summary>
    internal static bool SkinPalette(int part, BonePalette* dst) => SkinSolve(part, dst);
}
