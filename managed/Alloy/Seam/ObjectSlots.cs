using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Engine;

namespace CX.Alloy.Seam;

/// <summary>The engine's <c>Vector</c> as it is laid out and passed: three floats. SysV classifies both
/// eightbytes SSE, so it arrives in xmm0:xmm1 with no hidden buffer; Win64 passes anything over 8 bytes by
/// reference. Declaring it by value is correct on both -- NativeAOT lowers it per platform -- which is why only
/// by-value RETURNS need a per-OS declaration.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct EVec
{
    public float X, Y, Z;
    public Vector3 V => new(X, Y, Z);
    public static EVec From(Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };
}

/// <summary>The engine's <c>Quaternion</c>, <b>w first</b>: <c>Gob::Gob</c> @0x440a00 initialises the
/// orientation at +0xb4 with the eightbyte <c>0x3f80000000000000</c> written at +0xb0, i.e. 1.0f lands at
/// +0xb4, the FIRST float of the quaternion -- and <c>Part::GetWorld</c> @0x437930 seeds its out-param the same
/// way. An identity quaternion with 1 in slot 0 is (w, x, y, z).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct EQuat
{
    public float W, X, Y, Z;
    public Quaternion Q => new(X, Y, Z, W);
    public static EQuat From(Quaternion q) => new() { W = q.W, X = q.X, Y = q.Y, Z = q.Z };
    public static EQuat Identity => new() { W = 1f };
}

/// <summary>The <c>CAurObject</c> interface (80 slots) and the <c>Gob</c>-only slots the game reaches (14),
/// over <see cref="SceneDb"/>: one <c>[UnmanagedCallersOnly]</c> static per slot, declared at the LINUX index
/// and placed at the Windows one by <see cref="SeamVtables"/>. A body resolves the handle (retired or alien
/// poisons), works the columns, mirrors the shell word and marks a <see cref="Dirty"/> bit; never allocates.</summary>
public sealed unsafe partial class ObjectSlots : ISeamProvider
{
    /// <summary>The three ±10000 seeds <c>Gob::GetMinimumBoundingBox()</c> @0x436f60 writes before it asks the
    /// part tree, so an object with no geometry answers an inverted box rather than zeros.</summary>
    private const float BoundsSeed = 10000f;

    public void Register(SeamRegistry r)
    {
        // lifecycle and identity
        r.Slot(SeamTable.Gob, 0, (void*)(delegate* unmanaged<nint, void>)&Destruct);
        r.Slot(SeamTable.Gob, 1, (void*)(delegate* unmanaged<nint, void>)&DestructDelete);
        r.Slot(SeamTable.Gob, 2, (void*)(delegate* unmanaged<nint, nint>)&AsCAurCamera);
        r.Slot(SeamTable.Gob, 3, (void*)(delegate* unmanaged<nint, nint>)&GetName);
        r.Slot(SeamTable.Gob, 4, (void*)(delegate* unmanaged<nint, nint>)&GetModelName);
        r.Slot(SeamTable.Gob, 5, (void*)(delegate* unmanaged<nint, byte, void>)&Render);
        r.Slot(SeamTable.Gob, 80, (void*)(delegate* unmanaged<nint, nint>)&AsCamera);
        r.Slot(SeamTable.Gob, 81, (void*)(delegate* unmanaged<nint, nint>)&AsCAurObject);
        r.Slot(SeamTable.Gob, 82, (void*)(delegate* unmanaged<nint, nint>)&AsString);

        // animation
        r.Slot(SeamTable.Gob, 6, (void*)(delegate* unmanaged<nint, float, void>)&Animate);
        r.Slot(SeamTable.Gob, 7, (void*)(delegate* unmanaged<nint, nint, float, int, float, int>)&PlayAnimation);
        r.Slot(SeamTable.Gob, 8, (void*)(delegate* unmanaged<nint, nint, float*, float*, nint>)&GetAnimation);
        r.Slot(SeamTable.Gob, 9, (void*)(delegate* unmanaged<nint, nint, int>)&LoadAddInAnimations);
        r.Slot(SeamTable.Gob, 10, (void*)(delegate* unmanaged<nint, void>)&RemoveAddInAnimations);
        r.Slot(SeamTable.Gob, 57, (void*)(delegate* unmanaged<nint, void>)&EnableForceAnimations);
        r.Slot(SeamTable.Gob, 58, (void*)(delegate* unmanaged<nint, void>)&DisableForceAnimations);
        r.Slot(SeamTable.Gob, 59, (void*)(delegate* unmanaged<nint, void>)&EnableAnimations);
        r.Slot(SeamTable.Gob, 60, (void*)(delegate* unmanaged<nint, void>)&DisableAnimations);
        r.Slot(SeamTable.Gob, 74, (void*)(delegate* unmanaged<nint, nint, nint, nint, float, float, float, float, void>)&BeginLookAtAnimate);
        r.Slot(SeamTable.Gob, 75, (void*)(delegate* unmanaged<nint, void>)&EndLookAtAnimate);
        r.Slot(SeamTable.Gob, 77, (void*)(delegate* unmanaged<nint, byte>)&IsAnimating);
        r.Slot(SeamTable.Gob, 78, (void*)(delegate* unmanaged<nint, byte, void>)&SetSynchronizeAnimations);
        r.Slot(SeamTable.Gob, 85, (void*)(delegate* unmanaged<nint, byte>)&IsForceAnimated);
        r.Slot(SeamTable.Gob, 11, (void*)(delegate* unmanaged<nint, nint, nint, nint, nint>)&RegisterCallBack);
        r.Slot(SeamTable.Gob, 83, (void*)(delegate* unmanaged<nint, nint, void>)&DeregisterCallback);
        r.Slot(SeamTable.Gob, 84, (void*)(delegate* unmanaged<nint, nint, nint, void>)&DoEvent);

        // textures and materials
        r.Slot(SeamTable.Gob, 12, (void*)(delegate* unmanaged<nint, nint, nint, int>)&ReplaceTexture2);
        r.Slot(SeamTable.Gob, 13, (void*)(delegate* unmanaged<nint, nint, nint, int, nint, int, int>)&ReplaceTexture5);
        r.Slot(SeamTable.Gob, 14, (void*)(delegate* unmanaged<nint, nint, nint, int, nint, int>)&ReplaceTextureSubtree);
        r.Slot(SeamTable.Gob, 15, (void*)(delegate* unmanaged<nint, int>)&RestoreTexture);
        r.Slot(SeamTable.Gob, 16, (void*)(delegate* unmanaged<nint, byte, void>)&SetOverrideTextureFilter);
        r.Slot(SeamTable.Gob, 17, (void*)(delegate* unmanaged<nint, nint, nint, int>)&SetTextureAttributes);
        r.Slot(SeamTable.Gob, 18, (void*)(delegate* unmanaged<nint, nint, int>)&SetEnvironmentMap);
        r.Slot(SeamTable.Gob, 30, (void*)(delegate* unmanaged<nint, nint, nint, nint, void>)&SetMaterialParam);

        // attachment, scene, behaviour, tree
        r.Slot(SeamTable.Gob, 19, (void*)(delegate* unmanaged<nint, nint, int>)&AttachToScene);
        r.Slot(SeamTable.Gob, 20, (void*)(delegate* unmanaged<nint, nint, nint, int, nint>)&AttachToObject);
        r.Slot(SeamTable.Gob, 21, (void*)(delegate* unmanaged<nint, nint, nint, int, nint>)&AttachToPart);
        r.Slot(SeamTable.Gob, 22, (void*)(delegate* unmanaged<nint, nint, void>)&SetBehavior);
        r.Slot(SeamTable.Gob, 43, (void*)(delegate* unmanaged<nint, nint, nint, void>)&ReattachReference);
        r.Slot(SeamTable.Gob, 44, (void*)(delegate* unmanaged<nint, nint, void>)&BehaviorMessage);
        r.Slot(SeamTable.Gob, 45, (void*)(delegate* unmanaged<nint, nint, nint, int>)&Append);
        r.Slot(SeamTable.Gob, 46, (void*)(delegate* unmanaged<nint, nint, int>)&Amputate);
        r.Slot(SeamTable.Gob, 47, (void*)(delegate* unmanaged<nint, nint, int, byte>)&SetSubtreeEnabledSlot);
        r.Slot(SeamTable.Gob, 68, (void*)(delegate* unmanaged<nint, nint>)&GetScene);
        r.Slot(SeamTable.Gob, 69, (void*)(delegate* unmanaged<nint, nint, void>)&GetAttachedGobs);
        r.Slot(SeamTable.Gob, 70, (void*)(delegate* unmanaged<nint, int, nint>)&GetBehavior);

        // external data
        r.Slot(SeamTable.Gob, 31, (void*)(delegate* unmanaged<nint, nint>)&GetExternalData);
        r.Slot(SeamTable.Gob, 32, (void*)(delegate* unmanaged<nint, nint, void>)&SetExternalData);
        r.Slot(SeamTable.Gob, 33, (void*)(delegate* unmanaged<nint, int>)&GetExternalDataType);
        r.Slot(SeamTable.Gob, 34, (void*)(delegate* unmanaged<nint, int, void>)&SetExternalDataType);
        r.Slot(SeamTable.Gob, 35, (void*)(delegate* unmanaged<nint, nint, void>)&InheritExternal);

        RegisterXform(r);
        RegisterVisual(r);
    }

