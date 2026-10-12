using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using CX.Alloy.Mdl;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy.Db;

/// <summary>
/// The off-device suite: the part-tree builder and the mesh arena driven over hand-built
/// <see cref="ModelDef"/> fixtures, with no engine and no GL. Every expected value below comes from the
/// decompilation named on its check, not from running the code.
/// </summary>
internal static class DbSelfCheck
{
    /// <summary>The recipe's blend constants are table INDICES and <see cref="Issue.BlendGl"/> is the one
    /// translation, so a wrong index is three valid indices naming the wrong factors: nothing throws and only
    /// the picture is wrong. This pins both ends of that seam to the GL enums.</summary>
    private static bool BlendIndices()
        => Issue.BlendGl(DrawRecipe.BlendSrcAlpha) == Gl.SRC_ALPHA
        && Issue.BlendGl(DrawRecipe.BlendInvSrcAlpha) == Gl.ONE_MINUS_SRC_ALPHA
        && Issue.BlendGl(DrawRecipe.BlendOne) == 1u
        && Issue.BlendGl(AlloyCensus.BlendOne) == 1u;

    internal static bool Run(ExtenderLog log)
    {
        var check = new Checker(log, "PartTree.SelfCheck");
        Func<string, ModelDef?> savedResolve = PartTree.ResolveModel;
        Action<DbId, int, string>? savedRef = PartTree.OnReference;
        Func<string, byte[]?>? savedBytes = MtrDef.Bytes;
        ExtenderLog? savedArenaLog = MeshArena.Log, savedTreeLog = PartTree.Log;
        MeshArena.Log = null;
        PartTree.Log = null;
        try
        {
            check.Expect(InstanceTree(), "CreateInstanceTreeR: one part per node, pre-order, a parent before every child, the rest pose and kind off the node");
            check.Expect(References(), "ExpandReferenceParts: the referenced model instanced as the reference node's child subtree; the object path reports it instead");
            check.Expect(Collapsed(), "CollapsePartTree: every pose composed into the part's own, the links cut, and the one named subtree left under the root");
            check.Expect(Twins(), "#397: of two coincident sibling meshes only the later draws (vanilla's LEQUAL tie), while the door's two offset faces both do");
            check.Expect(Merge(), "CombineParts: compatible meshes merged into one new MeshDef, each absorbed part marked merged away and its arena range returned");
            check.Expect(MergeKeys(), "CreatePartBuckets: a different texture, render hint, tile fade, colour, shadow flag or vertex format is a different bucket");
            check.Expect(BBox(), "ComputePartBBox: the geometry through the part's own pose with all three corners rounded to 0.001, zeroed only for a part with no faces -- never for one that does not render");
            check.Expect(Ranges(), "part ranges: a freed chunk is reused by the next tree of its size class, and a tree past the table is a counted refusal");
            check.Expect(ArenaDedup(), "mesh arena: two objects over one mesh are ONE range with two references, and the last release frees it at the frame edge");
            check.Expect(CompactRestage(), "a compaction tells the draw side exactly which ranges moved, and a record staged before it no longer holds the old base vertex or index offset");
            check.Expect(ArenaCompact(), "mesh arena: a compaction slides the live ranges down and rebases their index runs");
            check.Expect(ArenaStreams(), "mesh arena: the packed tangent and paint words round-trip, sit last in both formats, and land at the locations the lit vertex stage declares");
            check.Expect(ArenaStreamIdentity(), "mesh arena: two meshes that differ only in their tangent frame or their paint are different content, so they get ranges of their own");
            check.Expect(Claimed(), "DbTable.Claim/Unclaim: every part of a live range is a LIVE table slot with a generation, and a freed range's handles are dead");
            check.Expect(OneAllocator(), "the part index space has ONE allocator: single-slot Alloc and range AllocRange never hand out the same index, in boot order (fixtures first, trees after)");
            check.Expect(Recycled(), "a recycled chunk inherits nothing: the feed's columns are re-seeded and a stale MeshDef shortcut cannot name a foreign range");
            check.Expect(Ceilings(), "the ceilings: a tree past the biggest size class, the 64k bucket, and the render flag as a bucket term");
            check.Expect(Census(), "CX-113's census: one placement per mesh-drawing part, dropped with the part range, and the merge moves the head's placement onto the merged mesh");
            check.Expect(BlendIndices(), "the recipe's blend pair is an INDEX into the engine's blendTranslationTable, not a GL factor");
            check.Expect(Mtr(), "the .mtr parse: the eleven texture slots, the render hints ExtractRenderHint maps, the parameters and both flags");
            check.Expect(TexReplace(), "Gob::ReplaceTexture: a part already showing the texture asked for is refreshed to the model's own first, so the SAME pair issued twice swaps again");
            check.Expect(PltMtrDiffuse(), "a replace matches the slot's effective name: the .mtr's texture when the file supplies one, else the mesh bitmap or the graft model -- so a creature's PLT replace leaves an authored diffuse alone");
            check.Expect(LateCoverage(), "a .mtr that lands after a recipe was interned re-seeds the coverage mode, which no texture serial would move");
            check.Expect(LateMaterialColumns(), "a .mtr that lands after a PART was built folds into its material columns, and a PLT replace on it then matches the file's diffuse rather than the mesh bitmap");
        }
        finally
        {
            PartTree.ResolveModel = savedResolve;
            PartTree.OnReference = savedRef;
            MtrDef.Bytes = savedBytes;
            MeshArena.Log = savedArenaLog;
            PartTree.Log = savedTreeLog;
            Drain();
            MtrDef.Forget();
            PartTree.ResetForCheck();
            // ResetForCheck refuses while anything is held; Drain above is what makes it safe, and a refusal
            // is a loud line rather than a wiped live arena (finding 20)
            MeshArena.ResetForCheck();
        }
        log.Info(check.Ok ? "PartTree.SelfCheck: passed" : "PartTree.SelfCheck: FAILED");
        return check.Ok;
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>A unit quad at the origin in the XY plane, two triangles, with the given texture.</summary>
    private static MeshDef Quad(string tex, float z = 0f)
    {
        var m = new MeshDef
        {
            Positions = new[] { new Vector3(0, 0, z), new Vector3(1, 0, z), new Vector3(1, 1, z), new Vector3(0, 1, z) },
            Normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
            Indices = new uint[] { 0, 1, 2, 0, 2, 3 },
        };
        m.Uv[0] = new[] { Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY };
        m.Textures[0] = tex;
        return m;
    }

    /// <summary>A model from a parent list and per-node payloads; children are derived, as the parser does.</summary>
    private static ModelDef Model(string name, int[] parents, NodeKind[] kinds, MeshDef?[] meshes,
                                  string[]? names = null, Vector3[]? pos = null, AnimDef[]? anims = null)
    {
        var m = new ModelDef { Name = name, Nodes = new NodeDef[parents.Length] };
        if (anims != null) m.Anims = anims;
        var kids = new List<int>[parents.Length];
        for (int i = 0; i < parents.Length; i++) kids[i] = new List<int>();
        for (int i = 0; i < parents.Length; i++) if (parents[i] >= 0) kids[parents[i]].Add(i);
        for (int i = 0; i < parents.Length; i++)
        {
            m.Nodes[i] = new NodeDef
            {
                Index = i,
                Parent = parents[i],
                Children = kids[i].ToArray(),
                Kind = kinds[i],
                Mesh = meshes[i],
                Name = names != null ? names[i] : $"{name}_{i}",
                Position = pos != null ? pos[i] : Vector3.Zero,
            };
            m.NodeByName[m.Nodes[i].Name] = i;
        }
        return m;
    }

    /// <summary>Objects the suite allocated, so the <c>finally</c> can give every part range and object slot
    /// back. Without it the suite's leftovers were freed by the LIVE session later and the builder's part
    /// tally went negative on the period line.</summary>
    private static readonly List<DbId> _made = new();

    private static DbId NewObject()
    {
        DbId o = SceneDb.Objects.Alloc();
        SceneDb.ObjKindOf[o.Index] = ObjKind.Model;
        SceneDb.ObjPartFirst[o.Index] = 0;
        SceneDb.ObjPartCount[o.Index] = 0;
        _made.Add(o);
        return o;
    }

    /// <summary>Give back every object and part range the suite took.</summary>
    private static void Drain()
    {
        for (int i = 0; i < _made.Count; i++) { PartTree.Free(_made[i]); SceneDb.Objects.Free(_made[i]); }
        _made.Clear();
    }

    private static void Reset()
    {
        Drain();
        PartTree.ResetForCheck();
        MeshArena.FrameEdgeForCheck();
        MeshArena.ResetForCheck();
        PartTree.ResolveModel = _ => null;
        PartTree.OnReference = null;
    }

    // ---------------------------------------------------------------- checks

    /// <summary><c>CreateInstanceTreeR</c> @0x43c6b0: a part per node, children in order after their parent.</summary>
    private static bool Twins()
    {
        var shield = Model("cx_twin", new[] { -1, 0, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh, NodeKind.TriMesh },
                           new MeshDef?[] { null, Quad("stag"), Quad("eagle") });
        var door = Model("cx_door", new[] { -1, 0, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh, NodeKind.TriMesh },
                         new MeshDef?[] { null, Quad("face", -0.05f), Quad("face", 0.05f) });
        // a compiled binary's rest tracks: one key each, as stock ashlw_072 carries on both twins
        foreach (int i in new[] { 1, 2 })
            shield.Nodes[i].Controllers = new[] { new ControllerTrack { Type = 8, Columns = 3, Times = new[] { 0f }, Values = new float[3] },
                                                  new ControllerTrack { Type = 20, Columns = 4, Times = new[] { 0f }, Values = new float[] { 0, 0, 0, 1 } } };
        return DrawRecipe.TwinShadowed(shield, 1) && !DrawRecipe.TwinShadowed(shield, 2)
            && !DrawRecipe.TwinShadowed(door, 1) && !DrawRecipe.TwinShadowed(door, 2);
    }

    private static bool InstanceTree()
    {
        Reset();
        // 0 root -> 1 mesh, 2 dummy -> 3 light
        var model = Model("tree", new[] { -1, 0, 0, 2 },
                          new[] { NodeKind.Dummy, NodeKind.TriMesh, NodeKind.Dummy, NodeKind.Light },
                          new MeshDef?[] { null, Quad("t"), null, null },
                          pos: new[] { Vector3.Zero, new Vector3(1, 0, 0), new Vector3(0, 2, 0), new Vector3(0, 0, 3) });
        DbId o = NewObject();
        if (!PartTree.Build(o, model)) return false;
        int f = SceneDb.ObjPartFirst[o.Index];
        if (SceneDb.ObjPartCount[o.Index] != 4) return false;
        // pre-order: root, 1, 2, 3 -- and node 3's parent is the part that instanced node 2
        if (SceneDb.PartNode[f] != 0 || SceneDb.PartNode[f + 1] != 1 || SceneDb.PartNode[f + 2] != 2 || SceneDb.PartNode[f + 3] != 3) return false;
        if (SceneDb.PartParent[f] != -1 || SceneDb.PartParent[f + 1] != f || SceneDb.PartParent[f + 2] != f || SceneDb.PartParent[f + 3] != f + 2) return false;
        for (int k = 0; k < 4; k++) if (SceneDb.PartParent[f + k] >= f + k) return false;   // parent always precedes
        if (SceneDb.PartKindOf[f + 1] != PartKind.Mesh || SceneDb.PartKindOf[f + 3] != PartKind.Light) return false;
        if (SceneDb.PartPos[f + 2] != new Vector3(0, 2, 0)) return false;
        if (SceneDb.PartMesh[f + 1] < 0 || SceneDb.PartMesh[f] >= 0) return false;
        if (SceneDb.PartObj[f + 3] != o) return false;
        return true;
    }

    /// <summary><c>Gob::ReplaceTexture</c> @0x43eb30 in two steps: a part already showing the texture asked for
    /// goes back to <c>MdlNodeTriMesh::GetMaterialName</c>'s own name first, and only then is the <c>from</c>
    /// matched -- which is what lets <c>CPanelLoadSave::HandleSelectSaveGame</c> @0x7fd500 issue
    /// <c>ReplaceTexture("gui_replace02", "Screen")</c> once per save and mean something each time.</summary>
    private static bool TexReplace()
    {
        Reset();
        var model = Model("texswap", new[] { -1, 0 },
                          new[] { NodeKind.Dummy, NodeKind.TriMesh },
                          new MeshDef?[] { null, Quad("gui_replace02") });
        DbId o = NewObject();
        if (!PartTree.Build(o, model)) return false;
        int p = SceneDb.ObjPartFirst[o.Index] + 1;
        try
        {
            if (Seam.ObjectTex.Replace(o, "gui_replace02", "Screen") != 1 || Shows(p) != "Screen") return false;
            // The regression this pins: the part now shows "Screen", so matching "gui_replace02" against its
            // CURRENT name found nothing and the next save's preview never arrived.
            if (Seam.ObjectTex.Replace(o, "gui_replace02", "Screen") != 1 || Shows(p) != "Screen") return false;
            // The match is on the MODEL's name, never the override's (AurTextureReplaceSingleReference).
            if (Seam.ObjectTex.Replace(o, "Screen", "Other") != 0 || Shows(p) != "Screen") return false;
            if (Seam.ObjectTex.Replace(o, "gui_replace02", "Other") != 1 || Shows(p) != "Other") return false;
            // CGuiInvbox::SetObjectHilite's un-highlight: (model name, model name) over a highlighted part.
            if (Seam.ObjectTex.Replace(o, "gui_replace02", "gui_replace02") != 1
                || Shows(p) is not ("" or "gui_replace02")) return false;
            if (Seam.ObjectTex.Restore(o) != 1 || Shows(p) != "") return false;
            return true;
        }
        finally { Seam.ObjectTex.Restore(o); }
    }

    /// <summary>A replace is matched against the slot's EFFECTIVE texture name. Oracle: hand-written from
    /// <c>Material::ReplaceTexturePLT</c> @0x4c7a10 and <c>Material::BindAllStandardTextures</c> @0x4c6e30 --
    /// the shared material's texture is bound over the material's own, so where the <c>.mtr</c> names slot 0
    /// that is what draws and a replace aimed at the mesh bitmap cannot be seen.</summary>
    private static bool PltMtrDiffuse()
    {
        Reset();
        MtrDef.Forget();
        MtrDef.Bytes = n => n switch
        {
            "cx_plt_mat" => Encoding.ASCII.GetBytes("texture0 cx_chest003\n"),
            "cx_norm_mat" => Encoding.ASCII.GetBytes("texture1 cx_chest001_n\n"),
            "cx_arm_mat" => Encoding.ASCII.GetBytes("texture0 cx_armour_d\nrenderhint NormalAndSpecMapped\n"),
            _ => null,
        };
        Span<int> rows = stackalloc int[SceneDb.PltLayers];
        for (int l = 0; l < SceneDb.PltLayers; l++) rows[l] = l + 1;

        // the .mtr names the PLT itself: that name is the one the replace has to carry, not the mesh bitmap
        int p = Dressed("cx_plt_mat", out DbId plt);
        if (p < 0) return false;
        if (Seam.ObjectTex.Replace(plt, "cx_chest001", "cx_chest042", rows: rows) != 0) return false;
        if (Shows(p) != "" || SceneDb.PartPltOwn[p]) return false;
        if (Seam.ObjectTex.Replace(plt, "cx_chest003", "cx_chest042", rows: rows) != 1) return false;
        if (Shows(p) != "cx_chest042" || !SceneDb.PartPltOwn[p]) return false;

        // a .mtr that supplies no diffuse leaves the mesh's own bitmap -- and the graft model's name -- standing
        p = Dressed("cx_norm_mat", out DbId norm);
        if (p < 0) return false;
        if (Seam.ObjectTex.Replace(norm, "cx_chest001", "cx_chest042", rows: rows) != 1) return false;
        if (Shows(p) != "cx_chest042") return false;
        if (Seam.ObjectTex.Restore(norm) != 1) return false;
        if (Seam.ObjectTex.Replace(norm, "cx_chest003", "cx_chest042", rows: rows) != 1) return false;

        // no .mtr at all: the ordinary replace, on the bitmap
        p = Dressed(null, out DbId bare);
        if (p < 0) return false;
        if (Seam.ObjectTex.Replace(bare, "cx_chest001", "cx_chest042") != 1 || Shows(p) != "cx_chest042") return false;

        // the .mtr names its own non-PLT diffuse: the creature's replace must leave it alone
        long stood = Seam.ObjectTex.MtrDiffuseKept;
        p = Dressed("cx_arm_mat", out DbId arm);
        if (p < 0) return false;
        if (Seam.ObjectTex.Replace(arm, "cx_chest003", "cx_chest042", rows: rows) != 0) return false;
        if (Seam.ObjectTex.Replace(arm, "cx_chest001", "cx_chest042", rows: rows) != 0) return false;
        if (Shows(p) != "" || SceneDb.PartPltOwn[p]) return false;
        if (SceneDb.PartTexName(p, 0) != "cx_armour_d") return false;
        return Seam.ObjectTex.MtrDiffuseKept == stood + 2;
    }

    /// <summary>One armour part as <c>CreateBodyParts</c> @0x734440 grafts it: the model named for the part
    /// (<c>cx_chest003</c>), a mesh whose bitmap is another (<c>cx_chest001</c>), and the material file
    /// <paramref name="material"/> if any. Returns the mesh part's index, or -1.</summary>
    private static int Dressed(string? material, out DbId obj)
    {
        MeshDef mesh = Quad("cx_chest001");
        if (material != null) mesh.MaterialName = material;
        var model = Model("cx_chest003", new[] { -1, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh },
                          new MeshDef?[] { null, mesh });
        obj = NewObject();
        if (!PartTree.Build(obj, model)) return -1;
        int p = SceneDb.ObjPartFirst[obj.Index] + 1;
        return (SceneDb.PartMtr[p] == null) == (material == null) ? p : -1;
    }

    /// <summary>The resref part <paramref name="p"/>'s slot 0 currently shows, empty for the model's own.</summary>
    private static string Shows(int p)
    {
        int tex = SceneDb.PartTexId[p * 4];
        return tex == 0 ? "" : TexRegistry.ResrefOf(new TexId((uint)tex));
    }

    /// <summary><c>ExpandReferenceParts</c> @0x462f70 against <c>CreateReferenceObjects</c> @0x4421e0.</summary>
    private static bool References()
    {
        Reset();
        var sub = Model("sub", new[] { -1, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh },
                        new MeshDef?[] { null, Quad("s") });
        PartTree.ResolveModel = n => n == "sub" ? sub : null;
        var host = Model("host", new[] { -1, 0 }, new[] { NodeKind.Dummy, NodeKind.Reference },
                         new MeshDef?[] { null, null });
        host.Nodes[1].Reference = new ReferenceDef { Model = "sub" };

        // the tile path expands it in place: 2 host nodes + 2 sub nodes, the sub root under the reference part
        DbId t = NewObject();
        if (!PartTree.Build(t, host, new PartTree.TreeOptions { ExpandReferences = true })) return false;
        int f = SceneDb.ObjPartFirst[t.Index];
        if (SceneDb.ObjPartCount[t.Index] != 4) return false;
        if (SceneDb.PartParent[f + 2] != f + 1) return false;
        if (SceneDb.PartModel[f + 2] != sub || SceneDb.PartModel[f + 1] != host) return false;
        if (SceneDb.PartKindOf[f + 3] != PartKind.Mesh) return false;

        // the object path leaves the tree alone and reports the reference for the seam to spawn
        string? seen = null;
        int at = -1;
        PartTree.OnReference = (_, p, name) => { seen = name; at = p; };
        DbId g = NewObject();
        if (!PartTree.Build(g, host)) return false;
        int gf = SceneDb.ObjPartFirst[g.Index];
        return SceneDb.ObjPartCount[g.Index] == 2 && seen == "sub" && at == gf + 1
               && SceneDb.PartKindOf[gf + 1] == PartKind.Reference;
    }

    /// <summary><c>CollapsePartTree</c> @0x462ff0.</summary>
    private static bool Collapsed()
    {
        Reset();
        // 0 root -> 1 "ta" (kept) -> 2 ; 0 -> 3 mesh
        var model = Model("t", new[] { -1, 0, 1, 0 },
                          new[] { NodeKind.Dummy, NodeKind.Dummy, NodeKind.TriMesh, NodeKind.TriMesh },
                          new MeshDef?[] { null, null, Quad("a"), Quad("b") },
                          names: new[] { "t", "ta", "ta_kid", "slab" },
                          pos: new[] { new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1), new Vector3(0, 0, 2) });
        DbId o = NewObject();
        var opt = new PartTree.TreeOptions
        {
            Collapse = true,
            Offset = new Vector3(10, 0, 0),
            Rotation = Quaternion.Identity,
            KeepSubtree = "ta",
        };
        if (!PartTree.Build(o, model, opt)) return false;
        int f = SceneDb.ObjPartFirst[o.Index];
        // the root takes the placement; the flattened mesh part carries root+own; the kept subtree stays local
        if (SceneDb.PartPos[f] != new Vector3(11, 0, 0)) return false;
        if (SceneDb.PartPos[f + 3] != new Vector3(11, 0, 2)) return false;
        if (SceneDb.PartParent[f + 3] != -1) return false;
        if (SceneDb.PartPos[f + 1] != new Vector3(0, 1, 0) || SceneDb.PartParent[f + 1] != f) return false;
        if (SceneDb.PartPos[f + 2] != new Vector3(0, 0, 1) || SceneDb.PartParent[f + 2] != f + 1) return false;
        return true;
    }

