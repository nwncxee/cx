using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;

namespace CX.Alloy.Seam;

public sealed unsafe partial class ObjectSlots
{
    private static void RegisterVisual(SeamRegistry r)
    {
        r.Slot(SeamTable.Gob, 48, (void*)(delegate* unmanaged<nint, EVec, float, int, void>)&SetColorShifting);
        r.Slot(SeamTable.Gob, 49, (void*)(delegate* unmanaged<nint, EVec, int, void>)&SetIllumination);
        r.Slot(SeamTable.Gob, 50, (void*)(delegate* unmanaged<nint, byte, void>)&SetPropagateHighlightEffects);
        r.Slot(SeamTable.Gob, 51, (void*)(delegate* unmanaged<nint, void>)&TurnOffShadows);
        r.Slot(SeamTable.Gob, 52, (void*)(delegate* unmanaged<nint, void>)&TurnOnShadows);
        r.Slot(SeamTable.Gob, 53, (void*)(delegate* unmanaged<nint, int, void>)&SetLightPriority);
        r.Slot(SeamTable.Gob, 54, (void*)(delegate* unmanaged<nint, float>)&GetMaximumLightRadius);
        r.Slot(SeamTable.Gob, 56, (void*)(delegate* unmanaged<nint, byte, float, void>)&SetWind);
        r.Slot(SeamTable.Gob, 61, (void*)(delegate* unmanaged<nint, void>)&DisableFog);
        r.Slot(SeamTable.Gob, 62, (void*)(delegate* unmanaged<nint, void>)&EnableFog);
        r.Slot(SeamTable.Gob, 66, (void*)(delegate* unmanaged<nint, nint, EVec, void>)&ControlPartLightColor);
        r.Slot(SeamTable.Gob, 67, (void*)(delegate* unmanaged<nint, void>)&SetAsCharacter);
        r.Slot(SeamTable.Gob, 73, (void*)(delegate* unmanaged<nint, byte, void>)&SetRenderPersonalSpace);
        r.Slot(SeamTable.Gob, 86, (void*)(delegate* unmanaged<nint, void>)&EnableInViewVolume);
        r.Slot(SeamTable.Gob, 87, (void*)(delegate* unmanaged<nint, void>)&DisableInViewVolume);
        r.Slot(SeamTable.Gob, 88, (void*)(delegate* unmanaged<nint, byte>)&IsInViewVolume);

        r.Slot(SeamTable.Gob, 36, (void*)(delegate* unmanaged<nint, EVec, EVec, EVec*, nint>)&HitCheckHack);
        r.Slot(SeamTable.Gob, 37, (void*)(delegate* unmanaged<nint, nint, EVec, EVec, EVec*, nint, nint>)&HitCheckPart);
        r.Slot(SeamTable.Gob, 38, (void*)(delegate* unmanaged<nint, EVec, EVec, EVec*, nint, nint>)&HitCheck);
    }

    // ---- colour ----

    /// <summary><c>Gob::SetColorShifting(Vector colour, float alpha, int propagate)</c> @0x439040: a 16-bit
    /// enable at +0xf6, the colour at +0xfc, the alpha at +0x108, and -- when <paramref name="propagate"/> is
    /// non-zero -- <c>DoEvent("propagatecolorshift")</c> so attached objects inherit it. The colour multiplies
    /// the material diffuse and the alpha multiplies the vertex alpha, which is why it also decides opacity.</summary>
    [UnmanagedCallersOnly]
    private static void SetColorShifting(nint self, EVec colour, float alpha, int propagate)
    {
        if (!Live(self, 48, out DbId id, out nint shell)) return;
        if (Renderer.HlDiag.V) Facade.Log?.Info($"[hldiag] SetColorShifting o={id.Index} ({colour.X:F2},{colour.Y:F2},{colour.Z:F2}) a={alpha:F2} propagate={propagate}");
        Shift(id, self, shell, colour.V, alpha, illum: false, propagate != 0, 0);
    }

