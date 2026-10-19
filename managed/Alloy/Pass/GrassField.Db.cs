using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Engine;

namespace CX.Alloy;

/// <summary>The grass field's patches, from the scene database. The rule is the engine's, term for term: a
/// face is grass when <c>AurGrassMaterialInUse</c> @0x5b4730 says its own material id is in use, and it
/// carries <c>(int)(area * g_GrassInfo[mat].density)</c> blades. The geometry is the part's OWN drawn mesh at
/// its own placement, so a blade can never stand on a face the frame does not show.</summary>
internal static unsafe partial class GrassField
{
    /// <summary>Parts one area may offer grass. A 32x32 outdoor area is a few thousand tile parts; a scene
    /// past this is counted and the rest is not enrolled, never silently dropped.</summary>
    private const int MaxDbParts = 8192;
    private static readonly int[] _dbList = new int[MaxDbParts];
    /// <summary>The patch run each enrolled part baked: its first patch index and how many (one per grass
    /// material on it). -1 = not baked yet, which is what the per-frame budget drains.</summary>
    private static readonly int[] _dbFirst = new int[MaxDbParts];
    private static readonly int[] _dbCount = new int[MaxDbParts];
    private static int _dbListN;
    /// <summary>The scene the list belongs to, and how far up the part table it has been walked. Tiles are
    /// created at area load, so the walk only ever extends -- and the area edge (<see cref="Forget"/>) is what
    /// drops the whole thing.</summary>
    private static uint _dbScene;
    private static int _dbWalked;
    private static int _dbEnrolled, _dbBakedParts, _dbGrassFaces, _dbOverflow;
    /// <summary>What the bake MET, so "no grass" can be told apart from "no grass materials": the faces
    /// tested, the highest material id any of them named, and how many grass-info records are in use.</summary>
    private static int _dbFacesTested, _dbMaxSurface = -1, _dbNamed;
    private const int NamedParts = 12;

    /// <summary>Scratch for one part's grass faces: nine floats and a blade count each, grown to the largest
    /// part met and then reused. Not on the hot path -- a part is baked once per area.</summary>
    private static GrassTriRec[] _dbVerts = Array.Empty<GrassTriRec>();
    private static int[] _dbBlades = Array.Empty<int>();

    /// <summary>Grass materials one part may carry. The tileset's own count is small; a part past this keeps
    /// the materials it has room for and is counted.</summary>
    private const int MaxPartMats = 8;

    /// <summary>The database's grass, for this frame: enrol what the area has added, bake what the budget
    /// allows, and mark every patch that is still live. Returns the patches sighted, as
    /// <see cref="Collect"/> does.</summary>
    private static int CollectDb(DbId scene, Matrix4x4* vp, ref int budget)
    {
        if (!SceneDb.Scenes.Valid(scene)) return 0;
        if (_dbScene != scene.Value) { _dbScene = scene.Value; _dbListN = 0; _dbWalked = 0; }
        EnrolDb(scene);
        int seen = 0;
        float far = FarDist();
        Vector3 eye = SceneFog.Origin(out _);
        Vector4[] planes = _binPlanes;
        {
        CullPass.Planes(in *vp, planes, AlloyRenderer.EyePos);
        for (int k = 0; k < _dbListN; k++)
        {
            int part = _dbList[k];
            if (!SceneDb.Parts.LiveAt(part)) continue;
            if (_dbFirst[k] < 0)
            {
                if (budget <= 0) continue;
                // Bake only what the frame can SHOW, which is what the engine path's bin walk does
                // (RefreshOwnedBins culls the scene's bins against this same matrix before anything is
                // interned). Without it the whole area -- every tile and the whole widened border ring --
                // was scattered the moment it loaded, which is resident blades nobody can see.
                if (!PartInView(part, planes, eye, far)) continue;
                budget--;
                BakeDbPart(k, part);
            }
            if (_dbFirst[k] < 0) continue;
            for (int i = 0; i < _dbCount[k] && seen < MaxPatches; i++)
            { _patches[_dbFirst[k] + i].Seen = _frame; seen++; }
        }
        }
        return seen;
    }