    /// <summary><c>CombineParts</c> @0x473d30 and its <c>part+0x96 = 0</c> marker.</summary>
    private static bool Merge()
    {
        Reset();
        var model = Model("tile", new[] { -1, 0, 0, 0 },
                          new[] { NodeKind.Dummy, NodeKind.TriMesh, NodeKind.TriMesh, NodeKind.TriMesh },
                          new MeshDef?[] { null, Quad("stone"), Quad("stone", 1f), Quad("wood") },
                          pos: new[] { Vector3.Zero, Vector3.Zero, new Vector3(0, 0, 5), Vector3.Zero });
        DbId o = NewObject();
        if (!PartTree.Build(o, model, new PartTree.TreeOptions { Collapse = true, CombineOwn = true, Rotation = Quaternion.Identity })) return false;
        int f = SceneDb.ObjPartFirst[o.Index];
        int head = f + 1, mem = f + 2, other = f + 3;
        if (!SceneDb.PartMerged[mem] || SceneDb.PartMerged[head] || SceneDb.PartMerged[other]) return false;
        if (SceneDb.PartKindOf[mem] != PartKind.Merged || SceneDb.PartMesh[mem] != -1) return false;
        MeshDef? h = SceneDb.PartMeshDef[head];
        if (h == null || h.Positions.Length != 8 || h.Indices.Length != 12) return false;
        // the member's vertices arrive in the head's space: its quad sat at z=1 under a part at z=5
        if (h.Positions[4] != new Vector3(0, 0, 6)) return false;
        // ... and its indices are rebased past the head's vertices
        if (h.Indices[6] != 4 || h.Indices[7] != 5) return false;
        if (SceneDb.PartMesh[head] < 0 || SceneDb.PartMesh[other] < 0) return false;
        // the member's own range went back at the merge; content identity ignores the texture NAME, so the
        // wood quad still holds the range the head gave up -- two meshes left, not three
        MeshArena.FrameEdgeForCheck();
        return MeshArena.MeshCount == 2 && MeshArena.RefsOf(SceneDb.PartMesh[head]) == 1;
    }

