using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using static CX.Alloy.TexLoadRead;

namespace CX.Alloy;

/// <summary>Where a resource lives, resolved on the MAIN thread without reading it. One pure engine lookup
/// (<c>CExoResMan::GetKeyEntry</c>) gives the key entry, and this turns it into a <see cref="TexLocation"/> a
/// worker can read from -- a VALUE record, because a <c>CKeyTableEntry*</c> is not a stable handle. Nothing here
/// is restype-specific: the caller names the restype it looked the entry up under.</summary>
internal static unsafe class ResLocate
{
    /// <summary>What kind of entry the locate walked, so each caller can count it under its own tally.</summary>
    internal enum Source { Plain, Resident, NwSync }

    /// <summary>A <c>RESID</c> is four dwords (see <see cref="EngineLayout.KeyEntryResId"/>).</summary>
    internal const int ResIdWords = 4;

    /// <summary>The restype <c>CExoResFile::OpenFile</c> @0x52f310 hands <c>CExoFile</c> for a BIF.</summary>
    private const ushort BifResType = 0x270e;   // not-an-offset: restype id
    /// <summary>The restypes <c>CExoEncapsulatedFile::LoadHeader</c> @0x52fbb0 tries, in its order; the first
    /// whose resolved filename exists is the container.</summary>
    private static ReadOnlySpan<ushort> EncapResTypes => [0x7db, 0x809, 0x270d, 0x80d, 0x80e];   // not-an-offset: restype ids

    /// <summary>Longest path this builds, and the resref field's width.</summary>
    private const int MaxPathChars = 512, ResRefChars = 16;

    private static IntPtr _resManSym, _exoBaseSym;
    /// <summary>Resolved container filename per (kind, table id, file index) -- one <c>ResolveFileName</c> and,
    /// for an encapsulated file, one existence probe per extension, then never again.</summary>
    private static readonly Dictionary<(byte Kind, uint TableId, int FileIndex), string?> _paths = new();

    /// <summary>Every symbol the resolve needs took. False means no caller may offer anything.</summary>
    internal static bool Ready { get; private set; }

