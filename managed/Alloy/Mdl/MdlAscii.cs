using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace CX.Alloy.Mdl;

/// <summary>The ASCII <c>.mdl</c> reader, ported keyword for keyword from the engine's line interpreter
/// (<c>dofile</c> @0x434930, <c>FuncInterp</c> @0x433720, <c>ImportMaxTree</c> @0x447ef0, <c>ParseNode</c>
/// @0x447540). Its quirks are deliberate: case-SENSITIVE <c>end</c>/<c>done</c> prefix terminators, unknown
/// node types becoming plain nodes, axis-angle by VALUE COUNT (four plain, five keyed), sscanf's keep-on-error.</summary>
internal static class MdlAscii
{
    internal static ModelDef? Read(ReadOnlySpan<byte> data, string name, out string? error)
    {
        var r = new Reader(data, name);
        r.Run();
        error = r.Error;
        return error == null ? r.Model : null;
    }

    /// <summary>Up to ten floats and the count <c>sscanf</c> matched: the engine's <c>AurGenericKey</c>
    /// (0x2c bytes).</summary>
    [InlineArray(10)]
    private struct Key10 { private float _e0; }

    private struct AurKey { public Key10 V; public int N; }

    /// <summary>One <c>weights</c> line: up to four (bone name, weight) pairs, the names defaulting to
    /// <c>ROOT</c> as the engine pre-fills them.</summary>
    private struct WeightLine
    {
        public string B0, B1, B2, B3;
        public float W0, W1, W2, W3;
        public int N;
    }

    /// <summary>Which of the three array-body forms a header line selects.</summary>
    private enum Form { Lines, Blob, EndList }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _d;
        private int _at;
        internal readonly ModelDef Model;
        internal string? Error;

        /// <summary>Nodes in FILE order with their parent's file index; the tree is flattened to pre-order
        /// once the geometry block closes, because that is the order a compiled binary stores.</summary>
        private readonly List<NodeDef> _file;
        private readonly List<int> _parentOf;
        private readonly List<string> _parentName;
        private int _rootFile;
        /// <summary>The file declared a WALKMESH geometry block (<c>beginwalkmeshgeom</c>), which is allowed to
        /// hold nothing at all -- a tileset's filler tiles ship exactly that.</summary>
        private bool _walkmesh;
        private AnimDef? _anim;
        private readonly List<AnimDef> _anims;
        /// <summary>The engine's node-index counter (<c>Model+0x58</c>), bumped per <c>endnode</c>.</summary>
        private int _nodeCounter;
        /// <summary>The events of the animation being parsed, kept sorted.</summary>
        private readonly List<AnimEvent> _events = new();
        /// <summary>Skin nodes whose <c>weights</c> block named bones; resolved once the tree exists.</summary>
        private readonly List<(NodeDef Node, List<WeightLine> Lines)> _weights;
        internal Reader(ReadOnlySpan<byte> d, string name)
        {
            _d = d;
            _at = 0;
            Error = null;
            Model = new ModelDef
            {
                Name = name,
                // Model::Model's defaults, which an ASCII file only overrides where it says so.
                BMin = new Vector3(-5f, -5f, -1f),
                BMax = new Vector3(5f, 5f, 10f),
                Radius = 7f,
                AnimScale = 1f,
                Fog = true,
            };
            _file = new List<NodeDef>();
            _parentOf = new List<int>();
            _parentName = new List<string>();
            _rootFile = -1;
            _anim = null;
            _anims = new List<AnimDef>();
            _nodeCounter = 0;
            _weights = new List<(NodeDef, List<WeightLine>)>();
        }

        // ---- the line stream ----

        /// <summary>One raw line, as <c>AurResGetNextLine</c> hands it: terminators removed, nothing trimmed.
        /// False at end of file (the engine's null).</summary>
        private bool NextRaw(out ReadOnlySpan<byte> line)
        {
            if (_at >= _d.Length) { line = default; return false; }
            int start = _at, end = start;
            while (end < _d.Length && _d[end] != (byte)'\n' && _d[end] != (byte)'\r') end++;
            int after = end;
            while (after < _d.Length && _d[after] == (byte)'\r') after++;
            if (after < _d.Length && _d[after] == (byte)'\n') after++;
            _at = after;
            line = _d[start..end];
            return true;
        }

        /// <summary>The next line an array body would consume: <c>Parse&lt;T&gt;</c> skips a line whose FIRST
        /// byte is NUL, LF or CR -- note that a line of spaces is not blank by that test.</summary>
        private bool NextArrayLine(out ReadOnlySpan<byte> line)
        {
            while (NextRaw(out line))
                if (line.Length != 0) return true;
            return false;
        }

        /// <summary><c>AurResGetData</c>: <paramref name="n"/> bytes straight off the cursor. Widened to
        /// long, because a declared count times a stride overflows an int on a hostile file.</summary>
        private ReadOnlySpan<byte> Blob(long n)
        {
            if (n < 0 || _at + n > _d.Length) { _at = _d.Length; Clamped++; return default; }
            ReadOnlySpan<byte> s = _d.Slice(_at, (int)n);
            _at += (int)n;
            return s;
        }

        /// <summary>Nodes the tree walk never reached from the root, because their <c>parent</c> line named a
        /// node that does not exist. The engine dereferences NULL on one of those; CX keeps the node and
        /// counts it.</summary>
        internal int Orphans;

        /// <summary>Declared element counts the file could not possibly supply, so the parse clamped them.
        /// A refusal of our own mechanism is counted and named, never a quiet hole.</summary>
        internal int Clamped;

        /// <summary>How many elements of <paramref name="stride"/> bytes the REST OF THE FILE could still
        /// supply, so a declared count cannot be believed into a huge allocation -- a deliberate difference
        /// from the engine, which allocates whatever the file declares. A line body needs at least two bytes,
        /// a blob needs <paramref name="stride"/>; anything past that is counted in <see cref="Clamped"/>.</summary>
        private int Cap(int declared, int stride, bool blob)
        {
            if (declared <= 0) return 0;
            long room = _d.Length - _at;
            long most = blob ? room / Math.Max(1, stride) : (room + 1) / 2;
            if (declared <= most) return declared;
            Clamped++;
            return (int)Math.Max(0, most);
        }

        private static ReadOnlySpan<byte> Skip(ReadOnlySpan<byte> s)
        {
            int i = 0;
            while (i < s.Length && (s[i] == (byte)' ' || s[i] == (byte)'\t')) i++;
            return s[i..];
        }

        /// <summary>The engine's block terminators are raw <c>memcmp</c>s: case-sensitive and prefix-only.</summary>
        private static bool Starts(ReadOnlySpan<byte> s, string lit)
        {
            if (s.Length < lit.Length) return false;
            for (int i = 0; i < lit.Length; i++) if (s[i] != (byte)lit[i]) return false;
            return true;
        }

        // ---- the driver ----

        internal void Run()
        {
            while (true)
            {
                if (!NextRaw(out ReadOnlySpan<byte> raw)) break;
                ReadOnlySpan<byte> line = Trim(raw);
                if (line.Length == 0 || line[0] == (byte)'#') continue;
                // FuncInterp: the command token runs to the first space or '=', then runs of both are skipped.
                int i = 0;
                while (i < line.Length && line[i] != (byte)' ' && line[i] != (byte)'=') i++;
                ReadOnlySpan<byte> cmd = line[..i];
                while (i < line.Length && (line[i] == (byte)' ' || line[i] == (byte)'=')) i++;
                ReadOnlySpan<byte> args = line[i..];
                Command(cmd, args);
                if (Error != null) return;
            }
            Finish();
        }

