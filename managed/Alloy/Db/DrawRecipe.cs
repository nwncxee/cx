using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary><b>The stable half of a draw, interned per <c>(ModelDef, node)</c>.</b> Every term in it is a
/// property of the MODEL, not of the instance -- a thousand doors of one resref share one recipe -- so a part
/// needs only <see cref="SceneDb.PartRecipe"/>. Interned, not cached: a recipe is immutable and lives as long as
/// its <see cref="ModelDef"/>, so its index is safe to hold across frames and threads with no identity test.</summary>
internal static partial class DrawRecipe
{
    /// <summary>Distinct recipes held at once. A heavy persistent-world area measured ~1,300 distinct drawn nodes across
    /// its tileset and its creatures; a session that visits many areas accumulates, so this is sized for the
    /// session rather than the area and the models' own release is what retires entries.</summary>
    internal const int Max = 1 << 14;

    /// <summary>The PBR floats a recipe stages verbatim into its record, matching
    /// <c>AlloyRenderer.ParamFloats</c>: the two stock vec4s then ADR 0026's three extension vec4s.</summary>
    internal const int ParamFloats = 20;
    /// <summary>Texture slots a recipe names: diffuse, the seven map slots (five maps, flow, AO), then the
    /// four PALETTE slots. The palette four are held but never BOUND -- the lit stage spends texture units
    /// 0..30, so they reach their one reader as handles in <see cref="TintTerms"/>' row.</summary>
    internal const int TexSlots = 1 + AlloyRenderer.MapSlots + AlloyRenderer.PalSlots;
    /// <summary>The map slot of <c>texture6</c>, the flow map / weather-ground overlay.</summary>
    private const int FlowSlot = 5;
    /// <summary>The recipe slot of the AO map, which reads <c>.mtr</c> <c>texture11</c>, not <c>texture7</c>.</summary>
    private const int AoRecipeSlot = 1 + AlloyRenderer.AoSlot;
    /// <summary>The first PALETTE recipe slot, which reads <c>.mtr</c> <see cref="AlloyRenderer.PalTexture0"/>
    /// (<c>texture7</c>); the other three follow it in <c>.mtr</c> order.</summary>
    private const int PalRecipeSlot0 = 1 + AlloyRenderer.MapSlots;

    /// <summary>The <c>.mtr</c> <c>textureN</c> a recipe slot reads: its own index, except the AO slot and the
    /// four palette slots, which sit past the map block and read <c>texture7</c>..<c>texture10</c>.</summary>
    internal static int MtrTextureOf(int s)
        => s == AoRecipeSlot ? AlloyRenderer.AoTexture
         : s >= PalRecipeSlot0 ? AlloyRenderer.PalTexture0 + (s - PalRecipeSlot0) : s;

    /// <summary>The slot-mask bit a bound recipe slot sets: none for the diffuse, the flow slot and the four
    /// palette slots only for the features that read them (vanilla's own <c>texture6Bound</c> test), and AO on
    /// its own out-of-line bit.</summary>
    internal static int MaskBitOf(int s, int feat)
        => s == 0 ? 0
         : s >= PalRecipeSlot0
             ? (ShaderFeatures.ReadsPaletteSlots(feat) ? AlloyRenderer.PalSlotBit(s - PalRecipeSlot0) : 0)
         : s - 1 == FlowSlot && !ShaderFeatures.ReadsAuxSlot(feat) ? 0
         : AlloyRenderer.SlotBit(s - 1);

    /// <summary>Palette slot <paramref name="k"/>'s bindless handle on recipe <paramref name="r"/>, for the
    /// one reader that takes them that way (<see cref="TintTerms.Upload"/>). 0 until the texture is resident.</summary>
    internal static ulong PalHandle(int r, int k)
        => (uint)r < Max && (uint)k < AlloyRenderer.PalSlots ? TexHandle(r, PalRecipeSlot0 + k) : 0ul;


    private static readonly Dictionary<(ModelDef, int), int> _byNode = new();
    private static int _n;
    /// <summary>Who holds each recipe slot and the retired ones on their way back -- one owner for the ownership
    /// rules. A retired slot waits two <see cref="Pump"/>s (<c>Retiring</c>, <c>Cooling</c>) before <c>Free</c>, so
    /// the feed fold that releases every draw slot still seated on it runs first.</summary>
    private sealed class SlotState
    {
        public readonly List<int> Retiring = new(), Cooling = new();
        public readonly Stack<int> Free = new();
        /// <summary>Parts naming each recipe (<see cref="SceneDb.PartRecipe"/>, written only through
        /// <see cref="Assign"/>). A slot is never retired while one does.</summary>
        public readonly int[] Users = new int[Max];
        /// <summary>Handed out and not yet retired: a second retire is refused, so no slot reaches two owners.</summary>
        public readonly bool[] Held = new bool[Max];
        /// <summary>Its model was evicted (or its dynamic owner let go) while a part still named it.</summary>
        public readonly bool[] Orphan = new bool[Max];
        /// <summary>Bumped at each hand-out, so a holder of (slot, gen) can tell its recipe from a later tenant's.</summary>
        public readonly uint[] Gen = new uint[Max];
        /// <summary>Each recipe's row in <see cref="TintTerms"/>' side table, or -1 -- the value a palette-tint
        /// record carries in <c>feat.z</c>. Seeded -1, because a zero-filled column would hand the untinted
        /// majority row 0 and retire it out from under whoever owns it.</summary>
        public readonly int[] TintRow = new int[Max];

