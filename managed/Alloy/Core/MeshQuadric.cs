using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CX.Alloy;

/// <summary>One triangle's unit plane: an outward normal and the offset that makes
/// <c>Nx*x + Ny*y + Nz*z + D</c> the signed distance from it. All four zero is the plane of a
/// degenerate triangle, which contributes nothing.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct TriPlane(double Nx, double Ny, double Nz, double D)
{
    /// <summary>All four coefficients zero: a triangle with no area, whose plane says nothing.</summary>
    internal bool IsZero => Nx == 0d && Ny == 0d && Nz == 0d && D == 0d;

    /// <summary>The dot of the two normals. Zero or negative means the surface turned over between them.</summary>
    internal double NormalDot(TriPlane other) => Nx * other.Nx + Ny * other.Ny + Nz * other.Nz;
}

/// <summary>The error quadric of a set of planes: the upper triangle of the symmetric 4x4 sum of their
/// outer products, named lane by lane. <see cref="At"/> is the summed squared distance from every plane
/// in it, so a vertex costs the same to score however many triangles meet there.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Quadric
{
    internal double Xx, Xy, Xz, Xw, Yy, Yz, Yw, Zz, Zw, Ww;

    /// <summary>Fold one plane in, in place: the quadric is an accumulator over the planes a vertex touches,
    /// and a vertex's own slot in the table is where it accumulates.</summary>
    internal void Add(in TriPlane p)
    {
        Xx += p.Nx * p.Nx; Xy += p.Nx * p.Ny; Xz += p.Nx * p.Nz; Xw += p.Nx * p.D;
        Yy += p.Ny * p.Ny; Yz += p.Ny * p.Nz; Yw += p.Ny * p.D;
        Zz += p.Nz * p.Nz; Zw += p.Nz * p.D; Ww += p.D * p.D;
    }

    /// <summary>Fold another quadric in, in place: what a collapse does to the surviving vertex.</summary>
    internal void Add(in Quadric o)
    {
        Xx += o.Xx; Xy += o.Xy; Xz += o.Xz; Xw += o.Xw;
        Yy += o.Yy; Yz += o.Yz; Yw += o.Yw;
        Zz += o.Zz; Zw += o.Zw; Ww += o.Ww;
    }

    /// <summary>The sum of two quadrics: the quadric of a merged vertex, which neither end's own slot may
    /// hold. Both operands come in by reference, so summing them copies neither.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Quadric Sum(in Quadric a, in Quadric b)
    {
        Quadric s;
        s.Xx = a.Xx + b.Xx; s.Xy = a.Xy + b.Xy; s.Xz = a.Xz + b.Xz; s.Xw = a.Xw + b.Xw;
        s.Yy = a.Yy + b.Yy; s.Yz = a.Yz + b.Yz; s.Yw = a.Yw + b.Yw;
        s.Zz = a.Zz + b.Zz; s.Zw = a.Zw + b.Zw; s.Ww = a.Ww + b.Ww;
        return s;
    }

    /// <summary>The summed squared distance from the quadric's planes to a point, floored at zero: a sum of
    /// squares that rounds slightly negative is a point on every plane, not a negative error.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly double At(double x, double y, double z)
    {
        double r = Xx * x * x + 2 * Xy * x * y + 2 * Xz * x * z + 2 * Xw * x
                   + Yy * y * y + 2 * Yz * y * z + 2 * Yw * y
                   + Zz * z * z + 2 * Zw * z + Ww;
        return r > 0d ? r : 0d;
    }
}
