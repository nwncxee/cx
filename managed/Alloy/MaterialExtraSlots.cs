using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// <c>.mtr</c> texture slots past the engine's ceiling: <c>SharedMaterial::ParseField</c> ends at
/// <c>texture10</c> and the storage behind it is inline and fixed, so a <c>texture11</c> line is parsed and
/// dropped. The extra slots therefore live BESIDE the object -- a table keyed on the <c>SharedMaterial*</c>,
/// filled under ParseField, loaded after <c>Init</c>, evicted by the destructor. The flow map stays on texture6.
/// </summary>
public sealed unsafe class MaterialExtraSlots : IExtenderService
{
    /// <summary>First extender-owned slot: <c>texture11</c>, the one past the engine's <c>texture10</c>.</summary>
    internal const int First = 11;
    /// <summary>How many: texture11..texture14. ponytail: raise the constant when a fourth Alloy map exists.</summary>
    internal const int Count = 4;
    /// <summary>16 resref bytes + NUL, the engine's own buffer size.</summary>
    private const int NameLen = 17;

    /// <summary>One material's extra slots: names as parsed, pointers as loaded (<c>long</c> because a fixed
    /// buffer cannot hold <c>IntPtr</c>). Native-allocated so the buffers are addressable.</summary>
    private struct Entry
    {
        public fixed byte Names[Count * NameLen];
        public fixed long Tex[Count];
    }