    /// <summary>The live <c>CExoResMan*</c>, or zero.</summary>
    internal static IntPtr ResMan => _resManSym == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)_resManSym;

    /// <summary>Resolve the two globals. Idempotent: every consumer calls it, the first one does the work.
    /// <paramref name="why"/> is the caller's refusal line when this answers false.</summary>
    internal static bool Attach(out string? why)
    {
        why = null;
        if (Ready) return true;
        _resManSym = Native.Sym(SymbolMap.ExoResMan);
        _exoBaseSym = Native.Sym(SymbolMap.ExoBase);
        if (_resManSym == IntPtr.Zero || _exoBaseSym == IntPtr.Zero || EngineFn.ResManGetKeyEntry == null
            || Native.Sym(SymbolMap.AliasResolveFileName) == IntPtr.Zero)
        {
            why = "g_pExoResMan, g_pExoBase, CExoResMan::GetKeyEntry or CExoAliasList::ResolveFileName unresolved";
            return false;
        }
        Ready = true;
        return true;
    }

    /// <summary>FNV-1a over the lower-cased name. A key alone is never an identity -- the entry's <c>RESID</c> is
    /// re-proved at the hand-back -- so a collision costs a miss, not the wrong resource.</summary>
    internal static ulong Key(byte* name)
    {
        const ulong Prime = 0x100000001B3UL;   // not-an-offset: FNV-1a 64 prime
        ulong h = 0xCBF29CE484222325UL;        // not-an-offset: FNV-1a 64 basis
        for (int k = 0; name[k] != 0; k++)
        {
            uint c = name[k]; if (c - 'A' < 26) c += 32;
            h = (h ^ c) * Prime;
        }
        return h;
    }

    /// <summary>The engine's own lookup for <paramref name="name"/> at <paramref name="resType"/>: true with the
    /// table and entry it lands on. Pure -- <c>logMissing: false</c> makes <c>GetKeyEntry</c> side-effect free.</summary>
    internal static bool Find(byte* name, ushort resType, out IntPtr table, out IntPtr entry)
    {
        table = entry = IntPtr.Zero;
        IntPtr resman = ResMan;
        IntPtr t, e;
        if (resman == IntPtr.Zero || EngineFn.ResManGetKeyEntry(resman, name, resType, &t, &e, 0) == 0) return false;
        table = t; entry = e;
        return true;
    }

    /// <summary>The same lookup again, answering whether it still lands on the <c>RESID</c>
    /// <paramref name="resId"/> was taken from: what turns a hash collision into a miss rather than the wrong
    /// resource, and a runtime resref alias into a miss rather than stale bytes.</summary>
    internal static bool StillAt(byte* name, ushort resType, uint r0, uint r1, uint r2, uint r3)
    {
        if (!Find(name, resType, out _, out IntPtr entry)) return false;
        Span<uint> id = new CKeyTableEntry(entry).ResId;
        return id[0] == r0 && id[1] == r1 && id[2] == r2 && id[3] == r3;
    }

    /// <summary>Snapshot the key entry into a value record. A resident resource (the entry holds a live
    /// <c>CRes*</c>) is left alone: the engine answers it from memory, and a worker cannot hold <c>CRes+0x20</c>
    /// because <c>CExoResMan::Malloc</c> @0x536300 frees off the to-be-freed list on every allocation.
    /// <paramref name="why"/> null on a false answer means the miss is named by <paramref name="src"/> instead.</summary>
    internal static bool Locate(IntPtr tablePtr, IntPtr entryPtr, uint* resId, out TexLocation loc,
                               out string? why, out Source src)
    {
        loc = default; why = null; src = Source.Plain;
        var e = new CKeyTableEntry(entryPtr);
        var t = new CExoKeyTable(tablePtr);
        if (e.Res != IntPtr.Zero) { src = Source.Resident; return false; }
        Span<uint> id = e.ResId;
        for (int i = 0; i < ResIdWords; i++) resId[i] = id[i];
        byte kind = (byte)id[0];
        uint tableId = id[2];
        ushort resType = e.ResType;
        if (kind == KindNwSync)
        {
            src = Source.NwSync;
            return NwSyncLoc(entryPtr, resType, tableId, out loc, out why);
        }
        IntPtr files = t.Files;
        switch (kind)
        {
            case KindBif:
            {
                if (files == IntPtr.Zero) { why = "the table has no file array"; return false; }
                IntPtr file = t.FileAt(id[1]);
                if (file == IntPtr.Zero) { why = $"BIF {id[1]} is not loaded"; return false; }
                IntPtr entries = new CExoResFile(file).Entries;
                if (entries == IntPtr.Zero) { why = "the BIF's resource table is unloaded"; return false; }
                var rec = new BiffVarResEntry(entries)[(int)id[3]];
                string? path = PathOf(kind, tableId, (int)id[1],
                                     (IntPtr)System.Runtime.CompilerServices.Unsafe.AsPointer(ref new CExoResFile(file).Name), BifResType);
                if (path == null) { why = "the BIF's filename does not resolve"; return false; }
                loc = new TexLocation(kind, tableId, path, rec.Offset, rec.StoredSize, rec.PlainSize, rec.Compressed != 0, resType);
                return true;
            }
            case KindErf:
            {
                if (files == IntPtr.Zero) { why = "the table has no file array"; return false; }
                IntPtr file = *(IntPtr*)files;
                if (file == IntPtr.Zero) { why = "the hak is not loaded"; return false; }
                IntPtr entries = new CExoEncapsulatedFile(file).Entries;
                if (entries == IntPtr.Zero) { why = "the hak's resource list is unloaded"; return false; }
                var rec = new ErfResListEntry(entries)[(int)id[3]];
                string? path = PathOf(kind, tableId, 0,
                                     (IntPtr)System.Runtime.CompilerServices.Unsafe.AsPointer(ref new CExoEncapsulatedFile(file).Name), 0);
                if (path == null) { why = "the hak's filename does not resolve"; return false; }
                loc = new TexLocation(kind, tableId, path, rec.Offset, rec.StoredSize, rec.PlainSize, rec.Compressed != 0, resType);
                return true;
            }
            case KindLoose:
            {
                string? path = LoosePath(t, e, resType);
                if (path == null) { why = "the directory's filename does not resolve"; return false; }
                loc = new TexLocation(kind, tableId, path, 0, -1, -1, false, resType);
                return true;
            }
            default:
                why = $"container kind {kind}";
                return false;
        }
    }

    /// <summary>NWSync (kind 5) has no offset in its <c>RESID</c> -- the index names a manifest vector that is
    /// freed once the table is built -- so the location is the lowercase resref alone, and the worker asks
    /// <see cref="NwSyncReader.TryRead"/> for the bytes. A refusal here is the engine's read.</summary>
    private static bool NwSyncLoc(IntPtr entry, ushort resType, uint tableId, out TexLocation loc, out string? why)
    {
        loc = default; why = null;
        if (!NwSyncReader.Armed) return false;
        Span<byte> r = new CKeyTableEntry(entry).ResRef;
        int n = r.IndexOf((byte)0);
        string resref = System.Text.Encoding.ASCII.GetString(n < 0 ? r : r[..n]).ToLowerInvariant();
        if (resref.Length == 0) { why = "an empty resref"; return false; }
        loc = new TexLocation(KindNwSync, tableId, resref, 0, -1, -1, false, resType);
        return true;
    }

    /// <summary>The container's absolute filename, resolved once per file. A BIF's name carries the restype
    /// <c>OpenFile</c> uses; an encapsulated file's does not, so the five <c>LoadHeader</c> tries in its own
    /// order and the first that exists wins -- the same answer the engine's own open reaches.</summary>
    private static string? PathOf(byte kind, uint tableId, int fileIndex, IntPtr nameString, ushort resType)
    {
        var k = (kind, tableId, fileIndex);
        if (_paths.TryGetValue(k, out string? cached)) return cached;
        string? path = null;
        if (resType != 0) path = Resolve(nameString, resType);
        else
            foreach (ushort rt in EncapResTypes)
            {
                string? p = Resolve(nameString, rt);
                if (p != null && System.IO.File.Exists(p)) { path = p; break; }
            }
        _paths[k] = path;
        return path;
    }

    /// <summary>A loose file's path: the table's alias name plus the entry's own resref, through the same
    /// resolution <c>ServiceFromDirectory</c> @0x538940 reaches by handing that string to <c>CExoFile</c>.</summary>
    private static string? LoosePath(CExoKeyTable t, CKeyTableEntry e, ushort resType)
    {
        byte* dir = (byte*)t.Name;
        if (dir == null) return null;
        byte* buf = stackalloc byte[MaxPathChars];
        int n = 0;
        for (int k = 0; dir[k] != 0 && n < MaxPathChars - ResRefChars - 2; k++) buf[n++] = dir[k];
        if (n > 0 && buf[n - 1] != (byte)'/' && buf[n - 1] != (byte)':') buf[n++] = (byte)'/';
        Span<byte> rr = e.ResRef;
        for (int i = 0; i < rr.Length && rr[i] != 0; i++) buf[n++] = rr[i];
        buf[n] = 0;
        // A CExoString is {char* buf; int len; int cap}; ResolveFileName only reads it, so a forged shell serves.
        ExoStringRec shell = new() { Buf = buf, Len = (uint)n };
        return Resolve((IntPtr)(&shell), resType);
    }

    /// <summary>One <c>CExoAliasList::ResolveFileName</c>: an alias name plus a restype to an absolute
    /// filename. The <c>CExoString</c> it returns is the CALLER's, so its buffer is freed here.</summary>
    private static string? Resolve(IntPtr nameString, ushort resType)
    {
        IntPtr aliases = new CExoBase(*(IntPtr*)_exoBaseSym).AliasList.Ptr;
        if (aliases == IntPtr.Zero) return null;
        long* outStr = stackalloc long[2];
        outStr[0] = 0; outStr[1] = 0;
        new CExoAliasList(aliases).AliasResolveFileName(outStr, nameString, resType);
        byte* text = (byte*)outStr[0];
        string? s = text == null || *text == 0 ? null : Marshal.PtrToStringAnsi((IntPtr)text);
        Native.DestroyExoString((IntPtr)outStr);
        return s;
    }
}
