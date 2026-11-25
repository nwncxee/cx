using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Mdl;

namespace CX.Alloy;

/// <summary>Hand-built creature bodies for the creature-print examples: every foot, paw, hoof and hem stands at a
/// hand-written place relative to the creature, model forward +y, nothing read from the game's models.</summary>
internal static unsafe partial class Weather
{
    /// <summary>A wolf's paws relative to its centre, front-left, front-right, back-left, back-right.</summary>
    private static readonly Vector2[] WolfPaws = [new(-0.15f, 0.45f), new(0.15f, 0.45f), new(-0.15f, -0.45f), new(0.15f, -0.45f)];
    /// <summary>A wolf's paw, across by along, metres.</summary>
    private static readonly Vector2 WolfPaw = new(0.10f, 0.12f);
    /// <summary>An ox's hooves relative to its centre, in <see cref="WolfPaws"/>' order, and one hoof's size.</summary>
    private static readonly Vector2[] OxHooves = [new(-0.25f, 0.8f), new(0.25f, 0.8f), new(-0.25f, -0.8f), new(0.25f, -0.8f)];
    private static readonly Vector2 OxHoof = new(0.12f, 0.14f);
    /// <summary>A planted paw's height and a lifted one's, for the fixtures that walk on level ground.</summary>
    private const float Planted = 0f, Lifted = 0.08f;

    /// <summary>A body under construction: nodes parent first, each placed at its spot relative to the creature.</summary>
    private sealed class FixBody
    {
        public readonly List<NodeDef> Nodes = new();
        private readonly List<Vector3> _at = new();

        public FixBody() => Node("root", -1, Vector3.Zero);

        /// <summary>A node at <paramref name="at"/> relative to the creature; its mesh is in the node's own space.</summary>
        public int Node(string name, int parent, Vector3 at, MeshDef? mesh = null, SkinDef? skin = null)
        {
            var n = new NodeDef
            {
                Name = name, Index = Nodes.Count, Parent = parent, Mesh = mesh, Skin = skin,
                Position = parent < 0 ? at : at - _at[parent],
                Kind = skin != null ? NodeKind.Skin : mesh != null ? NodeKind.TriMesh : NodeKind.Dummy,
            };
            Nodes.Add(n);
            _at.Add(at);
            return n.Index;
        }

        public ModelDef Model(string name)
        {
            var m = new ModelDef { Name = name, AnimScale = 1f, BMin = new Vector3(-6f), BMax = new Vector3(6f), Nodes = Nodes.ToArray() };
            for (int i = 0; i < m.Nodes.Length; i++)
            {
                var kids = new List<int>();
                for (int k = 0; k < m.Nodes.Length; k++) if (m.Nodes[k].Parent == i) kids.Add(k);
                m.Nodes[i].Children = kids.ToArray();
                m.NodeByName[m.Nodes[i].Name] = i;
            }
            return m;
        }
    }

    /// <summary>Boxes (and a hem ring) as one mesh, each vertex bound to the skin slot of the box it belongs to.</summary>
    private sealed class FixMesh
    {
        private readonly List<Vector3> _pos = new();
        private readonly List<uint> _idx = new();
        private readonly List<uint> _slot = new();
        private static readonly uint[] BoxFaces = [0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1,
                                                   2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3];

        /// <summary>A box standing on <paramref name="bottom"/>, <paramref name="across"/> on x by <paramref name="along"/> on y.</summary>
        public FixMesh Box(Vector3 bottom, float across, float along, float tall, int slot = 0)
        {
            uint b = (uint)_pos.Count;
            for (int k = 0; k < 8; k++)
            {
                _pos.Add(bottom + new Vector3((k & 1) != 0 ? across / 2f : -across / 2f, (k & 2) != 0 ? along / 2f : -along / 2f,
                                              (k & 4) != 0 ? tall : 0f));
                _slot.Add((uint)slot);
            }
            foreach (uint f in BoxFaces) _idx.Add(b + f);
            return this;
        }