        public SlotState() { System.Array.Fill(TintRow, -1); }
        public int RetiredInUse, DoubleRetire;
        /// <summary><c>.mtr</c> reads that landed, against what the last <see cref="Reclassify"/> saw and what
        /// the last part-column re-seed saw. No other serial moves for a material, and the two consumers are
        /// asked at different points of the fold, so each keeps its own watermark.</summary>
        public int MtrSerial, MtrSeen, MtrSeenParts;
        public CX.Framework.ExtenderLog? Log;
    }
    private static SlotState _own = new();

    /// <summary>The log the ownership refusals are said on. Set by the feed.</summary>
    internal static CX.Framework.ExtenderLog? Log { get => _own.Log; set => _own.Log = value; }

    /// <summary>Slot <paramref name="r"/>'s current hand-out generation.</summary>
    internal static uint Gen(int r) => (uint)r < Max ? _own.Gen[r] : 0;

    /// <summary>Recipes whose arena range was not resident at their last ask, as a drained list rather than a
    /// walk of the whole table. A range that lands leaves the list; nothing re-polls what is resident.</summary>
    private static readonly int[] _rangeWait = new int[Max];
    private static int _rangeWaitN;
    private static readonly bool[] _rangeQueued = new bool[Max];

    // the interned columns, parallel arrays indexed by recipe
    private static readonly ModelDef?[] _model = new ModelDef?[Max];
    private static readonly int[] _node = new int[Max];
    private static readonly MeshDef?[] _mesh = new MeshDef?[Max];
    private static readonly byte[] _cls = new byte[Max];
    private static readonly RecipeFlag[] _flags = new RecipeFlag[Max];
    private static readonly byte[] _src = new byte[Max], _dst = new byte[Max], _prim = new byte[Max];
    private static readonly int[] _feat = new int[Max];
    private static readonly float[] _featF = new float[Max * 4];
    private static readonly float[] _param = new float[Max * ParamFloats];
    private static readonly int[] _slots = new int[Max];
    private static readonly string[] _tex = new string[Max * TexSlots];
    /// <summary>The registry slot each texture slot names (<c>TexId.Value</c>; 0 = no texture). The bindless
    /// handle behind it is 64 bits, so the recipe keeps the HANDLE rather than an int id.</summary>
    private static readonly uint[] _texId = new uint[Max * TexSlots];
    /// <summary>1 while this texture slot's registry entry has not settled (arrived or failed).</summary>
    private static readonly byte[] _texWait = new byte[Max * TexSlots];
    /// <summary>How many of <see cref="_texWait"/> are still set. <see cref="Pump"/> skips its whole texture
    /// half while this is zero, so a settled area costs no re-ask at all.</summary>
    private static int _texPending;
    private static readonly Vector4[] _amb = new Vector4[Max], _dif = new Vector4[Max];
    private static readonly float[] _cos = new float[Max], _sin = new float[Max];
    private static readonly Vector3[] _centre = new Vector3[Max];
    private static readonly float[] _radius = new float[Max];
    private static readonly float[] _box = new float[Max * 6];
    private static readonly int[] _range = new int[Max];
    /// <summary>The recipe's material DECLARES water (<see cref="DeclaredWater"/>) -- the model-side half of
    /// the engine's water test, kept per recipe because <see cref="Reclassify"/> re-asks the class of every
    /// recipe every frame and resolving a <c>.mtr</c> there would be a dictionary lookup per recipe per
    /// frame.</summary>
    private static readonly bool[] _declWater = new bool[Max];
    /// <summary>The material's <c>NWN_ALPHA_MODE 2</c> -- draw this part through alpha-to-coverage
    /// (<see cref="RecipeFlag.Coverage"/>) instead of blending it. Kept per recipe for <see cref="Reclassify"/>.</summary>
    private static readonly bool[] _coverage = new bool[Max];

    /// <summary>Recipes in use: the slots ever handed out, less the retired ones waiting or free.</summary>
    internal static int Count => _n - _own.Free.Count - _own.Retiring.Count - _own.Cooling.Count;

    /// <summary>A slot for a new recipe: a retired one past its quarantine, else a fresh one; -1 when full.</summary>
    private static int Take()
    {
        int r;
        if (_own.Free.Count > 0) r = _own.Free.Pop();
        else if (_n < Max) r = _n++;
        else { Spilled++; return -1; }
        _own.Held[r] = true; _own.Orphan[r] = false; _own.Gen[r]++;
        return r;
    }

    /// <summary>Point part <paramref name="p"/> at recipe <paramref name="r"/> (-1 = none). The one writer of
    /// <see cref="SceneDb.PartRecipe"/>: it keeps the per-slot user count the retire paths read.</summary>
    internal static void Assign(int p, int r)
    {
        int was = SceneDb.PartRecipe[p];
        if (was == r) return;
        SceneDb.PartRecipe[p] = r;
        if ((uint)r < Max) _own.Users[r]++;
        if ((uint)was < Max && --_own.Users[was] <= 0)
        {
            _own.Users[was] = 0;
            if (_own.Orphan[was]) Retire(was);
        }
    }

