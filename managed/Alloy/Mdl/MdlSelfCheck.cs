using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace CX.Alloy.Mdl;

/// <summary>The off-device suite: one embedded ASCII model and one hand-built binary one, asserted on
/// known values -- the node tree, the weld, the derived normals, the adjacency, the surface area, the
/// controllers and the child reorder. No engine, no GL, no files, so it runs under <c>run-demo.sh</c> and
/// under <c>tools/mdlcheck --selfcheck</c> unchanged.</summary>
public static class MdlSelfCheck
{
    /// <summary>Run every case; returns the number of failures and reports each through
    /// <paramref name="report"/> (false plus the reason).</summary>
    public static int Run(Action<bool, string> report)
    {
        int bad = 0;
        void Expect(bool ok, string what) { if (!ok) { bad++; report(false, what); } }
        Ascii(Expect);
        AnimBlock(Expect);
        Wok(Expect);
        Binary(Expect);
        LateTangents(Expect);
        Tokens(Expect);
        Fuzz(Expect);
        if (bad == 0) report(true, $"MdlParser.SelfCheck: passed ({FuzzCases} hostile-input cases)");
        return bad;
    }

    /// <summary>A quad as two triangles, a light, a second mesh with a transparency hint, and controllers in
    /// all three spellings -- enough to exercise the driver, the array bodies, the weld, the derived normals,
    /// the adjacency, the surface area and the child reorder.</summary>
    private const string Model = """
        # a comment the driver drops
        newmodel CX_Fixture
        setsupermodel cx_fixture null
        classification tile
        setanimationscale 0.75
        beginmodelgeom cx_fixture
          node dummy rootnode
            parent NULL
          endnode
          node trimesh mesh_a
            parent rootnode
            position 1.0 2.0 3.0
            orientation 0 0 1 0
            scale 2.0
            transparencyhint 0
            diffuse 0.5 0.25 0.125
            bitmap cx_tex
            verts 4
              0.0 0.0 0.0
              1.0 0.0 0.0
              1.0 1.0 0.0
              0.0 1.0 0.0
            tverts 4
              0.0 0.0 0.0
              1.0 0.0 0.0
              1.0 1.0 0.0
              0.0 1.0 0.0
            faces 2
              0 1 2 1 0 1 2 7
              0 2 3 1 0 2 3 7
          endnode
          node light lamp
            parent rootnode
            flareradius 4.5
            lightpriority 3
            ambientonly 1
            colorkey 2
              0.0 1.0 0.0 0.0
              1.0 0.0 0.0 1.0
          endnode
          node trimesh mesh_b
            parent rootnode
            transparencyhint 2
            verts 3
              0.0 0.0 0.0
              1.0 0.0 0.0
              0.0 1.0 0.0
            faces 1
              0 1 2 0 0 0 0 1
          endnode
        endmodelgeom
        newanim walk cx_fixture
          length 2.5
          transtime 0.125
          animroot rootnode
          event 1.5 hit
          event 0.5 start
          node dummy rootnode
            parent NULL
            positionkey 2
              0.0 0.0 0.0 0.0
              1.0 1.0 0.0 0.0
          endnode
        doneanim
        donemodel cx_fixture
        """;

    /// <summary>A tile walkmesh, in the shape the containers actually hold one (restype 2016): the
    /// <c>#MAXWALKMESH</c> header, no <c>newmodel</c>, the geometry wrapped in <c>beginwalkmeshgeom</c>, and a
    /// single <c>node aabb</c> whose faces carry the surface ids the hit test reports. Verbatim-shaped from
    /// <c>tin01_a16_01.wok</c>.</summary>
    private const string Walkmesh = """
        #MAXWALKMESH  ASCII
        beginwalkmeshgeom cx_wok
        node aabb Object613
          parent cx_wok
          position 0.0 0.0 0.0
          orientation 0.0 0.0 0.0 0.0
          verts 4
            -5.0 -5.0 0.0
             5.0 -5.0 0.0
             5.0  5.0 0.0
            -5.0  5.0 0.0
          faces 2
            0 1 2  1  0 0 0  4
            0 2 3  1  0 0 0  4
          aabb  -5.0 -5.0 0.0   5.0 5.0 0.0  -1
            -5.0 -5.0 0.0  5.0 5.0 0.0  0
            -5.0 -5.0 0.0  5.0 5.0 0.0  1
        endnode
        endwalkmeshgeom
        """;

    /// <summary>An animmesh whose frames sit in an ANIMATION block that restates its geometry -- how a GUI bar
    /// is driven by its <c>percentage</c> animation. Oracle: the fixture's own frames, rest pose then doubled.</summary>
    private const string AnimFixture = """
newmodel cx_bar
beginmodelgeom cx_bar
node dummy cx_bar
  parent null
endnode
node animmesh bar
  parent cx_bar
  bitmap cx_bar_t
  verts 4
    0 0 0
    1 0 0
    0 1 0
    1 1 0
  tverts 4
    0 0 0
    1 0 0
    0 1 0
    1 1 0
  faces 2
    0 1 2 1 0 1 2 0
    3 2 1 1 3 2 1 0
endnode
endmodelgeom cx_bar
newanim grow cx_bar
  length 1.0
  transtime 0.0
  animroot cx_bar
  node dummy cx_bar
    parent null
  endnode
  node animmesh bar
    parent cx_bar
    verts 4
      0 0 0
      1 0 0
      0 1 0
      1 1 0
    tverts 4
      0 0 0
      1 0 0
      0 1 0
      1 1 0
    faces 2
      0 1 2 1 0 1 2 0
      3 2 1 1 3 2 1 0
    sampleperiod 1.0
    animverts 8
      0 0 0
      1 0 0
      0 1 0
      1 1 0
      0 0 0
      2 0 0
      0 2 0
      2 2 0
    endlist
  endnode
doneanim grow cx_bar
donemodel cx_bar
""";

