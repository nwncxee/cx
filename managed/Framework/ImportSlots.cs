using System;
using System.Runtime.InteropServices;

namespace CX.Framework;

/// <summary>
/// The client executable's own import slots, rewritten by name: the GOT entries of its dynamic imports on Linux, the IAT
/// entries of one DLL's imports on Windows. Only nwmain's slots change, so every other module keeps its real bindings.
/// Run before the engine first calls through them.
/// </summary>
public static unsafe class ImportSlots
{
    [DllImport("libc.so.6")] private static extern ulong getauxval(ulong type);
    [DllImport("libc.so.6")] private static extern int mprotect(IntPtr addr, nuint len, int prot);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(IntPtr name);
    [DllImport("kernel32.dll")] private static extern int VirtualProtect(IntPtr addr, nuint size, uint prot, uint* old);

    private const ulong AtPhdr = 3, AtPhnum = 5;
    private const uint PtDynamic = 2, PtPhdr = 6, PtGnuRelro = 0x6474E552;   // not-an-offset: ELF program-header types
    private const long DtPltRelSz = 2, DtStrTab = 5, DtSymTab = 6, DtRela = 7, DtRelaSz = 8, DtJmpRel = 23;
    private const uint RGlobDat = 6, RJumpSlot = 7;
    private const int PhdrSize = 56, PhdrVaddr = 16, PhdrMemSz = 40, SymSize = 24, RelaSize = 24, RelaInfo = 8, Page = 4096;
    private const int ProtRead = 1, ProtWrite = 2;
    private const uint PageReadWrite = 0x04;   // not-an-offset: PAGE_READWRITE
    /// <summary>PE: where the NT header's offset sits, and the import directory's RVA within a PE32+ NT header.</summary>
    private const int PeNtAt = 0x3C, PeImportDirAt = 24 + 0x78;   // not-an-offset: PE header layout, not engine memory
    /// <summary>An import descriptor: five uints -- name table, time, chain, DLL name, address table.</summary>
    private const int DescUints = 5, DescNames = 0, DescDll = 3, DescIat = 4, HintBytes = 2;

    /// <summary>Linux: every dynamic import of the executable whose name starts with <paramref name="prefix"/> points at
    /// <paramref name="target"/>(name). The slots rewritten, or -1 with <paramref name="why"/>.</summary>
    public static int RedirectElf(string prefix, Func<string, IntPtr> target, out string why)
    {
        why = "";
        byte* phdr = (byte*)getauxval(AtPhdr);
        int phnum = (int)getauxval(AtPhnum);
        if (phdr == null || phnum <= 0) { why = "the auxiliary vector names no program headers"; return -1; }
        long bias = -1, dynAt = 0, relroLo = 0, relroHi = 0;
        for (int i = 0; i < phnum; i++)
        {
            byte* h = phdr + i * PhdrSize;
            if (*(uint*)h == PtPhdr) bias = (long)(nint)phdr - *(long*)(h + PhdrVaddr);
        }
        if (bias < 0) { why = "the executable has no PT_PHDR"; return -1; }
        for (int i = 0; i < phnum; i++)
        {
            byte* h = phdr + i * PhdrSize;
            long vaddr = *(long*)(h + PhdrVaddr), memsz = *(long*)(h + PhdrMemSz);
            if (*(uint*)h == PtDynamic) dynAt = bias + vaddr;
            else if (*(uint*)h == PtGnuRelro) { relroLo = bias + vaddr; relroHi = relroLo + memsz; }
        }
        if (dynAt == 0) { why = "the executable has no dynamic section"; return -1; }
        long strtab = 0, symtab = 0, rela = 0, relasz = 0, jmprel = 0, pltsz = 0;
        for (long* d = (long*)dynAt; d[0] != 0; d += 2)
        {
            long v = d[1] < bias ? bias + d[1] : d[1];
            switch (d[0])
            {
                case DtStrTab: strtab = v; break;
                case DtSymTab: symtab = v; break;
                case DtRela: rela = v; break;
                case DtRelaSz: relasz = d[1]; break;
                case DtJmpRel: jmprel = v; break;
                case DtPltRelSz: pltsz = d[1]; break;
            }
        }
        if (strtab == 0 || symtab == 0) { why = "the dynamic section has no symbol table"; return -1; }
        var r = new Relocs(bias, strtab, symtab, relroLo, relroHi, prefix, target);
        int a = Rewrite(in r, rela, relasz), b = Rewrite(in r, jmprel, pltsz);
        if (a < 0 || b < 0) { why = "mprotect refused a GOT page"; return -1; }
        return a + b;
    }

