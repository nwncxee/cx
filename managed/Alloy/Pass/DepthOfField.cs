using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// Depth of field on the Vulkan frame: AMD's effect on the HDR image, ahead of bloom and the resolve, so a lamp blurs at its
/// real brightness and the interface stays sharp. The focus follows the stock <c>inc_postpr_dof</c>'s rules, worked out once
/// a frame here, and the blur grows in metres past the sharp band as the stock shader's does. It reads no history.
/// </summary>
internal static unsafe partial class DepthOfField
{
    /// <summary>The frame's focus inputs, in the stock shader's terms: the camera, and the depths read under the mouse and ahead of it.</summary>
    internal readonly record struct Scene(float CamDist, float PitchDeg, bool Dialog, bool Cutscene, float MouseDepth,
                                          float AheadU, float AheadL, float AheadR);

    /// <summary>The sharp band in metres, and the distance the blur is measured against.</summary>
    internal readonly record struct Band(float Near, float Far, float Reference);

    /// <summary>The stock shader's <c>fDOFMinDistance</c> and <c>fDOFMaxDistance</c>: its focus and its pixels' depth stay inside them.</summary>
    private const float MinDistance = 5f, MaxDistance = 45f;
    /// <summary>The stock shader's offsets of the focus at a horizontal view, a vertical view, and past a read depth, in metres.</summary>
    private const float HorizontalOffset = 2.5f, VerticalOffset = -1f, ReadOffset = -1f;
    /// <summary>The blur's radius at the stock shader's confusion of 1, in half-size pixels per pixel of frame height.</summary>
    private const float BlurFraction = 0.004f;
    /// <summary>How long the focus takes to cover about two thirds of a move, in seconds.</summary>
    private const float EaseSeconds = 0.25f;
    /// <summary>The three points the Dynamic focus reads, one per request, as (U, L, R).</summary>
    private const int Reads = 3, ReadBytes = Reads * sizeof(float);

    /// <summary>What the pass holds: the session's refusal, the eased focus, its output image and the depth reads. Replaced whole on context loss.</summary>
    private sealed class DofState
    {
        public VkBackend? Dev;
        public string? Refused;
        public Band Band;
        public bool HasBand;
        public ImageId Out;
        public int W, H;
        public BufferId Read;
        public bool Reading;
        public int ReadGen;
        public float A, B, MouseDepth, AheadU, AheadL, AheadR;
    }

    private static DofState _s = new();

    internal static void Attach(ExtenderLog log) => _ = Dof.G;

    /// <summary>World edge, or a camera cut: the next frame takes its focus whole.</summary>
    internal static void Revoke() => _s.HasBand = false;

    /// <summary>Context loss: the session's refusal and the device's objects are gone.</summary>
    internal static void Forget() => _s = new DofState();

    /// <summary>The focus the stock rules give for this frame. With <see cref="Dof.NeverBlur"/> the band covers the character's
    /// true distance, where stock's covers the distance it offsets for the view.</summary>
    internal static Band Focus(in Scene s)
    {
        float pitch = MathF.Abs(MathF.Cos(s.PitchDeg * MathF.PI / 180f));
        float cam = CameraFocus(s.CamDist, pitch);
        int type = Dof.Focus.V;
        float far;
        if (s.Dialog || (type == Dof.FocusPlayer && !s.Cutscene)) far = cam;
        else if (type == Dof.FocusPitch || s.Cutscene) far = MathF.Min(MaxDistance, cam / Math.Clamp((pitch * 1.15f - 0.15f) * 1.414f, 0.01f, 1f));
        else if (type == Dof.FocusMouse) far = s.MouseDepth - ReadOffset;
        else far = MathF.Max(MathF.Max(MathF.Min(s.AheadL, s.AheadR), s.AheadU) + ReadOffset, cam);
        float half = 0.5f * Dof.DeadZone.V, near = far - half, reference;
        far += half;
        if (Dof.NeverBlur.V || s.Cutscene)
        {
            near = MathF.Min(near, MathF.Min(cam, s.CamDist - half));
            far = MathF.Max(far, MathF.Max(cam, s.CamDist + half));
            reference = 0.5f * (near + far);
        }
        else reference = far;
        return new Band(Math.Clamp(near, MinDistance, MaxDistance), Math.Clamp(far, MinDistance, MaxDistance), reference);
    }

    /// <summary>The distance the stock rules start from: pushed away at a horizontal view, pulled in at a vertical one.
    /// <paramref name="pitch"/> is the absolute cosine of the camera's pitch.</summary>
    private static float CameraFocus(float dist, float pitch)
        => dist + HorizontalOffset * MathF.Sqrt(1f - pitch * pitch) + pitch * VerticalOffset;

    /// <summary>The focus a step of <paramref name="dt"/> seconds nearer <paramref name="target"/>, so a far move never jumps.</summary>
    internal static Band Ease(in Band cur, in Band target, float dt)
    {
        float k = 1f - MathF.Exp(-dt / EaseSeconds);
        return new Band(cur.Near + (target.Near - cur.Near) * k, cur.Far + (target.Far - cur.Far) * k, cur.Reference + (target.Reference - cur.Reference) * k);
    }

