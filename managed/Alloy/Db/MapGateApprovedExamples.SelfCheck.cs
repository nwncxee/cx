using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>
/// The material maps a mesh with no tangent frame drops (approved-spec, E20, E21): which resref
/// <see cref="DrawRecipe.SlotName"/> binds to the specular and self-illumination slots for a material that names
/// them. Oracle: the <c>.mtr</c> text and the mesh written here, and the stock rule that <c>fslit</c> reads neither
/// map (an empty name is the unbound slot, so the generated highlight and no glow).
/// </summary>
internal static class MapGateApprovedExamples
{
    /// <summary>Recipe slots of <c>texture2</c> (the specular map) and <c>texture5</c> (self-illumination).</summary>
    private const int SpecSlot = 2, IllumSlot = 5;

    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MapGateApprovedExamples.SelfCheck");
        var plain = new Mdl.MeshDef();
        plain.Textures[0] = "wall_d";

        // Given a material naming a specular map and no shader pair, on a model compiled without tangents
        // When it draws
        // Then its highlight is the plain generated one, as if it named no specular map, as stock does
        MtrDef spec = MtrDef.Parse(System.Text.Encoding.ASCII.GetBytes("texture0 wall_d\ntexture2 wall_s\n"), "map gate spec");
        string s = DrawRecipe.SlotName(SpecSlot, spec, plain);
        check.Expect(s == "", $"CX-451 E20: a tangentless mesh binds its material's specular map '{s}'");

        // Given a material naming a self-illumination map and no shader pair, on a model compiled without tangents
        // When it draws in the dark
        // Then it does not glow, as stock does
        MtrDef glow = MtrDef.Parse(System.Text.Encoding.ASCII.GetBytes("texture0 wall_d\ntexture5 wall_g\n"), "map gate glow");
        string g = DrawRecipe.SlotName(IllumSlot, glow, plain);
        check.Expect(g == "", $"CX-451 E21: a tangentless mesh binds its material's self-illumination map '{g}'");
        log.Info(check.Ok ? "MapGateApprovedExamples.SelfCheck: passed" : "MapGateApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }
}