    private static void AnimBlock(Action<bool, string> expect)
    {
        ModelDef? m = MdlParser.Parse(Encoding.ASCII.GetBytes(AnimFixture.Replace("\r\n", "\n")), "cx_bar", out string? err);
        expect(m != null, $"the animation-block animmesh fixture did not parse ({err})");
        if (m == null) return;
        MeshDef? rest = Array.Find(m.Nodes, n => n.Name == "bar")?.Mesh;
        AnimMeshDef? am = m.Anims.Length == 1 && m.Anims[0].Nodes.Length == 2 ? m.Anims[0].Nodes[1].AnimMesh : null;
        expect(rest != null && am != null, "the fixture lost its bar node or its animation's bar track");
        if (rest == null || am == null) return;
        expect(am.Positions.Length == 2, $"8 animverts over 4 authored vertices read as {am.Positions.Length} frame(s), expected 2");
        if (am.Positions.Length != 2) return;
        int n = rest.Positions.Length;
        expect(am.Positions[0].Length == n && am.Positions[1].Length == n,
               $"frames of {am.Positions[0].Length}/{am.Positions[1].Length} vertices against a {n}-vertex weld -- the morph would write the wrong vertices");
        if (am.Positions[0].Length != n || am.Positions[1].Length != n) return;
        bool same = true, doubled = true;
        for (int i = 0; i < n; i++)
        {
            same &= Near(am.Positions[0][i], rest.Positions[i]);
            doubled &= Near(am.Positions[1][i], rest.Positions[i] * 2f);
        }
        expect(same, "frame 0 is not the rest pose vertex for vertex: the frame is not indexed by the part's weld");
        expect(doubled, "frame 1 is not the fixture's doubled quad vertex for vertex");
    }