    /// <summary><c>Gob::SetIllumination(Vector colour, int propagate)</c> @0x4390a0: the same shape one offset
    /// over (+0xf8 enable, +0x110 colour), and the draw ADDS it into the ambient terms.</summary>
    [UnmanagedCallersOnly]
    private static void SetIllumination(nint self, EVec colour, int propagate)
    {
        if (!Live(self, 49, out DbId id, out nint shell)) return;
        if (Renderer.HlDiag.V) Facade.Log?.Info($"[hldiag] SetIllumination o={id.Index} ({colour.X:F2},{colour.Y:F2},{colour.Z:F2}) propagate={propagate}");
        Shift(id, self, shell, colour.V, 0f, illum: true, propagate != 0, 0);
    }

    /// <summary>How deep a propagated shift follows riders of riders: a guard against an attach cycle, which the
    /// engine's own recursion would never return from. The compose already names such a cycle (DbAnim's
    /// attach snap), so the cut needs no line of its own.</summary>
    private const int MaxShiftDepth = 256;

    /// <summary>A colour shift (or, with <paramref name="illum"/>, an illumination) on one object and on what
    /// shares or inherits it. A grafted piece is part of the engine's ONE gob, so it always takes it; with
    /// <paramref name="propagate"/>, each seam rider takes it too, as <c>PropagateColorShifting</c> @0x49fd90 /
    /// <c>PropagateIllumination</c> @0x49fe00 give it to the gob a <c>CAurBehaviorAttach</c> carries.</summary>
    internal static void Shift(DbId id, nint self, nint shell, Vector3 colour, float alpha, bool illum, bool propagate, int depth)
    {
        WriteShift(id, shell, colour, alpha, illum);
        for (int k = SceneDb.FirstRider(id.Index); k != 0; k = SceneDb.NextRider(k - 1))
        {
            int c = k - 1;
            if (SceneDb.ObjAttachTo[c].Value != id.Value || !SceneDb.Objects.LiveAt(c)) continue;
            DbId cid = SceneDb.Objects.IdAt(c);
            nint cs = Facade.ObjectShell(cid);
            if (DbAnim.Grafted(c)) { WriteShift(cid, cs, colour, alpha, illum); continue; }
            if (!propagate || SceneDb.ObjAttachMode[c] == SceneDb.AttachNone) continue;
            if (cs == 0 || *(byte*)(cs + SeamLayout.GobPropagateHighlight) == 0) continue;
            if (depth >= MaxShiftDepth) continue;
            Shift(cid, cs, cs, colour, alpha, illum, true, depth + 1);
        }
        if (propagate) FireEvent(self, illum ? EventNames.PropagateIllumination : EventNames.PropagateColorShift, 0);
    }

    /// <summary>One object's own colour-shift or illumination words and columns.</summary>
    private static void WriteShift(DbId id, nint shell, Vector3 colour, float alpha, bool illum)
    {
        int i = id.Index;
        if (illum)
        {
            if (shell != 0)
            {
                *(short*)(shell + SeamLayout.GobIllumFlag) = 1;
                float* e = (float*)(shell + SeamLayout.GobIllum);
                e[0] = colour.X; e[1] = colour.Y; e[2] = colour.Z;
            }
            SceneDb.ObjEmissive[i] = colour;
            Flag(id, SeamFlag.Illuminated, true);
            SceneDb.Touch(id, Dirty.Material);
            return;
        }
        if (shell != 0)
        {
            *(short*)(shell + SeamLayout.GobColourShiftFlag) = 1;
            float* c = (float*)(shell + SeamLayout.GobColourShift);
            c[0] = colour.X; c[1] = colour.Y; c[2] = colour.Z;
            *(float*)(shell + SeamLayout.GobColourAlpha) = alpha;
        }
        // the pick reads the engine's alpha (a faded object is out of it); the draw, PlaceableHold's held one at 1
        SceneDb.HitObjAlpha[i] = alpha;
        SceneDb.ObjTint[i] = new Vector4(colour, shell != 0 && PlaceableHold.Held(shell) ? 1f : alpha);
        Flag(id, SeamFlag.ColourShift, true);
        SceneDb.Touch(id, Dirty.Material);
    }