    /// <summary>Is this part's own sphere inside the view and inside the grass cut-off? The sphere is the
    /// part's model-space bound placed by its world matrix -- the same pair the cull reads.</summary>
    private static bool PartInView(int part, ReadOnlySpan<Vector4> planes, Vector3 eye, float far)
    {
        Vector3 centre = Vector3.Transform(SceneDb.PartCenter[part], SceneDb.PartWorld[part]);
        float radius = SceneDb.PartRadius[part];
        if (radius <= 0f) return false;
        // Distance first: it refuses most of a big area without touching the planes.
        float dx = centre.X - eye.X, dy = centre.Y - eye.Y, dz = centre.Z - eye.Z;
        float reach = far + radius;
        if (dx * dx + dy * dy + dz * dz > reach * reach) return false;
        return SphereVisible(centre, radius, planes);
    }

    /// <summary>Take in the tile parts the area has seated since the last walk. Extends only: a tile is
    /// created at area load and the area edge drops the list, so there is nothing here that can go stale
    /// inside an area.</summary>
    private static void EnrolDb(DbId scene)
    {
        int high = SceneDb.Parts.High;
        if (high <= _dbWalked) return;
        for (int p = _dbWalked; p < high; p++)
        {
            if (!SceneDb.Parts.LiveAt(p) || !SceneDb.PartEnabled[p]) continue;
            MeshDefHas(p, scene, out bool take);
            if (!take) continue;
            if (_dbListN >= MaxDbParts) { _dbOverflow++; break; }
            _dbList[_dbListN] = p; _dbFirst[_dbListN] = -1; _dbCount[_dbListN] = 0;
            _dbListN++; _dbEnrolled++;
        }
        _dbWalked = high;
    }

    /// <summary>Is this part a candidate -- tile geometry of this scene with faces to test? The grass test
    /// itself is per FACE and costs a walk, so it waits for the bake.</summary>
    private static void MeshDefHas(int p, DbId scene, out bool take)
    {
        take = false;
        CX.Alloy.Mdl.MeshDef? m = SceneDb.PartMeshDef[p];
        if (m == null || m.Faces.Length == 0 || m.Positions.Length == 0) return;
        // The WALKMESH, and only the walkmesh. Measured against the engine's own binning in the same area: the faces the
        // engine bins are the tile's walkable surface -- sub-metre triangles carrying the surface material
        // ids the grass table is indexed by (`[face] area=0.9 blades=1` against the drawn ground's
        // `area=50 blades=99`). The drawn ground is two coarse quads per tile, so scattering on it put
        // blades anywhere inside the quad, buildings included, which is what grew grass up walls and roofs.
        if (SceneDb.PartKindOf[p] != PartKind.Aabb) return;
        DbId obj = SceneDb.PartObj[p];
        if (!SceneDb.Objects.Valid(obj)) return;
        int o = obj.Index;
        // Grass is tileset geometry: the engine's own source is the faces the tile parts hand InitBins.
        if ((SceneDb.ObjFlags[o] & ObjFlag.Tile) == 0) return;
        if (SceneDb.ObjScene[o].Value != scene.Value) return;
        take = true;
    }

