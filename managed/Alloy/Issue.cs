using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;

using static CX.Alloy.AlloyRenderer;

namespace CX.Alloy;

/// <summary>BATCH ISSUE: upload the staged per-draw records and bone snapshots, then submit the built groups
/// as one multidraw per (program, VDO, texture, primitive) cut -- plus the blended and water variants that
/// ride the same records under different GL state. Hot: nothing on this path allocates. An ADR 0014 module,
/// not a partial: it reaches the renderer's tables through <c>using static</c>, the same static load.</summary>
internal static unsafe class Issue
{
    /// <summary>Forget every cached handle, the 4.6 context having gone and taken them with it. Cleared
    /// unconditionally, and nothing is made non-resident -- that would be a call into a dead context.</summary>
    internal static void ForgetHandles()
    {
        InvalidateRecords();   // #242: the persistent records hold the handles this forgets
        Array.Clear(_hTid, 0, _hTid.Length);
        Array.Clear(_hVal, 0, _hVal.Length);
    }

    /// <summary>Forget the cached handles at an enrollment. Only meaningful while the cache holds ENGINE names,
    /// which get recycled behind us; with <c>ownedtex</c> armed the names are Alloy's own and are invalidated
    /// precisely by <c>OwnedTextures.Drain</c> -&gt; <see cref="DropHandle"/>.</summary>
    internal static void WipeHandles()
    {
        if (OwnedTextures.Armed) return;
        InvalidateRecords();   // #242: same reason as ForgetHandles -- a record outlives the wipe otherwise
        Array.Clear(_hTid, 0, _hTid.Length);
    }

    /// <summary>The run of consecutive (or repeated) slots starting at sorted <c>_recDirty[d]</c>; returns
    /// the index past it.</summary>
    private static int NextRun(int[] dirty, int n, int d, out int first, out int last)
    {
        first = dirty[d]; last = first;
        while (++d < n && dirty[d] <= last + 1) last = dirty[d];
        return d;
    }

    /// <summary>The runs <see cref="NextRun"/> walks in the first <paramref name="n"/> of sorted
    /// <paramref name="dirty"/>.</summary>
    private static int CountRuns(int[] dirty, int n)
    {
        int runs = 0;
        for (int d = 0; d < n; runs++) d = NextRun(dirty, n, d, out _, out _);
        return runs;
    }

    /// <summary>The most runs one delta upload sends; past it the whole table goes up in one copy. A delta
    /// costs its copy count, so the budget is in runs, not in a share of the view's draws.</summary>
    // ponytail: a guessed break-even; tune it from the period line's runs.
    internal const int RunBudget = 512;

    /// <summary>A wrong run uploads a stale record over a live slot, silently, so this joins the disarm
    /// chain: duplicates collapse, adjacent slots merge, a gap splits, and a prefix counts only its own runs.
    /// Oracle: the hand-counted runs of the literal lists below.</summary>
    internal static bool RunsSelfCheck(ExtenderLog log)
    {
        int[] dirty = { 5, 3, 4, 9, 3, 11, 10 };
        Array.Sort(dirty);
        int d = NextRun(dirty, dirty.Length, 0, out int f0, out int l0);
        d = NextRun(dirty, dirty.Length, d, out int f1, out int l1);
        int[] three = { 3, 3, 4, 5, 9, 10, 11, 20 };
        bool ok = f0 == 3 && l0 == 5 && f1 == 9 && l1 == 11 && d == dirty.Length
               && CountRuns(three, three.Length) == 3 && CountRuns(three, 4) == 1 && CountRuns(three, 0) == 0;
        if (!ok) log.Info("AlloyRenderer.RunsSelfCheck: FAILED -- the dirty-slot runs do not merge or count as sorted");
        return ok;
    }

    /// <summary>Hand the moved records and this pass's draw order to the Vulkan frame. False = the frame sits out.</summary>
    internal static bool UploadDraws() => VkHandOff(_recOwnCap + MaxSkyDraws);

    /// <summary><see cref="UploadDraws"/> on Vulkan: no GL record table, handle table or stream ring -- the moved
    /// records go to the Vulkan table's dirty list, which uploads them in its own frame.</summary>
    private static bool VkHandOff(int cap)
    {
        bool whole = _recDirtyAll || cap > _recCapGpu;
        if (cap > _recCapGpu) _recCapGpu = cap;
        StaleMirrorRecords(cap, whole, _recDirty, _recDirtyN);
        VkNoteDirty(_recDirty, Math.Min(_recDirtyN, _recDirty.Length), whole);
        _recDirtyN = 0; _recDirtyAll = false; _texHDirty = false;
        return true;
    }