    /// <summary>Hand slot <paramref name="r"/> back: its range and texture references go, and it enters the
    /// quarantine. Refused, and counted, for a slot that is not held.</summary>
    private static void Retire(int r)
    {
        if (!_own.Held[r]) { _own.DoubleRetire++; return; }
        _own.Held[r] = false; _own.Orphan[r] = false;
        if (_range[r] >= 0) MeshArena.Release(_range[r]);
        _range[r] = -1;
        if (_own.TintRow[r] >= 0) { TintTerms.Free(_own.TintRow[r], r); _own.TintRow[r] = -1; }
        for (int s = 0; s < TexSlots; s++)
        {
            int o = r * TexSlots + s;
            TexRegistry.Release(new TexId(_texId[o]));
            _texId[o] = 0; _tex[o] = "";
            if (_texWait[o] != 0) { _texWait[o] = 0; _texPending--; }
        }
        _model[r] = null; _mesh[r] = null;
        _rangeQueued[r] = false;
        _own.Retiring.Add(r);
        Retired++;
    }

    /// <summary>A retire refused because the slot was not held: it would have reached the free list twice.</summary>
    internal static int DoubleRetire => _own.DoubleRetire;
    /// <summary>Recipes whose model was evicted while a part still named them: kept until that part lets go.</summary>
    internal static int RetiredInUse { get => _own.RetiredInUse; set => _own.RetiredInUse = value; }

    /// <summary>How many recipes actually have a resolved diffuse, and how many are drawing the white stand-in.
    /// "Most surfaces are untextured" is a question about these two numbers and nothing else.</summary>
    internal static (int named, int resolved, int standIn) DiffuseCensus()
    {
        int named = 0, resolved = 0, standIn = 0;
        for (int r = 0; r < _n; r++)
        {
            if (_mesh[r] == null) continue;
            int o = r * TexSlots;
            if (string.IsNullOrEmpty(_tex[o])) continue;
            named++;
            if (TexRegistry.Handle(new TexId(_texId[o])) != 0) resolved++; else standIn++;
        }
        return (named, resolved, standIn);
    }
    /// <summary>Recipes refused for want of a table slot. Loud, counted, never a quiet hole.</summary>
    internal static int Spilled { get; private set; }
    /// <summary>Nodes not drawn because a later coincident twin covers them (<see cref="TwinShadowed"/>).</summary>
    internal static int TwinRefused { get; private set; }
    /// <summary>Recipes whose file-stated box did not hold their own vertices, and the first one's model:node.
    /// The cull sphere and the area index's box are this box, so an unfixed one culls a visible mesh.</summary>
    internal static int BoxGrown { get; private set; }
    internal static string BoxGrownFirst { get; private set; } = "";

    /// <summary>Siblings past this many are not compared.</summary>
    private const int TwinMaxFanOut = 32;

    /// <summary>Stock content carries coincident sibling tri-meshes -- same placement and vertices, different
    /// textures (<c>ashlw_072.mdl</c>). Vanilla draws both in tree order under LEQUAL so only the LATER one's
    /// pixels survive; Alloy's culled batches have no stable order, so the earlier twin is dropped instead.
    /// ponytail: a later twin that is blended or hidden at runtime should let the earlier show; stock has none.</summary>
    internal static bool TwinShadowed(ModelDef model, int node)
    {
        NodeDef nd = model.Nodes[node];
        MeshDef? m = nd.Mesh;
        if (m == null || nd.Skin != null || Keyed(nd) || nd.Parent < 0) return false;
        int[] kids = model.Nodes[nd.Parent].Children;
        if (kids.Length > TwinMaxFanOut) return false;
        bool later = false;
        foreach (int k in kids)
        {
            if (k == node) { later = true; continue; }
            if (!later || (uint)k >= (uint)model.Nodes.Length) continue;
            NodeDef t = model.Nodes[k];
            MeshDef? tm = t.Mesh;
            if (tm == null || !tm.Render || tm.Indices.Length == 0 || t.Skin != null || Keyed(t)) continue;
            if (t.Position != nd.Position || t.Orientation != nd.Orientation || t.Scale != nd.Scale) continue;
            if (tm.Positions.AsSpan().SequenceEqual(m.Positions) && tm.Indices.AsSpan().SequenceEqual(m.Indices)) return true;
        }
        return false;
    }

    /// <summary>Does a track with more than one key drive <paramref name="n"/>? A compiled binary gives every node a
    /// one-key position and orientation track (its rest pose, already in <see cref="NodeDef.Position"/>), which is no animation.</summary>
    private static bool Keyed(NodeDef n)
    {
        foreach (ControllerTrack c in n.Controllers) if (c.Times.Length > 1) return true;
        return false;
    }

    internal static MeshDef? Mesh(int r) => _mesh[r];
    internal static ModelDef? Model(int r) => _model[r];
    internal static int Node(int r) => _node[r];
    internal static int Cls(int r) => _cls[r];
    internal static RecipeFlag Flags(int r) => _flags[r];
    internal static int Src(int r) => _src[r];
    internal static int Dst(int r) => _dst[r];
    internal static int Prim(int r) => _prim[r];
    internal static int Feat(int r) => _feat[r];
    internal static int Slots(int r) => _slots[r];
    internal static Vector4 Ambient(int r) => _amb[r];
    internal static Vector4 Diffuse(int r) => _dif[r];
    internal static float Cos(int r) => _cos[r];
    internal static float Sin(int r) => _sin[r];
    internal static Vector3 Centre(int r) => _centre[r];
    internal static float Radius(int r) => _radius[r];
    /// <summary>The mesh-arena range this recipe draws from, -1 when the arena could not hold it.</summary>
    internal static int Range(int r) => _range[r];
    internal static float Param(int r, int k) => _param[r * ParamFloats + k];

