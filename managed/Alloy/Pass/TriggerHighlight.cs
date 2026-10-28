using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The area-transition highlight, owned. Vanilla (<c>Scene::DoHighlightTriggers</c> @0x462310)
/// stencils the trigger's extruded outline as a shadow volume and alpha-blends a frustum hull through it,
/// which needs a STENCIL buffer -- and a missing one paints the whole screen. This asks the same question in
/// one fullscreen pass with no stencil: the world point under each pixel against the prism, in DISPLAY space.</summary>
internal static unsafe class TriggerHighlight
{
    /// <summary>Highlighted triggers per frame, and outline points across all of them. In play the count is
    /// one -- the trigger under the cursor -- because every other trigger in the bucket carries alpha 0.
    /// ponytail: past either cap the rest of the triggers go unhighlighted for the frame; a census would be
    /// the reason to raise them, not a guess.</summary>
    private const int MaxTriggers = 8, MaxPoints = 512;

    /// <summary>The SSBO layout the fragment stage reads: <c>MaxTriggers</c> headers, then
    /// <c>MaxTriggers</c> colours, then the points. Mirrors <c>CX_TRIG_MAX</c> in
    /// <c>cx_hilitetrig_f.glsl</c>; <see cref="SelfCheck"/> is what keeps the two from drifting.</summary>
    private const int PointBase = 2 * MaxTriggers;
    private const int Vec4s = PointBase + MaxPoints;
    private const uint BindTrig = SsboSlots.Trig;

    private static readonly Vector4[] _stage = new Vector4[Vec4s];
    private static int _count, _points;
    /// <summary>Enrolments that took at least one trigger (a non-zero highlight alpha), and draws that drew one,
    /// for the DB period line.</summary>
    internal static long Enrolled, Drawn;

    /// <summary>Once per world frame, on the game thread and BEFORE the frame anchor: snapshot the highlighted
    /// triggers from <see cref="Db.DbTriggers"/>' copies, never the engine objects. No in-view test: an unseen prism
    /// adds nothing. The filter is the ALPHA, 0.0 for every trigger nobody points at.</summary>
    internal static void Enroll()
    {
        _count = 0; _points = 0;
        int n = Db.DbTriggers.Count;
        for (int i = 0; i < n && _count < MaxTriggers; i++)
        {
            Db.DbTriggers.Entry t = Db.DbTriggers.At(i);
            if (!t.Highlighted || !t.Half) continue;
            float alpha = t.Colour.W;
            if (!(alpha > 0f)) continue;
            Vector3[] pts = t.Outline;
            int np = pts.Length;
            if (np < 3 || _points + np > MaxPoints) continue;
            float zmin = float.MaxValue, zmax = float.MinValue;
            for (int p = 0; p < np; p++)
            {
                Vector3 w = pts[p];
                _stage[PointBase + _points + p].X = w.X;
                _stage[PointBase + _points + p].Y = w.Y;
                zmin = MathF.Min(zmin, w.Z); zmax = MathF.Max(zmax, w.Z);
            }
            float h = t.Height;
            _stage[_count] = new Vector4(_points, np, zmin - h, zmax + h);
            _stage[MaxTriggers + _count] = t.Colour;
            _points += np;
            _count++;
        }
        if (_count > 0) Enrolled++;
    }

    /// <summary>The Vulkan half: the program and the staged outline buffer on one device.</summary>
    private sealed class TrigVkState
    {
        public VkBackend? Dev;
        public GuiVkProg? Prog;
        public BufferId Buf;
        public int Count = -1, InvVp = -1;
        public bool Tried, Live;
    }

    private static TrigVkState _vk = new();

    /// <summary>The pass on Vulkan, over the composite after the post stack, as GL draws it after its stack: the same
    /// stage, the outline buffer updated in the command list, the frame's depth sampled.</summary>
    internal static void DrawVk(VkBackend vk, CommandList cl, ImageId composite, ImageId depth, in Matrix4x4 vp, int* display,
                                ExtenderLog log)
    {
        TrigVkState s = _vk;
        if (s.Dev != vk) _vk = s = new TrigVkState { Dev = vk };
        if (_count == 0 || depth.IsNull || !Matrix4x4.Invert(vp, out Matrix4x4 inv)) return;
        if (!s.Tried)
        {
            s.Tried = true;
            s.Prog = PostChainVk.Program(vk, PostChain.FullscreenVs, ShaderSource.Get("cx_hilitetrig_f.glsl"), "trigger highlight",
                                         q => { s.Count = q.Loc("uTrigCount"); s.InvVp = q.Loc("uInvVP"); }, log);
            s.Buf = vk.CreateBuffer(new BufferDesc(Vec4s * sizeof(Vector4), BufferUse.Storage, MemoryKind.Upload, "vk trigger outlines"));
            if (s.Prog == null || s.Buf.IsNull)
            {
                s.Prog = null;
                log.Always("TriggerHighlight: the Vulkan program or outline buffer was REFUSED (logged above) -- area transitions "
                         + "will not highlight on hover this session");
            }
        }
        if (s.Prog == null) return;
        int bytes = (PointBase + _points) * sizeof(Vector4);
        cl.UpdateBuffer(s.Buf, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<Vector4>(_stage, 0, PointBase + _points)));
        PostChainVk.Open(s.Prog);
        LooseBlock.Set(s.Count, _count);
        LooseBlock.Set(s.InvVp, inv);
        int at = PostChainVk.Close(cl, s.Prog);
        PostChainVk.Draw(cl, s.Prog, Format.Rgba8, PostChainVk.Blend.Over, composite, LoadOp.Load,
                         new Rect(display[0], display[1], display[2], display[3]), at,
                         new PostChainVk.Inputs(default, Depth: depth, Storage: new BufferBinding(BindTrig, s.Buf, 0, bytes)), log);
        Drawn++;
        if (s.Live) return;
        s.Live = true;
        log.Always($"TriggerHighlight: the Vulkan area-transition highlight is LIVE -- {_count} trigger(s), {_points} outline point(s) "
                 + "against the frame's depth over the composite");
    }


    /// <summary>The SSBO layout is written twice -- here and in the GLSL -- and a silent disagreement reads
    /// every trigger's outline at its neighbour's offset. Asserted, not commented.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        string src = ShaderSource.Get("cx_hilitetrig_f.glsl");
        bool ok = src.Contains($"CX_TRIG_MAX = {MaxTriggers};")
               && src.Contains($"binding = {BindTrig})");
        log.Info(ok ? $"TriggerHighlight.SelfCheck: passed (CX_TRIG_MAX {MaxTriggers}, binding {BindTrig})"
                    : "TriggerHighlight.SelfCheck: FAILED -- cx_hilitetrig_f.glsl disagrees with the staged "
                    + "SSBO layout; the trigger highlight would read an outline at the wrong offset");
        return ok;
    }
}
