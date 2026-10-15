using System;
using System.Collections.Generic;
using CX.Framework;

namespace CX.Alloy.Mdl;

/// <summary>The off-device suite: a hand-built supermodel chain, a cycle, a missing supermodel, and the
/// refcount / resident-set / binding / census contracts. No engine, no GL, no files -- the whole point of the
/// <see cref="IModelSource"/> seam is that the cache can be driven from a dictionary.</summary>
public static partial class ModelCache
{
    /// <summary>A dictionary of hand-built models, standing in for the resource chain.</summary>
    private sealed class Fixture : IModelSource
    {
        internal readonly Dictionary<string, ModelDef> Models = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Names <see cref="Locate"/> pretends not to have, so a TRANSIENT miss can be staged.</summary>
        internal readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Names <see cref="StillValid"/> vetoes at the publish.</summary>
        internal readonly HashSet<string> Moved = new(StringComparer.OrdinalIgnoreCase);
        internal int Parses;

        public object? Locate(string resref, out string? why)
        {
            bool have = Models.ContainsKey(resref) && !Hidden.Contains(resref);
            why = have ? null : "not in the fixture";
            return have ? resref : null;
        }

        public ModelDef? Parse(object token, out string? why)
        {
            why = null;
            Parses++;
            return Models[(string)token];
        }

        public bool StillValid(object token, out string? why)
        {
            if (!Moved.Contains((string)token)) { why = null; return true; }
            why = "the fixture moved it under the read";
            return false;
        }
    }

    /// <summary>A node with a name, a parent and the children the caller wires afterwards.</summary>
    private static ModelDef Fix(string name, string super, float scale, string[] anims,
                                string[]? nodes = null, int[]? parents = null)
    {
        // CX-112: a fixture stands in for an ASCII-sourced model, which is the only kind the engine re-keys
        // (`beginmodelgeom` + `SynchronizeTree` are the ASCII interpreter's; the binary loader calls
        // neither). FileIndex and NodeIdNext are what the parse would have counted.
        var m = new ModelDef { Name = name, SuperName = super, AnimScale = scale, FromAscii = true };
        nodes ??= ["root"];
        parents ??= [-1];
        m.Nodes = new NodeDef[nodes.Length];
        var kids = new List<int>[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            m.Nodes[i] = new NodeDef { Name = nodes[i], Index = i, Parent = parents[i], FileIndex = i };
            m.NodeByName[nodes[i]] = i;
            kids[i] = new List<int>();
        }
        for (int i = 0; i < nodes.Length; i++) if (parents[i] >= 0) kids[parents[i]].Add(i);
        for (int i = 0; i < nodes.Length; i++) m.Nodes[i].Children = kids[i].ToArray();
        m.NodeIdNext = nodes.Length;
        m.Anims = new AnimDef[anims.Length];
        for (int i = 0; i < anims.Length; i++) m.Anims[i] = new AnimDef { Name = anims[i], Length = 1f };
        return m;
    }

    private static AnimDef FixAnim(string name, string[] nodes, int[] parents)
    {
        var a = new AnimDef { Name = name, Length = 1f };
        a.Nodes = new AnimNode[nodes.Length];
        for (int i = 0; i < nodes.Length; i++) a.Nodes[i] = new AnimNode { Name = nodes[i], Parent = parents[i] };
        return a;
    }

