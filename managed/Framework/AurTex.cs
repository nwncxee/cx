using System;
using CX.Engine;

namespace CX.Framework;

/// <summary>
/// A safe front door to <c>AurTextureGetTID</c>. A <c>CAurTexture*</c> is BORROWED (the engine frees it on an area
/// transition or a module unload) and the call opens by dereferencing the object's vtable, so a freed pointer is a
/// SIGSEGV; re-fetching per frame does not help, because <c>Material::GetTexture</c> @0x4c5810 is a plain field read
/// of whatever the material still holds. So the pointer is CHECKED against the engine's two texture vtables.
/// </summary>
public static unsafe class AurTex
{
    /// <summary>Resolved once: <see cref="Tid"/> runs per Alloy part per frame, so the lazy resolve's compare
    /// does not belong in it.</summary>
    private static AurTextureGetTIDFn _getTid;
    /// <summary>The two values a live texture's first word may hold: each class's vtable symbol plus the 0x10 the
    /// Itanium ABI puts before the function pointers.
    /// ponytail: a whitelist of two, not a mapped-range test -- a range test would also admit a freed object reused
    /// as another engine class. Ceiling: revisit if a third texture class ever appears.</summary>
    private static IntPtr _vtbl, _vtblBasic;
    private static long _refused;

    /// <summary>Resolved and usable. False leaves every caller where it was before its texture resolved --
    /// no texture id, so no Alloy draw for that part.</summary>
    public static bool Ready { get; private set; }

    /// <summary>How many dangling texture pointers have been refused this session. Nonzero is the engine handing
    /// out a field it already freed, not a bug here; a number that climbs every frame means something upstream
    /// holds a dead MATERIAL.</summary>
    public static long Refused => _refused;

    public static void Attach(ExtenderLog log)
    {
        if (Ready) return;
        _getTid = EngineFn.AurTextureGetTID;
        IntPtr v = Native.Sym(SymbolMap.AurTextureVtbl), b = Native.Sym(SymbolMap.AurTextureBasicVtbl);
        if (_getTid == null || v == IntPtr.Zero || b == IntPtr.Zero)
        {
            log.Info("AurTex: AurTextureGetTID or a texture vtable unresolved -- texture ids unavailable");
            return;
        }
        // Itanium ABI: the vtable SYMBOL points at the RTTI pair; an object's first word is symbol + 0x10.
        // MSVC's vftable symbol is the first word itself.
        int skew = OperatingSystem.IsWindows() ? 0 : 0x10;
        _vtbl = v + skew; _vtblBasic = b + skew;
        Ready = true;
    }

    /// <summary>Is this pointer still a texture? The whole guard, split out from <see cref="Tid"/> so it
    /// can be exercised without an engine to call into -- see <see cref="SelfCheck"/>. Two loads and two
    /// compares; a refusal is counted here, where it happens.</summary>
    private static bool Live(IntPtr tex)
    {
        if (!Ready || tex == IntPtr.Zero) return false;
        IntPtr vt = *(IntPtr*)tex;
        if (vt == _vtbl || vt == _vtblBasic) return true;
        _refused++;
        return false;
    }

    /// <summary>Would <c>AurTextureGetTID</c> survive this live texture? It resolves
    /// <see cref="EngineLayout.TexReferenced"/> (null = the object itself) and reads +0x6c off
    /// <c>AsCAurTextureBasic()</c>, which is NULL on a plain CAurTexture -- so a wrapper whose target is unset, not
    /// a basic, or freed faults in the engine. Counted as a refusal.</summary>
    private static bool Resolves(IntPtr tex)
    {
        IntPtr r = new CAurTexture(tex).Referenced.Ptr;
        if (r == IntPtr.Zero) r = tex;
        if (*(IntPtr*)r == _vtblBasic) return true;
        _refused++;
        return false;
    }

    /// <summary>This texture's GL id, or 0 if the pointer is null, no longer a texture, or resolves to no
    /// basic texture. Hot -- called per Alloy part per frame.</summary>
    public static int Tid(IntPtr tex) => Live(tex) && Resolves(tex) ? _getTid(tex) : 0;
    /// <summary>The guard alone, for a caller about to read the object through an engine call other than
    /// <c>AurTextureGetTID</c> -- the texture controller lookup, say.</summary>
    public static bool IsLive(IntPtr tex) => Live(tex);

    /// <summary>The sheet size of the basic texture <paramref name="tex"/> resolves to -- the level-0 size every
    /// upload of it (the engine's <c>GLRender::CreateTexture</c> @0x4ca2b0, Alloy's own) is made at. 0x0 when
    /// <see cref="Tid"/> would refuse it. No GL.</summary>
    public static void Size(IntPtr tex, out int w, out int h)
    {
        w = h = 0;
        if (!Live(tex) || !Resolves(tex)) return;
        IntPtr r = new CAurTexture(tex).Referenced.Ptr;
        var basic = new CAurTexture(r == IntPtr.Zero ? tex : r);
        w = basic.GetWidth(); h = basic.GetHeight();
    }

