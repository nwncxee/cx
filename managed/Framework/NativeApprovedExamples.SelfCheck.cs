using System.Runtime.InteropServices;

namespace CX.Framework;

/// <summary>The engine string readers at their cap (approved examples E1-E6), over hand-built CExoStrings and
/// NUL-terminated buffers. Oracle: the literal 10 MiB and 5,000-byte lengths and the bytes written here.</summary>
internal static unsafe class NativeApprovedExamples
{
    private const int TenMiB = 10 * 1024 * 1024;

    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "NativeApprovedExamples.SelfCheck");
        var heard = new List<string>();
        var ear = new ExtenderLog { Tap = heard.Add };
        byte* buf = (byte*)NativeMemory.Alloc(TenMiB + 2);
        byte* exo = stackalloc byte[16];
        try
        {
            NativeMemory.Fill(buf, TenMiB + 2, (byte)'x');
            *(byte**)exo = buf;
            int Cuts() => heard.Count(l => l.Contains("cut", StringComparison.OrdinalIgnoreCase));

            // Given a conversation line 5,000 characters long, such as a bulletin board post
            // When the player opens it
            // Then the conversation window shows all 5,000 characters above the replies
            *(uint*)(exo + 8) = 5000;
            check.Expect(Native.ReadExo((IntPtr)exo).Length == 5000, "E1 a 5,000-character conversation line reads whole");

            // Given a server message 5,000 characters long
            // When it arrives in the chat window
            // Then the chat window shows all 5,000 characters
            buf[4999] = (byte)'!';
            string chat = Native.ReadExo((IntPtr)exo);
            check.Expect(chat.Length == 5000 && chat[4999] == '!', "E2 a 5,000-character chat message reads whole, last byte included");
            buf[4999] = (byte)'x';

            Native.Attach(ear);

            // Given engine text exactly 10 MiB long
            // When CX reads it
            // Then all of it comes through, and cx.log says nothing about it
            *(uint*)(exo + 8) = TenMiB;
            check.Expect(Native.ReadExo((IntPtr)exo).Length == TenMiB && Cuts() == 0, $"E3 10 MiB reads whole and unlogged (cut lines {Cuts()})");

            // Given engine text one byte over 10 MiB
            // When CX reads it
            // Then the first 10 MiB come through and cx.log says the text was cut
            *(uint*)(exo + 8) = TenMiB + 1;
            check.Expect(Native.ReadExo((IntPtr)exo).Length == TenMiB && Cuts() == 1, $"E4 10 MiB + 1 reads as 10 MiB and logs the cut (cut lines {Cuts()})");

            // Given two texts over 10 MiB in one session
            // When CX reads both
            // Then cx.log says a text was cut only once
            *(uint*)(exo + 8) = TenMiB + 2;
            Native.ReadExo((IntPtr)exo);
            buf[TenMiB + 1] = 0;
            Native.Chars(buf);
            check.Expect(Cuts() == 1, $"E5 a second and third cut log nothing more (cut lines {Cuts()})");
            buf[TenMiB + 1] = (byte)'x';

            // Given Alloy is drawing and an engine window gets a label 5,000 characters long
            // When the window draws
            // Then all 5,000 characters show
            buf[5000] = 0;
            check.Expect(Native.Chars(buf).Length == 5000, $"E6 a 5,000-character engine label reads whole (got {Native.Chars(buf).Length})");
        }
        finally
        {
            NativeMemory.Free(buf);
            Native.Attach(log);
        }
        log.Info(check.Ok ? "NativeApprovedExamples.SelfCheck: passed" : "NativeApprovedExamples.SelfCheck: FAILED");
    }
}