        /// <summary>An open elliptic tube from <paramref name="bottom"/> up: a hem hanging around the legs.</summary>
        public FixMesh Ring(Vector3 bottom, float halfAcross, float halfAlong, float tall, int segments)
        {
            uint b = (uint)_pos.Count;
            for (int k = 0; k < segments; k++)
            {
                float a = MathF.Tau * k / segments;
                Vector3 rim = bottom + new Vector3(MathF.Cos(a) * halfAcross, MathF.Sin(a) * halfAlong, 0f);
                _pos.Add(rim); _pos.Add(rim + new Vector3(0f, 0f, tall));
                _slot.Add(0); _slot.Add(0);
            }
            for (int k = 0; k < segments; k++)
            {
                uint lo = b + (uint)(2 * k), hi = lo + 1, lo2 = b + (uint)(2 * ((k + 1) % segments)), hi2 = lo2 + 1;
                _idx.Add(lo); _idx.Add(lo2); _idx.Add(hi2); _idx.Add(lo); _idx.Add(hi2); _idx.Add(hi);
            }
            return this;
        }

        public MeshDef Mesh()
        {
            Vector3[] p = _pos.ToArray();
            var n = new Vector3[p.Length];
            Array.Fill(n, Vector3.UnitZ);
            Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
            foreach (Vector3 v in p) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
            return new MeshDef { Positions = p, Normals = n, Indices = _idx.ToArray(), BMin = lo, BMax = hi };
        }

        /// <summary>The skin binding every vertex wholly to its box's slot, slot = node index; <paramref name="bind"/>
        /// is each node's rest place, empty for a skin that binds no bones.</summary>
        public SkinDef Skin(Vector3[] bind)
        {
            var s = new SkinDef { Weights = new Vector4[_pos.Count], Bones = _slot.ToArray() };
            Array.Fill(s.Weights, new Vector4(1f, 0f, 0f, 0f));
            s.BoneNodes = new int[bind.Length];
            s.BindRotation = new Quaternion[bind.Length];
            s.BindTranslation = new Vector3[bind.Length];
            for (int i = 0; i < bind.Length; i++) { s.BoneNodes[i] = i; s.BindRotation[i] = Quaternion.Identity; s.BindTranslation[i] = -bind[i]; }
            return s;
        }
    }

    /// <summary>A four-legged body: a torso on four legs, a pad of <paramref name="pad"/> under each foot at
    /// <paramref name="feet"/>, each foot at its own height in <paramref name="z"/>.</summary>
    private static ModelDef Quadruped(string name, Vector2[] feet, Vector2 pad, float[] z)
    {
        var b = new FixBody();
        int torso = b.Node("torso", 0, new Vector3(0f, 0f, 0.45f), new FixMesh().Box(Vector3.Zero, 0.4f, 2f * MathF.Abs(feet[0].Y) + 0.3f, 0.35f).Mesh());
        string[] corner = ["fl", "fr", "bl", "br"];
        for (int k = 0; k < 4; k++)
        {
            var foot = new Vector3(feet[k].X, feet[k].Y, z[k]);
            int leg = b.Node("leg_" + corner[k], torso, foot + new Vector3(0f, 0f, 0.15f), new FixMesh().Box(Vector3.Zero, 0.06f, 0.06f, 0.3f).Mesh());
            b.Node("foot_" + corner[k], leg, foot, new FixMesh().Box(Vector3.Zero, pad.X, pad.Y, 0.06f).Mesh());
        }
        return b.Model(name);
    }

    /// <summary>The wolf: front-left and back-right paws planted, the other two lifted.</summary>
    private static ModelDef Wolf() => Quadruped("cx_pr_wolf", WolfPaws, WolfPaw, [Planted, Lifted, Lifted, Planted]);

    /// <summary>The wolf on a slope rising ahead: every paw planted, the front ones 0.164 m up, the back ones as far down.</summary>
    private static ModelDef SlopeWolf() => Quadruped("cx_pr_slope", WolfPaws, WolfPaw, [0.164f, 0.164f, -0.164f, -0.164f]);

