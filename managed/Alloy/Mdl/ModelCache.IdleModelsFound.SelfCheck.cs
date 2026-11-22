using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy.Mdl;

/// <summary>Found tests on "held by idle models", over the approved examples' fixture: the paths the mutation pass
/// showed no example reached. Oracles: texture bytes written here, and the MiB they read as.</summary>
public static partial class ModelCache
{
    /// <summary>The found suite, reported apart from the approved one; leaves the cache as it found it.</summary>
    internal static bool IdleModelsFoundSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "ModelCache.IdleModelsFound.SelfCheck");
        IModelSource? had = _source;
        long? hadBudget = BudgetOverride;
        var fx = new Fixture();
        var tex = new List<string>();
        try
        {
            Clear();
            _source = fx;
            BudgetOverride = long.MaxValue;
            MapSlotCounts(check, fx, tex);
            Reset(tex);
            ManyHoldersCountOnce(check, tex);
        }
        catch (Exception e) { check.Expect(false, $"threw -- {e}"); }
        finally
        {
            Reset(tex);
            BudgetOverride = hadBudget;
            _source = had;
        }
        log.Info(check.Ok ? "ModelCache.IdleModelsFound.SelfCheck: passed" : "ModelCache.IdleModelsFound.SelfCheck: FAILED");
        return check.Ok;
    }

    // Given an idle model whose mesh binds a map past its diffuse
    // When the next frame line is written
    // Then the map's bytes count toward held by idle models with the diffuse's
    private static void MapSlotCounts(Checker check, Fixture fx, List<string> tex)
    {
        const string Name = "cx_idle_maps";
        var mesh = new MeshDef
        {
            Positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            Indices = [0, 1, 2],
            Textures = ["cx_idle_maps_d", "cx_idle_maps_n", "", ""],
            RenderHintId = 2,
        };
        fx.Models[Name] = new ModelDef
        {
            Name = Name,
            Nodes = [new NodeDef { Name = "root", Index = 0, Kind = NodeKind.Dummy },
                     new NodeDef { Name = "mesh0", Index = 1, Parent = 0, Kind = NodeKind.TriMesh, Mesh = mesh }],
        };
        tex.Add("cx_idle_maps_d");
        tex.Add("cx_idle_maps_n");
        ModelDef? m = Acquire(Name);
        int r = m == null ? -1 : DrawRecipe.Intern(m, 1, plan: false);
        check.Expect(r >= 0, "idle maps: the fixture model with a diffuse and a normal map did not load or intern");
        if (m == null || r < 0) return;
        int bound = 0;
        for (int s = 0; s < DrawRecipe.TexSlots; s++)
        {
            TexId id = DrawRecipe.Tex(r, s);
            if (id.IsNull) continue;
            TexRegistry.Land(id, 642100u + (uint)s, (s == 0 ? 1 : 4) * IdleMiB);
            bound++;
        }
        check.Expect(bound == 2, $"idle maps: the recipe binds {bound} texture(s), not the diffuse and the normal map");
        Release(m);
        string line = IdleLine();
        check.Expect(AlloyRenderer.ClauseMiB(line, "held by idle models") == 5,
                     $"idle maps: an idle model's 1MiB diffuse and 4MiB normal map do not read held by idle models 5 MiB: \"{line}\"");
    }

    // Given one texture more holders reference than the registry has slots, every one of them tallied
    // When the tally is taken
    // Then its bytes count once
    private static void ManyHoldersCountOnce(Checker check, List<string> tex)
    {
        const string Name = "cx_idle_many";
        const int Holders = TexRegistry.MaxSlots + 1;
        TexId id = TexRegistry.Acquire(Name);
        tex.Add(Name);
        try
        {
            TexRegistry.Land(id, 642200u, 2 * IdleMiB);
            for (int i = 1; i < Holders; i++) TexRegistry.Acquire(Name);
            for (int i = 0; i < Holders; i++) TexRegistry.Tally(id);
            long sole = TexRegistry.TakeSoleBytes();
            check.Expect(sole == 2 * IdleMiB, $"idle many: a 2MiB texture tallied by each of its {Holders} holders counts {sole} bytes, not its bytes once");
        }
        finally
        {
            for (int i = 0; i < Holders; i++) TexRegistry.Release(id);
        }
    }
}
