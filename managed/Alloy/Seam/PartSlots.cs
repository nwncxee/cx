using CX.Alloy.Db;

namespace CX.Alloy.Seam;

/// <summary>What a <see cref="PartSlots.ForEach"/> visitor does with one of an object's part-and-texture-slot
/// pairs. The implementation is a struct, so each call site gets its own specialisation: the walk allocates
/// nothing and dispatches nothing virtually.</summary>
internal interface IPartSlots
{
    /// <summary>Part <paramref name="part"/>, texture slot <paramref name="slot"/>. A non-zero answer means
    /// "this one changed", which is what the walk counts and what <see cref="Done"/> is told.</summary>
    int At(int part, int slot);

    /// <summary>One object's own part range has been walked and <paramref name="changed"/> of its slots took.
    /// Marking the record belongs here: a graft chain marks each piece against its own count, not the
    /// chain's total.</summary>
    void Done(DbId obj, int changed);
}

/// <summary>The walk every per-object texture edit runs: this object's part range crossed with one texture
/// slot or all of them, then -- for the forms that follow grafts -- every piece <c>Gob::Append</c> added. A
/// creature's torso, arms and cloak are each a child object, so the base range alone is not the wardrobe.</summary>
internal static class PartSlots
{
    /// <summary>How deep a composition may be grafted before the walk gives up. A creature is one hop; the
    /// bound is a cycle guard, not a budget.</summary>
    internal const int MaxGraft = 8;

    /// <summary>Texture slots a part carries -- the stride of <see cref="SceneDb.PartTexId"/>, slot 0 the
    /// diffuse one.</summary>
    internal const int Slots = 4;

    /// <summary>Walk <paramref name="obj"/>, and with <paramref name="grafts"/> everything grafted onto it.
    /// A negative <paramref name="slot"/> takes all <see cref="Slots"/>. Returns the total the visitor
    /// reported changed, grafts included.</summary>
    internal static int ForEach<T>(DbId obj, int slot, bool grafts, ref T v, int depth = 0)
        where T : struct, IPartSlots, allows ref struct
    {
        if (depth > MaxGraft || !SceneDb.Objects.Valid(obj)) return 0;
        int first = SceneDb.ObjPartFirst[obj.Index], count = SceneDb.ObjPartCount[obj.Index];
        int lo = slot < 0 ? 0 : slot, hi = slot < 0 ? Slots : slot + 1;
        int changed = 0;
        for (int p = first; p < first + count; p++)
            for (int s = lo; s < hi; s++)
                changed += v.At(p, s);
        v.Done(obj, changed);
        if (!grafts) return changed;
        int total = changed;
        for (DbId c = SceneDb.ObjSeamAppendHead[obj.Index]; !c.IsNull; c = SceneDb.ObjSeamAppendNext[c.Index])
        {
            if (!SceneDb.Objects.Valid(c)) break;
            total += ForEach(c, slot, grafts, ref v, depth + 1);
        }
        return total;
    }
}