    // ---- the one resolution helper every slot opens with ----

    /// <summary><paramref name="shell"/> is the <c>Gob</c>-shaped block, not the facade base: a camera's
    /// <c>Gob</c> words are its <c>+8</c> sub-object's. <see cref="Facade.GobOf"/> resolves both halves
    /// together, which is the only way the camera case can be right in both.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Live(nint self, int logical, out DbId id, out nint shell)
        => Facade.GobOf(self, logical, out id, out shell);

    /// <summary>A NUL-terminated engine string as a managed one, or null. Cold paths only -- it allocates.</summary>
    private static string? Str(nint p) => p == 0 ? null : Marshal.PtrToStringUTF8(p);

    // ---- lifecycle ----

    /// <summary>The complete-object destructor (Linux slot 0). Everything the object owns goes now: its Part
    /// shells, its callbacks, its model reference, its part range and its record. The shell itself is retired
    /// and quarantined -- a later call through it is a named line, not a jump through freed memory.</summary>
    [UnmanagedCallersOnly]
    private static void Destruct(nint self) => Destroy(self, freeing: false);

    /// <summary>The DELETING destructor (Linux slot 1, and the one Windows destructor slot after the pair
    /// collapses): the half that frees. <c>Gob::~Gob</c> @0x4443c0 is the body plus
    /// <c>operator delete(this, 0x4c0)</c>, @0x444140 the body alone -- and a caller that invokes the
    /// complete-object destructor (slot 0) expects the storage to SURVIVE, so retiring there poisons a live shell.</summary>
    [UnmanagedCallersOnly]
    private static void DestructDelete(nint self) => Destroy(self, freeing: true);

    private static void Destroy(nint self) => Destroy(self, freeing: true);

    /// <summary>Run the destructor body once, and free the storage only when <paramref name="freeing"/> --
    /// the C++ contract the two slots encode. The body is idempotent: a complete-object destructor followed
    /// by a deleting one (which is what <c>delete</c> compiles to for some callers) must not release the
    /// record twice.</summary>
    private static void Destroy(nint self, bool freeing)
    {
        nint shell = Facade.GobBlock(self);
        // The base half of a destructor chain: the derived destructor already retired the block and the
        // engine's delete is now running this one. Expected, counted, not a fault -- the same reasoning as
        // Facade.Retire's redundant path, and without it every text and camera delete logs a use-after-free.
        if (shell != 0 && SeamShell.Known(shell, out byte st, out _) && st != SeamShell.StateLive)
        {
            Facade.NoteBaseDestructor();
            return;
        }
        if (shell == 0) { Facade.Retire(self); return; }
        DbId id = Facade.Object(self, 0);
        if (!id.IsNull)
        {
            // Gob::~Gob @0x544140's SetScene(this, NULL): the move fires UpdateScenes and every attached child
            // follows its parent out (UpdateScenesAttach). Without it a destroyed tab's label stayed in the
            // panel's scene at its last place -- the "Buddy List" floating over the server list.
            if (!SceneDb.ObjScene[id.Index].IsNull) PropagateScene(id, shell, default, 0);
            ReleaseRecord(id);
            SceneDb.ObjFacade[id.Index] = 0;
            SceneDb.Objects.Free(id);
            FreeShellLists(shell);
        }
        // Only the MOST DERIVED destructor retires the block, and only the DELETING one frees it: a text
        // shell's CAurString half and a camera shell's CAurCamera half each own their own destructor slot,
        // and a complete-object destructor leaves the storage alive for its caller. Retiring on either of
        // those poisons both vptrs of a shell the game goes on using.
        if (freeing && (FacadeKind)SeamShell.Head(shell)->Kind == FacadeKind.Object) Facade.Retire(self);
    }

    /// <summary>The scene this object was in has been destroyed. The engine's own teardown
    /// (<c>Scene::~Scene</c> @0x472e40) is a loop of <c>Gob::SetScene(child, NULL)</c>, so this DETACHES: the
    /// object keeps its record, its parts and its facade, and the game deletes it when it is ready. The shell's
    /// +0x88 word goes to 0 with it, which is what the engine's detach leaves for any raw reader of that word.</summary>
    internal static void LeaveDeadScene(DbId id)
    {
        if (!SceneDb.Objects.Valid(id)) return;
        SceneDb.ObjScene[id.Index] = default;
        nint shell = Facade.ObjectShell(id);
        if (shell != 0) Facade.SetSceneWord(shell, 0);
        SceneDb.Touch(id, Dirty.Tree);
    }

    /// <summary>Destroy an object by HANDLE -- the form a scene destroy uses for each of its members
    /// (<c>ObjectBridge.ObjectDestroy</c>), where there is no facade pointer to start from. The facade, if the
    /// game still holds one, is retired with it: a scene that dies takes its objects, and ADR 0028 names that
    /// edge the scene's.</summary>
    internal static void DestroyRecord(DbId id)
    {
        nint shell = Facade.ObjectShell(id);
        if (shell != 0) { Destroy(shell); return; }
        ReleaseRecord(id);
        SceneDb.ObjFacade[id.Index] = 0;
        SceneDb.Objects.Free(id);
    }

