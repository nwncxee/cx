using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The deferred path's SHADOW pass (ADR 0029): one compute dispatch between the cutout pass and
/// <see cref="DeferredLights"/> that picks, per PIXEL, the four shadow-casting lights reaching it most
/// strongly and writes each one's cube visibility once into the record this class owns (<c>cx_shrec.glsl</c>'s
/// format, at <c>shadow.deferredres</c>). A link failure stands the whole deferred path down.</summary>
internal static unsafe class DeferredShadows
{
    /// <summary>The overridable compute stage's resref (hak-limit length).</summary>
    internal const string ShdCs = "cx_defshadow_cs";

    /// <summary>Bytes a record texel the two targets cost together, for the allocation line.</summary>
    private const int BytesPerTexel = 4 + 8;

    /// <summary>Lights one record texel can hold -- <c>cx_shrec.glsl</c>'s <c>CX_SH_MAX</c>, which is the
    /// definition; this is the ceiling <c>shadow.perpixel</c> is declared against. Four packed slot bytes in one
    /// word and four visibility channels in one RGBA8, so widening it is a format change.</summary>
    internal const int RecordLights = 4;

    /// <summary>How far above the best DROPPED light's score a record lane must sit to keep its shadow in full
    /// -- <c>cx_shrec.glsl</c>'s <c>CX_SH_FADE</c>, which is the definition; this is the mirror the continuity
    /// self-check sweeps. An octave: a lane at the budget's edge is fully dissolved, so the light that falls out
    /// of the record had already faded to unshadowed and the budget's membership step is invisible.</summary>
    internal const float RecordFade = 2f;

    private static ExtenderLog? _log;
    private static readonly StandDown _down = new("DeferredShadows");

    /// <summary>The program whose link the deferred verdict rests on: context loss replaces the object, so no
    /// name from the dead context survives as a field nobody remembered to zero.</summary>
    private sealed class ShadowRecordState
    {
        public uint Prog;
    }

    private static ShadowRecordState _gl = new();

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Build the program on the first frame that wants it, once. False is latched and LOUD: the
    /// deferred path then stands down in favour of the forward loop, which has shadows of its own.</summary>
    internal static bool Ready()
    {
        if (_gl.Prog != 0) return true;
        if (_down.Failed) return false;
        if (Gl.DispatchCompute == null || Gl.MemoryBarrier == null || Gl.BindImageTexture == null || Gl.BindBufferBase == null)
            return _down.Fail(_log, "compute, image stores or SSBO entry points are missing",
                              "deferred point lights are off for the session -- the forward loop walks every light");
        uint prog = GlProgram.Compute(ShdCs, ShaderStages.Source(ShdCs), _log, "DeferredShadows");
        if (prog == 0)
        {
            _log?.Always("DeferredShadows: the per-pixel shadow program did not build (the driver's own log is "
                       + "above) -- deferred point lights are off for the session and the forward light loop, "
                       + "whose own shadow filter is intact, draws the frame");
            return _down.Fail(_log, "the compute program did not link",
                              "deferred point lights are off for the session");
        }
        _gl.Prog = prog;
        _log?.Info("DeferredShadows: ready -- the per-pixel visibility record's program links");
        return true;
    }

    /// <summary>Context loss: the program is gone.</summary>
    internal static void Forget()
    {
        _gl = new ShadowRecordState();
        _down.Forget();
    }

    internal static string Stats(int frames)
        => _down.Failed ? "defshadow STOOD DOWN: " + _down.Reason
         : !Lighting.Deferred.V ? "defshadow off"
         : $"defshadow on at 1/{Shadows.DeferredRes.V} of the frame, {RecordLights} lights a texel"
         + (_down.Reason.Length > 0 ? ", last refusal: " + _down.Reason : "");
}
