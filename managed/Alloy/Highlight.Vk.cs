using System;
using System.Numerics;
using CX.Alloy.Db;

using static CX.Alloy.AlloyRenderer;

namespace CX.Alloy;

internal static unsafe partial class Highlight
{
    /// <summary>This frame's outline draws for the Vulkan mask: each record's draw index, its root, whether it is a
    /// cutout (tested EQUAL, unbiased), and the roots' colours.</summary>
    private sealed class HlVkState
    {
        public readonly int[] Draw = new int[MaxHlDraws], Root = new int[MaxHlDraws], Grp = new int[MaxHlDraws];
        public readonly bool[] Cut = new bool[MaxHlDraws];
        public readonly float[] Cols = new float[MaxHlRoots * 3];
        public int N, Roots;
    }

    private static readonly HlVkState _vkHl = new();

    /// <summary>Record draws the Vulkan mask takes a frame; past it a highlighted crowd's last parts go unmasked.</summary>
    private const int MaxHlDraws = 4096;

    /// <summary>The outline's draws on Vulkan, gathered off the staged batches: every
    /// highlighted root the source hook claimed and its attached children, matched against the staged batches up to
    /// the blended class's end. A content pair's own depth twin is not on Vulkan, so its parts are not masked; a root
    /// with no masked draw takes no highlight here (the glow is GL's). The draws gathered.</summary>
    internal static int GatherVk(int groups)
    {
        HlVkState s = _vkHl;
        s.N = 0; s.Roots = 0;
        DbId dbScene = AlloyRenderer.DbPassScene;
        int nGobs = Math.Min(SceneFeed.HighlightCount(dbScene), MaxHlRoots);
        if (nGobs == 0 || !Renderer.OutlineHighlight) return 0;
        IntPtr* targets = stackalloc IntPtr[MaxHlTargets];
        byte* targetRoot = stackalloc byte[MaxHlTargets];
        int nT = 0;
        for (int i = 0; i < nGobs; i++)
        {
            SceneFeed.HighlightAt(dbScene, i, out DbId o, out Vector3 c);
            s.Cols[i * 3] = c.X; s.Cols[i * 3 + 1] = c.Y; s.Cols[i * 3 + 2] = c.Z;
            if (nT >= MaxHlTargets || !OwnedHighlightDb(o)) continue;
            targetRoot[nT] = (byte)i;
            targets[nT++] = (IntPtr)o.Value;
            nT = DbAttached(o, (byte)i, targets, targetRoot, nT);
        }
        s.Roots = nGobs;
        int end = groups < _gBlendEnd ? groups : _gBlendEnd;
        for (int g = 0; g < end && s.N < MaxHlDraws; g++)
        {
            if (ShaderFeatures.OutOfPrepass(_gFeat[g])) continue;
            for (int j = _gStart[g]; j < _gStart[g] + _gLen[g] && s.N < MaxHlDraws; j++)
            {
                int slot = _dSlot[j];
                if (!DbSlot(slot)) continue;
                int part = _oDbPart[slot];
                if (SceneDb.PartAlpha[part] <= 0f || AlloyRenderer.DbUntextured(slot)) continue;
                IntPtr gob = (IntPtr)SceneDb.PartObj[part].Value;
                int hit = -1;
                for (int k = 0; k < nT; k++)
                    if (targets[k] == gob) { hit = k; break; }
                if (hit < 0) continue;
                s.Draw[s.N] = j; s.Grp[s.N] = g; s.Root[s.N] = targetRoot[hit]; s.Cut[s.N] = _gCls[g] == (byte)AlloyClass.Cutout;
                s.N++;
            }
        }
        return s.N;
    }

    /// <summary>Gathered draw <paramref name="k"/>: its draw index, its batch, its root's colour and its depth rule.</summary>
    internal static (int Draw, int Group, Vector3 Colour, bool Cutout) VkDraw(int k)
    {
        HlVkState s = _vkHl;
        int r = s.Root[k];
        return (s.Draw[k], s.Grp[k], new Vector3(s.Cols[r * 3], s.Cols[r * 3 + 1], s.Cols[r * 3 + 2]), s.Cut[k]);
    }

    /// <summary>The composite's terms: fill opacity, outline radius in pixels, and the mask's bias toward the viewer.</summary>
    internal const float VkFill = FillAlpha, VkRadius = OutlinePx, VkBiasFactor = MaskOffsetFactor, VkBiasUnits = MaskOffsetUnits;
}
