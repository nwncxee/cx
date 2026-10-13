using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// The box of the geometry a model draws, read from the model file: the union of its meshes that render, in the model's own space. The engine's
/// cached bounds also cover dummies, emitters and meshes that never draw, which pulls a doll off the middle of its image; a file gives the
/// same answer with Alloy on or off. A model the file cannot be read for is framed from the engine's bounds instead.
/// </summary>
internal static class NuiModelBounds
{
    /// <summary>The most models whose box is kept, so a server naming many models cannot grow the cache.</summary>
    private const int MaxCached = 64;
    private static readonly Dictionary<string, Box?> _cache = new();
    private sealed class BoundsState { public ExtenderLog? Log; }
    private static readonly BoundsState _state = new();

    internal static void Init(ExtenderLog log) => _state.Log = log;

    internal readonly record struct Box(Vector3 Min, Vector3 Max);

    /// <summary>The union of <paramref name="def"/>'s meshes that render and hold geometry, in the model's space: each mesh's own box is in its node's space, so its corners
    /// are carried up through the rest-pose position, turn and scale of the node and every node above it. False when there is none, or a box is not finite.</summary>
    internal static bool MeshBox(ModelDef def, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        bool any = false;
        int count = def.Nodes.Length;
        var at = new Vector3[count];
        var turn = new Quaternion[count];
        var scale = new float[count];
        for (int i = 0; i < count; i++)
        {
            NodeDef n = def.Nodes[i];
            int p = n.Parent;
            if (p >= 0 && p < i)
            {
                scale[i] = scale[p] * n.Scale;
                turn[i] = Quaternion.Concatenate(n.Orientation, turn[p]);
                at[i] = at[p] + Vector3.Transform(n.Position * scale[p], turn[p]);
            }
            else { scale[i] = n.Scale; turn[i] = n.Orientation; at[i] = n.Position; }
            MeshDef? m = n.Mesh;
            if (m == null || !m.Render || m.Positions.Length == 0) continue;
            if (!Finite(m.BMin) || !Finite(m.BMax) || m.BMax.X < m.BMin.X || m.BMax.Y < m.BMin.Y || m.BMax.Z < m.BMin.Z) continue;
            for (int c = 0; c < 8; c++)
            {
                Vector3 w = at[i] + Vector3.Transform(NuiModelFrame.Corner(m.BMin, m.BMax, c) * scale[i], turn[i]);
                min = Vector3.Min(min, w);
                max = Vector3.Max(max, w);
            }
            any = true;
        }
        return any;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>The mesh box of the model named <paramref name="resref"/>, read from the game's files once and kept. False when the model cannot be found or read, or has no mesh.</summary>
    internal static bool Of(string resref, out Vector3 min, out Vector3 max)
    {
        min = max = default;
        if (_cache.TryGetValue(resref, out Box? hit))
        {
            if (hit is not { } b) return false;
            min = b.Min; max = b.Max;
            return true;
        }
        Box? box = null;
        object? token = CX.Alloy.ModelFeed.Locate(resref, out _);
        ModelDef? def = token == null ? null : CX.Alloy.ModelFeed.ParseToken(token, out _);
        if (def != null && MeshBox(def, out Vector3 lo, out Vector3 hi)) box = new Box(lo, hi);
        if (def != null) _state.Log?.Info($"NuiModelBounds: '{resref}' {def.Nodes.Length} nodes, box {(box is { } bb ? $"{bb.Min} {bb.Max}" : "none")}");
        if (_cache.Count < MaxCached) _cache[resref] = box;
        if (box is not { } r) return false;
        min = r.Min; max = r.Max;
        return true;
    }

    /// <summary>Oracle: a model written out by hand, two meshes that draw, one that does not, a dummy and an emitter with no mesh, and one with an
    /// empty mesh; the box is the two drawn meshes'.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        var def = new ModelDef
        {
            Nodes = new[]
            {
                new NodeDef { Name = "root" },
                new NodeDef { Name = "a", Mesh = Mesh(new Vector3(-1, -2, 0), new Vector3(1, 2, 3)) },
                new NodeDef { Name = "b", Mesh = Mesh(new Vector3(0, 0, -1), new Vector3(4, 1, 2)) },
                new NodeDef { Name = "hidden", Mesh = Mesh(new Vector3(-50, -50, -50), new Vector3(50, 50, 50), render: false) },
                new NodeDef { Name = "empty", Mesh = new MeshDef { Render = true, BMin = new Vector3(-9), BMax = new Vector3(9) } },
                new NodeDef { Name = "flipped", Mesh = Mesh(new Vector3(5, 5, 5), new Vector3(-5, -5, -5)) },
                new NodeDef { Name = "nan", Mesh = Mesh(new Vector3(float.NaN, 0, 0), new Vector3(1, 1, 1)) },
            },
        };
        check.Expect(MeshBox(def, out Vector3 min, out Vector3 max) && min == new Vector3(-1, -2, -1) && max == new Vector3(4, 2, 3),
                     "the box is the meshes that draw: not the hidden one, the empty one, a flipped or a non-finite box, or a node with no mesh");
        var moved = new ModelDef
        {
            Nodes = new[]
            {
                new NodeDef { Name = "root", Index = 0 },
                new NodeDef { Name = "hip", Index = 1, Parent = 0, Position = new Vector3(0, 0, 1), Scale = 2f },
                new NodeDef { Name = "body", Index = 2, Parent = 1, Position = new Vector3(1, 0, 0), Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
                              Mesh = Mesh(new Vector3(-1, -0.5f, -1), new Vector3(1, 0.5f, 1)) },
            },
        };
        check.Expect(MeshBox(moved, out min, out max) && Near(min, new Vector3(1f, -2f, -1f)) && Near(max, new Vector3(3f, 2f, 3f)),
                     "a mesh box in its node's space is carried up the node tree: hip at z 1 scaled 2, body at x 1 turned a quarter about z: a 2 by 1 by 2 mesh doubled and turned is 2 wide in x and 4 in y, centred at (2, 0, 1)");
        check.Expect(!MeshBox(new ModelDef { Nodes = new[] { new NodeDef { Name = "dummy" } } }, out _, out _) && !MeshBox(new ModelDef(), out _, out _), "a model with no mesh has no box");

        static bool Near(Vector3 a, Vector3 b) => (a - b).Length() < 1e-4f;
        static MeshDef Mesh(Vector3 lo, Vector3 hi, bool render = true)
            => new() { Render = render, Positions = new[] { lo, hi }, BMin = lo, BMax = hi };
    }
}
