using System;
using System.IO;
using System.Numerics;
using CX.Framework;

namespace CX.Services.Anim;

/// <summary>Runs the leg recorder's and the replay's self-checks at boot. Oracles: hand-worked literals (a leg of length 1
/// raised 0.25 is 0.25 shorter; 0.1 rad in half a second is 11.459 deg/s) and the fixture's construction
/// (<see cref="LegReplayFixture"/>), never the grader's own arithmetic.</summary>
public sealed class LegReplaySelfCheck : IExtenderService
{
    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _ = AnimKnobs.G;
        var check = new Checker(log);
        check.Expect(NotRecording(), "with recording not switched on, a run of frames asks for no folder, writes no recording file and buffers nothing");
        check.Expect(TicksAndTotals(), "recording: a tick with the knob off writes what is buffered and closes the file, a tick on an old buffer writes it, a second recording is a new file and the total runs across both");
        check.Expect(Refusals(), "recording: a folder that cannot be made is asked once, not on every frame, and switching off clears it; a failing write stops the recording without throwing");
        check.Expect(RecordsAndReads(), "a recording made through the recorder reads back as the frames put in, grouped by body, and a file of another frame layout is refused");
        check.Expect(Graded(), "grades: a leg raised 0.25 is 0.25 shorter and a pelvis move alone shortens nothing; a foot in contact that moves 1 in half a second slides at 2; 0.1 rad of hip turn in half a second is 11.459 deg/s; a teleport and a foot in the air count for neither");
        check.Expect(Replayed(), "replay: the fixture's walk grades 0 on all three and its step case grades 0.1/0.77 of the leg, 0.5 units/s and no turn, the same on a second replay");
        log.Info(check.Ok ? "LegReplay.SelfCheck: passed" : "LegReplay.SelfCheck: FAILED");
    }

    private static bool Near(float a, float b, float eps) => MathF.Abs(a - b) <= eps;

    /// <summary>A scratch folder under the temp path, emptied.</summary>
    private static string Scratch(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"cx-legrec-{name}-{Environment.ProcessId}");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        return dir;
    }

    /// <summary>With recording off, a run of frames asks for no folder, opens no file and keeps no frame. Oracle: the hand-written
    /// expectation that nothing is made, and a folder that must not exist afterwards. The knob is not read: the core takes it as
    /// an argument, so a cx.cfg that switches recording on cannot make this pass unchecked.</summary>
    private static bool NotRecording()
    {
        string dir = Scratch("off");
        int asked = 0;
        Func<string> folder = () => { asked++; return dir; };
        var st = new LegRecorder.RecorderState();
        LegFrame f = LegReplayFixture.Walk(0);
        for (int n = 0; n < 2 * LegRecorder.BufferFrames; n++) st = LegRecorder.Frame(st, false, folder, (IntPtr)1, f, n);
        return asked == 0 && st.Out == null && st.Written == 0 && st.Count == 0 && !Directory.Exists(dir);
    }

    /// <summary>Recording on writes a file; switching it off (a tick with the knob off, with no frame arriving) writes what is
    /// buffered and closes it; a tick on an old buffer writes it while recording goes on; a second recording is a new file and
    /// state and the total runs across both. Oracle: the frame counts put in, read back from the files.</summary>
    private static bool TicksAndTotals()
    {
        string dir = Scratch("tick");
        Func<string> folder = () => dir;
        LegFrame f = LegReplayFixture.Walk(0);
        var st = new LegRecorder.RecorderState();
        var first = st;
        for (int n = 0; n < 10; n++) st = LegRecorder.Frame(st, true, folder, (IntPtr)1, f, n);
        bool opened = st.Out != null && st.Written == 0 && !ReferenceEquals(st, first);
        LegRecorder.Tick(st, true, 11);
        bool young = st.Written == 0;
        LegRecorder.Tick(st, true, LegRecorder.FlushTicks + 11);
        bool aged = st.Written == 10 && st.Out != null;
        for (int n = 0; n < 3; n++) st = LegRecorder.Frame(st, true, folder, (IntPtr)1, f, LegRecorder.FlushTicks + 12);
        LegRecorder.Tick(st, false, LegRecorder.FlushTicks + 13);
        bool closed = st.Out == null && st.Written == 13 && !st.Failed;
        var again = st;
        for (int n = 0; n < 4; n++) st = LegRecorder.Frame(st, true, folder, (IntPtr)2, f, 0);
        LegRecorder.Tick(st, false, 1);
        string[] files = Directory.GetFiles(dir, "cx-legs-*.cxlegs");
        bool total = st.Total == 17 && !ReferenceEquals(st, again) && files.Length == 2;
        int read = 0;
        foreach (string file in files)
            if (LegRecording.Read(File.ReadAllBytes(file), out _) is { Count: 1 } one) read += one[0].Frames.Count;
        return opened && young && aged && closed && total && read == 17;
    }

    /// <summary>A folder that cannot be made is asked once per switching-on, not once per body per frame, and switching off clears
    /// the refusal; a stream that fails on write stops the recording without throwing. Oracle: the count of asks (1, then 2) and the
    /// state the failure leaves.</summary>
    private static bool Refusals()
    {
        string blocker = Scratch("block");
        File.WriteAllText(blocker, "a file where a folder is wanted");
        int asked = 0;
        Func<string> folder = () => { asked++; return Path.Combine(blocker, "logs"); };
        LegFrame f = LegReplayFixture.Walk(0);
        var st = new LegRecorder.RecorderState();
        for (int n = 0; n < 100; n++) st = LegRecorder.Frame(st, true, folder, (IntPtr)(n % 3 + 1), f, n);
        bool latched = asked == 1 && st.Failed && st.Out == null;
        LegRecorder.Tick(st, false, 200);
        bool cleared = !st.Failed;
        st = LegRecorder.Frame(st, true, folder, (IntPtr)1, f, 201);
        bool retried = asked == 2 && st.Failed;
        File.Delete(blocker);

        var broken = new Broken();
        var wr = new LegRecorder.RecorderState();
        LegRecorder.Begin(broken, wr);
        broken.Armed = true;
        for (int n = 0; n < LegRecorder.BufferFrames; n++) LegRecorder.Add(wr, (IntPtr)1, f, n);
        return latched && cleared && retried && wr.Out == null && wr.Failed && wr.Written == 0;
    }

    /// <summary>A stream that takes its header and then refuses every write.</summary>
    private sealed class Broken : MemoryStream
    {
        internal bool Armed;

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Armed) throw new IOException("disk full");
            base.Write(buffer);
        }
    }

    private static bool RecordsAndReads()
    {
        var ms = new MemoryStream();
        var st = new LegRecorder.RecorderState();
        LegRecorder.Begin(ms, st);
        LegFrame a = LegReplayFixture.Walk(3), b = LegReplayFixture.OnStep(5);
        LegRecorder.Add(st, 7, a, 0);
        LegRecorder.Add(st, 9, b, 0);
        LegRecorder.Add(st, 7, b, 0);
        LegRecorder.Stop(st);
        var bodies = LegRecording.Read(ms.ToArray(), out _);
        bool read = bodies is { Count: 2 } && bodies[0].Body == 1 && bodies[0].Frames.Count == 2 && bodies[1].Body == 2 && bodies[1].Frames.Count == 1
                    && bodies[0].Frames[0].Origin == a.Origin && bodies[0].Frames[1].RAnkle == b.RAnkle && bodies[1].Frames[0].Walk.ContactL;
        byte[] torn = ms.ToArray();
        torn[12]++;
        return read && LegRecording.Read(torn, out string why) == null && why.Length > 0 && LegRecording.Read(new byte[4], out _) == null;
    }

    private static bool Graded()
    {
        LegFrame up = default;
        up.Dt = 0.5f;
        up.Walk.Present = true;
        up.RThigh = new Vector3(0f, 0f, 1f);
        up.LThigh = new Vector3(1f, 0f, 1f);
        up.LAnkle = new Vector3(1f, 0f, 0f);
        LegPose raised = default;
        raised.Ik = true;
        raised.DeltaR = new Vector3(0f, 0f, 0.25f);
        LegPose pelvis = default;
        pelvis.Body = -0.4f;
        LegPose ignored = default;
        ignored.DeltaL = new Vector3(0f, 0f, 0.25f);
        LegFrame[] one = [up];
        bool knee = Near(LegGrade.Grade(one, [raised]).KneeBend, 0.25f, 1e-5f) && LegGrade.Grade(one, [pelvis]).KneeBend == 0f
                    && LegGrade.Grade(one, [ignored]).KneeBend == 0f;

        LegFrame a = default, c = default;
        a.Dt = c.Dt = 0.5f;
        a.Walk.ContactR = c.Walk.ContactR = true;
        c.RAnkle = new Vector3(1f, 0f, 0f);
        LegPose p0 = default, p1 = default;
        p1.Turn = 0.1f;
        bool slide = Near(LegGrade.Grade([a, c], [p0, p1]).Slide, 2f, 1e-5f);
        bool turn = Near(LegGrade.Grade([a, c], [p0, p1]).HipTurn, 11.459156f, 1e-3f);
        c.Snap = true;
        bool teleport = LegGrade.Grade([a, c], [p0, p1]) == default;
        c.Snap = false;
        c.Walk.ContactR = false;
        bool air = LegGrade.Grade([a, c], [p0, p1]).Slide == 0f;
        return knee && slide && turn && teleport && air;
    }

    private static bool Replayed()
    {
        var ms = new MemoryStream();
        LegReplayFixture.Write(ms);
        byte[] file = ms.ToArray();
        var one = LegReplay.Grade(file, out _);
        var two = LegReplay.Grade(file, out _);
        if (one is not { Count: 2 } || two is not { Count: 2 }) return false;
        LegGrades walk = one[0].Grades, step = one[1].Grades;
        return one[0].Frames == LegReplayFixture.WalkFrames && one[1].Frames == LegReplayFixture.StepFrames
               && walk.KneeBend < 1e-4f && walk.Slide < 1e-3f && walk.HipTurn < 1e-2f
               && Near(step.KneeBend, 0.1f / 0.77f, 1e-4f) && Near(step.Slide, 0.5f, 1e-3f) && step.HipTurn < 1e-2f
               && walk == two[0].Grades && step == two[1].Grades;
    }
}