    /// <summary>The walkmesh parses, and parses as something the hit test can use -- an
    /// <c>aabb</c> node with a tree and per-face surface ids. Without this the tile floor is silently absent
    /// and the symptom is a creature nine metres in the air.</summary>
    private static void Wok(Action<bool, string> expect)
    {
        // CRLF on purpose: the containers hold .wok files with DOS line endings, and an LF-only fixture
        // would pass while every real walkmesh parsed to nothing.
        byte[] src = Encoding.ASCII.GetBytes(Walkmesh.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        ModelDef? m = MdlParser.Parse(src, "cx_wok", out string? err);
        expect(m != null, $"the .wok fixture did not parse ({err})");
        if (m == null) return;
        int aabb = -1;
        for (int i = 0; i < m.Nodes.Length; i++)
            if (m.Nodes[i].Kind == NodeKind.Aabb) { aabb = i; break; }
        expect(aabb >= 0, "the .wok parsed with no aabb node -- the tile would have no floor");
        if (aabb < 0) return;
        NodeDef nd = m.Nodes[aabb];
        expect(nd.Mesh != null && nd.Mesh.Faces.Length == 2, "the walkmesh node carries no faces");
        expect(nd.Aabb != null && nd.Aabb.Tree.Length > 0, "the walkmesh node carries no aabb tree");
        if (nd.Mesh != null && nd.Mesh.Faces.Length == 2)
            expect(nd.Mesh.Faces[0].Surface == 4 && nd.Mesh.Faces[1].Surface == 4,
                   $"the walkmesh's surface ids read {nd.Mesh.Faces[0].Surface}/{nd.Mesh.Faces[1].Surface}, want 4 "
                 + "(the hit test reports them as CHitInfo+0x40)");
    }

    private static void Ascii(Action<bool, string> expect)
    {
        byte[] src = Encoding.ASCII.GetBytes(Model.Replace("\r\n", "\n"));
        ModelDef? m = MdlParser.Parse(src, "cx_fixture", out string? err);
        expect(m != null, $"the ASCII fixture did not parse ({err})");
        if (m == null) return;

        expect(m.StatedName == "CX_Fixture", $"newmodel's name read as '{m.StatedName}', case must be kept");
        expect(m.Classification == 2, $"classification tile read as {m.Classification}");
        expect(m.SuperName.Length == 0, $"supermodel 'null' read as '{m.SuperName}'");
        expect(Near(m.AnimScale, 0.75f), $"setanimationscale read as {m.AnimScale}");
        expect(m.Fog, "fog defaulted off");
        expect(m.Nodes.Length == 4, $"{m.Nodes.Length} nodes, expected 4");
        if (m.Nodes.Length != 4) return;

        // The reorder puts the non-mesh child first, then the meshes by ascending transparency hint.
        expect(m.Nodes[0].Name == "rootnode" && m.Nodes[0].Kind == NodeKind.Dummy, "node 0 is not the root dummy");
        expect(m.Nodes[1].Name == "lamp", $"node 1 is '{m.Nodes[1].Name}', expected the light (reorder)");
        expect(m.Nodes[2].Name == "mesh_a", $"node 2 is '{m.Nodes[2].Name}', expected mesh_a");
        expect(m.Nodes[3].Name == "mesh_b", $"node 3 is '{m.Nodes[3].Name}', expected mesh_b");
        expect(m.Nodes[1].Parent == 0 && m.Nodes[2].Parent == 0 && m.Nodes[3].Parent == 0, "children not parented to the root");
        expect(m.Nodes[0].Children.Length == 3, $"the root has {m.Nodes[0].Children.Length} children");
        // Node numbers follow FILE order, which the reorder does not touch.
        expect(m.Nodes[2].PartNumber == 1 && m.Nodes[1].PartNumber == 2 && m.Nodes[3].PartNumber == 3,
               "node numbers do not follow file order");

        NodeDef a = m.Nodes[2];
        expect(a.Kind == NodeKind.TriMesh, "mesh_a is not a trimesh");
        expect(Near(a.Position, new Vector3(1f, 2f, 3f)), $"rest position {a.Position}");
        expect(Near(a.Scale, 2f), $"rest scale {a.Scale}");
        // `orientation 0 0 1 0` is axis (0,0,1) angle 0 -> identity.
        expect(Near(a.Orientation.W, 1f) && Near(a.Orientation.Z, 0f), $"rest orientation {a.Orientation}");
        ControllerTrack? pos = a.Controller(Ctl.Position);
        expect(pos is { Columns: 3, Bezier: false, Times.Length: 1 }, "the plain position controller is not one 3-column key");
        MeshDef? mesh = a.Mesh;
        expect(mesh != null, "mesh_a has no mesh");
        if (mesh == null) return;
        expect(mesh.Textures[0] == "cx_tex", $"bitmap read as '{mesh.Textures[0]}'");
        expect(Near(mesh.Diffuse, new Vector3(0.5f, 0.25f, 0.125f)), $"diffuse {mesh.Diffuse}");
        expect(mesh.Positions.Length == 4, $"the weld produced {mesh.Positions.Length} vertices, expected 4");
        expect(mesh.Faces.Length == 2, $"{mesh.Faces.Length} faces, expected 2");
        expect(mesh.Indices.Length == 6 && mesh.Indices[0] == 0 && mesh.Indices[1] == 1 && mesh.Indices[2] == 2
               && mesh.Indices[3] == 0 && mesh.Indices[4] == 2 && mesh.Indices[5] == 3,
               "the welded index list is not 0,1,2,0,2,3");
        bool flat = true;
        foreach (Vector3 nn in mesh.Normals) flat &= Near(nn, new Vector3(0f, 0f, 1f));
        expect(flat && mesh.Normals.Length == 4, "the derived normals are not four +Z");
        expect(Near(mesh.SurfaceArea, 1f), $"surface area {mesh.SurfaceArea}, expected 1");
        expect(mesh.Faces[0].Adj2 == 1 && mesh.Faces[1].Adj0 == 0, "the shared edge did not find its neighbour");
        expect(mesh.Faces[0].Adj0 == -1 && mesh.Faces[0].Adj1 == -1, "an open edge claims a neighbour");
        expect(mesh.Faces[0].Surface == 7 && mesh.Faces[0].SmoothGroup == 1, "the face's surface id or smoothing group was lost");
        expect(Near(mesh.Faces[0].PlaneNormal, new Vector3(0f, 0f, 1f)) && Near(mesh.Faces[0].PlaneDistance, 0f),
               "the face plane is wrong");
        expect(Near(mesh.BMin, Vector3.Zero) && Near(mesh.BMax, new Vector3(1f, 1f, 0f)), $"mesh bounds {mesh.BMin}..{mesh.BMax}");
        expect(mesh.Uv[0].Length == 4 && Near(mesh.Uv[0][2], new Vector2(1f, 1f)), "UV set 0 did not survive the weld");

        NodeDef l = m.Nodes[1];
        expect(l.Kind == NodeKind.Light && l.Light != null, "lamp is not a light");
        expect(l.Light != null && Near(l.Light.FlareRadius, 4.5f) && l.Light.Priority == 3 && l.Light.AmbientOnly,
               "light fields were lost");
        ControllerTrack? col = l.Controller(Ctl.LightColor);
        expect(col is { Columns: 3, Times.Length: 2 }, "the light colour key block is not two 3-column keys");
        expect(col != null && Near(col.Times[1], 1f) && Near(col.Values[0], 1f) && Near(col.Values[3], 0f),
               "the colour keys' times and values are not in [all times][all values] order");

        expect(m.Anims.Length == 1, $"{m.Anims.Length} animations, expected 1");
        if (m.Anims.Length != 1) return;
        AnimDef an = m.Anims[0];
        expect(an.Name == "walk", $"animation named '{an.Name}'");
        expect(Near(an.Length, 2.5f) && Near(an.TransTime, 0.125f), "animation length or transtime was lost");
        expect(an.AnimRoot == "rootnode", $"animroot '{an.AnimRoot}'");
        expect(an.Events.Length == 2 && an.Events[0].Name == "start" && an.Events[1].Name == "hit",
               "animation events are not sorted by ascending time");
        expect(an.Nodes.Length == 1 && an.Nodes[0].Parent == -1, "the animation's node tree is wrong");
        expect(an.Nodes.Length == 1 && an.Nodes[0].Controllers.Length == 1
               && an.Nodes[0].Controllers[0].Times.Length == 2, "the animation track did not read two keys");
    }

    // ---- a hand-built binary model ----

    private const int MHeader = 0xe8, NHeader = 0x70, TriBlock = 0x270, FaceRec = 0x20;   // not-an-offset: a field of the MDL FILE FORMAT, written by hand here

    /// <summary>A hand-built binary model: a root dummy and one trimesh with three vertices, one face, one
    /// UV set and a stated surface area. Shared by the binary case and the hostile-input mutations.</summary>
    private static byte[] BinaryFixture()
    {
        // model block: header, root dummy, its child array, the trimesh, the face record.
        int rootAt = MHeader, kidsAt = rootAt + NHeader, meshAt = kidsAt + 4, facesAt = meshAt + TriBlock;
        int modelSize = facesAt + FaceRec;
        var model = new byte[modelSize];
        var raw = new byte[3 * 12 + 3 * 12 + 3 * 8];

        Name(model, 0x08, "CX_Bin");
        I32(model, 0x48, rootAt);
        I32(model, 0x4c, 2);
        model[0x6c] = 2;                       // geometry type: a model
        model[0x72] = 4;                       // classification: character
        model[0x73] = 1;                       // fog
        F32(model, 0xa0, 9f);                  // radius
        F32(model, 0xa4, 1f);                  // animationscale
        Name(model, 0xa8, "null");

        // root dummy
        I32(model, rootAt + 0x1c, 0);
        Name(model, rootAt + 0x20, "rootnode");
        I32(model, rootAt + 0x48, kidsAt);
        I32(model, rootAt + 0x4c, 1);
        I32(model, rootAt + 0x6c, 1);
        I32(model, kidsAt, meshAt);

        // the trimesh
        I32(model, meshAt + 0x1c, 1);
        Name(model, meshAt + 0x20, "mesh");
        I32(model, meshAt + 0x6c, 0x21);
        I32(model, meshAt + 0x78, facesAt);
        I32(model, meshAt + 0x7c, 1);
        F32(model, meshAt + 0xd0, 1f);         // shininess
        I32(model, meshAt + 0xd4, 1);          // shadow
        I32(model, meshAt + 0xdc, 1);          // render
        Name(model, meshAt + 0xe8, "cx_t");
        I32(model, meshAt + 0x1ec, 0);
        I32(model, meshAt + 0x1f0, 0);
        I32(model, meshAt + 0x210, 0);
        I32(model, meshAt + 0x214, 0);
        U32(model, meshAt + 0x21c, 0xFFFFFFFF);
        I32(model, meshAt + 0x224, 3);         // vertices per primitive
        U32(model, meshAt + 0x22c, 0);         // positions at raw 0
        U16(model, meshAt + 0x230, 3);         // vertex count
        U16(model, meshAt + 0x232, 1);         // one UV set
        U32(model, meshAt + 0x234, 36 + 36);   // uv0
        U32(model, meshAt + 0x238, 0xFFFFFFFF);
        U32(model, meshAt + 0x23c, 0xFFFFFFFF);
        U32(model, meshAt + 0x240, 0xFFFFFFFF);
        U32(model, meshAt + 0x244, 36);        // normals
        U32(model, meshAt + 0x248, 0xFFFFFFFF);
        U32(model, meshAt + 0x24c, 0xFFFFFFFF);
        U32(model, meshAt + 0x250, 0xFFFFFFFF);
        U32(model, meshAt + 0x254, 0xFFFFFFFF);
        U32(model, meshAt + 0x258, 0xFFFFFFFF);
        U32(model, meshAt + 0x25c, 0xFFFFFFFF);
        U32(model, meshAt + 0x260, 0xFFFFFFFF);
        F32(model, meshAt + 0x268, 0.5f);      // surface area

        // one face: plane +Z through the origin, surface 5, no neighbours, corners 0,1,2
        F32(model, facesAt + 0x08, 1f);
        I32(model, facesAt + 0x10, 5);
        U16(model, facesAt + 0x14, 0xFFFF);
        U16(model, facesAt + 0x16, 0xFFFF);
        U16(model, facesAt + 0x18, 0xFFFF);
        U16(model, facesAt + 0x1a, 0);
        U16(model, facesAt + 0x1c, 1);
        U16(model, facesAt + 0x1e, 2);

        Vec3(raw, 0, new Vector3(0f, 0f, 0f));
        Vec3(raw, 12, new Vector3(1f, 0f, 0f));
        Vec3(raw, 24, new Vector3(0f, 1f, 0f));
        for (int i = 0; i < 3; i++) Vec3(raw, 36 + i * 12, new Vector3(0f, 0f, 1f));
        for (int i = 0; i < 3; i++) { F32(raw, 72 + i * 8, i); F32(raw, 72 + i * 8 + 4, i * 2); }

        var file = new byte[12 + model.Length + raw.Length];
        I32(file, 0, 0);
        I32(file, 4, model.Length);
        I32(file, 8, raw.Length);
        model.CopyTo(file, 12);
        raw.CopyTo(file, 12 + model.Length);

        return file;
    }

    private static void Binary(Action<bool, string> expect)
    {
        byte[] file = BinaryFixture();
        ModelDef? m = MdlParser.Parse(file, "cx_bin", out string? err);
        expect(m != null, $"the binary fixture did not parse ({err})");
        if (m == null) return;
        expect(!m.FromAscii, "a binary file was read as ASCII");
        expect(m.StatedName == "CX_Bin", $"the header's model name read as '{m.StatedName}', case must be kept");
        expect(m.Classification == 4, $"classification {m.Classification}, expected 4");
        expect(m.SuperName.Length == 0, $"supermodel 'null' read as '{m.SuperName}'");
        expect(Near(m.Radius, 9f), $"radius {m.Radius}");
        expect(m.Nodes.Length == 2, $"{m.Nodes.Length} nodes, expected 2");
        if (m.Nodes.Length != 2) return;
        expect(m.Nodes[0].Name == "rootnode" && m.Nodes[1].Name == "mesh", "the node names are wrong");
        expect(m.Nodes[1].Parent == 0 && m.Nodes[0].Children.Length == 1, "the hierarchy is wrong");
        expect(m.Nodes[1].PartNumber == 1, $"part number {m.Nodes[1].PartNumber}");
        MeshDef? mesh = m.Nodes[1].Mesh;
        expect(mesh != null, "the trimesh has no mesh");
        if (mesh == null) return;
        expect(mesh.Positions.Length == 3, $"{mesh.Positions.Length} vertices, expected 3");
        expect(Near(mesh.Positions[1], new Vector3(1f, 0f, 0f)), $"vertex 1 is {mesh.Positions[1]}");
        expect(mesh.Normals.Length == 3 && Near(mesh.Normals[0], new Vector3(0f, 0f, 1f)), "the normal stream is wrong");
        expect(mesh.Uv[0].Length == 3 && Near(mesh.Uv[0][2], new Vector2(2f, 4f)), "UV set 0 is wrong");
        expect(mesh.Uv[1].Length == 0, "an absent UV set came back non-empty");
        expect(mesh.Faces.Length == 1 && mesh.Faces[0].Surface == 5, "the face record is wrong");
        expect(mesh.Faces[0].Adj0 == -1 && mesh.Faces[0].Adj1 == -1 && mesh.Faces[0].Adj2 == -1,
               "0xffff did not read back as 'no neighbour'");
        expect(mesh.Indices.Length == 3 && mesh.Indices[2] == 2, "the index list is wrong");
        expect(Near(mesh.SurfaceArea, 0.5f), $"surface area {mesh.SurfaceArea}");
        expect(mesh.Textures[0] == "cx_t", $"texture0 '{mesh.Textures[0]}'");
        expect(MdlBinary.EngineControllerId(Ctl.Position, NodeKind.Dummy) == 0x10, "the controller remap is wrong for position");
        expect(MdlBinary.EngineControllerId(Ctl.Alpha, NodeKind.TriMesh) == 0xa0, "the controller remap is wrong for alpha");
        expect(MdlBinary.EngineControllerId(Ctl.LightColor, NodeKind.Light) == 0x64, "the controller remap is wrong for light colour");
        expect(MdlBinary.EngineControllerId(Ctl.EmSizeMid, NodeKind.Emitter) == 0x230, "the controller remap is wrong for sizeMid");
    }

    /// <summary>The lexer's own contracts: the engine's separator set, the comment rule, sscanf's
    /// longest-prefix and stop-at-first-failure semantics, and the leave-the-target-alone behaviour.</summary>
    private static void Tokens(Action<bool, string> expect)
    {
        byte[] d = Encoding.ASCII.GetBytes("  hello 1 2\n# dropped\n\n\tworld\r\n");
        int at = 0;
        expect(MdlTok.Text(MdlTok.First(MdlTok.NextLine(d, ref at))) == "hello", "the first line's first word is wrong");
        expect(MdlTok.NextLine(d, ref at).Length == 0, "a # comment line was not dropped");
        expect(MdlTok.NextLine(d, ref at).Length == 0, "a blank line was not dropped");
        expect(MdlTok.Text(MdlTok.NextLine(d, ref at)) == "world", "a tab-indented line did not trim");
        expect(at == d.Length, "the cursor did not reach the end");

        expect(Near(MdlTok.Flt("x"u8, 42f), 42f), "a malformed float did not keep the old value");
        expect(Near(MdlTok.Flt("1.5abc"u8, 9f), 1.5f), "sscanf did not take the longest valid float prefix");
        expect(Near(MdlTok.Flt("1.#INF"u8, 9f), 1f), "1.#INF did not read as 1 and stop at the '#'");
        expect(Near(MdlTok.Flt("1e"u8, 9f), 1f), "an incomplete exponent was consumed");
        expect(Near(MdlTok.Flt("2e2"u8, 9f), 200f), "a complete exponent was not consumed");
        expect(MdlTok.Int("3.7"u8, 9) == 3, "%d did not stop at the point");
        expect(MdlTok.Int("tRuE"u8, 9) == 1, "Parse(int&) did not accept TRUE");
        expect(MdlTok.Int("maybe"u8, 9) == 9, "Parse(int&) accepted a word it should not");
        expect(MdlTok.Bool("yes"u8, true) && !MdlTok.Bool("yes"u8, false),
               "Parse(bool&) accepted a word the engine does not");
        expect(MdlTok.Words(" a  b\tc "u8) == 3, "the word count is wrong");

        // sscanf stops dead at the first failure, so a later well-formed field is NOT read.
        Vector3 v = MdlTok.Vec3("1 x 3"u8, new Vector3(7f, 8f, 9f));
        expect(Near(v, new Vector3(1f, 8f, 9f)), $"a stopped sscanf read past its failure: {v}");
        // A NUL ends the line for every scan: the engine's buffers are C strings, and a scanner that did not
        // stop there spun forever on a hostile file.
        expect(MdlTok.Words("a\0 b c"u8) == 1, "a NUL did not end the line for the word count");
        expect(MdlTok.Word("a\0b"u8, 1).Length == 0, "a NUL did not end the line for the word scan");

        expect(Near(MdlPost.Normalize(new Vector3(1.000001f, 0f, 0f)).X, 1.000001f),
               "Normalize touched a length already within its tolerance");
        expect(Near(MdlPost.Normalize(Vector3.Zero), new Vector3(1f, 0f, 0f)),
               "Normalize did not turn the zero vector into (1,0,0)");
    }

    /// <summary>A mesh whose tangent frame only becomes derivable once its <c>.mtr</c> is read: no hint means
    /// no frame, and the hint arriving later must build one -- splitting the two vertices whose triangles
    /// disagree on handedness and taking the skin and dangly arrays with them. Oracles: the frame and the
    /// split are worked out by hand off the fixture's UV gradient, and the per-vertex arrays are literals.</summary>
    private static void LateTangents(Action<bool, string> expect)
    {
        Func<string, int>? saved = MdlParser.MaterialHint;
        try
        {
            MeshDef md = Wedge();
            var skin = new SkinDef
            {
                Weights = new[] { new Vector4(1f, 0f, 0f, 0f), new Vector4(0.5f, 0.5f, 0f, 0f),
                                  new Vector4(0.25f, 0.75f, 0f, 0f), new Vector4(0f, 1f, 0f, 0f) },
                Bones = new[] { 10u, 11u, 12u, 13u },
                BoneIndices = new ushort[16],
            };
            for (int i = 0; i < 16; i++) skin.BoneIndices[i] = (ushort)(i / 4 * 10 + i % 4);
            var dangly = new DanglyDef { Constraints = new[] { 0.1f, 0.2f, 0.3f, 0.4f } };
            var node = new NodeDef { Kind = NodeKind.Skin, Mesh = md, Skin = skin, Dangly = dangly };

            // the parse worker's state: the .mtr is not cached, so the hint is 0 and the pass must decline
            MdlParser.MaterialHint = null;
            MdlPost.Tangents(md, node);
            expect(md.Tangents.Length == 0, $"a mesh with no material hint got {md.Tangents.Length} tangents");
            expect(md.Positions.Length == 4, "the declining pass changed the vertex count");

            // ...and the read lands: normalandspecmapped
            MdlParser.MaterialHint = _ => 2;
            MdlPost.Tangents(md, node);
            expect(md.Tangents.Length == 6 && md.Positions.Length == 6,
                   $"the late pass produced {md.Positions.Length} vertices / {md.Tangents.Length} tangents, expected 6");
            if (md.Tangents.Length != 6) return;
            expect(Near(md.Tangents[0], new Vector4(1f, 0f, 0f, 1f)), $"tangent 0 is {md.Tangents[0]}, expected +x handed +1");
            expect(Near(md.Tangents[3], new Vector4(0f, 1f, 0f, -1f)), $"tangent 3 is {md.Tangents[3]}, expected +y handed -1");
            expect(Near(md.Tangents[4], new Vector4(0f, 1f, 0f, -1f)), $"the split copy of vertex 0 is {md.Tangents[4]}");
            expect(Near(md.Tangents[5], new Vector4(0f, 1f, 0f, -1f)), $"the split copy of vertex 2 is {md.Tangents[5]}");
            // the split corners are re-indexed, and the untouched ones are not
            expect(md.Indices.Length == 6 && md.Indices[0] == 0 && md.Indices[1] == 1 && md.Indices[2] == 2
                   && md.Indices[3] == 4 && md.Indices[4] == 5 && md.Indices[5] == 3,
                   "the second triangle was not re-indexed onto the split vertices");
            bool flat = md.Normals.Length == 6;
            foreach (Vector3 nn in md.Normals) flat &= Near(nn, new Vector3(0f, 0f, 1f));
            expect(flat, "the normals did not survive the split as six +Z");
            expect(Near(md.Uv[0][4], Vector2.Zero) && Near(md.Uv[0][5], new Vector2(1f, 1f)),
                   "the split vertices did not inherit their source UVs");

            expect(skin.Weights.Length == 6 && Near(skin.Weights[0], new Vector4(1f, 0f, 0f, 0f))
                   && Near(skin.Weights[2], new Vector4(0.25f, 0.75f, 0f, 0f)),
                   "the skin weights of the unsplit vertices changed");
            expect(skin.Weights.Length == 6 && Near(skin.Weights[4], new Vector4(1f, 0f, 0f, 0f))
                   && Near(skin.Weights[5], new Vector4(0.25f, 0.75f, 0f, 0f)),
                   "the split vertices did not inherit their source weights");
            expect(skin.Bones.Length == 6 && skin.Bones[4] == 10u && skin.Bones[5] == 12u,
                   "the split vertices did not inherit their source bone slots");
            expect(skin.BoneIndices.Length == 24 && skin.BoneIndices[16] == 0 && skin.BoneIndices[19] == 3
                   && skin.BoneIndices[20] == 20 && skin.BoneIndices[23] == 23,
                   "the four-wide bone indices did not follow the split");
            expect(dangly.Constraints.Length == 6 && Near(dangly.Constraints[4], 0.1f) && Near(dangly.Constraints[5], 0.3f),
                   "the dangly constraints did not follow the split");

            // An ANIMMESH never splits: its accumulator overwrites rather than accumulating, so its
            // per-frame arrays stay indexed by the vertices the weld produced.
            MeshDef am = Wedge();
            var amNode = new NodeDef { Kind = NodeKind.AnimMesh, Mesh = am, AnimMesh = new AnimMeshDef() };
            MdlPost.Tangents(am, amNode);
            expect(am.Positions.Length == 4 && am.Tangents.Length == 4,
                   $"an animmesh split into {am.Positions.Length} vertices -- its per-frame arrays would be stale");
        }
        finally { MdlParser.MaterialHint = saved; }
    }

    /// <summary>The welded fixture the late pass runs on: a quad as two triangles whose UV gradients have
    /// OPPOSITE determinant, so the two vertices they share must be split on handedness.</summary>
    private static MeshDef Wedge()
    {
        var m = new MeshDef
        {
            Positions = new[] { Vector3.Zero, new Vector3(1f, 0f, 0f), new Vector3(1f, 1f, 0f), new Vector3(0f, 1f, 0f) },
            Normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
            Indices = new uint[] { 0, 1, 2, 0, 2, 3 },
            MaterialName = "cx_mat",
        };
        m.Uv[0] = new[] { Vector2.Zero, Vector2.UnitX, Vector2.One, new Vector2(2f, 0f) };
        return m;
    }

    /// <summary>Hostile input. Models arrive from a server's haks and NWSync, so a malformed one must come
    /// back as a refusal and nothing else: never a throw, never a hang, never an allocation past a bound.
    /// <para>Every case here was a reproduced crash or a 23 GiB allocation before it was fixed.</para></summary>
    private static void Fuzz(Action<bool, string> expect)
    {
        // 1 a NUL inside an aabb line: the word scanner used to spin on it forever
        Case(expect, "nul in an aabb line", Ascii("newmodel f\nbeginmodelgeom f\n node aabb w\n  parent NULL\n  aabb 0 0 0 0 0 0 \0\nendnode\nendmodelgeom\n"));
        // 2 a self-referencing child offset, and a node reachable twice: a stack overflow and an exponential walk
        Case(expect, "a node that is its own child", SelfChild());
        Case(expect, "a node reachable twice", DoubleChild());
        // 3 an aabb body of bare zeroes: one recursion per line
        Case(expect, "a deep aabb tree", Ascii("newmodel f\nbeginmodelgeom f\n node aabb w\n  parent NULL\n  aabb " + Repeat("0 0 0 0 0 0 -1\n", 4000) + "endnode\nendmodelgeom\n"));
        // 4 a parent chain thousands deep: one recursion per link in the pre-order flatten
        Case(expect, "a deep parent chain", Chain(6000));
        // 5 a declared count the file cannot possibly supply
        Case(expect, "tverts 2000000000", Ascii("newmodel f\nbeginmodelgeom f\n node trimesh m\n  parent NULL\n  tverts 2000000000\nendnode\nendmodelgeom\n"));
        Case(expect, "faces 1000000000", Ascii("newmodel f\nbeginmodelgeom f\n node trimesh m\n  parent NULL\n  faces 1000000000\nendnode\nendmodelgeom\n"));
        Case(expect, "weights 2000000000", Ascii("newmodel f\nbeginmodelgeom f\n node skin s\n  parent NULL\n  weights 2000000000\nendnode\nendmodelgeom\n"));
        // 6 counts whose stride overflows an int
        Case(expect, "a binary model with overflowing counts", Overflow());
        // truncations and bit flips of both valid fixtures
        byte[] bin = BinaryFixture();
        byte[] asc = Encoding.ASCII.GetBytes(Model.Replace("\r\n", "\n"));
        var rng = new Random(20261001);
        for (int i = 0; i < 400; i++)
        {
            byte[] src = (i & 1) == 0 ? bin : asc;
            byte[] m = src[..(1 + rng.Next(src.Length))];
            for (int k = rng.Next(4); k > 0; k--) m[rng.Next(m.Length)] ^= (byte)(1 << rng.Next(8));
            Case(expect, $"mutation {i}", m, quiet: true);
        }
    }

    /// <summary>Bytes a hostile file may not cost us: one parse must stay inside this much allocation and
    /// this much time, or it is a denial of service however correct its answer.</summary>
    private const long MaxBytes = 192L << 20;
    private const int MaxMs = 4000;

    /// <summary>Hostile cases run, so a suite that silently stopped exercising them is visible.</summary>
    internal static int FuzzCases;

    private static void Case(Action<bool, string> expect, string what, byte[] data, bool quiet = false)
    {
        FuzzCases++;
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            MdlParser.Parse(data, "fuzz", out _);
        }
        catch (Exception e)
        {
            expect(false, $"hostile input '{what}' threw {e.GetType().Name}: {e.Message}");
            return;
        }
        long used = GC.GetAllocatedBytesForCurrentThread() - before;
        if (sw.ElapsedMilliseconds > MaxMs) expect(false, $"hostile input '{what}' took {sw.ElapsedMilliseconds} ms");
        if (used > MaxBytes) expect(false, $"hostile input '{what}' allocated {used >> 20} MiB");
        if (quiet) { }
    }