    internal static float FeatF(int r, int k) => _featF[r * 4 + k];
    /// <summary>The resident bindless handle for one texture slot, or 0 -- which the draw submits the white
    /// stand-in for: an explicit, counted state, never a hole.</summary>
    internal static ulong TexHandle(int r, int s) => TexRegistry.Handle(new TexId(_texId[r * TexSlots + s]));
    /// <summary>The registry slot one texture slot names, so a record feed can register the part it is
    /// writing as a dependent (<c>TexRegistry.AddDependent</c>) and be TOLD when the texture lands
    /// instead of re-resolving it every frame.</summary>
    internal static TexId Tex(int r, int s) => new(_texId[r * TexSlots + s]);
    /// <summary>Is this slot's diffuse a PLT, i.e. does its record need the per-instance palette rows?</summary>
    internal static bool TexIsPlt(int r, int s) => TexRegistry.KindOf(new TexId(_texId[r * TexSlots + s])) == TexKind.Plt;
    internal static string TexName(int r, int s) => _tex[r * TexSlots + s] ?? "";

    /// <summary>The recipe for node <paramref name="node"/> of <paramref name="model"/>, building it on first
    /// ask; -1 when the node draws nothing or the table is full. <paramref name="plan"/> says whether the PART
    /// asking was built through the merge plan: one model is placed both ways (a static placeable takes the tile
    /// path, a usable one of the same model the plain tree), so the two get separate recipes.</summary>
    internal static int Intern(ModelDef model, int node, bool plan = true)
    {
        if ((uint)node >= (uint)model.Nodes.Length) return -1;
        if (_byNode.TryGetValue((model, plan ? ~node : node), out int r)) return r;
        NodeDef nd = model.Nodes[node];
        MeshDef? mesh = nd.Mesh;
        // CX-114: on a TILE model the geometry a node draws is the merge plan's, not its own -- a bucket head
        // draws the merged model-space mesh (which is why its part's pose is zeroed) and a node the bucket
        // swallowed draws nothing at all. Resolving `nd.Mesh` here drew every tile's UNMERGED pieces at the
        // head's zeroed pose AND drew the swallowed ones on top: the flat lavender mass.
        if (plan)
        {
            MeshDef? planned = PartTree.PlanMeshFor(model, node, out bool swallowed);
            if (swallowed) return -1;
            if (planned != null) mesh = planned;
        }
        if (mesh == null || !mesh.Render || mesh.Indices.Length == 0) return -1;
        if (TwinShadowed(model, node)) { TwinRefused++; return -1; }
        r = Take();
        if (r < 0) return -1;
        _byNode[(model, plan ? ~node : node)] = r;
        _model[r] = model; _node[r] = node; _mesh[r] = mesh;
        Fill(r, nd, mesh);
        return r;
    }

    /// <summary>Re-ask the mesh arena and the texture registry for every recipe whose answer was deferred.
    /// Called once per frame from the feed, so a mesh or texture that lands late joins its draws on the next
    /// frame rather than on the next area change. Allocation-free.</summary>
    internal static int Pump()
    {
        foreach (int c in _own.Cooling) _own.Free.Push(c);
        _own.Cooling.Clear();
        _own.Cooling.AddRange(_own.Retiring);
        _own.Retiring.Clear();
        int landed = 0;
        // R1-7: the ranges that were NOT resident, drained. The whole-table walk this replaces re-asked the
        // arena for every recipe every frame, inside the frame's own dirty fold.
        int w = 0;
        for (int k = 0; k < _rangeWaitN; k++)
        {
            int r = _rangeWait[k];
            if (_mesh[r] == null) { _rangeQueued[r] = false; continue; }
            if (_range[r] < 0) _range[r] = MeshArena.Acquire(_mesh[r]!);
            if (_range[r] >= 0) { _rangeQueued[r] = false; landed++; continue; }
            _rangeWait[w++] = r;
        }
        _rangeWaitN = w;
        if (_texPending == 0) return landed;
        // The texture half is the registry's own pending gate: a slot that is still Loading has told the
        // registry which parts depend on it (TexRegistry.AddDependent, from StageDbPart), and the arrival
        // marks those parts dirty -- so this only has to notice that the WAIT is over.
        for (int r = 0; r < _n && _texPending > 0; r++)
            for (int s = 0; s < TexSlots; s++)
            {
                int o = r * TexSlots + s;
                if (_texWait[o] == 0) continue;
                TexState st = TexRegistry.StateOf(new TexId(_texId[o]));
                if (st == TexState.Loading) continue;
                _texWait[o] = 0; _texPending--; landed++;
            }
        return landed;
    }

