using System;
using System.IO;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Diagnostic: <c>/alloy snap</c> writes the Vulkan frame's finished scene colour as a float-RGB
/// <c>.pfm</c>, one frame, then disarms; stalls that frame.</summary>
internal static unsafe partial class FrameTap
{
    internal static bool Armed;
    private static string _seq = "";
    private static ExtenderLog? _log;
    internal static string Dir => Native.ConfigPath("cx_snap");
    /// <summary>The armed set's name, which every file of it starts with.</summary>
    internal static string Seq => _seq;

    internal static void Install(ExtenderLog log) => _log = log;

    internal static void Arm()
    {
        _seq = DateTime.Now.ToString("HHmmss");
        Armed = true;
        _log?.Info($"[snap] armed -- {Dir}/{_seq}_*.pfm on the next frame");
    }
}
