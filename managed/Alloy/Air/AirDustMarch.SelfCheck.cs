using System;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace CX.Alloy;

/// <summary>The dust stage's march in C#, step for step, over the bytes <see cref="GroundMist.Pack"/> wrote, its numbers
/// read out of the shipped <c>cx_airdust_cs</c>; the look examples run it where no device can run the stage itself.</summary>
internal static class AirDustMarch
{
    /// <summary>A lamp of the rig: where it stands, how far it reaches and its colour as the engine hands it over.</summary>
    internal readonly record struct Point(Vector3 At, float Reach, Vector3 Colour);

    /// <summary>The room's light: the area's ambient, the sky's fog colour and its sun or moon.</summary>
    internal readonly record struct Shine(Vector3 Ambient, Vector3 Fog, Vector3 Diffuse);

    /// <summary>The shipped stage's text.</summary>
    internal static string Stage => ShaderSource.Get(AirScene.ShdCs + ".glsl");

    /// <summary>The value of the stage's <c>const</c> <paramref name="name"/>, int or float; NaN when it declares none.</summary>
    internal static float Const(string name)
    {
        string src = Stage;
        foreach (string kind in new[] { "const float ", "const int " })
        {
            int at = src.IndexOf(kind + name + " = ", StringComparison.Ordinal);
            if (at < 0) continue;
            at += kind.Length + name.Length + 3;
            int end = src.IndexOf(';', at);
            if (end > at && float.TryParse(src.AsSpan(at, end - at), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) return v;
        }
        return float.NaN;
    }

    /// <summary>The field as the stage reads it.</summary>
    internal readonly struct Field
    {
        private readonly byte[] _bytes;
        internal readonly Vector4 Head, Sky, Slab;
        internal readonly float Sigma, Step, Drop, Far;
        internal readonly int Steps, Probes, Lamps;

        internal Field(byte[] bytes)
        {
            _bytes = bytes;
            Head = MemoryMarshal.Read<Vector4>(bytes);
            Sky = MemoryMarshal.Read<Vector4>(bytes.AsSpan(GroundMist.SkyAt));
            Slab = MemoryMarshal.Read<Vector4>(bytes.AsSpan(GroundMist.SlabAt));
            Sigma = Const("CX_AIR_SIGMA");
            Step = Const("CX_AIR_STEP");
            Drop = Const("CX_AIR_DROP");
            Far = Const("CX_AIR_FAR");
            Steps = (int)Const("CX_AIR_STEPS");
            Probes = (int)Const("CX_AIR_PROBES");
            Lamps = (int)Const("CX_AIR_LAMPS");
        }

        /// <summary>Does the stage declare every number the march takes?</summary>
        internal bool Declared => !float.IsNaN(Sigma + Step + Drop + Far + Const("CX_AIR_STEPS") + Const("CX_AIR_PROBES") + Const("CX_AIR_LAMPS"));

        /// <summary>Column (<paramref name="x"/>, <paramref name="y"/>) of the field, or one with no floor off it.</summary>
        internal GroundMist.Cell Cell(int x, int y)
        {
            int side = (int)Head.Z;
            if ((uint)x >= (uint)side || (uint)y >= (uint)side) return new GroundMist.Cell(-1e9f, 0f, 0f, 0f);
            return MemoryMarshal.Cast<byte, GroundMist.Cell>(_bytes.AsSpan(GroundMist.Head)).Slice(y * side, side)[x];
        }

        /// <summary>Do two neighbouring columns with floors <paramref name="a"/> and <paramref name="b"/> join into one floor?</summary>
        internal bool Joined(float a, float b) => MathF.Abs(a - b) <= Step;

        /// <summary>The share each of two neighbouring column centres takes a fraction <paramref name="f"/> of the way across.</summary>
        internal static (float A, float B) Pair(float f, bool joined)
            => joined ? (1f - f, f) : (MathF.Max(1f - 2f * f, 0f), MathF.Max(2f * f - 1f, 0f));

        /// <summary>The dust and the mist per metre at <paramref name="p"/> on a ray that ends at height <paramref name="end"/>.</summary>
        internal (float Dust, float Mist) At(Vector3 p, float end)
        {
            float qx = p.X - Head.X - 0.5f, qy = p.Y - Head.Y - 0.5f;
            int x = (int)MathF.Floor(qx), y = (int)MathF.Floor(qy);
            float fx = qx - x, fy = qy - y;
            GroundMist.Cell c00 = Cell(x, y), c10 = Cell(x + 1, y), c01 = Cell(x, y + 1), c11 = Cell(x + 1, y + 1);
            (float a0, float b0) = Pair(fx, Joined(c00.Floor, c10.Floor));
            (float a1, float b1) = Pair(fx, Joined(c01.Floor, c11.Floor));
            (float l0, float u0) = Pair(fy, Joined(c00.Floor, c01.Floor));
            (float l1, float u1) = Pair(fy, Joined(c10.Floor, c11.Floor));
            float dust = 0f, mist = 0f;
            Add(c00, a0 * l0, p.Z, end, ref dust, ref mist);
            Add(c10, b0 * l1, p.Z, end, ref dust, ref mist);
            Add(c01, a1 * u0, p.Z, end, ref dust, ref mist);
            Add(c11, b1 * u1, p.Z, end, ref dust, ref mist);
            return (dust, mist);
        }

        private void Add(GroundMist.Cell c, float w, float z, float end, ref float dust, ref float mist)
        {
            if (w <= 0f || end < c.Floor - Drop) return;
            dust += w * c.Dust * Profile(z - c.Floor, c.Top);
            mist += w * c.Mist * Profile(z - c.Floor, Sky.W);
        }

        /// <summary><c>CxAirDust</c>: thickest on the floor, nothing at <paramref name="top"/>, integrating to 1.</summary>
        private static float Profile(float z, float top) => top <= 0f || z < 0f || z >= top ? 0f : 2f * (1f - z / top) / top;

        /// <summary>The light the lamps lay on air at <paramref name="at"/>: each colour made linear, falling off as
        /// <c>CxAirLit</c> does.</summary>
        internal Vector3 LampsAt(ReadOnlySpan<Point> lamps, Vector3 at)
        {
            Vector3 sum = Vector3.Zero;
            for (int i = 0; i < Math.Min(lamps.Length, Lamps); i++)
            {
                float left = lamps[i].Reach > 0f ? Math.Clamp(1f - Vector3.Distance(lamps[i].At, at) / lamps[i].Reach, 0f, 1f) : 0f;
                Vector3 c = lamps[i].Colour;
                sum += new Vector3(Lin(c.X), Lin(c.Y), Lin(c.Z)) * (left * left);
            }
            return sum;
        }

        private static float Lin(float c) => c > 0f ? MathF.Pow(c, AlloyLights.SrgbGamma) : 0f;

        /// <summary>March the eye's ray from <paramref name="eye"/> to the surface <paramref name="surf"/> at step offset
        /// <paramref name="jitter"/>: what it keeps of the surface and the light the air adds over it.</summary>
        internal (float Keep, Vector3 Added) March(Vector3 eye, Vector3 surf, Shine room, ReadOnlySpan<Point> lamps, float jitter = 0.5f)
        {
            Vector3 ray = surf - eye;
            float len = MathF.Min(ray.Length(), Far);
            if (Head.W < 0.5f || len <= 0f) return (1f, Vector3.Zero);
            Vector3 dir = Vector3.Normalize(ray);
            float t0 = 0f, t1 = len;
            if (MathF.Abs(dir.Z) > 1e-6f)
            {
                float ta = (Slab.X - eye.Z) / dir.Z, tb = (Slab.Y - eye.Z) / dir.Z;
                t0 = MathF.Max(t0, MathF.Min(ta, tb));
                t1 = MathF.Min(t1, MathF.Max(ta, tb));
            }
            else if (eye.Z < Slab.X || eye.Z > Slab.Y) return (1f, Vector3.Zero);
            if (t1 <= t0) return (1f, Vector3.Zero);
            float dl = (t1 - t0) / Steps;
            Span<float> rd = stackalloc float[Steps], rm = stackalloc float[Steps];
            int first = -1, last = -1;
            for (int k = 0; k < Steps; k++)
            {
                (float d, float m) = At(eye + dir * (t0 + dl * (k + jitter)), surf.Z);
                rd[k] = d * Sigma * dl;
                rm[k] = m * Sigma * dl;
                if (rd[k] + rm[k] <= 0f) continue;
                if (first < 0) first = k;
                last = k;
            }
            if (first < 0) return (1f, Vector3.Zero);
            Span<Vector3> probe = stackalloc Vector3[Probes];
            float span = last - first;
            for (int i = 0; i < Probes; i++)
                probe[i] = LampsAt(lamps, eye + dir * (t0 + dl * (first + span * i / (Probes - 1) + jitter)));
            Vector3 dusty = room.Ambient, sky = room.Fog + room.Diffuse, added = Vector3.Zero;
            float keep = 1f;
            for (int k = first; k <= last; k++)
            {
                float tau = rd[k] + rm[k];
                if (tau <= 0f) continue;
                float s = span > 0f ? (k - first) / span * (Probes - 1) : 0f;
                int i = Math.Min((int)s, Probes - 2);
                Vector3 lamp = Vector3.Lerp(probe[i], probe[i + 1], s - i);
                float a = 1f - MathF.Exp(-tau);
                added += keep * a * (rd[k] * (dusty + lamp) + rm[k] * (sky + lamp)) / tau;
                keep *= MathF.Exp(-tau);
            }
            return (keep, added);
        }
    }
}
