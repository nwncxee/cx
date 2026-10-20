using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The entry points the <c>CAurObject</c> animation slots call, in the spelling
/// <c>Seam/ObjectSlots.Anim.cs</c> uses, each one line over the runtime in the other <c>DbAnim*.cs</c> files.
/// <c>DbAnim.Api.cs</c> holds the rest of the surface, under the engine's own slot names.</summary>
public static unsafe partial class DbAnim
{
    /// <summary><c>Gob::Animate(float)</c> on ONE object: what the engine's per-gob animate did, for the paths
    /// that advance a single object (<c>CAurObject::AnimateExternal</c>, a GUI panel's model).</summary>
    public static void AnimateObject(DbId obj, float dt) => AnimateOneExternal(obj, dt);

    /// <summary>The animation gates: <c>Enable/DisableAnimations</c> (<paramref name="force"/> false) and
    /// <c>Enable/DisableForceAnimations</c> (true), which exempt an object from the SCENE's gate.</summary>
    public static void SetAnimationsEnabled(DbId obj, bool on, bool force)
        => SetFlag(obj, force ? ObjAnim.Forced : ObjAnim.Animating, on);

    /// <summary><c>Gob::BeginLookAtAnimate</c> (slot 74) in the ENGINE's argument order: @0x49d5b0 looks
    /// <paramref name="targetPart"/> up in the TARGET's +0x90 tree and <paramref name="ownPart"/> in its own.
    /// The floats are the yaw limit, the pitch limit, one with no consumer here, and the maximum distance,
    /// which <c>LookAtAnimation</c> @0x49d480 stores at +0x48..+0x54 via <c>ABS</c>, the first two clamped to 89.</summary>
    public static void BeginLookAt(DbId obj, DbId target, string? targetPart, string? ownPart,
                                  float yawLimitDeg, float pitchLimitDeg, float unresolved, float maxDistance)
        => LookAtBegin(obj, target, ownPart, targetPart, yawLimitDeg, pitchLimitDeg, maxDistance);

    /// <summary><c>Gob::EndLookAtAnimate</c> (slot 75).</summary>
    public static void EndLookAt(DbId obj) => LookAtEnd(obj);
}
