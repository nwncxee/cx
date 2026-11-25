using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary><b>The stable half of a draw, interned per <c>(ModelDef, node)</c>.</b> Every term in it is a
/// property of the MODEL, not of the instance -- a thousand doors of one resref share one recipe -- so a part
/// needs only <see cref="SceneDb.PartRecipe"/>. Interned, not cached: a recipe is immutable and lives as long as
/// its <see cref="ModelDef"/>, so its index is safe to hold across frames and threads with no identity test.</summary>
internal static partial class DrawRecipe
{
    /// <summary>Distinct recipes held at once: a long session's worth of areas. A full table evicts idle models to
    /// make room (<see cref="MakeRoom"/>), so only what the area draws can fill it.</summary>
    internal const int Max = 1 << 16;

    /// <summary>Texture slots a recipe names: diffuse, the seven map slots (five maps, flow, AO), then the
    /// four PALETTE slots. The palette four are held but never BOUND -- the lit stage spends texture units
    /// 0..30, so they reach their one reader as handles in <see cref="ContentTable"/>' row.</summary>
    internal const int TexSlots = 1 + AlloyRenderer.MapSlots + AlloyRenderer.PalSlots;
    /// <summary>The map slot of <c>texture6</c>, which a content pair's stage reads as <c>texUnit6</c>.</summary>
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

    /// <summary>The recipe slot holding material unit <paramref name="unit"/> (<c>texUnit1</c>..<c>texUnit10</c>): the map
    /// block's own index up to <c>texUnit6</c>, then the four palette slots past the AO slot.</summary>
    internal static int SlotOfUnit(int unit) => unit < AlloyRenderer.PalTexture0 ? unit : PalRecipeSlot0 + unit - AlloyRenderer.PalTexture0;

    /// <summary>The <see cref="ContentTable"/> row a part of object <paramref name="obj"/> reads on recipe
    /// <paramref name="recipe"/>: the object's own when it carries values, else the material's. -1 for no content pair,
    /// and for a full table with no material row.</summary>
    internal static int ContentRowFor(int recipe, DbId obj)
    {
        if (!ShaderFeatures.IsContent((int)FeatF(recipe, 0))) return -1;
        if (Seam.MaterialParams.HasContent(obj))
        {
            int own = ContentTable.ObjectRow(recipe, obj.Value);
            if (own >= 0) return own;
        }
        return (int)FeatF(recipe, 2);
    }

    /// <summary>The slot-mask bit a bound recipe slot sets: none for the diffuse, the slot-6 and the four palette
    /// slots only for a content pair, the stages that can name them (vanilla's own <c>texture6Bound</c> test), and AO
    /// on its own out-of-line bit.</summary>
    internal static int MaskBitOf(int s, int feat)
        => s == 0 ? 0
         : s >= PalRecipeSlot0
             ? (ShaderFeatures.ReadsExtraSlots(feat) ? AlloyRenderer.PalSlotBit(s - PalRecipeSlot0) : 0)
         : s - 1 == FlowSlot && !ShaderFeatures.ReadsExtraSlots(feat) ? 0
         : AlloyRenderer.SlotBit(s - 1);

    /// <summary>The recipe slot that holds the normal map (<c>CX_SLOT_NORMAL</c>).</summary>
    private const int NormalRecipeSlot = 1, RoughRecipeSlot = 3, HeightRecipeSlot = 4;

    /// <summary>Does recipe <paramref name="r"/> take a derived normal map: a lit mesh with a plain or PLT diffuse, no content
    /// pair, not additive, no dangly or morphing motion, and not a head (a face would crater).</summary>
    private static bool DerivesNormal(int r, NodeDef nd, int feat, bool additive)
        => Lighting.AiNormals.Boot && !additive && feat == 0 && !string.IsNullOrEmpty(TexName(r, 0))
           && nd.Kind is not (NodeKind.Dangly or NodeKind.AnimMesh) && !_declWater[r] && !IsHead(_model[r]?.Name);

    /// <summary>A character's head model (<c>p??#_head###</c>): faces keep their own look, whatever armour around them takes.</summary>
    internal static bool IsHead(string? model) => model != null && model.Contains("_head", StringComparison.OrdinalIgnoreCase);

    /// <summary>The resref recipe slot <paramref name="s"/> binds: the material's <c>textureN</c> where it
    /// declares one, else the MODEL's own word for that slot -- which past the diffuse only counts on a mesh
    /// that has a tangent frame (<see cref="ModelMaps"/>). The material's spec, roughness, height and
    /// self-illumination maps count only where the engine's stage reads them (<see cref="StageReadsMaps"/>).</summary>
    internal static string SlotName(int s, MtrDef? mtr, MeshDef mesh)
    {
        int src = MtrTextureOf(s);
        string name = mtr != null && mtr.Textures[src].Length > 0
                      ? (s is >= SpecRecipeSlot and <= IllumRecipeSlot && !StageReadsMaps(mtr, mesh) ? "" : mtr.Textures[src])
                    : s != 0 && !ModelMaps(mesh) ? ""
                    : src < mesh.Textures.Length ? mesh.Textures[src] : "";
        // "null" is the MDL's NO TEXTURE; taken as a resref the slot fails and the part draws black
        return name.Equals("null", StringComparison.OrdinalIgnoreCase) ? "" : name;
    }

    /// <summary>Do this mesh's own <c>texture1</c>..<c>texture3</c> reach the map slots? Only with a tangent
    /// frame: <c>GLRender::GetShaderByAttributes</c> @0x5cfa40 picks a <c>*_nm</c> program only for a vertex
    /// object carrying both the tangent and bitangent streams, and no other stock program so much as declares
    /// those sampler units -- so on any other mesh the words name a texture the engine never reads.</summary>
    private static bool ModelMaps(MeshDef mesh) => mesh.Tangents.Length != 0 || mesh.RenderHintId is 2 or 3;

    /// <summary>The recipe slots of <c>texture2</c> (spec) through <c>texture5</c> (self-illumination): the maps
    /// <c>fslit</c> compiles out and every <c>*_nm</c> stage reads.</summary>
    private const int SpecRecipeSlot = 2, IllumRecipeSlot = 5;

    /// <summary>Does the stage the engine draws <paramref name="mesh"/> with read <paramref name="mtr"/>'s spec,
    /// roughness, height and self-illumination maps? A named pair always runs (<c>GLRender::RenderVertexData</c>
    /// @0x5d0570 skips <c>GetShaderByAttributes</c> for it); otherwise only a <c>*_nm</c> stage, a tangent frame's (the model's, or the material's hint where the node states none).</summary>
    private static bool StageReadsMaps(MtrDef mtr, MeshDef mesh)
        => mtr.ShaderVs.Length != 0 && mtr.ShaderFs.Length != 0 || mesh.RenderHintId == 0 && mtr.RenderHint is 2 or 3 || ModelMaps(mesh);

