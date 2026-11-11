using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Perf;

/// <summary>Owns the engine's own per-frame scene walk on an Alloy world frame: <c>ManageSceneBSP</c> and
/// <c>Scene::AddPartsToDrawBuckets</c>, stood down or thinned as <c>alloy.skipbuckets</c> says. The two hooks
/// are installed whatever the timing diagnostic does, and they feed the <see cref="FrameZones.Zone.Bsp"/> and
/// <see cref="FrameZones.Zone.Buckets"/> zones only while <c>alloy.enginezones</c> asks for them.</summary>
public sealed unsafe class SceneWalkService : IExtenderService
{
    private static SceneWalkState _s = new();

    /// <summary>Everything the two trampolines read, in one owner so a decision is replaced rather than
    /// remembered field by field.</summary>
    private sealed class SceneWalkState
    {
        public ManageSceneBSPFn Bsp;
        public AddPartsToDrawBucketsFn Buckets;
        /// <summary>EXPERIMENT: <c>alloy.skipbuckets=1</c> skips the world scene's draw-bucket build while
        /// Alloy draws the frame. It freezes every gob part's transform (the build is the sole writer of the
        /// model matrix Alloy draws with), which is the measurement: Ai drops by the Buckets figure, and what
        /// breaks names the load-bearing half.</summary>
        public bool SkipBuckets;
        /// <summary><c>alloy.skipbuckets=3</c> -- the whole BSP walk stands down on an Alloy world frame. What
        /// it produced for the frame is Alloy's, and the in-view byte is the seam's (<c>ObjectSlots.Visual</c>
        /// answers <c>IsInViewVolume</c> out of the cull pass). The BSP RE-FILING of moved parts and gobs, the
        /// gobbucket and the fade planes are UNOWNED: in an Alloy session nothing in the tree reads them.</summary>
        public bool SkipBsp;
        /// <summary><c>alloy.skipbuckets=2</c>: the six flat loops over the BSP walk's tile-part lists (matrix
        /// rewrite of STATIC parts plus the filing of buckets nothing reads) run every
        /// <see cref="BucketsEvery"/>th frame, the gob and beam loops every frame, by zeroing the lists' counts
        /// ahead of the call. ponytail: a fixed cadence, no knob.</summary>
        public bool BucketsCadence;
        /// <summary>Whether the two zones are timed; <c>alloy.enginezones=0</c> leaves the suppression alone
        /// and takes only the clock reads away.</summary>
        public bool Timed;
        public int BucketsFrame;
        public byte* MeshBuckets, FadeBuckets, CapBuckets, StaticShadowBucket, MeshShadowBucket;
    }

    /// <summary>What one <c>alloy.skipbuckets</c> value asks of the two walks. 3 is the default: both stand
    /// down on an Alloy world frame. 2 keeps <c>ManageSceneBSP</c> and thins only the bucket build's tile
    /// loops (the 2026-09-06 default: Buckets 0.7 -> 0.2-0.3 ms), 0 is the full A/B, and 1 is the old
    /// experiment that empties the world of gob parts.</summary>
    internal readonly record struct Decision(bool SkipBsp, bool SkipBuckets, bool Cadence);

    /// <inheritdoc cref="Decision"/>
    internal static Decision Decide(int skip) => new(skip == 3, skip == 1 || skip == 3, skip == 2);

