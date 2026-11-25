using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Services.Anim;

namespace CX.Alloy;

/// <summary>A footfall read off the creature's own body: its feet are the rig's foot parts where its rig names
/// them, else its leaf mesh parts and each bone's share of its skins; each foot planted on the ground at that moment
/// presses a print its own size.</summary>
internal static unsafe partial class Weather
{
    /// <summary>Feet read off one body, at most: a skin's every bone slot may be one.</summary>
    private const int FeetMax = AlloyRenderer.BoneCount;
    /// <summary>Metres over the ground line under which a foot is planted: a lifted paw and a tail held 3 cm up clear it.</summary>
    private const float PlantedTol = 0.025f;
    /// <summary>The planted feet a grade must reach before a flatter one is taken over one that plants more.</summary>
    private const int StanceFeet = 2;
    /// <summary>The steepest ground a stride is read on, rise over run along the heading.</summary>
    private const float GradeMax = 1f;
    /// <summary>Metres along the heading a foot must sit off the centre to set the ground's grade.</summary>
    private const float GradeRunMin = 0.05f;
    /// <summary>The smallest half-size a pad is pressed at, so a leg tip still shows at the field's texel.</summary>
    private const float PadMinHalf = 0.03f;

    /// <summary>Points gathered in the heading's frame: their lowest height and their spans along and across.</summary>
    private struct Span3
    {
        public float Low, ALo, AHi, CLo, CHi;

        /// <summary>Nothing gathered yet.</summary>
        public static Span3 Empty => new() { Low = float.MaxValue, ALo = float.MaxValue, AHi = float.MinValue, CLo = float.MaxValue, CHi = float.MinValue };

        /// <summary>Takes in world point <paramref name="v"/>, seen along heading <paramref name="h"/>.</summary>
        public void Add(Vector3 v, Vector2 h)
        {
            float along = v.X * h.X + v.Y * h.Y, across = h.Y * v.X - h.X * v.Y;
            Low = MathF.Min(Low, v.Z);
            ALo = MathF.Min(ALo, along); AHi = MathF.Max(AHi, along);
            CLo = MathF.Min(CLo, across); CHi = MathF.Max(CHi, across);
        }

        /// <summary>The foot these points make; false when none were gathered or one was not finite.</summary>
        public readonly bool ToFoot(out Foot f)
        {
            f = default;
            if (!float.IsFinite(Low) || !float.IsFinite(AHi - ALo) || !float.IsFinite(CHi - CLo)) return false;
            f = new Foot
            {
                Along = (ALo + AHi) * 0.5f, Across = (CLo + CHi) * 0.5f, Low = Low,
                Len = MathF.Max((AHi - ALo) * 0.5f, PadMinHalf), Wide = MathF.Max((CHi - CLo) * 0.5f, PadMinHalf),
            };
            return true;
        }
    }

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
    /// every unskinned mesh part none of whose children carries a mesh, and each skinned part's bone slots. Returns how many.</summary>
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
            if (IsLeafMesh(p)) { if (Measure(p, h, out feet[n])) n++; }
            else n += MeasureSkin(p, h, feet[n..]);
        return n;
    }

    /// <summary>Part <paramref name="p"/>'s skin as feet: each bone slot's vertices, those it weighs most, posed by the
    /// part's palette into the world. None for an unskinned part or a skin binding no bones. Returns how many.</summary>
    private static int MeasureSkin(int p, Vector2 h, Span<Foot> feet)
    {
        ModelDef? m = SceneDb.PartModel[p];
        int node = SceneDb.PartNode[p];
        if (m == null || (uint)node >= (uint)m.Nodes.Length || m.Nodes[node].Mesh is not { } mesh) return 0;
        if (DbAnim.PartSkin(p) is not { } skin) return 0;
        BonePalette pal = default;
        if (!DbAnim.SkinPalette(p, &pal)) return 0;
        ReadOnlySpan<Matrix4x4> bones = pal;
        Matrix4x4 w = DbAnim.PartWorld(p);
        Span<Span3> slots = stackalloc Span3[AlloyRenderer.BoneCount];
        slots.Fill(Span3.Empty);
        int count = Math.Min(mesh.Positions.Length, Math.Min(skin.Weights.Length, skin.Bones.Length));
        for (int i = 0; i < count; i++)
        {
            int slot = HeaviestSlot(skin.Weights[i], skin.Bones[i]);
            if (slot < 0 || slot >= bones.Length) continue;
            slots[slot].Add(Vector3.Transform(skin.Skin(i, mesh.Positions[i], bones), w), h);
        }
        int n = 0;
        foreach (ref readonly Span3 s in slots)
            if (n < feet.Length && s.ToFoot(out feet[n])) n++;
        return n;
    }

    /// <summary>The bone slot of the largest of a vertex's four weights; -1 when it weighs none.</summary>
    private static int HeaviestSlot(Vector4 weight, uint slots)
    {
        int best = -1;
        float top = 0f;
        for (int k = 0; k < 4; k++)
            if (weight[k] > top) { top = weight[k]; best = (int)((slots >> (8 * k)) & 0xFF); }
        return best;
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
        Span3 s = Span3.Empty;
        for (int k = 0; k < 8; k++)
            s.Add(Vector3.Transform(new Vector3((k & 1) != 0 ? mesh.BMax.X : mesh.BMin.X, (k & 2) != 0 ? mesh.BMax.Y : mesh.BMin.Y,
                                                (k & 4) != 0 ? mesh.BMax.Z : mesh.BMin.Z), w), h);
        return s.ToFoot(out f);
    }

    /// <summary>Marks the planted feet. The ground is a line through the creature's centre along its heading, at a grade
    /// no foot sinks below: of the grades flat and through each foot, the flattest that plants as many as any, up to two.
    /// ponytail: no slope across the heading is read; read the walkmesh's normal under the creature if one shows.</summary>
    private static void Plant(Span<Foot> feet, float centre, float ground)
    {
        int most = 0;
        for (int c = -1; c < feet.Length; c++)
            if (Grade(feet, c, centre, ground, out float g)) most = Math.Max(most, PlantedCount(feet, centre, ground, g));
        int need = Math.Min(most, StanceFeet);
        bool found = false;
        float grade = 0f;
        for (int c = -1; c < feet.Length && need > 0; c++)
            if (Grade(feet, c, centre, ground, out float g) && PlantedCount(feet, centre, ground, g) >= need
                && (!found || MathF.Abs(g) < MathF.Abs(grade))) { found = true; grade = g; }
        for (int i = 0; i < feet.Length; i++)
            feet[i].Down = found && feet[i].Low - ground - grade * (feet[i].Along - centre) < PlantedTol;
    }

    /// <summary>The grade the ground line takes through foot <paramref name="c"/>, flat for -1; false for a foot too near
    /// the centre to set one, or a grade steeper than <see cref="GradeMax"/>.</summary>
    private static bool Grade(ReadOnlySpan<Foot> feet, int c, float centre, float ground, out float g)
    {
        g = 0f;
        if (c < 0) return true;
        float run = feet[c].Along - centre;
        if (MathF.Abs(run) < GradeRunMin) return false;
        g = (feet[c].Low - ground) / run;
        return MathF.Abs(g) <= GradeMax;
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
