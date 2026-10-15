using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>The engine FUNCTIONS the seam answers instead of the engine: the object factories and the free
/// functions that reach into a <c>Gob</c>'s or a <c>Part</c>'s innards rather than going through a vtable.
/// <para>The list is short because the shell is layout-compatible, so most such free functions run correctly
/// on a facade UNHOOKED. The ones here cannot: they walk an engine <c>Part</c> tree, or they allocate.</para></summary>
public sealed unsafe class ObjectFns : ISeamProvider
{
    private static ExtenderLog? _log;
    private static long _missingModels, _streamRefusals, _unreadableModels;

    public void Register(SeamRegistry r)
    {
        _log = Facade.Log;
        r.ReplaceEngine(SymbolMap.NewCAurObject, (void*)(delegate* unmanaged<nint, nint, nint, nint>)&NewCAurObject);
        r.ReplaceEngine(SymbolMap.NewCAurString, (void*)(delegate* unmanaged<int, nint>)&NewCAurString);
        r.ReplaceEngine(SymbolMap.SetShadowsPart, (void*)(delegate* unmanaged<nint, int, void>)&SetShadows);
        r.ReplaceEngine(SymbolMap.AurSetDynamicProjectionLight, (void*)(delegate* unmanaged<nint, void>)&AurSetDynamicProjectionLight);
        r.AfterTablesBuilt(PartShell.Build);
        // CX-120 seam-feed round 2: a chunk emitter spawns real objects, so it goes through the object
        // factory rather than the particle arrays. Unset, the feed counts a refusal instead of guessing.
        DbInputs.ChunkSpawn = SpawnChunk;
        DbInputs.ChunkPlace = PlaceChunk;
        DbInputs.ChunkScene = ChunkJoin;
        DbInputs.ChunkDestroy = ObjectSlots.DestroyRecord;
        // CX-114: a reference node names another model to instance. The part-tree builder reports it rather
        // than recursing, because the thing a reference becomes is an OBJECT with its own lifetime -- which is
        // the engine's `CreateReferenceObjects` @0x5421e0, and is why the factory is the one that answers it.
        PartTree.OnReference = OnReference;
    }

    /// <summary><c>NewCAurObject(char const* model, char const* name, FILE* stream)</c> @0x4412c0. Arg 2 is a
    /// NAME OVERRIDE -- empty or null means "name it after the model" -- and arg 3 an already-open stream to
    /// parse instead of the resref. Returns NULL when the model does not resolve, which every caller
    /// null-checks; the differences list says what the seam does not reproduce.</summary>
    [UnmanagedCallersOnly]
    private static nint NewCAurObject(nint modelName, nint name, nint stream) => NewCAurObjectBody(modelName, name, stream);

    /// <summary>Does the engine's resource lookup know this model? The one question that tells a model that is
    /// genuinely absent (NULL, as the engine answers) from one CX merely failed to read (an object with no
    /// model). Indirected so the self-check can state both answers without a client.</summary>
    internal static Func<string, bool> ResourcePresent = ModelFeed.Present;

    /// <summary>The factory by name, for <see cref="FacadeSelfCheck"/>: the same body the engine's call site
    /// reaches, with no engine pointer to marshal.</summary>
    internal static nint CreateForCheck(string resref)
    {
        byte[] nm = System.Text.Encoding.UTF8.GetBytes(resref + "\0");
        fixed (byte* p = nm) return NewCAurObjectBody((nint)p, 0, 0);
    }

