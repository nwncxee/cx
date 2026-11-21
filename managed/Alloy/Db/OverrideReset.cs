using System;
using System.Collections.Generic;

namespace CX.Alloy.Db;

/// <summary>A server set or cleared an override, so a name resolves to other files. What CX read and built from the
/// name's <c>.mtr</c> and <c>.txi</c> is read and worked out again, as the engine's own material refresh does; the
/// textures themselves are <see cref="TexRegistry"/>'s.</summary>
internal static class OverrideReset
{
    /// <summary>The names whose material was there before the reset or is there now.</summary>
    private static readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Game thread, from the registry's reset edge once its own slots are reloading, outside its lock.</summary>
    internal static void Apply(HashSet<string> names)
    {
        _changed.Clear();
        foreach (string name in names)
        {
            Seam.TexSeam.ForgetTxi(name);
            if (MtrDef.Reset(name)) _changed.Add(name);
        }
        if (_changed.Count == 0) return;
        int recipes = DrawRecipe.Refresh(_changed), parts = PartTree.Refold(_changed);
        Particles.MaterialsReset(_changed);
        DrawRecipe.Log?.Info($"OverrideReset: {string.Join(", ", _changed)} -- {recipes} recipe(s) and {parts} part(s) "
                           + "take the material as it reads now");
    }
}
