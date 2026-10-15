using System;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Seam;

public sealed unsafe partial class ObjectSlots
{
    /// <summary><c>Gob::RegisterCallBack(fn, name, data)</c> @0x497db0: always succeeds, never de-duplicates,
    /// and returns the new <c>CallBack*</c> the caller may later deregister. The <c>CallBack</c> is 0x30 bytes
    /// -- <c>char name[0x20]</c>, the function pointer at +0x20, the user data at +0x28 -- and a null name
    /// becomes the empty string, which is a wildcard.</summary>
    [UnmanagedCallersOnly]
    private static nint RegisterCallBack(nint self, nint fn, nint name, nint data)
    {
        if (!Live(self, 11, out DbId id, out nint shell)) return 0;
        return CallbackList.Add(shell, fn, name, data);
    }

    /// <summary><c>Gob::DeregisterCallback(CallBack*)</c> @0x497e80: linear search by pointer, close the gap,
    /// and DELETE the object -- so the caller's handle dangles afterwards, and a pointer that is not in the
    /// list is silently ignored. Both are the engine's behaviour and both are kept.</summary>
    [UnmanagedCallersOnly]
    private static void DeregisterCallback(nint self, nint cb)
    {
        nint shell = Facade.GobBlock(self);
        if (shell == 0) { Facade.Object(self, 83); return; }
        CallbackList.Remove(shell, cb);
    }

    /// <summary><c>Gob::DoEvent(char const*, void*)</c> @0x49d760.</summary>
    [UnmanagedCallersOnly]
    private static void DoEvent(nint self, nint name, nint data) => FireEvent(self, name, data);

    internal static void FireEvent(nint self, nint name, nint data)
    {
        nint shell = Facade.GobBlock(self);
        if (shell == 0) { Facade.Object(self, 84); return; }
        // The engine's Gob::SetScene @0x43e020 is NOT hooked -- it works on a facade, writes +0x88 itself and
        // then fires this event, which is where the attachment behaviours propagated the new scene down. The
        // behaviours do not exist here, so the propagation lives in this slot, keyed on the same event name.
        if (name != 0 && CallbackList.NameIs(name, "UpdateScenes"))
        {
            DbId id = Facade.Object(self, 84);
            if (!id.IsNull)
                PropagateScene(id, shell, SceneDb.ObjScene[id.Index], Facade.SceneWord(shell));
        }
        CallbackList.Dispatch(self, shell, name, data);
    }
}

/// <summary>The object's animation-event callbacks, kept in the engine's own <c>CExoArrayList&lt;CallBack*&gt;</c>
/// at the shell's +0x460 with the engine's own 0x30-byte <c>CallBack</c> records. Nothing but the seam touches
/// this list -- <c>Gob::RegisterCallBack</c> is a vtable slot, so there is no engine code left to realloc it --
/// which is why it is the one engine list the seam may grow and free with its own allocator.</summary>
internal static unsafe class CallbackList
{
    /// <summary><c>CallBack</c>: <c>char name[0x20]</c>, <c>fn</c> at +0x20, <c>void* data</c> at +0x28.</summary>
    private static readonly int Size = EngineLayout.SeamCallBackSizeof;
    /// <summary>The engine's first growth is capacity 0x10 (a 0x80-byte malloc), then doubling.</summary>
    private const int FirstCap = 0x10;   // not-an-offset: the capacity the engine's first growth asks for

    /// <summary>Callbacks alive, for the period line.</summary>
    internal static long Alive { get; private set; }

    internal static nint Add(nint shell, nint fn, nint name, nint data)
    {
        ExoArray list = new Gob(shell).CallbackArray;
        if (list.Count == list.Cap)
        {
            int grow = list.Cap == 0 ? FirstCap : list.Cap * 2;
            void* bigger = NativeMemory.Realloc((void*)list.Data, (nuint)(grow * sizeof(nint)));
            if (bigger == null)
            {
                Facade.Log?.Always("seam: out of memory growing a callback list -- RegisterCallBack refused");
                return 0;
            }
            list.Data = (nint)bigger;
            list.Cap = grow;
        }
        void* block = NativeMemory.AllocZeroed((nuint)Size);
        if (block == null)
        {
            Facade.Log?.Always("seam: out of memory for a CallBack -- RegisterCallBack refused");
            return 0;
        }
        nint cb = (nint)block;
        if (name != 0)
        {
            byte* src = (byte*)name;
            Span<byte> dst = new CallBack(cb).SeamName;
            int n = 0;
            while (n < dst.Length - 1 && src[n] != 0) { dst[n] = src[n]; n++; }
        }
        new CallBack(cb).SeamFn = fn;
        new CallBack(cb).SeamData = data;
        list[list.Count++] = cb;
        Alive++;
        return cb;
    }

