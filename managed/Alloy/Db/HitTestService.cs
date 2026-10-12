using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>Wires the scene database's hit test: the off-device checks (which run in the stub host before any
/// symbol gate, because the cast is pure logic over the DB columns) and, when <c>alloy.hitcheck</c> asks for
/// it, the live instrument on the engine's own <c>Scene::HitCheck</c>. No frame work of its own -- a cast runs
/// when the game asks for one, through the Aurora seam.</summary>
public sealed class HitTestService : IExtenderService
{
    /// <summary>Frames between <c>[hitprobe]</c> lines -- the same 900 the other Alloy period lines use.</summary>
    private const int LogEvery = 900;
    private static ExtenderLog? _log;
    private static int _frames;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        HitTest.Log = log;
        HitFeed.Logger(log);
        // Ahead of every symbol gate: the cast is pure logic over the DB columns, and the stub host is the
        // only place it is ever checked. It builds and frees records in the real tables, which is safe HERE
        // and nowhere else -- see HitTestCheck.Run's own precondition.
        HitTestCheck.Run(log);
        HitProbe.Install(log, hooks);
        TooltipProbe.Install(log, hooks);
        FrameRegistry.Global.Register(FramePhase.PostPresent, 35, Period, "HitTest.period", log);
    }

    private static void Period()
    {
        if (++_frames < LogEvery) return;
        _frames = 0;
        if (_log != null) HitTestCheck.Pump(_log);
        if (!ExtenderLog.Enabled) { HitProbe.ResetPeriod(); return; }
        string s = HitProbe.Period();
        if (s.Length > 0) _log?.Info(s);
        string trig = DbTriggers.Stats();
        if (trig.Length > 0) _log?.Info(trig);
        string seam = CX.Alloy.Seam.SeamHit.Stats();
        if (seam.Length > 0) _log?.Info(seam);
        // The producer and index census: whether anything produced the hit columns at all, what the fold
        // refreshed, and every refusal the cast counted (a full entry pool, a degraded tree, a traversal that
        // hit its stack cap, a fill that could not report its object). None of these is allowed to be quiet.
        if (HitFeed.Noted > 0 || SceneDb.Objects.Live > 0) _log?.Info("[hittest] " + HitFeed.Stats());
    }
}
