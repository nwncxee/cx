using System;
using System.Buffers.Binary;
using System.Numerics;

namespace CX.Alloy.Mdl;

/// <summary>The per-node-type half of the binary reader. Every offset below is the FILE offset inside the node
/// block, read off the engine's loader @0x5bd400 / @0x5b9290 and cross-checked against its writer, which pins
/// the sizes and tells uninitialised padding from data. A compiled binary mesh is ALREADY post-processed --
/// welded streams, face records with adjacency and planes -- which is what the ASCII post-process builds.</summary>
internal static class MdlBinaryPayload
{
    private const uint None = 0xFFFFFFFF;   // not-an-offset: the file format's "no such array" marker

    /// <summary>The walk's refusal counter. Set per node by <see cref="Read"/>; the reader is single-threaded
    /// per model, and each model gets its own context.</summary>
    [ThreadStatic] private static MdlBinary.Ctx? _ctx;

    /// <summary>Count a bound the file declared and could not supply, and answer false so the caller drops
    /// the array rather than reading past the block.</summary>
    private static bool Fits(long at, long count, long stride, long len)
    {
        if (count <= 0) return false;
        if (at < 0 || at + count * stride > len) { if (_ctx != null) _ctx.Clamped++; return false; }
        return true;
    }   // not-an-offset: a field of the MDL FILE FORMAT, not of an engine object

    // ---- trimesh (and every mesh subclass) ----
    private const int TFaces = 0x78, TFaceCount = 0x7c, TBMin = 0x84, TBMax = 0x90, TRadius = 0x9c,   // not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
                      TAverage = 0xa0, TDiffuse = 0xac, TAmbient = 0xb8, TSpecular = 0xc4, TShininess = 0xd0,
                      TShadow = 0xd4, TBeaming = 0xd8, TRender = 0xdc, TTransHint = 0xe0, TRenderHint = 0xe4,
                      TTexture0 = 0xe8, TTexture1 = 0x128, TTexture2 = 0x168, TMaterialName = 0x1a8,
                      TTileFade = 0x1e8, TVertIndices = 0x1ec, TVertIndicesCount = 0x1f0,
                      TLeftover = 0x1f8, TLeftoverCount = 0x1fc, TVertIndexCounts = 0x204,
                      TVertIndexCountsN = 0x208, TGroups = 0x210, TGroupCount = 0x214,
                      TIndexBuf = 0x21c, TIndexCount = 0x220, TPrimType = 0x224,
                      TPositions = 0x22c, TVertexCount = 0x230, TUvSets = 0x232,
                      TUv0 = 0x234, TUv1 = 0x238, TUv2 = 0x23c, TUv3 = 0x240,
                      TNormals = 0x244, TColors = 0x248, TTangents = 0x258, THandedness = 0x260,
                      TRotateTexture = 0x265, TSurfaceArea = 0x268;
    /// <summary>The <c>MaxFace</c> record: plane, surface id, three 16-bit neighbours and three 16-bit
    /// welded vertex indices.</summary>
    private const int FaceBytes = 0x20, FNormal = 0, FPlaneD = 0xc, FSurface = 0x10, FAdj = 0x14, FVert = 0x1a;   // not-an-offset: a field of the MDL FILE FORMAT, not of an engine object

