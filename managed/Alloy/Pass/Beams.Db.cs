using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Light beams out of the scene database: membership is <see cref="DbInputs.IsBeam"/> (the mesh's own
/// <c>beaming</c> keyword, interned on its <see cref="DrawRecipe"/>) and geometry is the part's arena range,
/// the mesh itself at its own placement. The engine instead baked a silhouette volume per (mesh, light) pair;
/// with no such bake this draws the whole mesh and derives the extrude flag from the NORMAL, denser by its caps.</summary>
internal static unsafe partial class Beams
{
    /// <summary>Did the last enrolment come from the database? Decides which program and which draw the pass
    /// runs. The two cannot mix: <see cref="EnrollDb"/> is the live one, <see cref="Enroll"/> the engine-side
    /// enrolment nothing calls now.</summary>
    private static int _owned;
    /// <summary>The <c>DbSetGen</c> and scene the DB table was walked for. The pair is the enrolment's whole
    /// change gate.</summary>
    private static int _dbGen = -1;
    private static uint _dbScene;
    private static uint _progDb;
    private static bool _dbFailed;
    private static int _locDbModel = -1, _locDbView = -1, _locDbProj = -1, _locDbSrc = -1,
                       _locDbZClip = -1, _locDbColor = -1;
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

    /// <summary>One DB beam's draw arguments: three integers off the arena, exactly as the world draw takes
    /// them. False means the range has no resident geometry yet -- the beam is skipped and its neighbours
    /// draw.</summary>
    private static bool StageDb(int i, int k)
    {
        ref Beam b = ref _beams[i];
        if (!MeshArena.Draw(b.Range, 0, out uint vao, out int baseVertex, out nint off, out int count, out _)
            || count <= 0 || vao == 0)
        { _dbSkipped++; return false; }
        _sCount[k] = count;
        _sOff[k] = off;
        _sBase[k] = baseVertex;
        _sVao[k] = vao;
        return true;
    }

    /// <summary>The GL VAO name each staged beam draws from: the arena hands out a name directly, so the
    /// draw groups on the name.</summary>
    private static readonly uint[] _sVao = new uint[MaxBeams];

    /// <summary>The DB draw: one call per beam, because each part carries its own model matrix (the engine's
    /// baked volumes were all in one space, which is what let it batch). A handful of beams per area, so the
    /// per-draw uniform is cheaper than the bake it replaces.</summary>
    private static void DrawDb(Matrix4x4* view, Matrix4x4* proj, in Vector3 dir, float alpha, int staged)
    {
        Gl.UseProgram(_progDb);
        Gl.UniformMatrix4fv(_locDbView, 1, 0, (float*)view);
        Gl.UniformMatrix4fv(_locDbProj, 1, 0, (float*)proj);
        float lin = MathF.Pow(TintRgb, AlloyLights.SrgbGamma);
        float* c = stackalloc float[4] { lin, lin, lin, alpha };
        Gl.Uniform4fv(_locDbColor, 1, c);
        float* src = stackalloc float[4] { dir.X, dir.Y, dir.Z, -1f };
        Gl.Uniform4fv(_locDbSrc, 1, src);
        Gl.Uniform1f(_locDbZClip, WorldZClip);
        new PassState(depth: true, depthWrite: false, blend: true,
                      blendSrc: GlSrcAlpha, blendDst: GlOne).Apply();
        uint vao = 0;
        for (int k = 0; k < staged; k++)
        {
            int i = _sBeam[k];
            if (_sVao[k] != vao) { vao = _sVao[k]; Gl.BindVertexArray(vao); }
            Matrix4x4 m = SceneDb.PartWorld[_beams[i].Part];
            Gl.UniformMatrix4fv(_locDbModel, 1, 0, (float*)&m);
            Gl.DrawElementsBaseVertex(Gl.TRIANGLES, _sCount[k], GlUnsignedShort,
                                      (void*)_sOff[k], _sBase[k]);
        }
        Gl.BindVertexArray(0);
    }

    private static bool EnsureDbProgram()
    {
        if (_progDb != 0) return true;
        if (_dbFailed) return false;
        _progDb = GlProgram.Graphics(BeamDbVs, BeamFs, _log, "BeamsDb");
        if (_progDb == 0)
        {
            _dbFailed = true;
            _log?.Always("Beams: the DB-mode beam program did not build -- light beams are NOT DRAWN in this "
                       + "session (there is no engine beam draw to fall back to in DB mode)");
            return false;
        }
        _locDbModel = GlProgram.Loc(_progDb, "uModel");
        _locDbView = GlProgram.Loc(_progDb, "uView");
        _locDbProj = GlProgram.Loc(_progDb, "uProj");
        _locDbSrc = GlProgram.Loc(_progDb, "uProjSrc");
        _locDbZClip = GlProgram.Loc(_progDb, "uZClip");
        _locDbColor = GlProgram.Loc(_progDb, "uColor");
        return true;
    }

    /// <summary>The beam vertex stage: the frontal projection is the mesh normal, which the arena's static
    /// format carries at location 2.</summary>
    private static string BeamDbVs => ShaderSource.Get("cx_beamdb_v.glsl");
}