    /// <summary><c>CreatePartBuckets</c> @0x473570: what makes two meshes one bucket.</summary>
    private static bool MergeKeys()
    {
        // a different texture does not merge
        Reset();
        var a = Model("m", new[] { -1, 0, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh, NodeKind.TriMesh },
                      new MeshDef?[] { null, Quad("x"), Quad("y") });
        DbId o = NewObject();
        if (!PartTree.Build(o, a, new PartTree.TreeOptions { CombineOwn = true, Rotation = Quaternion.Identity })) return false;
        int f = SceneDb.ObjPartFirst[o.Index];
        if (SceneDb.PartMerged[f + 1] || SceneDb.PartMerged[f + 2]) return false;

        // a different vertex format does not merge either: same texture, one quad without normals
        Reset();
        MeshDef flat = Quad("x");
        flat.Normals = Array.Empty<Vector3>();
        var b = Model("m", new[] { -1, 0, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh, NodeKind.TriMesh },
                      new MeshDef?[] { null, Quad("x"), flat });
        DbId p = NewObject();
        if (!PartTree.Build(p, b, new PartTree.TreeOptions { CombineOwn = true, Rotation = Quaternion.Identity })) return false;
        int pf = SceneDb.ObjPartFirst[p.Index];
        if (SceneDb.PartMerged[pf + 1] || SceneDb.PartMerged[pf + 2]) return false;

        // a model that carries ANIMATIONS is not eligible at all (Model+0xa0 >= 1)
        Reset();
        var c = Model("m", new[] { -1, 0, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh, NodeKind.TriMesh },
                      new MeshDef?[] { null, Quad("x"), Quad("x") },
                      anims: new[] { new AnimDef { Name = "wave" } });
        DbId q = NewObject();
        if (!PartTree.Build(q, c, new PartTree.TreeOptions { CombineOwn = true, Rotation = Quaternion.Identity })) return false;
        int qf = SceneDb.ObjPartFirst[q.Index];
        if (SceneDb.PartMerged[qf + 1] || SceneDb.PartMerged[qf + 2]) return false;

        // the same texture, format and colours in an anim-less model DOES merge
        Reset();
        var d = Model("m", new[] { -1, 0, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh, NodeKind.TriMesh },
                      new MeshDef?[] { null, Quad("x"), Quad("x", 2f) });
        DbId r = NewObject();
        if (!PartTree.Build(r, d, new PartTree.TreeOptions { CombineOwn = true, Rotation = Quaternion.Identity })) return false;
        int rf = SceneDb.ObjPartFirst[r.Index];
        return SceneDb.PartMerged[rf + 2] && !SceneDb.PartMerged[rf + 1];
    }

