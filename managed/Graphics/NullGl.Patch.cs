using System;
using CX.Framework;

namespace CX.Graphics;

public sealed unsafe partial class NullGl
{
    /// <summary>Point every GL import of nwmain at its stub: its <c>gl*</c>/<c>glX*</c> GOT slots on Linux, what it takes
    /// from OPENGL32.dll on Windows (<see cref="ImportSlots"/>). Null when done, else why not.</summary>
    private static string? Patch(NullGlState s)
    {
        int n = OperatingSystem.IsWindows() ? ImportSlots.RedirectPe("OPENGL32.dll", Proc, out string why)
                                            : ImportSlots.RedirectElf("gl", Proc, out why);
        if (n < 0) return why;
        if (n == 0) return "nwmain imports no GL entry by name";
        s.Patched = n;
        return null;
    }
}
