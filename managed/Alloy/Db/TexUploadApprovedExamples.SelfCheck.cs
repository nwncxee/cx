using System;
using System.Collections.Generic;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>A texture whose upload found no Vulkan device (approved-spec, E2-E5, E7): the line says why, once, with the
/// log off, and the texture comes back when asked for again. Oracle: the four device conditions written out here and
/// the stub host, where no device exists, so every upload fails the way the reporter's did.</summary>
internal static unsafe partial class TexRegistry
{
    /// <summary>What each device condition reads as in cx.log, written out by hand.</summary>
    private static readonly string[] DeviceConditions =
    [
        "Alloy is not drawing on Vulkan", "Alloy does not own the textures", "Alloy's gate failed", "the Vulkan device does not exist",
    ];

    /// <summary>The start of the line a missing device writes.</summary>
    private const string NoDeviceLine = "TexRegistry: no Vulkan device for textures -- ";

    internal static bool UploadApprovedExamplesSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TexRegistry.UploadApprovedExamples.SelfCheck");
        if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero)
        { check.Skip("in a session: the suite closes the log gate and waits out the retry, which a player's boot does not pay for"); return true; }
        if (VkTextures.Device(log) != null)
        { check.Skip("the stub host has a Vulkan device for textures, so no upload here fails for want of one"); return true; }
        ExtenderLog? saved = _log;
        _log ??= log;
        try
        {
            Cx569E3(check);
            Cx569E2(check);
            Cx569E4(check);
            Cx569E5(check);
            Cx569E7(check);
        }
        finally { _log = saved; }
        log.Info(check.Ok ? "TexRegistry.UploadApprovedExamples.SelfCheck: passed" : "TexRegistry.UploadApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    // Given a texture that fails because CX had no Vulkan device to put it on
    // When the game asks for it
    // Then cx.log says no device was there and why, and does not say Vulkan refused it
    private static void Cx569E2(Checker check)
    {
        uint name = Upload(UploadJob(0, 0, "cx_e2"), out string? why);
        check.Expect(name == 0, "E2: an upload with no device made an image");
        string w = why ?? "";
        check.Expect(!w.Contains("Vulkan refused", StringComparison.Ordinal), $"E2: an upload Vulkan was never asked about says \"{w}\"");
        check.Expect(w.Contains("no Vulkan device", StringComparison.Ordinal) && NamesCondition(w), $"E2: the reason \"{w}\" does not say which device condition was false");
    }

    // Given Alloy draws, but has no Vulkan device for textures
    // When textures load
    // Then cx.log holds a line naming which of these was false: Alloy draws on Vulkan, Alloy owns textures, Alloy's gate is open, the device exists
    private static void Cx569E3(Checker check)
    {
        ForgetNoDeviceLines();
        check.Expect(!NoDeviceLineInLog(), "E3: cx.log held a no-device line before the upload, so this check cannot tell");
        Upload(UploadJob(0, 0, "cx_e3"), out _);
        ExtenderLog.Flush();
        check.Expect(NoDeviceLineInLog(), "E3: cx.log holds no line naming the device condition an upload failed on");
    }

    /// <summary>Does the end of cx.log hold a no-device line naming one of the conditions?</summary>
    private static bool NoDeviceLineInLog()
    {
        ExtenderLog.Flush();
        foreach (string c in DeviceConditions) if (ExtenderLog.TailHas(NoDeviceLine + c)) return true;
        return false;
    }

    // Given textures keep finding no Vulkan device for the same reason
    // When a thousand of them load
    // Then cx.log holds that line once, not once per texture
    private static void Cx569E4(Checker check)
    {
        ForgetNoDeviceLines();
        List<string> heard = Listen();
        for (int k = 0; k < 1000; k++) Upload(UploadJob(0, 0, $"cx_e4_{k}"), out _);
        int lines = 0;
        foreach (string l in heard) if (l.StartsWith(NoDeviceLine, StringComparison.Ordinal)) lines++;
        check.Expect(lines == 1, $"E4: a thousand uploads with no device wrote the line {lines} times, not once");
    }

    // Given a texture that failed to load because no device was there for it
    // And the device is back
    // When the game asks for that texture again
    // Then it draws textured, not white
    private static void Cx569E5(Checker check)
    {
        TexId id = FailUpload("cx_e5");
        check.Expect(StateOf(id) == TexState.Failed, "E5: the upload with no device did not fail");
        BlockingWait.Sleep(RetryWaitMs, "self-check only: outlive the once-a-second retry");
        TexId again = Alloc("cx_e5", TexKind.Plain, 0, out bool offer);
        check.Expect(again.Index == id.Index && offer, "E5: asking again for a texture whose upload failed does not load it again");
        if (offer) Land(again, 0xE5E5, 4096);
        check.Expect(TryResident(again, out uint name, out _) && name == 0xE5E5, "E5: the texture asked for again does not draw textured");
        Release(again); Release(id);
    }

    // Given a texture whose upload fails every time
    // When it stays on screen for a minute
    // Then the frame rate holds as it does with that texture loaded, and cx.log reports it once
    private static void Cx569E7(Checker check)
    {
        List<string> heard = Listen();
        TexId id = FailUpload("cx_e7");
        int offers = 0;
        for (int k = 0; k < 1000; k++) { Alloc("cx_e7", TexKind.Plain, 0, out bool o); if (o) { offers++; _pend[id.Index] = PendFlight; } }
        check.Expect(offers == 0, $"E7: a failed upload was offered again {offers} times inside its first second");
        BlockingWait.Sleep(RetryWaitMs, "self-check only: outlive the once-a-second retry");
        for (int k = 0; k < 1000; k++) { Alloc("cx_e7", TexKind.Plain, 0, out bool o); if (o) { offers++; _pend[id.Index] = PendFlight; } }
        check.Expect(offers == 1, $"E7: a second on, a thousand asks offered the failed upload {offers} times, not once");
        FinishFailed(id);
        check.Expect(Reports(heard, "cx_e7") == 1, $"E7: an upload failing twice was reported {Reports(heard, "cx_e7")} times, not once");
        for (int k = 0; k < 2001; k++) Release(id);
    }

    /// <summary>Longer than the approved once-a-second retry.</summary>
    private const int RetryWaitMs = 1100;

    /// <summary>A slot whose upload has just failed on the stub host's missing device, through the drain the game uses.</summary>
    private static TexId FailUpload(string resref)
    {
        TexId id = Alloc(resref, TexKind.Plain, 0, out _);
        FinishFailed(id);
        return id;
    }

    /// <summary>Upload <paramref name="id"/>'s job the way the drain does, and land the failure on the game thread.</summary>
    private static void FinishFailed(TexId id)
    {
        _pend[id.Index] = PendFlight;
        Finish(UploadJob(id.Index, id.Gen, ResrefOf(id)));
        Tick();
    }

    /// <summary>A 16x16 five-level DXT1 job, the shape of the reporter's GUI textures.</summary>
    private static Job UploadJob(int slot, uint gen, string resref)
    {
        Job j = Rent();
        j.Slot = slot; j.Gen = gen; j.Resref = resref; j.Kind = TexKind.Plain; j.Why = null; j.Tail = false;
        j.W = j.H = 16; j.Levels = 5; j.GlFmt = 0x83F0; j.Compressed = true; j.Channels = 3;   // not-an-offset: COMPRESSED_RGB_S3TC_DXT1
        return j;
    }

    /// <summary>The lines <see cref="_log"/> writes from here on.</summary>
    private static List<string> Listen()
    {
        var heard = new List<string>();
        _log = new ExtenderLog { Tap = heard.Add };
        return heard;
    }

    /// <summary>How many "did not load" lines name <paramref name="resref"/>.</summary>
    private static int Reports(List<string> heard, string resref)
    {
        int n = 0;
        foreach (string l in heard) if (l.Contains($"\"{resref}\"", StringComparison.Ordinal) && l.Contains("did not load", StringComparison.Ordinal)) n++;
        return n;
    }

    private static bool NamesCondition(string s)
    {
        foreach (string c in DeviceConditions) if (s.Contains(c, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Let each device condition's line be written again, so this suite hears its own.</summary>
    private static void ForgetNoDeviceLines()
    {
        lock (_loudOnce) _loudOnce.RemoveWhere(k => k.StartsWith("nodevice:", StringComparison.Ordinal));
    }
}