    /// <summary>Everything the record owns, released: its Part shells, its part range and the model
    /// references the factory took. PartTree owns the part index space, so freeing a range is ITS call and
    /// never a <c>SceneDb.Parts</c> free of ours; the model reference is the factory's, so releasing it is.</summary>
    private static void ReleaseRecord(DbId id)
    {
        int i = id.Index;
        // SYNCHRONOUSLY, before anything else: the scene's member list, its draw order, its highlight set,
        // its shadow gob and its skybox slots can each name this object, and the count word at the scene
        // shell is read by game code without a vtable -- so the leave cannot wait for a frame boundary.
        DbScene.ObjectDied(id);
        // What it owns dies with it, as the engine's one tree did (Gob::~Gob, PartReference::~PartReference):
        // nothing else holds an appended piece or a reference instance, so left alone they lived for the session.
        for (DbId c = SceneDb.ObjSeamAppendHead[i]; SceneDb.Objects.Valid(c); )
        {
            DbId next = SceneDb.ObjSeamAppendNext[c.Index];
            DestroyRecord(c);
            c = next;
        }
        SceneDb.ObjSeamAppendHead[i] = default;
        DbAnim.ObjectRetired(id);
        ObjectTex.Free(id);
        TexSeam.ObjectDied(id);   // CX-117: the PLT colour rows and any material-param override
        PartShell.FreeAll(id);
        PartTree.Free(id);
        ModelDef? m = SceneDb.ObjModel[i];
        if (m != null) ModelCache.Release(m);
        ModelDef? l1 = SceneDb.ObjSeamLodModel1[i], l2 = SceneDb.ObjSeamLodModel2[i];
        if (l1 != null) ModelCache.Release(l1);
        if (l2 != null) ModelCache.Release(l2);
        SceneDb.ObjModel[i] = null;
        SceneDb.ObjSeamLodModel1[i] = null;
        SceneDb.ObjSeamLodModel2[i] = null;
        SceneDb.ObjSeamModelName[i] = null;
        SceneDb.ObjScene[i] = default;
        SceneDb.SetAttachTo(i, default);
        SceneDb.ObjSeamFlags[i] = 0;
    }

    /// <summary>Free the two heap blocks a shell owns -- its callback list and its attach list. Every kind of
    /// shell has them, so a TEXT object's destroy (which runs through the record rather than through
    /// <see cref="Destroy"/>) calls this too; without it a string's callback blocks leak.</summary>
    internal static void FreeShellLists(nint shell)
    {
        if (shell == 0) return;
        CallbackList.FreeAll(shell);
        FreeEngineList(shell + SeamLayout.GobAttachList);
    }

    /// <summary>Release a <c>CExoArrayList</c> the ENGINE grew inside our shell (the attachment list at +0x4a8,
    /// which <c>Gob::AddAttachment</c> @0x5424a0 reallocs in place). The array belongs to whichever heap grew
    /// it, so the rule is the SafePointer list's: free it on Linux, count it on Windows, where nwmain statically
    /// links its CRT and exports no <c>free</c>.</summary>
    private static void FreeEngineList(nint list)
    {
        nint arr = *(nint*)list;
        *(nint*)list = 0;
        *(int*)(list + 8) = 0;
        *(int*)(list + 12) = 0;
        if (arr == 0) return;
        if (OperatingSystem.IsWindows()) SeamShell.NoteForeignArray();
        else NativeMemory.Free((void*)arr);
    }

    // ---- identity ----

    /// <summary><c>Gob::AsCAurCamera</c> @0x444620 returns 0 for a plain object; a camera's own table
    /// overrides the slot (<see cref="SeamTable.CameraGob"/>), so this body is only ever a plain object's.</summary>
    [UnmanagedCallersOnly]
    private static nint AsCAurCamera(nint self) => 0;

    /// <summary><c>Gob::AsCamera</c> @0x444630: 0 for a plain object.</summary>
    [UnmanagedCallersOnly]
    private static nint AsCamera(nint self) => 0;

    /// <summary><c>Gob::AsCAurObject</c> @0x444640: the object itself. A camera's override answers its +8
    /// sub-object, which is what the shell's own pointer arithmetic already gives.</summary>
    [UnmanagedCallersOnly]
    private static nint AsCAurObject(nint self) => self;

    /// <summary><c>Gob::AsString</c> @0x444650: 0 unless the object is a text object, in which case the
    /// <c>StringGob</c> IS the shell and the engine's answer is <c>this</c>.</summary>
    [UnmanagedCallersOnly]
    private static nint AsString(nint self)
    {
        nint shell = Facade.GobBlock(self);
        if (shell == 0) return 0;
        return SeamShell.Known(shell, out _, out DbId id) && SceneDb.Objects.Valid(id)
               && SceneDb.ObjKindOf[id.Index] == ObjKind.Text ? shell : 0;
    }

    /// <summary><c>Gob::GetName</c> @0x444660 is <c>return this + 0x24</c> -- the ADDRESS of the shell's inline
    /// name array, which is why the name is storage in the shell and not a managed string.</summary>
    [UnmanagedCallersOnly]
    private static nint GetName(nint self)
    {
        nint shell = Facade.GobBlock(self);
        return shell == 0 ? 0 : shell + SeamLayout.GobName;
    }

    /// <summary><c>Gob::GetModelName</c> @0x437270 returns <c>model + 0x10</c>, the name inside the MODEL, so
    /// the pointer must outlive the call and be stable. The seam interns one native copy per resref; a text or
    /// empty object has no model and answers 0, exactly as the engine does for a null <c>Gob+0x68</c>.</summary>
    [UnmanagedCallersOnly]
    private static nint GetModelName(nint self)
    {
        if (!Live(self, 4, out DbId id, out _)) return 0;
        string? name = SceneDb.ObjSeamModelName[id.Index];
        return NativeNames.Of(name);
    }

    /// <summary><c>Gob::Render(bool)</c> @0x452260. Nothing draws from a per-object call: Alloy draws the
    /// published snapshot (ADR 0028 section 5), so a direct call is a no-op and is counted. The game layer
    /// never calls it (the engine's scene does), which is what the count is there to prove.</summary>
    [UnmanagedCallersOnly]
    private static void Render(nint self, byte shadowPass)
    {
        DirectRenderCalls++;
        if (DirectRenderCalls == 1)
            Facade.Log?.Always("seam: Gob::Render was called directly on a facade -- Alloy draws from the frame snapshot, so the call did nothing");
    }

    /// <summary>Direct <c>Gob::Render</c> calls, which nothing can honour.</summary>
    public static long DirectRenderCalls { get; private set; }

    // ---- scene and attachment ----