    /// <summary>This recipe's class from its content and its diffuse's measured coverage. Split out because it
    /// is re-asked: the first ask happens while the texture is still loading. Water is read here rather than taken
    /// from the caller because half of the term is the DIFFUSE's (<see cref="TexRegistry.BumpyShiny"/>), unknown
    /// until the texture lands -- the same deferral the coverage has, settled by the same <see cref="Reclassify"/>.</summary>
    private static AlloyClass ClassOf(int r, bool additive, bool declared)
    {
        var diffuse = new TexId(_texId[r * TexSlots]);
        TexCoverage cov = TexRegistry.Coverage(diffuse);
        bool opaque = cov == TexCoverage.Opaque;
        bool water = _declWater[r] || (PartTable.BumpyWaterOn && TexRegistry.BumpyShiny(diffuse));
        // MEASURED alpha decides opaque-or-not and NOTHING else. Blending is a DECLARED thing: the engine's
        // Material::GetIsOpaque @0x4c6190 reads the material's own `transparency`, an env map and a
        // non-default destination blend, and only then asks the diffuse whether it has alpha at all -- and
        // the answer to that last question lands a part in the alpha-TESTED bucket, which is also where the
        // engine path puts it (AlloyCensus passes the material's transparency keyword and the part's alpha,
        // never a texture measurement). Reading "this texture has fractional alpha" as "blend it" made every
        // equipped weapon and shield translucent: an NWN weapon's diffuse alpha is a REFLECTIVITY mask for
        // its environment map (w_metal_tex.txi: `envmaptexture TTR01__ref01`, 80% of its texels graded;
        // ashlw_031: a tenth of them at 164), and vanilla answers OPAQUE for an env-mapped material outright
        // -- GetIsOpaque returns the HasEnvMap flag itself and never looks at the texture.
        // CX-189: NWN_ALPHA_MODE 2 is the author saying "coverage, not blending" -- a declared transparency
        // on the same material is the vanilla fallback for clients without it, so it does not make this
        // blended. CX-224: nor does the measured diffuse make it opaque -- the classifier's own coverage term.
        return AlloyClasses.Of(water: water, additive: additive, opaque: opaque, declaredTransparent: declared,
                               alphaBelowOne: false, coverage: _coverage[r]);
    }

    /// <summary>Is this material DECLARED water, i.e. water by a term the MODEL carries? The <c>.mtr</c>'s
    /// <c>sample_framebuffer 2</c> is the engine's second route into draw bucket 6 (the first is the
    /// diffuse's <c>.txi</c>, read in <see cref="ClassOf"/>), and <c>AlloyCensus.IsWater</c> reads exactly
    /// these two on the engine path, so the modes classify water on one pair of terms.</summary>
    private static bool DeclaredWater(MeshDef mesh)
    {
        if (mesh.RenderHint.Equals("water", StringComparison.OrdinalIgnoreCase)) return true;
        MtrDef? mtr = MtrDef.Of(mesh);
        return mtr != null && mtr.SampleFramebuffer == AlloyCensus.SampleFbWater;
    }

    /// <summary>Re-ask the class -- and the latched bumpy-shiny half of
    /// <see cref="RecipeFlag.RotateTexture"/> -- of every recipe whose diffuse has landed since it was
    /// interned, and report how many MOVED. A move means the records of every part on that recipe have to be
    /// rewritten, which is what the texture's own arrival edge already marks them for.</summary>
    internal static int Reclassify()
    {
        // Every input is a landed texture's answer, a landed .mtr or the bumpy-water global; a frame where
        // none moved has nothing to re-ask (this walked all recipes with string compares, twice a frame).
        int serial = System.Threading.Volatile.Read(ref TexRegistry.CoverageSerial);
        bool material = _own.MtrSerial != _own.MtrSeen;
        if (serial == _classSerial && PartTable.BumpyWaterOn == _classWater && !material) return 0;
        _classSerial = serial; _classWater = PartTable.BumpyWaterOn;
        _own.MtrSeen = _own.MtrSerial;
        int moved = 0;
        for (int r = 0; r < _n; r++)
        {
            MeshDef? mesh = _mesh[r];
            if (mesh == null) continue;
            // A .mtr the resource layer read only after this recipe was interned carries the two terms
            // Fill took from the material and nothing else re-asks -- so a mode-2 material discovered late
            // would never reach the coverage layer, and a late sample_framebuffer never the water pass.
            if (material)
            {
                _coverage[r] = MtrDef.Of(mesh) is { AlphaMode: 2 };
                _declWater[r] = DeclaredWater(mesh);
            }
            bool additive = mesh.RenderHint.Equals("additive", StringComparison.OrdinalIgnoreCase);
            byte wasCls = _cls[r], wasDst = _dst[r];
            RecipeFlag wasFlags = _flags[r];
            TxiWords(r, additive, mesh.TransparencyHint > 0);
            if (TexRegistry.BumpyShiny(new TexId(_texId[r * TexSlots]))) _flags[r] |= RecipeFlag.RotateTexture;
            if (_cls[r] == wasCls && _flags[r] == wasFlags && _dst[r] == wasDst) continue;
            if (_cls[r] != wasCls) Reclassified++;
            moved++;
        }
        return moved;
    }

    /// <summary>The diffuse <c>.txi</c>'s material words over the model's own (<c>Material::ParseField</c> @0x5c5850):
    /// <c>blending additive</c> is the (SRC_ALPHA, ONE) pair and so the additive class -- no depth write, no shadow --
    /// and <c>decal 1</c> is the fullbright flag. Known once the diffuse's acquire offered it, so <see cref="Fill"/>
    /// asks after its texture loop and <see cref="Reclassify"/> again for an offer that waited.</summary>
    private static void TxiWords(int r, bool hintAdditive, bool declared)
    {
        var d = new TexId(_texId[r * TexSlots]);
        bool additive = hintAdditive || TexRegistry.TxiAdditive(d);
        _dst[r] = additive ? BlendOne : BlendInvSrcAlpha;
        if (hintAdditive || TexRegistry.TxiDecal(d)) _flags[r] &= ~RecipeFlag.Lit;
        else _flags[r] |= RecipeFlag.Lit;
        _cls[r] = (byte)ClassOf(r, additive, declared);
        if (AlloyClasses.Covers(_cls[r], _coverage[r])) _flags[r] |= RecipeFlag.Coverage;
        else _flags[r] &= ~RecipeFlag.Coverage;
    }