    private static nint NewCAurObjectBody(nint modelName, nint name, nint stream)
    {
        if (stream != 0)
        {
            _streamRefusals++;
            if (_streamRefusals == 1)
                _log?.Always("seam: NewCAurObject was handed an open FILE* to parse a model from; CX's model cache reads by "
                           + "resref only, so the call is refused rather than answered with an empty object");
            return 0;
        }
        string? resref = modelName == 0 ? null : Marshal.PtrToStringUTF8(modelName);
        if (string.IsNullOrEmpty(resref)) return 0;
        // NewCAurObject @0x5412c0: with g_bEnableLOD on it reads the model's .lod first, and the file names
        // all three levels. Read for EVERY object, as the engine does; the switch is what CX widens (DbAnim.LodChoose
        // runs for movers too, where the engine only switches gobs its tile BSP walk reaches -- statics).
        string? lodText = DbAnim.LodEnabled ? TexRegistry.ReadResourceText(resref, LodResType) : null;
        ParseLod(lodText, out string lod0, out float lodNear, out string lod1, out float lodFar, out string lod2);
        // ponytail: a .lod with no level-0 name keeps the resref (the engine would return NULL)
        if (lod0.Length > 0) resref = lod0;
        ModelDef? model = ModelCache.Acquire(resref, out string? why);
        if (model == null && ResourcePresent(resref))
        {
            // CX's own read failed on a model the engine's loader would have answered, so a NULL here is a
            // CX difference and not the engine's behaviour -- and one caller cannot survive it:
            // CGuiObject::SetModel @0x586520 retries with "GUI_empty" and then dereferences the result at
            // +0x45 with no null check, which is the DB-mode SIGSEGV in the item radial
            // (CRadialMenu::CRadialMenu -> SetModel("GUI_RA_MENU")). The record is minted with no model
            // instead: the object exists, draws nothing, and the refusal is loud rather than fatal.
            _unreadableModels++;
            if ((_unreadableModels & (_unreadableModels - 1)) == 0)
                _log?.Always($"seam: NewCAurObject(\"{resref}\") -- the resource IS there and CX could not load it "
                           + $"({why ?? "no reason given"}); the object is minted with NO MODEL rather than as a NULL the "
                           + $"engine's GUI path would crash on ({_unreadableModels} so far)");
        }
        else if (model == null)
        {
            _missingModels++;
            if ((_missingModels & (_missingModels - 1)) == 0)
                _log?.Always($"seam: NewCAurObject(\"{resref}\") -- no such model, returning NULL as the engine does ({_missingModels} so far)");
            return 0;
        }

        DbId id = SceneDb.Objects.Alloc();
        if (id.IsNull)
        {
            ModelCache.Release(model);
            _log?.Always($"seam: the object table is full -- NewCAurObject(\"{resref}\") refused");
            return 0;
        }
        int i = id.Index;
        DbFeed.InitObject(i);
        SceneDb.ObjKindOf[i] = ObjKind.Model;
        SceneDb.ObjModel[i] = model;
        SceneDb.ObjSeamModelName[i] = resref;
        SceneDb.SetAttachTo(i, default);
        SceneDb.SetAttachPart(i, -1);
        SceneDb.ObjPos[i] = Vector3.Zero;
        SceneDb.ObjRot[i] = Quaternion.Identity;
        SceneDb.ObjScale[i] = 1f;
        SceneDb.ObjVisible[i] = true;
        SceneDb.ObjTint[i] = Vector4.One;
        SceneDb.ObjEmissive[i] = Vector3.Zero;
        SceneDb.ObjSeamPrevPos[i] = new Vector3(SceneDb.NoPrevPos);
        SceneDb.ObjSeamLod[i] = 0;
        SceneDb.ObjSeamLodDist1[i] = lodNear;
        SceneDb.ObjSeamLodDist2[i] = lodFar;
        ModelDef? alt1 = lod1.Length > 0 ? ModelCache.Acquire(lod1, out _) : null;
        ModelDef? alt2 = lod2.Length > 0 ? ModelCache.Acquire(lod2, out _) : null;
        // a level 2 with no level 1 stands in as level 1: the choice steps one level at a time
        if (alt1 == null) (alt1, alt2) = (alt2, null);
        SceneDb.ObjSeamLodModel1[i] = alt1;
        SceneDb.ObjSeamLodModel2[i] = alt2;
        // Gob::Gob's own defaults: shadows on, highlight propagation on, in-view true until the first cull,
        // animations enabled. The in-view default is why a brand-new object draws on the frame it was made.
        SceneDb.ObjSeamFlags[i] = (uint)(SeamFlag.Shadows | SeamFlag.PropagateHighlight | SeamFlag.InView | SeamFlag.Animate);

        nint shell = Facade.Mint(FacadeKind.Object, id);
        if (shell == 0)
        {
            SceneDb.Objects.Free(id);
            ModelCache.Release(model);
            if (alt1 != null) ModelCache.Release(alt1);
            if (alt2 != null) ModelCache.Release(alt2);
            SceneDb.ObjSeamLodModel1[i] = SceneDb.ObjSeamLodModel2[i] = null;
            return 0;
        }
        SeedShell(shell);
        new Gob(shell).LodNear = lodNear;
        new Gob(shell).LodFar = lodFar;
        string? label = name == 0 ? null : Marshal.PtrToStringUTF8(name);
        // NewCAurObject: no name of its own takes the model's STATED name, case kept -- a .gui panel's scene is
        // named after it, and Camera::UpscaleViewport compares that scene name exactly.
        Facade.SetName(shell, !string.IsNullOrEmpty(label) ? label
                            : !string.IsNullOrEmpty(model?.StatedName) ? model.StatedName : resref);

        if (model != null) MtrFeed.Warm(model);
        if (alt1 != null) MtrFeed.Warm(alt1);
        if (alt2 != null) MtrFeed.Warm(alt2);
        if (model != null && !PartTree.Build(id, model, new PartTree.TreeOptions { Lod1 = alt1, Lod2 = alt2 }))
            _log?.Always($"seam: the part tree of \"{resref}\" could not be built -- the object exists with no geometry");
        ObjectSlots.NoteSkinned(id);
        // The lifecycle order the integration contract fixes, and each step wants the one before it: the
        // animation runtime needs the part range, and the hit index needs the bounds the tree gave.
        DbAnim.ObjectCreated(id);
        if (alt1 != null)
        {
            DbAnim.SetLodModels(id, alt1, alt2);
            DbAnim.SetLodDistances(id, lodNear, lodFar);
        }
        HitFeed.NoteObject(id);
        SceneDb.Touch(id, Dirty.Created | Dirty.Tree | Dirty.Xform);

        // The engine's own two construction steps, in its order: the emitter-target event, then the minimum
        // box (only when the model asks for it) and the minimum sphere.
        // Gob+0x90 is the root Part*, and AurEnableShadowing @0x461ad0 / AurDisableShadowing @0x461af0 read
        // it WITHOUT a vtable and hand it to the free SetShadows. A seam Part shell for the root is what
        // makes those two work unhooked (SetShadows itself is replaced, and resolves this pointer).
        if (SceneDb.ObjPartCount[i] > 0)
            new Gob(shell).PartTree = new Part(PartShell.For(id, SceneDb.ObjPartFirst[i]));
        ObjectSlots.FireEvent(shell, EventNames.EmitterTarget, 0);
        ObjectSlots.RecomputeBounds(id, shell);
        return shell;
    }

