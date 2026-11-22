using System;

namespace CX.Alloy;

/// <summary>The shader-contract text rules: pure string-to-bool over a stage's GLSL, with no renderer constant, embedded
/// stage or device behind them. Each covers a desync that fails silently as a misrender rather than an error. The
/// rules that read the renderer's own constants are <see cref="RendererContract"/>'s.</summary>
internal static class ShaderContract
{
    /// <summary>The same count, for any per-draw record a C# stride stages. <paramref name="floats"/> is the
    /// whole stride, counted by FIELD SIZE: a vec4-only count is blind to an appended <c>mat4</c>, and a stale
    /// override then reads a 44-float stride against a 60-float buffer.</summary>
    public static bool RecordSafe(string src, string structName, int floats)
    {
        int at = src.IndexOf("struct " + structName, StringComparison.Ordinal);
        if (at < 0) return true;                        // a stage that never declares it cannot disagree
        int end = src.IndexOf('}', at);
        string decl = end < 0 ? src[at..] : src[at..end];
        return 16 * (decl.Split("mat4").Length - 1) + 4 * (decl.Split("vec4").Length - 1) == floats;
    }

    /// <summary>Does this grass vertex stage reconstruct last frame's blade? An override without it links
    /// CLEANLY, writes nothing to the velocity outs and leaves the camera's field standing over every blade
    /// -- exactly the smear the sweep exists to remove. Named terms, so an override may restyle the sway as
    /// long as it says where last frame's is.</summary>
    public static bool SwaySafe(string vs)
        => vs.Contains("CxGrassSway(CxPrevClock() * uWindSpeed, phase)") && vs.Contains("vPrev = uPrevVP");

    /// <summary>Does this stage pair carry the cutout and the per-draw alpha? An override without them links
    /// CLEANLY -- the record did not grow, the clip expression did not change, and the per-draw alpha simply
    /// goes unread -- and ships a world with no alpha discard and a constant 1.0 alpha: grass as
    /// opaque quads, windows as walls. Named terms, so an override may restyle around them, not drop them.</summary>
    public static bool CutoutSafe(string vs, string fs)
        // The probe is a SUBSTRING of the whole stage text, COMMENTS INCLUDED: a stage that merely mentions
        // e.g. CasterPass's env threshold by name takes the vAlpha arm on the strength of the comment and
        // fails against a vertex stage that forwards nothing. Say "the caster's env threshold", not its
        // identifier, anywhere inside a stage string.
        => fs.Contains("uAlphaCut") && (fs.Contains("vAlpha") ? vs.Contains("vAlpha") : fs.Contains("rot.w"));

    /// <summary>Does this VERTEX stage carry the skinning branch? Same trap as <see cref="CutoutSafe"/>: an
    /// override without it links perfectly (the bone base rides in <c>feat.y</c>, the missing bone attributes
    /// are simply disabled) and renders every skinned robe in its BIND POSE, silently. On a server the HAK
    /// wins over a local override directory, so it is live content that breaks. Checked only when armed.</summary>
    public static bool SkinSafe(string vs, bool skinning) => !skinning || vs.Contains("CX_FEAT_SKIN");

    /// <summary>Does this FRAGMENT stage write LINEAR? One ending in <c>CxRevert(...)</c> links perfectly and
    /// writes display-space values into a target the resolve then encodes a SECOND time -- the frame is washed
    /// out end to end and nothing says so. The answer is to rebuild from the embedded text rather than log and
    /// carry on, because on a server the HAK wins over a local override directory.</summary>
    public static bool LinearSafe(string fs) => !fs.Contains("CxRevert") && !fs.Contains("CxSoftClip");

    /// <summary>The same guard from the other end, for the ONE stage that encodes: the resolve. It must encode
    /// and must not decode first -- an override opening with <c>CxLinear</c> on an already-linear target comes
    /// out a gamma dark, one that dropped the encode comes out washed out, and both link cleanly and say
    /// nothing. Named terms: the tonemap and the dither may be restyled, the colour-space decision may not.</summary>
    public static bool ResolveSafe(string fs) => !fs.Contains("CxLinear") && fs.Contains("CxEncode");

    /// <summary>The guard for a <see cref="PostStack"/> stage, which runs AFTER the resolve on a
    /// display-space image and hands one on: it must neither encode (a second encode, a gamma too bright) nor
    /// decode (the frame goes linear and a gamma dark). Named terms, like the two above.</summary>
    public static bool DisplaySafe(string fs) => !fs.Contains("CxLinear") && !fs.Contains("CxEncode");

    /// <summary>A stage that derives a draw index must reach its record THROUGH the slot map: the records are
    /// indexed by ownership slot and the draw order is not that order, so a stage still spelling
    /// <c>uDraw[uDrawBase + gl_DrawID]</c> reads a NEIGHBOUR's model matrix and textures while passing every
    /// other guard here. Stages with no <c>gl_DrawID</c> -- every fragment stage -- pass trivially.</summary>
    public static bool SlotIndexSafe(string src)
        => !src.Contains("gl_DrawID") || src.Contains("CxSlot(uDrawBase + gl_DrawID)");

    /// <summary>A fragment stage reading the chart UV needs a vertex stage that forwards it -- a stale
    /// <c>cx_ownlit_vs</c> override beside the embedded fragment stage leaves <c>vChart</c> unwritten (a link
    /// error on strict drivers, undefined reads on the rest). A fragment override that never reads it is fine
    /// either way: it simply never reads the lightmap.</summary>
    public static bool ChartSafe(string vs, string fs) => !fs.Contains("vChart") || vs.Contains("vChart");

    /// <summary>Does <paramref name="name"/> occur in <paramref name="src"/> as a whole identifier?</summary>
    internal static bool HasIdent(string src, string name)
    {
        for (int at = src.IndexOf(name, StringComparison.Ordinal); at >= 0; at = src.IndexOf(name, at + 1, StringComparison.Ordinal))
        {
            int e = at + name.Length;
            if ((at == 0 || !IsIdent(src[at - 1])) && (e == src.Length || !IsIdent(src[e]))) return true;
        }
        return false;
    }

    private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';
}
