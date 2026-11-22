using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy.Mdl;

/// <summary>Approved-spec: the frame line's "held by idle models" clause, off-device. Oracles: hand-built fixture
/// models with each texture's bytes written here, and the MiB those bytes read as.</summary>
public static partial class ModelCache
{
    private const long IdleMiB = 1 << 20;

    /// <summary>The suite. Leaves the cache empty and on the source it found, as <see cref="SelfCheck"/> does.</summary>
    internal static bool IdleModelsSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "ModelCache.IdleModelsSelfCheck");
        IModelSource? had = _source;
        long? hadBudget = BudgetOverride;
        var fx = new Fixture();
        var tex = new List<string>();
        try
        {
            Clear();
            _source = fx;
            BudgetOverride = long.MaxValue;
            IdleModelHolds(check, fx, tex);
            Reset(tex);
            SharedTextureNotIdle(check, fx, tex);
            Reset(tex);
            UnusedTextureNotIdle(check, fx, tex);
        }
        catch (Exception e) { check.Expect(false, $"threw -- {e}"); }
        finally
        {
            Reset(tex);
            BudgetOverride = hadBudget;
            _source = had;
        }
        log.Info(check.Ok ? "ModelCache.IdleModelsSelfCheck: passed" : "ModelCache.IdleModelsSelfCheck: FAILED");
        return check.Ok;
    }

    // Given cached models no area uses hold textures
    // When the next frame line is written
    // Then it shows held by idle models and how many MiB
    private static void IdleModelHolds(Checker check, Fixture fx, List<string> tex)
    {
        ModelDef? idle = Drawn(fx, tex, "cx_idle_a", ("cx_idle_a_tex", 3 * IdleMiB));
        check.Expect(idle != null, "idle models: the fixture model with a 3MiB texture did not load or intern");
        if (idle == null) return;
        Release(idle);
        check.Expect(RefsOf(idle) == 0, $"idle models: the released fixture model still holds {RefsOf(idle)} reference(s)");
        string line = IdleLine();
        check.Expect(AlloyRenderer.ClauseMiB(line, "held by idle models") == 3,
                     $"idle models: one cached model no area uses holds a 3MiB texture, and the line does not show held by idle models 3 MiB: \"{line}\"");
    }

    // Given a texture used by an idle model and by one the area draws
    // When the next frame line is written
    // Then it does not count toward held by idle models
    private static void SharedTextureNotIdle(Checker check, Fixture fx, List<string> tex)
    {
        ModelDef? idle = Drawn(fx, tex, "cx_idle_b", ("cx_idle_b_own", 2 * IdleMiB), ("cx_idle_shared", 5 * IdleMiB));
        ModelDef? drawn = Drawn(fx, tex, "cx_idle_drawn", ("cx_idle_shared", 5 * IdleMiB));
        check.Expect(idle != null && drawn != null, "idle models: the two fixture models sharing a texture did not load or intern");
        if (idle == null || drawn == null) return;
        Release(idle);
        check.Expect(RefsOf(idle) == 0 && RefsOf(drawn) == 1, "idle models: the fixture's idle and drawn models do not hold 0 and 1 reference");
        string line = IdleLine();
        check.Expect(AlloyRenderer.ClauseMiB(line, "held by idle models") == 2,
                     $"idle models: the idle model's own 2MiB texture is idle and its 5MiB one the area also draws is not, yet the line does not show held by idle models 2 MiB: \"{line}\"");
        Release(drawn);
    }

    // Given textures no model uses, kept by the cache
    // When the next frame line is written
    // Then they do not count toward held by idle models
    private static void UnusedTextureNotIdle(Checker check, Fixture fx, List<string> tex)
    {
        ModelDef? idle = Drawn(fx, tex, "cx_idle_c", ("cx_idle_c_tex", 1 * IdleMiB));
        check.Expect(idle != null, "idle models: the fixture model with a 1MiB texture did not load or intern");
        if (idle == null) return;
        Release(idle);
        TexId kept = TexRegistry.Acquire("cx_idle_unused");
        tex.Add("cx_idle_unused");
        TexRegistry.Land(kept, 642009u, 7 * IdleMiB);
        TexRegistry.Release(kept);
        check.Expect(TexRegistry.StateOf(kept) == TexState.Idle, $"idle models: the 7MiB texture no model uses is {TexRegistry.StateOf(kept)}, not kept idle by the cache");
        string line = IdleLine();
        check.Expect(AlloyRenderer.ClauseMiB(line, "held by idle models") == 1,
                     $"idle models: only the idle model's 1MiB texture counts, not the 7MiB one no model uses, yet the line does not show held by idle models 1 MiB: \"{line}\"");
    }

    /// <summary>The frame line's memory part through its own writer, on hand-written device figures; the writer reads
    /// what idle models hold itself.</summary>
    private static string IdleLine() => AlloyRenderer.FrameVram(900 * IdleMiB, 2000 * IdleMiB, 8000 * IdleMiB, 0, []);

    /// <summary>Acquire fixture model <paramref name="name"/>, one node per texture, intern each node's recipe and land
    /// each texture at its bytes. Null when the model or a recipe is refused.</summary>
    private static ModelDef? Drawn(Fixture fx, List<string> tex, string name, params (string Tex, long Bytes)[] textures)
    {
        var nodes = new NodeDef[textures.Length + 1];
        nodes[0] = new NodeDef { Name = "root", Index = 0, Kind = NodeKind.Dummy };
        for (int i = 0; i < textures.Length; i++)
            nodes[i + 1] = new NodeDef
            {
                Name = $"mesh{i}", Index = i + 1, Parent = 0, Kind = NodeKind.TriMesh,
                Mesh = new MeshDef
                {
                    Positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                    Indices = [0, 1, 2],
                    Textures = [textures[i].Tex, "", "", ""],
                },
            };
        fx.Models[name] = new ModelDef { Name = name, Nodes = nodes };
        ModelDef? m = Acquire(name);
        if (m == null) return null;
        for (int i = 0; i < textures.Length; i++)
        {
            int r = DrawRecipe.Intern(m, i + 1, plan: false);
            if (r < 0) return null;
            TexId id = DrawRecipe.Tex(r, 0);
            if (!tex.Contains(textures[i].Tex)) tex.Add(textures[i].Tex);
            if (TexRegistry.StateOf(id) != TexState.Resident) TexRegistry.Land(id, 642000u + (uint)tex.Count, textures[i].Bytes);
        }
        return m;
    }

    /// <summary>Every fixture model's recipes and the cache let go, and every fixture texture's idle slot retired.</summary>
    private static void Reset(List<string> tex)
    {
        foreach (Slot s in _table.ToArray()) DrawRecipe.Forget(s.Def);
        Clear();
        DrawRecipe.Pump();
        DrawRecipe.Pump();
        foreach (string t in tex) TexRegistry.Forget(t);
        tex.Clear();
    }
}
