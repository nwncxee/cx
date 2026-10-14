using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CX.Alloy.Mdl;

namespace CX.MdlCheck;

/// <summary>CX-112's off-device harness: runs <c>managed/Alloy/Mdl/</c> over the real corpora and reports.
/// See README.md for the invocation.</summary>
internal static class Program
{
    private const ushort MdlResType = 2002;
    /// <summary>CX-118: a tile WALKMESH, the resource that carries the floor a creature's ComputeZValue
    /// cast lands on. Same resref as the tile model, different restype.</summary>
    private const ushort WokResType = 2016;
    private const int CxHeaderBytes = 48;

    private static string Key = Path.Combine(RepoPath("NWN_DIR"), "data/nwn_base.key");
    private static string Ascii = RepoPath("CONTENT_MODELS");
    /// <summary>A per-machine path from <c>tools/paths.py</c> (environment, <c>local.env</c>, then a stock default),
    /// found by walking up from the working directory; empty when unset.</summary>
    private static string RepoPath(string key)
    {
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "tools/paths.py"))) dir = dir.Parent;
        if (dir == null) return "";
        var psi = new ProcessStartInfo("python3", $"tools/paths.py {key}")
        { RedirectStandardOutput = true, WorkingDirectory = dir.FullName };
        using Process? p = Process.Start(psi);
        string v = p?.StandardOutput.ReadToEnd().Trim() ?? "";
        p?.WaitForExit();
        return p?.ExitCode == 0 ? v : "";
    }
    private static int _verbose;
    private static int _limit = int.MaxValue;
    private static string? _only;

    private static int Main(string[] args)
    {
        var modes = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--key": Key = args[++i]; break;
                case "--ascii": Ascii = args[++i]; break;
                case "-v": _verbose++; break;
                case "--limit": _limit = int.Parse(args[++i]); break;
                case "--only": _only = args[++i]; break;
                case "--selfcheck": return SelfCheck();
                default: modes.Add(args[i]); break;
            }
        }
        if (modes.Count == 0) modes.AddRange(["golden", "bifs", "ascii", "fuzz"]);
        Materials();
        int bad = 0;
        foreach (string m in modes)
            bad += m switch
            {
                "bifs" => Bifs(),
                "woks" => Woks(),
                "floors" => Floors(),
                "ascii" => AsciiCorpus(),
                "tree" => Tree(),
                "animpos" => AnimPos(),
                "fuzz" => Fuzz(),
                "golden" => Golden(),
                _ => Fail($"unknown mode '{m}'"),
            };
        Console.WriteLine(bad == 0 ? "mdlcheck: PASS" : $"mdlcheck: FAIL ({bad})");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>One model's node tree as CX reads it: kind, parent, rest transform, controllers and mesh
    /// bounds, for every file in the <c>--ascii</c> directory whose name contains <c>--only</c>.</summary>
    private static int Tree()
    {
        if (_only == null) return Fail("tree needs --only <resref>");
        if (!Directory.Exists(Ascii)) return Fail("no content corpus: set CONTENT_MODELS or pass --ascii <dir>");
        foreach (string f in Directory.EnumerateFiles(Ascii, "*.mdl").Where(p => Path.GetFileName(p).Contains(_only, StringComparison.OrdinalIgnoreCase)))
        {
            ModelDef? m = MdlParser.Parse(File.ReadAllBytes(f), Path.GetFileNameWithoutExtension(f), out string? err);
            if (m == null) { Console.WriteLine($"{f}: {err}"); continue; }
            Console.WriteLine($"== {m.Name} super='{m.SuperName}' ascii={m.FromAscii} nodes={m.Nodes.Length} bounds={m.BMin}..{m.BMax} r={m.Radius} animscale={m.AnimScale} refusals={m.Refusals}");
            foreach (NodeDef n in m.Nodes)
            {
                string ctl = string.Join(",", n.Controllers.Select(c => $"{c.Type}/{c.Columns}{(c.Bezier ? "b" : "")}x{c.Times?.Length ?? 0}"));
                MeshDef? me = n.Mesh;
                string mesh = me == null ? "" : $" mesh v={me.Positions.Length} box={me.BMin}..{me.BMax} tex0='{(me.Textures.Length > 0 ? me.Textures[0] : "")}'";
                Console.WriteLine($"  [{n.Index}] {n.Name} {n.Kind} parent={n.Parent} pos={n.Position} rot={n.Orientation} s={n.Scale} ctl=[{ctl}]{mesh}");
            }
            foreach (AnimDef a in m.Anims)
            {
                Console.WriteLine($"  anim '{a.Name}' len={a.Length} root='{a.AnimRoot}' nodes={a.Nodes.Length}");
                foreach (AnimNode an in a.Nodes)
                    Console.WriteLine($"    {an.Name} parent={an.Parent} " + string.Join(" ", an.Controllers.Select(c => $"{c.Type}/{c.Columns}x{c.Times.Length}[{string.Join(",", c.Values.Take(Math.Min(c.Values.Length, 4)).Select(v => v.ToString("0.###")))}]")));
            }
        }
        return 0;
    }

    /// <summary>Every model under <c>--ascii</c> whose animations key a node's POSITION far from the node's own
    /// geometry position (first key vs rest), plus how many tracks are Bezier: the corpus question "do this
    /// content's animations move parts off their rest place".</summary>
    private static int AnimPos()
    {
        int models = 0, bezModels = 0, farModels = 0;
        var bezCtl = new Dictionary<int, int>();
        foreach (string f in Directory.EnumerateFiles(Ascii, "*.mdl"))
        {
            if (_only != null && !Path.GetFileName(f).Contains(_only, StringComparison.OrdinalIgnoreCase)) continue;
            ModelDef? m;
            try { m = MdlParser.Parse(File.ReadAllBytes(f), Path.GetFileNameWithoutExtension(f), out _); } catch { continue; }
            if (m == null) continue;
            models++;
            bool bez = false; float worst = 0f; string worstAt = "";
            foreach (NodeDef n in m.Nodes)
                foreach (ControllerTrack c in n.Controllers)
                    if (c.Bezier) { bez = true; bezCtl[c.Type] = bezCtl.GetValueOrDefault(c.Type) + 1; }
            foreach (AnimDef a in m.Anims)
                foreach (AnimNode an in a.Nodes)
                {
                    int at = m.Find(an.Name);
                    foreach (ControllerTrack c in an.Controllers)
                    {
                        if (c.Bezier) { bez = true; bezCtl[c.Type] = bezCtl.GetValueOrDefault(c.Type) + 1; }
                        if (at < 0 || c.Type != Ctl.Position || c.Values.Length < 3) continue;
                        var v = new System.Numerics.Vector3(c.Values[0], c.Values[1], c.Values[2]);
                        float d = (v - m.Nodes[at].Position).Length();
                        if (d > worst) { worst = d; worstAt = $"{a.Name}/{an.Name} key0={v} rest={m.Nodes[at].Position}{(c.Bezier ? " bez" : "")}"; }
                    }
                }
            if (bez) bezModels++;
            if (worst > 0.5f) { farModels++; if (_verbose > 0 || farModels <= 40) Console.WriteLine($"  far {m.Name} super='{m.SuperName}' d={worst:F2} {worstAt}"); }
            if (models >= _limit) break;
        }
        Console.WriteLine($"animpos: {models} models, {bezModels} with Bezier tracks ({string.Join(",", bezCtl.Select(kv => $"{kv.Key}:{kv.Value}"))}), {farModels} key a node > 0.5 m off its rest");
        return 0;
    }

    /// <summary>The golden fixture: ONE engine-compiled payload, checked in, parsed and asserted against
    /// recorded values: a regression lock on the binary offsets that fails if the reader's idea of the format
    /// ever drifts.
    /// <para>It is the ENGINE's own serialisation of a stock panel model, 1304 bytes, produced by this build
    /// of the client -- not Beamdog content.</para></summary>
    private static int Golden()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "golden-pnl_chat_prompt.cxmdl");
        if (!File.Exists(path)) path = Path.Combine("tools", "mdlcheck", "fixtures", "golden-pnl_chat_prompt.cxmdl");
        if (!File.Exists(path)) { Console.WriteLine("golden: SKIP (fixture not found)"); return 0; }
        byte[] file = File.ReadAllBytes(path);
        int bad = 0;
        void Want(bool ok, string what) { if (!ok) { bad++; Console.WriteLine($"  golden: {what}"); } }
        Head? h = Header(file, "pnl_chat_prompt");
        Want(h != null, "the header did not validate");
        if (h == null) { Console.WriteLine("golden: FAIL"); return 1; }
        ModelDef? m = MdlParser.Parse(file.AsSpan(CxHeaderBytes, h.Value.Payload), "pnl_chat_prompt", out string? err);
        Want(m != null, $"did not parse ({err})");
        if (m == null) { Console.WriteLine("golden: FAIL"); return 1; }
        Want(m.Refusals == 0, $"{m.Refusals} refusal(s): {m.RefusalNote}");
        Want(!m.FromAscii, "read as ASCII");
        Want(m.Nodes.Length == 2, $"{m.Nodes.Length} nodes, expected 2");
        if (m.Nodes.Length == 2)
        {
            Want(m.Nodes[0].Kind == NodeKind.Dummy && m.Nodes[0].Parent == -1, "node 0 is not the root dummy");
            Want(m.Nodes[1].Kind == NodeKind.TriMesh && m.Nodes[1].Parent == 0, "node 1 is not a parented trimesh");
            MeshDef? me = m.Nodes[1].Mesh;
            Want(me != null, "node 1 has no mesh");
            if (me != null)
            {
                Want(me.Positions.Length == 4, $"{me.Positions.Length} vertices, expected 4");
                Want(me.Faces.Length == 2, $"{me.Faces.Length} faces, expected 2");
                Want(me.Indices.Length == 6, $"{me.Indices.Length} indices, expected 6");
                Want(me.Uv[0].Length == 4, $"uv0 has {me.Uv[0].Length}, expected 4");
                Want(me.Normals.Length == 4, $"{me.Normals.Length} normals, expected 4");
            }
        }
        Console.WriteLine(bad == 0
            ? $"golden: PASS ({file.Length} bytes, {m.Nodes.Length} nodes, class {m.Classification})"
            : $"golden: FAIL ({bad})");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>Mutate real models and demand a refusal, never a throw, a hang or an allocation past a
    /// bound. The corpus is the interesting input: truncations and bit flips of files that ARE models find
    /// the paths a hand-written case does not reach.</summary>
    private static int Fuzz()
    {
        var seeds = new List<byte[]>();
        if (File.Exists(Key))
        {
            using var kb = new KeyBif(Key);
            foreach ((string Name, ushort Type) r in kb.Resources)
            {
                if (r.Type != MdlResType || seeds.Count >= 120) continue;
                byte[]? d = kb.Read(r.Name, MdlResType);
                if (d != null && d.Length > 64 && d.Length < (4 << 20)) seeds.Add(d);
            }
        }
        if (Directory.Exists(Ascii))
            foreach (string f in Directory.EnumerateFiles(Ascii, "*.mdl", SearchOption.AllDirectories))
            {
                if (seeds.Count >= 240) break;
                try { byte[] d = File.ReadAllBytes(f); if (d.Length > 64 && d.Length < (4 << 20)) seeds.Add(d); }
                catch { }
            }
        if (seeds.Count == 0) { Console.WriteLine("fuzz: SKIP (no corpus)"); return 0; }

        const long MaxBytes = 192L << 20;
        const int MaxMs = 4000;
        int cases = 0, threw = 0, slow = 0, fat = 0, parsed = 0, refused = 0;
        var rng = new Random(20261001);
        var sw = Stopwatch.StartNew();
        string worst = "";
        long worstMs = 0;
        for (int round = 0; round < 60; round++)
            foreach (byte[] seed in seeds)
            {
                byte[] m = seed[..(1 + rng.Next(seed.Length))];
                for (int k = rng.Next(1, 6); k > 0; k--) m[rng.Next(m.Length)] ^= (byte)(1 << rng.Next(8));
                cases++;
                long before = GC.GetAllocatedBytesForCurrentThread();
                long t0 = sw.ElapsedMilliseconds;
                try
                {
                    if (MdlParser.Parse(m, "fuzz", out _) != null) parsed++; else refused++;
                }
                catch (Exception e)
                {
                    threw++;
                    if (threw <= 5) Console.WriteLine($"  THREW {e.GetType().Name}: {e.Message}");
                    continue;
                }
                long ms = sw.ElapsedMilliseconds - t0;
                long used = GC.GetAllocatedBytesForCurrentThread() - before;
                if (ms > worstMs) { worstMs = ms; worst = $"{m.Length} bytes"; }
                if (ms > MaxMs) { slow++; if (slow <= 5) Console.WriteLine($"  SLOW {ms} ms on {m.Length} bytes"); }
                if (used > MaxBytes) { fat++; if (fat <= 5) Console.WriteLine($"  FAT {used >> 20} MiB on {m.Length} bytes"); }
            }
        sw.Stop();
        Console.WriteLine($"fuzz: {cases} mutations of {seeds.Count} real models in {sw.ElapsedMilliseconds} ms "
                          + $"({parsed} parsed, {refused} refused, {threw} threw, {slow} over {MaxMs} ms, "
                          + $"{fat} over {MaxBytes >> 20} MiB; worst {worstMs} ms on {worst})");
        return threw == 0 && slow == 0 && fat == 0 ? 0 : 1;
    }

    /// <summary>Wire the parser's material-hint seam to the corpus's own <c>.mtr</c> files, so a mesh that
    /// names a normal-mapped material gets its tangent frame exactly as the engine's
    /// <c>SharedMaterial::GetSharedMaterial</c> makes it.</summary>
    private static void Materials()
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(Ascii))
            foreach (string f in Directory.EnumerateFiles(Ascii, "*.mtr", SearchOption.AllDirectories))
                byName[Path.GetFileNameWithoutExtension(f)] = f;
        var cache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"materials: {byName.Count} .mtr files");
        MdlParser.MaterialHint = name =>
        {
            lock (cache)
            {
                if (cache.TryGetValue(name, out int v)) return v;
                v = 0;
                if (byName.TryGetValue(name, out string? path))
                    foreach (string line in File.ReadLines(path))
                    {
                        string t = line.Trim();
                        if (!t.StartsWith("renderhint", StringComparison.OrdinalIgnoreCase)) continue;
                        string w = t[10..].Trim().ToLowerInvariant();
                        v = w switch { "normalandspecmapped" => 2, "normaltangents" => 3, "none" => 1, _ => 0 };
                        break;
                    }
                cache[name] = v;
                return v;
            }
        };
    }

    private static int Fail(string why) { Console.Error.WriteLine($"mdlcheck: {why}"); return 1; }

    private static int SelfCheck()
    {
        var sb = new StringBuilder();
        int bad = MdlSelfCheck.Run((ok, why) => sb.AppendLine("  " + why));
        Console.Write(sb);
        Console.WriteLine($"selfcheck: {(bad == 0 ? "PASS" : $"FAIL ({bad})")}");
        return bad == 0 ? 0 : 1;
    }

    // ---- (a) every model in the base game BIFs ----

    /// <summary>Per-node geometry for a narrowed sweep (-v -v): the model's source form, then each mesh
    /// node's texture and the rectangle of it the node's own UVs cover. A GUI box whose quad asks for a
    /// hundredth of its atlas draws as a smear whatever the material does, and this is where that is visible
    /// without a client.</summary>
    private static void DumpNodes(ModelDef m)
    {
        Console.WriteLine($"  {m.Name}: {m.Nodes.Length} node(s), {(m.FromAscii ? "ASCII" : "binary")} source");
        foreach (NodeDef nd in m.Nodes)
        {
            MeshDef? me = nd.Mesh;
            if (me == null) { Console.WriteLine($"    {nd.Name} [{nd.Kind}]"); continue; }
            string uv = "uv0=none";
            if (me.Uv[0].Length > 0)
            {
                float u0 = me.Uv[0][0].X, u1 = u0, v0 = me.Uv[0][0].Y, v1 = v0;
                foreach (System.Numerics.Vector2 q in me.Uv[0])
                {
                    u0 = MathF.Min(u0, q.X); u1 = MathF.Max(u1, q.X);
                    v0 = MathF.Min(v0, q.Y); v1 = MathF.Max(v1, q.Y);
                }
                uv = $"uv0=({u0:F4}..{u1:F4},{v0:F4}..{v1:F4})";
            }
            Console.WriteLine($"    {nd.Name} [{nd.Kind}] v{me.Positions.Length} f{me.Faces.Length} "
                              + $"tex=\"{me.Textures[0]}\" sets={me.UvSetCount} {uv}");
        }
    }

    private static int Bifs() => Bifs(MdlResType, "bifs");

    /// <summary>CX-118: the same sweep over the tile walkmeshes (restype 2016). A .wok is an ASCII model
    /// whose geometry block is `beginwalkmeshgeom`, holding one `node aabb` -- and until CX parsed them, DB
    /// mode had a floor only on the handful of tiles whose .mdl happened to embed an aabb node.</summary>
    private static int Woks() => Bifs(WokResType, "woks");

    /// <summary>Where a tile's floor comes from, tile by tile: the aabb node in its own model, its .wok, or
    /// nowhere. This is the census behind TileBuild.Walkmesh -- CX takes the model's aabb when it has one and
    /// loads the .wok otherwise, so "no aabb and an empty .wok" is a tile with no floor BY DESIGN (a
    /// tileset's filler), and "no aabb and no readable .wok" would be a hole.</summary>
    private static int Floors()
    {
        if (!File.Exists(Key)) { Console.WriteLine("floors: SKIP (no key)"); return 0; }
        using var kb = new KeyBif(Key);
        List<string> names = kb.Resources.Where(r => r.Type == MdlResType && r.Name.StartsWith("t", StringComparison.OrdinalIgnoreCase))
                               .Select(r => r.Name).ToList();
        if (_only != null) names = names.Where(n => n.Contains(_only, StringComparison.OrdinalIgnoreCase)).ToList();
        int model = 0, fromWok = 0, emptyWok = 0, noWok = 0, badWok = 0, notATile = 0;
        foreach (string n in names)
        {
            byte[]? d = kb.Read(n, MdlResType);
            ModelDef? m = d == null ? null : MdlParser.Parse(d, n, out _);
            if (m == null) { notATile++; continue; }
            bool aabb = false;
            foreach (NodeDef nd in m.Nodes) aabb |= nd.Kind == NodeKind.Aabb;
            if (aabb) { model++; continue; }
            byte[]? w = kb.Read(n, WokResType);
            if (w == null) { noWok++; if (_verbose > 0) Console.WriteLine($"  {n}: no aabb and NO .wok"); continue; }
            ModelDef? wm = MdlParser.Parse(w, n, out string? err);
            if (wm == null) { badWok++; Console.WriteLine($"  {n}: .wok refused -- {err}"); continue; }
            bool wokAabb = false;
            foreach (NodeDef nd in wm.Nodes) wokAabb |= nd.Kind == NodeKind.Aabb && nd.Aabb != null && nd.Aabb.Tree.Length > 0;
            if (wokAabb) { fromWok++; if (_verbose > 0) Console.WriteLine($"  {n}: floor from its .wok"); }
            else emptyWok++;
        }
        Console.WriteLine($"floors: {model} from the model's aabb, {fromWok} from a .wok, {emptyWok} empty .wok (no floor by design), "
                          + $"{noWok} with neither, {badWok} UNREADABLE .wok, {notATile} unparsed");
        return badWok == 0 ? 0 : 1;
    }

    private static int Bifs(ushort resType, string label)
    {
        if (!File.Exists(Key)) { Console.WriteLine($"{label}: SKIP (no {Key})"); return 0; }
        using var kb = new KeyBif(Key);
        List<string> names = kb.Resources.Where(r => r.Type == resType).Select(r => r.Name).ToList();
        if (_only != null) names = names.Where(n => n.Contains(_only, StringComparison.OrdinalIgnoreCase)).ToList();
        if (names.Count > _limit) names = names.Take(_limit).ToList();

        long bytes = 0, nodes = 0, verts = 0;
        int ok = 0, failed = 0, withAabb = 0, empty = 0;
        var why = new ConcurrentDictionary<string, int>();
        var sw = Stopwatch.StartNew();
        // One reader (the container is a shared FileStream) then parse in parallel: the read is not what is
        // being measured.
        var blobs = new List<(string Name, byte[] Data)>(names.Count);
        foreach (string n in names)
        {
            byte[]? d = kb.Read(n, resType);
            if (d != null && d.Length > 12) blobs.Add((n, d));
        }
        long readMs = sw.ElapsedMilliseconds;
        sw.Restart();
        object gate = new();
        Parallel.ForEach(blobs, b =>
        {
            ModelDef? m = MdlParser.Parse(b.Data, b.Name, out string? err);
            lock (gate)
            {
                bytes += b.Data.Length;
                if (m == null) { failed++; why.AddOrUpdate(err ?? "?", 1, (_, v) => v + 1); if (_verbose > 0) Console.WriteLine($"  {b.Name}: {err}"); }
                else
                {
                    ok++; nodes += m.Nodes.Length;
                    bool aabb = false;
                    foreach (NodeDef nd in m.Nodes) { verts += nd.Mesh?.Positions.Length ?? 0; aabb |= nd.Kind == NodeKind.Aabb; }
                    if (aabb) withAabb++; else if (m.Nodes.Length == 0) empty++;
                    // -v -v on a narrowed sweep is the off-device answer to "what does this model actually
                    // ask the atlas for": the source form and, per mesh node, its texture and UV rectangle.
                    if (_verbose > 1) DumpNodes(m);
                }
            }
        });
        sw.Stop();
        double mb = bytes / (1024.0 * 1024.0);
        Console.WriteLine($"  refusals: {MdlPost.OutOfRange} out-of-range index/indices, "
                          + $"{MdlParser.MaterialHintMisses} unanswered material hints");
        Console.WriteLine($"  {withAabb} of {ok} carry an aabb node, {empty} are empty");
        Console.WriteLine($"{label}: {ok} parsed, {failed} failed, {nodes} nodes, {verts} vertices; "
                          + $"{mb:F1} MiB in {sw.Elapsed.TotalMilliseconds:F0} ms wall ({mb / Math.Max(0.001, sw.Elapsed.TotalSeconds):F0} MiB/s, read {readMs} ms)");
        foreach (var kv in why.OrderByDescending(k => k.Value).Take(8)) Console.WriteLine($"  {kv.Value,6} x {kv.Key}");
        return failed == 0 ? 0 : 1;
    }

    // ---- (b) the ASCII corpus ----

    private static int AsciiCorpus()
    {
        if (!Directory.Exists(Ascii)) { Console.WriteLine($"ascii: SKIP (no corpus at '{Ascii}': set CONTENT_MODELS)"); return 0; }
        List<string> files = Directory.EnumerateFiles(Ascii, "*.mdl", SearchOption.AllDirectories).ToList();
        if (_only != null) files = files.Where(f => Path.GetFileName(f).Contains(_only, StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count > _limit) files = files.Take(_limit).ToList();
        long bytes = 0, nodes = 0, verts = 0;
        int ok = 0, failed = 0, binary = 0, refusals = 0, clamped = 0;
        var why = new ConcurrentDictionary<string, int>();
        object gate = new();
        var sw = Stopwatch.StartNew();
        Parallel.ForEach(files, f =>
        {
            byte[] d;
            try { d = File.ReadAllBytes(f); } catch { return; }
            string name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            ModelDef? m = MdlParser.Parse(d, name, out string? err);
            lock (gate)
            {
                bytes += d.Length;
                if (d.Length >= 4 && BitConverter.ToUInt32(d) == 0) binary++;
                if (m == null) { failed++; why.AddOrUpdate(err ?? "?", 1, (_, v) => v + 1); if (_verbose > 0) Console.WriteLine($"  {name}: {err}"); }
                else
                {
                    ok++;
                    nodes += m.Nodes.Length;
                    foreach (NodeDef nd in m.Nodes) verts += nd.Mesh?.Positions.Length ?? 0;
                    if (m.Refusals != 0) { refusals++; clamped += m.Refusals; }
                }
            }
        });
        sw.Stop();
        double mb = bytes / (1024.0 * 1024.0);
        Console.WriteLine($"  refusals: {MdlPost.OutOfRange} out-of-range index/indices, "
                          + $"{refusals} model(s) with a refusal, {clamped} clamped count(s)");
        Console.WriteLine($"ascii: {ok} parsed, {failed} failed ({binary} of them binary files in the corpus), {nodes} nodes, {verts} vertices; "
                          + $"{mb:F1} MiB in {sw.Elapsed.TotalMilliseconds:F0} ms wall ({mb / Math.Max(0.001, sw.Elapsed.TotalSeconds):F0} MiB/s)");
        foreach (var kv in why.OrderByDescending(k => k.Value).Take(8)) Console.WriteLine($"  {kv.Value,6} x {kv.Key}");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>The golden fixture's 48-byte header: magic, format version, the client build key, the source
    /// hash, the source length, the payload length and the resref.</summary>
    private readonly record struct Head(ulong Build, ulong Hash, int SrcLen, int Payload, string ResRef);

    private const uint CxMagic = 0x444D5843;      // "CXMD"
    private const int CxVersion = 1;

    private static Head? Header(byte[] file, string resref)
    {
        if (file.Length < CxHeaderBytes) return null;
        if (BitConverter.ToUInt32(file) != CxMagic || BitConverter.ToInt32(file, 4) != CxVersion) return null;
        int payload = BitConverter.ToInt32(file, 28);
        if (payload < 12 || CxHeaderBytes + payload != file.Length) return null;
        var sb = new StringBuilder();
        for (int i = 0; i < 16 && file[32 + i] != 0; i++) sb.Append((char)file[32 + i]);
        if (!sb.ToString().Equals(resref, StringComparison.OrdinalIgnoreCase)) return null;
        return new Head(BitConverter.ToUInt64(file, 8), BitConverter.ToUInt64(file, 16),
                        BitConverter.ToInt32(file, 24), payload, sb.ToString());
    }
}
