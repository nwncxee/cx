using System;
using System.Globalization;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// The model view's service: one frame hook that builds, places and takes down <see cref="NuiModelDoll"/>. Prototype stage:
/// <c>nui.modelprobe</c> draws a doll straight to the screen, to answer what the engine's viewport does on its own.
/// </summary>
internal static unsafe class NuiModelView
{
    private const long ProbeAfterMs = 20_000;

    private sealed class ViewState
    {
        public readonly long Born = Environment.TickCount64;
        public bool ProbeDone, Ready;
        public string Applied = "";
        public long Frames, NextLog;
        public ExtenderLog? Log;
    }

    private static readonly ViewState _s = new();

    internal static void Init(ExtenderLog log, HookService hooks)
    {
        _s.Log = log;
        _s.Ready = NuiModelDoll.Init(log);
        if (!_s.Ready) { log.Always("NuiModelView: the engine pieces did not resolve -- no model views"); return; }
        FrameRegistry.Global.Register(FramePhase.PrePresent, 1, Frame, "NuiModelView.frame", log);
    }

    private static void Frame()
    {
        ViewState s = _s;
        s.Frames++;
        string probe = NuiKnobs.ModelProbe.V;
        if (probe.Length > 0 && probe != s.Applied && GameGate.InGame && Environment.TickCount64 - s.Born > ProbeAfterMs) Probe(s, probe);
        NuiModelDoll.KeepShown();
        if (NuiModelDoll.Doll.Enabled && Environment.TickCount64 > s.NextLog)
        {
            s.NextLog = Environment.TickCount64 + 5000;
            _s.Log?.Always("NuiModelView: " + NuiModelDoll.CameraLine() + " | " + NuiModelDoll.FadeLine());
        }
    }

    /// <summary><c>x,y,w,h[,angle,px,py,pz,yaw]</c>: the rectangle, then (when given) the camera's view angle and where the
    /// model stands in front of it, so a live run can try placements without a rebuild.</summary>
    private static void Probe(ViewState s, string spec)
    {
        s.Applied = spec;
        string[] t = spec.Split(',');
        float F(int i, float d) => i < t.Length && float.TryParse(t[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : d;
        NuiModelDoll.SetFadeMode((int)F(10, 1));
        IntPtr me = NuiModelDoll.Player();
        if (me == IntPtr.Zero) { _s.Log?.Always("NuiModelView: probe -- no player creature"); return; }
        if (NuiModelDoll.Doll.Creature == IntPtr.Zero)
        {
            if (!NuiModelDoll.Build(me, (int)F(0, 700), (int)F(1, 100), (int)F(2, 300), (int)F(3, 400), F(9, 1) != 0)) return;
            _s.Log?.Always("NuiModelView: probe built; camera as the engine made it: " + NuiModelDoll.CameraLine());
        }
        if (t.Length > 4) { var cam = new CAurCamera(NuiModelDoll.Doll.Camera); cam.SetViewAngle(F(4, 30f)); cam.SetClipDist(0.1f, 2000f); }
        if (t.Length > 5) NuiModelDoll.Place(new Vector3(F(5, 0), F(6, 0), F(7, 0)), F(8, 0));
        _s.Log?.Always("NuiModelView: probe placed; camera now: " + NuiModelDoll.CameraLine());
    }
}
