using CX.Framework;
using CX.Rhi;

namespace CX.Alloy;

internal sealed partial class DepthSnapshot
{
    private const int W = 2544, H = 1378;

    /// <summary>The copies the recorded list actually holds, read back out of the command arena rather than from
    /// the snapshot's own count, and the last one's source, destination and block. The walk reads one record shape,
    /// so anything else the list holds is a failure rather than a command to step over.</summary>
    private static int Copied(Checker check, CommandList cl, out CopyImageCmd last)
    {
        last = default;
        int n = 0;
        CommandReader r = cl.TakePending();
        while (r.Next(out Op op))
        {
            check.Expect(op == Op.CopyImage, $"the recorded list holds a {op}, whose record this walk cannot step over");
            if (op != Op.CopyImage) break;
            last = r.Read<CopyImageCmd>();
            n++;
        }
        return n;
    }

    /// <summary>Oracles: hand-written copy counts for a scripted run of writes, snapshots and frame edges, each
    /// counted by walking the commands the list recorded.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DepthSnapshot.SelfCheck");
        var cl = new CommandList("depth snapshot self-check", 4096);
        var depth = new ImageId(11);
        var read = new ImageId(12);
        var snap = new DepthSnapshot();

        check.Expect(!snap.Filled && snap.Stale, "a fresh snapshot is not stale-and-empty, so a frame could sample a copy nobody took");
        check.Expect(snap.Take(cl, depth, read, W, H), "the frame's first snapshot was skipped");
        int n = Copied(check, cl, out CopyImageCmd one);
        check.Expect(n == 1, $"the first snapshot recorded {n} copies, not 1");
        check.Expect(one.Src == depth && one.Dst == read && one.W == W && one.H == H && one.D == 1,
                     $"the copy is not the whole {W}x{H} scene depth into the sampled target: {one}");

        check.Expect(!snap.Take(cl, depth, read, W, H), "a snapshot nothing has written since was taken again");
        n = Copied(check, cl, out _);
        check.Expect(n == 0, $"the reused snapshot still recorded {n} copies");

        snap.Wrote();
        check.Expect(snap.Stale, "a pass wrote the depth and the snapshot did not go stale");
        check.Expect(snap.Take(cl, depth, read, W, H), "a snapshot after a depth write was skipped");
        check.Expect(Copied(check, cl, out _) == 1, "a snapshot after a depth write recorded no copy");

        snap.BeginFrame();
        check.Expect(snap.Stale && snap.Copies == 0, "a new frame opened on last frame's snapshot");
        check.Expect(snap.Take(cl, depth, read, W, H) && Copied(check, cl, out _) == 1, "the next frame's first snapshot was skipped");

        // the frame shape this change is for: four readers, with a depth write only before the second
        snap.BeginFrame();
        snap.Take(cl, depth, read, W, H);
        snap.Wrote();
        snap.Take(cl, depth, read, W, H);
        snap.Take(cl, depth, read, W, H);
        snap.Take(cl, depth, read, W, H);
        check.Expect(snap.Copies == 2, $"four readers past one depth write took {snap.Copies} copies, not 2");
        check.Expect(Copied(check, cl, out _) == 2, "the list holds a different number of copies than the frame counted");
        check.Expect(snap.TookMoreThanEver(), "a frame that took more copies than any before it did not say so");
        check.Expect(snap.High == 2, $"the high water is {snap.High} after a 2-copy frame");
        check.Expect(!snap.TookMoreThanEver(), "the same frame reported its high water twice");

        snap.BeginFrame();
        snap.Take(cl, depth, read, W, H);
        check.Expect(snap.Copies == 1 && snap.High == 2, $"a 1-copy frame left {snap.Copies} copies and a high water of {snap.High}");
        check.Expect(!snap.TookMoreThanEver(), "a frame that took fewer copies than one before it claimed a new high water");
        check.Expect(Copied(check, cl, out _) == 1, "the 1-copy frame recorded a different number of copies than it counted");

        // a target remade at a new size: the old size's high water goes with it, so the frame line names the new one
        snap.Remade();
        check.Expect(!snap.Filled, "a remade target still claims to hold a snapshot");
        check.Expect(snap.High == 0, $"a remade target kept a high water of {snap.High}, so the frame line never restates the count at the new size");
        snap.BeginFrame();
        check.Expect(snap.Take(cl, depth, read, W, H) && Copied(check, cl, out _) == 1,
                     "a snapshot into a remade target was skipped, so a pass would sample an image nothing wrote");
        check.Expect(snap.TookMoreThanEver(), "the first frame at a new size did not restate its copy count");

        log.Info(check.Ok ? "DepthSnapshot.SelfCheck: passed" : "DepthSnapshot.SelfCheck: FAILED");
    }
}
