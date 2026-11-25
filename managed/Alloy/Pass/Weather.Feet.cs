using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Services.Anim;

namespace CX.Alloy;

/// <summary>A footfall read off the creature's own body: its feet are the rig's foot parts where its rig names
/// them, else its leaf mesh parts; each foot planted on the ground at that moment presses a print its own size.</summary>
internal static unsafe partial class Weather
{
    /// <summary>Feet read off one body, at most.</summary>
    private const int FeetMax = 16;
    /// <summary>Metres over the ground line under which a foot is planted: a lifted paw clears it.</summary>
    private const float PlantedTol = 0.04f;
    /// <summary>The steepest ground a stride is read on, rise over run along the heading.</summary>
    private const float GradeMax = 1f;
    /// <summary>Metres along the heading a foot must sit off the centre to set the ground's grade.</summary>
    private const float GradeRunMin = 0.05f;
    /// <summary>The smallest half-size a pad is pressed at, so a leg tip still shows at the field's texel.</summary>
    private const float PadMinHalf = 0.03f;

    /// <summary>One foot in the heading's frame: its centre along and across, its lowest point, its half-sizes.</summary>
    private struct Foot
    {
        public float Along, Across, Low, Len, Wide;
        public bool Down;
    }

    /// <summary>Presses a print under each planted foot of <paramref name="s"/>'s body and throws one splash at the first;
    /// false when the body gives no foot geometry, so the caller presses the stand-in stamp.</summary>
    private static bool OwnFeet(in StepFacts s, float now)
    {
        if (s.Body.IsNull || !float.IsFinite(s.At.X) || !float.IsFinite(s.At.Y) || !float.IsFinite(s.At.Z)) return false;
        Vector2 h = Heading(s.Facing);
        Span<Foot> feet = stackalloc Foot[FeetMax];
        int n = ReadFeet(s.Body, h, feet);
        if (n == 0) return false;
        if (!Footfall(s.Id, now, out _)) return true;
        Plant(feet[..n], Vector2.Dot(new Vector2(s.At.X, s.At.Y), h), s.At.Z);
        bool pressed = false, splashed = false;
        foreach (ref readonly Foot f in feet[..n])
        {
            if (!f.Down || !s.Open) continue;
            Vector2 at = h * f.Along + new Vector2(h.Y, -h.X) * f.Across;
            pressed |= PressInto(at, h, now, PrintShape.Pad(f.Len, f.Wide));
            if (!splashed) { ThrowSplash(new Vector3(at, f.Low), now); splashed = true; }
        }
        if (pressed) _prints++;
        return true;
    }

    /// <summary>The body's feet into <paramref name="feet"/>: its rig's two foot parts when the rig names both, else
    /// every unskinned mesh part none of whose children carries a mesh. Returns how many.</summary>
    private static int ReadFeet(DbId body, Vector2 h, Span<Foot> feet)
    {
        nint shell = Seam.Facade.ObjectShell(body);
        var bones = default(PoseService.Bones);
        if (shell != 0) PoseRig.FindBones(body, RigMap.Of(shell, -1), ref bones);
        int n = 0;
        if (bones.RFoot != 0 && bones.LFoot != 0)
        {
            if (Measure(PoseRig.Index(bones.RFoot), h, out feet[n])) n++;
            if (Measure(PoseRig.Index(bones.LFoot), h, out feet[n])) n++;
            if (n == 2) return n;
            n = 0;
        }
        int first = SceneDb.ObjPartFirst[body.Index], count = SceneDb.ObjPartCount[body.Index];
        for (int p = first; p < first + count && n < feet.Length; p++)
            if (IsLeafMesh(p) && Measure(p, h, out feet[n])) n++;
        return n;
    }

