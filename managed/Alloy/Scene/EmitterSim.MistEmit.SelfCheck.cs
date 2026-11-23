using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Mdl;

namespace CX.Alloy;

internal static unsafe partial class EmitterSim
{
    /// <summary>Seat <paramref name="model"/> with one Fountain emitter per name in <paramref name="nodes"/>, each at its
    /// <paramref name="at"/>, and step them <paramref name="frames"/> frames through the walk's own <c>StepOne</c>, the
    /// ground mist stepped after each. <paramref name="before"/> runs ahead of each frame. Returns each node's live sprites per frame.</summary>
    internal static int[,] MistEmit(string model, string[] nodes, Vector3[] at, int frames, Action<int>? before = null)
    {
        Reset();
        var live = new int[nodes.Length, frames];
        var m = new ModelDef { Name = model };
        var kids = new int[nodes.Length];
        var all = new NodeDef[nodes.Length + 1];
        for (int k = 0; k < nodes.Length; k++)
        {
            kids[k] = k + 1;
            all[k + 1] = EmitterNode(nodes[k], k + 1, "Fountain", false);
            m.NodeByName[nodes[k]] = k + 1;
        }
        all[0] = new NodeDef { Name = "root", Index = 0, Kind = NodeKind.Dummy, Children = kids };
        m.Nodes = all;
        m.NodeByName["root"] = 0;
        DbId obj = SeatBlast(m, out int first);
        if (first < 0) return live;
        _tabN = nodes.Length;
        for (int k = 0; k < nodes.Length; k++)
        {
            int part = first + k + 1;
            SceneDb.PartWorld[part] = Matrix4x4.CreateTranslation(at[k]);
            Emitter e = default;
            FillDb(part, ref e);
            _tab[k] = default;
            _tab[k].Part = part;
            _tab[k].Rng = 12345u + (uint)k;
            Open(ref _tab[k], in e);
        }
        for (int f = 0; f < frames; f++)
        {
            before?.Invoke(f);
            for (int k = 0; k < nodes.Length; k++)
            {
                Emitter e = default;
                if (FillDb(first + k + 1, ref e))
                {
                    e.M = _tab[k].Mut;
                    StepOne(ref _tab[k], ref e, MistFixture.Frame);
                    _tab[k].Mut = e.M;
                }
                live[k, f] = _tab[k].Live;
            }
            GroundMist.Step(MistFixture.Frame, Vector2.Zero);
        }
        UnseatBlast(obj, first, m.Nodes.Length);
        Reset();
        return live;
    }
}