    /// <summary>Does the frame draw the effect? The setting is on and nothing refused it.</summary>
    internal static bool Wanted => Dof.Enabled.V && _s.Refused == null;

    /// <summary>Stand the effect down for the session: <paramref name="why"/> said once, on cx.log and on the panel.</summary>
    internal static void Refuse(string why, ExtenderLog log)
    {
        if (_s.Refused != null) return;
        _s.Refused = why;
        log.Always($"PostStack: depth of field is OFF for the session -- {why}. The frame draws with no blur.");
    }

    /// <summary>The <c>dof</c> panel's line: why the effect is off for the session, or empty.</summary>
    internal static string PanelLine() => _s.Refused == null ? "" : $"Depth of field is off for this session: {_s.Refused}.";

    /// <summary>One dispatch in AMD's terms from Alloy's. A world depth d is <c>M43 / (d + M33)</c> metres from the eye, the
    /// folded projection's depth row, which are these from the planes.</summary>
    internal static DofCmd Command(ImageId color, ImageId depth, ImageId output, in Band band, int height, float near, float far)
        => new(color, depth, output, Dof.Amount.V / band.Reference * BlurFraction * height, band.Near, band.Far,
               far * near / (far - near), near / (far - near), MinDistance, MaxDistance);

    /// <summary>The effect on this frame of <paramref name="w"/> x <paramref name="h"/>, when it is wanted and the view is a
    /// perspective one: <paramref name="hdr"/> becomes the image it wrote. A refusal stands it down for the session.</summary>
    internal static void Record(VkBackend vk, CommandList cl, ref ImageId hdr, ImageId depth, int w, int h, in Scene s, float dt,
                                float near, float far, float fovY, ExtenderLog log)
    {
        if (!Wanted || fovY <= 0f) return;
        if (_s.Dev != vk) _s = new DofState { Dev = vk };
        DofState d = _s;
        if (vk.DofReady(w, h) is string why) { Refuse(why, log); return; }
        if (d.Out.IsNull || d.W != w || d.H != h)
        {
            vk.Destroy(d.Out);
            d.Out = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, w, h, 1, 1, 1, 1,
                                                 ImageUse.Sampled | ImageUse.Storage | ImageUse.ColorTarget | ImageUse.CopySrc, "vk dof output"));
            d.W = w; d.H = h;
            if (d.Out.IsNull) { Refuse($"its {w}x{h} output image was refused (logged above)", log); return; }
        }
        Band target = Focus(in s);
        d.Band = d.HasBand ? Ease(in d.Band, in target, dt) : target;
        d.HasBand = true;
        cl.BeginLabel("depth of field"u8);
        cl.DepthOfField(Command(hdr, depth, d.Out, in d.Band, h, near, far));
        cl.EndLabel();
        hdr = d.Out;
    }

    private static readonly Action<int, bool> ReadLanded = OnReadLanded;

    /// <summary>Copy out the depths the Mouse or Dynamic focus reads, landing a frame or more later with no wait: one read in flight
    /// at a time. The mouse is in rows from the bottom, as the frame's rows run, on the <paramref name="dw"/> x <paramref name="dh"/>
    /// display; depth is <paramref name="rw"/> x <paramref name="rh"/>. Off those focus types, or in a conversation, nothing is read.</summary>
    internal static void AskDepths(VkBackend vk, CommandList cl, ImageId depth, int dw, int dh, int rw, int rh, in Scene s, int mouseX, int mouseY, float near, float far)
    {
        int type = Dof.Focus.V;
        if ((type != Dof.FocusMouse && type != Dof.FocusDynamic) || s.Dialog || s.Cutscene) return;
        if (_s.Dev != vk) _s = new DofState { Dev = vk };
        DofState d = _s;
        if (d.Reading) return;
        if (d.Read.IsNull)
        {
            d.Read = vk.CreateBuffer(new BufferDesc(ReadBytes, BufferUse.CopyDst, MemoryKind.Readback, "vk dof focus read"));
            if (d.Read.IsNull) return;
        }
        d.A = far * near / (far - near);
        d.B = near / (far - near);
        Span<(int X, int Y)> at = stackalloc (int, int)[Reads];
        if (type == Dof.FocusMouse) at.Fill((Math.Clamp((int)((long)mouseX * rw / dw), 0, rw - 1), Math.Clamp((int)((long)mouseY * rh / dh), 0, rh - 1)));
        else
        {
            float pitch = MathF.Abs(MathF.Cos(s.PitchDeg * MathF.PI / 180f));
            float k = (0.5f + 0.5f * pitch) / CameraFocus(s.CamDist, pitch), v = 0.53f + 0.9f * k, h = 0.05f * k;
            at[0] = Texel(0.49f - h, v, rw, rh);
            at[1] = Texel(0.5f, v, rw, rh);
            at[2] = Texel(0.51f + h, v, rw, rh);
        }
        for (int i = 0; i < Reads; i++)
            cl.CopyImageToBuffer(new CopyToBufferCmd(depth, new ImageRegion(0, at[i].X, at[i].Y, 0), 1, 1, d.Read, i * sizeof(float)));
        d.Reading = true;
        cl.WhenDone(ReadLanded, ++d.ReadGen);
    }

    private static (int X, int Y) Texel(float u, float v, int w, int h)
        => (Math.Clamp((int)(u * w), 0, w - 1), Math.Clamp((int)(v * h), 0, h - 1));

    /// <summary>A read landed: its depths as metres from the eye, for the next frame's focus. A reader may find the state replaced.</summary>
    private static void OnReadLanded(int gen, bool ran)
    {
        DofState d = _s;
        if (d.ReadGen != gen) return;
        d.Reading = false;
        Span<float> got = stackalloc float[Reads];
        if (!ran || d.Dev == null || !d.Dev.ReadMapped(d.Read, 0, MemoryMarshal.AsBytes(got))) return;
        d.AheadU = d.A / (got[0] + d.B);
        d.AheadL = d.A / (got[1] + d.B);
        d.AheadR = d.A / (got[2] + d.B);
        d.MouseDepth = d.AheadU;
    }

    /// <summary>The metres under the mouse, as last read.</summary>
    internal static float MouseDepth => _s.MouseDepth;

    /// <summary>This frame's focus inputs: the engine's camera, and the depths last read.</summary>
    internal static Scene EngineScene()
    {
        ShaderExposedRuntimeData rt = AlloyRenderer.Rt;
        DofState d = _s;
        return new Scene(rt.CamDist, rt.CamPitch, rt.DialogCamera != 0, rt.InCutscene != 0, d.MouseDepth, d.AheadU, d.AheadL, d.AheadR);
    }

    /// <summary>The engine's mouse, in window pixels with rows from the bottom.</summary>
    internal static (int X, int Y) EngineMouse()
    {
        ShaderExposedRuntimeData rt = AlloyRenderer.Rt;
        return (rt.MouseX, rt.MouseY);
    }

    /// <summary>Destroys what the pass made on <paramref name="vk"/>, and forgets it.</summary>
    internal static void Drop(VkBackend vk)
    {
        DofState d = _s;
        if (d.Dev == vk) { vk.Destroy(d.Out); vk.Destroy(d.Read); }
        Forget();
    }

    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DepthOfField.SelfCheck");
        int skipped = Checker.Skipped;
        FocusRulesSelfCheck(check);
        // the examples log a test refusal and read cx.log back: run-demo's own log, never a player's
        if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero) check.Skip("in a session: the depth of field examples run in run-demo");
        else Examples(check, log);
        log.Info(!check.Ok ? "DepthOfField.SelfCheck: FAILED" : Checker.Skipped > skipped ? "DepthOfField.SelfCheck: ran the rest (a skip above)" : "DepthOfField.SelfCheck: passed");
    }

    /// <summary>The focus rules no approved example names. Oracle: <c>inc_postpr_dof</c> worked by hand at a camera 8 m away and
    /// pitched 70 degrees, where the view's offset makes that 10.007 m and the pitch rule 29.09 m, with a dead zone of 3 m.</summary>
    private static void FocusRulesSelfCheck(Checker check)
    {
        (bool never, int focus, float dead) = (Dof.NeverBlur.V, Dof.Focus.V, Dof.DeadZone.V);
        try
        {
            Dof.DeadZone.V = 3f; Dof.NeverBlur.V = false;
            void Rule(string name, int type, in Scene s, float near, float far)
            {
                Dof.Focus.V = type;
                Band b = Focus(in s);
                check.Expect(MathF.Abs(b.Near - near) < 0.02f && MathF.Abs(b.Far - far) < 0.02f, $"{name}: the sharp band runs {near} to {far} m (got {b.Near:F2} to {b.Far:F2})");
            }
            var cam = new Scene(8f, 70f, false, false, 0f, 20f, 18f, 25f);
            Rule("Player Character", Dof.FocusPlayer, in cam, 8.51f, 11.51f);
            Rule("Camera Pitch", Dof.FocusPitch, in cam, 27.59f, 30.59f);
            Rule("Dynamic", Dof.FocusDynamic, in cam, 17.5f, 20.5f);
            Rule("A horizontal camera's focus stops at the far limit", Dof.FocusPitch, cam with { PitchDeg = 90f }, 43.5f, 45f);
            Rule("A cutscene takes the pitch rule and keeps the character", Dof.FocusMouse, cam with { Cutscene = true }, 6.5f, 30.59f);
        }
        finally { Dof.NeverBlur.V = never; Dof.Focus.V = focus; Dof.DeadZone.V = dead; }
    }

    /// <summary>The approved examples that need no device.</summary>
    static partial void Examples(Checker check, ExtenderLog log);
}
