using System;
using System.Collections.Generic;

namespace CX.Alloy.Mdl;

/// <summary>The animation half of the model cache -- the engine's <c>FindAnimation</c> @0x497c30,
/// <c>FindAnimScale</c> @0x499b10, <c>Gob::LoadAddInAnimations</c> @0x43a960, and the per-(model, animation)
/// node binding that replaces the engine's per-frame name/id probing.</summary>
public static partial class ModelCache
{
    /// <summary>The name <c>FindAnimation</c> falls back to at the END of the chain. The engine's literal
    /// (the 8-byte <c>"default"</c> it copies @0x497cc5).</summary>
    private const string Default = "default";

    /// <summary>The animation <paramref name="anim"/> as <paramref name="model"/> resolves it through its
    /// supermodel chain (the engine's <c>FindAnimation</c> @0x497c30, Windows rva 0x50f80), and the model that
    /// owns it. The engine's order: from <paramref name="model"/> up, the first model whose OWN animation list
    /// holds the name (case-insensitive); on a whole-chain miss, <b>only the last model</b> for "default".</summary>
    public static AnimDef? FindAnimation(ModelDef model, string anim, out ModelDef? owner)
    {
        owner = null;
        // The engine guards only a null pointer (@0x497c35). An EMPTY name therefore walks the chain and can
        // come back with the terminal model's "default", and CX answers the same thing.
        if (model == null || anim == null) return null;
        ModelDef m = model, last = model;
        for (int i = 0; i < MaxChain; i++)
        {
            AnimDef? got = Own(m, anim);
            if (got != null) { owner = m; return got; }
            last = m;
            if (m.Super == null) break;
            m = m.Super;
        }
        AnimDef? fallback = Own(last, Default);
        if (fallback != null) owner = last;
        return fallback;
    }

    /// <summary>A model's own animation list, by name. Linear and case-insensitive, as the engine's is: an
    /// animation list is tens of entries and the lookup is per PlayAnimation, not per frame.</summary>
    private static AnimDef? Own(ModelDef m, string anim)
    {
        AnimDef[] all = m.Anims;
        for (int i = 0; i < all.Length; i++)
            if (Strcasecmp(all[i].Name, anim) == 0) return all[i];
        return null;
    }

    /// <summary>The engine's <c>FindAnimScale</c> @0x499b10 (Windows rva 0x50ed0): the product of
    /// <see cref="ModelDef.AnimScale"/> over the chain BELOW the model that owns <paramref name="anim"/> (owner
    /// found by POINTER identity), so an animation the model owns itself scales by 1. One no model in the chain
    /// owns comes back with the whole product -- the engine's answer, not a refusal.</summary>
    public static float FindAnimScale(ModelDef? model, AnimDef? anim)
    {
        float scale = 1f;
        ModelDef? m = model;
        for (int i = 0; i < MaxChain && m != null; i++)
        {
            if (Holds(m, anim)) return scale;
            scale *= m.AnimScale;
            m = m.Super;
        }
        return scale;
    }

    private static bool Holds(ModelDef m, AnimDef? anim)
    {
        AnimDef[] all = m.Anims;
        for (int i = 0; i < all.Length; i++) if (ReferenceEquals(all[i], anim)) return true;
        return false;
    }

    /// <summary><c>Gob::LoadAddInAnimations(name)</c> @0x43a960: the extra animation set an object can be given
    /// on top of its model's. The engine's body is <c>FindModel(name)</c> plus one <c>Model::AddRef</c>, so this
    /// is <see cref="Acquire"/> and the caller <see cref="Release"/>s it when the object drops the set. The
    /// engine's null-FindModel fallback -- a loose <c>.mdl</c> in the working directory -- is not ported.</summary>
    public static ModelDef? LoadAddInAnimations(string resref)
    {
        return Acquire(resref);
    }

    // ---- the memo over the chain walk ----

