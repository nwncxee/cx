using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Engine;
using CX.Framework;
using CX.Services.Camera;

namespace CX.Alloy;

internal static unsafe partial class OccludeApprovedExamples
{
    /// <summary>One synthetic area built through <see cref="HitTestCheck.Fixture"/> into the real tables, its
    /// objects' parts, and the per-part fade state the route keeps, driven through <see cref="OccludeFan"/> frame by
    /// frame. <see cref="Tear"/> frees every record it made, so a run leaves the database as it found it.</summary>
    private sealed class Rig
    {
        public readonly DbId Scene = HitTestCheck.Fixture.NewScene();
        public readonly OccludeHits Hits = new();
        public DbId Character;
        public Vector3 Focus, Cam;
        private readonly List<int> _partList = new();
        private readonly List<bool> _litList = new();
        private readonly List<nint> _shells = new();
        private int[] _parts = Array.Empty<int>();
        private bool[] _lit = Array.Empty<bool>();
        private float[] _fade = Array.Empty<float>(), _held = Array.Empty<float>(), _peak = Array.Empty<float>();

        /// <summary>One object at the world origin whose parts are <paramref name="meshes"/>, in world space, each
        /// drawn lit; returns the first part's index, the rest following it.</summary>
        public int Obj(string name, byte cls, bool isStatic, ObjFlag flags, params MeshDef[] meshes)
        {
            DbId o = HitTestCheck.Fixture.Obj(Scene, name, cls, isStatic, flags, meshes);
            if (o.IsNull) return -1;
            int first = SceneDb.ObjPartFirst[o.Index];
            for (int k = 0; k < meshes.Length; k++) { _partList.Add(first + k); _litList.Add(true); }
            _parts = _partList.ToArray();
            _lit = _litList.ToArray();
            _fade = new float[_parts.Length];
            _held = new float[_parts.Length];
            _peak = new float[_parts.Length];
            return first;
        }

        /// <summary>Draw part <paramref name="part"/> unlit, as a part whose recipe lacks the lit flag is.</summary>
        public void Unlit(int part)
        {
            int k = Array.IndexOf(_parts, part);
            if (k >= 0) _lit[k] = false;
        }

        /// <summary>A dynamic placeable: an object whose facade shell says it is one, which is all the fan's
        /// placeable test reads.</summary>
        public int Placeable(string name, MeshDef mesh)
        {
            int first = Obj(name, 0, false, ObjFlag.None, mesh);
            nint shell = (nint)NativeMemory.AllocZeroed((nuint)(EngineLayout.GobExternalType + sizeof(int)));
            _shells.Add(shell);
            new Gob(shell).ExternalType = EngineLayout.GobKindPlaceable;
            SceneDb.ObjFacade[SceneDb.PartObj[first].Index] = shell;
            return first;
        }

        /// <summary>The followed character standing at <paramref name="feet"/>: the cast's ignore entry.</summary>
        public void Player(Vector3 feet)
        {
            int p = Obj("pc", HitTest.ClassCharacter, false, ObjFlag.Creature,
                        Box(feet + new Vector3(0f, 0f, 0.9f), new Vector3(0.3f, 0.3f, 0.9f)));
            Character = SceneDb.PartObj[p];
        }

        /// <summary><paramref name="n"/> frames of <paramref name="dt"/> seconds with the camera at
        /// <paramref name="cam"/> looking at <paramref name="focus"/>: the fan, then every part's verdict.</summary>
        public void Frames(int n, Vector3 focus, Vector3 cam, float dt = Frame)
        {
            Focus = focus; Cam = cam;
            for (int f = 0; f < n; f++)
            {
                OccludeFan.Cast(Scene, focus, cam, Character, Hits);
                for (int k = 0; k < _parts.Length; k++)
                    _peak[k] = MathF.Max(_peak[k], OccludeFan.Verdict(ref _fade[k], ref _held[k], _parts[k], _lit[k], Hits, dt));
            }
        }

        /// <summary>Part <paramref name="part"/>'s occluder verdict after the last frame.</summary>
        public float Occ(int part)
        {
            int k = Array.IndexOf(_parts, part);
            return k < 0 ? 0f : _fade[k];
        }