    /// <summary><c>Gob::AttachToScene(CAurScene*)</c> @0x460c80: join <paramref name="scene"/> (or leave every
    /// scene when it is null) and keep the shell's +0x88 word current, because game code and the engine's own
    /// <c>GobRenderLast</c>/<c>AnimateExternal</c>/<c>Gob::SetScene</c> read it without a vtable. Non-zero on
    /// success, which is what <c>RemoveAuroraObject</c> @0x58b210 ANDs into its own result.</summary>
    [UnmanagedCallersOnly]
    private static int AttachToScene(nint self, nint scene)
    {
        if (!Live(self, 19, out DbId id, out nint shell)) return 0;
        DbId sc = scene == 0 ? default : Facade.Scene(scene, 19);
        if (scene != 0 && sc.IsNull) Facade.NoteOrphaningAttach(id, SceneDb.ObjScene[id.Index], scene);
        // Through the engine's own Gob::SetScene @0x53e020 first: it tells the object's behaviour, and a
        // CAurBehaviorCrossSceneAttach REMEMBERS the scene (+0x70, @0x68c250) -- how CGuiViewport::TakeModel
        // keeps the server list in its viewport when the panel later activates. Its scene slots and the
        // UpdateScenes event land back in the seam; the direct join covers whatever it left undone.
        EngineFn.GobSetScene(shell, scene);
        if (Facade.SceneWord(shell) != scene || SceneDb.ObjScene[id.Index].Value != sc.Value)
            JoinScene(id, shell, sc, scene);
        // UNCONDITIONALLY 1, and the scene word is written whatever happened: Gob::AttachToScene @0x460c80 is
        // two steps -- SetScene(this, scene), then return 1 -- and `CGuiTabSet::FixRenderOrderHack` @0x696fe0
        // calls GetScene then the scene's RenderGobBefore through the RETURNED pointer with no null check. A
        // refusal that left +0x88 at zero was a segfault in the engine's own body, measured at character
        // select; the engine never fails this call, so neither does the seam.
        return 1;
    }

    /// <summary>Move <paramref name="id"/> into <paramref name="sc"/>: leave whatever scene it was in, join the
    /// new one, and mirror the scene pointer at the shell's +0x88. <c>DbScene.Join</c> is what actually makes it
    /// a MEMBER -- the object count word, the draw order, <c>Scene::GetObject</c>, the highlight set and the
    /// scene destroy all read that list, so writing <c>ObjScene</c> alone leaves a scene that holds nothing.</summary>
    internal static void JoinScene(DbId id, nint shell, DbId sc, nint sceneShell)
    {
        if (TileBuild.RefuseMove(id, sc, "JoinScene"))
        {
            DbId now = SceneDb.ObjScene[id.Index];
            // A null record's Index 0 is a live scene's slot; the engine only needs a non-zero word here.
            if (shell != 0) Facade.SetSceneWord(shell, now.IsNull ? sceneShell : SceneDb.SceneFacade[now.Index]);
            return;
        }
        DbId was = SceneDb.ObjScene[id.Index];
        if (!was.IsNull && was.Value != sc.Value) DbScene.Leave(was, id);
        if (!sc.IsNull) { DbScene.Join(sc, id); Flag(id, SeamFlag.EverAttached, true); }
        else SceneDb.ObjScene[id.Index] = default;
        if (shell != 0) Facade.SetSceneWord(shell, sceneShell);
        SceneDb.Touch(id, Dirty.Tree);
        PropagateScene(id, shell, sc, sceneShell);
    }

    /// <summary>Carry a scene change down the attachment tree. The engine did it through the <c>"UpdateScenes"</c>
    /// event each <c>CAurBehaviorAttach</c> registers for; CX builds no behaviour object, so the walk is here, and
    /// it must exist or a control tree attached before its panel was activated never learns the panel's scene. No
    /// depth cap (a button list chains ~40 deep), on an explicit stack, re-entrant: each call works above the top.</summary>
    internal static void PropagateScene(DbId id, nint shell, DbId sc, nint sceneShell)
    {
        int floor = _carryTop, steps = 0;
        Push(id, shell, sc, sceneShell);
        while (_carryTop > floor)
        {
            if (++steps > SceneDb.MaxObjects)
            {
                _carryTop = floor;
                Facade.Log?.Always("seam: a scene change walked more objects than exist -- an attach cycle; the walk is cut");
                break;
            }
            ref Carried f = ref _carry[--_carryTop];
            DbId pid = f.Id; nint pshell = f.Shell; DbId psc = f.Scene; nint pss = f.SceneShell;
            if (pshell == 0) continue;
            for (int k = SceneDb.FirstRider(pid.Index); k != 0; k = SceneDb.NextRider(k - 1))
                if (SceneDb.RiderParent(k - 1).Value == pid.Value && SceneDb.Objects.LiveAt(k - 1))
                {
                    DbId c = SceneDb.Objects.IdAt(k - 1);
                    Carry(pid, c, Facade.ObjectShell(c), psc, pss);
                }
            // The shell list too, for a rider some engine path filed there without the seam's attach.
            nint arr = *(nint*)(pshell + SeamLayout.GobAttachList);
            int n = *(int*)(pshell + SeamLayout.GobAttachCount);
            for (int i = 0; i < n && arr != 0; i++)
            {
                nint child = ((nint*)arr)[i];
                if (child != 0) Carry(pid, Facade.Object(child, 19), child, psc, pss);
            }
            // The APPEND chain as well. Gob::Append's children ride a part rather than the attach list (CX makes
            // each a child object, where the engine grew one part range), so they are not in GobAttachList. A
            // creature is built BEFORE it joins a scene -- the game appends the head, torso, arms and legs and
            // only then places it -- so without this every body part stayed in scene 0 and never drew.
            for (DbId a = SceneDb.ObjSeamAppendHead[pid.Index]; !a.IsNull; )
            {
                if (!SceneDb.Objects.Valid(a)) break;
                DbId next = SceneDb.ObjSeamAppendNext[a.Index];
                if (SceneDb.ObjScene[a.Index].Value != psc.Value)
                {
                    DbId was = SceneDb.ObjScene[a.Index];
                    if (!was.IsNull) DbScene.Leave(was, a);
                    if (!psc.IsNull) { DbScene.Join(psc, a); Flag(a, SeamFlag.EverAttached, true); }
                    else SceneDb.ObjScene[a.Index] = default;
                    nint ashell = Facade.ObjectShell(a);
                    if (ashell != 0) Facade.SetSceneWord(ashell, pss);
                    SceneDb.Touch(a, Dirty.Tree);
                    Push(a, ashell, psc, pss);
                }
                a = next;
            }
        }
    }

    /// <summary>One pending node of <see cref="PropagateScene"/>'s walk.</summary>
    private struct Carried { public DbId Id; public nint Shell; public DbId Scene; public nint SceneShell; }

    /// <summary>The walk's stack, kept across calls so a scene change allocates nothing; grown on overflow.</summary>
    private static Carried[] _carry = new Carried[256];
    private static int _carryTop;

    private static void Push(DbId id, nint shell, DbId sc, nint sceneShell)
    {
        if (_carryTop == _carry.Length) Array.Resize(ref _carry, _carry.Length * 2);
        _carry[_carryTop++] = new Carried { Id = id, Shell = shell, Scene = sc, SceneShell = sceneShell };
    }

