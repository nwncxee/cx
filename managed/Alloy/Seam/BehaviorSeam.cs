using System;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Seam;

public sealed unsafe partial class ObjectSlots
{
    /// <summary><c>Gob::SetBehavior(CAurBehavior*)</c> @0x43ef90, in the engine's exact order: store the new
    /// pointer at +0x450 FIRST, then run the OLD behaviour's deleting destructor (its vtable slot 1), then
    /// recompute the final scale (a behaviour names the parent an object inherits scale from). Storing before
    /// destroying matters: the old destructor calls back into the object and must not find itself installed.</summary>
    [UnmanagedCallersOnly]
    private static void SetBehavior(nint self, nint behavior)
    {
        if (!Live(self, 22, out DbId id, out nint shell)) return;
        nint old = *(nint*)(shell + SeamLayout.GobBehavior);
        *(nint*)(shell + SeamLayout.GobBehavior) = behavior;
        // The matching column write, so the behaviour the game installs actually SOLVES: BehaviorSolvers is
        // not installed, so the game's own Control runs, its Part::GetWorld call is virtual and lands on the
        // seam's Part vtable, and DbAnim solves the engine's classes over columns.
        DbAnim.SetBehavior(id, behavior);
        if (old != 0 && old != behavior) BehaviorSeam.Destroy(old);
        BehaviorSeam.Rescale(id, shell);
        SceneDb.Touch(id, Dirty.Xform);
    }

    /// <summary><c>Gob::GetBehavior(int)</c> @0x4371e0. <c>RemoveAuroraObject</c> calls it with -1, which is
    /// the "whatever you have" form; the engine's own callers pass an index into a behaviour chain the client
    /// never builds more than one link of.</summary>
    [UnmanagedCallersOnly]
    private static nint GetBehavior(nint self, int which)
    {
        nint shell = Facade.GobBlock(self);
        if (shell == 0) { Facade.Object(self, 70); return 0; }
        return *(nint*)(shell + SeamLayout.GobBehavior);
    }

    /// <summary><c>Gob::BehaviorMessage(char const*)</c> @0x437400: forward the message to the installed
    /// behaviour's own <c>Message</c> slot, nothing when there is none. <c>RemoveAuroraObject</c> ("Dislodge")
    /// and <c>CAurObject::DieWithoutParent</c> ("diewithoutparent 1") both reach a GAME-constructed behaviour
    /// through its own vtable, which is why the seam must never interpret the string itself.</summary>
    [UnmanagedCallersOnly]
    private static void BehaviorMessage(nint self, nint message)
    {
        nint shell = Facade.GobBlock(self);
        if (shell == 0) { Facade.Object(self, 44); return; }
        nint beh = *(nint*)(shell + SeamLayout.GobBehavior);
        bool say = Renderer.HitCheck.Boot && _saidMessage < 60 && beh != 0 && DbAnim.IsCrossSceneAttach(beh);
        string before = say ? Said(self, shell, beh) : "";
        BehaviorSeam.Message(beh, message);
        if (!say) return;
        _saidMessage++;
        Facade.Log?.Info($"[dbbeh] Message \"{Str(message)}\" before: {before} | after: {Said(self, shell, beh)}");
    }

    /// <summary><c>alloy.hitcheck=1</c>: cross-scene behaviour messages said ("Dislodge"/"Reattach"), budgeted.</summary>
    private static int _saidMessage;

    /// <summary>One object's scene as the seam and the engine word see it, and its cross-scene behaviour's kept
    /// scene, latch and target's scene word. Diagnostics only -- it allocates.</summary>
    private static string Said(nint self, nint shell, nint beh)
    {
        DbId id = Facade.Object(self, 44);
        string Scene(nint w)
        {
            if (w == 0) return "null";
            DbId s = Facade.Scene(w, 44);
            return s.IsNull ? $"{w:x}=NO SCENE" : $"s{s.Index}g{s.Gen}\"{SceneDb.SceneName[s.Index]}\"";
        }
        nint target = new CAurBehavior(beh).Target.Ptr;
        nint targetShell = target == 0 ? 0 : Facade.GobBlock(target);
        return $"o{id.Index}\"{(id.IsNull ? "-" : SceneDb.ObjModel[id.Index]?.Name ?? "-")}\" "
            + $"db={(id.IsNull || SceneDb.ObjScene[id.Index].IsNull ? "none" : $"s{SceneDb.ObjScene[id.Index].Index}")} "
            + $"word={Scene(Facade.SceneWord(shell))} kept={Scene(new CAurBehaviorCrossSceneAttach(beh).KeptScene)} "
            + $"latch={new CAurBehaviorCrossSceneAttach(beh).Latch} "
            + $"target={(targetShell == 0 ? "null" : Scene(Facade.SceneWord(targetShell)))}";
    }
}