    // ---- skin ----
    private const int SWeights = 0x27c, SBoneIdx = 0x280, SBoneMap = 0x284, SBoneMapCount = 0x288,   // not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
                      SQBone = 0x28c, SQBoneCount = 0x290, STBone = 0x298, STBoneCount = 0x29c,
                      SConstIdx = 0x2a4, SConstIdxCount = 0x2a8;
    // ---- animmesh ----
    private const int ASamplePeriod = 0x270, AVerts = 0x274, AVertsCount = 0x278, ATVerts = 0x280,   // not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
                      ATVertsCount = 0x284, AFlatVerts = 0x298, AFlatTVerts = 0x29c,
                      AVertFrames = 0x2a0, ATVertFrames = 0x2a4;
    // ---- dangly ----
    private const int DConstraints = 0x270, DConstraintCount = 0x274, DDisplacement = 0x27c,   // not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
                      DTightness = 0x280, DPeriod = 0x284;
    // ---- aabb ----
    private const int BRoot = 0x270;   // not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
    private const int AabbBytes = 0x28, AbMin = 0, AbMax = 0xc, AbLeft = 0x18, AbRight = 0x1c,   // not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
                      AbLeaf = 0x20, AbPlane = 0x24;
    // ---- light ----
    private const int LFlareRadius = 0x70, LFlareSizes = 0x80, LFlareSizesN = 0x84, LFlarePos = 0x8c,   // not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
                      LFlarePosN = 0x90, LFlareColors = 0x98, LFlareColorsN = 0x9c, LTexNames = 0xa4,
                      LTexNamesN = 0xa8, LPriority = 0xb0, LAmbientOnly = 0xb4, LDynamic = 0xb8,
                      LAffectDynamic = 0xbc, LShadow = 0xc0, LGenerateFlare = 0xc4, LFading = 0xc8;
    // ---- emitter ----
    private const int EDeadSpace = 0x70, EBlastRadius = 0x74, EBlastLength = 0x78, EXGrid = 0x7c,   // not-an-offset: a field of the MDL FILE FORMAT, not of an engine object
                      EYGrid = 0x80, ESpawnType = 0x84, EUpdate = 0x88, ERender = 0xa8, EBlend = 0xc8,
                      ETexture = 0xe8, EChunkName = 0x128, ETwoSided = 0x138, ELoop = 0x13c,
                      ERenderOrder = 0x140, EFlags = 0x144;
    // ---- reference ----
    private const int RefModel = 0x70, RefReattachable = 0xb0;   // not-an-offset: a field of the MDL FILE FORMAT, not of an engine object

    /// <summary>Read the node-type payload at <paramref name="at"/> into <paramref name="n"/>.</summary>
    internal static void Read(ReadOnlySpan<byte> model, ReadOnlySpan<byte> raw, int at, uint type, NodeDef n,
                              MdlBinary.Ctx ctx)
    {
        _ctx = ctx;
        // The engine's tests are `(type & mask) == mask`, so a trigger (0x421) passes the mesh test too --
        // but a trigger block is 0x70 bytes with nothing after the header, so it must be excluded by value.
        bool isMesh = type != 0x421 && (type & 0x21) == 0x21;
        if (isMesh) Mesh(model, raw, at, n);
        if ((type & 0x61) == 0x61) Skin(model, raw, at, n);
        if ((type & 0xa1) == 0xa1) AnimMesh(model, at, n);
        if ((type & 0x121) == 0x121) Dangly(model, at, n);
        if ((type & 0x221) == 0x221) Aabb(model, at, n);
        if ((type & 3) == 3) Light(model, at, n);
        if ((type & 5) == 5) Emitter(model, at, n);
        if ((type & 0x11) == 0x11) Reference(model, at, n);
    }