    /// <summary>A grafted piece appended after its creature was shifted takes the shift it would have been
    /// drawn with in the engine's one gob.</summary>
    internal static void InheritShift(DbId child, DbId root)
    {
        int r = root.Index;
        nint cs = Facade.ObjectShell(child), rs = Facade.ObjectShell(root);
        if (Has(root, SeamFlag.ColourShift))
        {
            Vector4 t = SceneDb.ObjTint[r];
            WriteShift(child, cs, new Vector3(t.X, t.Y, t.Z), rs != 0 ? *(float*)(rs + SeamLayout.GobColourAlpha) : t.W, false);
        }
        if (Has(root, SeamFlag.Illuminated)) WriteShift(child, cs, SceneDb.ObjEmissive[r], 0f, true);
    }


    /// <summary><c>Gob::SetPropagateHighlightEffects</c> @0x444770: one byte at +0x1c, no event. On by default.</summary>
    [UnmanagedCallersOnly]
    private static void SetPropagateHighlightEffects(nint self, byte on)
    {
        if (!Live(self, 50, out DbId id, out nint shell)) return;
        *(byte*)(shell + SeamLayout.GobPropagateHighlight) = on;
        Flag(id, SeamFlag.PropagateHighlight, on != 0);
        SceneDb.Touch(id, Dirty.Material);
    }

    // ---- shadows, lights, wind, fog ----

    /// <summary><c>Gob::TurnOffShadows</c> @0x444740 writes the 16-bit field at +0xf4 as a unit.</summary>
    [UnmanagedCallersOnly]
    private static void TurnOffShadows(nint self) => Shadows(self, 51, false);

    /// <summary><c>Gob::TurnOnShadows</c> @0x444750.</summary>
    [UnmanagedCallersOnly]
    private static void TurnOnShadows(nint self) => Shadows(self, 52, true);

    private static void Shadows(nint self, int logical, bool on)
    {
        if (!Live(self, logical, out DbId id, out nint shell)) return;
        *(short*)(shell + SeamLayout.GobShadows) = on ? (short)1 : (short)0;
        Flag(id, SeamFlag.Shadows, on);
        SceneDb.Touch(id, Dirty.Visible);
    }

    /// <summary><c>Gob::SetLightPriority(int)</c> @0x43f760 writes the priority on every LIGHT PART under the
    /// object's root and stores nothing on the object itself. The seam keeps it as an object column and lets
    /// the light set read it, because CX's lights are records keyed on (object, part) rather than fields on a
    /// part node.</summary>
    [UnmanagedCallersOnly]
    private static void SetLightPriority(nint self, int priority)
    {
        if (!Live(self, 53, out DbId id, out _)) return;
        SceneDb.ObjSeamLightPri[id.Index] = priority;
        SceneDb.Touch(id, Dirty.Material);
    }

    /// <summary><c>Gob::GetMaximumLightRadius</c> @0x43fba0 collects the object's lights and answers the
    /// largest range, 0 when it has none. The engine allocates a list per call, so no caller may be per-frame.</summary>
    [UnmanagedCallersOnly]
    private static float GetMaximumLightRadius(nint self)
    {
        if (!Live(self, 54, out DbId id, out _)) return 0f;
        return SeamBridge.MaxLightRadius(id);
    }

    /// <summary><c>Gob::SetWind(bool, float)</c> @0x436f40: two independent stores, +0x199 and +0x1a0.</summary>
    [UnmanagedCallersOnly]
    private static void SetWind(nint self, byte on, float strength)
    {
        if (!Live(self, 56, out DbId id, out nint shell)) return;
        *(byte*)(shell + SeamLayout.GobWindFlag) = on;
        *(float*)(shell + SeamLayout.GobWindStrength) = strength;
        Flag(id, SeamFlag.Wind, on != 0);
        SceneDb.ObjSeamWind[id.Index] = strength;
        SceneDb.Touch(id, Dirty.Material);
    }

    /// <summary><c>Gob::DisableFog</c> @0x43d630 keeps NO object state: it tail-jumps into
    /// <c>PartSetFog(root, false)</c>, so the fog bit lives on the parts and an <c>Append</c> loses it. The
    /// seam keeps it on the object, where a re-built tree inherits it -- a deliberate difference, and the
    /// better behaviour.</summary>
    [UnmanagedCallersOnly]
    private static void DisableFog(nint self) => Fog(self, 61, false);

    /// <summary><c>Gob::EnableFog</c> @0x43d650.</summary>
    [UnmanagedCallersOnly]
    private static void EnableFog(nint self) => Fog(self, 62, true);

