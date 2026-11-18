using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;

namespace CX.Services.Anim;

/// <summary>Runs a leg recording through the leg core offline and prints each body's grades: no game, no engine, and the
/// same numbers every time, because <see cref="Legs.Step"/> over a fresh <see cref="LegState"/> is all there is.</summary>
internal static class LegReplay
{
    /// <summary>Each body of the recording with its grades, in the order the bodies first appear; null with
    /// <paramref name="refused"/> when the bytes are no recording this build reads.</summary>
    internal static List<(int Body, int Frames, LegGrades Grades)>? Grade(ReadOnlySpan<byte> file, out string refused)
    {
        var bodies = LegRecording.Read(file, out refused);
        if (bodies == null) return null;
        var graded = new List<(int, int, LegGrades)>();
        foreach (var (body, frames) in bodies)
        {
            var poses = new LegPose[frames.Count];
            LegState state = default;
            var all = frames.ToArray();
            for (int i = 0; i < all.Length; i++) Legs.Step(ref state, all[i], out poses[i]);
            graded.Add((body, all.Length, LegGrade.Grade(all, poses)));
        }
        return graded;
    }

    /// <summary>Prints three lines per body to <paramref name="to"/>; false with the reason printed to <paramref name="err"/>
    /// when the file is no recording this build reads.</summary>
    internal static bool Run(string path, TextWriter to, TextWriter err)
    {
        byte[] file;
        try { file = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { err.WriteLine($"replay: {e.Message}"); return false; }
        var graded = Grade(file, out string refused);
        if (graded == null) { err.WriteLine($"replay: {path}: {refused}"); return false; }
        CultureInfo c = CultureInfo.InvariantCulture;
        foreach (var (body, _, g) in graded)
        {
            to.WriteLine(string.Create(c, $"body {body} most extra knee bend {g.KneeBend:F4} of hip-to-ankle distance"));
            to.WriteLine(string.Create(c, $"body {body} most foot slide {g.Slide:F3} units/s"));
            to.WriteLine(string.Create(c, $"body {body} fastest hip turn {g.HipTurn:F2} deg/s"));
        }
        return true;
    }
}