        /// <summary>Part <paramref name="part"/>'s largest occluder verdict over every frame so far: 0 says it was
        /// never chosen to fade.</summary>
        public float Peak(int part)
        {
            int k = Array.IndexOf(_parts, part);
            return k < 0 ? 0f : _peak[k];
        }

        /// <summary>The opacity part <paramref name="part"/> draws with: its tile fade, its keyhole state and its
        /// occluder verdict, folded as the route folds them.</summary>
        public float Drawn(int part)
            => OccludeFan.Fold(1f, SceneDb.PartFade[part], Occ(part), SceneDb.PartDissolve[part] < 1f);

        /// <summary>Free the fixture's records, then the facade shells only the rig allocates.</summary>
        public void Tear()
        {
            HitTestCheck.Fixture.Tear();
            foreach (nint s in _shells) NativeMemory.Free((void*)s);
            _shells.Clear();
        }
    }

    /// <summary>Collects world-space quads and boxes into one mesh -- one drawn part, as a tile's merged bucket
    /// is -- each face wound so its plane normal points the way the caller names.</summary>
    private sealed class MeshBuilder
    {
        private readonly List<Vector3> _pos = new();
        private readonly List<FaceDef> _faces = new();

        /// <summary>A quad a-b-c-d; its normal is (b - a) x (c - a).</summary>
        public MeshBuilder Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int at = _pos.Count;
            _pos.Add(a); _pos.Add(b); _pos.Add(c); _pos.Add(d);
            Vector3 n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            float dist = -Vector3.Dot(n, a);
            _faces.Add(new FaceDef { V0 = at, V1 = at + 1, V2 = at + 2, Adj0 = -1, Adj1 = -1, Adj2 = -1,
                                     PlaneNormal = n, PlaneDistance = dist });
            _faces.Add(new FaceDef { V0 = at, V1 = at + 2, V2 = at + 3, Adj0 = -1, Adj1 = -1, Adj2 = -1,
                                     PlaneNormal = n, PlaneDistance = dist });
            return this;
        }

        /// <summary>A horizontal quad at height <paramref name="z"/> over [x0,x1] x [y0,y1], facing +z or -z.</summary>
        public MeshBuilder Flat(float x0, float x1, float y0, float y1, float z, bool up)
            => up ? Quad(new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z))
                  : Quad(new(x0, y0, z), new(x0, y1, z), new(x1, y1, z), new(x1, y0, z));

        /// <summary>An axis-aligned box, every face pointing out.</summary>
        public MeshBuilder Box(Vector3 c, Vector3 h)
        {
            Vector3 lo = c - h, hi = c + h;
            Flat(lo.X, hi.X, lo.Y, hi.Y, hi.Z, up: true);
            Flat(lo.X, hi.X, lo.Y, hi.Y, lo.Z, up: false);
            Quad(new(lo.X, hi.Y, lo.Z), new(lo.X, hi.Y, hi.Z), new(hi.X, hi.Y, hi.Z), new(hi.X, hi.Y, lo.Z));
            Quad(new(lo.X, lo.Y, lo.Z), new(hi.X, lo.Y, lo.Z), new(hi.X, lo.Y, hi.Z), new(lo.X, lo.Y, hi.Z));
            Quad(new(hi.X, lo.Y, lo.Z), new(hi.X, hi.Y, lo.Z), new(hi.X, hi.Y, hi.Z), new(hi.X, lo.Y, hi.Z));
            Quad(new(lo.X, lo.Y, lo.Z), new(lo.X, lo.Y, hi.Z), new(lo.X, hi.Y, hi.Z), new(lo.X, hi.Y, lo.Z));
            return this;
        }

        public MeshDef Build()
        {
            var m = new MeshDef { Positions = _pos.ToArray(), Faces = _faces.ToArray() };
            Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
            foreach (Vector3 v in m.Positions) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
            m.BMin = lo - new Vector3(0.01f); m.BMax = hi + new Vector3(0.01f);
            return m;
        }
    }

    private static MeshDef Box(Vector3 c, Vector3 h) => new MeshBuilder().Box(c, h).Build();
}