    private static int _classSerial = -1;
    private static bool _classWater;

    /// <summary>The resource layer read a <c>.mtr</c> that was not cached before, so the next
    /// <see cref="Reclassify"/> re-asks the material's own terms. Called from the feed that read it.</summary>
    internal static void MaterialLanded() => _own.MtrSerial++;

    /// <summary>The same edge for the PART columns, consumed: true once however many materials landed, so
    /// <see cref="PartTree.ReseedMaterials"/> walks at most once a frame and not at all on a quiet one.</summary>
    internal static bool TakePartMaterialEdge()
    {
        if (_own.MtrSeenParts == _own.MtrSerial) return false;
        _own.MtrSeenParts = _own.MtrSerial;
        return true;
    }

    /// <summary>Recipes whose class moved once their diffuse's coverage was known.</summary>
    internal static int Reclassified { get; private set; }

    /// <summary>The water census: how many recipes are classified water, how many have a diffuse whose
    /// <c>.txi</c> names a bumpy-shiny map, and whether the engine's own gate on that route is live. "The
    /// water shader does not run" is a question about these three and nothing else.</summary>
    internal static string WaterNote()
    {
        int water = 0, bumpy = 0, declared = 0;
        for (int r = 0; r < _n; r++)
        {
            if (_mesh[r] == null) continue;
            if (_cls[r] == (byte)AlloyClass.Water) water++;
            if (TexRegistry.BumpyShiny(new TexId(_texId[r * TexSlots]))) bumpy++;
            if (_declWater[r]) declared++;
        }
        return $"water {water} recipe(s) ({bumpy} bumpy-shiny diffuse, {declared} declared, "
             + $"{AlloyRenderer.DbReclassSlots} slot(s) reclassified), "
             + $"g_nEnableBumpyWater {(PartTable.BumpyWaterOn ? "on" : "OFF")}";
    }