    /// <summary>One period's record-table uploads: the whole ones by reason, the bytes the whole record and handle
    /// tables sent (each one SubData into a buffer the GPU may still be reading), the deltas by their runs, and the
    /// most runs any upload had. <c>EpochSent</c> spans periods: a whole upload on a newer epoch is a restage.</summary>
    internal sealed class UploadState
    {
        internal long Growth, Overflow, Restage, OverBudget, RecordBytes, HandleBytes, Deltas, DeltaRuns;
        internal int MaxRuns, EpochSent;
        internal long Whole => Growth + Overflow + Restage + OverBudget;
    }

    /// <summary>This period's upload tallies; <see cref="ResetPeriod"/> replaces it.</summary>
    internal static UploadState Uploads = new();

    /// <summary>Start a period's tallies, keeping the record epoch the last upload sent.</summary>
    internal static void ResetPeriod() => Uploads = new() { EpochSent = Uploads.EpochSent };

    /// <summary>Did a batch write the depth <see cref="SsaoPass"/> read, and if not, WHICH of the prepass's
    /// exclusions caught it? A batch the prepass skipped must not consume the visibility buffer at all. The
    /// reason travels to the lit stage as the SIGN of <c>uSsaoOn</c> (which tests <c>&gt; 0</c>), so
    /// <c>/alloy viz 35</c> can tell "has no AO" from "CANNOT have AO" on the pixel.</summary>
    internal const int AoIn = 1, AoCutout = -1, AoOutOfPrepass = -2, AoBlended = -3, AoKeyhole = -4;

    /// <summary>Membership is MEASURED (<see cref="_gPrepassed"/>, written by the prepass as it walks); only
    /// the reason a batch is out is inferred, and only to colour <c>/alloy viz 35</c>. Predicting membership
    /// from the class was the bug this replaces.</summary>
    internal static int AoWhy(int g)
        => _gPrepassed[g] ? AoIn
         : g >= _gOpaqueEnd ? AoBlended
         : _gCls[g] == (byte)AlloyClass.Cutout ? AoCutout
         : ShaderFeatures.OutOfPrepass(_gFeat[g]) ? AoOutOfPrepass
         : _gDis[g] ? AoKeyhole
         : AoOutOfPrepass;

    /// <summary>Polygon offset for wash batches: a wash's own depth sits where its unsplit draw put it.</summary>
    internal const float WashOffsetFactor = -1f, WashOffsetUnits = -2f;

    /// <summary>Aurora blend-mode indices (see <see cref="BlendGl"/>) of the one pair OIT accumulates.</summary>
    private const byte AuroraSrcAlpha = 0, AuroraOneMinusSrcAlpha = 1;

    /// <summary>Does a lit, non-water batch draw a content pair's program -- built, and so its halves running?</summary>
    internal static bool HostedBody(bool lit, int feat) => lit && ShaderFeatures.IsContent(feat) && ContentPrograms.AskReady(feat);

    /// <summary>Does a blended batch sit the OIT accumulation out and draw after the resolve, the plain-blend way? A
    /// content pair's program does -- its stage, not Alloy, decides the alpha it blends at -- and weighted blending
    /// represents only the SrcAlpha / OneMinusSrcAlpha pair: a modulating veil would paint its colour.</summary>
    internal static bool SitsOitOut(bool content, int src, int dst)
        => content || src != AuroraSrcAlpha || dst != AuroraOneMinusSrcAlpha;

    /// <summary>One <c>Aurora::AuroraBlendModes</c> value as its GL blend factor -- the engine's own
    /// translation table at 0x12f5040, which <c>GLRender::SetBlendFunc</c> @0x4cb0c0 indexes. Out-of-range
    /// values fall back to the pair the engine's own default is, rather than to whatever GL enum 0 means.</summary>
    internal static uint BlendGl(int aurora) => aurora switch
    {
        0 => GlSrcAlpha,
        1 => GlOneMinusSrcAlpha,
        2 => GlOne,
        3 => GlZero,
        4 => GlDstColor,
        5 => GlSrcColor,
        // Entry 6 is 0x0305, ONE_MINUS_DST_ALPHA -- read out of blendTranslationTable @0x12f5040 rather than
        // inferred from the name beside it.
        6 => GlOneMinusDstAlpha,
        _ => GlSrcAlpha,
    };
}
