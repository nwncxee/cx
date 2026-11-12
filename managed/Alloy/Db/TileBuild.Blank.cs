using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>Which tiles are blank: the solid filler a tileset pads an area with, where nothing stands and nobody goes.</summary>
public static partial class TileBuild
{
    /// <summary>surfacemat.2da rows that say "solid, nothing here": NotDefined 0, Obscuring 2, Nonwalk 7. Deep water,
    /// lava and a pit are not walked either, but are places. ponytail: stock row ids; read <c>Label</c> if a server renumbers.</summary>
    private const ulong SolidSurfaces = (1UL << 0) | (1UL << 2) | (1UL << 7);

    /// <summary>Is <paramref name="walk"/> a walkmesh with faces, every one of them solid? A model with no walkmesh
    /// face says nothing either way, and is not blank.</summary>
    internal static bool Blank(ModelDef walk)
    {
        int faces = 0;
        foreach (NodeDef n in walk.Nodes)
        {
            if (n.Kind != NodeKind.Aabb || n.Mesh == null) continue;
            foreach (FaceDef f in n.Mesh.Faces)
            {
                if ((uint)f.Surface >= 64 || ((SolidSurfaces >> f.Surface) & 1UL) == 0) return false;
                faces++;
            }
        }
        return faces > 0;
    }

    /// <summary>Is a placement of <paramref name="tileDef"/> blank, by its own <c>aabb</c> node or else its <c>.wok</c>?
    /// Answered once per tile model.</summary>
    private static bool BlankTile(ModelDef tileDef)
    {
        MergePlan plan = Plan(tileDef);
        if (plan.Blank is { } known) return known;
        bool blank = false;
        if (plan.AabbNodes > 0) blank = Blank(tileDef);
        else if (ModelCache.Acquire(tileDef.Name + ModelFeed.WokSuffix, out _) is { } wok)
        {
            blank = Blank(wok);
            ModelCache.Release(wok);
        }
        plan.Blank = blank;
        return blank;
    }
}