    /// <summary><c>ComputePartBBox</c> @0x461370 and <c>Round</c> @0x460dc0.</summary>
    private static bool BBox()
    {
        Reset();
        MeshDef quad = Quad("x");
        MeshDef hidden = Quad("y");
        hidden.Render = false;
        var model = Model("b", new[] { -1, 0, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh, NodeKind.TriMesh },
                          new MeshDef?[] { null, quad, hidden },
                          pos: new[] { Vector3.Zero, new Vector3(0.00049f, 2f, 0f), Vector3.Zero });
        DbId o = NewObject();
        if (!PartTree.Build(o, model)) return false;
        int f = SceneDb.ObjPartFirst[o.Index];
        Vector3 lo = SceneDb.PartBMin[f + 1], hi = SceneDb.PartBMax[f + 1];
        // 0.00049 / 0.001 + 0.5 = 0.99 -> floor 0 -> 0.0, so the x corner rounds down to the quad's own 0 and 1
        if (MathF.Abs(lo.X) > 1e-6f || MathF.Abs(lo.Y - 2f) > 1e-6f) return false;
        if (MathF.Abs(hi.X - 1f) > 1e-6f || MathF.Abs(hi.Y - 3f) > 1e-6f) return false;
        if (SceneDb.PartRadius[f + 1] <= 0f) return false;
        // ComputePartBBox has NO render filter: a non-rendering part still gets its box, because the hit
        // test reads it (finding 14). Only no faces / no vertices zeroes it.
        if (SceneDb.PartBMax[f + 2] != new Vector3(1, 1, 0)) return false;
        if (SceneDb.PartBMin[f] != Vector3.Zero || SceneDb.PartBMax[f] != Vector3.Zero) return false;   // a dummy has no geometry
        // and the z corner is rounded too (Round @0x460dc0 loads +0x0, +0x4 AND +0x8; disassembled)
        MeshDef zq = Quad("z", 1.00049f);
        var zm = Model("z", new[] { -1 }, new[] { NodeKind.TriMesh }, new MeshDef?[] { zq });
        DbId zo = NewObject();
        if (!PartTree.Build(zo, zm)) return false;
        int zf = SceneDb.ObjPartFirst[zo.Index];
        return MathF.Abs(SceneDb.PartBMax[zf].Z - 1.0f) < 1e-6f;
    }

