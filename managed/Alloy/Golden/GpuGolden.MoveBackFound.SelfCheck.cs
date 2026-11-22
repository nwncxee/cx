using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>Found tests on the images a move back to device memory starts over: each owner that keeps one past a frame
/// remakes or drops what it held. Oracle: the owner's own validity flag after a real move on this device.</summary>
internal static partial class GpuGolden
{
    /// <summary>Side of every image these tests move; the move remakes it at its own size, so any size serves.</summary>
    private const int MovedSide = 8;

    /// <summary>The found move-back tests, under a name of their own; a failure fails <paramref name="suite"/> too.</summary>
    private static void MoveBackFoundSelfCheck(VkBackend vk, Checker suite, ExtenderLog log)
    {
        var check = new Checker(log, "GpuGolden.MoveBackFound");
        const ImageUse Colour = ImageUse.Sampled | ImageUse.ColorTarget;
        const ImageUse Store = ImageUse.Sampled | ImageUse.Storage;
        MovedOwner(vk, check, "reflection history", Colour, 2, (i, m) => AlloyRenderer.VkSsrDropsForCheck(vk, i[0], i[1], m));
        MovedOwner(vk, check, "occlusion history", Colour, 2, (i, m) => SsaoPassVk.DropsForCheck(vk, i[0], i[1], m));
        MovedOwner(vk, check, "highlight history", Colour, 2, (i, m) => AlloyRenderer.VkHlDropsForCheck(vk, i[0], i[1], m));
        MovedOwner(vk, check, "sky coverage", Colour, 2, (i, m) => PostStack.CoverDropsForCheck(vk, i[0], i[1], m));
        MovedOwner(vk, check, "probe cube", Store, 1, (i, m) => AlloyRenderer.VkProbeRevokesForCheck(vk, i[0], m));
        MovedOwner(vk, check, "sky map", Colour, 1, (i, m) => AlloyRenderer.VkSkyDropsForCheck(vk, i[0], m));
        MovedOwner(vk, check, "fog volumes", Store, 4, (i, m) => AlloyRenderer.VkFogDropsForCheck(vk, i[0], i[1], i[2], i[3], m));
        MovedOwner(vk, check, "hi-Z pyramid", Store, 1, (i, m) => CullPassVk.DropsForCheck(vk, i[0], m));
        MovedOwner(vk, check, "content stage", Store, 2, (i, m) => StageResources.VkRemakesForCheck(vk, i[0], i[1], m));
        WeatherMapsMoved(vk, check);
        MoveSeenOnce(vk, check);
        if (!check.Ok) suite.Ok = false;
        log.Info(check.Ok ? "GpuGolden.MoveBackFound: passed" : "GpuGolden.MoveBackFound: FAILED");
    }

    // Given an owner holding n new images it keeps past a frame, latched once
    // When a frame passes with no move, or a frame in which one of its images moves back to device memory
    // Then it keeps what it holds after the first, and drops or remakes it after the second, whichever image moved
    private static void MovedOwner(VkBackend vk, Checker check, string owner, ImageUse use, int n, Func<ImageId[], Action, bool> drops)
    {
        for (int at = -1; at < n; at++)
        {
            ImageId[]? ids = MoveImages(vk, check, use, n, owner);
            if (ids == null) return;
            try
            {
                int k = at;
                bool moved = false;
                bool dropped = drops(ids, () => moved = k >= 0 && vk.MoveBackForCheck(ids[k]));
                if (k < 0) { check.Expect(!dropped, $"move back {owner}: dropped with no image moved"); continue; }
                check.Expect(moved && vk.MovesOf(ids[k]) == 1, $"move back {owner}: image {k} did not move back");
                check.Expect(!moved || dropped, $"move back {owner}: kept what it held after image {k} moved back");
            }
            finally
            {
                foreach (ImageId i in ids) vk.Destroy(i);
            }
        }
    }

    // Given the weather's moment pair, wetness map, shelter cache and noise tile, new and latched
    // When a frame passes with no move, or one of them moves back to device memory
    // Then nothing is owed after the first, and after the second exactly that map is owed again, the noise tile let go
    private static void WeatherMapsMoved(VkBackend vk, Checker check)
    {
        const ImageUse Use = ImageUse.Sampled | ImageUse.Storage | ImageUse.ColorTarget;
        (int Image, (bool, bool, bool, bool) Owed)[] cases =
        [
            (-1, (false, false, false, false)), (0, (true, false, false, false)), (1, (true, false, false, false)),
            (2, (false, true, false, false)), (3, (false, false, true, false)), (4, (false, false, false, true)),
        ];
        foreach ((int at, (bool, bool, bool, bool) owed) in cases)
        {
            ImageId[]? ids = MoveImages(vk, check, Use, 5, "weather");
            if (ids == null) return;
            try
            {
                bool moved = false;
                var got = AlloyRenderer.VkWxLosesForCheck(vk, ids[0], ids[1], ids[2], ids[3], ids[4],
                                                          () => moved = at >= 0 && vk.MoveBackForCheck(ids[at]));
                check.Expect(moved == (at >= 0), $"move back weather: case {at} moved back {moved}");
                check.Expect(got == owed, $"move back weather: case {at} owed {got}, want {owed}");
                check.Expect(at != 4 || vk.MovesOf(ids[4]) == 0, "move back weather: the moved noise tile is still a live image");
            }
            finally
            {
                foreach (ImageId i in ids) vk.Destroy(i);
            }
        }
    }

    // Given two images, one of which moved back and whose move an owner has seen
    // When the owner asks again with no further move, and then after the other image moves
    // Then the first ask finds nothing new and the second finds the move
    private static void MoveSeenOnce(VkBackend vk, Checker check)
    {
        ImageId[]? ids = MoveImages(vk, check, ImageUse.Sampled | ImageUse.ColorTarget, 2, "seen once");
        if (ids == null) return;
        try
        {
            int seen = 0;
            bool moved = vk.MoveBackForCheck(ids[0]);
            bool first = vk.MovedBack(ref seen, ids[0], ids[1]);
            bool again = vk.MovedBack(ref seen, ids[0], ids[1]);
            bool other = vk.MoveBackForCheck(ids[1]);
            bool later = vk.MovedBack(ref seen, ids[0], ids[1]);
            check.Expect(moved && other, "move back seen once: an image did not move back");
            check.Expect(first && !again && later, $"move back seen once: seen {first}, again {again}, after the other {later}");
        }
        finally
        {
            foreach (ImageId i in ids) vk.Destroy(i);
        }
    }

    /// <summary><paramref name="n"/> new images of <paramref name="use"/>; null (failed on <paramref name="check"/>) when one was refused.</summary>
    private static ImageId[]? MoveImages(VkBackend vk, Checker check, ImageUse use, int n, string owner)
    {
        var ids = new ImageId[n];
        for (int k = 0; k < n; k++) ids[k] = MoveImage(vk, use, $"golden moveback {owner} {k}");
        if (!Array.Exists(ids, i => i.IsNull)) return ids;
        check.Expect(false, $"move back {owner}: an image was refused");
        foreach (ImageId i in ids) vk.Destroy(i);
        return null;
    }

    /// <summary>One small image of <paramref name="use"/> to move back.</summary>
    private static ImageId MoveImage(VkBackend vk, ImageUse use, string label)
        => vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, MovedSide, MovedSide, 1, 1, 1, 1, use, label));
}
