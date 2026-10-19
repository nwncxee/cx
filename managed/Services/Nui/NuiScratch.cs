using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CX.Services.Nui;

/// <summary>Zeroed native blocks a NUI self-check builds its fake nuklear / kiwi memory out of. Every block
/// handed out is freed on <see cref="Dispose"/>, so a failing assertion between the two cannot leak one.</summary>
internal sealed unsafe class NuiScratch : IDisposable
{
    private readonly List<IntPtr> _blocks = new();

    /// <summary>One zeroed block of <paramref name="bytes"/> bytes, owned by this scratch.</summary>
    internal IntPtr Block(int bytes)
    {
        IntPtr p = (IntPtr)NativeMemory.AllocZeroed((nuint)bytes);
        _blocks.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (IntPtr p in _blocks) NativeMemory.Free((void*)p);
        _blocks.Clear();
    }
}
