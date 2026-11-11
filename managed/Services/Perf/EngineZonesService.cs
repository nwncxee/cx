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
        log.Info("EngineZones: timing the engine loop's net / resman / AI / GUI steps and resource demands into [zones] (config alloy.enginezones=0 disables)");
    }

    private static ResManDemandFn _demand;

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
           + FrameZones.Ticks(FrameZones.Zone.Bcn);   // subtracted from Enroll already; it must not land in Ai instead

    [UnmanagedCallersOnly]
    private static long OnGui(IntPtr self, float dt)
    {
        long t = FrameZones.Now;
        long r = _gui != null ? _gui(self, dt) : 0;
        FrameZones.Add(FrameZones.Zone.Gui, t);
        return r;
    }
}