    /// <summary>What one animation name resolves to for one model: the animation, the model in the chain that
    /// owns it, and the scale <see cref="FindAnimScale"/> gives it. <see cref="Anim"/> null means the name does
    /// not resolve, which is memoised too.</summary>
    public readonly struct AnimLookup
    {
        public readonly AnimDef? Anim;
        public readonly ModelDef? Owner;
        public readonly float Scale;
        internal AnimLookup(AnimDef? anim, ModelDef? owner, float scale) { Anim = anim; Owner = owner; Scale = scale; }
    }

    /// <summary>Name equality the engine's way (<see cref="Strcasecmp"/>), so the memo cannot group two names
    /// the lookup would tell apart. .NET's <c>OrdinalIgnoreCase</c> agrees on ASCII and the single-byte codepage
    /// a model file is written in is not all ASCII, so the comparer is ours rather than nearly ours.</summary>
    private sealed class NameEq : IEqualityComparer<string>
    {
        internal static readonly NameEq Instance = new();
        public bool Equals(string? a, string? b) => a != null && b != null && Strcasecmp(a, b) == 0;
        public int GetHashCode(string s)
        {
            uint h = 2166136261;                       // not-an-offset: FNV-1a 32 basis
            for (int i = 0; i < s.Length; i++)
            {
                int c = s[i];
                if ((uint)(c - 'A') < 26u) c += 32;
                h = (h ^ (uint)c) * 16777619;          // not-an-offset: FNV-1a 32 prime
            }
            return (int)h;
        }
    }

    private static readonly Dictionary<ModelDef, Dictionary<string, AnimLookup>> _resolved =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>The lookup the animation runtime uses: <see cref="FindAnimation"/> and
    /// <see cref="FindAnimScale"/> once per (model, name), remembered after that. A real chain is five models
    /// deep with 165 animations at its base (<c>a_ba</c>), so the engine's unmemoised walk costs hundreds of
    /// <c>strcasecmp</c>s per <c>PlayAnimation</c>. Built BY the ported walk, and dropped with the model.</summary>
    public static AnimLookup Resolve(ModelDef model, string anim)
    {
        // A handle from FindModel or a read-ahead has not been through Acquire, so its chain may still be
        // unresolved -- memoising a chain-less answer would make it permanent.
        EnsureChained(model);
        if (!_resolved.TryGetValue(model, out Dictionary<string, AnimLookup>? by))
        {
            by = new Dictionary<string, AnimLookup>(NameEq.Instance);
            _resolved[model] = by;
        }
        if (by.TryGetValue(anim, out AnimLookup got)) { _resolveHits++; return got; }
        AnimDef? a = FindAnimation(model, anim, out ModelDef? owner);
        got = new AnimLookup(a, owner, FindAnimScale(model, a));
        by[anim] = got;
        _resolveMisses++;
        return got;
    }

    private static long _resolveHits, _resolveMisses;
    internal static long ResolveHits => _resolveHits;
    internal static long ResolveMisses => _resolveMisses;

    // ---- the per-(model, animation) node binding ----

    /// <summary>One animation resolved against one model's node tree: for each node of the animation, the index
    /// of the node it drives in <see cref="ModelDef.Nodes"/>, or -1. Built once and cached, so the animation
    /// runtime never matches a name or probes a sibling list per frame.</summary>
    public sealed class AnimBinding
    {
        public readonly ModelDef Model;
        public readonly AnimDef Anim;
        /// <summary>Per animation-node index, the model node index it drives; -1 for a node the model does not
        /// have (which the engine marks by writing -1 into <c>MdlNode+0x34</c>).</summary>
        public readonly int[] Node;
        public readonly int Bound, Unbound;

        internal AnimBinding(ModelDef model, AnimDef anim, int[] node, int bound)
        {
            Model = model; Anim = anim; Node = node; Bound = bound; Unbound = node.Length - bound;
        }
    }

    /// <summary>Bindings per model, then per animation: nested rather than keyed on the pair so dropping one
    /// model's bindings is a walk of ITS bindings instead of all of them.</summary>
    private static readonly Dictionary<ModelDef, Dictionary<AnimDef, AnimBinding>> _bindings =
        new(ReferenceEqualityComparer.Instance);
    private static int _bindingCount;

