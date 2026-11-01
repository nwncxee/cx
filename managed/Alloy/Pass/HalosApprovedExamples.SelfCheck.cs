using System;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// The halo fade's approved examples off the device (approved-spec), E6 and E7. Oracles: the step worked by hand
/// (3 per second x max(dt, 0.02 s)) and the slot each key is handed in the order the case stages them.
/// </summary>
internal static partial class Halos
{
    static partial void Examples(Checker check)
    {
        check.Expect(MathF.Abs(FadeStep(0.001f) - 0.06f) < 1e-6f, $"E6: a 1 ms frame steps the fade 0.06, on the 0.02 s floor (got {FadeStep(0.001f):G6})");
        check.Expect(MathF.Abs(FadeStep(0.1f) - 0.3f) < 1e-6f, $"E6: a 100 ms frame steps it 0.3 (got {FadeStep(0.1f):G6})");

        Revoke();
        TakeFadeReset();
        IntPtr a = 11, b = 22, c = 33;
        int sa = FadeSlot(a), sb = FadeSlot(b);
        check.Expect(sa == 0 && sb == 1, $"E7: the first two lights take slots 0 and 1 (got {sa}, {sb})");
        int sb2 = FadeSlot(b), sa2 = FadeSlot(a);
        check.Expect(sa2 == 0 && sb2 == 1, $"E7: the next frame, staged the other way round, they keep them (got {sa2}, {sb2})");
        int sc = FadeSlot(c);
        check.Expect(sc == 2, $"E7: a new light takes the next slot (got {sc})");
        check.Expect(!TakeFadeReset(), "E7: within the area the fade buffer is not zeroed");
        Revoke();
        int sc2 = FadeSlot(c);
        check.Expect(sc2 == 0, $"E7: past the area edge the slots restart (got {sc2})");
        check.Expect(TakeFadeReset(), "E7: ...and the fade buffer is zeroed once");
        check.Expect(!TakeFadeReset(), "E7: ...only once");
        Revoke();
    }
}