    private static void Fog(nint self, int logical, bool on)
    {
        if (!Live(self, logical, out DbId id, out _)) return;
        Flag(id, SeamFlag.Fog, on);
        SceneDb.Touch(id, Dirty.Material);
    }

    /// <summary><c>Gob::ControlPartLightColor(char const*, Vector)</c> @0x4436d0 recolours the light a named
    /// part owns. The engine does it by formatting <c>"color %f %f %f"</c> into a throwaway 0xa0-byte
    /// <c>MdlNode</c> clone and running the model-controller parser over it; CX's lights are records, so the
    /// seam writes the colour straight onto them -- same result, no per-call allocation.</summary>
    [UnmanagedCallersOnly]
    private static void ControlPartLightColor(nint self, nint partName, EVec colour)
    {
        if (!Live(self, 66, out DbId id, out _)) return;
        SeamBridge.SetPartLightColour(id, Str(partName), colour.V);
        SceneDb.Touch(id, Dirty.Material);
    }

    /// <summary><c>Gob::SetAsCharacter</c> @0x444790 writes <c>Model+0x8e = 4</c> -- on the MODEL, which every
    /// instance of that model shares, and as a whole-byte store that clears the model's other two flag bits.
    /// The seam makes it a per-OBJECT flag instead: the engine's version is a bug surface (two creatures of
    /// one appearance, one of them not a character) that nothing depends on.</summary>
    [UnmanagedCallersOnly]
    private static void SetAsCharacter(nint self)
    {
        if (!Live(self, 67, out DbId id, out _)) return;
        Flag(id, SeamFlag.Character, true);
        SceneDb.Touch(id, Dirty.Visible);
    }

    /// <summary><c>Gob::SetRenderPersonalSpace</c> @0x4447c0: one byte at +0x198.</summary>
    [UnmanagedCallersOnly]
    private static void SetRenderPersonalSpace(nint self, byte on)
    {
        if (!Live(self, 73, out DbId id, out nint shell)) return;
        *(byte*)(shell + SeamLayout.GobPersonalSpace) = on;
        Flag(id, SeamFlag.PersonalSpace, on != 0);
    }

    /// <summary><c>Gob::EnableInViewVolume</c> @0x436f10 sets the byte at +0x1b AND fires
    /// <c>propagateviewvolume</c>, which is how attachments inherit it; <c>Disable</c> @0x436f30 only clears
    /// it. The asymmetry is the engine's, kept.</summary>
    [UnmanagedCallersOnly]
    private static void EnableInViewVolume(nint self)
    {
        if (!Live(self, 86, out DbId id, out nint shell)) return;
        *(byte*)(shell + SeamLayout.GobInView) = 1;
        Flag(id, SeamFlag.InView, true);
        FireEvent(self, EventNames.PropagateViewVolume, 0);
    }

    [UnmanagedCallersOnly]
    private static void DisableInViewVolume(nint self)
    {
        if (!Live(self, 87, out DbId id, out nint shell)) return;
        *(byte*)(shell + SeamLayout.GobInView) = 0;
        Flag(id, SeamFlag.InView, false);
    }

    /// <summary><c>Gob::IsInViewVolume</c> @0x444780 reads the byte at +0x1b. It is the CULL PASS's answer,
    /// not a test: the engine's scene walk calls Enable/Disable as it sorts the frame.</summary>
    [UnmanagedCallersOnly]
    private static byte IsInViewVolume(nint self)
    {
        nint shell = Facade.GobBlock(self);
        if (shell == 0) { Facade.Object(self, 88); return 0; }
        return *(byte*)(shell + SeamLayout.GobInView);
    }

    // ---- hit tests: the cast is CX-118's, the slot is the seam's ----