    private static void Keep(ModelDef model, AnimDef anim, AnimBinding b)
    {
        if (!_bindings.TryGetValue(model, out Dictionary<AnimDef, AnimBinding>? by))
        {
            by = new Dictionary<AnimDef, AnimBinding>(ReferenceEqualityComparer.Instance);
            _bindings[model] = by;
        }
        if (by.TryAdd(anim, b)) _bindingCount++;
    }

    /// <summary>The binding of <paramref name="anim"/> onto <paramref name="model"/>'s nodes, cached with the
    /// model, by the engine's hierarchical name match (<c>MaxTree::SynchronizeTree</c> @0x453830): roots pair
    /// unconditionally, a child only against the matched parent's children, case-insensitive, unmatched -1 --
    /// but onto the node INDEX, not the engine's node ids, which collide at 0 and let its scan shift a bind.</summary>
    public static AnimBinding Bind(ModelDef model, AnimDef anim)
    {
        if (_bindings.TryGetValue(model, out Dictionary<AnimDef, AnimBinding>? have)
            && have.TryGetValue(anim, out AnimBinding? got)) return got;
        AnimNode[] an = anim.Nodes;
        int[] map = new int[an.Length];
        for (int i = 0; i < map.Length; i++) map[i] = -1;
        int bound = 0;
        if (an.Length != 0 && model.Nodes.Length != 0)
        {
            if (HasHierarchy(anim))
            {
                map[0] = 0;   // the two roots pair unconditionally, as SynchronizeTree does
                bound = 1 + Match(model, anim, 0, 0, map);
            }
            else
            {
                _flatBinds++;
                Say("flat-bind", $"ModelCache: animation {anim.Name} of {model.Name} carries no node hierarchy "
                               + "-- binding by flat node name, which cannot refuse a name under the wrong parent");
                for (int i = 0; i < an.Length; i++)
                {
                    int at = model.Find(an[i].Name);
                    if (i == 0 && at < 0) at = 0;
                    map[i] = at;
                    if (at >= 0) bound++;
                }
            }
        }
        var b = new AnimBinding(model, anim, map, bound);
        if (b.Unbound != 0)
        {
            _partialBinds++;
            Say("partial-bind", $"ModelCache: animation {anim.Name} of {model.Name} binds {b.Bound} of "
                              + $"{b.Bound + b.Unbound} nodes -- the {b.Unbound} the model does not name stay "
                              + "still (vanilla would drive a sibling in their place)");
        }
        Keep(model, anim, b);
        return b;
    }

    private static long _partialBinds;

    /// <summary>The binding of <paramref name="anim"/> onto a piece <c>Gob::Append</c> grafted under a part:
    /// the piece's root pairs with animation node <paramref name="animRoot"/> and the match runs on beneath it,
    /// as <c>AnimateHierarchy</c> @0x599990 reaches a grafted root -- only through its anchor's children.
    /// Cached like <see cref="Bind"/>, per (model, animation, start node).</summary>
    public static AnimBinding BindUnder(ModelDef model, AnimDef anim, int animRoot)
    {
        if (animRoot == 0) return Bind(model, anim);
        if (_under.TryGetValue((model, anim, animRoot), out AnimBinding? got)) return got;
        int[] map = new int[anim.Nodes.Length];
        Array.Fill(map, -1);
        int bound = 0;
        if ((uint)animRoot < (uint)map.Length && model.Nodes.Length != 0 && HasHierarchy(anim))
        {
            map[animRoot] = 0;
            bound = 1 + Match(model, anim, animRoot, 0, map);
        }
        var b = new AnimBinding(model, anim, map, bound);
        _under[(model, anim, animRoot)] = b;
        return b;
    }

    /// <summary><see cref="BindUnder"/>'s cache. ponytail: never evicted with its model -- bounded by the
    /// distinct (piece, animation, anchor) triples a session grafts; drop it in Forget if that ever grows.</summary>
    private static readonly Dictionary<(ModelDef, AnimDef, int), AnimBinding> _under = new();

    /// <summary>Does the animation state its own node hierarchy? A single node needs none; anything larger
    /// with no parent link at all is a parser that has not filled <see cref="AnimNode.Parent"/> yet.</summary>
    private static bool HasHierarchy(AnimDef anim)
    {
        AnimNode[] n = anim.Nodes;
        if (n.Length <= 1) return true;
        for (int i = 0; i < n.Length; i++) if (n[i].Parent >= 0) return true;
        return false;
    }

