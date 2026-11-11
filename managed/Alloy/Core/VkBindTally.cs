using System;
using System.Text;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Which part of the Vulkan frame asked for a descriptor set. The split exists because the frame's
/// bind count is one number and the work behind a bind is not the same in each part: the colour path stages a
/// program's whole loose block per bind, a compute dispatch binds two buffers.</summary>
internal enum VkBindBy
{
    /// <summary>The prepass, the G-buffer and the opaque, cutout and blended issue loops.</summary>
    Colour,
    /// <summary>Sky, grass, weather, particles, halos, beams, the probe, the lightmap bake and the motion sweeps.</summary>
    Effects,
    /// <summary>The caster bake and its gather.</summary>
    Casters,
    /// <summary>CX's own windows and the vanilla GUI on the Vulkan path.</summary>
    Gui,
    /// <summary>The cull, the clusters, the deferred walk, AO and the depth pyramid.</summary>
    Compute,
    /// <summary>The post chain, the OIT resolve, reflections, glow and the reactive mask.</summary>
    Post,
}

/// <summary>
/// Set binds per frame, by the part of the frame that asked (<see cref="VkBindBy"/>), and how many of the
/// colour path's binds re-staged a loose block rather than reusing the slice already in the frame's arena.
/// Bumped on the game thread inside the recording, so a count is one increment into a preallocated array and
/// nothing here allocates; the [alloy] line reads it once a period and zeroes it.
/// </summary>
internal static class VkBindTally
{
    /// <summary>One counter per <see cref="VkBindBy"/>; <see cref="SelfCheck"/> pins this to the enum.</summary>
    internal const int Classes = (int)VkBindBy.Post + 1;

    private static readonly long[] _by = new long[Classes];
    private static readonly string[] Names = ["colour", "effects", "casters", "gui", "compute", "post"];
    /// <summary>The colour path's loose blocks staged, and the refills that came out as the slice already staged.</summary>
    private static readonly long[] _loose = new long[2];
    private const int Staged = 0, Reused = 1;

    internal static void Count(VkBindBy by) => _by[(int)by]++;
    internal static void CountStaged() => _loose[Staged]++;
    internal static void CountReused() => _loose[Reused]++;

    /// <summary>The [alloy] line's bind clause: binds per frame by class, then the colour path's loose-block
    /// staging. Cold, once a period.</summary>
    internal static string Note(int frames)
    {
        float f = Math.Max(frames, 1);
        long all = 0;
        for (int i = 0; i < Classes; i++) all += _by[i];
        var sb = new StringBuilder($"set binds {all / f:F0}/frame (");
        for (int i = 0; i < Classes; i++) sb.Append(i == 0 ? "" : " ").Append($"{Names[i]} {_by[i] / f:F0}");
        sb.Append($"), colour loose blocks {_loose[Staged] / f:F0} staged, {_loose[Reused] / f:F0} reused");
        return sb.ToString();
    }

    internal static void ResetPeriod() { Array.Clear(_by); Array.Clear(_loose); }

    /// <summary>The counters against hand-made increments. Oracle: the literal counts below, and the enum's
    /// own length against the array the counters live in.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "VkBindTally.SelfCheck");
        check.Expect(Classes == Enum.GetValues<VkBindBy>().Length && Names.Length == Classes,
          $"one counter and one name per class: {Classes} for the enum's {Enum.GetValues<VkBindBy>().Length} and {Names.Length} names");
        long[] was = (long[])_by.Clone();
        long[] wasLoose = (long[])_loose.Clone();
        ResetPeriod();
        for (int i = 0; i < 8; i++) Count(VkBindBy.Colour);
        Count(VkBindBy.Gui); Count(VkBindBy.Gui);
        Count(VkBindBy.Compute); Count(VkBindBy.Compute);
        CountStaged(); CountStaged(); CountReused(); CountReused();
        string note = Note(2);
        check.Expect(note.Contains("set binds 6/frame (colour 4 effects 0 casters 0 gui 1 compute 1 post 0)"),
          $"twelve binds over two frames split four colour and one each gui and compute per frame: {note}");
        check.Expect(note.Contains("colour loose blocks 1 staged, 1 reused"),
          $"the loose-block clause carries the two stagings and the two reuses, per frame: {note}");
        ResetPeriod();
        check.Expect(Note(1).Contains("set binds 0/frame") && Note(1).Contains("0 staged, 0 reused"),
          $"a reset starts the next period from zero: {Note(1)}");
        Array.Copy(was, _by, Classes);
        Array.Copy(wasLoose, _loose, _loose.Length);
        log.Info(check.Ok ? "VkBindTally.SelfCheck: passed" : "VkBindTally.SelfCheck: FAILED -- see above");
    }
}
