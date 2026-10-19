using CX.Engine;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The ownership table, fed from the scene database: the ONE writer of a slot's ownership columns,
/// keyed by a <c>(DbId part, int recipe)</c> pair and driven by a dirty bit rather than a table scan.
/// <para>A DB slot is told apart by <see cref="_oDbPart"/> (-1 for an engine slot); its <c>_oTri</c> holds a
/// NEGATIVE sentinel, so "slot vacated" tests still read right and a forgotten DB branch faults at once.</para>
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The scene-database part this slot draws, -1 for a slot fed from the engine's scene graph.
    /// Filled with -1 rather than left zeroed: part 0 is a real index, so a zero default would read every
    /// engine slot in the table as the database's.</summary>
    internal static readonly int[] _oDbPart = Minus1(new int[MaxOwned]);
    /// <summary>The slot's interned <see cref="DrawRecipe"/> -- the stable half the engine path re-read off
    /// <c>Part</c>/<c>MdlNode</c>/<c>Material</c>/<c>VertexPrimitive</c> every frame.</summary>
    internal static readonly int[] _oDbRecipe = Minus1(new int[MaxOwned]);

    private static int[] Minus1(int[] a) { Array.Fill(a, -1); return a; }

    /// <summary>Is slot <paramref name="i"/> the database's?</summary>
    internal static bool DbSlot(int i) => (uint)i < (uint)_oDbPart.Length && _oDbPart[i] >= 0;

    /// <summary>May the lightmap bake take this DB slot? Lit, still geometry of a Static object (a tile or a
    /// Static placeable) and nothing else -- a baked mover would keep the light of wherever it stood when it
    /// enrolled, and a charted copy of a dangly, skinned or animmesh range is its vertices frozen at the offer.</summary>
    internal static bool DbBakeable(int i)
    {
        if (!DbSlot(i) || !_oLit[i]) return false;
        if (_oDbRecipe[i] < 0 || (DrawRecipe.Flags(_oDbRecipe[i]) & (RecipeFlag.Dangly | RecipeFlag.Skinned | RecipeFlag.AnimMesh)) != 0) return false;
        DbId o = SceneDb.PartObj[_oDbPart[i]];
        return SceneDb.Objects.Valid(o) && (SceneDb.ObjFlags[o.Index] & (ObjFlag.Static | ObjFlag.Sky)) == ObjFlag.Static;
    }

    /// <summary>Is this part a ground decal, folded into the surface under it (<see cref="DecalLayer"/>)? A lit
    /// sheet facing up whose <c>transparencyhint</c> would blend it (<see cref="RecipeFlag.HintBlended"/>), on a static object, with no self-illumination, env map or named hosted body,
    /// nothing posed or morphed. Never an alpha-tested cover: it hides its ground, normal and material too. A content stage's
    /// decal folds its plain albedo, and the surface it lies on runs its own body over it.</summary>
    internal static bool DbDecal(int recipe, RecipeFlag rf, ObjFlag of, bool env)
        => (rf & RecipeFlag.HintBlended) != 0
           && (rf & (RecipeFlag.Lit | RecipeFlag.FacesUp)) == (RecipeFlag.Lit | RecipeFlag.FacesUp)
           && (rf & (RecipeFlag.Dangly | RecipeFlag.Skinned | RecipeFlag.AnimMesh)) == 0
           && (of & (ObjFlag.Static | ObjFlag.Sky)) == ObjFlag.Static
           && DrawRecipe.Src(recipe) == DrawRecipe.BlendSrcAlpha && DrawRecipe.Dst(recipe) == DrawRecipe.BlendInvSrcAlpha
           && (DrawRecipe.Slots(recipe) & IllumBit) == 0 && !env
           && !ContentPrograms.MayMove(DrawRecipe.Feat(recipe));

    /// <summary>Does this part draw as a wash: a see-through sheet lying flat (<see cref="RecipeFlag.Wash"/>) on a
    /// static object, under a polygon offset so it cannot z-fight the surface it lies on? Never opaque -- the opaque
    /// class keeps its prepass depth exactly -- nor additive, which writes no depth to fight over; nor a coverage or
    /// env-mapped material (whose alpha is not a sheet's edge) or a posed mesh. A GUI panel's objects are not static.</summary>
    internal static bool DbWash(int recipe, RecipeFlag rf, ObjFlag of, bool env)
        => (rf & RecipeFlag.Wash) != 0 && DrawRecipe.Cls(recipe) is (int)AlloyClass.Cutout or (int)AlloyClass.Blended
           && (rf & (RecipeFlag.Dangly | RecipeFlag.Skinned | RecipeFlag.AnimMesh | RecipeFlag.Coverage)) == 0
           && !env && (of & (ObjFlag.Static | ObjFlag.Sky)) == ObjFlag.Static;

    /// <summary>The class a slot draws in (<see cref="AlloyClasses.SlotClass"/>): blended for a ground decal, a wash
    /// with <see cref="RecipeFlag.HintBlended"/>, or a graded cutout wash (vanilla's blend plus 0.2 test,
    /// <c>Scene::RenderSinglePass</c> @0x56e5f0); otherwise the recipe's class.</summary>
    internal static byte DbClass(int recipe, bool wash, bool decal)
        => AlloyClasses.SlotClass(DrawRecipe.Cls(recipe), (DrawRecipe.Flags(recipe) & RecipeFlag.HintBlended) != 0,
                                  DrawRecipe.Graded(recipe), wash, decal);

    /// <summary>The <c>_oWash</c> value: 0 not a wash; <see cref="WashWhole"/>; or <see cref="WashSplit"/>, a blending
    /// wash that is not a decal.</summary>
    internal static byte DbWashMode(bool wash, byte cls, bool decal)
        => !wash ? (byte)0 : cls == (byte)AlloyClass.Blended && !decal ? WashSplit : WashWhole;

    /// <summary><see cref="DbWashMode"/>'s values: a wash drawn in one piece, or one whose fully covered interior
    /// draws solid with the cutouts (<see cref="Issue.WashInteriors"/>) and whose graded rest blends.</summary>
    internal const byte WashWhole = 1, WashSplit = 2;

    /// <summary>Name a slot's wash in the log once its diffuse is measured -- before that its class is the
    /// conservative cutout guess, which is every tile's ground.</summary>
    private static void DbNoteWash(int recipe, RecipeFlag rf, ObjFlag of, bool lit, byte cls, byte decal, byte wash, bool env)
    {
        if (decal == 0 && wash != 0 && DrawRecipe.Measured(recipe))
            DecalLayer.NoteRefused(recipe, (AlloyClass)cls, DbDecalWhyNot(recipe, rf, of, lit, env));
    }

    /// <summary>The first term <see cref="DbDecal"/> refuses a flat see-through sheet on, for the log.</summary>
    private static string DbDecalWhyNot(int recipe, RecipeFlag rf, ObjFlag of, bool lit, bool env)
        => (rf & RecipeFlag.HintBlended) == 0 ? "no transparencyhint that would blend it"
         : (rf & RecipeFlag.FacesUp) == 0 ? "its normals do not face up"
         : !lit || (rf & RecipeFlag.Lit) == 0 ? "unlit"
         : (rf & (RecipeFlag.Dangly | RecipeFlag.Skinned | RecipeFlag.AnimMesh)) != 0 ? "posed or morphed"
         : (of & (ObjFlag.Static | ObjFlag.Sky)) != ObjFlag.Static ? "not a static object"
         : DrawRecipe.Src(recipe) != DrawRecipe.BlendSrcAlpha || DrawRecipe.Dst(recipe) != DrawRecipe.BlendInvSrcAlpha
             ? $"blend pair {DrawRecipe.Src(recipe)}/{DrawRecipe.Dst(recipe)}"
         : (DrawRecipe.Slots(recipe) & IllumBit) != 0 ? "self-illumination map"
         : env ? "env map"
         : ContentPrograms.MayMove(DrawRecipe.Feat(recipe)) ? "a content vertex half that may move it" : "?";

    /// <summary>The self-illumination map's mask bit (map slot 4, the stage's <c>CX_SLOT_ILLUM</c>).</summary>
    private const int IllumBit = 1 << 4;

    /// <summary>The sentinel a DB slot's <c>_oTri</c> carries: non-zero (so "vacated" still means vacated) and
    /// non-canonical (so an unbranched dereference faults rather than reads).</summary>
    private static IntPtr DbTri(int part) => (IntPtr)(-2L - part);

    /// <summary>Seat database part <paramref name="part"/> in an ownership slot and write its stable columns.
    /// Returns the slot, or -1 when the table is full (counted, logged once by the feed -- never a quiet drop).
    /// Idempotent: a part that already holds a slot keeps it, which is what makes the change-driven build's
    /// "update" path a column rewrite rather than a reseat.</summary>
    internal static int DbSeat(int part, int recipe)
    {
        int held = SceneDb.PartSlot[part];
        // R1-6: a part that already holds its slot with the same recipe has NOTHING stable to rewrite -- its
        // move is a per-frame field, which StageDbPart compares into place. Rewriting the columns (and
        // unstamping the record with them) on every move forced a full stable rebuild and a whole-record
        // resend per moving part per frame, which is exactly the delta scheme #242 exists to avoid.
        if (held >= 0 && _oDbPart[held] == part)
        {
            if (_oDbRecipe[held] != recipe) DbWriteColumns(held, part, recipe);
            return held;
        }
        if (_ownLive >= MaxOwned) { _capacitySpill++; return -1; }
        // R1-3: DB mode has no enrolment, so nothing ever advances _freeAvail -- the engine path's floor is
        // raised by EnrollBegin, and none of those run here. Without this the free list is never drawn from,
        // _ownCount climbs past MaxOwned on the first busy area, and the table walks off its own arrays.
        // A DB slot is freed by one told edge and may be reused immediately: there is no cross-frame walk
        // whose floor it could violate.
        if (_freeAvail < _freeCount) _freeAvail = _freeCount;
        int i = AllocSlot();
        if (i >= MaxOwned) { _ownCount = MaxOwned; _capacitySpill++; return -1; }
        _ownLive++;
        _oBone[i] = -1; _oDpAt[i] = -1;
        UnstampRecord(i);
        SetCastOk(i, false);
        // CX-120: the tenancy stamp is minted HERE, for THIS slot, and is unique to this seating -- the one
        // thing every reader of it means by it ("the slot changed hands"). It used to be the generation
        // current at the seat, with DbOrder re-stamping every live slot afterwards; see DbOrder.
        _gate[i].EnrollGen = ++_enrollGen;
        DbWriteColumns(i, part, recipe);
        SceneDb.PartSlot[part] = i;
        _dbSeated++;
        DbSetChanged(_churn.Seat(i, part));
        return i;
    }

    /// <summary>Release the slot database part <paramref name="part"/> held, if any. The one destruction edge:
    /// the game destroyed the object (or its part range changed), so there is nothing to infer.</summary>
    internal static void DbRelease(int part)
    {
        int i = SceneDb.PartSlot[part];
        if (i < 0) return;
        SceneDb.PartSlot[part] = -1;
        if ((uint)i >= (uint)_oDbPart.Length || _oDbPart[i] != part) return;
        _oDbPart[i] = -1; _oDbRecipe[i] = -1;
        FreeSlot(i);
        _dbReleased++;
        DbSetChanged(_churn.Release(i, part));
    }

    /// <summary>Everything about a draw that does not change while this part holds this slot.</summary>
    private static void DbWriteColumns(int i, int part, int recipe)
    {
        DbId obj = SceneDb.PartObj[part];
        int o = obj.Index;
        RecipeFlag rf = DrawRecipe.Flags(recipe);
        ObjFlag of = (uint)o < SceneDb.MaxObjects ? SceneDb.ObjFlags[o] : ObjFlag.None;
        _oDbPart[i] = part; _oDbRecipe[i] = recipe;
        _oEntry[i] = -1;
        _oTri[i] = DbTri(part);
        _oNode[i] = IntPtr.Zero; _oMat[i] = IntPtr.Zero; _oVdo[i] = IntPtr.Zero;
        _oItem[i] = -1; _oPrim[i] = (int)DrawRecipe.Prim(recipe);
        // Every DB draw comes out of the one mesh arena, so there is no per-VDO VAO to name and no harvest
        // to wait for: the slot's VAO term is the arena's bit and the range is the part's.
        _oVao[i] = 0; _oLodBit[i] = DbArenaVaoBit;
        _oRotBits[i] = 0;
        // R1-18: the batch sort's texture digit. The recipe's diffuse TexId folded into the key space, so
        // parts sharing a texture sort contiguous exactly as the engine path's interned CAurTexture* did.
        uint tex0 = DrawRecipe.Tex(recipe, 0).Value;
        _oTexKey[i] = tex0 == 0 ? 0 : (int)((tex0 * 2654435761u >> 8) % (MaxMats - 1)) + 1;
        DbWriteTexRot(i, o, recipe, rf);
        // Every DB part is matrix-class: its world matrix is composed, never baked into its vertices. The
        // arena holds MODEL-space geometry (that is what makes one range serve every instance), which is
        // exactly the case `_oMx` names.
        _oMx[i] = true;
        _oLit[i] = (rf & RecipeFlag.Lit) != 0 && _lighting;
        if ((rf & RecipeFlag.Lit) != 0 && (of & (ObjFlag.Static | ObjFlag.Sky)) == ObjFlag.Static)
            MeshArena.WantBake(DrawRecipe.Range(recipe));
        bool env = DbEnvMapped(part, SceneDb.PartTex(part, recipe, 0));
        bool washes = DbWash(recipe, rf, of, env);
        // before the class: a decal slot draws blended
        _oDecal[i] = _oLit[i] && DbDecal(recipe, rf, of, env) ? (byte)1 : (byte)0;
        _oCls[i] = DbClsOf(part, recipe, washes, _oDecal[i] != 0);
        _oA2c[i] = (rf & RecipeFlag.Coverage) != 0;
        _oSrc[i] = (byte)DrawRecipe.Src(recipe); _oDst[i] = (byte)DrawRecipe.Dst(recipe);
        _oWash[i] = DbWashMode(washes, _oCls[i], _oDecal[i] != 0);
        DbNoteWash(recipe, rf, of, _oLit[i], _oCls[i], _oDecal[i], _oWash[i], env);
        _oNoCull[i] = (rf & RecipeFlag.TwoSided) != 0;
        _oBucket[i] = 0;
        // No gob pointer exists in DB mode; the questions it answered are columns.
        _oGob[i] = IntPtr.Zero;
        _oTileGob[i] = (of & ObjFlag.Tile) != 0;
        _oLodRoot[i] = IntPtr.Zero;
        _oDangly[i] = (rf & RecipeFlag.Dangly) != 0;
        _oFadeTile[i] = IntPtr.Zero; _oTile[i] = IntPtr.Zero;
        Vector3 c = DrawRecipe.Centre(recipe);
        _oCx[i] = c.X; _oCy[i] = c.Y; _oCz[i] = c.Z;
        _oR[i] = DrawRecipe.Radius(recipe);
        // The WORLD box the area index files on, written here as well as at every stage: the index is built
        // from a slot-set edge and the stage that would fill it can be deferred (no geometry resident yet), so
        // a slot filed between the two would be indexed with a zero box -- i.e. unbounded, i.e. routed every
        // pass for the life of the area. Correct either way, needlessly expensive one way.
        DbWriteWorldBox(i, recipe, SceneDb.PartWorld[part]);
        _oBx[i] = c.X; _oBy[i] = c.Y; _oBz[i] = c.Z; _oBr[i] = _oR[i];
        int fo = i * 4;
        _oParam[i] = DrawRecipe.Params(recipe);
        // CX-117: Gob::SetMaterialParam's per-OBJECT overrides, over the recipe's shared floats. Gated on the
        // table being non-empty, so a scene that never calls it pays one bool.
        if (Seam.MaterialParams.Any) Seam.MaterialParams.Apply(SceneDb.PartObj[part], ref _oParam[i]);
        for (int k = 0; k < 4; k++) _oFeat[fo + k] = DrawRecipe.FeatF(recipe, k);
        // CX-303: ...and an object carrying SetMaterialShaderUniform values reads them from a side-table row of its own
        if (ShaderFeatures.IsContent((int)_oFeat[fo]) && Seam.MaterialParams.HasContent(SceneDb.PartObj[part]))
        {
            int own = Db.ContentTable.ObjectRow(recipe, SceneDb.PartObj[part].Value);
            if (own >= 0) _oFeat[fo + 2] = own;
        }
        _oSlots[i] = DrawRecipe.Slots(recipe);
        UnstampRecord(i);
    }

    /// <summary>The slot's tile TEXTURE rotation, as the record's <c>rot.xy</c>: <c>orient * 90</c> degrees on
    /// a mesh whose <c>rotatetexture</c> is set or whose material is bumpy-shiny (one
    /// <see cref="RecipeFlag.RotateTexture"/> covers both). Per PLACEMENT, so it comes off
    /// <see cref="SceneDb.ObjTexRotate"/>, not the recipe -- identity defeats tile anti-repetition.</summary>
    private static void DbWriteTexRot(int i, int o, int recipe, RecipeFlag rf)
    {
        int qt = (rf & RecipeFlag.RotateTexture) != 0 && (uint)o < SceneDb.MaxObjects
               ? SceneDb.ObjTexRotate[o] & 3 : 0;
        if (qt == 0) { _oCos[i] = DrawRecipe.Cos(recipe); _oSin[i] = DrawRecipe.Sin(recipe); return; }
        _oCos[i] = TexRotCos[qt]; _oSin[i] = TexRotSin[qt];
    }

    /// <summary>(cos, sin) of 0/90/180/270 degrees, exact rather than <c>MathF</c>'s, which leaves 1e-7 in the
    /// zero terms and skews a rotated tile's UVs by a texel at grazing angles.
    /// <para>Sign convention, verified: the engine's <c>aurMatrixStack::Rotate((0,0,1), angle)</c> @0x5dafe0
    /// gives GL columns <c>(c, s)</c> and <c>(-s, c)</c>, so the pair is unnegated and unswapped.</para></summary>
    private static readonly float[] TexRotCos = [1f, 0f, -1f, 0f];
    /// <inheritdoc cref="TexRotCos"/>
    private static readonly float[] TexRotSin = [0f, 1f, 0f, -1f];

    /// <summary>The <c>(cos, sin)</c> <paramref name="quarterTurns"/> puts in a record, for the self-check
    /// that ties the table to the engine's <c>orient * 90</c> degrees.</summary>
    internal static (float Cos, float Sin) DbTexRot(int quarterTurns)
        => (TexRotCos[quarterTurns & 3], TexRotSin[quarterTurns & 3]);

    /// <summary><c>_oLodBit</c>'s DB value: this slot draws out of the mesh arena. Above
    /// <see cref="ChartVaoBit"/> so it cannot collide with either engine-mode arena bit.</summary>
    internal const int DbArenaVaoBit = 1 << 22;

    /// <summary>The arena compacted and these ranges moved. Every reader resolves a DB slot's range live off
    /// the arena (the colour stage per frame, the bake through <see cref="DbCasterRange"/>), so nothing is
    /// rewritten here; what remains is the cascades, which hold geometry no mover event describes.</summary>
    internal static void DbRangesMoved(int[] moved, int count)
    {
        if (count <= 0) return;
        int fixedUp = 0;
        for (int i = 0; i < _ownCount; i++)
        {
            int part = _oDbPart[i];
            if (part < 0) continue;
            int range = DrawRecipe.Range(_oDbRecipe[i]);
            if (range < 0) continue;
            bool hit = false;
            for (int k = 0; k < count; k++) if (moved[k] == range) { hit = true; break; }
            if (!hit) continue;
            fixedUp++;
        }
        // A moved range is new geometry under the published cascades, and no mover event describes it.
        if (fixedUp > 0) CasterPass.ShadowInvalidate();
    }

    /// <summary>The slot's class: <see cref="DbClass"/>'s, except that an opaque PLT on an instance with no env map
    /// is a cutout. <c>Material::GetIsOpaque</c> @0x4c6190 answers opaque for an env-mapped gob without asking the
    /// texture; without one it asks <c>HasAlpha</c>, which every PLT answers yes (alpha mean 0.5), so the palette
    /// alpha is coverage there -- palette row 61 is alpha 0 throughout, the "ragged cloak" colour.</summary>
    private static byte DbClsOf(int part, int recipe, bool wash, bool decal)
    {
        byte cls = DbClass(recipe, wash, decal);
        if (cls != (byte)AlloyClass.Opaque) return cls;
        TexId diffuse = DbTexOf(part, recipe, 0);
        // ponytail: the shadow keeps the solid silhouette -- the caster cut samples the pair texture, whose alpha
        // is 1; resolve the palette in cx_castmaskcut's handle stage if a ragged cloak's shadow ever matters.
        return TexRegistry.KindOf(diffuse) == TexKind.Plt && !DbEnvMapped(part, diffuse) ? (byte)AlloyClass.Cutout : cls;
    }

    /// <summary>A recipe's CLASS moves once its diffuse lands (<see cref="DrawRecipe.Reclassify"/>), so every
    /// slot drawing it is told: the class columns are written at the seat and read by the route every pass, so
    /// a part interned before its texture arrived would otherwise keep the conservative cutout guess for ever.
    /// <para>A whole-table walk, at texture-arrival rate; only a disagreeing slot is rewritten.</para></summary>
    internal static void DbReclassified()
    {
        int moved = 0;
        for (int i = 0; i < _ownCount; i++)
        {
            int recipe = _oDbRecipe[i];
            if (_oDbPart[i] < 0 || recipe < 0) continue;
            RecipeFlag rf = DrawRecipe.Flags(recipe);
            int o = SceneDb.PartObj[_oDbPart[i]].Index;
            ObjFlag of = (uint)o < (uint)SceneDb.ObjFlags.Length ? SceneDb.ObjFlags[o] : ObjFlag.None;
            // ...and the two routes the class decides: a wash (see-through and flat) and a ground decal (blended)
            bool env = DbEnvMapped(_oDbPart[i], SceneDb.PartTex(_oDbPart[i], recipe, 0));
            bool washes = DbWash(recipe, rf, of, env);
            bool lit = (rf & RecipeFlag.Lit) != 0 && _lighting;
            byte decal = lit && DbDecal(recipe, rf, of, env) ? (byte)1 : (byte)0;
            byte cls = DbClsOf(_oDbPart[i], recipe, washes, decal != 0);
            // ...and the texture rotation with it: the bumpy-shiny half of RecipeFlag.RotateTexture latches
            // when the diffuse's .txi lands, which is after the slot took its columns.
            float wasCos = _oCos[i], wasSin = _oSin[i];
            DbWriteTexRot(i, o, recipe, rf);
            // ...and the .txi's blend pair and fullbright flag, which an offer that waited reads late.
            bool a2c = (rf & RecipeFlag.Coverage) != 0;
            byte wash = DbWashMode(washes, cls, decal != 0);
            if (wash != 0) DbNoteWash(recipe, rf, of, lit, cls, decal, wash, env);
            if (_oCls[i] == cls && _oDecal[i] == decal && _oWash[i] == wash && _oCos[i] == wasCos && _oSin[i] == wasSin
                && _oDst[i] == DrawRecipe.Dst(recipe) && _oLit[i] == lit && _oA2c[i] == a2c) continue;
            _oCls[i] = cls; _oLit[i] = lit; _oA2c[i] = a2c; _oDecal[i] = decal; _oWash[i] = wash;
            _oSrc[i] = (byte)DrawRecipe.Src(recipe); _oDst[i] = (byte)DrawRecipe.Dst(recipe);
            // The slot moves between passes, so its staged record and batch row are both stale.
            UnstampRecord(i);
            moved++;
        }
        _dbReclassSlots += moved;
        if (moved > 0) DbSetGen++;   // the route's own change gate: the pass order is not the same set any more
    }

    private static int _dbReclassSlots;
    /// <summary>Slots whose class column followed their recipe's reclassification.</summary>
    internal static int DbReclassSlots => _dbReclassSlots;

    private static int _pcSaid;
    private static long _pcAt;
    private static uint _pcScene, _pcWas;

    /// <summary>PC dumps a session: the boot one plus a few area edges.</summary>
    private const int MaxPcDumps = 6;
    private static int _blackProbed;

    /// <summary>Parts the black probe names per area. Enough for a pattern, few enough to read.</summary>
    private const int BlackProbes = 24;

    /// <summary>A new area is a new budget. <see cref="DbBlackProbe"/>.</summary>
    internal static void DbBlackProbeAreaEdge() => _blackProbed = 0;

    /// <summary>What draws BLACK: an unresolved diffuse samples the white stand-in and zero material terms
    /// shade to nothing, which look identical on screen -- so this names the model, node, texture asked for,
    /// what the registry did with it, and the terms, for the first few NON-TILE offenders. One-shot per AREA,
    /// so the first area cannot spend the whole budget.</summary>
    internal static void DbBlackProbe(int part, int recipe, ulong h0)
    {
        if (_blackProbed >= BlackProbes || _dbPasses < 300) return;
        DbId po = SceneDb.PartObj[part];
        if (!SceneDb.Objects.Valid(po)) return;
        ObjFlag of = SceneDb.ObjFlags[po.Index];
        if ((of & ObjFlag.Tile) != 0) return;
        TexId id = DbTexOf(part, recipe, 0);
        Vector4 dif = DrawRecipe.Diffuse(recipe);
        Vector4 tint = SceneDb.ObjTint[po.Index];
        bool noTex = h0 == 0 || id.IsNull;
        bool blackTerms = dif.X + dif.Y + dif.Z <= 0.001f;
        if (!noTex && !blackTerms) return;
        _blackProbed++;
        CX.Alloy.Mdl.MeshDef? m = DrawRecipe.Mesh(recipe);
        _log?.Info($"AlloyRenderer: [db black] part={part} obj={po.Value} model='{SceneDb.ObjSeamModelName[po.Index]}' "
                   + $"node='{PartTree.NameOf(part)}' flags={of} kind={SceneDb.PartKindOf[part]} "
                   + $"| tex0='{(m != null ? m.Textures[0] : "?")}' id={id.Value} resref='{TexRegistry.ResrefOf(id)}' "
                   + $"state={TexRegistry.StateOf(id)} h0={(h0 != 0 ? "yes" : "NO")} cov={TexRegistry.Coverage(id)} "
                   + $"cls={(AlloyClass)DrawRecipe.Cls(recipe)} | diffuse=({dif.X:F2},{dif.Y:F2},{dif.Z:F2}) "
                   + $"tint=({tint.X:F2},{tint.Y:F2},{tint.Z:F2},{tint.W:F2}) alpha={SceneDb.PartAlpha[part]:F2} "
                   + $"selfillum=({SceneDb.PartSelfIllum[part].X:F2},{SceneDb.PartSelfIllum[part].Y:F2},"
                   + $"{SceneDb.PartSelfIllum[part].Z:F2}) "
                   + $"| world.t=({SceneDb.PartWorld[part].Translation.X:F1},{SceneDb.PartWorld[part].Translation.Y:F1},{SceneDb.PartWorld[part].Translation.Z:F1})");
    }

    /// <summary>Why the player character does not draw. The camera FOLLOWS the PC, so its object is named
    /// without guessing: dump the object, then every part of it and of everything attached to it, with the
    /// term that would refuse each one. One-shot, once the camera has settled.</summary>
    internal static void DbPcProbe(DbId camera)
    {
        // Once the camera has settled, and again on every AREA EDGE: the body a creature carries across one is
        // the recurring DB question (a column kept from the slot's previous tenant, a texture the engine freed
        // under us), and a dump taken only at boot can never answer it. Capped, and keyed on the PC's scene
        // rather than a frame count, which is the edge itself.
        if (_pcSaid >= MaxPcDumps) return;
        DbId pc = SceneDb.Cameras.Valid(camera) ? SceneDb.CamFollow[camera.Index] : default;
        if (!SceneDb.Objects.Valid(pc)) return;
        DbId sc = SceneDb.ObjScene[pc.Index];
        if (_pcSaid == 0) { if (_dbPasses < 600) return; }
        else if (sc.Value == _pcScene && pc.Value == _pcWas) return;
        else if (_dbPasses < _pcAt + 120) return;   // let the new area's first frames settle
        _pcScene = sc.Value; _pcWas = pc.Value; _pcAt = _dbPasses;
        _pcSaid++;
        Pc(pc, "follow", 0);
        PcBelow(pc, 1);
    }

    /// <summary>Everything hanging off <paramref name="obj"/>, both ways down: the pieces <c>Gob::Append</c>
    /// grafted into its tree, and the separate gobs the game ATTACHED to one of its nodes -- cloak, tail,
    /// wings, robe, a wielded item, and the part models a composite weapon is built from. One-shot, so the
    /// table scan per level costs nothing per frame.</summary>
    private static void PcBelow(DbId obj, int depth)
    {
        if (depth > 3) return;
        for (DbId c = SceneDb.ObjSeamAppendHead[obj.Index]; !c.IsNull && SceneDb.Objects.Valid(c);
             c = SceneDb.ObjSeamAppendNext[c.Index]) Pc(c, "append", depth);
        for (int o = 0; o < SceneDb.MaxObjects; o++)
            if (SceneDb.Objects.LiveAt(o) && SceneDb.ObjAttachTo[o].Value == obj.Value
                && (SceneDb.ObjSeamFlags[o] & (uint)SeamFlag.Composed) == 0)
            {
                DbId c = SceneDb.Objects.IdAt(o);
                Pc(c, $"attached at \"{SceneDb.ObjAttachNode[o] ?? "(root)"}\"", depth);
                PcBelow(c, depth + 1);
            }
    }

    private static void Pc(DbId obj, string why, int depth)
    {
        int o = obj.Index;
        int first = SceneDb.ObjPartFirst[o], n = SceneDb.ObjPartCount[o];
        int withMesh = 0, withRecipe = 0, seated = 0, withRange = 0, drawn = 0, skins = 0, skinned = 0;
        string firstBad = "", tex = "";
        for (int k = 0; k < n; k++)
        {
            int p = first + k;
            CX.Alloy.Mdl.ModelDef? pm = SceneDb.PartModel[p];
            int pn = SceneDb.PartNode[p];
            if (pm != null && (uint)pn < (uint)pm.Nodes.Length && pm.Nodes[pn].Skin != null)
            { skins++; if (SceneDb.PartBoneBase[p] >= 0) skinned++; }
            if (SceneDb.PartMeshDef[p] == null) continue;
            withMesh++;
            int rec = SceneDb.PartRecipe[p];
            if (rec < 0) { if (firstBad.Length == 0) firstBad = $"part {k} '{PartTree.NameOf(p)}' has NO RECIPE"; continue; }
            withRecipe++;
            if (tex.Length == 0)
            {
                TexId d = DrawRecipe.Tex(rec, 0);
                tex = $" tex0={(d.IsNull ? "(none)" : TexRegistry.ResrefOf(d))}"
                    + $" {TexRegistry.KindOf(d)}/{TexRegistry.StateOf(d)}"
                    + $" cls={(AlloyClass)DbClsOf(p, rec, false, false)} env={(SceneDb.PartEnvMapped(p) ? 1 : 0)} reveal={SceneDb.PartReveal[p]:F2}"
                    + $" alpha={SceneDb.PartAlpha[p]:F2} fade={SceneDb.PartFade[p]:F2}";
            }
            int rng = DrawRecipe.Range(rec);
            if (rng < 0) { if (firstBad.Length == 0) firstBad = $"part {k} '{PartTree.NameOf(p)}' recipe {rec} has NO RANGE"; continue; }
            withRange++;
            int slot = SceneDb.PartSlot[p];
            if (slot < 0) { if (firstBad.Length == 0) firstBad = $"part {k} '{PartTree.NameOf(p)}' is NOT SEATED"; continue; }
            seated++;
            if (_cIdx[slot] > 0) drawn++;
            else if (firstBad.Length == 0) firstBad = $"part {k} '{PartTree.NameOf(p)}' slot {slot} has ZERO index count";
        }
        _log?.Info($"AlloyRenderer: [db pc] {new string(' ', depth * 2)}{why} obj={obj.Value} "
                   + $"model='{SceneDb.ObjSeamModelName[o]}' flags={SceneDb.ObjFlags[o]} visible={SceneDb.ObjVisible[o]} "
                   + $"scene={SceneDb.ObjScene[o].Value} attachTo={SceneDb.ObjAttachTo[o].Value} "
                   + $"pos=({SceneDb.ObjPos[o].X:F1},{SceneDb.ObjPos[o].Y:F1},{SceneDb.ObjPos[o].Z:F1}) "
                   + $"world.t=({SceneDb.ObjWorld[o].Translation.X:F1},{SceneDb.ObjWorld[o].Translation.Y:F1},{SceneDb.ObjWorld[o].Translation.Z:F1}) "
                   + $"sphere.r={SceneDb.ObjSphere[o].W:F1} | parts={n} withMesh={withMesh} recipe={withRecipe} "
                   + $"range={withRange} seated={seated} drawn={drawn} skinned={skinned}/{skins} "
                   + $"anim='{CX.Alloy.Db.DbAnim.CurrentAnimation(obj) ?? "-"}'"
                   + (CX.Alloy.Db.DbAnim.IsAnimating(obj) ? "" : " NOT ANIMATING") + tex
                   + (firstBad.Length > 0 ? $" | FIRST REFUSAL: {firstBad}" : ""));
    }

    /// <summary>A DB slot's BASE index range, for a consumer that would otherwise walk the VDO item tables.
    /// <para>Resolved live off the arena, never out of <c>_dCount</c>/<c>_dOff</c>/<c>_dBase</c>: those are
    /// indexed by DRAW rank and rewritten by every batch build, so reading them by slot hands a caster another
    /// part's mesh. The arena's offset is in BYTES of <c>GL_UNSIGNED_SHORT</c>, so the index form is half.</para></summary>
    internal static bool DbCasterRange(int slot, out int idxCount, out uint first, out uint baseVert)
    {
        idxCount = 0; first = 0; baseVert = 0;
        if (!DbSlot(slot)) return false;   // the one already defined above: this slot holds a DB part
        int range = DrawRecipe.Range(_oDbRecipe[slot]);
        if (range < 0 || !MeshArenaDraw.Draw(range, 0, out int n, out nint off, out int bv) || n <= 0)
            return false;
        idxCount = n;
        first = (uint)((long)off / 2);
        baseVert = (uint)bv;
        return true;
    }

    /// <summary>An arena range's two coarse runs in the staged record's form -- counts and FIRST INDICES, as
    /// <see cref="DbCasterRange"/> spells them, because the indirect command takes elements where the arena
    /// stores bytes. A count of 0 is a level the simplifier declined, and the dispatch that reads the record
    /// bakes the finer run it does have.</summary>
    internal static bool ArenaLevels(int range, out uint cnt1, out uint first1, out uint cnt2, out uint first2)
    {
        cnt1 = 0; first1 = 0; cnt2 = 0; first2 = 0;
        if (range < 0 || !MeshArena.Levels(range, out nint o1, out int n1, out nint o2, out int n2)) return false;
        cnt1 = (uint)n1; first1 = (uint)((long)o1 / 2);
        cnt2 = (uint)n2; first2 = (uint)((long)o2 / 2);
        return true;
    }

    /// <summary>A DB caster's two coarse runs, for the staging the per-view dispatch picks a level out of.
    /// False (and zeros) = not a DB part, so it bakes its one range in every view.</summary>
    internal static bool DbCasterLevels(int slot, out uint cnt1, out uint first1, out uint cnt2, out uint first2)
    {
        cnt1 = 0; first1 = 0; cnt2 = 0; first2 = 0;
        return DbSlot(slot)
               && ArenaLevels(DrawRecipe.Range(_oDbRecipe[slot]), out cnt1, out first1, out cnt2, out first2);
    }

    /// <summary>A DB caster's alpha-mask texture: the recipe's diffuse, the same texture the colour pass binds.
    /// <para>A <see cref="TexId"/> value -- a REGISTRY SLOT, not a GL name and not an engine tid: the mask's
    /// identity, for a key or a counter. A stage that BINDS one wants <see cref="DbCasterMask"/>.</para></summary>
    internal static int DbCasterTid(int slot)
        => DbSlot(slot) ? (int)DrawRecipe.Tex(_oDbRecipe[slot], 0).Value : 0;

    /// <summary>A DB caster's mask as the masked DEPTH stage needs it: the GL name it binds and what the decode
    /// said that texture's alpha carries. False = nothing to alpha-test against, and the caster casts solid.
    /// <para>Never through <c>OwnedTextures</c>: database textures skip the engine's upload funnel, so its
    /// tables know none of them. <paramref name="holes"/> is a VERDICT (cutout 1, slab 0), not a fraction.</para></summary>
    internal static bool DbCasterMask(int slot, out int name, out float holes, out float graded)
    {
        name = 0; holes = 0f; graded = 0f;
        if (!DbSlot(slot)) return false;
        TexId id = DrawRecipe.Tex(_oDbRecipe[slot], 0);
        if (!TexRegistry.TryDrawable(id, out uint gl, out _)) return false;
        name = (int)gl;
        switch (TexRegistry.Coverage(id))
        {
            case TexCoverage.Opaque: break;                             // a slab: the solid stage
            case TexCoverage.Blended: holes = 1f; graded = 1f; break;    // #363's glass, through the dither
            default: holes = 1f; break;                                 // Cutout, and Unknown taken as one
        }
        return true;
    }

    /// <summary>Write slot <paramref name="i"/>'s per-draw record from the database columns -- the DB-mode
    /// counterpart of <c>StagePart</c>'s record half, with every term a column read instead of a pointer
    /// chase. Returns false when the part has no resident geometry yet (counted, re-asked next frame).</summary>
    internal static bool DbStage(int i)
    {
        int part = _oDbPart[i];
        if (part < 0) return false;
        int recipe = _oDbRecipe[i];
        int range = DrawRecipe.Range(recipe);
        if (range < 0) { _dbNoRange++; return false; }
        // the BASE run is the residency probe; the drawn level is the batch row's (CX-50)
        if (!MeshArenaDraw.Draw(range, 0, out _, out _, out _))
        { _dbNoRange++; return false; }

        DbId obj = SceneDb.PartObj[part];
        int o = obj.Index;
        ref DrawRecordBlock r = ref _dData[i];
        bool fresh = _gate[i].RecGen != _recEpoch || _gate[i].RecEnroll != _gate[i].EnrollGen;
        var f = default(RecordFrameState);
        f.HasModel = true;
        f.Model = SceneDb.PartWorld[part];
        // The keyhole lane is the stage's (1 = dissolving, gated on lit and feature); PartDissolve is the
        // inverse sense, and writing it here flipped every folded solid part to dissolving until the stage
        // flipped it back -- two record rewrites a fold, enough to push the frame into a whole-table upload.
        f.Dissolve = fresh ? 0f : r.Dissolve;
        Vector4 tint = SceneDb.ObjTint[o];
        f.Alpha = SceneDb.PartAlpha[part] * SceneDb.PartFade[part] * tint.W;
        f.Lit = _oLit[i];
        if (f.Lit)
        {
            Vector4 amb = DrawRecipe.Ambient(recipe), dif = DrawRecipe.Diffuse(recipe);
            f.AmbR = amb.X * tint.X; f.AmbG = amb.Y * tint.Y; f.AmbB = amb.Z * tint.Z;
            f.DifR = dif.X * tint.X; f.DifG = dif.Y * tint.Y; f.DifB = dif.Z * tint.Z;
            Vector3 emi = SceneDb.PartSelfIllum[part] + SceneDb.ObjEmissive[o];
            f.EmiR = emi.X; f.EmiG = emi.Y; f.EmiB = emi.Z;
            f.Slots = _oSlots[i];
        }
        f.FeatX = ShaderFeatures.WithPose((int)_oFeat[i * 4], _oBone[i] >= 0);
        f.FeatY = _oBone[i] >= 0 ? _oBone[i] : _oFeat[i * 4 + 1];

        if (fresh) DbWriteStable(ref r, i);
        // #302's mover level, stamped HERE as well as in StageDbPart: this fold write consumes the move, so the
        // stage that runs after it in the same frame sees the model already in place and would never stamp it.
        bool casterMoved = Moved(in r.Model, in f.Model, CasterStep);
        bool moved = WritePerFrame(ref r, in f, roll: true, fresh: fresh);
        if (fresh || moved) MarkRecordDirty(i);
        if (casterMoved || fresh) _recMovedAt[i] = _frameSeq;
        _gate[i].RecGen = _recEpoch; _gate[i].RecEnroll = _gate[i].EnrollGen; _gate[i].RecFrame = _frameSeq;
        DbWriteSphere(i, in f);
        return true;
    }

    /// <summary>The DB-mode stable half: the same record fields <c>WriteStable</c> writes, sourced from the
    /// recipe and the object's flags instead of <c>_oTri</c>'s vtable and <c>_oGob</c>'s attachment chain.</summary>
    private static void DbWriteStable(ref DrawRecordBlock r, int i)
    {
        int recipe = _oDbRecipe[i], part = _oDbPart[i];
        int o = SceneDb.PartObj[part].Index;
        ObjFlag of = (uint)o < SceneDb.MaxObjects ? SceneDb.ObjFlags[o] : ObjFlag.None;
        r.Cos = _oCos[i]; r.Sin = _oSin[i];
        int fo = i * 4;
        r.FeatZ = _oFeat[fo + 2];
        // feat.w = -1 marks a DANGLY part for the receiver gate, exactly as the engine path's vtable probe did.
        r.FeatW = (DrawRecipe.Flags(recipe) & RecipeFlag.Dangly) != 0 ? -1f : _oFeat[fo + 3];
        if (!_oLit[i]) return;
        r.AmbW = 1f; r.DifW = 1f;
        // emi.w, the contact-shadow receiver class and the snow gate: 1 tileset, 0 object, -1 creature. Up the
        // ATTACHMENT chain (CreaturePart), because a body piece, a held weapon or a creature's VFX is its own
        // object and only the body carries the flag -- the object's own flags snowed on every PC's limbs. An
        // EFFECT-class model (a VFX) is no surface for snow either.
        bool fx = ((SceneDb.PartModel[part]?.Classification ?? 0) & HitTest.ClassEffect) != 0;
        r.EmiW = (of & ObjFlag.Tile) != 0 ? 1f : fx || CreaturePart(i) ? -1f : 0f;
        if (r.EmiW < 0f) RecCreature++; else if (r.EmiW > 0f) RecTile++; else RecObject++;
        ref ParamRow p = ref _oParam[i];
        r.PbrR = p.Specularity; r.PbrG = p.Roughness; r.PbrB = p.Metallicness;
        r.PbrX = p.CustomR; r.PbrY = p.CustomG; r.PbrZ = p.CustomB; r.PbrW = p.CustomDeclared;
        r.Ext = p.Ext;
    }

    /// <summary>The DB-mode cull sphere: the recipe's model-space centre and radius through the part's world
    /// matrix. The three exemptions are the engine path's own and for the same reason -- a posed skin, a
    /// dangly and a displacing feature all move their vertices under a matrix that does not.</summary>
    private static void DbWriteSphere(int i, in RecordFrameState f)
    {
        RecipeFlag rf = DrawRecipe.Flags(_oDbRecipe[i]);
        bool exempt = _oBone[i] >= 0 || (rf & RecipeFlag.Dangly) != 0
                      || ContentPrograms.MayMove((int)_oFeat[i * 4]) || !(_oR[i] > 0f);
        float cx, cy, cz, rad;
        if (exempt) { cx = 0f; cy = 0f; cz = 0f; rad = NeverCull; }
        else
        {
            Vector3 c = Vector3.Transform(new Vector3(_oCx[i], _oCy[i], _oCz[i]), f.Model);
            cx = c.X; cy = c.Y; cz = c.Z;
            rad = _oR[i] * DbScaleOf(in f.Model);
        }
        _dSph[i] = new Sphere { Center = new Vector3(cx, cy, cz), Radius = rad };
        // The caster sphere is the same bound, GEOMETRIC even for the exempted kinds, with a skinned part's
        // pose slack: a bind-pose box does not reach where an extended arm or a swinging cloak does.
        Vector3 gc = Vector3.Transform(new Vector3(_oBx[i], _oBy[i], _oBz[i]), f.Model);
        float gr = _oBr[i] * DbScaleOf(in f.Model);
        float bare = gr;
        // A rigged dangly part leaves its rest pose by its own authored cage, not by a creature's reach, and
        // its recipe carries BOTH flags -- so this branch has to come first.
        if ((rf & RecipeFlag.Dangly) != 0 && _oBone[i] >= 0)
            gr += DbAnim.DanglyCasterSlack(_oDbPart[i]) * DbScaleOf(in f.Model);
        else if ((rf & RecipeFlag.Skinned) != 0) gr = MathF.Max(gr + 2f, 3f);
        if (!(gr > 0f)) gr = DegenerateCasterRadius;
        _dCastSlack[i] = bare > 0f ? gr - bare : 0f;
        _dCastSph[i] = new Sphere { Center = gc, Radius = gr };
    }

    /// <summary>The largest axis scale a composed matrix applies -- the factor a radius has to carry so a
    /// scaled object culls about its own silhouette rather than its pivot (the scaled-gob-cull lesson).</summary>
    private static float DbScaleOf(in Matrix4x4 m)
    {
        float a = new Vector3(m.M11, m.M12, m.M13).Length();
        float b = new Vector3(m.M21, m.M22, m.M23).Length();
        float c = new Vector3(m.M31, m.M32, m.M33).Length();
        return MathF.Max(a, MathF.Max(b, c));
    }

    /// <summary>The slot-table bookkeeping a self-check has to put back, so exercising the DB path at
    /// Initialize cannot leave the live table holding scratch tenants.</summary>
    internal readonly record struct DbSnapshot(int OwnCount, int OwnLive, int FreeCount, int FreeAvail);

    internal static DbSnapshot DbTableSnapshot() => new(_ownCount, _ownLive, _freeCount, _freeAvail);

    /// <summary>The ownership table's high-water mark, for the self-check that asserts a released DB slot is
    /// handed back out rather than the table only ever growing (R1-3).</summary>
    internal static int OwnHighForSelfCheck => _ownCount;

    /// <summary>The class a seated slot carries, for the order check's own assertions.</summary>
    internal static int ClsOfForSelfCheck(int slot) => _oCls[slot];

    internal static void DbTableRestore(DbSnapshot s)
    {
        // R1-26: every slot at or above the snapshot's high-water mark, plus any the check seated below it
        // (DbRelease already cleared those). A scratch tenant left in _oDbPart would make a live engine slot
        // read as the database's.
        for (int i = 0; i < _ownCount && i < MaxOwned; i++)
            if (i >= s.OwnCount || _oDbPart[i] >= 0)
            { _oDbPart[i] = -1; _oDbRecipe[i] = -1; _oTri[i] = IntPtr.Zero; }
        _ownCount = s.OwnCount; _ownLive = s.OwnLive;
        _freeCount = s.FreeCount; _freeAvail = s.FreeAvail;
        _dbSeated = 0; _dbReleased = 0; _dbNoRange = 0;
    }

    /// <summary>Bumped whenever the DB slot SET changes (a seat or a release). The order and the area index are
    /// rebuilt off this edge and no other, so a frame in which nothing was created or destroyed re-sorts
    /// nothing.</summary>
    internal static int DbSetGen = 1;

    /// <summary>The slot set changed: a DB part was seated, released, or its arena range moved.
    /// <para>A STATIC part also has to reach the shadow side: the sun's staleness predicate is
    /// <c>invalidate || keyChanged || hadMover || hasMover</c>, and a static DB world trips none of them. A
    /// mover's arrival and departure are the predicate's own appeared and ghost terms, scoped to its views.</para></summary>
    private static void DbSetChanged(bool world = true)
    {
        DbSetGen++;
        if (world) CasterPass.ShadowInvalidate();
    }
    private static int _dbOrderGen;

    /// <summary>Rebuild the draw order and the area index if the slot set has moved since the last one;
    /// returns the live order count. It re-stamps NOTHING: the caster list reads a slot's enrolment stamp as
    /// the tenancy it collected under, so re-stamping every live slot on a set edge would turn the whole
    /// standing list off. <see cref="DbSeat"/> is the only place a DB slot changes tenant.</summary>
    internal static int DbOrder()
    {
        if (_dbOrderGen == DbSetGen) return _orderCount;
        _dbOrderGen = DbSetGen;
        long t0 = FrameZones.Now;
        RebuildOrder();
        _churn.Rebuilds++; _churn.Ticks += FrameZones.Now - t0;
        return _orderCount;
    }

    /// <summary>Off-device: the DB draw ORDER. Every live slot appears exactly once, the order is grouped by
    /// class, and each class's [start, end) range covers exactly its own slots -- a slot that fell out is
    /// invisible geometry, and an overlapping range draws parts in the wrong pass. Neither errors.</summary>
    internal static bool DbOrderSelfCheck(ExtenderLog log, int[] slots, int[] wantCls)
    {
        var check = new Checker(log, "AlloyRenderer.DbOrderSelfCheck");
        DbSetChanged();
        _dbOrderGen = DbSetGen - 1;
        int live = DbOrder();
        check.Expect(live == slots.Length,
                     $"the order holds the {slots.Length} slot(s) the caller seated and nothing else ({live})");

        int found = 0;
        for (int k = 0; k < _orderCount; k++)
        {
            int i = _order[k];
            for (int s = 0; s < slots.Length; s++) if (slots[s] == i) { found++; break; }
        }
        check.Expect(found == slots.Length,
                     $"every seated DB slot is in the order exactly once ({found} of {slots.Length})");

        // The order is grouped by class, and each class's range holds only its own.
        bool grouped = true, ranged = true;
        byte last = 0;
        for (int k = 0; k < _orderCount; k++)
        {
            byte c = _oCls[_order[k]];
            if (c < last) grouped = false;
            last = c;
        }
        for (int c = 0; c < AlloyClasses.Count; c++)
            for (int k = _clsStart[c]; k < _clsEnd[c]; k++)
                if (_oCls[_order[k]] != c) ranged = false;
        check.Expect(grouped, "the order walks grouped by class, so each pass owns a contiguous run");
        check.Expect(ranged, "...and each class's range holds only that class's slots");

        int covered = 0;
        for (int c = 0; c < AlloyClasses.Count; c++) covered += _clsEnd[c] - _clsStart[c];
        check.Expect(covered == _orderCount, "the class ranges cover the order exactly once over");
        for (int s = 0; s < slots.Length && s < wantCls.Length; s++)
            check.Expect(_oCls[slots[s]] == wantCls[s], $"slot {s} kept the class its recipe gave it");
        return check.Ok;
    }

    /// <summary>Slots seated, released and refused for want of resident geometry since arming -- the feed's
    /// period line, so a hole is a number rather than an absence.</summary>
    internal static int _dbSeated, _dbReleased, _dbNoRange;
    /// <summary>Whole-table revokes that stood down because the slot space is the feed's. Counted so a DB
    /// session that is tearing down every frame is visible rather than merely slow.</summary>
    internal static int _dbRevokeAll;

    internal static string DbTableNote()
        => $"db: seated {_dbSeated} released {_dbReleased} no-geometry {_dbNoRange} live {_ownLive}"
         + (_capacitySpill > 0 ? $" SPILLED {_capacitySpill}" : "") + _churn.Note();

    private static readonly OrderChurnState _churn = new();

    /// <summary>What moved the slot set this period: order rebuilds and their cost, and the seats and releases
    /// behind them split static/mover, with the objects that churned most.</summary>
    private sealed class OrderChurnState
    {
        internal int Rebuilds, Static, Mover;
        internal long Ticks, IndexTicks;
        private readonly int[] _byObj = new int[SceneDb.MaxObjects];

        /// <summary>Was the slot's tenant a static object when it was seated? Read back at the release, when
        /// the object row may already be dead or recycled.</summary>
        private readonly bool[] _static = new bool[MaxOwned];

        /// <summary>Count one seat; true when the part belongs to a static object, or to none that can be named,
        /// which is treated as the world.</summary>
        internal bool Seat(int slot, int part)
        {
            DbId o = SceneDb.PartObj[part];
            bool world = !SceneDb.Objects.Valid(o) || (SceneDb.ObjFlags[o.Index] & ObjFlag.Static) != 0;
            if (SceneDb.Objects.Valid(o)) _byObj[o.Index]++;
            _static[slot] = world;
            return Count(world);
        }

        /// <summary>Count one release, by the static-ness its seat recorded.</summary>
        internal bool Release(int slot, int part)
        {
            DbId o = SceneDb.PartObj[part];
            if (SceneDb.Objects.Valid(o)) _byObj[o.Index]++;
            return Count(_static[slot]);
        }

        private bool Count(bool world)
        {
            if (world) Static++; else Mover++;
            return world;
        }

        internal string Note()
        {
            Span<int> top = stackalloc int[3];
            top.Fill(-1);
            for (int o = 0; o < _byObj.Length; o++)
            {
                int c = _byObj[o];
                if (c == 0) continue;
                for (int t = 0; t < top.Length; t++)
                {
                    if (top[t] >= 0 && _byObj[top[t]] >= c) continue;
                    for (int u = top.Length - 1; u > t; u--) top[u] = top[u - 1];
                    top[t] = o;
                    break;
                }
            }
            var sb = new System.Text.StringBuilder(
                $"; order rebuilt {Rebuilds}x ({Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} ms, index "
                + $"{IndexTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1}) "
                + $"off {Static} static / {Mover} mover seat+release(s), most:");
            foreach (int o in top)
                if (o >= 0)
                    sb.Append($" '{SceneDb.ObjSeamModelName[o]}' x{_byObj[o]} flags={SceneDb.ObjFlags[o]}");
            Rebuilds = Static = Mover = 0; Ticks = IndexTicks = 0;
            Array.Clear(_byObj);
            return sb.ToString();
        }
    }
}