    /// <summary>The <c>.lod</c> resource type <c>NewCAurObject</c> asks <c>CExoResMan</c> for.</summary>
    internal const ushort LodResType = 0x81e;   // not-an-offset: a resource type id

    /// <summary><c>NewCAurObject</c>'s <c>sscanf("%63s\n%f%63s\n%f%63s")</c> over a <c>.lod</c>: level-0 model,
    /// near distance, level-1 model, far distance, level-2 model, stopping at the first field that does not
    /// parse. What it leaves unread keeps the engine's defaults: no name, <see cref="LodNoSwitch"/>.</summary>
    internal static void ParseLod(string? text, out string m0, out float d1, out string m1, out float d2, out string m2)
    {
        m0 = m1 = m2 = ""; d1 = d2 = LodNoSwitch;
        string[] t = text?.Split((char[]?)null, 5, StringSplitOptions.RemoveEmptyEntries) ?? [];
        static bool F(string[] t, int k, ref float d)
        {
            if (k >= t.Length || !float.TryParse(t[k], System.Globalization.NumberStyles.Float,
                                                 System.Globalization.CultureInfo.InvariantCulture, out float v)) return false;
            d = v;
            return true;
        }
        static string S(string[] t, int k) => k < t.Length ? t[k].Split((char[]?)null)[0] : "";
        if ((m0 = S(t, 0)).Length == 0 || !F(t, 1, ref d1) || (m1 = S(t, 2)).Length == 0 || !F(t, 3, ref d2)) return;
        m2 = S(t, 4);
    }

