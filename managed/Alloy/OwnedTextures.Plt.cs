using System;
using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>PLT, the engine's palette-composited skins: g_bGeneratePLTOnGPU is held at zero so every character is
/// composited on the CPU and comes down the upload funnel, where Alloy uploads it like any other image.</summary>
internal static unsafe partial class OwnedTextures
{
    /// <summary>The engine's GPU-PLT switch, held at zero so PLT textures come through the upload funnel as
    /// decoded pixels. Null when the symbol did not resolve, which leaves PLT content borrowed.</summary>
    private static int* _pltOnGpu;
    private static long _pltForced;

    /// <summary>Hold <c>g_bGeneratePLTOnGPU</c> at zero. Re-asserted per frame rather than stored once: the
    /// engine writes it from a settings callback that can run after our install and again whenever the option
    /// is applied, and a single frame with it set is a character created outside the upload funnel and
    /// borrowed until something re-creates it. The count is reported: forcing a global is not silent.</summary>
    internal static void KeepPltOnCpu()
    {
        if (_pltOnGpu == null || *_pltOnGpu == 0) return;
        *_pltOnGpu = 0;
        // #358: the [vram] line carries the count, but it only prints when the high-water GROWS -- so after
        // an area has settled the count reads stale, and "re-cleared 1x" looked like proof the flag never
        // came back when it was only proof nobody had looked since. The re-clears that matter are the ones
        // AFTER Attach's, they are cold, and there are a handful of them: say each one out loud.
        if (_pltForced++ > 0 && _pltDiagLeft > 0)
        {
            _pltDiagLeft--;
            _log?.Info($"OwnedTextures: g_bGeneratePLTOnGPU was set back to 1 -- re-cleared ({_pltForced}x). "
                     + (_origConstructImage != null && _origGlImageBasic != null
                        ? "Held at the readers, so no PLT texture is rebuilt outside the upload funnel (#358)"
                        : "The readers are NOT hooked, so a rebuild in this same engine step is GPU-composited "
                        + "and its character stays BORROWED until something re-creates it (#358)"));
        }
    }

    /// <summary>A small budget of named re-clears: the population is what matters and it is small.</summary>
    private static int _pltDiagLeft = 8;
}