    private const int BucketsEvery = 8;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        SelfCheck(log);
        var s = new SceneWalkState();
        var d = Decide(Alloy.Renderer.SkipBuckets.Boot);
        s.SkipBsp = d.SkipBsp;
        s.SkipBuckets = d.SkipBuckets;
        s.BucketsCadence = d.Cadence;
        s.Timed = Alloy.Renderer.EngineZones.Boot;
        if (s.BucketsCadence)
        {
            s.MeshBuckets = (byte*)Native.Sym(SymbolMap.MeshBuckets); s.FadeBuckets = (byte*)Native.Sym(SymbolMap.FadeBuckets);
            s.CapBuckets = (byte*)Native.Sym(SymbolMap.CapBuckets); s.StaticShadowBucket = (byte*)Native.Sym(SymbolMap.StaticShadowBucket);
            s.MeshShadowBucket = (byte*)Native.Sym(SymbolMap.MeshShadowBucket);
            string missing = (s.MeshBuckets == null ? " meshbuckets" : "") + (s.FadeBuckets == null ? " fadebuckets" : "")
                           + (s.CapBuckets == null ? " capbuckets" : "") + (s.StaticShadowBucket == null ? " staticshadowbucket" : "")
                           + (s.MeshShadowBucket == null ? " meshshadowbucket" : "");
            if (missing.Length > 0)
            { s.BucketsCadence = false; log.Info($"SceneWalk: alloy.skipbuckets=2 refused -- unresolved:{missing}"); }
        }
        // the owner first: a hook can fire the moment it installs, and a handler reading the old one walks
        // last session's state
        _s = s;
        s.Bsp = hooks.InstallManageSceneBSP(&OnBsp);
        s.Buckets = hooks.InstallAddPartsToDrawBuckets(&OnBuckets);
        if (s.Bsp == null || s.Buckets == null)
            log.Info("SceneWalk: REFUSED -- " + (s.Bsp == null ? "ManageSceneBSP " : "") + (s.Buckets == null ? "AddPartsToDrawBuckets " : "")
                     + "did not hook; the engine's scene walk runs beside Alloy's and alloy.skipbuckets does nothing");
        log.Info($"SceneWalk: alloy.skipbuckets={Alloy.Renderer.SkipBuckets.Boot}" +
                 (s.SkipBsp ? " (default): ManageSceneBSP and AddPartsToDrawBuckets both stand down on an Alloy world frame" : "") +
                 (s.SkipBuckets && !s.SkipBsp ? ": EXPERIMENT -- the world scene's AddPartsToDrawBuckets is SKIPPED while Alloy draws (expect no gob parts)" : "") +
                 (s.BucketsCadence ? $": the tile-part loops of AddPartsToDrawBuckets run every {BucketsEvery}th frame while Alloy draws" : "") +
                 (!s.SkipBsp && !s.SkipBuckets && !s.BucketsCadence ? ": both engine walks run beside Alloy's (the A/B)" : "") +
                 (s.Timed ? " -- timed into [zones]" : " -- untimed (alloy.enginezones=0)"));
    }

    /// <summary>The world scene's render preparation, timed and not subtracted. A GUI 3D panel's Scene has
    /// no tile BSP root and is left out, so the two zones read the world walk alone.</summary>
    [UnmanagedCallersOnly]
    private static long OnBsp(IntPtr scene)
    {
        var s = _s;
        bool world = new CX.Engine.Scene(scene).BspRoot != IntPtr.Zero;
        if (world && s.SkipBsp && CX.Alloy.AlloyGate.Alloy) return 0;
        bool time = world && s.Timed;
        long t = time ? FrameZones.Now : 0;
        long r = s.Bsp != null ? s.Bsp(scene) : 0;
        if (time) FrameZones.Add(FrameZones.Zone.Bsp, t);
        return r;
    }

    [UnmanagedCallersOnly]
    private static long OnBuckets(IntPtr scene)
    {
        var s = _s;
        bool world = new CX.Engine.Scene(scene).BspRoot != IntPtr.Zero;
        // Under 3 the two skips travel together: the bucket build's inputs are the lists the BSP visitor
        // fills, so re-arming the walk without re-arming this would leave an A/B reading half a frame.
        if (world && s.SkipBuckets && CX.Alloy.AlloyGate.Alloy) return 0;
        if (world && s.BucketsCadence && CX.Alloy.AlloyGate.Alloy && (++s.BucketsFrame % BucketsEvery) != 0) ThinTileLists(s);
        bool time = world && s.Timed;
        long t = time ? FrameZones.Now : 0;
        long r = s.Buckets != null ? s.Buckets(scene) : 0;
        if (time) FrameZones.Add(FrameZones.Zone.Buckets, t);
        return r;
    }

    /// <summary>meshbuckets and fadebuckets are <c>List&lt;Bucket&gt;</c> objects in .bss -- CExoArrayList {items at
    /// +0, count at +8, alloc at +0xc} -- whose 16-byte {items, count} pairs live on the HEAP, indexed by texture id
    /// (ClearBuckets @0x47f4e0, CheckBucketArraySize @0x47f420, alloc &gt;= 5000), NOT inline in .bss. Every count on
    /// the heap pairs is cleared, as the engine's own clear does; the three single lists carry their count at +8.</summary>
    private static void ThinTileLists(SceneWalkState s)
    {
        if (s.MeshBuckets == null) return;
        ClearPairCounts(s.MeshBuckets);
        ClearPairCounts(s.FadeBuckets);
        ((ExoArrayRec*)s.CapBuckets)->Count = 0;
        ((ExoArrayRec*)s.StaticShadowBucket)->Count = 0;
        ((ExoArrayRec*)s.MeshShadowBucket)->Count = 0;
    }

    private static void ClearPairCounts(byte* list)
    {
        var head = (ExoArrayRec*)list;
        var pairs = (ExoArrayRec*)head->Items;
        if (pairs == null) return;
        for (int k = 0; k < head->Alloc; k++) pairs[k].Count = 0;
    }

    /// <summary>Oracle: the four <c>alloy.skipbuckets</c> values as the knob's own range spells them, written
    /// out by hand here -- which walk stands down, which is thinned, which runs.</summary>
    private static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "SceneWalk.SelfCheck");
        check.Expect(Decide(0) == new Decision(false, false, false), "0 runs both engine walks: the A/B");
        check.Expect(Decide(1) == new Decision(false, true, false), "1 skips only the bucket build");
        check.Expect(Decide(2) == new Decision(false, false, true), "2 keeps both walks and thins the bucket build's tile loops");
        check.Expect(Decide(3) == new Decision(true, true, false), "3, the default, stands both walks down");
        check.Expect(Decide(Alloy.Renderer.SkipBuckets.Default) == new Decision(true, true, false), "the knob's default is 3");
        log.Info(check.Ok ? "SceneWalk.SelfCheck: passed" : "SceneWalk.SelfCheck: FAILED");
    }
}
