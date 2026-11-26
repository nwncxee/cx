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

    /// <summary>The attenuation pair the engine latches at its shipped lighting settings: maximum intensity 1.5, intensity
    /// 0.2 at range and a cutoff range multiplier of 2, the defaults both clients bind, as <see cref="AlloyLights.LatchAtten"/> forms it.</summary>
    internal static readonly Vector2 ShippedPair = new(MathF.Pow(1.5f, -AlloyLights.SrgbGamma),
                                                       4f * (MathF.Pow(0.2f, -AlloyLights.SrgbGamma) - MathF.Pow(1.5f, -AlloyLights.SrgbGamma)));

    /// <summary>The shipped stage's text.</summary>
    internal static string Stage => ShaderSource.Get(AirScene.ShdCs + ".glsl");

    /// <summary>The value of the <c>const</c> <paramref name="name"/> the dust stage, or <paramref name="stage"/>'s text,
    /// declares, int or float; NaN when it declares none.</summary>
    internal static float Const(string name, string? stage = null)
    {
        string src = stage ?? Stage;
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
        internal readonly float Sigma, Albedo, Step, Drop, Wall, Far, Climb;
        internal readonly int Steps, Probes, Lamps;

        internal Field(byte[] bytes)
        {
            _bytes = bytes;
            Head = MemoryMarshal.Read<Vector4>(bytes);
            Sky = MemoryMarshal.Read<Vector4>(bytes.AsSpan(GroundMist.SkyAt));
            Slab = MemoryMarshal.Read<Vector4>(bytes.AsSpan(GroundMist.SlabAt));
            Sigma = Const("CX_AIR_SIGMA");
            Albedo = Const("CX_AIR_ALBEDO");
            Step = Const("CX_AIR_STEP");
            Drop = Const("CX_AIR_DROP");
            Wall = Const("CX_AIR_WALL");
            Far = Const("CX_AIR_FAR");
            Climb = Const("CX_AIR_CLIMB");
            Steps = (int)Const("CX_AIR_STEPS");
            Probes = (int)Const("CX_AIR_PROBES");
            Lamps = (int)Const("CX_AIR_LAMPS");
        }

        /// <summary>Does the stage declare every number the march takes?</summary>
        internal bool Declared => !float.IsNaN(Sigma + Albedo + Step + Drop + Wall + Far + Climb + Const("CX_AIR_STEPS") + Const("CX_AIR_PROBES") + Const("CX_AIR_LAMPS"));

        /// <summary>Column (<paramref name="x"/>, <paramref name="y"/>) of the field, or one with no floor off it.</summary>
        internal GroundMist.Cell Cell(int x, int y)
        {
            int side = (int)Head.Z;
            if ((uint)x >= (uint)side || (uint)y >= (uint)side) return new GroundMist.Cell(-1e9f, 0f, 0f, 0f);
            return MemoryMarshal.Cast<byte, GroundMist.Cell>(_bytes.AsSpan(GroundMist.Head)).Slice(y * side, side)[x];
        }

        /// <summary><c>CxAirQuarterAt</c>: the floor of half-metre quarter (<paramref name="x"/>, <paramref name="y"/>), or none off the field.</summary>
        private float QuarterAt(int x, int y)
        {
            int side = (int)Head.Z, cx = x >> 1, cy = y >> 1;
            if ((uint)cx >= (uint)side || (uint)cy >= (uint)side) return -1e9f;
            Vector4 q = MemoryMarshal.Cast<byte, Vector4>(_bytes.AsSpan(GroundMist.QuartersAt)).Slice(cy * side, side)[cx];
            return ((y & 1) * 2 + (x & 1)) switch { 0 => q.X, 1 => q.Y, 2 => q.Z, _ => q.W };
        }

        /// <summary><c>CxAirQuarter</c>: the floor under (<paramref name="x"/>, <paramref name="y"/>) from the four nearest
        /// quarters, climbing across the joins; none where none has one.</summary>
        internal float Quarter(float x, float y)
        {
            float qx = (x - Head.X) * 2f - 0.5f, qy = (y - Head.Y) * 2f - 0.5f;
            int ix = (int)MathF.Floor(qx), iy = (int)MathF.Floor(qy);
            float fx = qx - ix, fy = qy - iy;
            float a = QuarterAt(ix, iy), b = QuarterAt(ix + 1, iy), d = QuarterAt(ix, iy + 1), e = QuarterAt(ix + 1, iy + 1);
            (float a0, float b0) = Climbed(fx, a, b);
            (float a1, float b1) = Climbed(fx, d, e);
            (float l0, float u0) = Climbed(fy, a, d);
            (float l1, float u1) = Climbed(fy, b, e);
            float wa = a < Wall ? 0f : a0 * l0, wb = b < Wall ? 0f : b0 * l1, wd = d < Wall ? 0f : a1 * u0, we = e < Wall ? 0f : b1 * u1;
            float sum = wa + wb + wd + we;
            return sum > 0f ? (wa * a + wb * b + wd * d + we * e) / sum : -1e9f;
        }

        /// <summary><c>CxAirClimb</c>: the shares of two neighbouring quarters of floors <paramref name="a"/> and <paramref name="b"/>
        /// a fraction <paramref name="f"/> across, the floor climbing a join over <see cref="Climb"/> metres a metre of rise.</summary>
        private (float A, float B) Climbed(float f, float a, float b)
        {
            if (!Joined(a, b)) return Pair(f, false);
            float w = MathF.Min(Climb * MathF.Abs(a - b) * 2f, 1f);
            if (MathF.Min(a, b) < Wall || w <= 1e-4f) return Pair(f, true);
            float t = Math.Clamp((f - 0.5f) / w + 0.5f, 0f, 1f);
            return (1f - t, t);
        }

        /// <summary><c>CxAirBase</c>: the floor a cell of floor <paramref name="a"/> lays its air over where the quarter's is <paramref name="here"/>.</summary>
        private float Base(float a, float here) => here > Wall && MathF.Abs(a - here) <= Step ? here : a;

        /// <summary>Do two neighbouring columns with floors <paramref name="a"/> and <paramref name="b"/> blend: one floor,
        /// or a floor beside a column with none?</summary>
        internal bool Joined(float a, float b) => MathF.Abs(a - b) <= Step || MathF.Min(a, b) < Wall;

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
            float w00 = a0 * l0, w10 = b0 * l1, w01 = a1 * u0, w11 = b1 * u1;
            float top = Floored(c00, w00) + Floored(c10, w10) + Floored(c01, w01) + Floored(c11, w11);
            float dust = 0f, mist = 0f, here = Quarter(p.X, p.Y);
            Add(c00, w00, p.Z, end, top, here, ref dust, ref mist);
            Add(c10, w10, p.Z, end, top, here, ref dust, ref mist);
            Add(c01, w01, p.Z, end, top, here, ref dust, ref mist);
            Add(c11, w11, p.Z, end, top, here, ref dust, ref mist);
            return (dust, mist);
        }

        private float Floored(GroundMist.Cell c, float w) => c.Floor < Wall ? 0f : w;

        private void Add(GroundMist.Cell c, float w, float z, float end, float top, float here, ref float dust, ref float mist)
        {
            if (w <= 0f || end < c.Floor - Drop) return;
            float h = z - Base(c.Floor, here);
            dust += w * c.Dust * Profile(h, c.Top);
            mist += w * c.Mist * Profile(h, Sky.W * top);
        }

        /// <summary><c>CxAirDust</c>: thickest on the floor, nothing at <paramref name="top"/>, integrating to 1.</summary>
        private static float Profile(float z, float top) => top <= 0f || z < 0f || z >= top ? 0f : 2f * (1f - z / top) / top;

        /// <summary>The light the lamps lay on air at <paramref name="at"/>: each colour made linear, falling off as
        /// <c>CxAirLamp</c> does over the attenuation pair <paramref name="pair"/> (zero: the one the lit stage holds).</summary>
        internal Vector3 LampsAt(ReadOnlySpan<Point> lamps, Vector3 at, Vector2 pair = default)
        {
            Vector3 sum = Vector3.Zero;
            for (int i = 0; i < Math.Min(lamps.Length, Lamps); i++)
            {
                Vector3 c = lamps[i].Colour;
                sum += new Vector3(Lin(c.X), Lin(c.Y), Lin(c.Z)) * Lamp(lamps[i], at, pair);
            }
            return sum;
        }

        /// <summary><c>CxAirLamp</c>: the lit stage's falloff over the pair, its master gain and light.binreach's window.</summary>
        internal static float Lamp(Point lamp, Vector3 at, Vector2 pair = default)
        {
            float f2 = Vector3.DistanceSquared(lamp.At, at) / MathF.Max(lamp.Reach * lamp.Reach, 1e-8f);
            if (f2 >= 1f) return 0f;
            float fade = 1f - f2, reach = AlloyLights.ReachUniform;
            if (reach > 0f)
            {
                float r2 = reach * reach;
                if (f2 > r2) return 0f;
                fade *= MathF.Min((r2 - f2) / (r2 * 0.3f), 1f);
            }
            if (!(pair.X > 0f)) pair = AlloyLights.Atten.X > 0f ? AlloyLights.Atten : Vector2.One;
            return Lighting.Gain.V * fade / (pair.X + pair.Y * f2);
        }

        private static float Lin(float c) => c > 0f ? MathF.Pow(c, AlloyLights.SrgbGamma) : 0f;

        /// <summary>What the eye at <paramref name="eye"/> keeps of <paramref name="surf"/> over the exact floor
        /// <paramref name="floor"/>: each of <paramref name="steps"/> samples lays its own column's air from that floor.</summary>
        internal float ExactKeep(Vector3 eye, Vector3 surf, Func<float, float, float> floor, int steps)
        {
            Vector3 ray = surf - eye;
            float dl = ray.Length() / steps, tau = 0f;
            for (int k = 0; k < steps; k++)
            {
                Vector3 p = eye + ray * ((k + 0.5f) / steps);
                GroundMist.Cell c = Cell((int)MathF.Floor(p.X - Head.X), (int)MathF.Floor(p.Y - Head.Y));
                float z = p.Z - floor(p.X, p.Y);
                tau += (c.Dust * Profile(z, c.Top) + c.Mist * Profile(z, Sky.W)) * Sigma * dl;
            }
            return MathF.Exp(-tau);
        }

        /// <summary>March the eye's ray from <paramref name="eye"/> to the surface <paramref name="surf"/> at step offset
        /// <paramref name="jitter"/>: what it keeps of the surface and the light the air adds over it.</summary>
        internal (float Keep, Vector3 Added) March(Vector3 eye, Vector3 surf, Shine room, ReadOnlySpan<Point> lamps, float jitter = 0.5f,
                                                   Vector2 pair = default)
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
                probe[i] = LampsAt(lamps, eye + dir * (t0 + dl * (first + span * i / (Probes - 1) + jitter)), pair);
            Vector3 dusty = room.Ambient, sky = Slab.Z > 0.5f ? dusty : room.Fog + room.Diffuse, added = Vector3.Zero;
            float keep = 1f;
            for (int k = first; k <= last; k++)
            {
                float tau = rd[k] + rm[k];
                if (tau <= 0f) continue;
                float s = span > 0f ? (k - first) / span * (Probes - 1) : 0f;
                int i = Math.Min((int)s, Probes - 2);
                Vector3 lamp = Vector3.Lerp(probe[i], probe[i + 1], s - i);
                float a = 1f - MathF.Exp(-tau);
                added += keep * a * (rd[k] * Albedo * (dusty + lamp) + rm[k] * (Slab.Z > 0.5f ? Albedo : 1f) * (sky + lamp)) / tau;
                keep *= MathF.Exp(-tau);
            }
            return (keep, added);
        }
    }
}