    /// <summary>A wolf mid-lunge: two forepaws raised in a line rising ahead, one forepaw and one hind paw planted.</summary>
    private static readonly Vector2[] LungePaws = [new(-0.15f, 0.12f), new(0.15f, 0.45f), new(-0.15f, 0.40f), new(0.15f, -0.45f)];

    /// <summary>The lunging wolf: its raised forepaws 0.08 m and 0.3 m up, on one line that the planted forepaw sinks below.</summary>
    private static ModelDef LungeWolf() => Quadruped("cx_pr_lunge", LungePaws, WolfPaw, [Lifted, 0.3f, Planted, Planted]);

    /// <summary>A wolf climbing a 1-in-10 rise ahead, one hind paw drawn in under its centre: only that paw is on the level.</summary>
    private static readonly Vector2[] ClimbPaws = [new(-0.15f, 0.45f), new(0.15f, 0.6f), new(-0.15f, 0f), new(0.15f, 0.3f)];

    /// <summary>The climbing wolf: every paw planted on the rise, each a tenth of its reach ahead above the centre's ground.</summary>
    private static ModelDef ClimbWolf() => Quadruped("cx_pr_climb", ClimbPaws, WolfPaw, [0.045f, 0.06f, Planted, 0.03f]);

    /// <summary>A wolf walking down a 1-in-9 slope: its forepaws just ahead of its centre, its hind paws far behind.</summary>
    private static readonly Vector2[] DescentPaws = [new(-0.15f, 0.04f), new(0.15f, 0.03f), new(-0.15f, -0.48f), new(0.15f, -0.48f)];

    /// <summary>The descending wolf: its back-left paw planted on the slope behind, the back-right one 8 cm above it, both
    /// forepaws 3 cm up.</summary>
    private static ModelDef DescentWolf() => Quadruped("cx_pr_descent", DescentPaws, WolfPaw, [0.03f, 0.032f, 0.053f, 0.133f]);

    /// <summary>The ox: front-left and back-right hooves planted.</summary>
    private static ModelDef Ox() => Quadruped("cx_pr_ox", OxHooves, OxHoof, [Planted, Lifted, Lifted, Planted]);