    /// <summary>The LOD switch distance <c>NewCAurObject</c> pre-seeds both channels with (99999.0f) when the
    /// object has no <c>.lod</c> resource, i.e. "never switch".</summary>
    private const float LodNoSwitch = 99999f;

    /// <summary>The non-zero defaults <c>Gob::Gob</c> leaves in the block, at the engine's own offsets. A
    /// zeroed shell would be an object with no scale, a black colour fold and an inverted identity.</summary>
    internal static void SeedShell(nint shell)
    {
        var g = new Gob(shell);
        g.Rot.W = 1f;                                           // w first: Gob::Gob writes 1.0f at +0xb4
        Span<float> ch = g.ScaleChannels;
        ch[0] = 1f; ch[1] = 1f;
        g.ScaleBase = 1f;
        g.Colour = Vector3.One;
        g.Alpha = 1f;
        g.SeamShadows = 1;
        g.PropagateHilite = 1;
        g.InView = 1;
        g.SeamTriMeshArea = -1f;   // the engine's "area not computed" sentinel
        VisualXform.Seed(shell);
        g.LodLevel = 0;
        g.LodNear = LodNoSwitch;
        g.LodFar = LodNoSwitch;
    }

    /// <summary><c>NewCAurString(int maxLines)</c> @0x494050: allocate a <c>StringGob</c> and return its
    /// <c>CAurString</c> sub-object at +0x4c0. The seam's text shell is <c>StringGob</c>-sized with the
    /// <c>Gob</c> table at +0 and the <c>CAurString</c> table at +0x4c0, so the hard-coded -0x4c0 and -0x70
    /// adjusts in <c>RemoveAuroraString</c> and <c>CAurString::DieWithoutParent</c> land on the shell base.</summary>
    [UnmanagedCallersOnly]
    private static nint NewCAurString(int maxLines)
    {
        DbId id = TextObjects.Create(default, maxLines);
        if (id.IsNull)
        {
            _log?.Always($"seam: NewCAurString({maxLines}) -- the text record could not be made, returning NULL");
            return 0;
        }
        nint shell = Facade.Mint(FacadeKind.String, id);
        if (shell == 0) { TextObjects.Destroy(id); return 0; }
        SeedShell(shell);
        SceneDb.Touch(id, Dirty.Created | Dirty.Text);
        return new StringGob(shell).StringSub;
    }

    /// <summary><c>SetShadows(Part*, int)</c> @0x460c90 -- a FREE function the engine recurses over a part
    /// tree, writing the flag on every light node. <c>AurEnableShadowing</c> @0x461ad0 and
    /// <c>AurDisableShadowing</c> @0x461af0 read <c>Gob+0x90</c> and call it, so replacing this one answers
    /// both without hooking either, and the root <c>Part*</c> the shell carries at +0x90 is a seam Part shell.</summary>
    [UnmanagedCallersOnly]
    private static void SetShadows(nint part, int on)
    {
        if (!PartShell.Resolve(part, out DbId obj, out _))
        {
            if (part != 0) Facade.Log?.Always($"seam: SetShadows on 0x{part:x}, which is not a CX part -- ignored");
            return;
        }
        nint shell = Facade.ObjectShell(obj);
        if (shell == 0) return;
        new Gob(shell).SeamShadows = on != 0 ? (short)1 : (short)0;
        ObjectSlots.Flag(obj, SeamFlag.Shadows, on != 0);
        SceneDb.Touch(obj, Dirty.Visible);
    }

