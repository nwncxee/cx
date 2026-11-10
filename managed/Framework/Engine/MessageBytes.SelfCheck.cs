using System;
using CX.Framework;

namespace CX.Engine;

/// <summary>Proof that a message answers for its own length. Oracle: a hand-built object at the rows'
/// offsets, with a length the fixture chose and a buffer the fixture filled.</summary>
internal static unsafe class MessageBytesCheck
{
    /// <summary>The fixture's byte count, and the value its last byte carries.</summary>
    private const int Bytes = 40, Mark = 167;

    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "CNWMessage.Bytes.SelfCheck");
        int span = Math.Max(Math.Max(EngineLayout.MsgBuffer + sizeof(IntPtr), EngineLayout.MsgLength + sizeof(uint)),
                            EngineLayout.MsgCursor + sizeof(uint));
        byte* obj = stackalloc byte[span];
        new Span<byte>(obj, span).Clear();
        byte* buf = stackalloc byte[Bytes];
        new Span<byte>(buf, Bytes).Clear();
        buf[Bytes - 1] = Mark;

        var m = new CNWMessage((IntPtr)obj);
        m.Buffer = (IntPtr)buf;
        m.Length = Bytes;
        ReadOnlySpan<byte> bytes = m.Bytes;
        check.Expect(bytes.Length == Bytes, $"a message's bytes are as long as its length row says ({bytes.Length})");
        check.Expect(!bytes.IsEmpty && bytes[^1] == Mark, "...and reach the last byte the buffer holds");

        m.Buffer = IntPtr.Zero;
        check.Expect(m.Bytes.IsEmpty, "a message with no buffer reads as no bytes");
        m.Buffer = (IntPtr)buf;
        m.Length = 0;
        check.Expect(m.Bytes.IsEmpty, "...and so does one whose length says nothing arrived");
        log.Info(check.Ok ? "CNWMessage.Bytes.SelfCheck: passed" : "CNWMessage.Bytes.SelfCheck: FAILED");
    }
}
