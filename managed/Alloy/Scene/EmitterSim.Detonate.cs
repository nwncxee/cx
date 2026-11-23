using CX.Alloy.Db;
using CX.Alloy.Mdl;

namespace CX.Alloy;

internal static unsafe partial class EmitterSim
{
    /// <summary><c>Detonate</c> @0x4aa770, which <c>MdlNodeEmitter::InternalCreateInstance</c> registers for
    /// every Explosion emitter on the Gob it is built with: an event <c>Gob::DoEvent</c>'s <c>strcasecmp</c>
    /// matches sets the burst request of each one, read and cleared by that emitter's next step.</summary>
    internal static void DetonateEvent(DbId obj, byte* name)
    {
        if (name == null || !Seam.CallbackList.NameIs((nint)name, DetonateName)) return;
        RequestBursts(obj, 0);
    }

    /// <summary>The name the callback is registered under.</summary>
    private const string DetonateName = "Detonate";

    /// <summary>Request a burst from <paramref name="obj"/>'s Explosion emitters and its appended pieces', which
    /// <c>Gob::Append</c> instances on the same Gob. A reference instance is an object with a Gob of its own.</summary>
    private static void RequestBursts(DbId obj, int depth)
    {
        if (depth >= SceneDb.PieceDepthCap || !SceneDb.Objects.Valid(obj)) return;
        int first = SceneDb.ObjPartFirst[obj.Index], count = SceneDb.ObjPartCount[obj.Index];
        for (int p = first; p < first + count; p++)
            if ((uint)p < (uint)SceneDb.MaxParts && IsBlast(p) && !Expanded(p, first)) SceneDb.PartDetonate[p] = 1;
        foreach (DbId c in SceneDb.Pieces(obj))
            if (!IsReferenceInstance(c)) RequestBursts(c, depth + 1);
    }

    /// <summary>Is part <paramref name="p"/> an Explosion emitter, the one kind that registers the callback?</summary>
    private static bool IsBlast(int p)
    {
        ModelDef? m = SceneDb.PartModel[p];
        int n = SceneDb.PartNode[p];
        return m != null && (uint)n < (uint)m.Nodes.Length && m.Nodes[n].Emitter is { } d
               && ModeOf(d.Update) == ModeExplosion;
    }

    /// <summary>Is part <paramref name="p"/> inside a reference node's expansion, the one place a part's model
    /// differs from its parent's? <c>ExpandReferenceParts</c> instances it with no Gob, so its emitters register
    /// nothing. A parent always precedes its child.</summary>
    private static bool Expanded(int p, int first)
    {
        for (int c = p; ;)
        {
            int a = SceneDb.PartParent[c];
            if (a < first || a >= c) return false;
            if (!ReferenceEquals(SceneDb.PartModel[a], SceneDb.PartModel[c])) return true;
            c = a;
        }
    }

    /// <summary>Is piece <paramref name="c"/> the instance its attach part references, rather than appended?</summary>
    private static bool IsReferenceInstance(DbId c)
    {
        int at = SceneDb.ObjAttachPart[c.Index];
        return (uint)at < (uint)SceneDb.MaxParts && SceneDb.PartRefObj[at].Value == c.Value;
    }
}