    /// <summary>Palette slot <paramref name="k"/>'s bindless handle on recipe <paramref name="r"/>, for the
    /// one reader that takes them that way (<see cref="ContentTable.Upload"/>). 0 until the texture is resident.</summary>
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
        /// <summary>Each recipe's row in <see cref="ContentTable"/>, or -1 -- the value a content record carries in
        /// <c>feat.z</c>. Seeded -1, because a zero-filled column would hand the plain majority row 0 and retire it
        /// out from under whoever owns it.</summary>
        public readonly int[] ContentRow = new int[Max];
        /// <summary>Each recipe's parsed <c>.mtr</c>, or null: what a content pair's row is filled from
        /// (<see cref="ContentRows"/>). Shared and immutable, so the column holds the reference.</summary>
        public readonly MtrDef?[] Mtr = new MtrDef?[Max];
        /// <summary>Each recipe's material face terms (<see cref="FacesOf"/>). Its own column because a late
        /// <c>.mtr</c> moves these terms but not <see cref="Mtr"/>, which the content row was built from.</summary>
        public readonly byte[] FaceTerms = new byte[Max];

        public SlotState() { System.Array.Fill(ContentRow, -1); }
        /// <summary>The frame line's recipe counts.</summary>
        public Tally Counts = new() { BoxGrownFirst = "" };
        /// <summary>A self-check's smaller table (<see cref="CapOverride"/>), or null for <see cref="Max"/>.</summary>
        public int? CapOverride;
        /// <summary><c>.mtr</c> reads that landed, against what the last <see cref="Reclassify"/> saw.</summary>
        public int MtrSerial, MtrSeen;
        /// <summary>Interns and reclassifications of a CONTENT recipe (<see cref="ContentSerial"/>).</summary>
        public int ContentSerial;
        public CX.Framework.ExtenderLog? Log;
    }
    private static SlotState _own = new();

    /// <summary>The frame line's recipe counts, one value so the session edge zeroes them all at once.</summary>
    internal record struct Tally
    {
        public int Spilled, TwinRefused, BoxGrown, Retired, Reclassified, RetiredInUse, DoubleRetire;
        /// <summary>Models evicted this period to make room in the table, beside the memory and texture budgets'.</summary>
        public int RoomEvicted;
        /// <summary>Recipes found as painted water (<see cref="WaterLookOf"/>) and said on cx.log, to a cap.</summary>
        public int WaterLogged;
        public string BoxGrownFirst;
    }

    /// <summary>The session's frame line starts from no recipe counts.</summary>
    internal static void SessionBegins() => _own.Counts = new() { BoxGrownFirst = "" };

    /// <summary>The log the ownership refusals are said on. Set by the feed.</summary>
    internal static CX.Framework.ExtenderLog? Log { get => _own.Log; set => _own.Log = value; }

    /// <summary>Recipe <paramref name="r"/>'s parsed <c>.mtr</c>, or null.</summary>
    internal static MtrDef? Mtr(int r) => (uint)r < Max ? _own.Mtr[r] : null;

    /// <summary>Slot <paramref name="r"/>'s current hand-out generation.</summary>
    internal static uint Gen(int r) => (uint)r < Max ? _own.Gen[r] : 0;

    /// <summary>Is slot <paramref name="r"/> handed out? A retired slot keeps its columns until a new tenant
    /// fills them, so a walk of the whole table asks this before reading one.</summary>
    internal static bool Held(int r) => (uint)r < Max && _own.Held[r];

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
    private static readonly ParamRow[] _param = new ParamRow[Max];
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
    private static readonly Box[] _box = new Box[Max];
    private static readonly int[] _range = new int[Max];
    /// <summary>The recipe's material DECLARES water (<see cref="DeclaredWater"/>) -- the model-side half of
    /// the engine's water test, kept per recipe because <see cref="Reclassify"/> re-asks the class of every
    /// recipe every frame and resolving a <c>.mtr</c> there would be a dictionary lookup per recipe per
    /// frame.</summary>
    private static readonly bool[] _declWater = new bool[Max];
    /// <summary>The material's <c>NWN_ALPHA_MODE 2</c> -- draw this part through alpha-to-coverage
    /// (<see cref="RecipeFlag.Coverage"/>) instead of blending it. Kept per recipe for <see cref="Reclassify"/>.</summary>
    private static readonly bool[] _coverage = new bool[Max];
    /// <summary>The material declares <c>transparency</c> and its own fragment stage sets the fragment's alpha after
    /// its lighting call (<see cref="StageBlends"/>): the engine blends that alpha, so the part is blended.</summary>
    private static readonly bool[] _stageBlend = new bool[Max];
    /// <summary>The material's content-program key (<see cref="GuiContent.Key"/>; 0 = none), which a GUI part draws
    /// through. Re-read with the other late material terms.</summary>
    private static readonly int[] _content = new int[Max];
    /// <summary>Each recipe's foliage class (<see cref="LeafOf"/>), re-asked with its class in <see cref="TxiWords"/>.</summary>
    private static readonly byte[] _leaf = new byte[Max];
    /// <summary>Each recipe's water look (<see cref="WaterLookOf"/>), re-asked with its class in <see cref="TxiWords"/>.</summary>
    private static readonly bool[] _waterLook = new bool[Max];
    /// <summary>Why an alpha-tested recipe is no leaf (<see cref="LeafWhyContent"/>, <see cref="LeafWhyOther"/>), for /alloy viz 55.</summary>
    private static readonly byte[] _leafWhy = new byte[Max];
    /// <summary>An alpha-tested recipe refused as a leaf: not refused, drawn by a content pair's own stage, or another reason.</summary>
    internal const byte LeafWhyNone = 0, LeafWhyContent = 1, LeafWhyOther = 2;

    /// <summary>The foliage classes a recipe can carry: none, a thin cut-out card (light passes through it from
    /// behind), or foliage proper (also shaded as one rounded canopy).</summary>
    internal const byte LeafNone = 0, LeafThin = 1, LeafFoliage = 2;
    /// <summary>Name fragments stock and community foliage carries in its texture, node or model name. One vote,
    /// never the verdict alone: a name is the builder's habit, not a measurement.</summary>
    private static readonly string[] LeafWords =
        ["leaf", "leav", "foli", "tree", "bush", "shrub", "hedge", "ivy", "vine", "fern", "frond", "branch", "canop", "willow"];
    /// <summary>Words that hold a <see cref="LeafWords"/> fragment and name no plant.</summary>
    private static readonly string[] LeafFalseWords = ["street", "ravine", "divine"];
    /// <summary>A leafy mean colour: green over red by this much, and over blue by <see cref="LeafGreenOverBlue"/>.
    /// Measured over the whole diffuse, holes included, so the ratios matter rather than the level.</summary>
    private const float LeafGreenOverRed = 0.95f, LeafGreenOverBlue = 1.2f, LeafWarmOverBlue = 1.5f;
    /// <summary>Votes a thin card needs to be foliage: a dangly mesh is two, a leafy name and a leafy colour one each.</summary>
    private const int LeafVotesDangly = 2, LeafVotesNeeded = 2;

    /// <summary>Recipes in use: the slots ever handed out, less the retired ones waiting or free.</summary>
    internal static int Count => _n - _own.Free.Count - _own.Retiring.Count - _own.Cooling.Count;

    /// <summary>Recipes the table holds at once: <see cref="Max"/>, or a self-check's smaller
    /// <see cref="CapOverride"/>.</summary>
    internal static int Cap => _own.CapOverride ?? Max;
    /// <summary>A smaller table for the self-checks ONLY, so an example can fill it off-device.</summary>
    internal static int? CapOverride { get => _own.CapOverride; set => _own.CapOverride = value; }

    /// <summary>A slot for a new recipe: a retired one past its quarantine, else a fresh one; -1 when full.</summary>
    private static int Take()
    {
        int r;
        if (Count >= Cap) { _own.Counts.Spilled++; return -1; }
        if (_own.Free.Count > 0) r = _own.Free.Pop();
        else if (_n < Max) r = _n++;
        else { _own.Counts.Spilled++; return -1; }
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
        if (!_own.Held[r]) { _own.Counts.DoubleRetire++; return; }
        _own.Held[r] = false; _own.Orphan[r] = false;
        if (_range[r] >= 0) MeshArena.Release(_range[r]);
        _range[r] = -1;
        ReleaseMaterial(r);
        _model[r] = null; _mesh[r] = null;
        _rangeQueued[r] = false;
        _own.Retiring.Add(r);
        _own.Counts.Retired++;
    }

    /// <summary>Give back what recipe <paramref name="r"/>'s material holds: its content row and its texture references.</summary>
    private static void ReleaseMaterial(int r)
    {
        if (_own.ContentRow[r] >= 0) { ContentTable.Free(_own.ContentRow[r], r); _own.ContentRow[r] = -1; }
        _own.Mtr[r] = null;
        _own.FaceTerms[r] = 0;
        for (int s = 0; s < TexSlots; s++)
        {
            int o = r * TexSlots + s;
            TexRegistry.Release(new TexId(_texId[o]));
            _texId[o] = 0; _tex[o] = "";
            if (_texWait[o] != 0) { _texWait[o] = 0; _texPending--; }
        }
    }

    /// <summary>A server's override changed the materials in <paramref name="names"/>: every held recipe drawing one
    /// of them works its material terms and texture slots out again, in place, so the parts on it keep their recipe.
    /// Answers how many did. Game thread, cold.</summary>
    internal static int Refresh(HashSet<string> names)
    {
        int n = 0;
        for (int r = 0; r < _n; r++)
        {
            MeshDef? mesh = _mesh[r];
            ModelDef? model = _model[r];
            if (!_own.Held[r] || mesh == null || model == null || (uint)_node[r] >= (uint)model.Nodes.Length
                || !names.Contains(MtrDef.NameOf(mesh))) continue;
            // a dynamic dangly recipe draws solved vertices and carries no palette, whatever the node's rig says
            RecipeFlag skinned = _flags[r] & RecipeFlag.Skinned;
            ReleaseMaterial(r);
            FillMaterial(r, model.Nodes[_node[r]], mesh);
            _flags[r] = (_flags[r] & ~RecipeFlag.Skinned) | skinned;
            TexRegistry.WatchMask(Tex(r, 0));
            n++;
        }
        return n;
    }

    /// <summary>Off-device: a held slot carrying exactly <paramref name="flags"/> and no model, mesh, range or
    /// texture, for a check that drives a path keyed on the flags; -1 when the table is full.
    /// <see cref="ReleaseForCheck"/> hands it back.</summary>
    internal static int FlagsForCheck(RecipeFlag flags)
    {
        int r = Take();
        if (r < 0) return -1;
        // Take() leaves the columns as the last tenant left them, and a fresh slot's _range reads 0 --
        // a real range index, which Retire would hand back out from under its owner.
        _range[r] = -1;
        _flags[r] = flags;
        _leaf[r] = LeafNone;
        _leafWhy[r] = LeafWhyNone;
        _waterLook[r] = false;
        return r;
    }

    /// <summary>A recipe's four feature lanes, by what a content pair's record reads of them.</summary>
    private struct FeatRow { public float Feat, Y, Row, W; }

    /// <summary>Off-device: recipe <paramref name="r"/> reads as a content pair's, on material row <paramref name="row"/>.</summary>
    internal static void ContentForCheck(int r, int feat, int row)
    {
        ref FeatRow f = ref MemoryMarshal.Cast<float, FeatRow>(_featF.AsSpan())[r];
        f.Feat = feat;
        f.Row = row;
    }

    /// <summary>Give back a slot <see cref="FlagsForCheck"/> handed out.</summary>
    internal static void ReleaseForCheck(int r)
    {
        if (r < 0) return;
        _flags[r] = 0;
        Retire(r);
    }

    /// <summary>A retire refused because the slot was not held: it would have reached the free list twice.</summary>
    internal static int DoubleRetire => _own.Counts.DoubleRetire;
    /// <summary>Recipes whose model was evicted while a part still named them: kept until that part lets go.</summary>
    internal static int RetiredInUse => _own.Counts.RetiredInUse;

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
    internal static int Spilled => _own.Counts.Spilled;
    /// <summary>Nodes not drawn because a later coincident twin covers them (<see cref="TwinShadowed"/>).</summary>
    internal static int TwinRefused => _own.Counts.TwinRefused;
    /// <summary>Recipes whose file-stated box did not hold their own vertices, and the first one's model:node.
    /// The cull sphere and the area index's box are this box, so an unfixed one culls a visible mesh.</summary>
    internal static int BoxGrown => _own.Counts.BoxGrown;
    internal static string BoxGrownFirst => _own.Counts.BoxGrownFirst;

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
    internal static int Cls(int r) => _cls[r];
    /// <summary>The diffuse measured GRADED alpha (<see cref="TexCoverage.Blended"/>): a soft edge, not a punch-through.</summary>
    internal static bool Graded(int r) => TexRegistry.Coverage(new TexId(_texId[r * TexSlots])) == TexCoverage.Blended;
    /// <summary>The diffuse's coverage is measured, so <see cref="Cls"/> is no longer the conservative guess.</summary>
    internal static bool Measured(int r) => TexRegistry.Coverage(new TexId(_texId[r * TexSlots])) != TexCoverage.Unknown;
    internal static RecipeFlag Flags(int r) => _flags[r];
    internal static int Src(int r) => _src[r];
    internal static int Dst(int r) => _dst[r];
    internal static int Prim(int r) => _prim[r];
    internal static int Feat(int r) => _feat[r];
    /// <summary>Slots ever handed out: the bound of a walk over the whole table, free and retired ones included
    /// (each reads as its last tenant until a new one fills it).</summary>
    internal static int High => _n;
    /// <summary>Bumped whenever a CONTENT recipe is interned or reclassified, so a reader keyed on the content
    /// set walks the table only when its answer can have moved.</summary>
    internal static int ContentSerial => System.Threading.Volatile.Read(ref _own.ContentSerial);
    internal static int Content(int r) => _content[r];
    internal static int Slots(int r) => _slots[r];
    internal static Vector4 Ambient(int r) => _amb[r];
    internal static Vector4 Diffuse(int r) => _dif[r];
    internal static float Cos(int r) => _cos[r];
    internal static float Sin(int r) => _sin[r];
    internal static Vector3 Centre(int r) => _centre[r];
    internal static float Radius(int r) => _radius[r];
    /// <summary>The mesh-arena range this recipe draws from, -1 when the arena could not hold it.</summary>
    internal static int Range(int r) => _range[r];
    /// <summary>The material terms this recipe stages into its record.</summary>
    internal static ParamRow Params(int r) => _param[r];

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
        if (TwinShadowed(model, node)) { _own.Counts.TwinRefused++; return -1; }
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
        MakeRoom();
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
        // A content pair that makes its own coverage needs no class of its own: its program runs its discard in
        // whatever class the material lands in, and its depth twin makes the same one in the prepass.
        return AlloyClasses.Of(water: water, additive: additive, opaque: opaque,
                               declaredTransparent: declared,
                               alphaBelowOne: false, coverage: _coverage[r], stageBlends: _stageBlend[r]);
    }

    /// <summary>Does this material blend an alpha its own stage writes? The <c>.mtr</c> declares <c>transparency</c>,
    /// which <c>Material::GetIsOpaque</c> @0x5c6190 reads first, and its content pair sets the alpha after lighting.</summary>
    private static bool StageBlends(MtrDef? mtr, int feat) => mtr is { Transparency: true } && ContentPrograms.SetsAlpha(feat);

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
        int landed = System.Threading.Volatile.Read(ref _own.MtrSerial);
        bool material = landed != _own.MtrSeen;
        if (serial == _classSerial && PartTable.BumpyWaterOn == _classWater && !material) return 0;
        _classSerial = serial; _classWater = PartTable.BumpyWaterOn;
        _own.MtrSeen = landed;
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
                MtrDef? late = MtrDef.Of(mesh);
                _coverage[r] = late is { AlphaMode: 2 };
                _stageBlend[r] = StageBlends(late, _feat[r]);
                _own.FaceTerms[r] = FacesOf(late);
                _content[r] = GuiContent.Key(late);
                _declWater[r] = DeclaredWater(mesh);
            }
            bool additive = mesh.RenderHint.Equals("additive", StringComparison.OrdinalIgnoreCase);
            byte wasCls = _cls[r], wasDst = _dst[r], wasLeaf = _leaf[r], wasWhy = _leafWhy[r];
            bool wasWater = _waterLook[r];
            RecipeFlag wasFlags = _flags[r];
            TxiWords(r, additive, mesh.TransparencyHint > 0);
            if (TexRegistry.BumpyShiny(new TexId(_texId[r * TexSlots]))) _flags[r] |= RecipeFlag.RotateTexture;
            if (_cls[r] == wasCls && _flags[r] == wasFlags && _dst[r] == wasDst && _leaf[r] == wasLeaf && _leafWhy[r] == wasWhy && _waterLook[r] == wasWater) continue;
            if (_cls[r] != wasCls) _own.Counts.Reclassified++;
            // ...and a content recipe whose state moved names pipeline keys the prewarm has not seen
            if (ShaderFeatures.IsContent(_feat[r])) System.Threading.Interlocked.Increment(ref _own.ContentSerial);
            moved++;
        }
        return moved;
    }

    /// <summary>The <c>.mtr</c> face terms: <c>twosided</c>, <c>volumetric</c>, and any <c>sample_framebuffer</c>.</summary>
    private const byte FaceTwoSided = 1, FaceVolumetric = 2, FaceSamplesFb = 4;

    /// <summary>The face terms a parsed <c>.mtr</c> states; none for a mesh with no material.</summary>
    private static byte FacesOf(MtrDef? mtr) => mtr == null ? (byte)0 : (byte)(
        (mtr.TwoSided ? FaceTwoSided : 0) | (mtr.Volumetric ? FaceVolumetric : 0)
        | (mtr.SampleFramebuffer != 0 ? FaceSamplesFb : 0));

    /// <summary>Does the engine draw this part's back faces (<c>Scene::AddPartsToDrawBuckets</c> @0x4751d0)? A
    /// <c>twosided</c> material, unless it is also volumetric; a volumetric one is drawn back faces then front, but
    /// only off the blended route, not when additive, water or <c>sample_framebuffer</c> files it first.</summary>
    private static bool DrawsBackFaces(byte faces, bool additive, AlloyClass cls)
        => (faces & FaceVolumetric) == 0
            ? (faces & FaceTwoSided) != 0
            : (faces & FaceSamplesFb) == 0 && !additive && cls != AlloyClass.Water;

    /// <summary>The diffuse <c>.txi</c>'s material words over the model's own (<c>Material::ParseField</c> @0x5c5850):
    /// <c>blending additive</c> is the (SRC_ALPHA, ONE) pair and so the additive class -- no depth write, no shadow --
    /// and <c>decal 1</c> is the fullbright flag. Known once the diffuse's acquire offered it, so <see cref="Fill"/>
    /// asks after its texture loop and <see cref="Reclassify"/> again for an offer that waited.</summary>
    private static void TxiWords(int r, bool hintAdditive, bool hinted)
    {
        var d = new TexId(_texId[r * TexSlots]);
        bool additive = hintAdditive || TexRegistry.TxiAdditive(d);
        _dst[r] = additive ? BlendOne : BlendInvSrcAlpha;
        if (hintAdditive || TexRegistry.TxiDecal(d)) _flags[r] &= ~RecipeFlag.Lit;
        else _flags[r] |= RecipeFlag.Lit;
        _cls[r] = (byte)ClassOf(r, additive, false);
        if (hinted && ClassOf(r, additive, true) == AlloyClass.Blended) _flags[r] |= RecipeFlag.HintBlended;
        else _flags[r] &= ~RecipeFlag.HintBlended;
        if (AlloyClasses.Covers(_cls[r], _coverage[r])) _flags[r] |= RecipeFlag.Coverage;
        else _flags[r] &= ~RecipeFlag.Coverage;
        // ponytail: volumetric's back-then-front is drawn as cull-off, exact under weighted OIT; alloy.oit 0 needs a front-culled draw first
        if (DrawsBackFaces(_own.FaceTerms[r], additive, (AlloyClass)_cls[r])) _flags[r] |= RecipeFlag.TwoSided;
        else _flags[r] &= ~RecipeFlag.TwoSided;
        _leaf[r] = LeafOf(r);
        bool wasWater = _waterLook[r];
        _waterLook[r] = WaterLookOf(r);
        if (_waterLook[r] && !wasWater && System.Threading.Interlocked.Increment(ref _own.Counts.WaterLogged) <= WaterLogCap)
            Log?.Always($"DrawRecipe: painted water found -- texture '{TexName(r, 0)}' on {_model[r]?.Name} "
                        + (WaterFaces(_mesh[r]!) ? "(water material)" : TexRegistry.TxiRipple(Tex(r, 0)) ? "(ripple .txi)" : "(by name)"));
    }

    private const int WaterLogCap = 20;

    /// <summary>Words in a texture or node name that say water.</summary>
    private static readonly string[] WaterWords = ["water", "pond", "river", "stream", "lake", "swamp", "marsh", "puddle"];
    private static readonly string[] WaterFalseWords = ["waterfall", "fall"];

    /// <summary>Is recipe <paramref name="r"/> a water surface the engine draws as plain paint: a lit, upward-facing, static
    /// part whose diffuse is an arturo ripple or whose name says water, and that the water class does not already draw.</summary>
    private static bool WaterLookOf(int r)
    {
        MeshDef? mesh = _mesh[r];
        ModelDef? model = _model[r];
        if (mesh == null || model == null || (uint)_node[r] >= (uint)model.Nodes.Length) return false;
        RecipeFlag f = _flags[r];
        // a tileset's animated water mesh counts (the layer is per fragment), and a few banked faces do not disqualify it
        if (_cls[r] == (byte)AlloyClass.Water || (f & RecipeFlag.Lit) == 0 || !MostlyUp(mesh)
            || (f & (RecipeFlag.Skinned | RecipeFlag.Dangly)) != 0 || ShaderFeatures.IsContent(_feat[r]))
            return false;
        return WaterFaces(mesh) || TexRegistry.TxiRipple(Tex(r, 0)) || WaterName(TexName(r, 0)) || WaterName(model.Nodes[_node[r]].Name);
    }

    /// <summary>surfacemat.2da's water rows: Water 6, Puddles 11, Swamp 12, DeepWater 17. ponytail: stock row ids, as
    /// TileBuild.Blank's are.</summary>
    private const ulong WaterSurfaces = (1UL << 6) | (1UL << 11) | (1UL << 12) | (1UL << 17);

    /// <summary>How far a water sheet may rise across its extent, metres, and how level its normals must be: the material
    /// tags the bed and the shallows under real water too, and those slope.</summary>
    private const float WaterSheetRise = 0.05f, WaterSheetUp = 0.98f;

    /// <summary>Does at least half of <paramref name="mesh"/>'s faces carry a water material -- the tileset's own word --
    /// on a level sheet, which is what a water surface is and a riverbed or beach under one is not?</summary>
    private static bool WaterFaces(MeshDef mesh)
    {
        if (mesh.BMax.Z - mesh.BMin.Z > WaterSheetRise) return false;
        foreach (Vector3 n in mesh.Normals) if (!(n.Z >= WaterSheetUp * n.Length())) return false;
        int water = 0;
        foreach (FaceDef f in mesh.Faces)
            if ((uint)f.Surface < 64 && ((WaterSurfaces >> f.Surface) & 1UL) != 0) water++;
        return mesh.Faces.Length > 0 && water * 2 >= mesh.Faces.Length;
    }

    /// <summary>The share of a water mesh's normals that must face up (within <see cref="UpCos"/>).</summary>
    private const float WaterUpShare = 0.8f;

    private static bool MostlyUp(MeshDef mesh)
    {
        int up = 0;
        foreach (Vector3 n in mesh.Normals) if (n.Z >= UpCos * n.Length()) up++;
        return mesh.Normals.Length > 0 && up >= WaterUpShare * mesh.Normals.Length;
    }

    private static bool WaterName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (string no in WaterFalseWords) if (name.Contains(no, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (string w in WaterWords) if (name.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Is recipe <paramref name="r"/> a water surface drawn as paint (water.surfaces)?</summary>
    internal static bool WaterLook(int r) => (uint)r < Max && _waterLook[r];

    /// <summary>Recipe <paramref name="r"/>'s foliage class, read off what the content already says: no tag, no
    /// edit to a model or texture. A thin card is a lit, alpha-TESTED part whose diffuse measured as a binary cutout;
    /// foliage is a thin card that also wins <see cref="LeafVotesNeeded"/> votes (dangly, a leafy name, a leafy
    /// colour). Creatures are filtered per record (<c>DbWriteLeaf</c>), where the attachment chain is known.</summary>
    private static byte LeafOf(int r)
    {
        _leafWhy[r] = LeafWhyNone;
        MeshDef? mesh = _mesh[r];
        ModelDef? model = _model[r];
        if (mesh == null || model == null || (uint)_node[r] >= (uint)model.Nodes.Length) return LeafNone;
        if (_cls[r] != (byte)AlloyClass.Cutout) return LeafNone;
        RecipeFlag f = _flags[r];
        TexId diffuse = Tex(r, 0);
        // alpha-TESTED, hard or soft edged: a graded diffuse still draws through the cutout class
        TexCoverage cov = TexRegistry.Coverage(diffuse);
        bool refused = cov is not (TexCoverage.Cutout or TexCoverage.Blended)
                       || (f & RecipeFlag.Lit) == 0 || (f & (RecipeFlag.Wash | RecipeFlag.AnimMesh)) != 0
                       // a true skin is a body; a dangly node carries Skinned too when rigged, and a tree's leaves are often that
                       || (f & RecipeFlag.Skinned) != 0 && (f & RecipeFlag.Dangly) == 0
                       || (model.Classification & HitTest.ClassEffect) != 0;
        if (ShaderFeatures.IsContent(_feat[r])) { _leafWhy[r] = LeafWhyContent; return LeafNone; }
        if (refused) { _leafWhy[r] = LeafWhyOther; return LeafNone; }
        int votes = (f & RecipeFlag.Dangly) != 0 ? LeafVotesDangly : 0;
        if (LeafName(TexName(r, 0)) || LeafName(model.Nodes[_node[r]].Name) || LeafName(model.Name)) votes++;
        if (TexRegistry.TryMean(diffuse, out Vector3 m) && LeafHue(m)) votes++;
        return votes >= LeafVotesNeeded ? LeafFoliage : LeafThin;
    }

    /// <summary>A leafy mean colour: green, or an autumn red, orange or yellow -- warm well above blue.</summary>
    private static bool LeafHue(Vector3 m)
        => m.Y > m.X * LeafGreenOverRed && m.Y > m.Z * LeafGreenOverBlue || MathF.Max(m.X, m.Y) > m.Z * LeafWarmOverBlue;

    /// <summary>Does <paramref name="name"/> carry a foliage word that is not part of a known non-plant word?</summary>
    private static bool LeafName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (string no in LeafFalseWords) if (name.Contains(no, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (string w in LeafWords) if (name.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Recipe <paramref name="r"/>'s foliage class: <see cref="LeafNone"/>, <see cref="LeafThin"/> or <see cref="LeafFoliage"/>.</summary>
    internal static byte Leaf(int r) => (uint)r < Max ? _leaf[r] : LeafNone;
    /// <summary>Why recipe <paramref name="r"/> is no leaf although it is alpha-tested, or <see cref="LeafWhyNone"/>.</summary>
    internal static byte LeafWhy(int r) => (uint)r < Max ? _leafWhy[r] : LeafWhyNone;

    private static int _classSerial = -1;
    private static bool _classWater;

    /// <summary>The resource layer read a <c>.mtr</c> that was not cached before, so the next
    /// <see cref="Reclassify"/> re-asks the material's own terms. Called from the feed that read it.</summary>
    internal static void MaterialLanded() => System.Threading.Interlocked.Increment(ref _own.MtrSerial);

    /// <summary>Recipes whose class moved once their diffuse's coverage was known.</summary>
    internal static int Reclassified => _own.Counts.Reclassified;

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
        FillMaterial(r, nd, mesh);
        _cos[r] = 1f; _sin[r] = 0f;
        // A compiled mesh's bmin/bmax is the compiler's word, and community compilers get it wrong; the draw
        // culls on this box, so it is grown over the vertices the arena actually draws.
        Vector3 lo = mesh.BMin, hi = mesh.BMax, flo = lo, fhi = hi;
        foreach (Vector3 v in mesh.Positions) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
        if (lo != flo || hi != fhi)
        {
            if (_own.Counts.BoxGrown++ == 0) _own.Counts.BoxGrownFirst = $"{_model[r]?.Name}:{nd.Name} {flo}..{fhi} -> {lo}..{hi}";
        }
        _centre[r] = (lo + hi) * 0.5f;
        Vector3 ex = hi - lo;
        _radius[r] = 0.5f * ex.Length();
        _box[r] = new Box { Min = lo, Max = hi };
        _range[r] = MeshArena.Acquire(mesh);
        if (_range[r] < 0 && !_rangeQueued[r] && _rangeWaitN < _rangeWait.Length)
        { _rangeWait[_rangeWaitN++] = r; _rangeQueued[r] = true; }
    }

    /// <summary>Every term of recipe <paramref name="r"/> that comes from its material, and the texture slots it
    /// names: what <see cref="Refresh"/> works out again when a server's override changes the material.</summary>
    private static void FillMaterial(int r, NodeDef nd, MeshDef mesh)
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
        // any pair the material names runs translated, unless both its halves add nothing to stock
        int feat = ContentPrograms.Register(mtr);
        _coverage[r] = mtr is { AlphaMode: 2 };
        _stageBlend[r] = StageBlends(mtr, feat);
        _own.FaceTerms[r] = FacesOf(mtr);
        // TransparencyHint is the engine's child-order sort key and never a blend (MdlNode::ReorderChildren
        // @0x558120 is its one reader); a flat sheet still keeps it, through RecipeFlag.HintBlended.
        bool hinted = mesh.TransparencyHint > 0;
        // R1-8. The engine's own test is Material::GetIsOpaque @0x5c6190, and its terms are: the SharedMaterial's
        // `transparency` keyword, an env map, a non-default destination blend, and -- last -- whether the DIFFUSE
        // actually carries alpha coverage, which it asks the texture's own vtable -- CAurTexture::HasAlpha
        // @0x584630, the image's mean alpha under 1, or a `.txi` alphamean line overriding it.
        // The first three are model state and are read here. The fourth is a DECODE question (texture-coverage-is-a-decode-question) and
        // the texture is not resident when a recipe is interned, so it cannot be answered yet.
        // The fourth term is answerable now (CX-117): the decode decides a texture's coverage and the arrival
        // re-dirties its dependents, so a recipe interned before its diffuse landed is reclassified the frame
        // it does. Unknown keeps the conservative guess -- CUTOUT, which is safe rather than merely
        // convenient: a cutout is alpha-tested AND depth-writing, so a diffuse that turns out to be fully
        // opaque passes the 0.2 discard on every texel and draws identically. Guessing OPAQUE the other way
        // would punch the alpha out of every graded leaf and fringe in the game.
        _cls[r] = (byte)ClassOf(r, additive, false);
        RecipeFlag f = RecipeFlag.None;
        // R1-9. The engine reads the Material's blend flag (mat+0x98): set means it sends flat white
        // and the part is fullbright. `!Lightmapped` was plainly wrong -- a lightmapped tile is still SHADED
        // in vanilla (lightmap x diffuse), so that unlit every tile in the game. The model-side equivalent of
        // the blend flag is the additive renderhint, which is what the materials carrying it declare.
        if (!additive) f |= RecipeFlag.Lit;
        // no env-map term: Material::HasEnvMap is per instance, asked per part by AlloyRenderer.DbEnvMapped
        if (mesh.RotateTexture) f |= RecipeFlag.RotateTexture;
        if (nd.Kind == NodeKind.Dangly) f |= RecipeFlag.Dangly;
        // A rigged dangly node draws as a skinned part (docs/spec/dangly-motions.md section 4a): the flag is
        // what enrols its slot for a bone base. With no palette at all there is nothing to enrol it for.
        if (nd.Kind == NodeKind.Dangly && nd.Rig?.Skin != null && AlloyRenderer.Skinning) f |= RecipeFlag.Skinned;
        if (nd.Kind == NodeKind.Skin) f |= RecipeFlag.Skinned;
        if (nd.Kind == NodeKind.AnimMesh) f |= RecipeFlag.AnimMesh;
        if (!mesh.Shadow) f |= RecipeFlag.NoShadow;
        if (mesh.Beaming) f |= RecipeFlag.Beaming;
        if (mesh.TileFade != 0) f |= RecipeFlag.Fades;
        if (FacesUp(mesh)) f |= RecipeFlag.FacesUp;
        if (IsSheet(mesh)) f |= RecipeFlag.Wash;
        _flags[r] = f;
        _prim[r] = 0;
        _feat[r] = feat;
        // CX-317: the prewarm's edge -- interning runs on the feed's workers, so the bump is atomic
        if (ShaderFeatures.IsContent(feat)) System.Threading.Interlocked.Increment(ref _own.ContentSerial);
        // ...and the GUI pass interns the same pair of its own; its build asks the same stock verdict, so a part
        // of this material draws Alloy's own GUI program there exactly as it draws Alloy's own lit stage here.
        _content[r] = GuiContent.Key(mtr);
        _own.Mtr[r] = mtr;
        MtrParams.NoteCustomPair(mtr, feat, Log);
        Span<float> featF = _featF.AsSpan(r * 4, 4);
        featF.Clear();
        featF[0] = feat;
        // The stock PBR vec4s: 0 means "not declared" and the shader synthesises it. NOT the MDL's shininess:
        // the engine never turns it into a roughness, and 1 - shininess/128 read PLT armour as rough metal.
        ref ParamRow param = ref _param[r];
        param = default;
        MtrParams.ResetExt(ref param.Ext);
        MtrParams.Fold(mtr, ref param, featF);
        // ...and its row carries its material's own uniforms and palette maps, the record its INDEX in feat.z; -1 (the
        // table full) leaves the pair reading every uniform's default, which CxContentRow makes a defined read
        _own.ContentRow[r] = -1;
        if (ShaderFeatures.IsContent(feat))
        {
            _own.ContentRow[r] = ContentTable.Alloc(r);
            featF[2] = _own.ContentRow[r];
        }
        int mask = 0;
        for (int s = 0; s < TexSlots; s++)
        {
            string name = SlotName(s, mtr, mesh);
            _tex[r * TexSlots + s] = name;
            TexId id = string.IsNullOrEmpty(name) ? default : TexRegistry.Acquire(name, cuts: s == 0 && !additive);
            // light.ainormals: a map-less slot 1 takes the normal map the registry derives from the diffuse
            bool derived = s == NormalRecipeSlot && id.IsNull && DerivesNormal(r, nd, feat, additive);
            if (derived) { id = TexRegistry.Acquire(TexName(r, 0), cell: TexRegistry.NormalCell); mask |= MaskBitOf(s, feat); }
            // light.relief: its height map beside it, which the lit stage's parallax march reads
            // light.aimaterial: its roughness map, which the synthesis takes as declared content
            if (s == RoughRecipeSlot && id.IsNull && Lighting.AiMaterial.Boot && !Tex(r, NormalRecipeSlot).IsNull
                && string.IsNullOrEmpty(TexName(r, NormalRecipeSlot)) && SlotName(SpecRecipeSlot, mtr, mesh).Length == 0)
            { id = TexRegistry.Acquire(TexName(r, 0), cell: TexRegistry.RoughCell); mask |= MaskBitOf(s, feat); }
            if (s == HeightRecipeSlot && id.IsNull && Lighting.AiRelief.Boot && !Tex(r, NormalRecipeSlot).IsNull
                && string.IsNullOrEmpty(TexName(r, NormalRecipeSlot)))
            { id = TexRegistry.Acquire(TexName(r, 0), cell: TexRegistry.HeightCell); mask |= MaskBitOf(s, feat); }
            _texId[r * TexSlots + s] = id.Value;
            bool wait = !id.IsNull && !TexRegistry.Resident(id);
            _texWait[r * TexSlots + s] = wait ? (byte)1 : (byte)0;
            if (wait) _texPending++;
            if (!string.IsNullOrEmpty(name)) mask |= MaskBitOf(s, feat);
        }
        _slots[r] = mask;
        // The engine's second route into the tile texture rotation, and it LATCHES: SetPartTextureRotate
        // @0x561670 writes `mdlnode+0x2b9 = 1` on a material with a bumpy-shiny map, i.e. it turns the mesh's
        // own `rotatetexture` on for good. Re-asked in Reclassify because the diffuse's .txi is not read
        // until the texture lands, which is after the recipe is interned.
        if (TexRegistry.BumpyShiny(new TexId(_texId[r * TexSlots]))) _flags[r] |= RecipeFlag.RotateTexture;
        TxiWords(r, additive, hinted);
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
                if (_own.Counts.RetiredInUse++ < 8)
                    Log?.Always($"DrawRecipe: model '{model.Name}' was evicted while {_own.Users[r]} part(s) still draw "
                              + $"node {_node[r]} -- the recipe is kept until they let go (a model reference was dropped too early)");
                continue;
            }
            Retire(r);
        }
    }

    /// <summary>The share of the table <see cref="MakeRoom"/> keeps free, and of the texture slots: a sixteenth.</summary>
    internal const int RoomShare = 16;
    /// <summary>Idle models one frame evicts at most to make room: each eviction walks the table in <see cref="Forget"/>.</summary>
    private const int RoomPerFrame = 16;

    /// <summary>Evict idle models, least recently used first, until a sixteenth of the table and a sixteenth of the
    /// texture slots are free (at least one recipe), at most <see cref="RoomPerFrame"/> a frame. A model an area draws
    /// is never idle. Game thread.</summary>
    private static void MakeRoom()
    {
        int want = Math.Max(1, Cap / RoomShare);
        for (int k = 0; k < RoomPerFrame && (Cap - Count < want || TexRegistry.SlotsShort(TexRegistry.MaxSlots / RoomShare)); k++)
            if (!EvictForRoom()) return;
    }

    /// <summary>Evict the least recently used idle model and count it against the table; false when none is idle.</summary>
    private static bool EvictForRoom()
    {
        if (!ModelCache.EvictLeastRecent()) return false;
        _own.Counts.RoomEvicted++;
        return true;
    }

    /// <summary>The share of the table the area change leaves in use at most: a quarter.</summary>
    private const int AreaKeepShare = 4;

    /// <summary>The frame line's key for the models evicted to make room in the table.</summary>
    internal const string EvictedKey = "models evicted for recipes";

    /// <summary>The frame line's key for new recipes the full table refused.</summary>
    internal const string SpilledKey = "RECIPES SPILLED";

    /// <summary>The frame line's key for recipes retired while a part still named them.</summary>
    internal const string InUseKey = "RECIPES EVICTED IN USE";

    /// <summary>The frame line's key for recipes retired a second time.</summary>
    internal const string TwiceKey = "RECIPES RETIRED TWICE";

    /// <summary>The boot line naming every recipe fault key, so a grader reads the keys from the session it grades.</summary>
    internal const string FaultKeysLine = "dbfeed: recipe fault keys: " + SpilledKey + "|" + EvictedKey + "|" + InUseKey + "|" + TwiceKey;

    /// <summary>The area change: the model cache's own edge, then idle models evicted, least recently used first,
    /// until at most a quarter of the table is in use. Runs behind the loading screen.</summary>
    internal static void AreaEdge()
    {
        ModelCache.AreaEdge();
        while (Count > Cap / AreaKeepShare && EvictForRoom()) { }
    }

    /// <summary>Models evicted this period to make room in the table.</summary>
    internal static int RoomEvicted => _own.Counts.RoomEvicted;

    /// <summary>Start a new frame-line period.</summary>
    internal static void ResetPeriod() => _own.Counts.RoomEvicted = 0;

    /// <summary>Recipes retired with their model.</summary>
    internal static int Retired => _own.Counts.Retired;

    /// <summary>Bytes of the textures that only recipes of idle cached models hold: a texture a held model also draws,
    /// and one no recipe holds, count nothing. A retired recipe has no model, and an orphan's model has left the cache.</summary>
    internal static long IdleModelTextureBytes()
    {
        for (int r = 0; r < _n; r++)
        {
            if (_model[r] is not ModelDef m || !ModelCache.Idle(m)) continue;
            for (int s = 0; s < TexSlots; s++) TexRegistry.Tally(Tex(r, s));
        }
        return TexRegistry.TakeSoleBytes();
    }

    /// <summary>Subscribe to the model cache's eviction edge. Multicast (<c>+=</c>), because the cache's other
    /// listeners are not ours to replace.</summary>
    internal static void Attach()
    {
        if (_attached) return;
        _attached = true;
        ModelCache.Evicting += Forget;
    }

    private static bool _attached;

    /// <summary>How far from +Z a <see cref="RecipeFlag.FacesUp"/> mesh's normals may lean: cos 45 degrees.</summary>
    private const float UpCos = 0.7f;

    /// <summary>Every normal of the mesh within <see cref="UpCos"/> of +Z: a sheet laid over the ground.</summary>
    internal static bool FacesUp(MeshDef mesh)
    {
        if (mesh.Normals.Length == 0) return false;
        foreach (Vector3 n in mesh.Normals)
            if (!(n.Z >= UpCos * n.Length())) return false;
        return true;
    }

    /// <summary>A sheet laid over another surface, judged off the triangles whatever their authored normals say:
    /// every triangle within <see cref="UpCos"/> of horizontal, or of the first triangle's plane -- a decal hung
    /// on a wall is as much a sheet as one on the floor, and a merged static has its node orientation baked in.</summary>
    internal static bool IsSheet(MeshDef mesh) => Within(mesh, Vector3.UnitZ) || Within(mesh, FirstAxis(mesh));

    /// <summary>The unit normal of the mesh's first non-degenerate triangle, zero for none.</summary>
    private static Vector3 FirstAxis(MeshDef mesh)
    {
        uint[] ix = mesh.Indices;
        Vector3[] p = mesh.Positions;
        for (int t = 0; t + 2 < ix.Length; t += 3)
        {
            if (ix[t] >= p.Length || ix[t + 1] >= p.Length || ix[t + 2] >= p.Length) return Vector3.Zero;
            Vector3 n = Vector3.Cross(p[ix[t + 1]] - p[ix[t]], p[ix[t + 2]] - p[ix[t]]);
            if (n.Length() >= 1e-8f) return Vector3.Normalize(n);
        }
        return Vector3.Zero;
    }

    /// <summary>Every triangle within <see cref="UpCos"/> of <paramref name="axis"/>, either side; false for none.</summary>
    private static bool Within(MeshDef mesh, Vector3 axis)
    {
        uint[] ix = mesh.Indices;
        Vector3[] p = mesh.Positions;
        bool any = false;
        for (int t = 0; t + 2 < ix.Length; t += 3)
        {
            if (ix[t] >= p.Length || ix[t + 1] >= p.Length || ix[t + 2] >= p.Length) return false;
            Vector3 n = Vector3.Cross(p[ix[t + 1]] - p[ix[t]], p[ix[t + 2]] - p[ix[t]]);
            float len = n.Length();
            if (len < 1e-8f) continue;
            if (MathF.Abs(Vector3.Dot(n, axis)) < UpCos * len) return false;
            any = true;
        }
        return any;
    }

    /// <summary>The recipe's local box.</summary>
    internal static Box ModelBox(int r) => _box[r];

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
    /// <summary>The part draws its back faces too, with face culling off (<see cref="DrawRecipe.DrawsBackFaces"/>).</summary>
    TwoSided = 1 << 2,
    /// <summary>The mesh is a sheet (<see cref="DrawRecipe.IsSheet"/>): drawn see-through, it is a wash laid over
    /// another surface, and is drawn pulled toward the eye so it cannot z-fight what it lies on.</summary>
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
    /// <summary>Every normal leans less than 45 degrees off +Z (<see cref="DrawRecipe.FacesUp"/>): a ground decal
    /// can be one of these.</summary>
    FacesUp = 1 << 13,
    /// <summary>The model's <c>transparencyhint</c> would have blended this part. Only a flat sheet acts on it
    /// (<see cref="AlloyClasses.SlotClass"/>): vanilla reads the hint as a child draw order, never a blend.</summary>
    HintBlended = 1 << 14,
}