    /// <summary>One rider of <see cref="PropagateScene"/>: move <paramref name="c"/> to the scene its parent got
    /// (or the one a cross-scene attach remembers) and queue it to carry on down. A rider already in its target
    /// is not walked again, which is also what ends a cycle.</summary>
    private static void Carry(DbId id, DbId c, nint child, DbId sc, nint sceneShell)
    {
        if (c.IsNull || child == 0 || c.Value == id.Value) return;
        DbId target = sc;
        nint targetShell = sceneShell;
        // CAurBehaviorCrossSceneAttach::SetScene() @0x58b560: a control the game moved to another scene
        // (CGuiViewport::TakeModel puts the server list in its viewport's) returns THERE when its parent
        // gets a scene -- unless its +0x80 latch is set ("Dislodge", "StopSceneChanges"), when it stays put.
        nint cbeh = SceneDb.ObjFacade[c.Index] == 0 ? 0 : *(nint*)(SceneDb.ObjFacade[c.Index] + SeamLayout.GobBehavior);
        bool cross = cbeh != 0 && DbAnim.IsCrossSceneAttach(cbeh);
        if (cross && *(byte*)(cbeh + CX.Framework.EngineLayout.CrossAttachLatch) != 0) return;
        if (!sc.IsNull && cross)
        {
            DbId kept = SceneDb.ObjCrossScene[c.Index];
            if (SceneDb.Scenes.Valid(kept)) { target = kept; targetShell = SceneDb.SceneFacade[kept.Index]; }
        }
        DbId was = SceneDb.ObjScene[c.Index];
        if (was.Value == target.Value) return;
        if (!was.IsNull) DbScene.Leave(was, c);
        if (!target.IsNull) { DbScene.Join(target, c); Flag(c, SeamFlag.EverAttached, true); }
        else SceneDb.ObjScene[c.Index] = default;
        Facade.SetSceneWord(child, targetShell);
        SceneDb.Touch(c, Dirty.Tree);
        Push(c, child, target, targetShell);
    }

    /// <summary><c>Gob::AttachToObject(CAurObject*, char const*, int)</c> @0x43e0e0: ride
    /// <paramref name="parent"/>'s named part, or detach when <paramref name="parent"/> is null. The engine
    /// returns the attachment record it created, which nothing in the game layer dereferences -- it is only
    /// tested against 0 -- so the seam builds none and answers the object's own shell as the non-null token.</summary>
    [UnmanagedCallersOnly]
    private static nint AttachToObject(nint self, nint parent, nint partName, int flags)
    {
        if (!Live(self, 20, out DbId id, out nint shell)) return 0;
        if (parent == 0) { Detach(id); return shell; }
        DbId p = Facade.Object(parent, 20);
        if (p.IsNull) return 0;
        AttachAt(id, shell, p, PartByName(p, (byte*)partName), Mode(flags));
        return shell;
    }

    /// <summary>The attach itself: re-file <paramref name="id"/> as a rider of <paramref name="parent"/>'s
    /// part <paramref name="part"/>. Shared by <c>Gob::AttachToObject</c> and the reference re-point, which
    /// is the engine's own sharing -- both reach <c>CAurBehaviorAttach</c>'s constructor.</summary>
    private static void AttachAt(DbId id, nint shell, DbId parent, int part, byte mode)
    {
        Detach(id);
        SceneDb.SetAttachTo(id.Index, parent);
        SceneDb.SetAttachPart(id.Index, part);
        SceneDb.ObjAttachMode[id.Index] = mode;
        AddAttachment(Facade.ObjectShell(parent), shell);
        InheritScene(id, shell, parent);
        SceneDb.Touch(id, Dirty.Tree | Dirty.Xform);
    }

    /// <summary>The LAST statement of both <c>Gob::AttachToObject</c> @0x43e0e0 and <c>Gob::AttachToPart</c>
    /// @0x43e2a0: <c>SetScene(this, *(Scene**)(parent + 0x88))</c>. Attaching to an object INHERITS its scene,
    /// and that is how the whole GUI control tree gets one -- a panel attaches only its own root model, and
    /// every control below it rides in on this line; without it <c>FixRenderOrderHack</c> @0x696fe0 derefs NULL.</summary>
    private static void InheritScene(DbId id, nint shell, DbId parent)
    {
        nint ps = Facade.ObjectShell(parent);
        JoinScene(id, shell, SceneDb.Objects.Valid(parent) ? SceneDb.ObjScene[parent.Index] : default,
                  ps == 0 ? 0 : Facade.SceneWord(ps));
    }

    /// <summary>The engine's <c>Gob::AddAttachment</c> @0x5424a0, which only ever ran from a
    /// <c>CAurBehaviorAttach</c> constructor: append <paramref name="child"/> to <paramref name="parent"/>'s
    /// <c>CExoArrayList&lt;Gob*&gt;</c> at +0x4a8, deduplicated, and stop at 16 as the engine does. CX builds no
    /// behaviour object, so the seam does it here -- the list is what <c>GetAttachedGobs</c> answers from.</summary>
    private static void AddAttachment(nint parent, nint child)
    {
        if (parent == 0 || child == 0) return;
        nint list = parent + SeamLayout.GobAttachList;
        int n = *(int*)(list + 8), cap = *(int*)(list + 12);
        nint arr = *(nint*)list;
        if (n >= 16) return;                     // the engine stops adding past 16 attachments
        for (int i = 0; i < n; i++) if (((nint*)arr)[i] == child) return;
        if (n == cap)
        {
            int grow = cap == 0 ? 16 : cap * 2;
            void* bigger = NativeMemory.Realloc((void*)arr, (nuint)(grow * sizeof(nint)));
            if (bigger == null) { Facade.Log?.Always("seam: out of memory growing an attachment list"); return; }
            arr = (nint)bigger;
            *(nint*)list = arr;
            *(int*)(list + 12) = grow;
        }
        ((nint*)arr)[n] = child;
        *(int*)(list + 8) = n + 1;
    }

    /// <summary>The engine's <c>Gob::RemoveAttachment</c> @0x53f8f0: close the gap, keep the order.</summary>
    private static void RemoveAttachment(nint parent, nint child)
    {
        if (parent == 0 || child == 0) return;
        nint list = parent + SeamLayout.GobAttachList;
        nint arr = *(nint*)list;
        int n = *(int*)(list + 8);
        if (arr == 0) return;
        for (int i = 0; i < n; i++)
        {
            if (((nint*)arr)[i] != child) continue;
            for (int j = i; j < n - 1; j++) ((nint*)arr)[j] = ((nint*)arr)[j + 1];
            *(int*)(list + 8) = n - 1;
            return;
        }
    }

    /// <summary>The object half of a <c>CAurString</c> attach (that interface's slots 2 and 3, which
    /// <see cref="TextSlots"/> answers for the record): unlink the text from its old parent's attach list and,
    /// once the text record has taken its columns, link it into the new parent's list and inherit the parent's
    /// scene, as the last line of <c>Gob::AttachToObject</c> does for any gob.</summary>
    internal static void UnlinkText(DbId id) => Detach(id);

