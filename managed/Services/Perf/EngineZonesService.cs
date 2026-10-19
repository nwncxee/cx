using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Perf;

/// <summary>Splits the [zones] line's <c>other</c> column: four timing hooks on the engine main loop's non-render
/// steps (see <see cref="FrameZones.Zone.Net"/>), each calling the original through its trampoline between two
/// timestamps. The handlers forward the argument register untouched -- one integer or float each, no by-value structs
/// (the mangled names say so) -- and return <c>long</c>, so a void, int or pointer result rides rax intact. Hot, no allocation.</summary>
public sealed unsafe class EngineZonesService : IExtenderService
{
    private static NetProcessReceivedFramesFn _net;
    private static ResManUpdateFn _res;
    private static ClientAiUpdateStateFn _ai;
    private static GuiUpdateAndRenderFn _gui;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        if (!Alloy.Renderer.EngineZones.Boot) { log.Info("EngineZones: off (alloy.enginezones=0)"); return; }
        _net = hooks.InstallNetProcessReceivedFrames(&OnNet);
        _res = hooks.InstallResManUpdate(&OnRes);
        _ai = hooks.InstallClientAiUpdateState(&OnAi);
        _gui = hooks.InstallGuiUpdateAndRender(&OnGui);
        _demand = hooks.InstallResManDemand(&OnDemand);
        _image = hooks.ChainAurGetImageWrapper(&OnImage, ImagePriority);
        _bsp = hooks.InstallManageSceneBSP(&OnBsp);
        _buckets = hooks.InstallAddPartsToDrawBuckets(&OnBuckets);
        // 3 by default: both engine walks stand down on an Alloy world frame. 2 keeps ManageSceneBSP and
        // thins only the bucket build's tile loops (the 2026-09-06 default: Buckets 0,7 -> 0,2-0,3ms), 0 is
        // the full A/B, and 1 is the old experiment that empties the world of gob parts.
        int skip = Alloy.Renderer.SkipBuckets.Boot;
        _skipBuckets = skip == 1 || skip == 3;
        _bucketsCadence = skip == 2;
        // 3 skips the BSP walk as well. Everything it produced for the FRAME is Alloy's (the tile
        // fade ramp, the grass bins, the emitter/trigger/beam lists, the visible-gob list, ProcessGob and the
        // LOD call), and everything it produced for the rest of the CLIENT is CX.Alloy.ScenePrep's (the
        // in-view byte, gobbucket, the three LightManager steps). ScenePrep.Needed is the per-frame veto:
        // any /buckets A/B or validator that wants an engine list re-arms both walks.
        _skipBsp = skip == 3;
        if (_bucketsCadence)
        {
            _meshBuckets = (byte*)Native.Sym(SymbolMap.MeshBuckets); _fadeBuckets = (byte*)Native.Sym(SymbolMap.FadeBuckets);
            _capBuckets = (byte*)Native.Sym(SymbolMap.CapBuckets); _staticShadowBucket = (byte*)Native.Sym(SymbolMap.StaticShadowBucket);
            _meshShadowBucket = (byte*)Native.Sym(SymbolMap.MeshShadowBucket);
            string missing = (_meshBuckets == null ? " meshbuckets" : "") + (_fadeBuckets == null ? " fadebuckets" : "")
                           + (_capBuckets == null ? " capbuckets" : "") + (_staticShadowBucket == null ? " staticshadowbucket" : "")
                           + (_meshShadowBucket == null ? " meshshadowbucket" : "");
            if (missing.Length > 0)
            { _bucketsCadence = false; log.Info($"EngineZones: alloy.skipbuckets=2 refused -- unresolved:{missing}"); }
        }
        log.Info("EngineZones: timing the engine loop's net / resman / AI / GUI steps and resource demands into [zones] (config alloy.enginezones=0 disables)" +
                 (_skipBuckets && !_skipBsp ? " -- EXPERIMENT alloy.skipbuckets=1: the world scene's AddPartsToDrawBuckets is SKIPPED while Alloy draws (expect no gob parts)" : "") +
                 (_bucketsCadence ? $" -- alloy.skipbuckets=2: the tile-part loops of AddPartsToDrawBuckets run every {BucketsEvery}th frame while Alloy draws (alloy.skipbuckets=0 restores them)" : "") +
                 (_skipBsp ? " -- alloy.skipbuckets=3 (default): ManageSceneBSP and AddPartsToDrawBuckets both stand down on an Alloy world frame, and CX.Alloy.ScenePrep owns what they did for the rest of the client (the in-view byte, gobbucket, the LightManager steps); /buckets reports it, and any A/B there re-arms both walks" : ""));
    }

    private static ResManDemandFn _demand;
    private static ManageSceneBSPFn _bsp;
    private static AddPartsToDrawBucketsFn _buckets;

    /// <summary>The world scene's render preparation, timed and not subtracted. A GUI 3D panel's Scene has
    /// no tile BSP root and is left out, so the two zones read the world walk alone.</summary>
    [UnmanagedCallersOnly]
    private static long OnBsp(IntPtr scene)
    {
        bool world = new CX.Engine.Scene(scene).BspRoot != IntPtr.Zero;
        if (world && _skipBsp && CX.Alloy.AlloyGate.Alloy)
        { _bspSkipped++; return 0; }
        long t = world ? FrameZones.Now : 0;
        long r = _bsp != null ? _bsp(scene) : 0;
        if (world) FrameZones.Add(FrameZones.Zone.Bsp, t);
        return r;
    }

    /// <summary>EXPERIMENT: <c>alloy.skipbuckets=1</c> in cx.cfg skips the world scene's draw-bucket build while Alloy
    /// draws the frame. It freezes every gob part's transform (the build is the sole writer of the model matrix Alloy
    /// draws with), which is the measurement: Ai drops by the Buckets figure, and what breaks names the load-bearing
    /// half. Never on a GUI panel's scene and never while the engine renders (<c>alloy.enabled=0</c>).</summary>
    private static bool _skipBuckets;
    private static long _bucketsSkipped;
    /// <summary><c>alloy.skipbuckets=3</c> -- the whole BSP walk stands down on an Alloy world frame. What it produced
    /// is Alloy's: the tile fade ramp, the grass bins, the emitter, trigger and beam lists, the visible-gob list, the
    /// per-gob LOD call and <c>Gob::ProcessGob</c>. Its side effects left with no owner are the BSP RE-FILING of moved
    /// parts and gobs (<c>SceneBSPUpdatePart</c>/<c>Gob</c>, which hit testing walks), the fade planes and <c>gob+0x1b</c>.</summary>
    private static bool _skipBsp;
    private static long _bspSkipped;
    /// <summary><c>alloy.skipbuckets=2</c>: the six flat loops over the BSP walk's tile-part lists (matrix rewrite of
    /// STATIC parts plus the filing of buckets nothing reads) run every <see cref="BucketsEvery"/>th frame, the gob and
    /// beam loops every frame, done by zeroing the lists' counts ahead of the call; a freshly spawned tile's parts
    /// carry no matrix until the next full frame. ponytail: a fixed cadence, no knob.</summary>
    private static bool _bucketsCadence;
    private const int BucketsEvery = 8;
    private static int _bucketsFrame;
    private static byte* _meshBuckets, _fadeBuckets, _capBuckets, _staticShadowBucket, _meshShadowBucket;

    /// <summary>meshbuckets and fadebuckets are <c>List&lt;Bucket&gt;</c> objects in .bss -- CExoArrayList {items at
    /// +0, count at +8, alloc at +0xc} -- whose 16-byte {items, count} pairs live on the HEAP, indexed by texture id
    /// (ClearBuckets @0x47f4e0, CheckBucketArraySize @0x47f420, alloc &gt;= 5000), NOT inline in .bss. Every count on
    /// the heap pairs is cleared, as the engine's own clear does; the three single lists carry their count at +8.</summary>
    private static void ThinTileLists()
    {
        if (_meshBuckets == null) return;
        ClearPairCounts(_meshBuckets);
        ClearPairCounts(_fadeBuckets);
        ((ExoArrayRec*)_capBuckets)->Count = 0;
        ((ExoArrayRec*)_staticShadowBucket)->Count = 0;
        ((ExoArrayRec*)_meshShadowBucket)->Count = 0;
    }

    private static void ClearPairCounts(byte* list)
    {
        var head = (ExoArrayRec*)list;
        var pairs = (ExoArrayRec*)head->Items;
        if (pairs == null) return;
        for (int k = 0; k < head->Alloc; k++) pairs[k].Count = 0;
    }

    [UnmanagedCallersOnly]
    private static long OnBuckets(IntPtr scene)
    {
        bool world = new CX.Engine.Scene(scene).BspRoot != IntPtr.Zero;
        // Under 3 the two skips travel together: the bucket build's inputs are the lists the BSP visitor
        // fills, so re-arming the walk without re-arming this would leave an A/B reading half a frame.
        if (world && _skipBuckets && CX.Alloy.AlloyGate.Alloy) { _bucketsSkipped++; return 0; }
        if (world && _bucketsCadence && CX.Alloy.AlloyGate.Alloy && (++_bucketsFrame % BucketsEvery) != 0) ThinTileLists();
        long t = world ? FrameZones.Now : 0;
        long r = _buckets != null ? _buckets(scene) : 0;
        if (world) FrameZones.Add(FrameZones.Zone.Buckets, t);
        return r;
    }
    private static int _demandDepth;
    /// <summary>Outermost demands since boot: ticks in all, in models, in restypes step 0 does not name, and calls.
    /// Never reset (a load-screen present clears the frame zones mid-<c>LoadArea</c>), so a caller samples deltas.</summary>
    public static long DemandTicks, DemandMdlTicks, DemandOtherTicks, DemandCalls;
    /// <summary>Ticks in texture demands at ANY depth, decode included, never reset. The
    /// <see cref="FrameZones.Zone.TexRead"/> zone is the same bracket minus the decode and is cleared every
    /// frame; a model's parse subtracts this instead, because the texture demands nested in it are the
    /// engine's work under our bracket and not the model parse we are sizing.</summary>
    public static long DemandTexTicks;

    /// <summary>The on-demand resource load, wherever the engine asks for it (a model on first sight, a texture, an
    /// animation). Demands nest -- a model demands its textures -- so only the outermost call is bracketed and counted,
    /// while a texture's demand is bracketed at ANY depth, minus the decode inside it. The restype is read BEFORE the
    /// call, the same answer: neither <c>Demand</c> nor the four <c>ServiceFrom*</c> writes the <c>CRes</c>'s <c>+0x28</c>.</summary>
    [UnmanagedCallersOnly]
    private static long OnDemand(IntPtr self, IntPtr res)
    {
        ushort type = ResType(res);
        bool tex = type == ResTypes.Dds || type == ResTypes.Tga || type == ResTypes.Plt;
        long tr = tex ? FrameZones.Now : 0;
        long dec0 = tex ? FrameZones.Ticks(FrameZones.Zone.TexDecode) : 0;
        long t = _demandDepth++ == 0 ? FrameZones.Now : 0;
        long r = _demand != null ? _demand(self, res) : 0;
        if (tex)
        {
            DemandTexTicks += FrameZones.Now - tr;
            FrameZones.Add(FrameZones.Zone.TexRead, tr, FrameZones.Ticks(FrameZones.Zone.TexDecode) - dec0);
        }
        CX.Alloy.ResSources.Note(res == IntPtr.Zero ? 0 : new CRes(res).SourceKind, tex);   // every depth: a tile's textures nest under its model
        if (--_demandDepth == 0)
        {
            long dt = FrameZones.Now - t;
            DemandTicks += dt; DemandCalls++;
            if (type == ResTypes.Mdl) DemandMdlTicks += dt;
            else if (!tex) DemandOtherTicks += dt;
            FrameZones.Add(FrameZones.Zone.Demand, t);
            FrameZones.Count(FrameZones.Ev.Demands);
            switch (type)
            {
                case ResTypes.Dds: FrameZones.Add(FrameZones.Zone.DemDds, t); FrameZones.Count(FrameZones.Ev.DdsDemands); break;
                case ResTypes.Tga: FrameZones.Add(FrameZones.Zone.DemTga, t); FrameZones.Count(FrameZones.Ev.TgaDemands); break;
                case ResTypes.Plt: FrameZones.Add(FrameZones.Zone.DemPlt, t); FrameZones.Count(FrameZones.Ev.PltDemands); break;
                case ResTypes.Mdl: FrameZones.Add(FrameZones.Zone.DemMdl, t); FrameZones.Count(FrameZones.Ev.MdlDemands); break;
                default: FrameZones.Add(FrameZones.Zone.DemOther, t); break;
            }
        }
        return r;
    }

    /// <summary>A resource's restype, through its key-table entry; 0 when it has none.</summary>
    private static ushort ResType(IntPtr res)
    {
        if (res == IntPtr.Zero) return 0;
        var entry = new CKeyTableEntry(new CRes(res).KeyEntry);
        return entry.IsNull ? (ushort)0 : entry.ResType;
    }

    private static AurGetImageWrapperFn* _image;
    /// <summary>Outermost on the wrapper: the zone times whatever <see cref="Alloy.RasterReader"/> adds under it.</summary>
    private const int ImagePriority = 100;
    private static int _imageDepth;

    /// <summary>One texture's read + decode, outermost call only.</summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnImage(IntPtr name, int* w, int* h, int* bpp, int* size, int* container, int* format, float* alpha, int allow)
    {
        long t = _imageDepth++ == 0 ? FrameZones.Now : 0;
        IntPtr r = (*_image)(name, w, h, bpp, size, container, format, alpha, allow);
        if (--_imageDepth == 0) FrameZones.Add(FrameZones.Zone.Image, t);
        return r;
    }

    [UnmanagedCallersOnly]
    private static long OnNet(IntPtr self, int a)
    {
        long t = FrameZones.Now;
        long r = _net != null ? _net(self, a) : 0;
        FrameZones.Add(FrameZones.Zone.Net, t);
        return r;
    }

    [UnmanagedCallersOnly]
    private static long OnRes(IntPtr self, uint a)
    {
        long t = FrameZones.Now;
        long r = _res != null ? _res(self, a) : 0;
        FrameZones.Add(FrameZones.Zone.Res, t);
        return r;
    }

    /// <summary>The first live read had <c>Ai</c> at 10ms with <c>other</c> at -7: the AI update is where
    /// the engine renders the scene from, so every renderer bracket sits inside it. Their ticks are
    /// subtracted, and what is left is the update itself.</summary>
    [UnmanagedCallersOnly]
    private static long OnAi(IntPtr self)
    {
        long t = FrameZones.Now;
        long inner0 = Inner();
        long r = _ai != null ? _ai(self) : 0;
        FrameZones.Add(FrameZones.Zone.Ai, t, Inner() - inner0);
        return r;
    }

    private static long Inner()
        => FrameZones.Ticks(FrameZones.Zone.Enroll) + FrameZones.Ticks(FrameZones.Zone.Shadow)
           + FrameZones.Ticks(FrameZones.Zone.Shim) + FrameZones.Ticks(FrameZones.Zone.Alloy)
           + FrameZones.Ticks(FrameZones.Zone.Ui) + FrameZones.Ticks(FrameZones.Zone.Present)
           + FrameZones.Ticks(FrameZones.Zone.Seam)
           + FrameZones.Ticks(FrameZones.Zone.Bcn) + FrameZones.Ticks(FrameZones.Zone.Prebuild);   // subtracted from Enroll already; they must not land in Ai instead

    [UnmanagedCallersOnly]
    private static long OnGui(IntPtr self, float dt)
    {
        long t = FrameZones.Now;
        long r = _gui != null ? _gui(self, dt) : 0;
        FrameZones.Add(FrameZones.Zone.Gui, t);
        return r;
    }
}