    private static long _flatBinds;

    /// <summary>One level of <c>SynchronizeNodes</c>: each child of the bound animation node against the
    /// children of the bound model node, by name, then recurse. Scoped to the parent, so a name that exists
    /// elsewhere in the tree is NOT a match.</summary>
    private static int Match(ModelDef model, AnimDef anim, int animIdx, int modelIdx, int[] map)
    {
        int bound = 0;
        int[] animKids = Children(anim, animIdx);
        int[] modelKids = model.Nodes[modelIdx].Children;
        for (int a = 0; a < animKids.Length; a++)
        {
            string name = anim.Nodes[animKids[a]].Name;
            for (int k = 0; k < modelKids.Length; k++)
            {
                if (Strcasecmp(model.Nodes[modelKids[k]].Name, name) != 0) continue;
                map[animKids[a]] = modelKids[k];
                bound++;
                bound += Match(model, anim, animKids[a], modelKids[k], map);
                break;
            }
        }
        return bound;
    }

    /// <summary>An animation node's children. <see cref="AnimNode"/> carries only a name, so the animation's
    /// hierarchy is its own node order: the parser emits it pre-order with the parent ahead of its children,
    /// and <see cref="AnimDef.Nodes"/> indices are taken from the matching <see cref="ModelDef"/> shape. Cached
    /// per animation, because the walk asks for it once per level.</summary>
    private static int[] Children(AnimDef anim, int node)
    {
        if (!_animKids.TryGetValue(anim, out int[][]? kids))
        {
            kids = BuildKids(anim);
            _animKids[anim] = kids;
        }
        return kids[node];
    }

    private static readonly Dictionary<AnimDef, int[][]> _animKids = new(ReferenceEqualityComparer.Instance);

    /// <summary>The animation's parent/child shape, from its nodes' <see cref="AnimNode.Parent"/> links.</summary>
    private static int[][] BuildKids(AnimDef anim)
    {
        AnimNode[] n = anim.Nodes;
        var lists = new List<int>[n.Length];
        for (int i = 0; i < n.Length; i++) lists[i] = new List<int>();
        for (int i = 0; i < n.Length; i++)
        {
            int p = n[i].Parent;
            if (p >= 0 && p < n.Length && p != i) lists[p].Add(i);
        }
        var kids = new int[n.Length][];
        for (int i = 0; i < n.Length; i++) kids[i] = lists[i].ToArray();
        return kids;
    }

    /// <summary>Drop every binding that names <paramref name="m"/>, on its eviction. O(this model's
    /// bindings), which is what the nested map buys.</summary>
    private static void Forget(ModelDef m)
    {
        _resolved.Remove(m);
        if (_bindings.Remove(m, out Dictionary<AnimDef, AnimBinding>? by)) _bindingCount -= by.Count;
        foreach (AnimDef a in m.Anims) _animKids.Remove(a);
    }

    /// <summary>Drop one model's resolution memo without touching its bindings: what the live verify uses for
    /// the mirror models it builds, which are never in the table and so are never evicted.</summary>
    internal static void ForgetResolution(ModelDef m) => _resolved.Remove(m);

    /// <summary>The memo's own hit/miss counters, so a diagnostic that resolves on purpose can put them back
    /// and not show up as the animation runtime's traffic.</summary>
    internal static (long Hits, long Misses) ResolveCounters => (_resolveHits, _resolveMisses);
    /// <inheritdoc cref="ResolveCounters"/>
    internal static void RestoreResolveCounters(long hits, long misses) { _resolveHits = hits; _resolveMisses = misses; }

    private static void ClearBindings()
    {
        _bindings.Clear();
        _bindingCount = 0;
        _animKids.Clear();
        _resolved.Clear();
    }

    /// <summary>Bindings held, for the self-checks and the period line.</summary>
    internal static int Bindings => _bindingCount;
    internal static long FlatBinds => _flatBinds;
    internal static long PartialBinds => _partialBinds;
}