    /// <summary><c>AurSetDynamicProjectionLight(CAurObject*)</c> @0x461b10: the engine collects the object's
    /// lights and filters on each element's +0x74/+0x80, i.e. engine <c>Part</c> memory the database has no
    /// counterpart for. SIMPLIFIED: the seam marks the object as the dynamic projection source and lets the
    /// light set decide, since Alloy's shadow pass already picks the qualifying light each frame.</summary>
    [UnmanagedCallersOnly]
    private static void AurSetDynamicProjectionLight(nint self)
    {
        // CNWCArea passes NULL every frame no object projects; vanilla returns on it (@0x461b10's first test).
        if (self == 0) return;
        DbId id = Facade.Object(self, SeamSlot.Unnamed);
        if (id.IsNull) return;
        SceneDb.ObjSeamLightPri[id.Index] = int.MaxValue;
        SceneDb.Touch(id, Dirty.Material);
    }

    /// <summary>Create an object from <paramref name="resref"/> and attach it to part
    /// <paramref name="part"/> of <paramref name="parent"/>, in the parent's scene -- what a reference node
    /// and an <c>Append</c> both come down to. Null when the model does not resolve.</summary>
    internal static DbId Compose(string resref, DbId parent, int part)
    {
        nint name = Marshal.StringToCoTaskMemUTF8(resref);
        nint child;
        try { child = NewCAurObjectBody(name, 0, 0); }
        finally { Marshal.FreeCoTaskMem(name); }
        if (child == 0) return default;
        DbId id = Facade.Object(child, SeamSlot.Unnamed);
        if (id.IsNull) return default;
        SceneDb.SetAttachTo(id.Index, parent);
        SceneDb.SetAttachPart(id.Index, part);
        // The piece is part of the parent's ONE tree, not a gob riding one of its nodes.
        ObjectSlots.Flag(id, SeamFlag.Composed, true);
        nint ps = Facade.ObjectShell(parent);
        ObjectSlots.JoinScene(id, child, SceneDb.ObjScene[parent.Index], ps == 0 ? 0 : Facade.SceneWord(ps));
        SceneDb.Touch(id, Dirty.Tree | Dirty.Xform);
        return id;
    }

    /// <summary><c>CreateReferenceObjects</c>'s work: the reference part names a model, so a child object is
    /// created from it and attached to that part. The child is an ordinary record with an ordinary lifetime --
    /// it dies when its parent does, through the attachment.</summary>
    private static void OnReference(DbId obj, int part, string model)
    {
        DbId child = Compose(model, obj, part);
        if (child.IsNull || !SceneDb.Objects.Valid(obj)) return;
        // A reference child is owned by its PART, not by the attachment: PartReference::~PartReference
        // @0x4448e0 runs the child's deleting destructor off `part + 0x68`, and that part dies with the
        // parent's tree in Gob::~Gob. (The attachment behaviour does NOT do it -- UpdateScenesAttach
        // @0x49fc70 deletes the attached object only when the behaviour's +0x3c flag is set, and
        // CAurBehaviorAttach's constructor zeroes it; only CAurObject::DieWithoutParent's
        // "diewithoutparent 1" message sets it.) So a reference child goes on the same owned list as an
        // appended one, and the parent's release destroys both -- without this it outlived its parent for
        // the session and the object table grew across every area change.
        SceneDb.ObjSeamAppendNext[child.Index] = SceneDb.ObjSeamAppendHead[obj.Index];
        SceneDb.ObjSeamAppendHead[obj.Index] = child;
        // The engine's PartReference+0x68: the part keeps its instance, which is what Gob::ReattachReference
        // re-points and what SetEmitterTarget reads back as a p2p emitter's target.
        if ((uint)part < (uint)SceneDb.MaxParts) SceneDb.PartRefObj[part] = child;
        RefChildren++;
    }