    /// <summary>Native, so the fixed buffers are addressable; freed by <see cref="Forget"/>. Every texture in it
    /// is an ADDREF'd reference -- <c>LoadTexture</c> is <c>AurTextureGetReference</c> -- released on
    /// <c>~SharedMaterial</c>, the same edge the engine releases its own eleven on.</summary>
    private static readonly Dictionary<IntPtr, IntPtr> _table = new();
    /// <summary>ponytail: one global lock. ParseField/Init/dtor are load-edge cold and the read is per owned part
    /// per colour-cadence frame off the stage workers -- uncontended in practice.</summary>
    private static readonly object _lock = new();
    private static SharedMaterialParseFieldFn _origParse;
    private static SharedMaterialInitFn _origInit;
    private static SharedMaterialDtorFn _origDtor;
    private static LoadTextureFn _loadTexture;
    private static AurTextureReleaseFn _release;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        SelfCheck(log);
        _loadTexture = EngineFn.LoadTexture;
        _release = EngineFn.AurTextureRelease;
        if (_loadTexture == null || _release == null)
        { log.Info("MaterialExtraSlots: LoadTexture/AurTextureRelease unresolved -- texture11+ stand down"); return; }
        // The death notice FIRST: without it the table would fill and hold ADDREF'd textures nobody evicts,
        // so neither of the other two hooks may install unless this one did.
        _origDtor = hooks.InstallSharedMaterialDtor(&OnDtor);
        if (_origDtor == null)
        { log.Info("MaterialExtraSlots: ~SharedMaterial unhooked -- texture11+ stand down rather than leak"); return; }
        _origParse = hooks.InstallSharedMaterialParseField(&OnParseField);
        _origInit = hooks.InstallSharedMaterialInit(&OnInit);
        log.Info(_origParse != null && _origInit != null
            ? $"MaterialExtraSlots: armed -- texture{First}..texture{First + Count - 1} are extender-owned"
            : "MaterialExtraSlots: ParseField/Init unhooked -- texture11+ read as empty");
    }

    /// <summary>The extra slot's texture, or null. <paramref name="n"/> is the <c>.mtr</c> index
    /// (<c>texture11</c> = 11). Borrowed like every material texture: guard before dereferencing.</summary>
    internal static TextureRef Texture(IntPtr shared, int n)
    {
        int s = n - First;
        if (shared == IntPtr.Zero || (uint)s >= Count) return new(IntPtr.Zero);
        lock (_lock)
            return _table.TryGetValue(shared, out var e) ? new((IntPtr)((Entry*)e)->Tex[s]) : new(IntPtr.Zero);
    }

    [UnmanagedCallersOnly]
    private static void OnParseField(IntPtr shared, byte* line)
    {
        _origParse(shared, line);
        if (line != null) Note(shared, line);
    }

    [UnmanagedCallersOnly]
    private static void OnInit(IntPtr shared, byte* name)
    {
        _origInit(shared, name);
        Load(shared);
    }

    /// <summary>Linux <c>D2</c> ignores the second argument and returns nothing; Windows is the scalar deleting
    /// destructor (<c>this</c>, flags) returning <c>this</c>. One handler forwards both faithfully.</summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnDtor(IntPtr shared, uint flags)
    {
        Forget(shared);
        return _origDtor(shared, flags);
    }

    /// <summary>One <c>.mtr</c> line after the engine has had it: <c>textureN value</c> with N in range is
    /// kept, anything else (including N the engine owns) is ignored. Cold; allocates the entry.</summary>
    internal static void Note(IntPtr shared, byte* line)
    {
        int q = 0;
        while (line[q] == ' ' || line[q] == '\t') q++;
        if (!StartsWithTexture(&line[q])) return;
        q += 7;
        int n = 0, digits = 0;
        while (line[q] >= '0' && line[q] <= '9' && digits < 3) { n = n * 10 + (line[q] - '0'); q++; digits++; }
        if (digits == 0 || (line[q] != 0 && line[q] != ' ' && line[q] != '\t' && line[q] != '\r' && line[q] != '\n')) return;
        int s = n - First;
        if ((uint)s >= Count) return;
        while (line[q] == ' ' || line[q] == '\t') q++;
        lock (_lock)
        {
            if (!_table.TryGetValue(shared, out IntPtr ep))
                _table[shared] = ep = (IntPtr)NativeMemory.AllocZeroed((nuint)sizeof(Entry));
            byte* dst = &((Entry*)ep)->Names[s * NameLen];
            int i = 0;
            // sscanf("%s") shape, as ::Parse(char const*, char*) @0x446740 does it: one whitespace-delimited token.
            while (i < NameLen - 1 && line[q] > ' ') dst[i++] = line[q++];
            dst[i] = 0;
        }
    }

    private static bool StartsWithTexture(byte* p)
    {
        const string k = "texture";
        for (int i = 0; i < k.Length; i++) if ((p[i] | 0x20) != k[i]) return false;
        return true;
    }

    /// <summary>Load every named extra slot, the way <c>SharedMaterial::LoadTextureArray</c> loads the
    /// engine's; a pointer already held is released first, as its SafePointer assignment would.</summary>
    private static void Load(IntPtr shared)
    {
        // Names are copied out so the engine's texture load (disk, NWSync) runs with the lock released: the
        // stage workers take it per owned part, and a load edge must not stall a colour frame.
        byte* names = stackalloc byte[Count * NameLen];
        lock (_lock)
        {
            if (!_table.TryGetValue(shared, out var ep)) return;
            Buffer.MemoryCopy(((Entry*)ep)->Names, names, Count * NameLen, Count * NameLen);
        }
        for (int s = 0; s < Count; s++)
        {
            byte* name = &names[s * NameLen];
            if (*name == 0) continue;
            IntPtr tex = _loadTexture(name);
            IntPtr old;
            lock (_lock)
            {
                if (!_table.TryGetValue(shared, out var ep)) { ReleaseIfLive(tex); return; }
                old = (IntPtr)((Entry*)ep)->Tex[s];
                ((Entry*)ep)->Tex[s] = (long)tex;
            }
            ReleaseIfLive(old);
        }
    }

    /// <summary>The death notice: drop the entry and the references it holds. True if there was one.</summary>
    /// <summary>Evict one material's slots, releasing each texture it holds. The engine nulls its own
    /// SafePointers when a texture dies out from under them; a raw pointer here is not, so the release is skipped
    /// for a pointer that is no longer a texture (and a read goes through <see cref="AurTex"/>'s vtable guard).</summary>
    internal static bool Forget(IntPtr shared)
    {
        IntPtr ep;
        lock (_lock) if (!_table.Remove(shared, out ep)) return false;
        var e = (Entry*)ep;
        for (int s = 0; s < Count; s++) ReleaseIfLive((IntPtr)e->Tex[s]);
        NativeMemory.Free(e);
        return true;
    }

    private static void ReleaseIfLive(IntPtr tex)
    {
        if (_release != null && AurTex.IsLive(tex)) _release(tex);
    }

    /// <summary>Name of extra slot <paramref name="n"/> as parsed, for the self-check and diagnostics.</summary>
    internal static string Name(IntPtr shared, int n)
    {
        int s = n - First;
        if ((uint)s >= Count) return "";
        lock (_lock)
        {
            if (!_table.TryGetValue(shared, out var ep)) return "";
            return Marshal.PtrToStringAnsi((IntPtr)(&((Entry*)ep)->Names[s * NameLen])) ?? "";
        }
    }

    /// <summary>Off-device: the parser's accept/reject line and the eviction, on synthetic keys.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        IntPtr a = (IntPtr)0x1000, b = (IntPtr)0x2000;
        var check = new Checker(log);
        void Line(IntPtr k, string s) { fixed (byte* p = System.Text.Encoding.ASCII.GetBytes(s + "\0")) Note(k, p); }
        Line(a, "texture11 plc_ao");
        Line(a, "  Texture12\tbar_ao  trailing");
        Line(a, "texture9 engine_owned");
        Line(a, "texture100 too_far");
        Line(a, "texture11x junk");
        Line(a, "texture14 0123456789abcdefXYZ");
        Line(b, "bitmap diffuse");
        check.Expect(Name(a, 11) == "plc_ao", "texture11 value");
        check.Expect(Name(a, 12) == "bar_ao", "case + tabs + trailing words");
        check.Expect(Name(a, 13) == "", "unset slot empty");
        check.Expect(Name(a, 14) == "0123456789abcdef", "resref clipped to 16");
        check.Expect(Name(a, 9) == "" && Name(a, 15) == "", "out-of-range reads empty");
        check.Expect(Texture(a, 11).IsNull, "unloaded slot reads null");
        check.Expect(!Forget(b), "no entry for a material with no extra lines");
        check.Expect(Forget(a) && !Forget(a) && Name(a, 11) == "", "evicted once");
        log.Info(check.Ok ? "MaterialExtraSlots.SelfCheck: passed" : "MaterialExtraSlots.SelfCheck: FAILED");
        return check.Ok;
    }
}