    /// <summary>What one pass over a relocation table needs.</summary>
    private readonly record struct Relocs(long Bias, long StrTab, long SymTab, long RelroLo, long RelroHi, string Prefix,
                                          Func<string, IntPtr> Target);

    private static int Rewrite(in Relocs r, long at, long bytes)
    {
        int n = 0;
        for (long e = at; at != 0 && e < at + bytes; e += RelaSize)
        {
            ulong info = *(ulong*)(e + RelaInfo);
            uint type = (uint)info;
            if (type != RJumpSlot && type != RGlobDat) continue;
            string name = Marshal.PtrToStringUTF8((IntPtr)(r.StrTab + *(uint*)(r.SymTab + (long)(info >> 32) * SymSize))) ?? "";
            if (!name.StartsWith(r.Prefix, StringComparison.Ordinal)) continue;
            long slot = r.Bias + *(long*)e;
            IntPtr page = (IntPtr)(slot & ~(long)(Page - 1));
            if (mprotect(page, Page, ProtRead | ProtWrite) != 0) return -1;
            *(IntPtr*)slot = r.Target(name);
            if (slot >= r.RelroLo && slot < r.RelroHi) mprotect(page, Page, ProtRead);
            n++;
        }
        return n;
    }

    /// <summary>Windows: every entry the executable imports from <paramref name="dll"/> points at
    /// <paramref name="target"/>(name); an import by ordinal gets <paramref name="target"/>(""). The entries rewritten,
    /// or -1 with <paramref name="why"/>.</summary>
    public static int RedirectPe(string dll, Func<string, IntPtr> target, out string why)
    {
        why = "";
        byte* b = (byte*)GetModuleHandleW(IntPtr.Zero);
        if (b == null) { why = "no module handle for the executable"; return -1; }
        byte* nt = b + *(uint*)(b + PeNtAt);
        uint imports = *(uint*)(nt + PeImportDirAt);
        if (imports == 0) { why = "the executable has no import directory"; return -1; }
        for (uint* d = (uint*)(b + imports); d[DescDll] != 0; d += DescUints)
        {
            if (!(Marshal.PtrToStringUTF8((IntPtr)(b + d[DescDll])) ?? "").Equals(dll, StringComparison.OrdinalIgnoreCase)) continue;
            if (d[DescNames] == 0) { why = $"{dll}'s imports carry no name table"; return -1; }
            ulong* names = (ulong*)(b + d[DescNames]);
            IntPtr* iat = (IntPtr*)(b + d[DescIat]);
            int count = 0;
            while (names[count] != 0) count++;
            uint old;
            if (VirtualProtect((IntPtr)iat, (nuint)(count * sizeof(IntPtr)), PageReadWrite, &old) == 0)
            { why = "VirtualProtect refused the import table"; return -1; }
            for (int i = 0; i < count; i++)
                iat[i] = target((names[i] >> 63) != 0 ? "" : Marshal.PtrToStringUTF8((IntPtr)(b + (uint)names[i] + HintBytes)) ?? "");
            VirtualProtect((IntPtr)iat, (nuint)(count * sizeof(IntPtr)), old, &old);
            return count;
        }
        why = $"the executable does not import {dll}";
        return -1;
    }
}