    /// <summary>The range allocator: reuse by size class, and the ceiling as a refusal.</summary>
    private static bool Ranges()
    {
        Reset();
        var model = Model("r", new[] { -1, 0, 0 }, new[] { NodeKind.Dummy, NodeKind.Dummy, NodeKind.Dummy },
                          new MeshDef?[] { null, null, null });
        DbId a = NewObject();
        if (!PartTree.Build(a, model)) return false;
        int fa = SceneDb.ObjPartFirst[a.Index];
        DbId b = NewObject();
        if (!PartTree.Build(b, model)) return false;
        int fb = SceneDb.ObjPartFirst[b.Index];
        if (fb == fa || fb < fa + 3) return false;   // 3 parts round up to a 4-slot chunk
        PartTree.Free(a);
        if (SceneDb.ObjPartCount[a.Index] != 0 || SceneDb.PartModel[fa] != null) return false;
        DbId c = NewObject();
        if (!PartTree.Build(c, model)) return false;
        if (SceneDb.ObjPartFirst[c.Index] != fa) return false;   // the freed chunk came back

        // a tree past the biggest size class is refused, not clamped
        var huge = new ModelDef { Name = "huge", Nodes = new NodeDef[1] };
        huge.Nodes[0] = new NodeDef { Index = 0, Parent = -1 };
        DbId d = NewObject();
        if (!PartTree.Build(d, huge)) return false;
        return SceneDb.ObjPartCount[d.Index] == 1;
    }

    /// <summary>The arena's content dedup and its refcounted free.</summary>
    private static bool ArenaDedup()
    {
        Reset();
        MeshDef shared = Quad("x");
        var model = Model("s", new[] { -1, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh },
                          new MeshDef?[] { null, shared });
        DbId a = NewObject(), b = NewObject();
        if (!PartTree.Build(a, model) || !PartTree.Build(b, model)) return false;
        int ra = SceneDb.PartMesh[SceneDb.ObjPartFirst[a.Index] + 1];
        int rb = SceneDb.PartMesh[SceneDb.ObjPartFirst[b.Index] + 1];
        if (ra < 0 || ra != rb) return false;
        if (MeshArena.RefsOf(ra) != 2 || MeshArena.MeshCount != 1) return false;

        // a DIFFERENT MeshDef object with the same bytes is the same range too (content identity)
        var twin = Model("s2", new[] { -1, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh },
                         new MeshDef?[] { null, Quad("x") });
        DbId c = NewObject();
        if (!PartTree.Build(c, twin)) return false;
        if (SceneDb.PartMesh[SceneDb.ObjPartFirst[c.Index] + 1] != ra) return false;
        if (MeshArena.RefsOf(ra) != 3 || MeshArena.MeshCount != 1) return false;

        PartTree.Free(a);
        MeshArena.FrameEdgeForCheck();
        if (MeshArena.RefsOf(ra) != 2) return false;
        PartTree.Free(b);
        PartTree.Free(c);
        if (MeshArena.RefsOf(ra) != 0) return false;   // released, and dead to every reader at once
        MeshArena.FrameEdgeForCheck();
        return MeshArena.MeshCount == 0 && !MeshArena.Runs(ra, 0, out _, out _, out _, out _);
    }

    /// <summary>The arena's two packed streams. Three independent oracles: <c>GL_INT_2_10_10_10_REV</c> words
    /// written out BY HAND from the field order in the GL spec, hand-written byte offsets and strides, and
    /// the embedded lit vertex stage's own text for the attribute locations the VAO binds.</summary>
    private static bool ArenaStreams()
    {
        // the packing. 0x400001FF is (+x, handedness +1): x lane 511, y and z 0, w lane 1 in bits 30..31.
        if (MeshArena.PackTangent(new Vector4(1f, 0f, 0f, 1f)) != 0x400001FFu) return false;
        // ...and 0xE0100000 is (-z, handedness -1): z lane -511 as 0x201 at bit 20, w lane -1 as 3.
        if (MeshArena.PackTangent(new Vector4(0f, 0f, -1f, -1f)) != 0xE0100000u) return false;
        if (MeshArena.PackTangent(Vector4.Zero) != 0u) return false;   // "no frame": the derivative fallback
        Vector4 a = MeshArena.UnpackTangent(0x400001FFu);
        if (MathF.Abs(a.X - 1f) > 1e-3f || MathF.Abs(a.Y) > 1e-3f || MathF.Abs(a.Z) > 1e-3f || a.W != 1f) return false;
        Vector4 b = MeshArena.UnpackTangent(0xE0100000u);
        if (MathF.Abs(b.Z + 1f) > 1e-3f || MathF.Abs(b.X) > 1e-3f || b.W != -1f) return false;
        if (MeshArena.UnpackTangent(0u) != Vector4.Zero) return false;

        // the layout: both words LAST, so the bone slots and weights keep offsets 40 and 44
        if (MeshArena.StrideBytes(MeshArena.Fmt.Static) != 48) return false;
        if (MeshArena.StrideBytes(MeshArena.Fmt.Skinned) != 68) return false;
        if (MeshArena.TanWord(MeshArena.Fmt.Static) * 4 != 40 || MeshArena.PaintWord(MeshArena.Fmt.Static) * 4 != 44) return false;
        if (MeshArena.TanWord(MeshArena.Fmt.Skinned) * 4 != 60 || MeshArena.PaintWord(MeshArena.Fmt.Skinned) * 4 != 64) return false;

        // ...and the locations the lit vertex stage declares, read off the embedded GLSL
        string vs = AlloyRenderer.OwnedLitVs;
        if (!vs.Contains($"layout(location = {AlloyRenderer.LocTangent}) in vec4 aTangent;")) return false;
        if (!vs.Contains($"layout(location = {AlloyRenderer.LocPaint}) in vec4 aPaint;")) return false;

        // ...and what actually reached the mirror, for a mesh that carries both and one that carries neither
        Reset();
        MeshDef painted = Quad("tan");
        painted.Tangents = new[] { new Vector4(1f, 0f, 0f, 1f), new Vector4(1f, 0f, 0f, 1f),
                                   new Vector4(0f, 0f, -1f, -1f), new Vector4(0f, 0f, -1f, -1f) };
        painted.Colors = new[] { 0xFF4080C0u, 0xFF4080C0u, 0xFF4080C0u, 0xFF4080C0u };
        int r = MeshArena.Acquire(painted);
        int bare = MeshArena.Acquire(Quad("bare", 3f));
        bool ok = r >= 0 && bare >= 0
               && MeshArena.StreamProbe(r, 0, out uint t0, out uint p0) && t0 == 0x400001FFu && p0 == 0xFF4080C0u
               && MeshArena.StreamProbe(r, 2, out uint t2, out _) && t2 == 0xE0100000u
               && MeshArena.StreamProbe(bare, 0, out uint tb, out uint pb) && tb == 0u && pb == 0xFFFFFFFFu;
        if (r >= 0) MeshArena.Release(r);
        if (bare >= 0) MeshArena.Release(bare);
        MeshArena.FrameEdgeForCheck();
        return ok;
    }