    /// <inheritdoc cref="UnlinkText"/>
    internal static void LinkText(DbId id, DbId parent)
    {
        nint shell = Facade.ObjectShell(id);
        if (shell == 0 || !SceneDb.Objects.Valid(parent)) return;
        AddAttachment(Facade.ObjectShell(parent), shell);
        InheritScene(id, shell, parent);
    }

    /// <summary>Clear the attachment columns -- what <c>AttachToObject(NULL, "", 0)</c> does, and the first
    /// half of what <c>RemoveAuroraObject</c> @0x58b210 does to let an object go.</summary>
    private static void Detach(DbId id)
    {
        DbId was = SceneDb.ObjAttachTo[id.Index];
        if (SceneDb.Objects.Valid(was))
            RemoveAttachment(Facade.ObjectShell(was), Facade.ObjectShell(id));
        SceneDb.SetAttachTo(id.Index, default);
        SceneDb.SetAttachPart(id.Index, -1);
        SceneDb.ObjAttachMode[id.Index] = 0;
        SceneDb.Touch(id, Dirty.Tree | Dirty.Xform);
    }

    /// <summary>The <c>mode</c> argument of <c>Gob::AttachToObject</c> @0x43e0e0 as the column holds it: its
    /// switch has five arms (0 plain, 1 translate, 2 rotate, 3 no-orientation, 4 GUI-scale) and its default
    /// arm installs a NULL behaviour, which is <see cref="SceneDb.AttachNone"/> here.</summary>
    private static byte Mode(int flags) => (byte)((uint)flags <= 4 ? flags : SceneDb.AttachNone);

    /// <summary><c>Gob::AttachToPart(CAurObject*, CAurPart*, int)</c> @0x43e2a0: the same with the part given
    /// as the <c>CAurPart*</c> a previous <c>GetPart</c> handed out.</summary>
    [UnmanagedCallersOnly]
    private static nint AttachToPart(nint self, nint parent, nint part, int flags)
    {
        if (!Live(self, 21, out DbId id, out nint shell)) return 0;
        if (parent == 0 || part == 0) { Detach(id); return shell; }
        if (!PartShell.Resolve(part, out DbId owner, out int idx)) return 0;
        DbId p = Facade.Object(parent, 21);
        if (p.IsNull || owner.Value != p.Value) return 0;
        Detach(id);
        SceneDb.SetAttachTo(id.Index, p);
        SceneDb.SetAttachPart(id.Index, idx);
        SceneDb.ObjAttachMode[id.Index] = Mode(flags);
        AddAttachment(Facade.ObjectShell(p), shell);
        InheritScene(id, shell, p);
        SceneDb.Touch(id, Dirty.Tree | Dirty.Xform);
        return shell;
    }

    /// <summary><c>Gob::GetScene</c> @0x4447b0 is <c>return *(Scene**)(this+0x88)</c> -- the shell word, not a
    /// column, so a getter and a raw read can never disagree.</summary>
    [UnmanagedCallersOnly]
    private static nint GetScene(nint self)
    {
        nint shell = Facade.GobBlock(self);
        if (shell == 0) { Facade.Object(self, 68); return 0; }
        nint word = Facade.SceneWord(shell);
        if (word != 0) return word;
        // The mirror can lag the membership: an object joins a scene through SEVERAL paths -- its own
        // AttachToScene, the engine's non-virtual Gob::SetScene, and the scene's own AddObject/ChildAdd slots
        // -- and only the first two go through this shell. The column is the truth, so answer from it and
        // repair the word, because the raw readers (GobRenderLast, AnimateExternal, Gob::SetScene) read the
        // word and not the column.
        DbId id = Facade.Object(self, 68);
        if (id.IsNull) return 0;
        DbId sc = SceneDb.ObjScene[id.Index];
        if (SceneDb.Scenes.Valid(sc))
        {
            word = SceneDb.SceneFacade[sc.Index];
            if (word != 0) { Facade.SetSceneWord(shell, word); return word; }
        }
        // Genuinely sceneless. The engine returns NULL here too, but several game-layer callers deref it
        // without a check (CGuiTabSet::FixRenderOrderHack @0x696fe0 calls the returned scene's
        // RenderGobBefore), so a zero here is a crash in the engine's own body and worth naming.
        SceneLessGets++;
        if ((SceneLessGets & (SceneLessGets - 1)) == 0)
            Facade.Log?.Always($"seam: Gob::GetScene on \"{Facade.NameOf(shell)}\" has no scene "
                             + (Has(id, SeamFlag.EverAttached) ? "(it was attached and then detached)" : "(it was NEVER attached)")
                             + $" -- returning NULL as the engine does, but some callers deref it unchecked ({SceneLessGets} so far)");
        return 0;
    }

    /// <summary><c>GetScene</c> calls that had no scene to answer with.</summary>
    public static long SceneLessGets { get; private set; }

    /// <summary><c>Gob::GetAttachedGobs(List&lt;CAurObject*&gt;&amp;)</c> @0x439350: append every gob attached
    /// to this one to the caller's list. The list is <c>{T* array, int count, int capacity}</c> grown with
    /// <c>realloc</c>, so the seam calls the engine's OWN <c>CExoArrayList&lt;CAurObject*&gt;::Add</c> @0x4b3540
    /// rather than reallocating across a CRT boundary. The source is the shell's attach list at +0x4a8.</summary>
    [UnmanagedCallersOnly]
    private static void GetAttachedGobs(nint self, nint list)
    {
        if (list == 0) return;
        nint shell = Facade.GobBlock(self);
        if (shell == 0) { Facade.Object(self, 69); return; }
        if (EngineFn.ListAddCAurObject == null)
        {
            Facade.Log?.Always("seam: CExoArrayList<CAurObject*>::Add did not resolve -- GetAttachedGobs answers an empty list");
            return;
        }
        Gather(shell, list, 0);
    }

    /// <summary>What <c>Gob::GetAttachedGobs</c> does under the one-line entry point: the TRANSITIVE closure
    /// of the attachment tree, duplicate-free (it linear-scans the out-list before each append), with the
    /// object itself left out. The depth guard is the seam's: the engine would recurse for ever on a cycle the
    /// game could build, and a 16-deep attachment chain is already past anything the game makes.</summary>
    private static void Gather(nint shell, nint list, int depth)
    {
        if (depth > 16) return;
        nint arr = *(nint*)(shell + SeamLayout.GobAttachList);
        int n = *(int*)(shell + SeamLayout.GobAttachCount);
        if (arr == 0) return;
        for (int i = 0; i < n; i++)
        {
            nint child = ((nint*)arr)[i];
            if (child == 0) continue;
            if (!Contains(list, child)) EngineFn.ListAddCAurObject(list, child);
            nint cs = Facade.GobBlock(child);
            if (cs != 0) Gather(cs, list, depth + 1);
        }
    }

    /// <summary>Is <paramref name="item"/> already in the engine's <c>{ptr, count, capacity}</c> list?</summary>
    private static bool Contains(nint list, nint item)
    {
        nint arr = *(nint*)list;
        int n = *(int*)(list + 8);
        if (arr == 0) return false;
        for (int i = 0; i < n; i++) if (((nint*)arr)[i] == item) return true;
        return false;
    }