    private static void Mesh(ReadOnlySpan<byte> model, ReadOnlySpan<byte> raw, int at, NodeDef n)
    {
        var m = new MeshDef
        {
            BMin = V3(model, at + TBMin),
            BMax = V3(model, at + TBMax),
            Radius = F(model, at + TRadius),
            Average = V3(model, at + TAverage),
            Diffuse = V3(model, at + TDiffuse),
            Ambient = V3(model, at + TAmbient),
            Specular = V3(model, at + TSpecular),
            Shininess = F(model, at + TShininess),
            Shadow = I(model, at + TShadow) != 0,
            Beaming = I(model, at + TBeaming) != 0,
            Render = I(model, at + TRender) != 0,
            TransparencyHint = I(model, at + TTransHint),
            RenderHintId = I(model, at + TRenderHint),
            TileFade = I(model, at + TTileFade),
            RotateTexture = model[at + TRotateTexture] != 0,
            SurfaceArea = F(model, at + TSurfaceArea),
        };
        m.Textures[0] = MdlBinary.Resref(model.Slice(at + TTexture0, 64));
        m.Textures[1] = MdlBinary.Resref(model.Slice(at + TTexture1, 64));
        m.Textures[2] = MdlBinary.Resref(model.Slice(at + TTexture2, 64));
        m.MaterialName = MdlBinary.Resref(model.Slice(at + TMaterialName, 64));
        n.Mesh = m;

        int faceCount = I(model, at + TFaceCount), faceAt = I(model, at + TFaces);
        if (Fits(faceAt, faceCount, FaceBytes, model.Length))
        {
            m.Faces = new FaceDef[faceCount];
            for (int i = 0; i < faceCount; i++)
            {
                int r = faceAt + i * FaceBytes;
                m.Faces[i] = new FaceDef
                {
                    PlaneNormal = V3(model, r + FNormal),
                    PlaneDistance = F(model, r + FPlaneD),
                    Surface = I(model, r + FSurface),
                    Adj0 = S16(model, r + FAdj), Adj1 = S16(model, r + FAdj + 2), Adj2 = S16(model, r + FAdj + 4),
                    V0 = U16(model, r + FVert), V1 = U16(model, r + FVert + 2), V2 = U16(model, r + FVert + 4),
                    SmoothGroup = 0,
                };
            }
        }

        int verts = U16(model, at + TVertexCount);
        int uvSets = U16(model, at + TUvSets);
        if (verts <= 0) return;

        m.Positions = Vec3Stream(raw, U(model, at + TPositions), verts);
        m.Normals = Vec3Stream(raw, U(model, at + TNormals), verts);
        m.Tangents = TangentStream(raw, U(model, at + TTangents), U(model, at + THandedness), verts);
        Span<int> uvAt = [TUv0, TUv1, TUv2, TUv3];
        for (int s = 0; s < 4; s++) m.Uv[s] = Vec2Stream(raw, U(model, at + uvAt[s]), verts);
        uint colAt = U(model, at + TColors);
        if (colAt != None && (long)colAt + (long)verts * 4 <= raw.Length)
        {
            m.Colors = new uint[verts];
            for (int i = 0; i < verts; i++) m.Colors[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw[(int)(colAt + i * 4)..]);
        }

        // The index buffer: the engine's own groups when the file states them, else the faces' own corners,
        // which is what BuildVertexIndices derives.
        m.VertexIndexCounts = IntArray(model, I(model, at + TVertIndexCounts), I(model, at + TVertIndexCountsN));
        m.LeftoverFaces = UintArray(model, I(model, at + TLeftover), I(model, at + TLeftoverCount));
        int groupCount = I(model, at + TGroupCount), groupsAt = I(model, at + TGroups);
        if (groupCount > 0 && Fits(groupsAt, groupCount, 4, model.Length) && m.VertexIndexCounts.Length >= groupCount)
        {
            var groups = new uint[groupCount][];
            for (int g = 0; g < groupCount; g++)
            {
                uint off = U(model, groupsAt + g * 4);
                int cnt = m.VertexIndexCounts[g];
                groups[g] = off == None || cnt <= 0 || (long)off + (long)cnt * 2 > raw.Length
                    ? Array.Empty<uint>()
                    : U16Array(raw, (int)off, cnt);
            }
            m.VertexIndices = groups;
            if (groups.Length > 0) m.Indices = groups[0];
        }
        if (m.Indices.Length == 0)
        {
            uint ib = U(model, at + TIndexBuf);
            int ic = I(model, at + TIndexCount);
            if (ib != None && ic > 0 && (long)ib + (long)ic * 2 <= raw.Length) m.Indices = U16Array(raw, (int)ib, ic);
        }
        if (m.Indices.Length == 0 && m.Faces.Length > 0)
        {
            m.Indices = new uint[m.Faces.Length * 3];
            for (int i = 0; i < m.Faces.Length; i++)
            {
                m.Indices[i * 3] = (uint)m.Faces[i].V0;
                m.Indices[i * 3 + 1] = (uint)m.Faces[i].V1;
                m.Indices[i * 3 + 2] = (uint)m.Faces[i].V2;
            }
        }
        // uvSets is the engine's own count of the streams it uploaded; a set whose offset is absent came
        // back empty above, so there is nothing to reconcile -- the count is carried for the record only.
        m.UvSetCount = uvSets;
    }