        private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> s)
        {
            int a = 0, b = s.Length;
            while (a < b && (s[a] == (byte)' ' || s[a] == (byte)'\t')) a++;
            while (b > a && MdlTok.IsSpace(s[b - 1])) b--;
            return s[a..b];
        }

        /// <summary>The nine commands a <c>.mdl</c> may call. <c>FindConCallBack</c> looks a name up with
        /// <c>strcmp</c>, so these are the only spellings that work.</summary>
        private void Command(ReadOnlySpan<byte> cmd, ReadOnlySpan<byte> args)
        {
            if (Eq(cmd, "newmodel"))
            {
                var sc = new MdlTok.Scan(args);
                Model.StatedName = MdlTok.TextRaw(sc.Word());
                return;
            }
            if (Eq(cmd, "setsupermodel"))
            {
                var sc = new MdlTok.Scan(args);
                sc.Word();
                string s = MdlTok.Text(sc.Word());
                Model.SuperName = string.Equals(s, "null", StringComparison.OrdinalIgnoreCase) ? "" : s;
                return;
            }
            if (Eq(cmd, "classification"))
            {
                // strcasecmp against the WHOLE argument, so a trailing word means no match at all.
                if (MdlTok.Is(args, "tile")) Model.Classification = 2;
                else if (MdlTok.Is(args, "character")) Model.Classification = 4;
                else if (MdlTok.Is(args, "effect") || MdlTok.Is(args, "effects")) Model.Classification = 1;
                else if (MdlTok.Is(args, "door")) Model.Classification = 8;
                return;
            }
            if (Eq(cmd, "setanimationscale")) { Model.AnimScale = MdlTok.Flt(args, 1f); return; }
            if (Eq(cmd, "ignorefog")) { if (MdlTok.Int(args, 0) == 1) Model.Fog = false; return; }
            if (Eq(cmd, "beginmodelgeom")) { ImportTree(Scope.Model); return; }
            // A tile walkmesh (.wok, restype 2016) is an ASCII model whose geometry block is spelled
            // `beginwalkmeshgeom` -- the header is `#MAXWALKMESH ASCII` and there is no `newmodel`, so the
            // resref is the identity, as it already is for a .mdl. The body is an ordinary `node aabb`, and
            // ImportTree already stops on any `end`/`done` line, so `endwalkmeshgeom` needs nothing.
            if (Eq(cmd, "beginwalkmeshgeom")) { _walkmesh = true; ImportTree(Scope.Model); return; }
            if (Eq(cmd, "newanim"))
            {
                _anim = new AnimDef { Name = MdlTok.Str(args), Length = 1f, TransTime = 0.25f };
                int firstNode = _file.Count;
                // ParseNode numbers a node off the TREE it belongs to, and an animation is its own tree.
                int saveCounter = _nodeCounter, saveRoot = _rootFile;
                _nodeCounter = 0;
                _rootFile = -1;
                ImportTree(Scope.Anim);
                CloseAnim(firstNode);
                _nodeCounter = saveCounter;
                _rootFile = saveRoot;
                return;
            }
            // donemodel is an empty function; filedependancy is whitelisted but unregistered, so both are
            // no-ops, and anything else never reaches a handler at all.
        }

        private static bool Eq(ReadOnlySpan<byte> s, string lit)
        {
            if (s.Length != lit.Length) return false;
            for (int i = 0; i < lit.Length; i++) if (s[i] != (byte)lit[i]) return false;
            return true;
        }

        /// <summary>Hostile-input bounds on the walkmesh tree: a balanced tree over 65536 faces is 17 deep,
        /// and no engine mesh carries more than that many.</summary>
        private const int MaxAabbDepth = 128, MaxAabbNodes = 1 << 18;
        /// <summary>Nodes one ASCII model may declare. The heaviest real model has a few hundred.</summary>
        private const int MaxNodes = 1 << 16;

        private enum Scope { Model, Anim }

        /// <summary><c>ImportMaxTree</c>: every line to the tree's field handler until one starts
        /// <c>end</c> or <c>done</c>.</summary>
        private void ImportTree(Scope scope)
        {
            while (NextRaw(out ReadOnlySpan<byte> raw))
            {
                ReadOnlySpan<byte> line = Skip(raw);
                if (Starts(line, "done") || Starts(line, "end")) return;
                if (Starts(line, "node")) { ParseNode(line[4..]); continue; }
                if (scope == Scope.Model) ModelField(line); else AnimField(line);
            }
        }

        private void ModelField(ReadOnlySpan<byte> line)
        {
            ReadOnlySpan<byte> w = MdlTok.First(line);
            if (MdlTok.Is(w, "radius")) Model.Radius = MdlTok.Flt(line[6..], Model.Radius);
            else if (MdlTok.Is(w, "bmin")) Model.BMin = MdlTok.Vec3(line[4..], Model.BMin);
            else if (MdlTok.Is(w, "bmax")) Model.BMax = MdlTok.Vec3(line[4..], Model.BMax);
        }

        private void AnimField(ReadOnlySpan<byte> line)
        {
            AnimDef a = _anim!;
            ReadOnlySpan<byte> w = MdlTok.First(line);
            // animlength and event are raw memcmps in the engine: case-sensitive and prefix-only.
            if (Starts(line, "animlength"))
            {
                // sscanf("%s %f"): the engine skips ONE token before the float, so `animlength 1.5` is a no-op.
                var sc = new MdlTok.Scan(line[10..]);
                sc.Word();
                a.Length = sc.Flt(a.Length);
                return;
            }
            if (Starts(line, "event"))
            {
                var sc = new MdlTok.Scan(line[5..]);
                float t = sc.Flt(0f);
                ReadOnlySpan<byte> nw = sc.Word();
                string n = nw.Length == 0 ? "" : MdlTok.Text(nw);
                // The engine keeps the array sorted by ascending time (a linear scan plus a memmove), and
                // inserts BEFORE the events of equal time. One list, one ToArray at doneanim.
                int at = 0;
                while (at < _events.Count && _events[at].Time < t) at++;
                _events.Insert(at, new AnimEvent { Time = t, Name = n });
                return;
            }
            if (MdlTok.Is(w, "length")) a.Length = MdlTok.Flt(line[6..], a.Length);
            else if (MdlTok.Is(w, "transtime")) a.TransTime = MdlTok.Flt(line[9..], a.TransTime);
            else if (MdlTok.Is(w, "animroot")) a.AnimRoot = MdlTok.Str(line[8..]);
        }

        // ---- nodes ----

        private void ParseNode(ReadOnlySpan<byte> args)
        {
            if (_file.Count >= MaxNodes) { Clamped++; SkipToEndnode(); return; }
            _ctl.Clear();
            ReadOnlySpan<byte> type = MdlTok.Word(args, 0);
            string nodeName = MdlTok.TextRaw(MdlTok.Word(args, 1));
            NodeKind kind = TypeOf(type);
            var n = new NodeDef
            {
                Name = nodeName,
                Kind = kind,
                ContentFlags = MdlBinary.FlagsOf(kind),
            };
            Payload(n, kind);
            int me = _file.Count;
            _file.Add(n);
            _parentOf.Add(-1);
            _parentName.Add("");

            while (NextRaw(out ReadOnlySpan<byte> raw))
            {
                ReadOnlySpan<byte> line = Skip(raw);
                if (Starts(line, "endnode")) break;
                NodeField(n, me, line);
            }
            n.Controllers = _ctl.Count == 0 ? Array.Empty<ControllerTrack>() : _ctl.ToArray();
            _ctl.Clear();
            n.FileIndex = me;
            n.PartNumber = _nodeCounter++;
        }

        /// <summary>Drain a node body we are refusing, so the line stream stays in step.</summary>
        private void SkipToEndnode()
        {
            while (NextRaw(out ReadOnlySpan<byte> raw))
                if (Starts(Skip(raw), "endnode")) return;
        }

        /// <summary>The node-type keywords <c>ParseNode</c> accepts. Anything else -- including
        /// <c>dummy</c> -- makes a plain node.</summary>
        private static NodeKind TypeOf(ReadOnlySpan<byte> t)
        {
            if (MdlTok.Is(t, "camera")) return NodeKind.Camera;
            if (MdlTok.Is(t, "emitter")) return NodeKind.Emitter;
            if (MdlTok.Is(t, "light")) return NodeKind.Light;
            if (MdlTok.Is(t, "trimesh")) return NodeKind.TriMesh;
            if (MdlTok.Is(t, "animmesh")) return NodeKind.AnimMesh;
            if (MdlTok.Is(t, "danglymesh")) return NodeKind.Dangly;
            if (MdlTok.Is(t, "reference")) return NodeKind.Reference;
            if (MdlTok.Is(t, "aabb")) return NodeKind.Aabb;
            if (MdlTok.Is(t, "skin")) return NodeKind.Skin;
            return NodeKind.Dummy;
        }

        /// <summary>Allocate a kind's payloads with the engine constructors' defaults.</summary>
        private static void Payload(NodeDef n, NodeKind kind)
        {
            bool mesh = kind is NodeKind.TriMesh or NodeKind.Skin or NodeKind.AnimMesh or NodeKind.Dangly
                                or NodeKind.Aabb;
            if (mesh)
            {
                n.Mesh = new MeshDef { Raw = new MeshRaw(), Shininess = 1f, Render = true, Shadow = true };
                if (kind == NodeKind.Aabb)
                {
                    // MdlNodeAABB's ctor forces both off, and no keyword can turn them back on before it.
                    n.Mesh.Render = false;
                    n.Mesh.Shadow = false;
                    n.Mesh.WalkMesh = true;
                }
            }
            switch (kind)
            {
                case NodeKind.Skin: n.Skin = new SkinDef(); break;
                case NodeKind.Dangly: n.Dangly = new DanglyDef(); break;
                case NodeKind.AnimMesh: n.AnimMesh = new AnimMeshDef(); break;
                case NodeKind.Aabb: n.Aabb = new AabbDef(); break;
                case NodeKind.Light: n.Light = new LightDef { Shadow = true, Dynamic = true, AffectDynamic = true, Priority = 5, Fading = true }; break;
                case NodeKind.Emitter: n.Emitter = new EmitterDef(); break;
                case NodeKind.Reference: n.Reference = new ReferenceDef(); break;
            }
        }

        /// <summary>One field line, dispatched down the class chain the engine chains in.</summary>
        private void NodeField(NodeDef n, int me, ReadOnlySpan<byte> line)
        {
            // MdlNodeTrigger::InternalParseField is empty and does not even call its base.
            if (n.Kind == NodeKind.Trigger) return;
            switch (n.Kind)
            {
                case NodeKind.Skin: if (SkinField(n, line)) return; break;
                case NodeKind.Dangly: if (DanglyField(n, line)) return; break;
                case NodeKind.AnimMesh: if (AnimMeshField(n, line)) return; break;
                case NodeKind.Aabb: if (AabbField(n, line)) return; break;
                case NodeKind.Light: if (LightField(n, line)) return; break;
                case NodeKind.Emitter: if (EmitterField(n, line)) return; break;
                case NodeKind.Reference: if (ReferenceField(n, line)) return; break;
            }
            if (n.Mesh != null && MeshField(n, line)) return;
            BaseField(n, me, line);
        }

        private void BaseField(NodeDef n, int me, ReadOnlySpan<byte> line)
        {
            ReadOnlySpan<byte> w = MdlTok.First(line);
            if (MdlTok.Is(w, "position")) { Single(n, line[8..], Ctl.Position); return; }
            if (MdlTok.Is(w, "positionkey")) { Keyed(n, line[11..], Ctl.Position, false); return; }
            if (MdlTok.Is(w, "positionbezierkey")) { Keyed(n, line[17..], Ctl.Position, true); return; }
            if (MdlTok.Is(w, "orientation")) { Single(n, line[11..], Ctl.Orientation); return; }
            if (MdlTok.Is(w, "orientationkey")) { Keyed(n, line[14..], Ctl.Orientation, false); return; }
            if (MdlTok.Is(w, "orientationbezierkey")) { Keyed(n, line[20..], Ctl.Orientation, true); return; }
            if (MdlTok.Is(w, "scale")) { Single(n, line[5..], Ctl.Scale); return; }
            if (MdlTok.Is(w, "scalekey")) { Keyed(n, line[8..], Ctl.Scale, false); return; }
            if (MdlTok.Is(w, "scalebezierkey")) { Keyed(n, line[14..], Ctl.Scale, true); return; }
            if (MdlTok.Is(w, "wirecolor")) return;    // parsed into a discarded local by the engine
            if (MdlTok.Is(w, "inheritcolor")) { n.InheritColor = MdlTok.Int(line[12..], n.InheritColor ? 1 : 0) != 0; return; }
            if (Starts(line, "parent"))
            {
                string p = MdlTok.Str(line[6..]);
                if (string.Equals(p, "null", StringComparison.OrdinalIgnoreCase)) _rootFile = me;
                else _parentName[me] = p;
            }
        }

        private bool ReferenceField(NodeDef n, ReadOnlySpan<byte> line)
        {
            ReadOnlySpan<byte> w = MdlTok.First(line);
            if (MdlTok.Is(w, "refModel")) { n.Reference!.Model = MdlTok.Str(line[8..]); return true; }
            if (MdlTok.Is(w, "reattachable")) { n.Reference!.Reattachable = MdlTok.Int(line[12..], 0) != 0; return true; }
            return false;
        }

        private bool DanglyField(NodeDef n, ReadOnlySpan<byte> line)
        {
            DanglyDef d = n.Dangly!;
            ReadOnlySpan<byte> w = MdlTok.First(line);
            if (MdlTok.Is(w, "displacement")) { d.Displacement = MdlTok.Flt(line[12..], d.Displacement); return true; }
            if (MdlTok.Is(w, "period")) { d.Period = MdlTok.Flt(line[6..], d.Period); return true; }
            if (MdlTok.Is(w, "tightness")) { d.Tightness = MdlTok.Flt(line[9..], d.Tightness); return true; }
            if (MdlTok.Is(w, "constraints")) { d.Constraints = ArrFloat(line[11..]); return true; }
            return false;
        }

        private bool AnimMeshField(NodeDef n, ReadOnlySpan<byte> line)
        {
            AnimMeshDef a = n.AnimMesh!;
            ReadOnlySpan<byte> w = MdlTok.First(line);
            if (MdlTok.Is(w, "sampleperiod")) { a.SamplePeriod = MdlTok.Flt(line[12..], a.SamplePeriod); return true; }
            if (MdlTok.Is(w, "animverts")) { a.Positions = new[] { ArrVec3(line[9..]) }; return true; }
            if (MdlTok.Is(w, "animtverts")) { a.Uvs = new[] { ArrVec2(line[10..]) }; return true; }
            return false;
        }

        private bool AabbField(NodeDef n, ReadOnlySpan<byte> line)
        {
            if (!MdlTok.Is(MdlTok.First(line), "aabb")) return false;
            var tree = new List<AabbNode>();
            AabbLine(line[4..], tree, 0);
            n.Aabb!.Tree = tree.ToArray();
            return true;
        }

        /// <summary>One <c>aabb</c> line, pre-order: six bounds floats, the leaf face index, and an optional
        /// split plane. A leaf index of -1 means the next two lines are this node's children.</summary>
        private int AabbLine(ReadOnlySpan<byte> args, List<AabbNode> tree, int depth)
        {
            // Hostile input: the engine recurses once per interior line with no bound, so a file of bare
            // zeroes overflows its stack. Ours refuses past a depth no real walkmesh reaches.
            if (depth > MaxAabbDepth || tree.Count >= MaxAabbNodes) { Clamped++; return -1; }
            var sc = new MdlTok.Scan(args);
            var node = new AabbNode
            {
                BMin = new Vector3(sc.Flt(0f), sc.Flt(0f), sc.Flt(0f)),
                BMax = new Vector3(sc.Flt(0f), sc.Flt(0f), sc.Flt(0f)),
                Left = -1,
                Right = -1,
            };
            // The leaf index's default is the engine's uninitialised stack slot; -1 (interior) is the only
            // value that makes the recursion terminate, so a line that states none is treated as a leaf.
            node.Face = sc.Int(0);
            int plane = sc.Int(0);
            int given = sc.Matched;
            int me = tree.Count;
            tree.Add(node);
            if (node.Face == -1)
            {
                if (NextArrayLine(out ReadOnlySpan<byte> l0)) node.Left = AabbLine(Skip(l0), tree, depth + 1);
                if (NextArrayLine(out ReadOnlySpan<byte> l1)) node.Right = AabbLine(Skip(l1), tree, depth + 1);
                node.Plane = given >= 8
                    ? plane
                    : DerivePlane(node.Left >= 0 ? tree[node.Left] : default, node.Right >= 0 ? tree[node.Right] : default);
            }
            else node.Plane = given >= 8 ? plane : 0;
            tree[me] = node;
            return me;
        }

        /// <summary>With no split plane stated the engine derives one from the children's box centres: the
        /// dominant axis of <c>(c1.min + c1.max) - (c0.min + c0.max)</c> and its sign.</summary>
        private static int DerivePlane(AabbNode c0, AabbNode c1)
        {
            Vector3 d = c1.BMin + c1.BMax - (c0.BMin + c0.BMax);
            float ax = MathF.Abs(d.X), ay = MathF.Abs(d.Y), az = MathF.Abs(d.Z);
            // The engine's tests are strict: a tie falls through to the next axis, and a zero delta is
            // negative (Parse(char const*, AABB*&) @0x446ed0).
            if (ax > ay && ax > az) return d.X > 0f ? 1 : 8;
            if (ay > az) return d.Y > 0f ? 2 : 0x10;
            return d.Z > 0f ? 4 : 0x20;
        }

        private bool SkinField(NodeDef n, ReadOnlySpan<byte> line)
        {
            SkinDef s = n.Skin!;
            ReadOnlySpan<byte> w = MdlTok.First(line);
            if (MdlTok.Is(w, "weights")) { Weights(n, line[7..]); return true; }
            if (MdlTok.Is(w, "qbone_ref_inv")) { s.BindRotation = ArrQuat(line[13..]); return true; }
            if (MdlTok.Is(w, "tbone_ref_inv")) { s.BindTranslation = ArrVec3(line[13..]); return true; }
            if (MdlTok.Is(w, "boneconstantindices")) { s.BoneNodes = ArrInt(line[19..]); return true; }
            return false;
        }

        private bool MeshField(NodeDef n, ReadOnlySpan<byte> line)
        {
            MeshDef m = n.Mesh!;
            MeshRaw raw = m.Raw!;
            ReadOnlySpan<byte> w = MdlTok.First(line);
            if (MdlTok.Is(w, "verts")) { raw.Verts = ArrVec3(line[5..]); return true; }
            if (MdlTok.Is(w, "tverts")) { raw.TVerts[0] = ArrVec3(line[6..]); return true; }
            if (MdlTok.Is(w, "tverts1")) { raw.TVerts[1] = ArrVec3(line[7..]); return true; }
            if (MdlTok.Is(w, "tverts2")) { raw.TVerts[2] = ArrVec3(line[7..]); return true; }
            if (MdlTok.Is(w, "tverts3")) { raw.TVerts[3] = ArrVec3(line[7..]); return true; }
            if (MdlTok.Is(w, "colors")) { raw.Colors = ArrVec3(line[6..]); return true; }
            if (MdlTok.Is(w, "tangents")) { raw.Tangents = ArrVec4(line[8..]); return true; }
            if (MdlTok.Is(w, "normals")) { raw.Normals = ArrVec3(line[7..]); return true; }
            if (MdlTok.Is(w, "faces"))
            {
                // The engine guards on the scratch face count, so the FIRST faces block of a node wins.
                RawFace[] f = ArrFaces(line[5..]);
                if (raw.Faces.Length == 0) raw.Faces = f;
                return true;
            }
            if (MdlTok.Is(w, "texindices1")) { raw.TexIndices[0] = ArrVec3(line[11..]); return true; }
            if (MdlTok.Is(w, "texindices2")) { raw.TexIndices[1] = ArrVec3(line[11..]); return true; }
            if (MdlTok.Is(w, "texindices3")) { raw.TexIndices[2] = ArrVec3(line[11..]); return true; }
            if (MdlTok.Is(w, "mirrorlist")) { raw.MirrorList = ArrInt(line[10..]); return true; }
            if (MdlTok.Is(w, "texture0")) { m.Textures[0] = MdlTok.Str(line[8..]); return true; }
            if (MdlTok.Is(w, "texture1")) { m.Textures[1] = MdlTok.Str(line[8..]); return true; }
            if (MdlTok.Is(w, "texture2")) { m.Textures[2] = MdlTok.Str(line[8..]); return true; }
            if (MdlTok.Is(w, "bitmap"))
            {
                string b = MdlTok.Str(line[6..]);
                if (b.Length != 0) m.Textures[0] = b;           // the engine wipes the field first, then copies
                return true;
            }
            if (MdlTok.Is(w, "diffuse")) { m.Diffuse = MdlTok.Vec3(line[7..], m.Diffuse); return true; }
            if (MdlTok.Is(w, "ambient")) { m.Ambient = MdlTok.Vec3(line[7..], m.Ambient); return true; }
            if (MdlTok.Is(w, "specular")) { m.Specular = MdlTok.Vec3(line[8..], m.Specular); return true; }
            if (MdlTok.Is(w, "shininess")) { m.Shininess = MdlTok.Flt(line[9..], m.Shininess); return true; }
            if (MdlTok.Is(w, "shadow")) { m.Shadow = MdlTok.Int(line[6..], m.Shadow ? 1 : 0) != 0; return true; }
            if (MdlTok.Is(w, "beaming")) { m.Beaming = MdlTok.Int(line[7..], m.Beaming ? 1 : 0) != 0; return true; }
            if (MdlTok.Is(w, "render")) { m.Render = MdlTok.Int(line[6..], m.Render ? 1 : 0) != 0; return true; }
            if (MdlTok.Is(w, "transparencyhint")) { m.TransparencyHint = MdlTok.Int(line[16..], m.TransparencyHint); return true; }
            if (MdlTok.Is(w, "renderhint"))
            {
                m.RenderHint = MdlTok.Str(line[10..]);
                m.RenderHintId = m.RenderHint switch
                {
                    "normalandspecmapped" => 2,
                    "normaltangents" => 3,
                    "none" => 1,
                    _ => 0,
                };
                return true;
            }
            if (MdlTok.Is(w, "rotatetexture")) { m.RotateTexture = MdlTok.Bool(line[13..], m.RotateTexture); return true; }
            if (MdlTok.Is(w, "tilefade")) { m.TileFade = MdlTok.Int(line[8..], m.TileFade); return true; }
            if (MdlTok.Is(w, "selfillumcolor")) { Single(n, line[14..], Ctl.SelfIllumColor); return true; }
            if (MdlTok.Is(w, "selfillumcolorkey")) { Keyed(n, line[17..], Ctl.SelfIllumColor, false); return true; }
            if (MdlTok.Is(w, "selfillumcolorbezierkey")) { Keyed(n, line[23..], Ctl.SelfIllumColor, true); return true; }
            if (MdlTok.Is(w, "alpha")) { Single(n, line[5..], Ctl.Alpha); return true; }
            if (MdlTok.Is(w, "alphakey")) { Keyed(n, line[8..], Ctl.Alpha, false); return true; }
            if (MdlTok.Is(w, "alphabezierkey")) { Keyed(n, line[14..], Ctl.Alpha, true); return true; }
            if (MdlTok.Is(w, "bmin")) { m.BMin = MdlTok.Vec3(line[4..], m.BMin); return true; }
            if (MdlTok.Is(w, "bmax")) { m.BMax = MdlTok.Vec3(line[4..], m.BMax); return true; }
            if (MdlTok.Is(w, "vertexindicescount")) { m.VertexIndexCounts = ArrInt(line[18..]); return true; }
            if (MdlTok.Is(w, "leftoverfaces")) { m.LeftoverFaces = ArrUint(line[13..]); return true; }
            if (MdlTok.Is(w, "vertexindices")) { m.VertexIndices = ArrNestedUint(line[13..]); return true; }
            if (MdlTok.Is(w, "materialname")) { m.MaterialName = MdlTok.Str(line[12..]); return true; }
            return false;
        }

        private bool LightField(NodeDef n, ReadOnlySpan<byte> line)
        {
            LightDef l = n.Light!;
            ReadOnlySpan<byte> w = MdlTok.First(line);
            if (Ctl3(n, w, line, "color", Ctl.LightColor, 5)) return true;
            if (Ctl3(n, w, line, "radius", Ctl.LightRadius, 6)) return true;
            if (Ctl3(n, w, line, "shadowradius", Ctl.LightShadowRadius, 12)) return true;
            if (Ctl3(n, w, line, "verticaldisplacement", Ctl.LightVerticalDisplacement, 20)) return true;
            if (Ctl3(n, w, line, "multiplier", Ctl.LightMultiplier, 10)) return true;
            if (MdlTok.Is(w, "ambientonly")) { l.AmbientOnly = MdlTok.Int(line[11..], l.AmbientOnly ? 1 : 0) != 0; return true; }
            if (MdlTok.Is(w, "shadow")) { l.Shadow = MdlTok.Int(line[6..], l.Shadow ? 1 : 0) != 0; return true; }
            if (MdlTok.Is(w, "isdynamic")) { l.Dynamic = MdlTok.Int(line[9..], l.Dynamic ? 1 : 0) != 0; return true; }
            if (MdlTok.Is(w, "nDynamicType")) { l.Dynamic = MdlTok.Int(line[12..], l.Dynamic ? 1 : 0) != 0; return true; }
            if (MdlTok.Is(w, "affectdynamic")) { l.AffectDynamic = MdlTok.Int(line[13..], l.AffectDynamic ? 1 : 0) != 0; return true; }
            if (MdlTok.Is(w, "lightpriority")) { l.Priority = MdlTok.Int(line[13..], l.Priority); return true; }
            if (MdlTok.Is(w, "generateflare")) { l.GenerateFlare = MdlTok.Int(line[13..], l.GenerateFlare ? 1 : 0) != 0; return true; }
            if (MdlTok.Is(w, "fadingLight")) { l.Fading = MdlTok.Int(line[11..], l.Fading ? 1 : 0) != 0; return true; }
            if (MdlTok.Is(w, "flareradius")) { l.FlareRadius = MdlTok.Flt(line[11..], l.FlareRadius); return true; }
            if (MdlTok.Is(w, "texturenames")) { l.FlareTextures = ArrStr(line[12..]); return true; }
            if (MdlTok.Is(w, "flaresizes")) { l.FlareSizes = ArrFloat(line[10..]); return true; }
            if (MdlTok.Is(w, "flarepositions")) { l.FlarePositions = ArrFloat(line[14..]); return true; }
            if (MdlTok.Is(w, "flarecolorshifts")) { l.FlareColors = ArrVec3(line[16..]); return true; }
            return false;
        }

        /// <summary>The three spellings of one controller keyword: plain, <c>...key</c>, <c>...bezierkey</c>.
        /// The argument offset is always <c>strlen(keyword)</c>, then +3 and +9.</summary>
        private bool Ctl3(NodeDef n, ReadOnlySpan<byte> w, ReadOnlySpan<byte> line, string kw, int id, int len)
        {
            if (MdlTok.Is(w, kw)) { Single(n, line[len..], id); return true; }
            if (w.Length == len + 3 && MdlTok.Is(w[..len], kw) && MdlTok.Is(w[len..], "key"))
            { Keyed(n, line[(len + 3)..], id, false); return true; }
            if (w.Length == len + 9 && MdlTok.Is(w[..len], kw) && MdlTok.Is(w[len..], "bezierkey"))
            { Keyed(n, line[(len + 9)..], id, true); return true; }
            return false;
        }

        private bool EmitterField(NodeDef n, ReadOnlySpan<byte> line)
        {
            EmitterDef e = n.Emitter!;
            ReadOnlySpan<byte> w = MdlTok.First(line);
            // Flags: the engine parses each into a local that starts false and never CLEARS the bit, so a
            // `p2p 0` line leaves an already-set bit set.
            if (MdlTok.Is(w, "p2p")) { if (MdlTok.Bool(line[3..], false)) e.Flags |= 0x001; return true; }
            if (MdlTok.Is(w, "p2p_sel")) { if (MdlTok.Int(line[7..], 0) == 1) e.Flags |= 0x002; return true; }
            if (MdlTok.Is(w, "affectedByWind")) { if (MdlTok.Bool(line[14..], false)) e.Flags |= 0x004; return true; }
            if (MdlTok.Is(w, "m_isTinted")) { if (MdlTok.Bool(line[10..], false)) e.Flags |= 0x008; return true; }
            if (MdlTok.Is(w, "bounce")) { if (MdlTok.Bool(line[6..], false)) e.Flags |= 0x010; return true; }
            if (MdlTok.Is(w, "random")) { if (MdlTok.Bool(line[6..], false)) e.Flags |= 0x020; return true; }
            if (MdlTok.Is(w, "inherit")) { if (MdlTok.Bool(line[7..], false)) e.Flags |= 0x040; return true; }
            if (MdlTok.Is(w, "inheritvel")) { if (MdlTok.Bool(line[10..], false)) e.Flags |= 0x080; return true; }
            if (MdlTok.Is(w, "inherit_local")) { if (MdlTok.Bool(line[13..], false)) e.Flags |= 0x100; return true; }
            if (MdlTok.Is(w, "splat")) { if (MdlTok.Bool(line[5..], false)) e.Flags |= 0x200; return true; }
            if (MdlTok.Is(w, "inherit_part")) { if (MdlTok.Bool(line[12..], false)) e.Flags |= 0x400; return true; }
            if (MdlTok.Is(w, "renderorder")) { e.RenderOrder = (short)MdlTok.Int(line[11..], e.RenderOrder); return true; }
            if (MdlTok.Is(w, "spawntype")) { e.SpawnType = MdlTok.Int(line[9..], e.SpawnType); return true; }
            if (MdlTok.Is(w, "update")) { e.Update = MdlTok.Str(line[6..]); return true; }
            if (MdlTok.Is(w, "render")) { e.Render = MdlTok.Str(line[6..]); return true; }
            if (MdlTok.Is(w, "blend")) { e.Blend = MdlTok.Str(line[5..]); return true; }
            if (MdlTok.Is(w, "texture")) { e.Texture = MdlTok.Str(line[7..]); return true; }
            if (MdlTok.Is(w, "chunkName")) { e.ChunkName = MdlTok.Str(line[9..]); return true; }
            if (MdlTok.Is(w, "xgrid")) { e.XGrid = MdlTok.Int(line[5..], e.XGrid); return true; }
            if (MdlTok.Is(w, "ygrid")) { e.YGrid = MdlTok.Int(line[5..], e.YGrid); return true; }
            if (MdlTok.Is(w, "loop")) { e.Loop = MdlTok.Int(line[4..], e.Loop ? 1 : 0) != 0; return true; }
            if (MdlTok.Is(w, "deadspace")) { e.DeadSpace = MdlTok.Flt(line[9..], e.DeadSpace); return true; }
            if (MdlTok.Is(w, "twosidedtex")) { e.TwoSidedTex = MdlTok.Int(line[11..], e.TwoSidedTex ? 1 : 0) != 0; return true; }
            if (MdlTok.Is(w, "blastRadius")) { e.BlastRadius = MdlTok.Flt(line[11..], e.BlastRadius); return true; }
            if (MdlTok.Is(w, "blastLength")) { e.BlastLength = MdlTok.Flt(line[11..], e.BlastLength); return true; }
            return EmitterCtl(n, w, line);
        }

        /// <summary>The 39 emitter controllers, each in three spellings.</summary>
        private bool EmitterCtl(NodeDef n, ReadOnlySpan<byte> w, ReadOnlySpan<byte> line) =>
            Ctl3(n, w, line, "blurlength", Ctl.EmBlurLength, 10) ||
            Ctl3(n, w, line, "detonate", Ctl.EmDetonate, 8) ||
            Ctl3(n, w, line, "velocity", Ctl.EmVelocity, 8) ||
            Ctl3(n, w, line, "randvel", Ctl.EmRandVel, 7) ||
            Ctl3(n, w, line, "birthrate", Ctl.EmBirthRate, 9) ||
            Ctl3(n, w, line, "colorStart", Ctl.EmColorStart, 10) ||
            Ctl3(n, w, line, "colorMid", Ctl.EmColorMid, 8) ||
            Ctl3(n, w, line, "colorEnd", Ctl.EmColorEnd, 8) ||
            Ctl3(n, w, line, "percentStart", Ctl.EmPercentStart, 12) ||
            Ctl3(n, w, line, "percentMid", Ctl.EmPercentMid, 10) ||
            Ctl3(n, w, line, "percentEnd", Ctl.EmPercentEnd, 10) ||
            Ctl3(n, w, line, "alphaStart", Ctl.EmAlphaStart, 10) ||
            Ctl3(n, w, line, "alphaMid", Ctl.EmAlphaMid, 8) ||
            Ctl3(n, w, line, "alphaEnd", Ctl.EmAlphaEnd, 8) ||
            Ctl3(n, w, line, "sizeStart", Ctl.EmSizeStart, 9) ||
            Ctl3(n, w, line, "sizeMid", Ctl.EmSizeMid, 7) ||
            Ctl3(n, w, line, "sizeEnd", Ctl.EmSizeEnd, 7) ||
            Ctl3(n, w, line, "sizeStart_y", Ctl.EmSizeStartY, 11) ||
            Ctl3(n, w, line, "sizeMid_y", Ctl.EmSizeMidY, 9) ||
            Ctl3(n, w, line, "sizeEnd_y", Ctl.EmSizeEndY, 9) ||
            Ctl3(n, w, line, "frameStart", Ctl.EmFrameStart, 10) ||
            Ctl3(n, w, line, "frameEnd", Ctl.EmFrameEnd, 8) ||
            Ctl3(n, w, line, "lifeExp", Ctl.EmLifeExp, 7) ||
            Ctl3(n, w, line, "mass", Ctl.EmMass, 4) ||
            Ctl3(n, w, line, "spread", Ctl.EmSpread, 6) ||
            Ctl3(n, w, line, "particleRot", Ctl.EmParticleRot, 11) ||
            Ctl3(n, w, line, "lightningScale", Ctl.EmLightningScale, 14) ||
            Ctl3(n, w, line, "fps", Ctl.EmFps, 3) ||
            Ctl3(n, w, line, "xsize", Ctl.EmXSize, 5) ||
            Ctl3(n, w, line, "ysize", Ctl.EmYSize, 5) ||
            Ctl3(n, w, line, "bounce_co", Ctl.EmBounceCo, 9) ||
            Ctl3(n, w, line, "lightningDelay", Ctl.EmLightningDelay, 14) ||
            Ctl3(n, w, line, "lightningRadius", Ctl.EmLightningRadius, 15) ||
            Ctl3(n, w, line, "p2p_bezier2", Ctl.EmP2PBezier2, 11) ||
            Ctl3(n, w, line, "p2p_bezier3", Ctl.EmP2PBezier3, 11) ||
            Ctl3(n, w, line, "combinetime", Ctl.EmCombineTime, 11) ||
            Ctl3(n, w, line, "drag", Ctl.EmDrag, 4) ||
            Ctl3(n, w, line, "grav", Ctl.EmGrav, 4) ||
            Ctl3(n, w, line, "threshold", Ctl.EmThreshold, 9);

        // ---- controllers ----

        /// <summary>The controllers of the node being parsed. One list, emptied per node, turned into the
        /// node's array at its <c>endnode</c> -- a model with sixty thousand key lines would otherwise
        /// reallocate the whole array sixty thousand times.</summary>
        private readonly List<ControllerTrack> _ctl = new();

        private void Add(ControllerTrack t) => _ctl.Add(t);

        /// <summary><c>AddGenericControllerSingle</c>: one key at time 0. Exactly four values are read as
        /// axis-angle and converted -- by value count, not by keyword.</summary>
        private void Single(NodeDef n, ReadOnlySpan<byte> args, int id)
        {
            AurKey k = ParseKey(args);
            if (k.N == 4) AxisAngle(ref k, 0);
            var t = new ControllerTrack { Type = id, Columns = k.N, Times = new[] { 0f }, Values = new float[Math.Max(0, k.N)] };
            for (int i = 0; i < k.N; i++) t.Values[i] = k.V[i];
            Add(t);
        }

        /// <summary><c>AddGenericController</c> / <c>AddBezierController</c>: an array of keys whose first
        /// value is the time. All the times are stored first, then all the values.</summary>
        private void Keyed(NodeDef n, ReadOnlySpan<byte> args, int id, bool bezier)
        {
            AurKey[] keys = ArrKeys(args);
            if (keys.Length == 0) return;                      // a zero-key block adds no controller at all
            int cols = keys[0].N - 1;
            if (bezier) cols /= 3;
            if (cols < 0) cols = 0;
            int per = bezier ? cols * 3 : cols;
            var t = new ControllerTrack
            {
                Type = id,
                Columns = cols,
                Bezier = bezier,
                Times = new float[keys.Length],
                Values = new float[keys.Length * per],
            };
            for (int i = 0; i < keys.Length; i++) t.Times[i] = keys[i].V[0];
            int at = 0;
            for (int i = 0; i < keys.Length; i++)
                for (int c = 1; c < keys[i].N && at < t.Values.Length; c++) t.Values[at++] = keys[i].V[c];
            Add(t);
        }

        /// <summary>The axis-angle conversion both key readers do: the axis is normalised the engine's way
        /// (<c>Vector::Normalize</c> leaves a length already within 1e-5 of one ALONE and turns a zero vector
        /// into <c>(1,0,0)</c>), scaled by <c>sin(a/2)</c>, and <c>w = cos(a/2)</c>.</summary>
        private static void AxisAngle(ref AurKey k, int at)
        {
            float x = k.V[at], y = k.V[at + 1], z = k.V[at + 2], a = k.V[at + 3];
            (float sin, float cos) = (MathF.Sin(a * 0.5f), MathF.Cos(a * 0.5f));
            float len2 = x * x + y * y + z * z;
            if (len2 == 0f) { x = 1f; y = 0f; z = 0f; }
            else if (len2 != 1f && (len2 < 0.99999f || len2 > 1.00001f))
            {
                float s = MathF.Sqrt(1f / len2);
                x *= s; y *= s; z *= s;
            }
            k.V[at] = x * sin; k.V[at + 1] = y * sin; k.V[at + 2] = z * sin; k.V[at + 3] = cos;
        }

        /// <summary><c>Parse(char const*, AurGenericKey&amp;)</c>: one <c>sscanf</c> of ten <c>%f</c>, whose
        /// return count IS the key's column count.</summary>
        private static AurKey ParseKey(ReadOnlySpan<byte> args)
        {
            var k = new AurKey();
            var sc = new MdlTok.Scan(args);
            for (int n = 0; n < 10; n++)
            {
                float v = sc.Flt(float.NaN);
                if (sc.Matched != n + 1) break;
                k.V[n] = v;
            }
            k.N = sc.Matched;
            return k;
        }

        // ---- array bodies ----

        /// <summary>The header of an array field. <c>N</c> alone means N following lines; <c>N</c> plus any
        /// second token means a raw blob of <c>N * stride</c> bytes straight off the stream; no number at all
        /// means read until a line starting <c>endlist</c>.</summary>
        private Form Header(ReadOnlySpan<byte> args, int stride, out int n, out ReadOnlySpan<byte> blob)
        {
            blob = default;
            var sc = new MdlTok.Scan(args);
            int declared = sc.Int(0);
            if (sc.Matched == 0)
            {
                n = 0;
                return Form.EndList;
            }
            bool raw = sc.Word().Length != 0;
            n = Cap(declared, stride, raw);
            if (raw)
            {
                blob = Blob((long)n * stride);
                return Form.Blob;
            }
            return Form.Lines;
        }

        private Vector3[] ArrVec3(ReadOnlySpan<byte> args)
        {
            Form f = Header(args, 12, out int n, out ReadOnlySpan<byte> blob);
            if (f == Form.Blob)
            {
                var o = new Vector3[Math.Max(0, Math.Min(n, blob.Length / 12))];
                for (int i = 0; i < o.Length; i++) o[i] = Flt3(blob[(i * 12)..]);
                return o;
            }
            if (f == Form.Lines)
            {
                var o = new Vector3[Math.Max(0, n)];
                for (int i = 0; i < o.Length; i++)
                    o[i] = NextArrayLine(out ReadOnlySpan<byte> l) ? MdlTok.Vec3(l, default) : default;
                return o;
            }
            var list = new List<Vector3>();
            while (NextArrayLine(out ReadOnlySpan<byte> l2))
            {
                if (Starts(Skip(l2), "endlist")) break;
                list.Add(MdlTok.Vec3(l2, default));
            }
            return list.ToArray();
        }

        /// <summary>A UV array. A <c>tverts</c> line states three floats and the compiled file keeps two, so
        /// the third (the unused <c>w</c>) is dropped here as the engine drops it.</summary>
        private Vector2[] ArrVec2(ReadOnlySpan<byte> args)
        {
            Vector3[] v = ArrVec3(args);
            var o = new Vector2[v.Length];
            for (int i = 0; i < v.Length; i++) o[i] = new Vector2(v[i].X, v[i].Y);
            return o;
        }

        private Vector4[] ArrVec4(ReadOnlySpan<byte> args)
        {
            Form f = Header(args, 16, out int n, out ReadOnlySpan<byte> blob);
            if (f == Form.Blob)
            {
                var o = new Vector4[Math.Max(0, Math.Min(n, blob.Length / 16))];
                for (int i = 0; i < o.Length; i++)
                    o[i] = new Vector4(Flt3(blob[(i * 16)..]), BitConverter.ToSingle(blob[(i * 16 + 12)..]));
                return o;
            }
            if (f == Form.Lines)
            {
                var o = new Vector4[Math.Max(0, n)];
                for (int i = 0; i < o.Length; i++)
                    o[i] = NextArrayLine(out ReadOnlySpan<byte> l) ? MdlTok.Vec4(l, default) : default;
                return o;
            }
            var list = new List<Vector4>();
            while (NextArrayLine(out ReadOnlySpan<byte> l2))
            {
                if (Starts(Skip(l2), "endlist")) break;
                list.Add(MdlTok.Vec4(l2, default));
            }
            return list.ToArray();
        }

        private Quaternion[] ArrQuat(ReadOnlySpan<byte> args)
        {
            Vector4[] v = ArrVec4(args);
            var o = new Quaternion[v.Length];
            for (int i = 0; i < v.Length; i++) o[i] = new Quaternion(v[i].X, v[i].Y, v[i].Z, v[i].W);
            return o;
        }

        private float[] ArrFloat(ReadOnlySpan<byte> args)
        {
            Form f = Header(args, 4, out int n, out ReadOnlySpan<byte> blob);
            if (f == Form.Blob)
            {
                var o = new float[Math.Max(0, Math.Min(n, blob.Length / 4))];
                for (int i = 0; i < o.Length; i++) o[i] = BitConverter.ToSingle(blob[(i * 4)..]);
                return o;
            }
            if (f == Form.Lines)
            {
                var o = new float[Math.Max(0, n)];
                for (int i = 0; i < o.Length; i++) o[i] = NextArrayLine(out ReadOnlySpan<byte> l) ? MdlTok.Flt(l, 0f) : 0f;
                return o;
            }
            var list = new List<float>();
            while (NextArrayLine(out ReadOnlySpan<byte> l2))
            {
                if (Starts(Skip(l2), "endlist")) break;
                list.Add(MdlTok.Flt(l2, 0f));
            }
            return list.ToArray();
        }

        private int[] ArrInt(ReadOnlySpan<byte> args)
        {
            Form f = Header(args, 4, out int n, out ReadOnlySpan<byte> blob);
            if (f == Form.Blob)
            {
                var o = new int[Math.Max(0, Math.Min(n, blob.Length / 4))];
                for (int i = 0; i < o.Length; i++) o[i] = BitConverter.ToInt32(blob[(i * 4)..]);
                return o;
            }
            if (f == Form.Lines)
            {
                var o = new int[Math.Max(0, n)];
                for (int i = 0; i < o.Length; i++) o[i] = NextArrayLine(out ReadOnlySpan<byte> l) ? MdlTok.Int(l, 0) : 0;
                return o;
            }
            var list = new List<int>();
            while (NextArrayLine(out ReadOnlySpan<byte> l2))
            {
                if (Starts(Skip(l2), "endlist")) break;
                list.Add(MdlTok.Int(l2, 0));
            }
            return list.ToArray();
        }

        private uint[] ArrUint(ReadOnlySpan<byte> args)
        {
            int[] v = ArrInt(args);
            var o = new uint[v.Length];
            for (int i = 0; i < v.Length; i++) o[i] = (uint)v[i];
            return o;
        }

        /// <summary><c>vertexindices N</c>: each of the N following lines carries its own count and is
        /// followed by that many one-integer lines.</summary>
        private uint[][] ArrNestedUint(ReadOnlySpan<byte> args)
        {
            Form f = Header(args, 8, out int n, out _);
            if (f != Form.Lines) return Array.Empty<uint[]>();
            var o = new uint[Math.Max(0, n)][];
            for (int i = 0; i < o.Length; i++)
            {
                o[i] = Array.Empty<uint>();
                if (!NextArrayLine(out ReadOnlySpan<byte> head)) continue;
                int m = Cap(MdlTok.Int(head, 0), 4, false);
                if (m <= 0) continue;
                var inner = new uint[m];
                for (int k = 0; k < m; k++)
                    inner[k] = NextArrayLine(out ReadOnlySpan<byte> l) ? (uint)MdlTok.Int(l, 0) : 0u;
                o[i] = inner;
            }
            return o;
        }

        private string[] ArrStr(ReadOnlySpan<byte> args)
        {
            // Parse<char*> reads only the count; it has no raw-blob branch.
            var sch = new MdlTok.Scan(args);
            int declaredStr = sch.Int(0);
            if (sch.Matched != 0)
            {
                int n = Cap(declaredStr, 8, false);
                var o = new string[n];
                for (int i = 0; i < o.Length; i++) o[i] = NextArrayLine(out ReadOnlySpan<byte> l) ? MdlTok.Str(l) : "";
                return o;
            }
            var list = new List<string>();
            while (NextArrayLine(out ReadOnlySpan<byte> l2))
            {
                if (Starts(Skip(l2), "endlist")) break;
                list.Add(MdlTok.Str(l2));
            }
            return list.ToArray();
        }

        private RawFace[] ArrFaces(ReadOnlySpan<byte> args)
        {
            Form f = Header(args, 0x20, out int n, out ReadOnlySpan<byte> blob);
            if (f == Form.Blob)
            {
                var o = new RawFace[Math.Max(0, Math.Min(n, blob.Length / 0x20))];
                for (int i = 0; i < o.Length; i++)
                {
                    ReadOnlySpan<byte> r = blob[(i * 0x20)..];
                    o[i] = new RawFace
                    {
                        V0 = BitConverter.ToInt32(r), V1 = BitConverter.ToInt32(r[4..]), V2 = BitConverter.ToInt32(r[8..]),
                        SmoothGroup = BitConverter.ToInt32(r[12..]),
                        T0 = BitConverter.ToInt32(r[16..]), T1 = BitConverter.ToInt32(r[20..]), T2 = BitConverter.ToInt32(r[24..]),
                        Surface = BitConverter.ToInt32(r[28..]),
                    };
                }
                return o;
            }
            if (f == Form.Lines)
            {
                var o = new RawFace[Math.Max(0, n)];
                for (int i = 0; i < o.Length; i++) if (NextArrayLine(out ReadOnlySpan<byte> l)) o[i] = Face(l);
                return o;
            }
            var list = new List<RawFace>();
            while (NextArrayLine(out ReadOnlySpan<byte> l2))
            {
                if (Starts(Skip(l2), "endlist")) break;
                list.Add(Face(l2));
            }
            return list.ToArray();
        }

        /// <summary>One <c>faces</c> line: <c>sscanf("%d %d %d %d %d %d %d %d")</c>, which stops dead at the
        /// first field that does not convert and leaves the rest at zero (the engine zeroes the record first).</summary>
        private static RawFace Face(ReadOnlySpan<byte> l)
        {
            var sc = new MdlTok.Scan(l);
            return new RawFace
            {
                V0 = sc.Int(0), V1 = sc.Int(0), V2 = sc.Int(0),
                SmoothGroup = sc.Int(0),
                T0 = sc.Int(0), T1 = sc.Int(0), T2 = sc.Int(0),
                Surface = sc.Int(0),
            };
        }

        private void Weights(NodeDef node, ReadOnlySpan<byte> args)
        {
            Form f = Header(args, 0x48, out int n, out _);
            var list = new List<WeightLine>(Math.Max(0, n));
            if (f == Form.Lines)
            {
                for (int i = 0; i < n; i++) if (NextArrayLine(out ReadOnlySpan<byte> l)) list.Add(Weight(l));
            }
            else if (f == Form.EndList)
            {
                while (NextArrayLine(out ReadOnlySpan<byte> l))
                {
                    if (Starts(Skip(l), "endlist")) break;
                    list.Add(Weight(l));
                }
            }
            // The bone names stay names until the whole tree exists, so the resolve waits for Finish().
            _weights.Add((node, list));
        }

        private static WeightLine Weight(ReadOnlySpan<byte> l)
        {
            var w = new WeightLine { B0 = "root", B1 = "root", B2 = "root", B3 = "root" };
            var sc = new MdlTok.Scan(l);
            for (int i = 0; i < 4; i++)
            {
                ReadOnlySpan<byte> bn = sc.Word();
                if (bn.Length == 0) break;
                string name = MdlTok.Text(bn);
                float v = sc.Flt(0f);
                switch (i)
                {
                    case 0: w.B0 = name; w.W0 = v; break;
                    case 1: w.B1 = name; w.W1 = v; break;
                    case 2: w.B2 = name; w.W2 = v; break;
                    case 3: w.B3 = name; w.W3 = v; break;
                }
            }
            w.N = sc.Matched / 2;
            return w;
        }

        private AurKey[] ArrKeys(ReadOnlySpan<byte> args)
        {
            Form f = Header(args, 0x2c, out int n, out ReadOnlySpan<byte> blob);
            if (f == Form.Blob)
            {
                var o = new AurKey[Math.Max(0, Math.Min(n, blob.Length / 0x2c))];
                for (int i = 0; i < o.Length; i++)
                {
                    ReadOnlySpan<byte> r = blob[(i * 0x2c)..];
                    var k = new AurKey { N = BitConverter.ToInt32(r[40..]) };
                    for (int c = 0; c < 10; c++) k.V[c] = BitConverter.ToSingle(r[(c * 4)..]);
                    o[i] = k;
                }
                return o;
            }
            if (f == Form.Lines)
            {
                var o = new AurKey[Math.Max(0, n)];
                for (int i = 0; i < o.Length; i++)
                {
                    if (!NextArrayLine(out ReadOnlySpan<byte> l)) break;
                    AurKey k = ParseKey(l);
                    if (k.N == 5) AxisAngle(ref k, 1);
                    o[i] = k;
                }
                return o;
            }
            var list = new List<AurKey>();
            while (NextArrayLine(out ReadOnlySpan<byte> l2))
            {
                if (Starts(Skip(l2), "endlist")) break;
                AurKey k = ParseKey(l2);
                if (k.N == 5) AxisAngle(ref k, 1);
                list.Add(k);
            }
            return list.ToArray();
        }

        private static Vector3 Flt3(ReadOnlySpan<byte> s) =>
            new(BitConverter.ToSingle(s), BitConverter.ToSingle(s[4..]), BitConverter.ToSingle(s[8..]));

        // ---- closing the file ----

        /// <summary>An animation's nodes are the ones parsed between <c>newanim</c> and <c>doneanim</c>: they
        /// move out of the node list into the animation, keeping only their name and tracks.</summary>
        private void CloseAnim(int firstNode)
        {
            AnimDef a = _anim!;
            a.Events = _events.Count == 0 ? Array.Empty<AnimEvent>() : _events.ToArray();
            _events.Clear();
            int count = _file.Count - firstNode;
            var an = new AnimNode[Math.Max(0, count)];
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < an.Length; i++)
            {
                NodeDef nd = _file[firstNode + i];
                an[i] = new AnimNode { Name = nd.Name, Controllers = nd.Controllers, AnimMesh = nd.AnimMesh };
                byName.TryAdd(nd.Name, i);
            }
            // The animation-to-node binding is parent-scoped (CX-113), so every track carries its parent.
            for (int i = 0; i < an.Length; i++)
            {
                string p = _parentName[firstNode + i];
                an[i].Parent = p.Length != 0 && byName.TryGetValue(p, out int pi) && pi != i ? pi : -1;
            }
            a.Nodes = an;
            _file.RemoveRange(firstNode, count);
            _parentOf.RemoveRange(firstNode, count);
            _parentName.RemoveRange(firstNode, count);
            _anims.Add(a);
            _anim = null;
        }

        /// <summary>Resolve the parent names, then flatten the tree to pre-order, which is the order a
        /// compiled binary stores and therefore the order a node-for-node comparison expects.</summary>
        private void Finish()
        {
            Model.Anims = _anims.ToArray();
            int n = _file.Count;
            if (n == 0)
            {
                // A tile walkmesh may legitimately be EMPTY: a tileset's filler tiles ship a
                // `beginwalkmeshgeom` block with nothing between it and its `endwalkmeshgeom` (TIN01_Z10_01 is
                // 87 bytes whole), and the engine's own CNWTileSurfaceMesh::LoadWalkMesh @0x727cb0 reads that
                // as a surface mesh with no face -- not as a failure, and not as the default walkmesh either
                // (that is only for a resource that will not demand). Refusing it instead made every placement
                // of such a tile read as a MISSING walkmesh. For a .mdl, no nodes is still no model.
                if (!_walkmesh) { Error = "no nodes"; return; }
                Model.Nodes = Array.Empty<NodeDef>();
                Model.NodeIdNext = _nodeCounter;
                return;
            }
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < n; i++) byName.TryAdd(_file[i].Name, i);
            var kids = new List<int>[n];
            for (int i = 0; i < n; i++) kids[i] = new List<int>();
            for (int i = 0; i < n; i++)
            {
                string p = _parentName[i];
                if (p.Length == 0) continue;
                if (!byName.TryGetValue(p, out int pi) || pi == i) continue;
                _parentOf[i] = pi;
                kids[pi].Add(i);
            }
            if (_rootFile < 0)
            {
                for (int i = 0; i < n; i++) if (_parentOf[i] < 0) { _rootFile = i; break; }
                if (_rootFile < 0) _rootFile = 0;
            }

            // MdlNode::ReorderChildren runs on the whole tree once the geometry block closes, and the
            // compiled binary stores the tree in the order it leaves behind -- so it must precede the flatten.
            for (int i = 0; i < n; i++) ReorderKids(kids[i]);

            var order = new List<int>(n);
            var remap = new int[n];
            Array.Fill(remap, -1);
            var work = new Stack<int>();
            Walk(_rootFile, kids, order, remap, work);
            // Any node the tree never reached (an unresolvable `parent`) still exists in the file; the engine
            // would have dereferenced NULL on it, so keeping it is strictly better than dropping it.
            for (int i = 0; i < n; i++) if (remap[i] < 0) { Orphans++; Walk(i, kids, order, remap, work); }

            var nodes = new NodeDef[order.Count];
            for (int i = 0; i < order.Count; i++)
            {
                NodeDef nd = _file[order[i]];
                nd.Index = i;
                nd.Parent = _parentOf[order[i]] < 0 ? -1 : remap[_parentOf[order[i]]];
                List<int> k = kids[order[i]];
                nd.Children = k.Count == 0 ? Array.Empty<int>() : new int[k.Count];
                for (int c = 0; c < k.Count; c++) nd.Children[c] = remap[k[c]];
                nodes[i] = nd;
            }
            Model.Nodes = nodes;
            Model.NodeIdNext = _nodeCounter;
            Model.Refusals = Clamped + Orphans;
            if (Model.Refusals != 0)
                Model.RefusalNote = $"{Clamped} count(s)/depth(s) the file cannot supply, {Orphans} node(s) with an unresolvable parent";
            for (int i = 0; i < nodes.Length; i++) Model.NodeByName.TryAdd(nodes[i].Name, i);
            foreach (var (node, lines) in _weights) ResolveWeights(node, lines);
        }

        /// <summary><c>MdlNode::ReorderChildren</c> @0x458120: when any mesh child carries a transparency hint
        /// of one or more, the whole child list goes through <c>std::sort</c>. Most children compare EQUAL
        /// under the engine's comparator, so the order is whatever libstdc++'s introsort leaves -- not a stable
        /// sort's -- so <c>__introsort_loop</c> @0x4586e0 and <c>__insertion_sort</c> @0x458490 are ported too.</summary>
        private void ReorderKids(List<int> k)
        {
            int n = k.Count;
            if (n < 1) return;
            bool sort = false;
            foreach (int c in k)
                if (_file[c].Mesh is { TransparencyHint: >= 1 }) { sort = true; break; }
            if (!sort) return;
            int[] a = k.ToArray();
            int depth = 2 * (31 - System.Numerics.BitOperations.LeadingZeroCount((uint)n));
            Introsort(a, 0, n, depth);
            // __final_insertion_sort: the whole range at 16 or fewer, else the first sixteen then unguarded.
            if (n <= Threshold) Insertion(a, 0, n);
            else { Insertion(a, 0, Threshold); for (int i = Threshold; i < n; i++) LinearInsert(a, i); }
            for (int i = 0; i < n; i++) k[i] = a[i];
        }

        /// <summary>libstdc++'s <c>_S_threshold</c>: a range of this many or fewer is left to the final
        /// insertion pass.</summary>
        private const int Threshold = 16;

        private void Introsort(int[] a, int first, int last, int depth)
        {
            while (last - first > Threshold)
            {
                // ponytail: the depth-limit heapsort fallback is not ported. It needs 2*floor(log2 n) levels
                // of worst-case pivots, which a comparator this coarse (nearly every pair compares equal) does
                // not produce; the final insertion pass still leaves the range sorted either way.
                if (depth == 0) return;
                depth--;
                int cut = PartitionPivot(a, first, last);
                Introsort(a, cut, last, depth);
                last = cut;
            }
        }

        /// <summary>The engine's comparator <c>FUN_00553470</c>: a non-mesh child sorts before a mesh child,
        /// two meshes compare on transparency hint, and every other pair is equal.</summary>
        private bool Less(int a, int b)
        {
            MeshDef? ma = _file[a].Mesh, mb = _file[b].Mesh;
            if (ma == null && mb == null) return false;
            if (ma != null) return mb != null && ma.TransparencyHint < mb.TransparencyHint;
            return true;
        }

        private int PartitionPivot(int[] a, int first, int last)
        {
            int mid = first + (last - first) / 2;
            MedianToFirst(a, first, first + 1, mid, last - 1);
            return Partition(a, first + 1, last, first);
        }

        /// <summary><c>__move_median_to_first(result, a, b, c)</c>.</summary>
        private void MedianToFirst(int[] arr, int result, int ia, int ib, int ic)
        {
            if (Less(arr[ia], arr[ib]))
            {
                if (Less(arr[ib], arr[ic])) Swap(arr, result, ib);
                else if (Less(arr[ia], arr[ic])) Swap(arr, result, ic);
                else Swap(arr, result, ia);
            }
            else if (Less(arr[ia], arr[ic])) Swap(arr, result, ia);
            else if (Less(arr[ib], arr[ic])) Swap(arr, result, ic);
            else Swap(arr, result, ib);
        }

        private int Partition(int[] a, int first, int last, int pivot)
        {
            while (true)
            {
                while (Less(a[first], a[pivot])) first++;
                last--;
                while (Less(a[pivot], a[last])) last--;
                if (first >= last) return first;
                Swap(a, first, last);
                first++;
            }
        }

        private void Insertion(int[] a, int first, int last)
        {
            if (first == last) return;
            for (int i = first + 1; i < last; i++)
            {
                if (Less(a[i], a[first]))
                {
                    int v = a[i];
                    Array.Copy(a, first, a, first + 1, i - first);
                    a[first] = v;
                }
                else LinearInsert(a, i);
            }
        }

        /// <summary><c>__unguarded_linear_insert</c>: no bound test, because the caller guarantees a sentinel
        /// at or before <c>first</c>.</summary>
        private void LinearInsert(int[] a, int i)
        {
            int v = a[i];
            while (Less(v, a[i - 1])) { a[i] = a[i - 1]; i--; }
            a[i] = v;
        }

        private static void Swap(int[] a, int i, int j) => (a[i], a[j]) = (a[j], a[i]);

        /// <summary>Pre-order flatten with an explicit stack: a hostile file can chain parents hundreds of
        /// thousands deep, and a recursive walk would take the process down with it.</summary>
        private static void Walk(int i, List<int>[] kids, List<int> order, int[] remap, Stack<int> work)
        {
            if (remap[i] >= 0) return;
            work.Clear();
            work.Push(i);
            while (work.Count != 0)
            {
                int at = work.Pop();
                if (remap[at] >= 0) continue;
                remap[at] = order.Count;
                order.Add(at);
                List<int> k = kids[at];
                for (int c = k.Count - 1; c >= 0; c--) work.Push(k[c]);
            }
        }

        /// <summary>Turn each weight line's bone NAMES into slots into the skin's bone table, in first-seen
        /// order, and pack four (slot, weight) pairs per vertex.</summary>
        private void ResolveWeights(NodeDef node, List<WeightLine> lines)
        {
            SkinDef s = node.Skin!;
            var slots = new List<int>();
            var named = new List<string>();
            var slotOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            s.Weights = new Vector4[lines.Count];
            s.Bones = new uint[lines.Count];
            s.BoneIndices = new ushort[lines.Count * 4];
            s.Influences = new int[lines.Count];
            Span<int> b = stackalloc int[4];
            Span<float> v = stackalloc float[4];
            for (int i = 0; i < lines.Count; i++)
            {
                WeightLine w = lines[i];
                string[] names = { w.B0, w.B1, w.B2, w.B3 };
                float[] ws = { w.W0, w.W1, w.W2, w.W3 };
                for (int k = 0; k < 4; k++)
                {
                    if (k >= w.N) { b[k] = 0; v[k] = 0f; continue; }
                    if (!slotOf.TryGetValue(names[k], out int slot))
                    {
                        slot = slots.Count;
                        slotOf[names[k]] = slot;
                        slots.Add(Model.Find(names[k]));
                        named.Add(names[k]);
                    }
                    b[k] = slot;
                    v[k] = ws[k];
                }
                s.Weights[i] = new Vector4(v[0], v[1], v[2], v[3]);
                s.Bones[i] = (uint)((b[0] & 0xFF) | ((b[1] & 0xFF) << 8) | ((b[2] & 0xFF) << 16) | ((b[3] & 0xFF) << 24));
                for (int k = 0; k < 4; k++) s.BoneIndices[i * 4 + k] = (ushort)b[k];
                s.Influences[i] = w.N;
            }
            s.BoneNodes = slots.ToArray();
            // The NAME is the authored fact; the node index is only this file's guess at it. A piece grafted
            // onto a skeleton names bones no node of its own file carries, and Find answers -1 for those.
            s.BoneNames = named.ToArray();
            s.BoneNodesAreMap = false;
        }

    }
}
