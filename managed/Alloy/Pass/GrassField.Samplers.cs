using System;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Which atlas each live grass material draws from, resolved once per frame.</summary>
internal static unsafe partial class GrassField
{
    /// <summary>Every live grass material's texture name, for this frame only -- never cached.</summary>
    private static void ResolveTextures(int* tids, bool* live)
    {
        for (int m = 0; m < MaxGrassMaterials; m++)
        {
            CAurGrassInfo info = GrassInfo(m);
            live[m] = !info.IsNull && info.InUse != 0;
            IntPtr tex = !live[m] ? IntPtr.Zero
                : _aurGet((byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref info.Name), IntPtr.Zero, null, null, 0, 0);
            tids[m] = OwnedTextures.Draw(AurTex.Tid(tex));
        }
    }
}