    private static void Skin(ReadOnlySpan<byte> model, ReadOnlySpan<byte> raw, int at, NodeDef n)
    {
        int verts = n.Mesh?.Positions.Length ?? 0;
        var s = new SkinDef();
        n.Skin = s;
        uint wAt = U(model, at + SWeights), bAt = U(model, at + SBoneIdx);
        if (verts > 0 && wAt != None && (long)wAt + (long)verts * 16 <= raw.Length)
        {
            s.Weights = new Vector4[verts];
            for (int i = 0; i < verts; i++)
            {
                int r = (int)wAt + i * 16;
                s.Weights[i] = new Vector4(F(raw, r), F(raw, r + 4), F(raw, r + 8), F(raw, r + 12));
            }
        }
        if (verts > 0 && bAt != None && (long)bAt + (long)verts * 8 <= raw.Length)
        {
            s.BoneIndices = new ushort[verts * 4];
            s.Bones = new uint[verts];
            for (int i = 0; i < verts; i++)
            {
                int r = (int)bAt + i * 8;
                uint packed = 0;
                for (int k = 0; k < 4; k++)
                {
                    ushort slot = (ushort)U16(raw, r + k * 2);
                    s.BoneIndices[i * 4 + k] = slot;
                    packed |= (uint)((slot & 0xFF) << (k * 8));
                }
                s.Bones[i] = packed;
            }
        }
        int bm = I(model, at + SBoneMapCount), bmAt = I(model, at + SBoneMap);
        if (Fits(bmAt, bm, 2, model.Length))
        {
            s.BoneNodes = new int[bm];
            s.BoneNodesAreMap = true;
            for (int i = 0; i < bm; i++) s.BoneNodes[i] = S16(model, bmAt + i * 2);
        }
        int qn = I(model, at + SQBoneCount), qAt = I(model, at + SQBone);
        if (Fits(qAt, qn, 16, model.Length))
        {
            s.BindRotation = new Quaternion[qn];
            for (int i = 0; i < qn; i++)
            {
                int r = qAt + i * 16;
                // The engine's Quaternion is {w, x, y, z}; System.Numerics is {x, y, z, w}.
                s.BindRotation[i] = new Quaternion(F(model, r + 4), F(model, r + 8), F(model, r + 12), F(model, r));
            }
        }
        int tn = I(model, at + STBoneCount), tAt = I(model, at + STBone);
        if (Fits(tAt, tn, 12, model.Length))
        {
            s.BindTranslation = new Vector3[tn];
            for (int i = 0; i < tn; i++) s.BindTranslation[i] = V3(model, tAt + i * 12);
        }
        s.BoneConstantIndices = IntArray(model, I(model, at + SConstIdx), I(model, at + SConstIdxCount));
    }

    /// <summary>The two morph pools of an animmesh node: the node's GENERATED pools
    /// (<c>MdlNodeAnimMesh+0x300</c> / <c>+0x308</c>, built by <c>InternalGenVertices</c> @0x555490, read by
    /// <c>InternalControl</c> @0x599320), not the <c>animverts</c>/<c>animtverts</c> lists the ASCII parse
    /// fills. They are VERTEX-major -- every frame of vertex 0, then of vertex 1 -- so the reads stride by FRAMES.</summary>
    private static void AnimMesh(ReadOnlySpan<byte> model, int at, NodeDef n)
    {
        int verts = n.Mesh?.Positions.Length ?? 0;
        var a = new AnimMeshDef { SamplePeriod = F(model, at + ASamplePeriod) };
        n.AnimMesh = a;
        int vf = I(model, at + AVertFrames), tf = I(model, at + ATVertFrames);
        int flatV = I(model, at + AFlatVerts), flatT = I(model, at + AFlatTVerts);
        if (verts > 0 && vf > 0 && flatV > 0 && flatV + (long)verts * (long)vf * 12 <= model.Length)
        {
            a.Positions = new Vector3[vf][];
            for (int f = 0; f < vf; f++) a.Positions[f] = new Vector3[verts];
            for (int i = 0; i < verts; i++)
                for (int f = 0; f < vf; f++)
                    a.Positions[f][i] = V3(model, flatV + (i * vf + f) * 12);
        }
        if (verts > 0 && tf > 0 && flatT > 0 && flatT + (long)verts * (long)tf * 8 <= model.Length)
        {
            a.Uvs = new Vector2[tf][];
            for (int f = 0; f < tf; f++) a.Uvs[f] = new Vector2[verts];
            for (int i = 0; i < verts; i++)
                for (int f = 0; f < tf; f++)
                {
                    int r = flatT + (i * tf + f) * 8;
                    a.Uvs[f][i] = new Vector2(F(model, r), F(model, r + 4));
                }
        }
    }

