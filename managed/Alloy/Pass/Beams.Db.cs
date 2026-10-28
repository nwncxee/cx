using CX.Alloy.Db;

namespace CX.Alloy;

/// <summary>Light beams out of the scene database: membership is <see cref="DbInputs.IsBeam"/> (the mesh's own
/// <c>beaming</c> keyword, interned on its <see cref="DrawRecipe"/>) and geometry is the part's arena range,
/// the mesh itself at its own placement. The engine instead baked a silhouette volume per (mesh, light) pair;
/// with no such bake this draws the whole mesh and derives the extrude flag from the NORMAL, denser by its caps.</summary>
internal static partial class Beams
{
    /// <summary>Beaming parts this scene holds, for the period line: what <see cref="EnrollDb"/> walked past.</summary>
    private static int _owned;
    /// <summary>The <c>DbSetGen</c> and scene the DB table was walked for. The pair is the enrolment's whole
    /// change gate.</summary>
    private static int _dbGen = -1;
    private static uint _dbScene;
    private static int _dbSkipped;

    /// <summary>The population, in DB mode: every live part of this scene whose recipe carries
    /// <see cref="RecipeFlag.Beaming"/>. Rebuilt each frame from the database, like the engine path rebuilt it
    /// from the bucket -- a walk of the part table is one flag test per part and the table is the area's.</summary>
    internal static void EnrollDb(DbId scene)
    {
        if (_off || Disabled) return;
        // Change-driven, like the draw order beside it: a beam is baked static geometry, so the membership can
        // only move when the part SET moves. DbSetGen is that edge, and a still frame re-walks nothing.
        if (_dbGen == AlloyRenderer.DbSetGen && _dbScene == scene.Value) return;
        _dbGen = AlloyRenderer.DbSetGen;
        _dbScene = scene.Value;
        _count = 0;
        _owned = 0;
        // Bounded by the table's high-water mark, not by its capacity: the walk is one flag test per part slot
        // ever allocated, and it runs only on a set edge.
        int top = SceneDb.Parts.High;
        for (int p = 0; p < top && _count < MaxBeams; p++)
        {
            if (!SceneDb.Parts.LiveAt(p)) continue;
            int recipe = SceneDb.PartRecipe[p];
            if (recipe < 0 || (DrawRecipe.Flags(recipe) & RecipeFlag.Beaming) == 0) continue;
            DbId obj = SceneDb.PartObj[p];
            if (!SceneDb.Objects.Valid(obj) || SceneDb.ObjScene[obj.Index].Value != scene.Value) continue;
            if (!SceneDb.PartEnabled[p] || !SceneDb.ObjVisible[obj.Index]) continue;
            _owned++;
            int range = DrawRecipe.Range(recipe);
            if (range < 0) { _refused++; continue; }
            ref Beam b = ref _beams[_count++];
            b.Part = p; b.Range = range;
            _enrolled++;
        }
    }

    /// <summary>The beam vertex stage: the frontal projection is the mesh normal, which the arena's static
    /// format carries at location 2.</summary>
    private static string BeamDbVs => ShaderSource.Get("cx_beamdb_v.glsl");
}