    /// <summary>A resman texture's GL id by RESREF -- the resolve and the guard in one call, for the handful of
    /// engine textures an owned pass wants by name rather than off a material (<c>solid_noise</c>, the wave field
    /// vanilla's water shader samples). Re-resolves every call and caches NOTHING, because the pointer is borrowed.
    /// Once per pass, not per part, and allocates nothing: the name is marshalled on the stack.</summary>
    public static int TidByName(string resref) => Tid(FindByName(resref));

    /// <summary>...and the CAurTexture the resman answered with, which is what a caller needs to ask the object its
    /// own name: Windows and Linux can resolve the same resref to different-looking textures, and only the object
    /// says whether that is a different RESOURCE or a different upload. Borrowed, never cached.</summary>
    public static IntPtr FindByName(string resref)
    {
        if (!Ready || EngineFn.AurTextureGet == null || resref.Length == 0) return IntPtr.Zero;
        byte* name = stackalloc byte[17];   // 16 resref bytes + NUL
        int n = resref.Length < 16 ? resref.Length : 16;
        for (int i = 0; i < n; i++) name[i] = (byte)resref[i];
        name[n] = 0;
        return EngineFn.AurTextureGet(name, IntPtr.Zero, null, null, 0, 0);
    }

    /// <summary>Is this pointer a live <c>CAurTextureBasic</c> specifically? <see cref="Live"/> admits both texture
    /// classes, which is right for <c>AurTextureGetTID</c> and wrong for anything reading a BASIC field: the
    /// per-cell GL name array at <c>+0x50</c> and the resolved name at <c>+0xa8</c> exist on the subclass only.
    /// Cold: once per engine texture upload.</summary>
    public static bool IsBasic(IntPtr tex) => Ready && tex != IntPtr.Zero && *(IntPtr*)tex == _vtblBasic;

    /// <summary>Off-device: the guard in both directions, with SYNTHETIC vtables, so it runs on a machine with no
    /// client. The real vtable addresses cannot be asserted here; what can is that an exact match is admitted, that
    /// anything else is refused and counted, and that a null or an unarmed AurTex is refused without a deref.
    /// <see cref="Live"/> rather than <see cref="Tid"/>: admitting a match through Tid would call into no engine.</summary>
    public static bool SelfCheck(ExtenderLog log)
    {
        bool wasReady = Ready; IntPtr wv = _vtbl, wb = _vtblBasic;
        long before = _refused;
        // Saving Ready is not the same as clearing it, and the two assertions below are ABOUT the unarmed
        // state. On a live client Ready is already true, so the "unarmed" probe was really an armed probe
        // against the client's own vtables: refused AND counted, which made `_refused == before` false and
        // this check fail on every live run since it was written. It passed everywhere it was ever watched
        // (headless, where Ready is false), which is how it stayed that way.
        Ready = false;
        bool ok = !Live(IntPtr.Zero);                              // null, before anything is armed
        IntPtr* obj = stackalloc IntPtr[4];
        obj[1] = obj[2] = obj[3] = IntPtr.Zero;
        IntPtr* fakeVtbl = stackalloc IntPtr[2];
        *obj = (IntPtr)fakeVtbl;
        ok &= !Live((IntPtr)obj) && _refused == before;             // unarmed: refused, and not counted

        Ready = true; _vtbl = (IntPtr)fakeVtbl; _vtblBasic = (IntPtr)(fakeVtbl + 1);
        ok &= Live((IntPtr)obj) && _refused == before;               // exact match on the first vtable
        *obj = (IntPtr)(fakeVtbl + 1);
        ok &= Live((IntPtr)obj) && _refused == before;               // ...and on the second
        *obj = (IntPtr)0x1234;                                       // a freed object's clobbered first word
        ok &= !Live((IntPtr)obj) && _refused == before + 1;          // refused AND counted
        ok &= !Live(IntPtr.Zero) && _refused == before + 1;          // null still costs no dereference

        IntPtr* target = stackalloc IntPtr[1];
        *obj = (IntPtr)fakeVtbl;                                     // a live wrapper...
        ok &= !Resolves((IntPtr)obj) && _refused == before + 2;      // ...with no target is not a basic
        obj[EngineLayout.TexReferenced / 8] = (IntPtr)target;
        *target = (IntPtr)fakeVtbl;
        ok &= !Resolves((IntPtr)obj) && _refused == before + 3;      // ...nor is a wrapper for a wrapper
        *target = (IntPtr)(fakeVtbl + 1);
        ok &= Resolves((IntPtr)obj) && _refused == before + 3;       // a wrapper for a basic resolves
        obj[EngineLayout.TexReferenced / 8] = IntPtr.Zero;
        *obj = (IntPtr)(fakeVtbl + 1);
        ok &= Resolves((IntPtr)obj) && _refused == before + 3;       // a basic answers for itself

        Ready = wasReady; _vtbl = wv; _vtblBasic = wb;
        _refused = before;                                           // the check's own refusal is not a session's
        log.Info(ok ? $"AurTex.SelfCheck: passed (armed={wasReady})" : "AurTex.SelfCheck: FAILED");
        return ok;
    }
}
