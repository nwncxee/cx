using System;
using CX.Framework;

namespace CX.Engine;

/// <summary>Proof that a <see cref="ResRef"/> is the engine's 17 bytes and always terminated. Oracle: the Win64
/// <c>CExoResMan::GetOverride</c> copy (bytes 0..16 of its argument) and hand-written names.</summary>
internal static unsafe class ResRefCheck
{
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "ResRef.SelfCheck");
        check.Expect(sizeof(ResRef) == 17, $"a ResRef spans the 17 bytes the engine copies ({sizeof(ResRef)})");

        byte* tail = stackalloc byte[8];
        "plc_a04"u8.CopyTo(new Span<byte>(tail, 8));
        tail[7] = 0;
        ResRef shortName = ResRef.Of(tail);
        check.Expect(shortName.Length == 7 && shortName[6] == (byte)'4', "an 8-byte name copies its 7 characters");
        bool padded = true;
        for (int i = 7; i < 17; i++) padded &= shortName[i] == 0;
        check.Expect(padded, "...and every byte past them, the terminator included, is NUL");

        ResRef longName = ResRef.Of("abcdefghijklmnopqrst");
        check.Expect(longName.Length == 16 && longName[15] == (byte)'p' && longName[16] == 0,
                     "a 20-character name is cut at 16 and still terminated");

        ResRef lower = ResRef.Of("PLC_A04", lower: true);
        check.Expect(lower[0] == (byte)'p' && lower[3] == (byte)'_' && lower[6] == (byte)'4', "lower folds A-Z only");

        ResRef none = ResRef.Of((byte*)null);
        check.Expect(none.Length == 0 && none[0] == 0, "a null name is the empty resref");
        log.Info(check.Ok ? "ResRef.SelfCheck: passed" : "ResRef.SelfCheck: FAILED");
    }
}
