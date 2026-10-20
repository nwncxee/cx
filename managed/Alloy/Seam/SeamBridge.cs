using System;
using System.Numerics;
using CX.Alloy.Db;

namespace CX.Alloy.Seam;

/// <summary>The seam's one indirection point onto subsystems other files own -- the texture registry, hit
/// test, part bounds. Every entry starts as a counted refusal, so a slot whose subsystem nobody wired reports
/// itself instead of returning a plausible zero; wiring it is one assignment. The entries are cold (a texture
/// swap, a click's cast), never per-draw, and a slot that calls its subsystem directly does not belong here.</summary>
public static class SeamBridge
{
    private static readonly long[] _refusals = new long[16];
    private static readonly bool[] _named = new bool[16];

    private static bool Refuse(int slot, string what)
    {
        _refusals[slot]++;
        if (!_named[slot])
        {
            _named[slot] = true;
            Facade.Log?.Always($"seam: {what} is not wired on this build -- the call is refused, not faked (SeamBridge)");
        }
        return false;
    }

    /// <summary>One material parameter, in the ENGINE's argument order (ADR 0026):
    /// <c>Material::SetMaterialParam</c> @0x4c5d70 matches the first against each parameter's name (to 64
    /// characters), takes the second as the value (<b>empty RESETS it</b> to the material's declared default)
    /// and uses the third to filter on the SharedMaterial's name (to 16).</summary>
    public static Action<DbId, string?, string?, string?> SetMaterialParam =
        static (o, paramName, value, materialFilter) => Refuse(2, "TexRegistry.SetMaterialParam (CX-117)");

    /// <summary><c>Gob::SetTextureAttributes</c> -- the <c>.txi</c> RESOURCE named <c>txiName</c> applied to
    /// every texture of the object named <c>texName</c> (<c>CAurTextureBasic::Init</c> @0x488940 does
    /// <c>AurResGet(txiName, ".txi")</c>; it is a resref, not inline text).</summary>
    public static Func<DbId, string?, string?, int> SetTextureAttributes =
        static (o, texName, txiName) => Refuse(4, "TexRegistry.SetTextureAttributes (CX-117)") ? 1 : 0;

    /// <summary>Bilinear/nearest override for every texture of the object.</summary>
    public static Action<DbId, bool> SetTextureFilterOverride = static (o, on) => Refuse(5, "TexRegistry.SetTextureFilterOverride (CX-117)");

    /// <summary>The object's bounds in object space, from its part bounds -- what
    /// <c>GetBoundingBox</c> / <c>GetMinimumBoundingBox</c> / <c>GetMinimumBoundingSphere</c> answer. Returns
    /// false when the part tree has no bounds yet (the slot then answers an empty box, and says so once).</summary>
    public delegate bool BoundsFn(DbId obj, bool minimumOnly, out Vector3 min, out Vector3 max);

    public static BoundsFn ObjectBounds = RefusedBounds;

    private static bool RefusedBounds(DbId obj, bool minimumOnly, out Vector3 min, out Vector3 max)
    {
        min = default; max = default;
        return Refuse(7, "the part tree's object bounds (CX-114)");
    }

    /// <summary>The light records a light part owns, so <c>GetMaximumLightRadius</c> and
    /// <c>ControlPartLightColor</c> have something to read. Returns the largest range of the object's lights.</summary>
    public static Func<DbId, float> MaxLightRadius = static o => { Refuse(8, "the DB light set's radius query (CX-116)"); return 0f; };

    /// <summary>Recolour the light a named part owns.</summary>
    public static Action<DbId, string?, Vector3> SetPartLightColour = static (o, p, c) => Refuse(9, "the DB light set's recolour (CX-116)");

    /// <summary>Does the model carry a skinned mesh (<c>Gob::ContainsSkinnedMeshes</c>)? The
    /// engine re-walks the part tree on every call; the answer cannot change without a tree change, so the
    /// seam asks once at construction and keeps a bit.</summary>
    public static Func<CX.Alloy.Mdl.ModelDef, bool> HasSkinnedMesh =
        static m => { Refuse(10, "the model's skinned-mesh query (CX-112)"); return false; };

    /// <summary>A part's weight for <c>Gob::GetRandomPart</c>'s area-weighted draw.</summary>
    public static Func<int, float> PartArea = static p => { Refuse(11, "the part's area query (CX-114)"); return 0f; };

    /// <summary>Calls refused across every bridge entry, for the seam's period line. Each distinct entry
    /// also names itself once as it refuses; this is the running total.</summary>
    public static long Refused
    {
        get { long total = 0; foreach (long n in _refusals) total += n; return total; }
    }
}
