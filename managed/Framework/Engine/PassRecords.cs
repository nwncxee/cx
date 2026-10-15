using CX.Alloy;

namespace CX.Engine;

/// <summary>One light in the area-light upload: position and range, colour, and the cube slot in the spare lane.</summary>
public unsafe struct AreaLightRec
{
    /// <summary>Floats per record.</summary>
    public const int N = 8;
    /// <summary>The record's floats.</summary>
    public fixed float V[N];
}

/// <summary>One mover box: min xyz, max xyz and the lone mover's radius.</summary>
public unsafe struct MoverBox
{
    /// <summary>The record's floats.</summary>
    public fixed float V[CasterPass.MoverBoxF];
}

/// <summary>One caster-gather source record.</summary>
public unsafe struct CasterSrcRec
{
    /// <summary>The record's words.</summary>
    public fixed uint W[CasterGather.SrcUints];
}

/// <summary>One caster-gather view record.</summary>
public unsafe struct CasterViewRec
{
    /// <summary>The record's floats.</summary>
    public fixed float V[CasterGather.ViewFloats];
}

/// <summary>One cull-pass source record.</summary>
public unsafe struct CullSrcRec
{
    /// <summary>The record's words.</summary>
    public fixed uint W[CullPass.SrcUints];
}

/// <summary>One indirect draw command.</summary>
public unsafe struct DrawCmdRec
{
    /// <summary>The command's words.</summary>
    public fixed uint W[CullPass.CmdUints];
}

/// <summary>One bounding sphere: centre xyz and radius.</summary>
public unsafe struct SphereRec
{
    /// <summary>The sphere's floats.</summary>
    public fixed float V[4];
}

/// <summary>One grass face: three corners of three floats.</summary>
public unsafe struct GrassTriRec
{
    /// <summary>The corners' floats.</summary>
    public fixed float V[9];
}

/// <summary>One lens-flare record.</summary>
public unsafe struct HaloRec
{
    /// <summary>Floats per record.</summary>
    public const int N = 12;
    /// <summary>The record's floats.</summary>
    public fixed float V[N];
}

/// <summary>One highlight-glow quad.</summary>
public unsafe struct GlowRec
{
    /// <summary>Floats per record.</summary>
    public const int N = 16;
    /// <summary>The record's floats.</summary>
    public fixed float V[N];
}

/// <summary>One particle record.</summary>
public unsafe struct ParticleRec
{
    /// <summary>Floats per record.</summary>
    public const int N = 28;
    /// <summary>The record's floats.</summary>
    public fixed float V[N];
}

/// <summary>One trigger-highlight vec4 row.</summary>
public unsafe struct TriggerRow
{
    /// <summary>The row's four floats.</summary>
    public fixed float V[4];
}

/// <summary>One row of the weather track texture.</summary>
public unsafe struct TrackRowRec
{
    /// <summary>The row's floats.</summary>
    public fixed float V[Weather.TrackSize * 4];
}

/// <summary>One 16-bit index.</summary>
public struct IndexRec
{
    /// <summary>The index.</summary>
    public ushort V;
}

/// <summary>One RGBA8 texel.</summary>
public unsafe struct TexelRec
{
    /// <summary>The texel's four bytes.</summary>
    public fixed byte B[4];
}