    /// <summary>Record whether the object's model carries a skinned mesh, which is what
    /// <c>Gob::ContainsSkinnedMeshes</c> answers; the engine re-walks the tree on every call, the seam reads a
    /// bit the tree build set.</summary>
    internal static void NoteSkinned(DbId id)
    {
        ModelDef? m = SceneDb.ObjModel[id.Index];
        Flag(id, SeamFlag.Skinned, m != null && SeamBridge.HasSkinnedMesh(m));
    }

    /// <summary><c>Gob::ReattachReference(CAurObject*, char const*)</c> @0x43fc50: walk THIS object's part tree
    /// and re-file every <c>reattachable</c> reference node's instance as a rider of <paramref name="other"/>'s
    /// named part, then turn this object's local +Z onto the line to it; null puts the instance back. The beam
    /// edge: the beam hangs off the caster while its instance rides the victim, read back by <c>SetEmitterTarget</c>.</summary>
    [UnmanagedCallersOnly]
    private static void ReattachReference(nint self, nint other, nint partName)
    {
        if (!Live(self, 43, out DbId id, out nint shell)) return;
        DbId target = other == 0 ? default : Facade.Object(other, 43);
        int first = SceneDb.ObjPartFirst[id.Index], n = SceneDb.ObjPartCount[id.Index];
        for (int p = first; p < first + n; p++)
        {
            if (SceneDb.PartKindOf[p] != PartKind.Reference || !Reattachable(p)) continue;
            DbId inst = SceneDb.PartRefObj[p];
            if (!SceneDb.Objects.Valid(inst)) continue;
            nint ishell = Facade.ObjectShell(inst);
            // The engine's reference rebuild installs no attach behaviour, so the variant goes with it.
            if (target.IsNull) AttachAt(inst, ishell, id, p, 0);
            else AttachAt(inst, ishell, target, PartByName(target, (byte*)partName), 0);
            LookAt(id, inst);
        }
        SceneDb.Touch(id, Dirty.Tree | Dirty.Xform);
    }

    /// <summary>Does part <paramref name="p"/>'s reference node say <c>reattachable 1</c>? The engine's
    /// <c>MdlNodeReference+0xdc</c> test, which is what keeps a decorative reference (a torch's flame model)
    /// from being dragged onto a beam's victim.</summary>
    private static bool Reattachable(int p)
    {
        CX.Alloy.Mdl.ModelDef? m = SceneDb.PartModel[p];
        int node = SceneDb.PartNode[p];
        if (m == null || (uint)node >= (uint)m.Nodes.Length) return false;
        CX.Alloy.Mdl.ReferenceDef? r = m.Nodes[node].Reference;
        return r != null && r.Reattachable;
    }

    /// <summary><c>Gob::ReattachReference</c>'s last statement: <c>rot = RotationArc(localZ, at - pos) * rot</c>,
    /// so a beam model that draws along its own +Z points at where its reference instance went. Both positions
    /// are the engine's <c>Gob::GetPosition</c> reads of <c>Gob+0xa8</c>, which for an ATTACHED gob is the world
    /// position its attach behaviour last wrote, so this reads the composed matrix, not that local column.</summary>
    private static void LookAt(DbId id, DbId at)
    {
        Quaternion q = SceneDb.ObjRot[id.Index];
        Vector3 fwd = new(2f * (q.X * q.Z + q.Y * q.W), 2f * (q.Y * q.Z - q.X * q.W),
                          1f - 2f * (q.X * q.X + q.Y * q.Y));
        Vector3 d = SceneDb.ObjWorld[at.Index].Translation - SceneDb.ObjWorld[id.Index].Translation;
        float l = d.LengthSquared();
        if (l <= 0f) return;
        d *= 1f / MathF.Sqrt(l);
        float dot = Vector3.Dot(fwd, d);
        if (dot >= 1f - 1e-6f) return;
        Quaternion arc;
        if (dot <= -1f + 1e-6f)
        {
            // Antiparallel: the arc is genuinely ambiguous, so take a half-turn about any perpendicular
            // rather than divide by a zero cross product.
            Vector3 a = MathF.Abs(fwd.X) <= MathF.Abs(fwd.Y) && MathF.Abs(fwd.X) <= MathF.Abs(fwd.Z)
                      ? Vector3.UnitX : MathF.Abs(fwd.Y) <= MathF.Abs(fwd.Z) ? Vector3.UnitY : Vector3.UnitZ;
            Vector3 ax = Vector3.Normalize(Vector3.Cross(fwd, a));
            arc = new Quaternion(ax.X, ax.Y, ax.Z, 0f);
        }
        else
        {
            Vector3 c = Vector3.Cross(fwd, d);
            float s = MathF.Sqrt(2f * (1f + dot));
            arc = new Quaternion(c.X / s, c.Y / s, c.Z / s, s * 0.5f);
        }
        SceneDb.ObjRot[id.Index] = arc * q;
        SceneDb.Touch(id, Dirty.Xform);
    }

    // ---- external data ----

    /// <summary><c>Gob::GetExternalData</c> @0x444700 = <c>*(void**)(this+0x490)</c>. The game's
    /// devirtualisation guard in <c>CGuiMan::GuiObjectHitCheck</c> reads the same word inline when the slot
    /// holds the engine's own body, so the word and the getter must agree -- they are the same word.</summary>
    [UnmanagedCallersOnly]
    private static nint GetExternalData(nint self)
    {
        nint shell = Facade.GobBlock(self);
        return shell == 0 ? 0 : *(nint*)(shell + SeamLayout.GobExternal);
    }

    [UnmanagedCallersOnly]
    private static void SetExternalData(nint self, nint data)
    {
        nint shell = Facade.GobBlock(self);
        if (shell != 0) *(nint*)(shell + SeamLayout.GobExternal) = data;
    }

    /// <summary><c>Gob::GetExternalDataType</c> @0x444720 = <c>*(int*)(this+0x498)</c>.</summary>
    [UnmanagedCallersOnly]
    private static int GetExternalDataType(nint self)
    {
        nint shell = Facade.GobBlock(self);
        return shell == 0 ? 0 : *(int*)(shell + SeamLayout.GobExternalType);
    }

    [UnmanagedCallersOnly]
    private static void SetExternalDataType(nint self, int type)
    {
        nint shell = Facade.GobBlock(self);
        if (shell == 0) return;
        *(int*)(shell + SeamLayout.GobExternalType) = type;
        NoteKind(self, type);
    }

    /// <summary>The game has said what kind of object this is, so the record can carry it: the engine answers
    /// "is this a creature?" by reading <c>Gob+0x498 == 5</c> and walking the attachment chain, and CX answers
    /// it from <see cref="ObjFlag.Creature"/> instead -- one flags read where the engine walked up to eight
    /// hops, and the flag is what gives a creature record a creature's receiver class.</summary>
    private static void NoteKind(nint self, int type)
    {
        DbId id = Facade.Object(self, 44);
        if (id.IsNull) return;
        bool creature = type == CX.Framework.EngineLayout.GobKindCreature;
        ObjFlag was = SceneDb.ObjFlags[id.Index];
        ObjFlag now = creature ? was | ObjFlag.Creature : was & ~ObjFlag.Creature;
        if (now == was) return;
        SceneDb.ObjFlags[id.Index] = now;
        SceneDb.Touch(id, Dirty.Material);
    }