    internal static void Remove(nint shell, nint cb)
    {
        ExoArray list = new Gob(shell).CallbackArray;
        nint arr = list.Data;
        if (arr == 0 || cb == 0) return;
        for (int i = 0; i < list.Count; i++)
        {
            if (((nint*)arr)[i] != cb) continue;
            for (int j = i; j < list.Count - 1; j++) ((nint*)arr)[j] = ((nint*)arr)[j + 1];
            list.Count--;
            NativeMemory.Free((void*)cb);
            Alive--;
            return;
        }
    }

    /// <summary>Dispatch as <c>Gob::DoEvent</c> does, every rule load-bearing: the count is snapshotted before
    /// the walk (a callback registered during dispatch is not seen); the walk runs backward, newest first; a
    /// callback fires on an empty name, a <c>'*'</c> prefix or a whole <c>strcasecmp</c> match; the <c>void*</c>
    /// is the caller's, or the callback's own when null; the walk stops once the object dies -- it may delete it.</summary>
    internal static void Dispatch(nint self, nint shell, nint name, nint data)
    {
        ExoArray list = new Gob(shell).CallbackArray;
        nint arr = list.Data;
        int n = list.Count;
        if (arr == 0 || n <= 0) return;
        if (!SeamShell.Known(shell, out byte state, out DbId id) || state != SeamShell.StateLive) return;
        for (int i = n - 1; i >= 0; i--)
        {
            nint cb = ((nint*)arr)[i];
            if (cb == 0) continue;
            byte* cbName = (byte*)cb;
            bool fires = cbName[0] == 0 || cbName[0] == (byte)'*' || NameEquals(cbName, (byte*)name);
            if (!fires) continue;
            var fn = (delegate* unmanaged<nint, nint, nint, void>)new CallBack(cb).SeamFn;
            if (fn == null) continue;
            nint arg = data != 0 ? data : new CallBack(cb).SeamData;
            fn(self, name, arg);
            if (!SeamShell.Known(shell, out state, out DbId now) || state != SeamShell.StateLive
                || now.Value != id.Value) return;
            // The array can have MOVED (a callback registered inside the callback reallocs it) and entries
            // can have shifted down (a callback deregistered one). The engine snapshots the count and walks
            // backwards, so a shift below the cursor is already accounted for; what it does not do is
            // re-read the count, and neither does this -- re-reading would fire a shifted entry twice.
            arr = list.Data;
            if (arr == 0) return;
            int live = list.Count;
            if (i > live) i = live;   // the list shrank past the cursor: resume at its new end
        }
    }

    /// <summary>Is the NUL-terminated engine string <paramref name="name"/> exactly
    /// <paramref name="what"/>, case-insensitively? For the seam's own handling of the engine's events.</summary>
    internal static bool NameIs(nint name, string what)
    {
        byte* a = (byte*)name;
        int i = 0;
        for (; i < what.Length; i++)
        {
            byte x = a[i];
            if (x == 0) return false;
            if (x >= 'A' && x <= 'Z') x += 32;
            char y = what[i];
            if (y >= 'A' && y <= 'Z') y = (char)(y + 32);
            if (x != y) return false;
        }
        return a[i] == 0;
    }

    /// <summary>Case-insensitive whole-name compare over ASCII, which is what <c>strcasecmp</c> is here: the
    /// engine's event names and every registered name are ASCII literals.</summary>
    private static bool NameEquals(byte* a, byte* b)
    {
        if (b == null) return a[0] == 0;
        int i = 0;
        while (true)
        {
            byte x = a[i], y = b[i];
            if (x >= 'A' && x <= 'Z') x += 32;
            if (y >= 'A' && y <= 'Z') y += 32;
            if (x != y) return false;
            if (x == 0) return true;
            i++;
        }
    }

    /// <summary>Free the object's callbacks and their list; the object destroy does this before the record goes.</summary>
    internal static void FreeAll(nint shell)
    {
        ExoArray list = new Gob(shell).CallbackArray;
        nint arr = list.Data;
        int n = list.Count;
        if (arr != 0)
        {
            for (int i = 0; i < n; i++)
            {
                nint cb = ((nint*)arr)[i];
                if (cb == 0) continue;
                NativeMemory.Free((void*)cb);
                Alive--;
            }
            NativeMemory.Free((void*)arr);
        }
        list.Data = 0;
        list.Count = 0;
        list.Cap = 0;
    }
}
