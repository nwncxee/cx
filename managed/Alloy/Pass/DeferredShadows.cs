using CX.Framework;

namespace CX.Alloy;

/// <summary>The deferred path's SHADOW pass (ADR 0029): one compute dispatch between the cutout pass and
/// <see cref="DeferredLights"/> that picks, per PIXEL, the four shadow-casting lights reaching it most
/// strongly and writes each one's cube visibility once into the record this class owns (<c>cx_shrec.glsl</c>'s
/// format, at <c>shadow.deferredres</c>).</summary>
internal static class DeferredShadows
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

    internal static string Stats(int frames)
        => !Lighting.Deferred.V ? "defshadow off"
         : $"defshadow on at 1/{Shadows.DeferredRes.V} of the frame, {RecordLights} lights a texel";
}