    /// <summary>Bake one part's grass faces -- one patch per grass material on it -- into the resident face
    /// buffer. A part with no grass bakes nothing and is never looked at again (its run is empty, not
    /// unbaked), which is what keeps the budget moving down the list.</summary>
    private static void BakeDbPart(int k, int part)
    {
        CX.Alloy.Mdl.MeshDef? m = SceneDb.PartMeshDef[part];

        _dbFirst[k] = _patchCount; _dbCount[k] = 0;
        if (m == null) return;
        int faces = m.Faces.Length;
        // The materials this part actually carries, found once: the per-material pass below is over a
        // handful of ids, not over the grass-info table.
        int* mats = stackalloc int[MaxPartMats];
        int matN = 0;
        _dbFacesTested += faces;
        for (int f = 0; f < faces; f++)
        {
            int s = m.Faces[f].Surface;
            if (s > _dbMaxSurface) _dbMaxSurface = s;
            if (!MaterialInUse(s)) continue;
            int at = 0;
            while (at < matN && mats[at] != s) at++;
            if (at == matN && matN < MaxPartMats) mats[matN++] = s;
        }
        if (matN == 0) return;
        if (_dbVerts.Length < faces) { _dbVerts = new GrassTriRec[faces]; _dbBlades = new int[faces]; }
        Matrix4x4 world = SceneDb.PartWorld[part];
        int firstPatch = _patchCount;
        for (int i = 0; i < matN; i++)
        {
            int mat = mats[i];
            float density = GrassInfo(mat).Density;
            int n = 0;
            fixed (GrassTriRec* verts = _dbVerts)
            fixed (int* blades = _dbBlades)
            {
                for (int f = 0; f < faces; f++)
                {
                    ref CX.Alloy.Mdl.FaceDef fd = ref m.Faces[f];
                    if (fd.Surface != mat) continue;
                    if ((uint)fd.V0 >= (uint)m.Positions.Length || (uint)fd.V1 >= (uint)m.Positions.Length
                        || (uint)fd.V2 >= (uint)m.Positions.Length) continue;
                    Vector3 a = Vector3.Transform(m.Positions[fd.V0], world);
                    Vector3 b = Vector3.Transform(m.Positions[fd.V1], world);
                    Vector3 c = Vector3.Transform(m.Positions[fd.V2], world);
                    // CAurTriangleList::AddTriangle @0x5b5650: half the cross product's magnitude -- the
                    // triangle's AREA -- times the authored density, truncated. A face that rounds to no
                    // blade is not filed at all, which is the engine's own zero-count early out.
                    int count = (int)(Vector3.Cross(b - a, c - a).Length() * 0.5f * density);
                    if (count <= 0) continue;
                    verts[n] = new GrassTriRec { A = a, B = b, C = c };
                    blades[n] = count;
                    n++;
                }
                if (n == 0) continue;
                if (BakeFaces(verts, blades, n, mat) < 0) break;
            }
            _dbGrassFaces += n;
        }
        _dbCount[k] = _patchCount - firstPatch;
        if (_dbCount[k] > 0) _dbBakedParts++;
        // What grew grass, named: the first few parts with their model and node, so "grass on a wall" is a
        // line in the log rather than a look at a screenshot.
        if (_dbCount[k] > 0 && _dbNamed < NamedParts)
        {
            _dbNamed++;
            CX.Alloy.Mdl.ModelDef? md = SceneDb.PartModel[part];
            CX.Alloy.Mdl.NodeDef? nd = PartTree.NodeOf(part);
            _log?.Info($"GrassField: [db part] model='{md?.Name}' node='{nd?.Name}' faces={faces} "
                       + $"mats={matN} first={mats[0]} patches={_dbCount[k]}");
        }
    }

    /// <summary><c>AurGrassMaterialInUse</c> @0x5b4730, read off the same live record the pass takes the
    /// texture and height from: the id is in range and the record's <c>inUse</c> byte is set.
    /// <c>AurGrassDisableMaterial</c> @0x5b4780 clears that byte without touching the density, so the byte is
    /// the test and the density is not.</summary>
    private static bool MaterialInUse(int mat)
    {
        if (mat < 0 || mat >= MaxGrassMaterials) return false;
        CAurGrassInfo info = GrassInfo(mat);
        return !info.IsNull && info.InUse != 0;
    }

    /// <summary>The DB grass clause on the period line.</summary>
    private static string DbNote()
    {
        if (_dbListN == 0) return "";
        int live = _grassInfo.IsNull ? -1 : _grassInfo.Count, inUse = 0;
        for (int i = 0; i < live && i < MaxGrassMaterials; i++) if (MaterialInUse(i)) inUse++;
        return $", db {_dbBakedParts}/{_dbListN} part(s) with grass, {_dbGrassFaces} face(s) of "
             + $"{_dbFacesTested} tested (highest material id {_dbMaxSurface}), "
             + $"{inUse} of {live} grass-info record(s) in use"
             + (_dbOverflow > 0 ? $", {_dbOverflow} PAST THE PART CAP" : "");
    }

    /// <summary>The area edge: the list names parts of a table the feed is about to refill.</summary>
    private static void ForgetDb()
    {
        _dbListN = 0; _dbWalked = 0; _dbScene = 0;
        _dbBakedParts = 0; _dbGrassFaces = 0; _dbEnrolled = 0; _dbOverflow = 0;
        _dbFacesTested = 0; _dbMaxSurface = -1; _dbNamed = 0;
    }
}
