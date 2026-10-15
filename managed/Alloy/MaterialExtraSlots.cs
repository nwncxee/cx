using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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

    /// <summary>One slot's resref, NUL-terminated.</summary>
    [InlineArray(NameLen)]
    private struct SlotName { private byte _e; }

    /// <summary>The resref of each extra slot.</summary>
    [InlineArray(Count)]
    private struct SlotNames { private SlotName _e; }

    /// <summary>The loaded texture of each extra slot.</summary>
    [InlineArray(Count)]
    private struct SlotTextures { private IntPtr _e; }

    /// <summary>One material's extra slots: names as parsed, textures as loaded. Native-allocated, so the
    /// table can hand out its address.</summary>
    private struct Entry
    {
        public SlotNames Names;
        public SlotTextures Tex;
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
            return _table.TryGetValue(shared, out var e) ? new(((Entry*)e)->Tex[s]) : new(IntPtr.Zero);
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
    internal static void Note(IntPtr shared, byte* lineZ)
    {
        ReadOnlySpan<byte> rest = SkipBlanks(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(lineZ));
        if (!StartsWithTexture(rest)) return;
        rest = rest[TextureKeyword.Length..];
        int n = 0, digits = 0;
        while (digits < 3 && digits < rest.Length && rest[digits] >= '0' && rest[digits] <= '9') { n = n * 10 + (rest[digits] - '0'); digits++; }
        rest = rest[digits..];
        if (digits == 0 || (rest.Length > 0 && rest[0] != ' ' && rest[0] != '\t' && rest[0] != '\r' && rest[0] != '\n')) return;
        int s = n - First;
        if ((uint)s >= Count) return;
        rest = SkipBlanks(rest);
        lock (_lock)
        {
            if (!_table.TryGetValue(shared, out IntPtr ep))
                _table[shared] = ep = (IntPtr)NativeMemory.AllocZeroed((nuint)sizeof(Entry));
            Span<byte> dst = ((Entry*)ep)->Names[s];
            int i = 0;
            // sscanf("%s") shape, as ::Parse(char const*, char*) @0x446740 does it: one whitespace-delimited token.
            while (i < NameLen - 1 && i < rest.Length && rest[i] > ' ') { dst[i] = rest[i]; i++; }
            dst[i] = 0;
        }
    }

    private static ReadOnlySpan<byte> SkipBlanks(ReadOnlySpan<byte> s)
    {
        int n = 0;
        while (n < s.Length && (s[n] == ' ' || s[n] == '\t')) n++;
        return s[n..];
    }

    private const string TextureKeyword = "texture";

    private static bool StartsWithTexture(ReadOnlySpan<byte> s)
    {
        if (s.Length < TextureKeyword.Length) return false;
        for (int i = 0; i < TextureKeyword.Length; i++) if ((s[i] | 0x20) != TextureKeyword[i]) return false;
        return true;
    }

    /// <summary>Load every named extra slot, the way <c>SharedMaterial::LoadTextureArray</c> loads the
    /// engine's; a pointer already held is released first, as its SafePointer assignment would.</summary>
    private static void Load(IntPtr shared)
    {
        // Names are copied out so the engine's texture load (disk, NWSync) runs with the lock released: the
        // stage workers take it per owned part, and a load edge must not stall a colour frame.
        SlotNames names;
        lock (_lock)
        {
            if (!_table.TryGetValue(shared, out var ep)) return;
            names = ((Entry*)ep)->Names;
        }
        for (int s = 0; s < Count; s++)
        {
            IntPtr tex;
            fixed (byte* name = (Span<byte>)names[s])
            {
                if (*name == 0) continue;
                tex = _loadTexture(name);
            }
            IntPtr old;
            lock (_lock)
            {
                if (!_table.TryGetValue(shared, out var ep)) { ReleaseIfLive(tex); return; }
                old = ((Entry*)ep)->Tex[s];
                ((Entry*)ep)->Tex[s] = tex;
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
        for (int s = 0; s < Count; s++) ReleaseIfLive(e->Tex[s]);
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
            fixed (byte* nm = (Span<byte>)((Entry*)ep)->Names[s])
                return Marshal.PtrToStringAnsi((IntPtr)nm) ?? "";
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
