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
        check.Expect(NotRecording(), "with recording not switched on, a run of frames writes no recording file and buffers nothing");
        check.Expect(RecordsAndReads(), "a recording made through the recorder reads back as the frames put in, grouped by body, and a file of another frame layout is refused");
        check.Expect(Graded(), "grades: a leg raised 0.25 is 0.25 shorter and a pelvis move alone shortens nothing; a foot in contact that moves 1 in half a second slides at 2; 0.1 rad of hip turn in half a second is 11.459 deg/s; a teleport and a foot in the air count for neither");
        check.Expect(Replayed(), "replay: the fixture's walk grades 0 on all three and its step case grades 0.1/0.77 of the leg, 0.5 units/s and no turn, the same on a second replay");
        log.Info(check.Ok ? "LegReplay.SelfCheck: passed" : "LegReplay.SelfCheck: FAILED");
    }

    private static bool Near(float a, float b, float eps) => MathF.Abs(a - b) <= eps;

    /// <summary>The recording files in the logs folder.</summary>
    private static int Files()
    {
        string logs = UserFolders.Logs;
        return Directory.Exists(logs) ? Directory.GetFiles(logs, "cx-legs-*.cxlegs").Length : 0;
    }

    /// <summary>The behaviour already held when recording existed only as a plan: with <c>anim.record</c> at its default, frames
    /// are not kept.</summary>
    private static bool NotRecording()
    {
        if (AnimKnobs.Record.V) return true;
        int before = Files(), written = LegRecorder.FramesWritten;
        LegFrame f = LegReplayFixture.Walk(0);
        for (int n = 0; n < 2 * LegRecorder.BufferFrames; n++) LegRecorder.Frame((IntPtr)1, f);
        return Files() == before && LegRecorder.FramesWritten == written;
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
