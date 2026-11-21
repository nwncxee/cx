using System;
using System.Collections.Generic;

namespace CX.Alloy.Db;

/// <summary>A server set or cleared an override, so a name resolves to other files. What CX read and built from the
/// name's <c>.mtr</c> and <c>.txi</c> is read and worked out again, as the engine's own material refresh does; the
/// textures themselves are <see cref="TexRegistry"/>'s. A shadow caster's mask follows both.</summary>
internal static class OverrideReset
{
    /// <summary>Frames between two reads of a <c>.mtr</c> that exists and could not be read.</summary>
    private const int RetryEvery = 15;
    /// <summary>Reads of one such <c>.mtr</c> before the reset gives up on it and the old material stands.</summary>
    private const int RetryLimit = 240;
    /// <summary>Names read again in one frame, so a burst of unreadable files spends a bounded share of it.</summary>
    private const int RetryPerTick = 8;

    /// <summary>The reset's working sets and its unread names, one owner.</summary>
    private sealed class ResetState
    {
        /// <summary>The names whose material was there before the reset or is there now.</summary>
        public readonly HashSet<string> Changed = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The names whose <c>.mtr</c> could not be read, each with the reads it has had.</summary>
        public readonly Dictionary<string, int> Unread = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Due = new();
        public int Frame;
    }

    private static ResetState _s = new();

    /// <summary>Game thread, from the registry's reset edge once its own slots are reloading, outside its lock.
    /// <paramref name="reloaded"/>: the registry reloaded a live slot, whose image every caster's mask follows.</summary>
    internal static void Apply(HashSet<string> names, bool reloaded)
    {
        ResetState s = _s;
        s.Changed.Clear();
        foreach (string name in names)
        {
            Seam.TexSeam.ForgetTxi(name);
            s.Unread.Remove(name);
            Settle(s, name, 0);
        }
        Rederive(s, reloaded);
    }

    /// <summary>Once a frame, from the registry's tick: read again the materials that were due, and follow a watched
    /// mask that landed or failed. Allocation-free, and nothing but two reads on a frame with neither.</summary>
    internal static void Tick()
    {
        ResetState s = _s;
        bool landed = TexRegistry.TakeMaskLanded();
        s.Changed.Clear();
        if (s.Unread.Count > 0 && ++s.Frame % RetryEvery == 0)
        {
            s.Due.Clear();
            foreach (KeyValuePair<string, int> u in s.Unread)
            {
                if (s.Due.Count >= RetryPerTick) break;
                s.Due.Add(u.Key);
            }
            foreach (string name in s.Due)
            {
                int reads = s.Unread[name] + 1;
                s.Unread.Remove(name);
                Settle(s, name, reads);
            }
        }
        Rederive(s, landed);
    }

    /// <summary>Read <paramref name="name"/>'s material again: a change goes on <see cref="ResetState.Changed"/>, a
    /// file that could not be read back on the unread list, until <see cref="RetryLimit"/> reads.</summary>
    private static void Settle(ResetState s, string name, int reads)
    {
        if (MtrDef.Reset(name, out bool pending)) { s.Changed.Add(name); return; }
        if (!pending) return;
        if (reads < RetryLimit) { s.Unread[name] = reads; return; }
        DrawRecipe.Log?.Always($"OverrideReset: '{name}'.mtr still could not be read after {reads} tries -- "
                             + "its parts keep the material they had");
    }

    /// <summary>Work out again everything built from the changed materials, and re-stage the shadow masks when
    /// that, or <paramref name="masks"/>, moved one.</summary>
    private static void Rederive(ResetState s, bool masks)
    {
        if (s.Changed.Count > 0)
        {
            int recipes = DrawRecipe.Refresh(s.Changed), parts = PartTree.Refold(s.Changed);
            Particles.MaterialsReset(s.Changed);
            DrawRecipe.Log?.Info($"OverrideReset: {string.Join(", ", s.Changed)} -- {recipes} recipe(s) and {parts} part(s) "
                               + "take the material as it reads now");
            masks = true;
        }
        if (!masks) return;
        // A standing caster staging keeps the mask it resolved, and a cached shadow the mask it baked.
        CasterPass.ListChanged();
        CasterPass.ShadowInvalidate();
    }
}