/// <summary>How the seam deals with <c>CAurBehavior</c> objects. They are NOT CX's: the game allocates them,
/// runs the engine's constructors in place, patches the vptr with its own and writes their fields directly, so
/// the seam stores the pointer in the shell at the engine's +0x450 and calls their vtables -- never their
/// fields, never their allocation. 10 measured slots; the destructor pair collapses on Windows.</summary>
public static unsafe class BehaviorSeam
{
    /// <summary>Linux byte offsets in a <c>CAurBehavior</c> vtable. Windows is one pointer earlier for every
    /// slot after the collapsed destructor pair.</summary>
    private static readonly int DeletingDtor = EngineLayout.SeamCAurBehaviorVtblDtorDeleting;
    private static readonly int MessageSlot = EngineLayout.SeamCAurBehaviorVtblMessage;
    private static readonly int GetTypeSlot = EngineLayout.SeamCAurBehaviorVtblGetType;

    /// <summary>The type id <c>CAurBehaviorCrossSceneAttach::GetType</c> @0x58bff0 returns: an INTEGER tag,
    /// not the address of a string -- 0x314d18 is in no section of the image, and the Windows export
    /// <c>?GetType@CAurBehavior@@UEAAHXZ</c> carries the <c>int</c> return Itanium drops.
    /// <c>RemoveAuroraObject</c> @0x58b210 compares against it to pick "let go" over "detach object+scene".</summary>
    public const int CrossSceneAttachType = 0x314d18;   // not-an-offset: an integer type tag, and 0x314d18 is not an address in this image

    /// <summary>The behaviour types the engine's own classes return, for a seam that needs to answer as one:
    /// the base is 0 and the family runs 0x3e9..0x3f1 in declaration order.</summary>
    public const int TypeBase = 0;   // not-an-offset: an integer type tag CAurBehavior::GetType returns
    public const int TypeAttach = 0x3e9;   // not-an-offset: a type tag
    public const int TypeAttachTranslate = 0x3ea;   // not-an-offset: a type tag
    public const int TypeAttachRotate = 0x3eb;   // not-an-offset: a type tag
    public const int TypeNavigate = 0x3ec;   // not-an-offset: a type tag
    public const int TypeCameraFollow = 0x3ed;   // not-an-offset: a type tag
    public const int TypeCameraLooseFollow = 0x3ee;   // not-an-offset: a type tag
    public const int TypeCameraOnAStick = 0x3ef;   // not-an-offset: a type tag
    public const int TypeAttachNoOrientation = 0x3f0;   // not-an-offset: a type tag
    public const int TypeAttachGUIScale = 0x3f1;   // not-an-offset: a type tag

    /// <summary>The behaviour installed on <paramref name="obj"/>, or 0 -- the shell's own +0x450 word, which
    /// is what <c>Gob::GetBehavior(-1)</c> answers.</summary>
    public static nint Installed(DbId obj)
    {
        nint shell = Facade.ObjectShell(obj);
        return shell == 0 ? 0 : *(nint*)(shell + SeamLayout.GobBehavior);
    }

    /// <summary>A behaviour was attached to <paramref name="target"/>. The pairing is the shell's own +0x450
    /// word, which the game also reads raw, so recording it is writing that word -- there is no second table
    /// to keep in step.</summary>
    public static void Attached(nint behavior, DbId target)
    {
        nint shell = Facade.ObjectShell(target);
        if (shell == 0) return;
        *(nint*)(shell + SeamLayout.GobBehavior) = behavior;
    }