    private static void Dangly(ReadOnlySpan<byte> model, int at, NodeDef n)
    {
        var d = new DanglyDef
        {
            Displacement = F(model, at + DDisplacement),
            Tightness = F(model, at + DTightness),
            Period = F(model, at + DPeriod),
            Constraints = FloatArray(model, I(model, at + DConstraints), I(model, at + DConstraintCount)),
        };
        n.Dangly = d;
    }

    private static void Aabb(ReadOnlySpan<byte> model, int at, NodeDef n)
    {
        var a = new AabbDef();
        n.Aabb = a;
        // PartAABB::Draw is a debug view, so a walkmesh never draws or casts, whatever its render word says.
        if (n.Mesh != null) { n.Mesh.WalkMesh = true; n.Mesh.Render = false; n.Mesh.Shadow = false; }
        int root = I(model, at + BRoot);
        if (root <= 0) return;
        var list = new System.Collections.Generic.List<AabbNode>();
        AabbRec(model, root, list, 0);
        a.Tree = list.ToArray();
    }

    /// <summary>One AABB record and, pre-order, its children. The depth bound is a self-check against a
    /// cyclic file, not a real tree depth.</summary>
    private static int AabbRec(ReadOnlySpan<byte> model, int at, System.Collections.Generic.List<AabbNode> list, int depth)
    {
        if (at <= 0 || at + AabbBytes > model.Length || depth > 128 || list.Count > (1 << 20)) return -1;
        var node = new AabbNode
        {
            BMin = V3(model, at + AbMin),
            BMax = V3(model, at + AbMax),
            Face = I(model, at + AbLeaf),
            Plane = I(model, at + AbPlane),
            Left = -1,
            Right = -1,
        };
        int me = list.Count;
        list.Add(node);
        int l = I(model, at + AbLeft), r = I(model, at + AbRight);
        if (l > 0) node.Left = AabbRec(model, l, list, depth + 1);
        if (r > 0) node.Right = AabbRec(model, r, list, depth + 1);
        list[me] = node;
        return me;
    }

    private static void Light(ReadOnlySpan<byte> model, int at, NodeDef n)
    {
        var l = new LightDef
        {
            FlareRadius = F(model, at + LFlareRadius),
            Priority = I(model, at + LPriority),
            AmbientOnly = I(model, at + LAmbientOnly) != 0,
            Dynamic = I(model, at + LDynamic) != 0,
            AffectDynamic = I(model, at + LAffectDynamic) != 0,
            Shadow = I(model, at + LShadow) != 0,
            GenerateFlare = I(model, at + LGenerateFlare) != 0,
            Fading = I(model, at + LFading) != 0,
            FlareSizes = FloatArray(model, I(model, at + LFlareSizes), I(model, at + LFlareSizesN)),
            FlarePositions = FloatArray(model, I(model, at + LFlarePos), I(model, at + LFlarePosN)),
        };
        int fc = I(model, at + LFlareColorsN), fcAt = I(model, at + LFlareColors);
        if (Fits(fcAt, fc, 12, model.Length))
        {
            l.FlareColors = new Vector3[fc];
            for (int i = 0; i < fc; i++) l.FlareColors[i] = V3(model, fcAt + i * 12);
        }
        int tn = I(model, at + LTexNamesN), tnAt = I(model, at + LTexNames);
        if (Fits(tnAt, tn, 4, model.Length))
        {
            l.FlareTextures = new string[tn];
            for (int i = 0; i < tn; i++)
            {
                int so = I(model, tnAt + i * 4);
                l.FlareTextures[i] = so > 0 && so < model.Length ? MdlBinary.Resref(model[so..]) : "";
            }
        }
        n.Light = l;
    }

