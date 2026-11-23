using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

internal static partial class WorldDepth
{
    /// <summary>The wash's pull against its geometry. Oracle: the view point scaled toward the eye by 1 - 2^-16,
    /// put through a GL perspective (near 0.5, far 1000, jittered) built here and folded, in double; a
    /// buried sheet and a coplanar one are then laid along one ray and compared as the GPU would, in float.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void PullSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        const float n = 0.5f, f = 1000f;
        var gl = new Matrix4x4
        {
            M11 = 1.2f, M22 = 1.6f, M31 = 0.0007f, M32 = -0.0011f,
            M33 = -(f + n) / (f - n), M34 = -1f, M43 = -2f * f * n / (f - n),
        };
        Matrix4x4 p = Fold(gl);
        float pull = Pull(n, f);
        double s = 1.0 - 1.0 / 65536;
        foreach (Vector3 v in (ReadOnlySpan<Vector3>)[new(0f, 0f, -3f), new(12f, -4f, -100f), new(-40f, 25f, -700f)])
        {
            var plain = Clip(p, v, 1.0);
            var scaled = Clip(p, v, s);
            double mirror = (plain.Z + pull) / plain.W, oracle = scaled.Z / scaled.W;
            double lift = oracle - plain.Z / plain.W;
            check.Expect(lift > 0 && Math.Abs(mirror - oracle) < 1e-3 * lift,
                $"at {-v.Z} m the pulled depth {mirror:R} is the depth of the point pulled 2^-16 toward the eye, {oracle:R}");
            check.Expect(Math.Abs(plain.X / plain.W - scaled.X / scaled.W) < 1e-6 && Math.Abs(plain.Y / plain.W - scaled.Y / scaled.W) < 1e-6,
                $"at {-v.Z} m the pull leaves the point where it was on screen, jitter included");
        }
        check.Expect(Pull(0f, 0f) == 0f && Pull(1f, 1f) == 0f, $"an orthographic pass, which has no planes, is not pulled ({Pull(0f, 0f)})");

        float pitch = 5f * MathF.PI / 180f;
        var ray = Vector3.Normalize(new Vector3(0f, -MathF.Sin(pitch), -MathF.Cos(pitch)));
        float ground = Depth(p, ray * 100f, 0f), buried = Depth(p, ray * (100f + 0.01f / MathF.Sin(pitch)), pull);
        check.Expect(buried < ground, $"a sheet 1 cm under ground 100 m off at 5 degrees of pitch stays behind it ({buried:R} vs {ground:R})");
        var down = new Vector3(0f, 0f, -1f);
        float near600 = Depth(p, down * 600.01f, pull), at600 = Depth(p, down * 600f, 0f);
        float near700 = Depth(p, down * 700.01f, pull), at700 = Depth(p, down * 700f, 0f);
        check.Expect(near600 < at600 && near700 > at700,
            $"square on, a sheet 1 cm under stays behind at 600 m and breaks through by 700 m, past 0.01 / 2^-16 = 655 m ({near600:R} < {at600:R}, {near700:R} > {at700:R})");
        foreach (float d in (ReadOnlySpan<float>)[3f, 100f, 900f])
        {
            float lies = Depth(p, ray * d, 0f), pulled = Depth(p, ray * d, pull);
            int steps = BitConverter.SingleToInt32Bits(pulled) - BitConverter.SingleToInt32Bits(lies);
            check.Expect(steps >= 128, $"a sheet lying on the surface {d} m off wins by {steps} float steps, at least 128");
        }

        const string vs = "#version 460 core\nvoid main() { gl_Position = vec4(0.0); }\n", fs = "#version 460 core\nout vec4 o;\nvoid main() { o = vec4(1.0); }\n";
        string? on = Translate(vs, fs, new Dictionary<string, uint>(), pull: true).Vs, off = Translate(vs, fs, new Dictionary<string, uint>()).Vs;
        check.Expect(on != null && on.Contains("void cxPullMain() { gl_Position = vec4(0.0); }", StringComparison.Ordinal)
                     && on.Contains("cxPullMain();\n    gl_Position.z += uPull;\n}", StringComparison.Ordinal),
            $"a pulled stage runs its own main, then adds the push member to clip z ({on})");
        check.Expect(off != null && !off.Contains("cxPullMain", StringComparison.Ordinal), $"an unpulled stage keeps its main as written ({off})");
        log.Info(check.Ok ? "WorldDepth.PullSelfCheck: passed" : "WorldDepth.PullSelfCheck: FAILED");

        static (double X, double Y, double Z, double W) Clip(in Matrix4x4 m, Vector3 v, double k)
        {
            double x = v.X * k, y = v.Y * k, z = v.Z * k;
            return (x * m.M11 + y * m.M21 + z * m.M31 + m.M41, x * m.M12 + y * m.M22 + z * m.M32 + m.M42,
                    x * m.M13 + y * m.M23 + z * m.M33 + m.M43, x * m.M14 + y * m.M24 + z * m.M34 + m.M44);
        }

        static float Depth(in Matrix4x4 m, Vector3 v, float add)
        {
            Vector4 c = Vector4.Transform(new Vector4(v, 1f), m);
            return (c.Z + add) / c.W;
        }
    }
}