    /// <summary>The suite. Leaves the cache empty and sourceless, so a session's own
    /// <see cref="SetSource"/> is unaffected.</summary>
    public static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "ModelCache.SelfCheck");
        IModelSource? had = _source;
        long? hadBudget = BudgetOverride;
        Action<ModelDef>? hadEvict = Evicting;
        try { Run(check, log, hadEvict); }
        catch (Exception e) { check.Expect(false, $"threw -- {e}"); }
        finally
        {
            Clear();
            ResetCensus();
            ResetCounters();
            Evicting = hadEvict;
            BudgetOverride = hadBudget;
            _source = had;
        }
        if (check.Ok) log.Info("ModelCache.SelfCheck: passed");
        return check.Ok;
    }

    private static void Run(Checker check, ExtenderLog log, Action<ModelDef>? hadEvict)
    {
        // ---- the name gate FindModel @0x44c2a0 applies before it looks anything up ----
        Clear(); ResetCensus(); ResetCounters();
        BudgetOverride = long.MaxValue;
        _source = null;
        check.Expect(FindModel(null) == null && FindModel("") == null && FindModel("NULL") == null
                     && FindModel("null") == null && FindModel("nUlL") == null,
                     "the FindModel name gate let a null name through");
        check.Expect(Acquire("NULL") == null && Acquire("") == null, "Acquire took a null name");
        check.Expect(Misses == 0 && Parsed == 0, "a refused name still counted as a miss");

        // ---- strcasecmp, not OrdinalIgnoreCase ----
        check.Expect(Strcasecmp("A_b", "a_B") == 0, "strcasecmp is case sensitive");
        check.Expect(Strcasecmp("ab", "abc") < 0 && Strcasecmp("abc", "ab") > 0, "strcasecmp mis-orders a prefix");
        check.Expect(Strcasecmp("a_b", "ab") < 0,
                     "strcasecmp must LOWER-case: '_' (0x5f) sorts below the lower-case letters");
        check.Expect(string.Compare("a_b", "ab", StringComparison.OrdinalIgnoreCase) > 0,
                     ".NET's OrdinalIgnoreCase no longer disagrees, so this check proves nothing");

        // ---- the sorted table: BinarySearchModel / InsertModel ----
        var fx = new Fixture();
        _source = fx;
        string[] names = ["c_wolf", "a_ba", "zzz", "c_badger", "a_fa", "m_mid"];
        foreach (string n in names) fx.Models[n] = Fix(n, "", 1f, []);
        foreach (string n in names) check.Expect(Acquire(n) != null, $"{n} did not load");
        bool sorted = true;
        for (int i = 1; i < _table.Count; i++) if (Strcasecmp(_table[i - 1].Def.Name, _table[i].Def.Name) >= 0) sorted = false;
        check.Expect(sorted, "the model table is not sorted by the engine's key");
        check.Expect(_table.Count == names.Length, $"the table holds {_table.Count} of {names.Length} models");
        foreach (string n in names)
            check.Expect(BinarySearchModel(n.ToUpperInvariant(), 0, _table.Count - 1) >= 0,
                         $"{n} is not findable case-insensitively");
        check.Expect(BinarySearchModel("no_such", 0, _table.Count - 1) < 0, "the search found a model that is not there");
        check.Expect(Parsed == names.Length && Misses == names.Length, "the cold loads were not all misses");
        long parsedCold = Parsed;
        foreach (string n in names) Release(Acquire(n)!);
        check.Expect(Parsed == parsedCold, "a resident model was parsed again");
        check.Expect(Hits == names.Length, $"the warm loads counted {Hits} hits, not {names.Length}");

        // ---- the module edge: nothing read for the previous module answers a lookup ----
        Clear(); ResetCounters();
        fx = new Fixture();
        _source = fx;
        fx.Models["held"] = Fix("held", "", 1f, []);
        fx.Models["idle"] = Fix("idle", "", 1f, []);
        ModelDef held = Acquire("held")!;
        Release(Acquire("idle")!);
        var (edgeEvicted, edgeHeld) = ModuleEdge();
        check.Expect(edgeEvicted == 1 && edgeHeld == 1 && FindModel("held") == null && FindModel("idle") == null,
                     "a model from the previous module still answered a lookup after the module edge");
        ModelDef serverHeld = Fix("held", "", 1f, []);
        fx.Models["held"] = serverHeld;   // the next module's own file of the same name
        ModelDef? again = Acquire("held");
        check.Expect(ReferenceEquals(again, serverHeld), "the next module's acquire did not read its own file");
        Release(held);
        check.Expect(!_bySlot.ContainsKey(held), "a model held across the edge was not evicted at its last release");
        if (again != null) Release(again);

        // ---- the supermodel chain ----
        Clear(); ResetCounters();
        fx = new Fixture();
        _source = fx;
        fx.Models["base"] = Fix("base", "", 2f, ["walk", "default"]);
        fx.Models["mid"] = Fix("mid", "base", 3f, ["run"]);
        fx.Models["top"] = Fix("top", "mid", 5f, ["walk"]);
        fx.Models["selfy"] = Fix("selfy", "selfy", 1f, ["spin"]);
        fx.Models["cyca"] = Fix("cyca", "cycb", 1f, ["a"]);
        fx.Models["cycb"] = Fix("cycb", "cyca", 1f, ["b"]);
        fx.Models["orphan"] = Fix("orphan", "nosuch", 1f, ["o"]);

        ModelDef top = Acquire("top")!;
        ModelDef mid = FindModel("mid")!, bas = FindModel("base")!;
        check.Expect(ReferenceEquals(top.Super, mid) && ReferenceEquals(mid.Super, bas) && bas.Super == null,
                     "the supermodel chain did not resolve");
        check.Expect(DepthOf(top) == 2 && DepthOf(mid) == 1 && DepthOf(bas) == 0,
                     $"chain depths are {DepthOf(top)}/{DepthOf(mid)}/{DepthOf(bas)}, not 2/1/0");
        check.Expect(RefsOf(mid) == 1 && RefsOf(bas) == 1,
                     "a model does not hold the reference on its supermodel that setsupermodel takes");

        // ---- FindAnimation: first owner up the chain, then "default" at the TERMINAL model only ----
        AnimDef? a = FindAnimation(top, "WALK", out ModelDef? owner);
        check.Expect(a != null && ReferenceEquals(owner, top), "the model's own animation did not win");
        check.Expect(FindAnimScale(top, a) == 1f, "an animation a model owns itself must not be scaled");
        a = FindAnimation(top, "run", out owner);
        check.Expect(a != null && ReferenceEquals(owner, mid), "the supermodel's animation was not found");
        check.Expect(FindAnimScale(top, a) == 5f, $"the scale through one level is {FindAnimScale(top, a)}, not 5");
        a = FindAnimation(mid, "walk", out owner);
        check.Expect(a != null && ReferenceEquals(owner, bas), "the chain walk stopped short");
        check.Expect(FindAnimScale(mid, a) == 3f, "the scale through one level from mid is wrong");
        a = FindAnimation(top, "sit", out owner);
        check.Expect(a != null && a.Name == "default" && ReferenceEquals(owner, bas),
                     "an unknown animation did not fall back to 'default' at the end of the chain");
        check.Expect(MathF.Abs(FindAnimScale(top, a) - 15f) < 1e-5f,
                     $"the fallback's scale is {FindAnimScale(top, a)}, not 5*3");
        // The engine guards only a null pointer, so an EMPTY name walks the chain to the terminal "default".
        check.Expect(FindAnimation(top, "", out ModelDef? emptyOwner)?.Name == "default"
                     && ReferenceEquals(emptyOwner, bas),
                     "an empty animation name must behave like any other unknown one, as the engine's does");

        // An animation that belongs to no model in the chain: the engine answers the whole product, not a
        // refusal (FindAnimScale @0x499b10 falls out of its loop with the accumulated scale).
        check.Expect(MathF.Abs(FindAnimScale(top, new AnimDef { Name = "stranger" }) - 30f) < 1e-4f,
                     "an animation outside the chain did not get the whole product");

        // ---- the memo, against the chain written out by hand: top owns walk unscaled, mid owns run one
        // level up (x5), and every other name lands on base's terminal "default" (x5 x3).
        long rh = ResolveHits, rm = ResolveMisses;
        (string Ask, string Anim, ModelDef Owner, float Scale)[] want =
        [
            ("walk", "walk", top, 1f),
            ("run", "run", mid, 5f),
            ("sit", "default", bas, 15f),
            ("default", "default", bas, 15f),
            ("nothing_like_it", "default", bas, 15f),
        ];
        foreach (var w in want)
        {
            AnimLookup got = Resolve(top, w.Ask);
            check.Expect(got.Anim?.Name == w.Anim && ReferenceEquals(got.Owner, w.Owner)
                         && MathF.Abs(got.Scale - w.Scale) < 1e-5f,
                         $"the memo answers '{w.Ask}' with {got.Anim?.Name ?? "nothing"} at x{got.Scale}, not {w.Anim} at x{w.Scale}");
            check.Expect(ReferenceEquals(Resolve(top, w.Ask.ToUpperInvariant()).Anim, got.Anim),
                         $"the memo is case sensitive for '{w.Ask}'");
        }
        check.Expect(ResolveMisses == rm + 5 && ResolveHits == rh + 5,
                     $"the memo resolved {ResolveMisses - rm} times and hit {ResolveHits - rh}, not 5 and 5");

        // ---- cycles and a missing supermodel ----
        long cycles = Cycles;
        ModelDef selfy = Acquire("selfy")!;
        check.Expect(selfy.Super == null && Cycles == cycles + 1, "a self-supermodel was not cut and counted");
        check.Expect(FindAnimation(selfy, "nope", out _) == null, "the cut self-cycle still has a chain");
        cycles = Cycles;
        ModelDef cyca = Acquire("cyca")!;
        check.Expect(Cycles == cycles + 1, "a two-model supermodel cycle was not counted");
        int hops = 0;
        for (ModelDef? m = cyca; m != null && hops < 100; m = m.Super) hops++;
        check.Expect(hops < 100, "the cut cycle is still walkable forever");
        check.Expect(FindAnimation(cyca, "b", out _) != null || FindAnimation(cyca, "a", out _) != null,
                     "the cut cycle lost both animations");
        long missing = MissingSupers;
        ModelDef orphan = Acquire("orphan")!;
        check.Expect(orphan.Super == null && MissingSupers == missing + 1,
                     "a missing supermodel was not counted");
        check.Expect(FindAnimation(orphan, "o", out _) != null, "a model with no supermodel lost its own animations");

        // ---- filed under the resref it was ASKED for, not the file's internal name ----
        Clear(); ResetCounters(); ResetCensus();
        fx = new Fixture();
        _source = fx;
        BudgetOverride = long.MaxValue;
        fx.Models["asked_as"] = Fix("something_else", "", 1f, ["wag"]);
        ModelDef odd = Acquire("asked_as")!;
        check.Expect(odd.Name == "asked_as", $"the model was filed as {odd.Name}, not under the requested resref");
        check.Expect(Renamed == 1, "a model file whose internal name differs was not counted");
        long parses = fx.Parses;
        check.Expect(ReferenceEquals(Acquire("asked_as"), odd) && fx.Parses == parses,
                     "the second acquire of a renamed model re-parsed it, which is the bug the filing avoids");

        // ---- the publish re-proves the source's token, and drops the model if it moved ----
        fx.Models["moved"] = Fix("moved", "", 1f, []);
        fx.Moved.Add("moved");
        check.Expect(Acquire("moved") == null && StaleResources == 1,
                     "a resource that moved under the read was filed anyway");
        fx.Moved.Clear();
        check.Expect(Acquire("moved") != null, "the model stayed refused after the move was over");

        // ---- a parse in flight across a source change is dropped, not filed ----
        Clear(); ResetCounters();
        fx = new Fixture();
        _source = fx;
        fx.Models["crossing"] = Fix("crossing", "", 1f, []);
        Prefetch("crossing");
        for (int i = 0; i < 1000 && Finished == 0; i++) BlockingWait.Sleep(1, "self-check: wait for the loader thread");
        _sourceGen++;                                 // stand in for a SetSource between the parse and the pump
        Pump();
        check.Expect(FindModel("crossing") == null && StaleSources == 1,
                     "a parse from a replaced source was filed instead of dropped");

        // ---- a transient supermodel miss does not latch the chain ----
        Clear(); ResetCounters();
        fx = new Fixture();
        _source = fx;
        fx.Models["childy"] = Fix("childy", "parenty", 1f, ["c"]);
        fx.Models["parenty"] = Fix("parenty", "", 2f, ["p"]);
        fx.Hidden.Add("parenty");
        ModelDef childy = Acquire("childy")!;
        check.Expect(childy.Super == null && MissingSupers == 1, "the hidden supermodel was not reported");
        Release(childy);
        fx.Hidden.Clear();
        childy = Acquire("childy")!;
        check.Expect(childy.Super != null && childy.Super.Name == "parenty",
                     "a chain latched on a TRANSIENT miss: the model is animation-less for the session");
        check.Expect(FindAnimation(childy, "p", out _) != null, "the retried chain did not bring its animations");
        // ...but not forever: past MaxChainTries it settles rather than probing on every acquire.
        Clear(); ResetCounters();
        fx = new Fixture();
        _source = fx;
        fx.Models["lonely"] = Fix("lonely", "never_there", 1f, ["l"]);
        ModelDef lonely = Acquire("lonely")!;
        for (int i = 0; i < MaxChainTries + 3; i++) { Release(lonely); lonely = Acquire("lonely")!; }
        check.Expect(MissingSupers == MaxChainTries,
                     $"the chain retried {MissingSupers} times, not {MaxChainTries}");

        // ---- one log budget PER CAUSE ----
        Clear(); ResetCounters();
        _source = null;
        ExtenderLog? quiet = _log;
        _log = null;        // the counting is what is under test; the lines are not cx.log's business
        try
        {
            for (int i = 0; i < LogCapPerCause + 5; i++) Say("cause-a", "cause A");
            for (int i = 0; i < LogCapPerCause + 5; i++) Say("cause-b", "cause B");
        }
        finally { _log = quiet; }
        check.Expect(_said["cause-a"] == LogCapPerCause && _said["cause-b"] == LogCapPerCause,
                     "one cause spent another cause's log budget");

        // ---- the chain resolve re-keys node numbers off the supermodel (MdlPost.Resync) ----
        Clear(); ResetCounters();
        fx = new Fixture();
        _source = fx;
        // Every model has the same three node names; only the numbering differs, so a resync is visible.
        ModelDef gran = Fix("gran", "", 1f, [], ["root", "a", "b"], [-1, 0, 0]);
        ModelDef par = Fix("par", "gran", 1f, [], ["root", "b", "a"], [-1, 0, 0]);
        ModelDef kid2 = Fix("kid2", "par", 1f, [], ["root", "a", "zz"], [-1, 0, 0]);
        for (int i = 0; i < 3; i++) { gran.Nodes[i].PartNumber = i; par.Nodes[i].PartNumber = 100 + i; kid2.Nodes[i].PartNumber = 200 + i; }
        fx.Models["gran"] = gran; fx.Models["par"] = par; fx.Models["kid2"] = kid2;
        ModelDef k2 = Acquire("kid2")!;
        check.Expect(gran.NodeIdsSynced == false, "the chain ROOT was resynced; it has nothing to resync against");
        check.Expect(par.NodeIdsSynced && k2.NodeIdsSynced, "the chain resolve did not resync the node numbers");
        check.Expect(Resynced == 2, $"{Resynced} models were resynced, not 2");
        // par's "a" (index 2) takes gran's "a" (number 1); par's "b" (index 1) takes gran's "b" (number 2).
        check.Expect(par.Nodes[2].PartNumber == 1 && par.Nodes[1].PartNumber == 2,
                     $"par resynced to [{par.Nodes[0].PartNumber},{par.Nodes[1].PartNumber},{par.Nodes[2].PartNumber}]");
        // ...and kid2 takes PAR's already-resynced numbers, which is what makes the order bottom-up: its "a"
        // ends up with the chain ROOT's 1, not par's original 102.
        check.Expect(k2.Nodes[1].PartNumber == 1, $"kid2's 'a' is {k2.Nodes[1].PartNumber}, not the chain root's 1");
        check.Expect(k2.Nodes[2].PartNumber == -1, "a node the supermodel does not have must take -1");
        long resynced = Resynced;
        Release(k2);
        check.Expect(ReferenceEquals(Acquire("kid2"), k2) && Resynced == resynced,
                     "a model already synced was resynced again");

        // ---- the node binding: hierarchical, parent-scoped, by name ----
        Clear(); ResetCounters(); ResetCensus();
        fx = new Fixture();
        _source = fx;
        // root -> a -> c, root -> b
        fx.Models["rig"] = Fix("rig", "", 1f, [], ["root", "a", "c", "b"], [-1, 0, 1, 0]);
        ModelDef rig = Acquire("rig")!;
        AnimDef shaped = FixAnim("shaped", ["root", "a", "c", "zzz"], [-1, 0, 1, 0]);
        AnimBinding b1 = Bind(rig, shaped);
        check.Expect(b1.Node[0] == 0 && b1.Node[1] == 1 && b1.Node[2] == 2 && b1.Node[3] == -1,
                     $"the binding is [{string.Join(",", b1.Node)}], not [0,1,2,-1]");
        check.Expect(b1.Bound == 3 && b1.Unbound == 1, $"the binding counted {b1.Bound}/{b1.Unbound}");
        check.Expect(ReferenceEquals(Bind(rig, shaped), b1), "the binding is rebuilt instead of cached");
        // "c" exists in the model, but under "a" -- a parent-scoped match must refuse it under "root".
        AnimDef misplaced = FixAnim("misplaced", ["root", "c"], [-1, 0]);
        AnimBinding b2 = Bind(rig, misplaced);
        check.Expect(b2.Node[1] == -1,
                     "a node matched by name under the WRONG parent: the match is not scoped to the hierarchy");
        check.Expect(rig.Find("c") == 2, "the model's own flat name index disagrees, so the test proves nothing");
        long flat = FlatBinds;
        AnimDef hierless = FixAnim("hierless", ["root", "c"], [-1, -1]);
        check.Expect(Bind(rig, hierless).Node[1] == 2 && FlatBinds == flat + 1,
                     "an animation with no hierarchy did not fall back to a flat match, loudly");
        check.Expect(Bindings == 3, $"the binding cache holds {Bindings} entries, not 3");
        // Finding 13: a rig whose animation names the model does not have animates in vanilla (the engine's
        // circular scan lands on a sibling) and stands still here, so the count has to be visible.
        check.Expect(PartialBinds == 2, $"{PartialBinds} partial bindings counted, not 2");
        check.Expect(CensusClause().Contains("partialbind=2"), $"the partial bindings are not on the line: {CensusClause()}");

        // ---- refcounts, the resident set and eviction ----
        Clear(); ResetCounters(); ResetCensus();
        fx = new Fixture();
        _source = fx;
        fx.Models["base"] = Fix("base", "", 1f, ["default"]);
        fx.Models["kid"] = Fix("kid", "base", 1f, ["walk"]);
        BudgetOverride = long.MaxValue;
        ModelDef kid = Acquire("kid")!;
        check.Expect(ReferenceEquals(Acquire("kid"), kid), "a second acquire handed back a different model");
        check.Expect(RefsOf(kid) == 2, $"two acquires left {RefsOf(kid)} references");
        check.Expect(fx.Parses == 2, $"{fx.Parses} parses for 2 models with a shared supermodel");
        Release(kid);
        Release(kid);
        check.Expect(RefsOf(kid) == 0 && Resident == 2, "zero references evicted the model at an infinite budget");
        check.Expect(ReferenceEquals(Acquire("kid"), kid) && fx.Parses == 2, "an idle model was parsed again");
        check.Expect(Hits >= 2, "the warm acquires were not counted as hits");

        long overs = OverReleases;
        Release(kid);
        Release(kid);
        check.Expect(OverReleases == overs + 1, "releasing a model with no reference held was not counted");

        var evicted = new List<string>();
        Evicting = m => evicted.Add(m.Name);
        BudgetOverride = 0;
        Trim();
        check.Expect(Resident == 0, $"{Resident} models survived a zero budget");
        check.Expect(evicted.Contains("kid") && evicted.Contains("base"),
                     $"eviction fired for [{string.Join(",", evicted)}], not both models");
        check.Expect(Bindings == 0, "the bindings of an evicted model were kept");
        check.Expect(Evictions >= 2, "the evictions were not counted");
        // Restore, never null: other streams subscribe at their own Attach (CX-114 points this at
        // MeshArena.Forget), and nulling it here dropped their subscription for the session.
        Evicting = hadEvict;

        // The supermodel's reference is dropped by the EVICTION, not by the child reaching zero: the child's
        // own bindings and arena ranges are still valid while it is idle.
        BudgetOverride = long.MaxValue;
        kid = Acquire("kid")!;
        ModelDef bse = FindModel("base")!;
        check.Expect(RefsOf(bse) == 1, "the re-acquired model did not take the supermodel reference again");
        Release(kid);
        check.Expect(RefsOf(bse) == 1, "the supermodel's reference went with the child's last reference");

        // ---- the read-ahead: a prefetch makes the later acquire a hit with no wait, and CANNOT be evicted
        //      before whatever asked for it arrives (it carries the loader's own reference) ----
        Clear(); ResetCounters(); ResetCensus();
        fx = new Fixture();
        _source = fx;
        BudgetOverride = 0;                       // the harshest budget there is
        fx.Models["pre"] = Fix("pre", "", 1f, []);
        Prefetch("pre");
        for (int i = 0; i < 1000 && FindModel("pre") == null; i++) { Drain(); BlockingWait.Sleep(1, "self-check: wait for the loader thread"); }
        check.Expect(FindModel("pre") != null, "a prefetched model never reached the table");
        Trim();
        check.Expect(FindModel("pre") != null,
                     "a read-ahead was evicted at a zero budget before anything could acquire it");
        long hits = Hits;
        ModelDef pre = Acquire("pre")!;
        check.Expect(pre != null && Hits == hits + 1 && fx.Parses == 1, "an acquire after a prefetch was not a hit");
        check.Expect(RefsOf(pre!) == 1, $"the loader's reference was not taken over: {RefsOf(pre!)} held");
        Release(pre!);
        check.Expect(FindModel("pre") == null && Evictions == 1,
                     "the model survived its last release at a zero budget");

        // ...and a read-ahead nothing ever asks for does NOT pin memory for the session.
        Clear(); ResetCounters();
        fx = new Fixture();
        _source = fx;
        BudgetOverride = 0;
        fx.Models["unasked"] = Fix("unasked", "", 1f, []);
        Prefetch("unasked");
        for (int i = 0; i < 1000 && FindModel("unasked") == null; i++) { Drain(); BlockingWait.Sleep(1, "self-check: wait for the loader thread"); }
        check.Expect(FindModel("unasked") != null, "the read-ahead never landed");
        for (int i = 0; i <= LoaderGraceDrips; i++) Pump();
        check.Expect(FindModel("unasked") == null && LoaderDropped == 1,
                     "a read-ahead nothing asked for held its reference past the grace");
        BudgetOverride = long.MaxValue;

        // ---- the census: distinct meshes against placements ----
        ResetCensus();
        var m1 = new MeshDef();
        var m2 = new MeshDef();
        for (int i = 0; i < 5; i++) NotePlacement(m1);
        for (int i = 0; i < 2; i++) NotePlacement(m2);
        check.Expect(PlacedMeshes == 2 && Placements == 7, $"the census says {PlacedMeshes} meshes / {Placements} placements");
        check.Expect(CensusClause().Contains("share=3.50x"), $"the share clause is wrong: {CensusClause()}");
        DropPlacement(m2);
        DropPlacement(m2);
        check.Expect(PlacedMeshes == 1 && Placements == 5, "dropping every placement did not retire the mesh");
        long stray = StrayDrops;
        DropPlacement(m2);
        check.Expect(StrayDrops == stray + 1, "an unmatched placement drop was not counted");
        ResetCensus();

        // ---- the resident distinct-mesh count is maintained at the table's edges, not recomputed ----
        Clear(); ResetCounters(); ResetCensus();
        fx = new Fixture();
        _source = fx;
        BudgetOverride = long.MaxValue;
        var shared = new MeshDef();
        ModelDef twoNodes = Fix("twomesh", "", 1f, [], ["root", "a"], [-1, 0]);
        twoNodes.Nodes[0].Mesh = shared;
        twoNodes.Nodes[1].Mesh = shared;          // one mesh, two nodes: the count is of MESHES
        ModelDef oneNode = Fix("onemesh", "", 1f, []);
        oneNode.Nodes[0].Mesh = new MeshDef();
        fx.Models["twomesh"] = twoNodes;
        fx.Models["onemesh"] = oneNode;
        ModelDef t2 = Acquire("twomesh")!, o1 = Acquire("onemesh")!;
        check.Expect(ResidentMeshes == 2, $"the resident mesh count is {ResidentMeshes}, not 2");
        Release(o1);
        BudgetOverride = 0;
        Trim();
        check.Expect(ResidentMeshes == 1, $"evicting a model left its mesh in the count ({ResidentMeshes})");
        BudgetOverride = long.MaxValue;
        Release(t2);

        // ---- the source seam: no source is a counted refusal, never a main-thread parse ----
        Clear(); ResetCounters();
        _source = null;
        check.Expect(Acquire("anything") == null, "a sourceless cache answered an acquire");
        check.Expect(Stats(1).Contains("nosource"), $"the refusal is not on the period line: {Stats(1)}");
    }
}