    private static void Fill(int r, NodeDef nd, MeshDef mesh)
    {
        _amb[r] = new Vector4(mesh.Ambient, 1f);
        _dif[r] = new Vector4(mesh.Diffuse, 1f);
        // #126's blend pair as the engine states it: an .mdl declares none, so the default pair is the
        // engine's own (SRC_ALPHA, ONE_MINUS_SRC_ALPHA) and a renderhint is what moves it.
        bool additive = nd.ContentFlags != 0 && mesh.RenderHint.Equals("additive", StringComparison.OrdinalIgnoreCase);
        _src[r] = BlendSrcAlpha; _dst[r] = additive ? BlendOne : BlendInvSrcAlpha;
        _declWater[r] = DeclaredWater(mesh);
        // Material::GetTexture @0x5c5810: the .mtr's texture wins wherever it states one.
        MtrDef? mtr = MtrDef.Of(mesh);
        _coverage[r] = mtr is { AlphaMode: 2 };
        // TransparencyHint > 0 is the model's own "this is see-through" (the engine's child-order sort key);
        // a mesh with no alpha anywhere and no hint is genuinely opaque.
        bool declared = mesh.TransparencyHint > 0;
        // R1-8. The engine's own test is Material::GetIsOpaque @0x5c6190, and its terms are: the SharedMaterial's
        // `transparency` keyword, an env map, a non-default destination blend, and -- last -- whether the DIFFUSE
        // actually carries alpha coverage, which it asks the texture's own vtable. The first three are model
        // state and are read here. The fourth is a DECODE question (texture-coverage-is-a-decode-question) and
        // the texture is not resident when a recipe is interned, so it cannot be answered yet.
        // The fourth term is answerable now (CX-117): the decode decides a texture's coverage and the arrival
        // re-dirties its dependents, so a recipe interned before its diffuse landed is reclassified the frame
        // it does. Unknown keeps the conservative guess -- CUTOUT, which is safe rather than merely
        // convenient: a cutout is alpha-tested AND depth-writing, so a diffuse that turns out to be fully
        // opaque passes the 0.2 discard on every texel and draws identically. Guessing OPAQUE the other way
        // would punch the alpha out of every graded leaf and fringe in the game.
        _cls[r] = (byte)ClassOf(r, additive, declared);
        RecipeFlag f = RecipeFlag.None;
        // R1-9. The engine reads the Material's blend flag (mat+0x98): set means it sends flat white
        // and the part is fullbright. `!Lightmapped` was plainly wrong -- a lightmapped tile is still SHADED
        // in vanilla (lightmap x diffuse), so that unlit every tile in the game. The model-side equivalent of
        // the blend flag is the additive renderhint, which is what the materials carrying it declare.
        if (!additive) f |= RecipeFlag.Lit;
        // NOT from shininess. The engine's Env is Material::HasEnvMap (mat+0x40) -- a DECLARED environment
        // map -- and it means "this is shiny 2002 content, so read its diffuse alpha as a reflectivity mask".
        // Nearly every NWN mesh carries some shininess and almost none declares an env map, so inferring one
        // from the other marked the whole world env-mapped and sent every tile down the reflective branch
        // with no probe behind it. A MeshDef states no env map, so until the material path provides one the
        // honest answer is no.
        // ponytail: no env map in DB mode. The upgrade is the .mtr/material term, not a shininess guess.
        if (mesh.RotateTexture) f |= RecipeFlag.RotateTexture;
        if (nd.Kind == NodeKind.Dangly) f |= RecipeFlag.Dangly;
        if (nd.Kind == NodeKind.Skin) f |= RecipeFlag.Skinned;
        if (nd.Kind == NodeKind.AnimMesh) f |= RecipeFlag.AnimMesh;
        if (!mesh.Shadow) f |= RecipeFlag.NoShadow;
        if (mesh.Beaming) f |= RecipeFlag.Beaming;
        if (mesh.TileFade != 0) f |= RecipeFlag.Fades;
        _flags[r] = f;
        _prim[r] = 0;
        int feat = MtrParams.Feature(mtr?.ShaderVs ?? "");
        _feat[r] = feat;
        Span<float> featF = _featF.AsSpan(r * 4, 4);
        featF.Clear();
        featF[0] = feat;
        // The stock PBR vec4s: 0 means "not declared" and the shader synthesises it. NOT the MDL's shininess:
        // the engine never turns it into a roughness, and 1 - shininess/128 read PLT armour as rough metal.
        Span<float> param = _param.AsSpan(r * ParamFloats, ParamFloats);
        param.Clear();
        MtrParams.ResetExt(param[AlloyRenderer.ParamExt..]);
        MtrParams.Fold(mtr, param, featF);
        // CX-223: a palette-tint material takes a row in the side table and the record carries its INDEX in
        // the feature's own feat.z lane, so the tint's terms cost the other hundred thousand records nothing.
        // A table with no row left is the feature OFF for this material -- the plain lit path, which is what
        // the record says too, since an index nothing backs would read another material's glow.
        _own.TintRow[r] = -1;
        if (feat == (int)ShaderFeature.PaletteTint)
        {
            _own.TintRow[r] = TintTerms.Alloc(r);
            if (_own.TintRow[r] < 0) { feat = 0; _feat[r] = 0; featF[0] = 0f; }
            else
            {
                Span<float> tint = stackalloc float[MtrParams.TintTermCount];
                MtrParams.FoldTint(mtr, tint, Log);
                TintTerms.SetTerms(_own.TintRow[r], tint);
                featF[2] = _own.TintRow[r];
            }
        }
        int mask = 0;
        for (int s = 0; s < TexSlots; s++)
        {
            int src = MtrTextureOf(s);
            string name = mtr != null && mtr.Textures[src].Length > 0 ? mtr.Textures[src]
                        : src < mesh.Textures.Length ? mesh.Textures[src] : "";
            // "null" is the MDL's way of writing NO TEXTURE -- the same spelling the supermodel line uses,
            // and the one MdlPost already honours for the lightmap slot. Taken literally it becomes a resref
            // nothing can load, the slot fails, the record gets no handle, and the part draws BLACK: that is
            // the black door-sized quad in the ladder's area 2 (T_Door09, tex0='null', state=Failed, h0=NO).
            if (name.Equals("null", StringComparison.OrdinalIgnoreCase)) name = "";
            _tex[r * TexSlots + s] = name;
            TexId id = string.IsNullOrEmpty(name) ? default : TexRegistry.Acquire(name);
            _texId[r * TexSlots + s] = id.Value;
            bool wait = !id.IsNull && !TexRegistry.Resident(id);
            _texWait[r * TexSlots + s] = wait ? (byte)1 : (byte)0;
            if (wait) _texPending++;
            if (!string.IsNullOrEmpty(name)) mask |= MaskBitOf(s, feat);
        }
        if ((f & RecipeFlag.Env) != 0) mask |= EnvSlotBit;
        _slots[r] = mask;
        // The engine's second route into the tile texture rotation, and it LATCHES: SetPartTextureRotate
        // @0x561670 writes `mdlnode+0x2b9 = 1` on a material with a bumpy-shiny map, i.e. it turns the mesh's
        // own `rotatetexture` on for good. Re-asked in Reclassify because the diffuse's .txi is not read
        // until the texture lands, which is after the recipe is interned.
        if (TexRegistry.BumpyShiny(new TexId(_texId[r * TexSlots]))) _flags[r] |= RecipeFlag.RotateTexture;
        TxiWords(r, additive, declared);
        _cos[r] = 1f; _sin[r] = 0f;
        // A compiled mesh's bmin/bmax is the compiler's word, and community compilers get it wrong; the draw
        // culls on this box, so it is grown over the vertices the arena actually draws.
        Vector3 lo = mesh.BMin, hi = mesh.BMax, flo = lo, fhi = hi;
        foreach (Vector3 v in mesh.Positions) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
        if (lo != flo || hi != fhi)
        {
            if (BoxGrown++ == 0) BoxGrownFirst = $"{_model[r]?.Name}:{nd.Name} {flo}..{fhi} -> {lo}..{hi}";
        }
        _centre[r] = (lo + hi) * 0.5f;
        Vector3 ex = hi - lo;
        _radius[r] = 0.5f * ex.Length();
        _box[r * 6 + 0] = lo.X; _box[r * 6 + 1] = lo.Y; _box[r * 6 + 2] = lo.Z;
        _box[r * 6 + 3] = hi.X; _box[r * 6 + 4] = hi.Y; _box[r * 6 + 5] = hi.Z;
        _range[r] = MeshArena.Acquire(mesh);
        if (_range[r] < 0 && !_rangeQueued[r] && _rangeWaitN < _rangeWait.Length)
        { _rangeWait[_rangeWaitN++] = r; _rangeQueued[r] = true; }
    }

