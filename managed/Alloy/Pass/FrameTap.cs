using System;
using System.IO;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Diagnostic: <c>/alloy snap</c> writes one frame at three points -- the linear HDR target before
/// and after the sky pass (<c>.pfm</c>, float RGB; Alloy sessions only) and the default framebuffer at
/// present (<c>.ppm</c>, 8-bit; vanilla sessions too) -- and every native stage's images after the sky. One frame,
/// then disarmed; stalls that frame. A matched set from ONE camera, so a fault can be bisected by pass.</summary>
internal static unsafe class FrameTap
{
    internal static bool Armed;
    private static string _seq = "";
    private static ExtenderLog? _log;
    internal static string Dir => Native.ConfigPath("cx_snap");
    /// <summary>The armed set's name, which every file of it starts with.</summary>
    internal static string Seq => _seq;

    internal static void Install(ExtenderLog log)
    {
        _log = log;
        FrameRegistry.Global.Register(FramePhase.PrePresent, 30, Final, "FrameTap.final", log);
    }

    internal static void Arm()
    {
        _seq = DateTime.Now.ToString("HHmmss");
        Armed = true;
        _log?.Info($"[snap] armed -- {Dir}/{_seq}_*.{{pfm,ppm}} on the next frame");
    }

    /// <summary>Inside the Alloy bracket, off the bound framebuffer's attachment 0, at the pass viewport.</summary>
    internal static void Take(string stage, int* vp)
    {
        if (!Armed || Gl.ReadPixels == null) return;
        int w = vp[2], h = vp[3];
        if (w <= 0 || h <= 0) return;
        var px = new float[w * h * 3];
        Gl.PixelStorei(Gl.PACK_ALIGNMENT, 1);
        fixed (float* p = px) Gl.ReadPixels(vp[0], vp[1], w, h, Gl.RGB, Gl.FLOAT, p);
        Directory.CreateDirectory(Dir);
        using var f = File.Create(Path.Combine(Dir, $"{_seq}_{stage}.pfm"));
        var hdr = System.Text.Encoding.ASCII.GetBytes($"PF\n{w} {h}\n-1.0\n");
        f.Write(hdr, 0, hdr.Length);
        fixed (float* p = px) f.Write(new ReadOnlySpan<byte>(p, px.Length * 4));
    }

    /// <summary>At present, the default framebuffer -- what the player sees -- as 8-bit. Runs in a vanilla
    /// session too, which is what makes the on/off pair comparable through one path.</summary>
    private static void Final()
    {
        if (!Armed || Gl.ReadPixels == null) return;
        Armed = false;
        int* vp = stackalloc int[4];
        Gl.GetIntegerv(Gl.VIEWPORT, vp);
        int w = vp[2], h = vp[3];
        if (w <= 0 || h <= 0) return;
        int prev; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &prev);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        var px = new byte[w * h * 3];
        Gl.PixelStorei(Gl.PACK_ALIGNMENT, 1);
        fixed (byte* p = px) Gl.ReadPixels(vp[0], vp[1], w, h, Gl.RGB, Gl.UNSIGNED_BYTE, p);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)prev);
        Directory.CreateDirectory(Dir);
        using var f = File.Create(Path.Combine(Dir, $"{_seq}_final.ppm"));
        var hdr = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
        f.Write(hdr, 0, hdr.Length);
        // GL rows are bottom-up; PPM is top-down
        for (int y = h - 1; y >= 0; y--) f.Write(px, y * w * 3, w * 3);
        _log?.Info($"[snap] wrote set {_seq} to {Dir} ({w}x{h})");
    }
}