    /// <summary>A chunk emitter's spawn: one object of <paramref name="model"/> in
    /// <paramref name="scene"/>, placed by <paramref name="world"/>. The same path as
    /// <c>NewCAurObject</c> plus the attach. Its lifetime is its particle's: <c>EmitterSim</c> places it every
    /// frame, takes it out of the scene when the particle dies and frees it with the particle record.</summary>
    private static DbId SpawnChunk(DbId scene, string model, Matrix4x4 world)
    {
        nint name = Marshal.StringToCoTaskMemUTF8(model);
        nint shell;
        try { shell = NewCAurObjectManaged(name); }
        finally { Marshal.FreeCoTaskMem(name); }
        if (shell == 0) return default;
        DbId id = Facade.Object(shell, SeamSlot.Unnamed);
        if (id.IsNull) return default;
        ObjectSlots.JoinScene(id, shell, scene,
                              SceneDb.Scenes.Valid(scene) ? SceneDb.SceneFacade[scene.Index] : 0);
        Matrix4x4.Decompose(world, out Vector3 s, out Quaternion q, out Vector3 t);
        Facade.SetXform(id, shell, t, q);
        SceneDb.ObjScale[id.Index] = s.X;
        SceneDb.Touch(id, Dirty.Tree | Dirty.Xform);
        return id;
    }

    /// <summary>A live chunk follows its particle.</summary>
    private static void PlaceChunk(DbId id, Vector3 pos, Quaternion rot)
    {
        if (SceneDb.Objects.Valid(id)) Facade.SetXform(id, Facade.ObjectShell(id), pos, rot);
    }

    /// <summary>A chunk joins <paramref name="scene"/> on its particle's birth and leaves on its death.</summary>
    private static void ChunkJoin(DbId id, DbId scene)
    {
        if (!SceneDb.Objects.Valid(id)) return;
        ObjectSlots.JoinScene(id, Facade.ObjectShell(id), scene,
                              SceneDb.Scenes.Valid(scene) ? SceneDb.SceneFacade[scene.Index] : 0);
    }

    /// <summary>The factory body, callable from managed code (the chunk spawn) as well as from the hook.</summary>
    private static nint NewCAurObjectManaged(nint resref) => NewCAurObjectBody(resref, 0, 0);

    /// <summary>Reference-node children created, and owned children (reference + appended) destroyed with
    /// their parent. The two must converge as scenes tear down; a gap is the leak this pair exists to show.</summary>
    public static long RefChildren { get; internal set; }
    public static long OwnedFreed { get; internal set; }

    /// <summary>Objects minted with no model because CX could not read one whose resource IS there. The
    /// self-check drives one on purpose and puts the count back, so a live session's line starts at zero.</summary>
    internal static long UnreadableModels { get => _unreadableModels; set => _unreadableModels = value; }

    /// <inheritdoc cref="UnreadableModels"/>
    internal static long MissingModels { get => _missingModels; set => _missingModels = value; }

    /// <summary>Models the factory was asked for and could not find, the ones whose resource was there and CX
    /// could not read (minted with no model -- each one is a model the engine would have loaded), and streams
    /// it had to refuse.</summary>
    public static string Stats()
        => $"newobj: missing-model={_missingModels} unreadable-model={_unreadableModels} "
         + $"stream-refused={_streamRefusals} ref-children={RefChildren} owned-freed={OwnedFreed}";
}