    /// <summary>The packed words are part of the arena's CONTENT identity, so a mesh that differs only in
    /// them cannot be collapsed onto another's range. Oracle: the hand-counted range tally.</summary>
    private static bool ArenaStreamIdentity()
    {
        Reset();
        MeshDef plain = Quad("id");
        MeshDef tangents = Quad("id");
        tangents.Tangents = new[] { new Vector4(1f, 0f, 0f, 1f), new Vector4(1f, 0f, 0f, 1f),
                                    new Vector4(1f, 0f, 0f, 1f), new Vector4(1f, 0f, 0f, 1f) };
        MeshDef painted = Quad("id");
        painted.Colors = new[] { 0xFF102030u, 0xFF102030u, 0xFF102030u, 0xFF102030u };
        // ...and two that differ only in the VALUES, not in whether the stream is there at all: the presence
        // bit alone would collapse these onto the pair above.
        MeshDef turned = Quad("id");
        turned.Tangents = new[] { new Vector4(0f, 1f, 0f, 1f), new Vector4(0f, 1f, 0f, 1f),
                                  new Vector4(0f, 1f, 0f, 1f), new Vector4(0f, 1f, 0f, 1f) };
        MeshDef repainted = Quad("id");
        repainted.Colors = new[] { 0xFF405060u, 0xFF405060u, 0xFF405060u, 0xFF405060u };
        int[] r =
        {
            MeshArena.Acquire(plain), MeshArena.Acquire(tangents), MeshArena.Acquire(painted),
            MeshArena.Acquire(turned), MeshArena.Acquire(repainted),
        };
        bool ok = MeshArena.MeshCount == r.Length;
        for (int i = 0; i < r.Length; i++)
        {
            ok &= r[i] >= 0;
            for (int j = i + 1; j < r.Length; j++) ok &= r[i] != r[j];
        }
        foreach (int id in r) if (id >= 0) MeshArena.Release(id);
        MeshArena.FrameEdgeForCheck();
        return ok;
    }

    /// <summary>The compaction: the live ranges slide down and their index runs come with them.</summary>
    private static bool ArenaCompact()
    {
        Reset();
        // distinct GEOMETRY, not just distinct textures: the arena keys on content, and the texture is not
        // part of it
        var m1 = Model("c1", new[] { -1 }, new[] { NodeKind.TriMesh }, new MeshDef?[] { Quad("a", 0f) });
        var m2 = Model("c2", new[] { -1 }, new[] { NodeKind.TriMesh }, new MeshDef?[] { Quad("b", 7f) });
        DbId a = NewObject(), b = NewObject();
        if (!PartTree.Build(a, m1) || !PartTree.Build(b, m2)) return false;
        int rb = SceneDb.PartMesh[SceneDb.ObjPartFirst[b.Index]];
        if (MeshArena.PlaceForCheck(rb).Base != 4) return false;
        PartTree.Free(a);
        MeshArena.CompactForCheck();
        var place = MeshArena.PlaceForCheck(rb);
        if (place.Base != 0 || place.Off0 != 0 || place.Cnt0 != 6) return false;
        return MeshArena.Runs(rb, 0, out int bv, out nint off, out int cnt, out _) && bv == 0 && off == 0 && cnt == 6;
    }

    /// <summary>BLOCKER 1: a part index is a live <see cref="SceneDb.Parts"/> slot, so every consumer gated
    /// on <c>LiveAt</c> sees it, and its generation moves on at the free.</summary>
    private static bool Claimed()
    {
        Reset();
        var model = Model("cl", new[] { -1, 0, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh, NodeKind.TriMesh },
                          new MeshDef?[] { null, Quad("x"), Quad("y", 1f) });
        DbId o = NewObject();
        if (!PartTree.Build(o, model)) return false;
        int f = SceneDb.ObjPartFirst[o.Index], n = SceneDb.ObjPartCount[o.Index];
        for (int p = f; p < f + n; p++) if (!SceneDb.Parts.LiveAt(p)) return false;
        DbId h = SceneDb.Parts.IdAt(f + 1);
        if (!SceneDb.Parts.Valid(h)) return false;
        // a mark reaches the dirty list only because the slot is live
        int before = SceneDb.Parts.DirtyCount;
        SceneDb.TouchPart(f + 1, Dirty.Material);
        if (SceneDb.Parts.DirtyCount < before) return false;
        if ((SceneDb.Parts.DirtyAt(f + 1) & Dirty.Material) == 0) return false;
        PartTree.Free(o);
        for (int p = f; p < f + n; p++) if (SceneDb.Parts.LiveAt(p)) return false;
        return !SceneDb.Parts.Valid(h);   // the generation moved on at the free
    }

    /// <summary>BLOCKER 2 and finding 3: a chunk handed to a new tree carries nothing of the dead tenant --
    /// not the feed's slot, and not a <see cref="MeshDef"/> shortcut into a range that has been recycled.</summary>
    private static bool Recycled()
    {
        Reset();
        MeshDef shared = Quad("x");
        var a = Model("ra", new[] { -1, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh }, new MeshDef?[] { null, shared });
        DbId o1 = NewObject();
        if (!PartTree.Build(o1, a)) return false;
        int f = SceneDb.ObjPartFirst[o1.Index];
        SceneDb.PartSlot[f + 1] = 42;
        SceneDb.PartFade[f + 1] = 0.25f;
        int range = SceneDb.PartMesh[f + 1];
        PartTree.Free(o1);
        MeshArena.FrameEdgeForCheck();
        // the mesh's shortcut went with the range
        if (MeshArena.MappedForCheck(shared)) return false;

        // another tree takes the same chunk: no inherited slot, no inherited fade
        var b = Model("rb", new[] { -1, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh }, new MeshDef?[] { null, Quad("z", 4f) });
        DbId o2 = NewObject();
        if (!PartTree.Build(o2, b)) return false;
        if (SceneDb.ObjPartFirst[o2.Index] != f) return false;
        if (SceneDb.PartSlot[f + 1] != -1 || SceneDb.PartFade[f + 1] != 1f) return false;
        if (SceneDb.PartRecipe[f + 1] != -1 || SceneDb.PartBoneBase[f + 1] != -1) return false;
        // and re-acquiring the first mesh must NOT hand back the recycled id with the other mesh's geometry
        int again = MeshArena.Acquire(shared);
        if (again >= 0 && again == SceneDb.PartMesh[f + 1]) return false;
        MeshArena.Release(again);
        return true;
    }

    /// <summary>Single part slots and part RANGES come from one allocator: a range allocator with a cursor of
    /// its own eventually hands out a chunk holding slots <see cref="DbTable.Alloc"/> has already made live, and
    /// the claim fails. Drives that order -- fixtures first, trees after, interleaved.</summary>
    private static bool OneAllocator()
    {
        Reset();
        var held = new List<DbId>();
        var taken = new HashSet<int>();
        // the boot walk: other suites take single part slots and many never give them back
        for (int i = 0; i < 64; i++)
        {
            DbId h = SceneDb.Parts.Alloc();
            if (h.IsNull) return false;
            if (!taken.Add(h.Index)) return false;   // Alloc handed out one index twice
            held.Add(h);
        }
        var model = Model("oa", new[] { -1, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh },
                          new MeshDef?[] { null, Quad("x") });
        // then the trees, interleaved with more single slots, as a GUI panel's objects arrive
        for (int round = 0; round < 24; round++)
        {
            DbId o = NewObject();
            if (!PartTree.Build(o, model)) return false;   // a refusal here IS the blocker
            int f = SceneDb.ObjPartFirst[o.Index], n = SceneDb.ObjPartCount[o.Index];
            for (int p = f; p < f + n; p++)
            {
                if (!taken.Add(p)) return false;           // a chunk overlapped something already handed out
                if (!SceneDb.Parts.LiveAt(p)) return false;
            }
            DbId h = SceneDb.Parts.Alloc();
            if (h.IsNull || !taken.Add(h.Index)) return false;
            held.Add(h);
            // every other tree goes away again, so the chunk bins are exercised too
            if ((round & 1) == 0)
            {
                for (int p = f; p < f + n; p++) taken.Remove(p);
                PartTree.Free(o);
                for (int p = f; p < f + n; p++) if (SceneDb.Parts.LiveAt(p)) return false;
            }
        }
        // The single slots are deliberately NOT returned. A sibling fixture (DbAnim's) needs three
        // CONSECUTIVE indices out of Alloc(), which only holds while the table's single-slot free list is
        // empty and it is handing out _high++ -- so a check that frees single part slots silently breaks it.
        // The chunk bins above are a different list and disturb nothing. (The durable fix is for that
        // fixture to take a range through AllocRange, which now guarantees contiguity.)
        held.Clear();
        return true;
    }

