using System;
using CX.Framework;

namespace CX.Graphics;

public sealed unsafe partial class NullGl
{
    /// <summary>The stubs' two pieces of logic against hand-packed bytes: a 2x2 GL_RGB upload with 4-byte row padding,
    /// a BGRA texel and an ALPHA texel widened to RGBA8 (the literals are what GL's unpack rules give); and the entry
    /// table giving a named stub its own pointer, every unknown name the one no-op, and both GetProcAddress names one stub.</summary>
    private static void SelfCheck(ExtenderLog log)
    {
        var c = new Checker(log, "NullGl.SelfCheck");
        Span<byte> rgb = stackalloc byte[16] { 1, 2, 3, 4, 5, 6, 0xEE, 0xEE, 7, 8, 9, 10, 11, 12, 0xEE, 0xEE };
        byte[] out4 = new byte[16];
        Widen(rgb, 0, 0, 2, 2, 2, GlRgb, 3, 4, 0, out4);
        c.Expect(out4[0] == 1 && out4[3] == 255 && out4[4] == 4 && out4[6] == 6 && out4[8] == 7 && out4[12] == 10 && out4[14] == 12,
                 "a padded GL_RGB upload widens row by row, skipping each row's padding, with alpha 255");
        Span<byte> bgra = stackalloc byte[4] { 30, 20, 10, 40 };
        byte[] one = new byte[8];
        Widen(bgra, 1, 0, 1, 1, 2, GlBgra, 4, 4, 0, one);
        c.Expect(one[4] == 10 && one[5] == 20 && one[6] == 30 && one[7] == 40 && one[0] == 0,
                 "a GL_BGRA texel lands red-first at its x offset");
        Span<byte> alpha = stackalloc byte[1] { 77 };
        Widen(alpha, 0, 0, 1, 1, 1, GlAlpha, 1, 1, 0, one);
        c.Expect(one[0] == 0 && one[3] == 77, "a GL_ALPHA texel is black with that alpha");
        c.Expect(Proc("glGetString") != Proc("glNoSuchEntry"), "a named stub has its own entry");
        c.Expect(Proc("glNoSuchEntry") == Proc("glAnotherUnknown"), "every unknown entry is the one no-op");
        c.Expect(Proc("wglGetProcAddress") == Proc("glXGetProcAddressARB"), "both GetProcAddress names are one stub");
        _s.Generic = 0;
        if (c.Ok) log.Info("NullGl.SelfCheck: passed");
    }
}
