using System;
using System.IO;
using System.Numerics;

namespace CX.Services.Anim;

/// <summary>A small recording whose grades are known by construction, written through the recorder. Body 1: the synthetic walk
/// (<see cref="GaitClip.SyntheticFoot"/>) at its own pace on flat floors, every grade 0. Body 2: a floor step of 0.1 under one
/// foot, thigh 0.77 over the ankle, the ankle in contact sliding 0.5 units/s: a leg 0.1 / 0.77 shorter, slide 0.5, no turn.
/// Body 3: the walk entered mid-stride by an already tracked body, every grade 0.</summary>
internal static class LegReplayFixture
{
    internal const float Dt = 1f / 60f;
    private const float WalkSpeed = 1.5f, WalkFloor = 0.08f, WalkLift = 0.12f, WalkStance = 0.6f;
    /// <summary>The step body 2's left floor stands above its right, the thigh's height over the ankle, the ankle's slide.</summary>
    internal const float Step = 0.1f, ThighUp = 0.77f, Slide = 0.5f;
    /// <summary>Frames of the walk and of the step.</summary>
    internal const int WalkFrames = 240, StepFrames = 120, MidFrames = 120;
    /// <summary>Where in the walk body 3's recording starts, in frames.</summary>
    private const int MidStart = 37;

    /// <summary>Writes the recording to <paramref name="to"/>, the two bodies' frames interleaved.</summary>
    internal static void Write(Stream to)
    {
        var st = new LegRecorder.RecorderState();
        LegRecorder.Begin(to, st, 0);
        for (int n = 0; n < WalkFrames; n++)
        {
            LegRecorder.Add(st, 1, Walk(n), 0);
            if (n < StepFrames) LegRecorder.Add(st, 2, OnStep(n), 0);
            if (n < MidFrames) LegRecorder.Add(st, 3, MidWalk(n), 0);
        }
        LegRecorder.Stop(st);
    }

    private static LegFrame Base(int n)
    {
        LegFrame f = default;
        f.Dt = Dt;
        f.Knobs = new LegKnobs { Stride = true, FootIk = true, MaxSlope = 0.5f, Lift = 0.25f, Mass = true };
        f.Facing = Quaternion.Identity;
        f.FwdH = Vector3.UnitY;
        f.Right = Vector3.UnitX;
        f.Scale = 1f;
        f.Weight = 1f;
        f.Hips = true;
        f.Snap = false;
        Flat(ref f.FloorR, 0f);
        Flat(ref f.FloorL, 0f);
        return f;
    }

    internal static LegFrame Walk(int n)
    {
        float t = n * Dt, phase = t - MathF.Floor(t);
        Vector3 origin = new(0f, WalkSpeed * t, 0f);
        LegFrame f = Base(n);
        f.Origin = origin;
        f.Ground = WalkSpeed;
        f.Vel = new Vector2(0f, WalkSpeed);
        f.Loco = f.Paced = true;
        f.RAnkle = origin + GaitClip.SyntheticFoot(phase, 0.1f, WalkFloor, WalkSpeed, WalkLift);
        f.LAnkle = origin + GaitClip.SyntheticFoot(phase + 0.5f, -0.1f, WalkFloor, WalkSpeed, WalkLift);
        f.RThigh = origin + new Vector3(0.1f, 0f, 0.85f);
        f.LThigh = origin + new Vector3(-0.1f, 0f, 0.85f);
        f.Walk = new LegWalk
        {
            Present = true, Weight = 1f, Strides = 1f, Dir = Vector3.UnitY, LeadDir = Vector3.UnitY, Speed = WalkSpeed,
            EngineRate = 1f, Rate = 1f, Phase = phase, Length = 1f, Cyclic = true, Authored = true, Changed = n == 0,
            FloorR = WalkFloor, FloorL = WalkFloor, LegLength = 0.9f, DownR = 0f, DownL = 0.5f,
            LandR = new Vector3(0.1f, 0.45f, WalkFloor), LandL = new Vector3(-0.1f, 0.45f, WalkFloor),
            ContactR = Stance(phase), ContactL = Stance(phase + 0.5f), SwingR = Swing(phase), SwingL = Swing(phase + 0.5f),
        };
        return f;
    }

    /// <summary>The walk entered mid-stride by a recording switched on mid-session: the body already tracked (<c>Tracked</c>, no
    /// <c>Snap</c>), facing a world heading other than 0, <c>anim.pivot</c> on and a locomotion anim. Nothing turns the hips.</summary>
    internal static LegFrame MidWalk(int n)
    {
        LegFrame f = Walk(n + MidStart);
        f.Tracked = true;
        f.Snap = false;
        f.Knobs.Pivot = true;
        f.Anim = 2;
        return f;
    }

    internal static LegFrame OnStep(int n)
    {
        LegFrame f = Base(n);
        f.Snap = n == 0;
        f.Knobs.Stride = false;
        f.Knobs.Mass = false;
        Vector3 at = new(0f, Slide * n * Dt, 0f);
        f.Origin = at;
        f.RAnkle = at + new Vector3(0.1f, 0f, WalkFloor);
        f.LAnkle = at + new Vector3(-0.1f, 0f, WalkFloor);
        f.RThigh = f.RAnkle + new Vector3(0f, 0f, ThighUp);
        f.LThigh = f.LAnkle + new Vector3(0f, 0f, ThighUp);
        f.Walk = new LegWalk { Present = true, Weight = 1f, Strides = 1f, Speed = WalkSpeed, EngineRate = 1f, Rate = 1f, Length = 1f, LegLength = 0.9f, ContactL = true };
        Flat(ref f.FloorL, Step);
        return f;
    }

    private static bool Stance(float phase) => phase - MathF.Floor(phase) < WalkStance;

    private static float Swing(float phase)
    {
        float q = phase - MathF.Floor(phase);
        return q < WalkStance ? 1f : (q - WalkStance) / (1f - WalkStance);
    }

    /// <summary>A floor <paramref name="off"/> above the reference height under the foot, under its landing and on both slopes.</summary>
    private static void Flat(ref FootFloor floor, float off)
    {
        floor = default;
        floor.Add(new FloorSample(true, off));
        floor.Land = new FloorSample(true, off);
        floor.NewF = floor.NewS = true;
    }
}