    /// <summary>Whether part <paramref name="p"/> carries an unskinned mesh and no child of its node carries one.</summary>
    private static bool IsLeafMesh(int p)
    {
        ModelDef? m = SceneDb.PartModel[p];
        int node = SceneDb.PartNode[p];
        if (m == null || (uint)node >= (uint)m.Nodes.Length) return false;
        NodeDef d = m.Nodes[node];
        if (d.Mesh == null || d.Skin != null) return false;
        foreach (int k in d.Children)
            if ((uint)k < (uint)m.Nodes.Length && m.Nodes[k].Mesh != null) return false;
        return true;
    }

    /// <summary>Part <paramref name="p"/>'s mesh bounds in the world, in heading <paramref name="h"/>'s frame.</summary>
    private static bool Measure(int p, Vector2 h, out Foot f)
    {
        f = default;
        ModelDef? m = SceneDb.PartModel[p];
        int node = SceneDb.PartNode[p];
        if (m == null || (uint)node >= (uint)m.Nodes.Length || m.Nodes[node].Mesh is not { } mesh) return false;
        Matrix4x4 w = DbAnim.PartWorld(p);
        float low = float.MaxValue, aLo = float.MaxValue, aHi = float.MinValue, cLo = float.MaxValue, cHi = float.MinValue;
        for (int k = 0; k < 8; k++)
        {
            var corner = new Vector3((k & 1) != 0 ? mesh.BMax.X : mesh.BMin.X, (k & 2) != 0 ? mesh.BMax.Y : mesh.BMin.Y,
                                     (k & 4) != 0 ? mesh.BMax.Z : mesh.BMin.Z);
            Vector3 v = Vector3.Transform(corner, w);
            float along = v.X * h.X + v.Y * h.Y, across = h.Y * v.X - h.X * v.Y;
            low = MathF.Min(low, v.Z);
            aLo = MathF.Min(aLo, along); aHi = MathF.Max(aHi, along);
            cLo = MathF.Min(cLo, across); cHi = MathF.Max(cHi, across);
        }
        if (!float.IsFinite(low) || !float.IsFinite(aHi - aLo) || !float.IsFinite(cHi - cLo)) return false;
        f = new Foot
        {
            Along = (aLo + aHi) * 0.5f, Across = (cLo + cHi) * 0.5f, Low = low,
            Len = MathF.Max((aHi - aLo) * 0.5f, PadMinHalf), Wide = MathF.Max((cHi - cLo) * 0.5f, PadMinHalf),
        };
        return true;
    }

    /// <summary>Marks the planted feet. The ground is a line through the creature's centre along its heading, at a grade
    /// no foot sinks below: of the grades flat and through each foot, the one that plants most, the flattest on a tie.
    /// ponytail: no slope across the heading is read; read the walkmesh's normal under the creature if one shows.</summary>
    private static void Plant(Span<Foot> feet, float centre, float ground)
    {
        int best = 0;
        float grade = 0f;
        for (int c = -1; c < feet.Length; c++)
        {
            float g = 0f;
            if (c >= 0)
            {
                float run = feet[c].Along - centre;
                if (MathF.Abs(run) < GradeRunMin) continue;
                g = (feet[c].Low - ground) / run;
                if (!(MathF.Abs(g) <= GradeMax)) continue;
            }
            int planted = PlantedCount(feet, centre, ground, g);
            if (planted > best || planted == best && planted > 0 && MathF.Abs(g) < MathF.Abs(grade)) { best = planted; grade = g; }
        }
        for (int i = 0; i < feet.Length; i++)
            feet[i].Down = best > 0 && feet[i].Low - ground - grade * (feet[i].Along - centre) < PlantedTol;
    }

    /// <summary>How many feet stand on the ground line at grade <paramref name="g"/>; 0 when one sinks below it.</summary>
    private static int PlantedCount(ReadOnlySpan<Foot> feet, float centre, float ground, float g)
    {
        int n = 0;
        foreach (ref readonly Foot f in feet)
        {
            float over = f.Low - ground - g * (f.Along - centre);
            if (over < -PlantedTol) return 0;
            if (over < PlantedTol) n++;
        }
        return n;
    }
}
