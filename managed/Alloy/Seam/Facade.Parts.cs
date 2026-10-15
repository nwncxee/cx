using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Engine;

namespace CX.Alloy.Seam;

/// <summary>The facade services the scene and camera seam reaches for beyond <see cref="Facade"/> itself.</summary>
public static unsafe class FacadeParts
{
    /// <summary>The object record a camera IS -- its <c>Gob</c> half. An <c>ObjKind.Empty</c> record with no
    /// model: the engine's <c>Camera</c> inherits every <c>Gob</c> word and uses almost none of them, and the
    /// ones it does use are a transform.</summary>
    public static DbId NewCameraObject(string name)
    {
        DbId id = SceneDb.Objects.Alloc();
        if (id.IsNull) { Facade.Log?.Always("seam: the object table is full -- a camera's object half was refused"); return default; }
        int i = id.Index;
        DbFeed.InitObject(i);
        SceneDb.ObjKindOf[i] = ObjKind.Empty;
        SceneDb.ObjModel[i] = null;
        SceneDb.ObjSeamModelName[i] = null;
        SceneDb.ObjPos[i] = Vector3.Zero;
        SceneDb.ObjRot[i] = Quaternion.Identity;
        SceneDb.ObjScale[i] = 1f;
        SceneDb.ObjVisible[i] = false;
        SceneDb.SetAttachPart(i, -1);
        SceneDb.ObjSeamFlags[i] = (uint)SeamFlag.InView;
        SceneDb.Touch(id, Dirty.Created);
        return id;
    }

    /// <summary>The part index behind a <c>CAurPart*</c> the game holds, or -1.</summary>
    public static int PartIndex(nint part) => PartShell.Resolve(part, out _, out int p) ? p : -1;

    /// <summary>The part index of <paramref name="name"/> inside <paramref name="obj"/>, or the object's root
    /// when the name is null or empty -- which is what <c>Gob::GetPart</c> @0x43c630 does for <c>"root"</c>.</summary>
    public static int PartIndexByName(DbId obj, byte* name) => ObjectSlots.PartByName(obj, name);

    /// <summary>The game's own pointer on the object and its type, read from the shell words the game also
    /// reads raw -- so <c>GetGameobjectHit</c>'s vtable path and <c>CGuiMan</c>'s inlined read agree.</summary>
    public static nint ExternalData(DbId obj)
    {
        nint shell = Facade.ObjectShell(obj);
        return shell == 0 ? 0 : new Gob(shell).ExternalData;
    }

    /// <inheritdoc cref="ExternalData"/>
    public static int ExternalDataType(DbId obj)
    {
        nint shell = Facade.ObjectShell(obj);
        return shell == 0 ? 0 : new Gob(shell).ExternalType;
    }
}