    /// <summary>Construct the chase camera's behaviour as <c>Camera::SetFollow</c> @0x5aa500 does:
    /// <c>operator_new(0x90)</c> plus the engine's own constructor, the camera's <c>CAurObject</c> sub-object
    /// (<c>camera + 8</c>) as owner and <paramref name="target"/> as what it follows. The engine's allocator is
    /// required -- <see cref="Destroy"/> calls <c>operator delete</c>; 0 is a loud refusal and the camera is still.</summary>
    public static nint NewCameraFollow(nint cameraShell, nint target)
    {
        if (cameraShell == 0 || target == 0) return 0;
        var alloc = (delegate* unmanaged<nuint, nint>)Native.Sym(SymbolMap.OperatorNew);
        nint ctor = Native.Sym(SymbolMap.CameraFollowCtor);
        if (alloc == null || ctor == 0)
        {
            if (!_saidNoFollow)
            {
                _saidNoFollow = true;
                KnobGroup.Log?.Always("BehaviorSeam: the camera-follow behaviour cannot be constructed ("
                    + (alloc == null ? "operator new" : "CAurBehaviorCameraFollow's constructor")
                    + " did not resolve) -- the chase camera is NOT installed and the world camera will not "
                    + "move. Nothing else places it.");
            }
            return 0;
        }
        nint beh = alloc(CameraFollowSize);
        if (beh == 0) return 0;
        ((delegate* unmanaged<nint, nint, nint, void>)ctor)(beh, cameraShell + SeamLayout.CameraGobSub, target);
        _followsMade++;
        return beh;
    }

    /// <summary><c>Camera::SetFollow</c>'s own <c>operator_new(0x90)</c>.</summary>
    private const nuint CameraFollowSize = 0x90;   // not-an-offset: the allocation size at Camera::SetFollow @0x5aa500
    private static bool _saidNoFollow;
    private static long _followsMade;
    /// <summary>Chase-camera behaviours constructed this session.</summary>
    public static long FollowsMade => _followsMade;

    /// <summary>Call the behaviour's <c>Message(char const*)</c> slot with a native string.</summary>
    public static void Message(nint behavior, byte* msg) => Message(behavior, (nint)msg);

    /// <summary>Call a behaviour's <c>Message</c> slot. Nothing is interpreted here: the string is the game's
    /// protocol with its own behaviour class.</summary>
    public static void Message(nint behavior, nint message)
    {
        if (behavior == 0 || message == 0) return;
        nint vptr = *(nint*)behavior;
        if (vptr == 0) return;
        var fn = (delegate* unmanaged<nint, nint, void>)*(nint*)(vptr + MessageSlot);
        if (fn != null) fn(behavior, message);
    }

    /// <summary>A behaviour's integer type tag.</summary>
    public static int TypeOf(nint behavior)
    {
        if (behavior == 0) return 0;
        nint vptr = *(nint*)behavior;
        if (vptr == 0) return 0;
        var fn = (delegate* unmanaged<nint, int>)*(nint*)(vptr + GetTypeSlot);
        return fn == null ? 0 : fn(behavior);
    }

    /// <summary>Run a behaviour's deleting destructor, which is what <c>Gob::SetBehavior</c> does to the one it
    /// replaces. Itanium's slot 1 and MSVC's single scalar-deleting destructor are the same slot after the
    /// collapse, so one offset answers both.</summary>
    public static void Destroy(nint behavior)
    {
        if (behavior == 0) return;
        nint vptr = *(nint*)behavior;
        if (vptr == 0) return;
        var fn = (delegate* unmanaged<nint, void>)*(nint*)(vptr + DeletingDtor);
        if (fn != null) fn(behavior);
    }

    /// <summary><c>Gob::CalculateFinalScale</c> @0x43eef0: the object's two scale channels times the PARENT's
    /// final scale when a behaviour is installed (the behaviour names the parent), then the same push down
    /// every attachment. The seam folds the parent through the attachment column, not the behaviour's fields,
    /// whose layout is the game's and which ADR 0028 says the seam must not read.</summary>
    public static void Rescale(DbId id, nint shell)
    {
        float* ch = (float*)(shell + SeamLayout.GobScaleChannel);
        float own = ch[0] * ch[1];
        DbId parent = SceneDb.ObjAttachTo[id.Index];
        float inherited = 1f;
        if (SceneDb.Objects.Valid(parent))
        {
            nint p = SceneDb.ObjFacade[parent.Index];
            if (p != 0) inherited = *(float*)(p + SeamLayout.GobScaleFinal);
        }
        float final = own * inherited;
        *(float*)(shell + SeamLayout.GobScaleFinal) = final;
        SceneDb.ObjScale[id.Index] = final;
        SceneDb.Touch(id, Dirty.Xform);
    }
}