    /// <summary>A spider's eight leg tips: angle and reach from its centre; the even ones are planted.</summary>
    private static Vector3 SpiderTip(int k)
    {
        float a = MathF.Tau * (k + 0.5f) / 8f, r = (k & 2) != 0 ? 1.6f : 1.2f;
        return new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, (k & 1) == 0 ? Planted : 0.15f);
    }

    /// <summary>The giant spider: a body 0.6 m up, eight legs, a 4 cm tip at the end of each.</summary>
    private static ModelDef Spider()
    {
        var b = new FixBody();
        int body = b.Node("body", 0, new Vector3(0f, 0f, 0.6f), new FixMesh().Box(Vector3.Zero, 0.9f, 0.9f, 0.4f).Mesh());
        for (int k = 0; k < 8; k++)
        {
            Vector3 tip = SpiderTip(k);
            int leg = b.Node($"leg{k}", body, new Vector3(tip.X * 0.6f, tip.Y * 0.6f, 0.5f), new FixMesh().Box(Vector3.Zero, 0.05f, 0.05f, 0.2f).Mesh());
            b.Node($"tip{k}", leg, tip, new FixMesh().Box(Vector3.Zero, 0.04f, 0.04f, 0.04f).Mesh());
        }
        return b.Model("cx_pr_spider");
    }

    /// <summary>A basilisk's four feet, all planted, each 0.14 by 0.18 m.</summary>
    private static readonly Vector2[] BasiliskFeet = [new(-0.3f, 0.55f), new(0.3f, 0.55f), new(-0.3f, -0.55f), new(0.3f, -0.55f)];
    /// <summary>Where a basilisk's calf dips lowest, 3 cm off the ground: 0.25 m behind its foot.</summary>
    private static Vector2 BasiliskCalf(int k) => BasiliskFeet[k] + new Vector2(0f, -0.25f);

    /// <summary>The basilisk on a frame its calves dip: each calf, the foot's parent, reaches 3 cm off the ground.</summary>
    private static ModelDef Basilisk()
    {
        var b = new FixBody();
        int torso = b.Node("torso", 0, new Vector3(0f, 0f, 0.5f), new FixMesh().Box(Vector3.Zero, 0.6f, 1.4f, 0.4f).Mesh());
        for (int k = 0; k < 4; k++)
        {
            Vector2 c = BasiliskCalf(k), f = BasiliskFeet[k];
            int calf = b.Node($"calf{k}", torso, new Vector3(c.X, c.Y, 0.03f), new FixMesh().Box(Vector3.Zero, 0.08f, 0.08f, 0.3f).Mesh());
            b.Node($"foot{k}", calf, new Vector3(f.X, f.Y, Planted), new FixMesh().Box(Vector3.Zero, 0.14f, 0.18f, 0.05f).Mesh());
        }
        return b.Model("cx_pr_basilisk");
    }

    /// <summary>The stock biped's leg nodes in <see cref="Biped"/>'s order: pelvis, then thigh, shin, foot right and left.</summary>
    private static readonly BipedNames StockLegs = new("pelvis_g", new("rthigh_g", "rshin_g", "rfoot_g"), new("lthigh_g", "lshin_g", "lfoot_g"));
    /// <summary>One leg's node names, hip down.</summary>
    private readonly record struct LegNames(string Thigh, string Shin, string Foot);
    /// <summary>A biped's pelvis and leg node names.</summary>
    private readonly record struct BipedNames(string Pelvis, LegNames Right, LegNames Left);
    /// <summary>A sole, across by along, metres.</summary>
    private static readonly Vector2 Sole = new(0.10f, 0.26f);
    /// <summary>Where a robe's hem hangs: an ellipse about the creature's centre, 2 cm off the ground.</summary>
    private const float HemAcross = 0.285f, HemAlong = 0.27f, HemLow = 0.02f;
    /// <summary>A mounted rider's hooves, in <see cref="WolfPaws"/>' order: front left and hind right planted.</summary>
    private static readonly Vector3[] Hooves = [new(-0.2f, 0.7f, Planted), new(0.2f, 0.7f, 0.15f), new(-0.2f, -0.7f, 0.15f), new(0.2f, -0.7f, Planted)];
    /// <summary>The rider body's hoof nodes, in <see cref="Hooves"/>' order.</summary>
    private static readonly string[] HoofNodes = ["lhand_h", "rhand_h", "lfoot_h", "rfoot_h"];

    /// <summary>A biped whose right and left soles stand on <paramref name="right"/> and <paramref name="left"/>,
    /// named <paramref name="legs"/>; with a hem hanging to <see cref="HemLow"/>, with a horse's hoof nodes, with one stray,
    /// or with <paramref name="bareFeet"/>: foot bones with no mesh, as a player's are under the boots hung from them.</summary>
    private static ModelDef Biped(string name, BipedNames legs, Vector3 right, Vector3 left, bool hem = false, bool hooves = false, bool strayHoof = false,
                                  bool bareFeet = false)
    {
        var b = new FixBody();
        float hip = hooves ? 1.6f : 1f;
        int pelvis = b.Node(legs.Pelvis, 0, new Vector3(0f, 0f, hip), new FixMesh().Box(Vector3.Zero, 0.35f, 0.2f, 0.3f).Mesh());
        b.Node("torso_g", pelvis, new Vector3(0f, 0f, hip + 0.3f), new FixMesh().Box(Vector3.Zero, 0.4f, 0.25f, 0.5f).Mesh());
        (LegNames Names, Vector3 At)[] sides = [(legs.Right, right), (legs.Left, left)];
        foreach ((LegNames leg, Vector3 f) in sides)
        {
            int thigh = b.Node(leg.Thigh, pelvis, new Vector3(f.X, f.Y * 0.5f, hip), new FixMesh().Box(new Vector3(0f, 0f, -0.4f), 0.12f, 0.12f, 0.4f).Mesh());
            int shin = b.Node(leg.Shin, thigh, f + new Vector3(0f, 0f, 0.5f), new FixMesh().Box(new Vector3(0f, 0f, -0.42f), 0.1f, 0.1f, 0.42f).Mesh());
            b.Node(leg.Foot, shin, f, bareFeet ? null : new FixMesh().Box(Vector3.Zero, Sole.X, Sole.Y, 0.08f).Mesh());
        }
        if (hem) b.Node("robe_g", pelvis, new Vector3(0f, 0f, HemLow), new FixMesh().Ring(Vector3.Zero, HemAcross, HemAlong, 0.6f, 16).Mesh());
        if (hooves)
            for (int k = 0; k < 4; k++) b.Node(HoofNodes[k], 0, Hooves[k]);
        if (strayHoof) b.Node(HoofNodes[0], 0, Hooves[0]);
        return b.Model(name);
    }

    /// <summary>A boot: one sole-sized mesh standing on its own origin, a body part model hung from a foot bone.</summary>
    private static ModelDef Boot()
    {
        var b = new FixBody();
        b.Node("boot", 0, Vector3.Zero, new FixMesh().Box(Vector3.Zero, Sole.X, Sole.Y, 0.08f).Mesh());
        return b.Model("cx_pr_boot");
    }

    /// <summary>Hangs a <see cref="Boot"/> from <paramref name="body"/>'s part named <paramref name="node"/>, placed on that
    /// foot at <paramref name="at"/> in the world, as the engine composes a body part onto its node; default when it fails.</summary>
    private static DbId HangBoot(DbId body, string node, Vector3 at)
    {
        if (body.IsNull) return default;
        int foot = -1, first = SceneDb.ObjPartFirst[body.Index];
        for (int p = first; p < first + SceneDb.ObjPartCount[body.Index]; p++)
            if (SceneDb.PartModel[p] is { } m && m.Nodes[SceneDb.PartNode[p]].Name == node) foot = p;
        DbId boot = foot < 0 ? default : Place(Boot(), at);
        if (boot.IsNull) return boot;
        if (!SceneDb.PieceLink(body, boot)) { Unplace(boot); return default; }
        SceneDb.SetAttachTo(boot.Index, body);
        SceneDb.SetAttachPart(boot.Index, foot);
        return boot;
    }

    /// <summary>The plain biped's planted left sole and its lifted right one, relative to its centre.</summary>
    private static readonly Vector3 LeftDown = new(-0.1f, -0.2f, 0f), RightUp = new(0.1f, 0.25f, 0.12f);

    /// <summary>A body with no foot geometry at all: two dummies, no mesh.</summary>
    private static ModelDef Bare()
    {
        var b = new FixBody();
        b.Node("torso", 0, new Vector3(0f, 0f, 1f));
        return b.Model("cx_pr_bare");
    }

    /// <summary>A red dragon's nodes: root, skin, spine, paws front-left, front-right, back-left, back-right, three tail bones.</summary>
    private const int DragonNodes = 10, DragonFirstPaw = 3, DragonFirstTail = 7;
    /// <summary>Each dragon node's rest place, the skin's bind pose.</summary>
    private static readonly Vector3[] DragonBind =
    [
        Vector3.Zero, Vector3.Zero, new(0f, 0f, 1.2f),
        new(-0.8f, 1.2f, 0f), new(0.8f, 1.2f, 0f), new(-0.8f, -1.2f, 0f), new(0.8f, -1.2f, 0f),
        new(0f, -3f, 0f), new(0f, -4f, 0f), new(0f, -5f, 0f),
    ];
    /// <summary>Each dragon node's place mid-stride: paws spread out from rest, front-left and back-right planted.</summary>
    private static readonly Vector3[] DragonPose =
    [
        Vector3.Zero, Vector3.Zero, new(0f, 0f, 1.2f),
        new(-1.3f, 2f, Planted), new(1.3f, 2f, 0.4f), new(-1.3f, -2f, 0.4f), new(1.3f, -2f, Planted),
        new(0f, -3f, 0f), new(0f, -4f, 0f), new(0f, -5f, 0f),
    ];
    /// <summary>A dragon paw, across by along, metres; a tail segment's box lies 3 cm off the ground.</summary>
    private static readonly Vector2 DragonPaw = new(0.7f, 0.9f);
    private const float TailLow = 0.03f;

    /// <summary>Where a dragon's rigid claw guard lies, ahead of its forepaws on the ground, and its size.</summary>
    private static readonly Vector2 ClawGuardAt = new(0f, 3f), ClawGuard = new(0.3f, 0.3f);

    /// <summary>The red dragon as one skinned mesh over bone dummies posed at <see cref="DragonPose"/>; with
    /// <paramref name="bones"/> false its skin binds none, so it stays at its rest pose. A <paramref name="guard"/>
    /// adds a rigid claw guard mesh after the skin.</summary>
    private static ModelDef Dragon(bool bones, bool guard = false)
    {
        var mesh = new FixMesh();
        mesh.Box(DragonBind[2], 1.6f, 3.6f, 1.2f, 2);
        for (int k = 0; k < 4; k++) mesh.Box(DragonBind[DragonFirstPaw + k], DragonPaw.X, DragonPaw.Y, 0.3f, DragonFirstPaw + k);
        for (int k = 0; k < 3; k++) mesh.Box(DragonBind[DragonFirstTail + k] + new Vector3(0f, 0f, TailLow), 0.4f, 0.9f, 0.2f, DragonFirstTail + k);
        var b = new FixBody();
        b.Node("dskin", 0, Vector3.Zero, mesh.Mesh(), mesh.Skin(bones ? DragonBind : []));
        string[] names = ["spine", "paw_fl", "paw_fr", "paw_bl", "paw_br", "tail1", "tail2", "tail3"];
        for (int k = 2; k < DragonNodes; k++) b.Node(names[k - 2], 0, DragonPose[k]);
        if (guard) b.Node("claw_guard", 0, new Vector3(ClawGuardAt, Planted), new FixMesh().Box(Vector3.Zero, ClawGuard.X, ClawGuard.Y, 0.1f).Mesh());
        return b.Model(guard ? "cx_pr_guarded" : bones ? "cx_pr_dragon" : "cx_pr_boneless");
    }

    /// <summary>One placed creature: a scene-database object of <paramref name="m"/> at <paramref name="at"/>, facing +y,
    /// parts built and composed; default when the tables are full.</summary>
    private static DbId Place(ModelDef m, Vector3 at)
    {
        DbId obj = SceneDb.Objects.Alloc();
        if (obj.IsNull) return obj;
        int o = obj.Index;
        SceneDb.ObjKindOf[o] = ObjKind.Model;
        SceneDb.ObjModel[o] = m;
        SceneDb.ObjPos[o] = at;
        SceneDb.ObjRot[o] = Quaternion.Identity;
        SceneDb.ObjScale[o] = 1f;
        SceneDb.ObjAttachTo[o] = default;
        DbAnim.ObjectCreated(obj);
        if (PartTree.Build(obj, m)) DbAnim.PartGetWorld(SceneDb.ObjPartFirst[o], out _, out _);
        else { Unplace(obj); return default; }
        if (Seam.Facade.Built && Seam.Facade.Mint(Seam.FacadeKind.Object, obj) is var shell and not 0)
            Seam.SeamShell.NoteScene(shell, world: true);
        return obj;
    }

    private static void Unplace(DbId obj)
    {
        if (obj.IsNull) return;
        if (SceneDb.Objects.Valid(SceneDb.ObjAttachTo[obj.Index])) SceneDb.PieceUnlink(SceneDb.ObjAttachTo[obj.Index], obj);
        nint shell = Seam.Facade.ObjectShell(obj);
        if (shell != 0) { Seam.Facade.Retire(shell); SceneDb.ObjFacade[obj.Index] = 0; }
        DbAnim.ObjectRetired(obj);
        PartTree.Free(obj);
        SceneDb.Objects.Free(obj);
    }
}