    /// <summary>Finding 17: the ceilings and bucket terms the earlier suite never drove.</summary>
    private static bool Ceilings()
    {
        // a tree past the biggest size class is refused, not clamped
        Reset();
        int big = 1 << 17;   // one past the 65536-part class
        var huge = new ModelDef { Name = "huge", Nodes = new NodeDef[big] };
        var kids = new int[big - 1];
        for (int i = 0; i < big; i++) huge.Nodes[i] = new NodeDef { Index = i, Parent = i == 0 ? -1 : 0 };
        for (int i = 1; i < big; i++) kids[i - 1] = i;
        huge.Nodes[0].Children = kids;
        DbId o = NewObject();
        if (PartTree.Build(o, huge)) return false;
        if (SceneDb.ObjPartCount[o.Index] != 0) return false;

        // render == 0 is not merge-eligible (CreatePartBuckets' node+0x118 term)
        Reset();
        MeshDef hidden = Quad("x", 1f);
        hidden.Render = false;
        MeshDef hidden2 = Quad("x", 2f);
        hidden2.Render = false;
        var nr = Model("nr", new[] { -1, 0, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh, NodeKind.TriMesh },
                       new MeshDef?[] { null, hidden, hidden2 });
        DbId p = NewObject();
        if (!PartTree.Build(p, nr, new PartTree.TreeOptions { CombineOwn = true, Rotation = Quaternion.Identity })) return false;
        int pf = SceneDb.ObjPartFirst[p.Index];
        if (SceneDb.PartMerged[pf + 1] || SceneDb.PartMerged[pf + 2]) return false;
        // ... and its box is still computed, because the hit test reads it (finding 14)
        if (SceneDb.PartBMax[pf + 1] == Vector3.Zero) return false;

        // the 64k bucket ceiling: two meshes whose vertices together reach it do not merge
        Reset();
        MeshDef fat = Big(40000);
        MeshDef fat2 = Big(40000);
        var bc = Model("bc", new[] { -1, 0, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh, NodeKind.TriMesh },
                       new MeshDef?[] { null, fat, fat2 });
        DbId q = NewObject();
        if (!PartTree.Build(q, bc, new PartTree.TreeOptions { CombineOwn = true, Rotation = Quaternion.Identity })) return false;
        int qf = SceneDb.ObjPartFirst[q.Index];
        if (SceneDb.PartMerged[qf + 2]) return false;

        // a different materialname is a different bucket
        Reset();
        MeshDef m1 = Quad("x"), m2 = Quad("x", 1f);
        m1.MaterialName = "one";
        m2.MaterialName = "two";
        var mn = Model("mn", new[] { -1, 0, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh, NodeKind.TriMesh },
                       new MeshDef?[] { null, m1, m2 });
        DbId r = NewObject();
        if (!PartTree.Build(r, mn, new PartTree.TreeOptions { CombineOwn = true, Rotation = Quaternion.Identity })) return false;
        int rf = SceneDb.ObjPartFirst[r.Index];
        return !SceneDb.PartMerged[rf + 1] && !SceneDb.PartMerged[rf + 2];
    }

    /// <summary>A fan of <paramref name="n"/> vertices: enough triangles to reach a bucket ceiling.</summary>
    private static MeshDef Big(int n)
    {
        var m = new MeshDef
        {
            Positions = new Vector3[n],
            Normals = new Vector3[n],
            Indices = new uint[(n - 2) * 3],
        };
        m.Uv[0] = new Vector2[n];
        for (int i = 0; i < n; i++) { m.Positions[i] = new Vector3(i, i * 0.5f, 0); m.Normals[i] = Vector3.UnitZ; }
        for (int i = 0; i < n - 2; i++) { m.Indices[i * 3] = 0; m.Indices[i * 3 + 1] = (uint)(i + 1); m.Indices[i * 3 + 2] = (uint)(i + 2); }
        m.Textures[0] = "fat";
        return m;
    }

    /// <summary>The meshes-vs-placements census, fed from this builder.</summary>
    private static bool Census()
    {
        Reset();
        long p0 = CX.Alloy.Mdl.ModelCache.Placements;
        var model = Model("cen", new[] { -1, 0, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh, NodeKind.TriMesh },
                          new MeshDef?[] { null, Quad("x"), Quad("x", 3f) });
        DbId o = NewObject();
        if (!PartTree.Build(o, model)) return false;
        if (CX.Alloy.Mdl.ModelCache.Placements != p0 + 2) return false;   // the dummy places nothing
        PartTree.Free(o);
        if (CX.Alloy.Mdl.ModelCache.Placements != p0) return false;

        // the merge: the two members' placements go and the merged mesh takes one
        DbId m = NewObject();
        if (!PartTree.Build(m, model, new PartTree.TreeOptions { CombineOwn = true, Rotation = Quaternion.Identity })) return false;
        if (CX.Alloy.Mdl.ModelCache.Placements != p0 + 1) return false;
        PartTree.Free(m);
        return CX.Alloy.Mdl.ModelCache.Placements == p0;
    }

    /// <summary><c>DbStage</c> writes a range's base vertex and index offset into the slot ONCE, so a compaction
    /// that slides the range down would leave the record drawing out of whatever took its place. A,B,C; free B;
    /// compact; C must have MOVED, be reported as moved, and the arena's answer for it must be the new
    /// placement -- which is what the draw side refreshes from.</summary>
    private static bool CompactRestage()
    {
        Reset();
        Action<int[], int>? saved = MeshArena.RangesMoved;
        var reported = new List<int>();
        MeshArena.RangesMoved = (ids, n) => { for (int k = 0; k < n; k++) reported.Add(ids[k]); };
        try
        {
            var ma = Model("ka", new[] { -1 }, new[] { NodeKind.TriMesh }, new MeshDef?[] { Quad("a", 0f) });
            var mb = Model("kb", new[] { -1 }, new[] { NodeKind.TriMesh }, new MeshDef?[] { Quad("b", 1f) });
            var mc = Model("kc", new[] { -1 }, new[] { NodeKind.TriMesh }, new MeshDef?[] { Quad("c", 2f) });
            DbId a = NewObject(), b = NewObject(), c = NewObject();
            if (!PartTree.Build(a, ma) || !PartTree.Build(b, mb) || !PartTree.Build(c, mc)) return false;
            int ra = SceneDb.PartMesh[SceneDb.ObjPartFirst[a.Index]];
            int rb = SceneDb.PartMesh[SceneDb.ObjPartFirst[b.Index]];
            int rc = SceneDb.PartMesh[SceneDb.ObjPartFirst[c.Index]];
            if (ra < 0 || rb < 0 || rc < 0 || ra == rb || rb == rc) return false;

            // what a record staged BEFORE the compaction would have cached for C
            if (!MeshArena.Runs(rc, 0, out int wasBase, out nint wasOff, out int wasCount, out _)) return false;

            PartTree.Free(b);
            MeshArena.CompactForCheck();

            // C slid down into B's place, so the cached numbers are now someone else's
            if (!MeshArena.Runs(rc, 0, out int nowBase, out nint nowOff, out int nowCount, out _)) return false;
            if (nowBase == wasBase && nowOff == wasOff) return false;   // nothing moved: the check proves nothing
            if (nowCount != wasCount) return false;                     // same geometry, only its place changed
            if (!reported.Contains(rc)) return false;                   // the draw side was not told
            if (reported.Contains(rb)) return false;                    // a freed range is not a mover

            // and the arena's own bookkeeping agrees with what it hands a draw
            var place = MeshArena.PlaceForCheck(rc);
            return place.Base == nowBase && place.Off0 == nowOff && place.Cnt0 == nowCount;
        }
        finally { MeshArena.RangesMoved = saved; }
    }