    /// <summary><c>Gob::InheritExternal</c> @0x437290 copies +0x490 and +0x498 from the other object, in that
    /// order and nothing else.</summary>
    [UnmanagedCallersOnly]
    private static void InheritExternal(nint self, nint other)
    {
        nint a = Facade.GobBlock(self), b = Facade.GobBlock(other);
        if (a == 0 || b == 0) return;
        *(nint*)(a + SeamLayout.GobExternal) = *(nint*)(b + SeamLayout.GobExternal);
        *(int*)(a + SeamLayout.GobExternalType) = *(int*)(b + SeamLayout.GobExternalType);
        NoteKind(self, *(int*)(a + SeamLayout.GobExternalType));
    }

    // ---- textures and materials: the seam only resolves and marks ----

    [UnmanagedCallersOnly]
    private static int ReplaceTexture2(nint self, nint from, nint to)
    {
        if (!Live(self, 12, out DbId id, out _)) return 0;
        return ObjectTex.Replace(id, Str(from), Str(to));
    }

    /// <summary><c>Gob::ReplaceTexture(from, to, layers, unsigned short* rows, subtree)</c> @0x43eb30. The
    /// <c>ushort*</c> is the PLT LAYER ROW TABLE -- the skin/hair/armour tints of a composite creature. The
    /// engine routes it through <c>Material::ReplaceTexturePLT</c>: only a PLT named <paramref name="to"/> is
    /// taken (a same-named TGA never), and with none nothing changes and the engine's 1 comes back.</summary>
    [UnmanagedCallersOnly]
    private static int ReplaceTexture5(nint self, nint from, nint to, int layers, nint rows, int subtree)
    {
        if (!Live(self, 13, out DbId id, out _)) return 0;
        string? name = Str(to);
        if (string.IsNullOrEmpty(name) || !TexRegistry.PltExists(name)) return 1;
        Span<int> got = stackalloc int[SceneDb.PltLayers];
        int n = PltRows(id, layers, rows, got);
        return ObjectTex.Replace(id, Str(from), name, 0, subtree != 0, got[..n], TexKind.Plt);
    }

    /// <summary>Copy the engine's layer rows onto the object's PLT columns (<c>PltColors.SetRows</c>). A row is
    /// the master-palette row for that layer, and the engine's own bound is 258 entries. The object's rows are
    /// only the fallback a part with none of its own reads; the rows that colour a swapped part are pinned on
    /// it by <c>ObjectTex.Replace</c>, from <paramref name="dst"/>. Returns how many rows it holds.</summary>
    private static int PltRows(DbId id, int layers, nint rows, Span<int> dst)
    {
        if (rows == 0 || layers <= 0) return 0;
        int n = Math.Min(layers, SceneDb.PltLayers);
        for (int i = 0; i < n; i++) dst[i] = ((ushort*)rows)[i];
        PltRowsOn(id, dst[..n], 0);
        if (layers > SceneDb.PltLayers)
        {
            PltRowsClamped++;
            if ((PltRowsClamped & (PltRowsClamped - 1)) == 0)
                Facade.Log?.Always($"seam: ReplaceTexture was given {layers} PLT layer rows and the database holds "
                                 + $"{SceneDb.PltLayers} -- the rest are dropped ({PltRowsClamped} so far)");
        }
        return n;
    }

    /// <summary>The rows onto one object and every piece <c>Gob::Append</c> grafted onto it. A creature's
    /// torso, arms and legs are child objects in CX where the engine had one part tree, so rows written only
    /// to the base left every appended piece on the master palette's first row -- the uncoloured default
    /// body, under the right armour model.</summary>
    private static void PltRowsOn(DbId id, ReadOnlySpan<int> rows, int depth)
    {
        if (depth > 8 || !SceneDb.Objects.Valid(id)) return;   // not-an-offset: the composition's cycle guard
        PltColors.SetRows(id, rows);
        SceneDb.Touch(id, Dirty.Material);
        for (DbId c = SceneDb.ObjSeamAppendHead[id.Index]; !c.IsNull; c = SceneDb.ObjSeamAppendNext[c.Index])
        {
            if (!SceneDb.Objects.Valid(c)) break;
            PltRowsOn(c, rows, depth + 1);
        }
    }

    /// <summary>Calls whose PLT layer count exceeded the database's.</summary>
    public static long PltRowsClamped { get; private set; }

    [UnmanagedCallersOnly]
    private static int ReplaceTextureSubtree(nint self, nint from, nint to, int layers, nint rows)
    {
        if (!Live(self, 14, out DbId id, out _)) return 0;
        string? name = Str(to);
        if (string.IsNullOrEmpty(name) || !TexRegistry.PltExists(name)) return 1;
        Span<int> got = stackalloc int[SceneDb.PltLayers];
        int n = PltRows(id, layers, rows, got);
        return ObjectTex.Replace(id, Str(from), name, 0, subtree: true, got[..n], TexKind.Plt);
    }

    [UnmanagedCallersOnly]
    private static int RestoreTexture(nint self)
    {
        if (!Live(self, 15, out DbId id, out _)) return 0;
        return ObjectTex.Restore(id);
    }

    [UnmanagedCallersOnly]
    private static void SetOverrideTextureFilter(nint self, byte on)
    {
        if (!Live(self, 16, out DbId id, out _)) return;
        Flag(id, SeamFlag.TexFilterOverride, on != 0);
        SeamBridge.SetTextureFilterOverride(id, on != 0);
        SceneDb.Touch(id, Dirty.Texture);
    }

    [UnmanagedCallersOnly]
    private static int SetTextureAttributes(nint self, nint tex, nint attrs)
    {
        if (!Live(self, 17, out DbId id, out _)) return 0;
        int ok = SeamBridge.SetTextureAttributes(id, Str(tex), Str(attrs));
        if (ok != 0) SceneDb.Touch(id, Dirty.Texture);
        return ok;
    }

    [UnmanagedCallersOnly]
    private static int SetEnvironmentMap(nint self, nint tex)
    {
        if (!Live(self, 18, out DbId id, out _)) return 0;
        return ObjectTex.SetEnvMap(id, Str(tex));
    }

    [UnmanagedCallersOnly]
    private static void SetMaterialParam(nint self, nint part, nint name, nint value)
    {
        if (!Live(self, 30, out DbId id, out _)) return;
        SeamBridge.SetMaterialParam(id, Str(part), Str(name), Str(value));
        SceneDb.Touch(id, Dirty.Material);
    }

    // ---- flags ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Flag(DbId id, SeamFlag f, bool on)
    {
        if (on) SceneDb.ObjSeamFlags[id.Index] |= (uint)f;
        else SceneDb.ObjSeamFlags[id.Index] &= ~(uint)f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Has(DbId id, SeamFlag f) => (SceneDb.ObjSeamFlags[id.Index] & (uint)f) != 0;
}
