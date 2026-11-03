using CX.Rhi;

namespace CX.Alloy;

/// <summary>The scene depth's sampled copy and the one rule over it: a pass cannot sample its own depth attachment,
/// so it samples the snapshot, and a reader takes a new copy only once a pass has written depth since the last one.
/// Held by the colour state, which owns the two images.</summary>
internal sealed partial class DepthSnapshot
{
    /// <summary>A copy has been recorded since the target was made: until then it holds nothing, and a stage that
    /// declares it gets the stand-in.</summary>
    public bool Filled { get; private set; }

    /// <summary>A pass has written the scene depth since the last copy, so the snapshot no longer speaks for it.</summary>
    public bool Stale { get; private set; } = true;

    /// <summary>Copies recorded this frame, and the most any frame has recorded.</summary>
    public int Copies { get; private set; }
    public int High { get; private set; }

    /// <summary>A frame opens stale: its prepass clears the depth and draws it again.</summary>
    public void BeginFrame()
    {
        Stale = true;
        Copies = 0;
    }

    /// <summary>The target was remade at a new size, so nothing in it speaks for any depth.</summary>
    public void Remade()
    {
        Filled = false;
        Stale = true;
    }

    /// <summary>A pass wrote the scene depth.</summary>
    public void Wrote() => Stale = true;

    /// <summary>The frame's copy count rose past every frame before it, which is the line worth logging.</summary>
    public bool TookMoreThanEver()
    {
        if (Copies <= High) return false;
        High = Copies;
        return true;
    }

    /// <summary><paramref name="from"/> into <paramref name="to"/>, outside any pass; nothing when the snapshot
    /// standing in <paramref name="to"/> already IS that depth. True when a copy was recorded.</summary>
    public bool Take(CommandList cl, ImageId from, ImageId to, int w, int h)
    {
        if (Filled && !Stale) return false;
        cl.CopyImage(new CopyImageCmd(from, new ImageRegion(0, 0, 0, 0), to, new ImageRegion(0, 0, 0, 0), w, h, 1));
        Filled = true;
        Stale = false;
        Copies++;
        return true;
    }
}