    private static string Repeat(string s, int n)
    {
        var sb = new StringBuilder(s.Length * n);
        for (int i = 0; i < n; i++) sb.Append(s);
        return sb.ToString();
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Chain(int depth)
    {
        var sb = new StringBuilder("newmodel f\nbeginmodelgeom f\n node dummy n0\n  parent NULL\n endnode\n");
        for (int i = 1; i < depth; i++) sb.Append($" node dummy n{i}\n  parent n{i - 1}\n endnode\n");
        sb.Append("endmodelgeom\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>A binary model whose only node lists itself as its own child.</summary>
    private static byte[] SelfChild() => Hostile((model, rootAt, kidsAt) =>
    {
        I32(model, rootAt + 0x48, kidsAt);
        I32(model, rootAt + 0x4c, 1);
        I32(model, kidsAt, rootAt);
    });

    /// <summary>A binary model whose node lists the same child twice, forty times over.</summary>
    private static byte[] DoubleChild() => Hostile((model, rootAt, kidsAt) =>
    {
        I32(model, rootAt + 0x48, kidsAt);
        I32(model, rootAt + 0x4c, 2);
        I32(model, kidsAt, rootAt + NHeader);
        I32(model, kidsAt + 4, rootAt + NHeader);
        int child = rootAt + NHeader;
        I32(model, child + 0x6c, 1);
        I32(model, child + 0x48, kidsAt);
        I32(model, child + 0x4c, 2);
    });

    /// <summary>A binary model whose counts multiply past an int.</summary>
    private static byte[] Overflow() => Hostile((model, rootAt, kidsAt) =>
    {
        I32(model, rootAt + 0x6c, 0x21);                       // a trimesh
        I32(model, rootAt + 0x78, 0x40);                       // faces at a valid offset
        I32(model, rootAt + 0x7c, 0x20000000);                 // x 0x20 bytes = 16 GiB
        I32(model, rootAt + 0x54, 0x40);                       // controllers
        I32(model, rootAt + 0x58, 0x30000000);                 // x 0xc bytes
        I32(model, rootAt + 0x60, 0x40);
        I32(model, rootAt + 0x64, 0x40000000);                 // x 4 bytes of controller data
        I32(model, 0x78, 0x40);                                // the animation offset array
        I32(model, 0x7c, 0x40000000);
    });

    private static byte[] Hostile(Action<byte[], int, int> poke)
    {
        int rootAt = MHeader, kidsAt = rootAt + NHeader * 2;
        var model = new byte[kidsAt + 64];
        Name(model, 0x08, "cx_hostile");
        I32(model, 0x48, rootAt);
        I32(model, 0x4c, 2);
        model[0x6c] = 2;
        Name(model, 0xa8, "null");
        I32(model, rootAt + 0x6c, 1);
        Name(model, rootAt + 0x20, "root");
        poke(model, rootAt, kidsAt);
        var file = new byte[12 + model.Length];
        I32(file, 0, 0);
        I32(file, 4, model.Length);
        I32(file, 8, 0);
        model.CopyTo(file, 12);
        return file;
    }

    // ---- byte writers ----

    private static void I32(byte[] b, int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), v);
    private static void U32(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
    private static void U16(byte[] b, int at, int v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), (ushort)v);
    private static void F32(byte[] b, int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at), v);
    private static void Vec3(byte[] b, int at, Vector3 v) { F32(b, at, v.X); F32(b, at + 4, v.Y); F32(b, at + 8, v.Z); }
    private static void Name(byte[] b, int at, string s) { for (int i = 0; i < s.Length; i++) b[at + i] = (byte)s[i]; }

    private static bool Near(float a, float b) => MathF.Abs(a - b) < 1e-5f;
    private static bool Near(Vector3 a, Vector3 b) => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);
    private static bool Near(Vector2 a, Vector2 b) => Near(a.X, b.X) && Near(a.Y, b.Y);
    private static bool Near(Vector4 a, Vector4 b) => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z) && Near(a.W, b.W);
}
