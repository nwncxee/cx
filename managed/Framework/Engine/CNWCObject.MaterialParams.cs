using System;
using CX.Framework;

namespace CX.Engine;

public readonly unsafe partial struct CNWCObject
{
    /// <summary>The object's material values as raw bytes: a change in any of them changes these.</summary>
    public ReadOnlySpan<byte> MaterialParamBytes
    {
        get
        {
            ref ExoArrayRec list = ref MaterialParams;
            return list.Items == IntPtr.Zero || list.Count <= 0 ? default : new ReadOnlySpan<byte>((void*)list.Items, list.Count * EngineLayout.MaterialParamStride);
        }
    }
}