    /// <summary>One object's cast, shared by the three hit slots. The engine restricts <c>Gob::HitCheck</c> to
    /// this object by walking its own part tree; the seam casts over the object's own scene and keeps only a
    /// hit on this object -- the same answer, one pass.</summary>
    private static nint CastOne(DbId id, nint shell, EVec from, EVec to, EVec* at, nint info, bool boxOnly)
    {
        HitQuery q = HitQuery.Ray(SceneDb.ObjScene[id.Index], from.V, to.V);
        q.Objects = true;
        q.StaticPlaceables = false;
        q.WalkMesh = false;
        q.BoxOnly = boxOnly;
        if (!HitTest.Cast(q, out HitResult r)) return 0;
        // StringGob::HitCheck answers the model riding the line it hit (a chat line's reply button), not itself.
        nint hit = r.Obj.Value == id.Value ? shell
                 : HitTest.RidesLine(id, r.Obj) ? SceneDb.ObjFacade[r.Obj.Index] : 0;
        if (hit == 0) return 0;
        if (at != null) *at = EVec.From(r.Point);
        if (info != 0) HitTest.Fill(r, (byte*)info);
        return hit;
    }

    /// <summary><c>Gob::HitCheck(Vector from, Vector to, Vector* at, CHitInfo* info)</c> @0x4381d0: one
    /// object's cast, returning the object hit or null and filling <paramref name="info"/>. A null
    /// <c>CHitInfo*</c> is the engine's own default options block, not a refusal -- the engine substitutes a
    /// global for it on the first line.</summary>
    [UnmanagedCallersOnly]
    private static nint HitCheck(nint self, EVec from, EVec to, EVec* at, nint info)
    {
        if (!Live(self, 38, out DbId id, out nint shell)) return 0;
        return CastOne(id, shell, from, to, at, info, boxOnly: false);
    }

    /// <summary><c>Gob::HitCheckHack(Vector, Vector, Vector*)</c> @0x437240 is a one-line forwarder: it tail
    /// jumps to <c>HitCheck</c> with the <c>CHitInfo*</c> forced to null. It exists in the vtable only so a
    /// subclass can intercept the three-argument form.</summary>
    [UnmanagedCallersOnly]
    private static nint HitCheckHack(nint self, EVec from, EVec to, EVec* at)
    {
        if (!Live(self, 36, out DbId id, out nint shell)) return 0;
        return CastOne(id, shell, from, to, at, 0, boxOnly: false);
    }

    /// <summary><c>Gob::HitCheckPart(CAurPart*, Vector, Vector, Vector*, CHitInfo*)</c> @0x437e10: the cast
    /// restricted to one part's subtree.
    /// <para>SIMPLIFIED: the seam casts against the whole object and keeps the hit only when the reported part
    /// is the one asked for -- the same answer for a leaf, narrower for an interior part.</para></summary>
    [UnmanagedCallersOnly]
    private static nint HitCheckPart(nint self, nint part, EVec from, EVec to, EVec* at, nint info)
    {
        if (!Live(self, 37, out DbId id, out nint shell)) return 0;
        if (!PartShell.Resolve(part, out DbId owner, out int want) || owner.Value != id.Value) return 0;
        HitQuery q = HitQuery.Ray(SceneDb.ObjScene[id.Index], from.V, to.V);
        q.Objects = true;
        q.StaticPlaceables = false;
        q.WalkMesh = false;
        if (!HitTest.Cast(q, out HitResult r) || r.Obj.Value != id.Value || r.Part != want) return 0;
        if (at != null) *at = EVec.From(r.Point);
        if (info != 0) HitTest.Fill(r, (byte*)info);
        return shell;
    }
}

/// <summary>The engine's own event names, as interned native strings, so firing one costs no allocation.
/// They are the literals around <c>.rodata</c> 0x12eeed8 that the engine's own slots pass to
/// <c>Gob::DoEvent</c>.</summary>
internal static unsafe class EventNames
{
    internal static readonly nint PropagateColorShift = Marshal.StringToCoTaskMemUTF8("propagatecolorshift");
    internal static readonly nint PropagateIllumination = Marshal.StringToCoTaskMemUTF8("propagateillumination");
    internal static readonly nint PropagateViewVolume = Marshal.StringToCoTaskMemUTF8("propagateviewvolume");
    internal static readonly nint PropagateRender = Marshal.StringToCoTaskMemUTF8("propagaterender");
    internal static readonly nint UpdateScenes = Marshal.StringToCoTaskMemUTF8("UpdateScenes");
    internal static readonly nint EmitterTarget = Marshal.StringToCoTaskMemUTF8("_EmitterTarget");
    internal static readonly nint Dying = Marshal.StringToCoTaskMemUTF8("Dying");
}