    /// <summary>The <c>.mtr</c> parse, against <c>SharedMaterial::ParseField</c> @0x5c6760.</summary>
    private static bool Mtr()
    {
        const string text = "// a material\n"
                          + "bitmap stone_d\n"
                          + "texture1 stone_n\n"
                          + "texture10 stone_x\n"
                          + "renderhint NormalAndSpecMapped\n"
                          + "customShaderVS cx_my_v\n"
                          + "parameter float roughness 0.4 0.5\n"
                          + "parameter int NWN_ALPHA_MODE 2\n"
                          + "parameter untyped 1\n"
                          + "twosided 1\n";
        MtrDef m = MtrDef.Parse(Encoding.ASCII.GetBytes(text), "stone");
        if (m.Textures[0] != "stone_d" || m.Textures[1] != "stone_n" || m.Textures[10] != "stone_x") return false;
        if (m.Textures[2].Length != 0) return false;
        if (m.RenderHint != 2 || m.ShaderVs != "cx_my_v" || !m.TwoSided || m.Transparency) return false;
        if (m.Parameters.Count != 2 || m.Parameters[0].Name != "roughness"
            || m.Parameters[0].Values.Length != 2 || MathF.Abs(m.Parameters[0].Values[1] - 0.5f) > 1e-6f) return false;
        if (m.AlphaMode != 2) return false;
        if (MtrDef.Hint("none") != 1 || MtrDef.Hint("normaltangents") != 3 || MtrDef.Hint("whatever") != 0) return false;

        // the part's material folds the file in: its texture name wins over the mesh's, and so does its hint
        Reset();
        MtrDef.Forget();
        MtrDef.Bytes = n => n == "stone" ? Encoding.ASCII.GetBytes(text) : null;
        MeshDef mesh = Quad("mesh_tex");
        mesh.MaterialName = "stone";
        var model = Model("mt", new[] { -1 }, new[] { NodeKind.TriMesh }, new MeshDef?[] { mesh });
        DbId o = NewObject();
        if (!PartTree.Build(o, model)) return false;
        int f = SceneDb.ObjPartFirst[o.Index];
        return SceneDb.PartMtr[f] != null && SceneDb.PartTexName(f, 0) == "stone_d"
               && SceneDb.PartTexName(f, 1) == "stone_n" && SceneDb.PartMtl[f].RenderHint == 2;
    }

    /// <summary>A recipe interned while its mesh's <c>.mtr</c> was unreadable: the coverage mode cannot be
    /// known yet, and only the material's own landing edge can supply it -- no texture serial moves for it.
    /// Oracle: the mode is a hand-written <c>.mtr</c> text and the expected flag is written out, not read
    /// back off the recipe.</summary>
    private static bool LateCoverage()
    {
        const string text = "bitmap cov_d\nparameter int NWN_ALPHA_MODE 2\n";
        Reset();
        MtrDef.Forget();
        MtrDef.Bytes = _ => null;   // the parse asked and the feed had not read it yet
        MeshDef mesh = Quad("cov_tex");
        mesh.MaterialName = "cx_selfcheck_cov";
        var model = Model("cx_selfcheck_cov", new[] { -1, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh },
                          new MeshDef?[] { null, mesh });
        int r = DrawRecipe.Intern(model, 1, plan: false);
        bool ok = r >= 0 && (DrawRecipe.Flags(r) & RecipeFlag.Coverage) == 0;
        // ...and the read lands. Reclassify is the one re-ask, and it must report the move so the records
        // of every part on this recipe are rewritten.
        MtrDef.Bytes = n => n == "cx_selfcheck_cov" ? Encoding.ASCII.GetBytes(text) : null;
        DrawRecipe.MaterialLanded();
        ok &= DrawRecipe.Reclassify() > 0;
        ok &= r >= 0 && (DrawRecipe.Flags(r) & RecipeFlag.Coverage) != 0
                     && DrawRecipe.Cls(r) == (int)AlloyClass.Cutout;
        DrawRecipe.Forget(model);
        DrawRecipe.Pump();
        DrawRecipe.Pump();
        return ok;
    }

    /// <summary>A part built while its mesh's <c>.mtr</c> was unreadable: its material columns are the mesh's
    /// alone, and only the read's own landing edge can fold the file in -- after which a PLT replace aims at
    /// the file's diffuse and no longer at the mesh bitmap. Oracles: a hand-written <c>.mtr</c> text and the
    /// resrefs the replace must take and must refuse.</summary>
    private static bool LateMaterialColumns()
    {
        Reset();
        MtrDef.Forget();
        MtrDef.Bytes = _ => null;   // the part is built while the feed has not read the file
        MeshDef mesh = Quad("cx_chest001");
        mesh.MaterialName = "cx_late_mat";
        var model = Model("cx_chest003", new[] { -1, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh },
                          new MeshDef?[] { null, mesh });
        DbId obj = NewObject();
        if (!PartTree.Build(obj, model)) return false;
        int p = SceneDb.ObjPartFirst[obj.Index] + 1;
        bool ok = SceneDb.PartMtr[p] == null && SceneDb.PartTexName(p, 0) == "cx_chest001"
                  && SceneDb.PartMtl[p].RenderHint == 0
                  && (SceneDb.Parts.DirtyAt(p) & Dirty.Material) == 0;

        // ...and the read lands. The edge is the one re-ask: nothing else revisits a built part's material.
        MtrDef.Bytes = n => n == "cx_late_mat"
            ? Encoding.ASCII.GetBytes("texture0 cx_chest003\nrenderhint NormalAndSpecMapped\n") : null;
        DrawRecipe.MaterialLanded();
        ok &= PartTree.ReseedMaterials() > 0;
        ok &= SceneDb.PartMtr[p] != null && SceneDb.PartTexName(p, 0) == "cx_chest003"
              && SceneDb.PartMtl[p].RenderHint == 2;
        // ...and the part is MARKED, which is what rewrites its record and re-resolves its texture slots
        ok &= (SceneDb.Parts.DirtyAt(p) & Dirty.Material) != 0;

        // CX-226's rule over the freshly folded file: the mesh bitmap no longer names slot 0, the file does.
        Span<int> rows = stackalloc int[SceneDb.PltLayers];
        for (int l = 0; l < SceneDb.PltLayers; l++) rows[l] = l + 1;
        ok &= Seam.ObjectTex.Replace(obj, "cx_chest001", "cx_chest042", rows: rows) == 0;
        ok &= Shows(p) == "" && !SceneDb.PartPltOwn[p];
        ok &= Seam.ObjectTex.Replace(obj, "cx_chest003", "cx_chest042", rows: rows) == 1;
        ok &= Shows(p) == "cx_chest042" && SceneDb.PartPltOwn[p];
        return ok;
    }
}