    /// <summary>R1-15 + tex-3: retire every recipe of <paramref name="model"/>. Subscribed to
    /// <c>ModelCache.Evicting</c>, which is the ONE edge that says a model is going: without it a recipe held
    /// a strong reference to the <see cref="ModelDef"/> and a reference to each of its texture slots, so the
    /// model cache could never actually free a model and <c>texcache_mb</c> could never evict a texture.</summary>
    internal static void Forget(ModelDef model)
    {
        for (int r = 0; r < _n; r++)
        {
            if (!ReferenceEquals(_model[r], model) || _own.Orphan[r]) continue;
            if (_byNode.TryGetValue((model, _node[r]), out int a) && a == r) _byNode.Remove((model, _node[r]));
            if (_byNode.TryGetValue((model, ~_node[r]), out int b) && b == r) _byNode.Remove((model, ~_node[r]));
            if (_own.Users[r] > 0)
            {
                _own.Orphan[r] = true;
                if (_own.RetiredInUse++ < 8)
                    Log?.Always($"DrawRecipe: model '{model.Name}' was evicted while {_own.Users[r]} part(s) still draw "
                              + $"node {_node[r]} -- the recipe is kept until they let go (a model reference was dropped too early)");
                continue;
            }
            Retire(r);
        }
    }

    /// <summary>Recipes retired with their model.</summary>
    internal static int Retired { get; private set; }

    /// <summary>Subscribe to the model cache's eviction edge. Multicast (<c>+=</c>), because the cache's other
    /// listeners are not ours to replace.</summary>
    internal static void Attach()
    {
        if (_attached) return;
        _attached = true;
        ModelCache.Evicting += Forget;
    }

    private static bool _attached;

    /// <summary>The recipe's local box, six floats (min then max), into <paramref name="dst"/>.</summary>
    internal static void Box(int r, Span<float> dst)
    {
        for (int k = 0; k < 6; k++) dst[k] = _box[r * 6 + k];
    }

    /// <summary>The env-map bit of the slot mask: the material declares an env map with no texture behind it (the
    /// per-area probe replaces what it held). It is <see cref="AlloyRenderer.EnvSlot"/> and nothing else -- that
    /// value is FROZEN at 64 because shipped <c>cx_ownlit_fs.shd</c> overrides carry a copy of it, so any second
    /// definition here is a fact that can drift from content on disk.</summary>
    internal const int EnvSlotBit = AlloyRenderer.EnvSlot;
    /// <summary><c>Aurora::AuroraBlendModes</c> values, which <see cref="Issue.BlendGl"/> translates to
    /// <c>glBlendFunc</c> factors. These are INDICES into the engine's <c>blendTranslationTable</c> @0x12f5040,
    /// which <c>GLRender::SetBlendFunc</c> @0x5cb0c0 indexes with both arguments; in .rodata that table is
    /// <c>SRC_ALPHA, ONE_MINUS_SRC_ALPHA, ONE, ZERO, DST_COLOR, SRC_COLOR, ONE_MINUS_DST_ALPHA</c>.</summary>
    internal const byte BlendSrcAlpha = 0, BlendInvSrcAlpha = 1, BlendOne = 2;

}

/// <summary>What a <see cref="DrawRecipe"/> says about its node, in place of the engine vtable probes and
/// material-flag reads the draw used to make per frame.</summary>
[Flags]
internal enum RecipeFlag : ushort
{
    None = 0,
    /// <summary>The engine would draw this part LIT (it is not lightmapped into its own vertices).</summary>
    Lit = 1 << 0,
    /// <summary>The material declares an env map: the "this is shiny" bit the stock shaders read.</summary>
    Env = 1 << 1,
    /// <summary>The material's <c>twosided</c>: draws with face culling off.</summary>
    TwoSided = 1 << 2,
    /// <summary>A graded-alpha cutout re-filed as blended, drawn with no depth write.
    /// ponytail: nothing sets it; the wash path behind it is dormant until something does.</summary>
    Wash = 1 << 3,
    /// <summary>A dangly mesh: a permanent shadow mover, posed by the solver.</summary>
    Dangly = 1 << 4,
    /// <summary>A skinmesh: posed by a bone palette.</summary>
    Skinned = 1 << 5,
    /// <summary>An animmesh: its vertices are morphed per frame.</summary>
    AnimMesh = 1 << 6,
    /// <summary>The mesh asked not to cast.</summary>
    NoShadow = 1 << 7,
    /// <summary>The mesh's <c>beaming</c> keyword.</summary>
    Beaming = 1 << 8,
    /// <summary>The mesh takes part in tile fade (its <c>tilefade</c> is non-zero).</summary>
    Fades = 1 << 9,
    /// <summary>The mesh's texture rotates (<c>rotatetexture</c>).</summary>
    RotateTexture = 1 << 10,
    /// <summary>A cutout whose material asks for alpha-to-coverage (<c>NWN_ALPHA_MODE 2</c>).</summary>
    Coverage = 1 << 11,
}