    private static void Emitter(ReadOnlySpan<byte> model, int at, NodeDef n) => n.Emitter = new EmitterDef
    {
        DeadSpace = F(model, at + EDeadSpace),
        BlastRadius = F(model, at + EBlastRadius),
        BlastLength = F(model, at + EBlastLength),
        XGrid = I(model, at + EXGrid),
        YGrid = I(model, at + EYGrid),
        SpawnType = I(model, at + ESpawnType),
        Update = MdlBinary.Resref(model.Slice(at + EUpdate, 32)),
        Render = MdlBinary.Resref(model.Slice(at + ERender, 32)),
        Blend = MdlBinary.Resref(model.Slice(at + EBlend, 32)),
        Texture = MdlBinary.Resref(model.Slice(at + ETexture, 64)),
        ChunkName = MdlBinary.Resref(model.Slice(at + EChunkName, 16)),
        TwoSidedTex = I(model, at + ETwoSided) != 0,
        Loop = I(model, at + ELoop) != 0,
        RenderOrder = S16(model, at + ERenderOrder),
        Flags = U(model, at + EFlags),
    };

    private static void Reference(ReadOnlySpan<byte> model, int at, NodeDef n) => n.Reference = new ReferenceDef
    {
        Model = MdlBinary.Resref(model.Slice(at + RefModel, 64)),
        Reattachable = I(model, at + RefReattachable) != 0,
    };

    // ---- stream helpers ----

    private static Vector3[] Vec3Stream(ReadOnlySpan<byte> raw, uint off, int n)
    {
        if (off == None || n <= 0 || (long)off + (long)n * 12 > raw.Length)
        { if (off != None && n > 0 && _ctx != null) _ctx.Clamped++; return Array.Empty<Vector3>(); }
        var o = new Vector3[n];
        for (int i = 0; i < n; i++) o[i] = V3(raw, (int)off + i * 12);
        return o;
    }

    private static Vector2[] Vec2Stream(ReadOnlySpan<byte> raw, uint off, int n)
    {
        if (off == None || n <= 0 || (long)off + (long)n * 8 > raw.Length)
        { if (off != None && n > 0 && _ctx != null) _ctx.Clamped++; return Array.Empty<Vector2>(); }
        var o = new Vector2[n];
        for (int i = 0; i < n; i++) o[i] = new Vector2(F(raw, (int)off + i * 8), F(raw, (int)off + i * 8 + 4));
        return o;
    }

    /// <summary>The tangent frame is two streams: xyz in one pool and the handedness float in another.</summary>
    private static Vector4[] TangentStream(ReadOnlySpan<byte> raw, uint t, uint h, int n)
    {
        if (t == None || n <= 0 || (long)t + (long)n * 12 > raw.Length) return Array.Empty<Vector4>();
        bool haveH = h != None && (long)h + (long)n * 4 <= raw.Length;
        var o = new Vector4[n];
        for (int i = 0; i < n; i++)
            o[i] = new Vector4(V3(raw, (int)t + i * 12), haveH ? F(raw, (int)h + i * 4) : 1f);
        return o;
    }

    private static int[] IntArray(ReadOnlySpan<byte> s, int at, int n)
    {
        if (!Fits(at, n, 4, s.Length)) return Array.Empty<int>();
        var o = new int[n];
        for (int i = 0; i < n; i++) o[i] = I(s, at + i * 4);
        return o;
    }

    private static uint[] UintArray(ReadOnlySpan<byte> s, int at, int n)
    {
        if (!Fits(at, n, 4, s.Length)) return Array.Empty<uint>();
        var o = new uint[n];
        for (int i = 0; i < n; i++) o[i] = U(s, at + i * 4);
        return o;
    }

    private static float[] FloatArray(ReadOnlySpan<byte> s, int at, int n)
    {
        if (!Fits(at, n, 4, s.Length)) return Array.Empty<float>();
        var o = new float[n];
        for (int i = 0; i < n; i++) o[i] = F(s, at + i * 4);
        return o;
    }

    private static uint[] U16Array(ReadOnlySpan<byte> s, int at, int n)
    {
        var o = new uint[n];
        for (int i = 0; i < n; i++) o[i] = (uint)U16(s, at + i * 2);
        return o;
    }

    private static int I(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadInt32LittleEndian(s[at..]);
    private static uint U(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32LittleEndian(s[at..]);
    private static int U16(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt16LittleEndian(s[at..]);
    private static int S16(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadInt16LittleEndian(s[at..]);
    private static float F(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadSingleLittleEndian(s[at..]);
    private static Vector3 V3(ReadOnlySpan<byte> s, int at) => new(F(s, at), F(s, at + 4), F(s, at + 8));
}
