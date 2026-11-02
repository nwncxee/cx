using System;
using System.Numerics;

namespace CX.Alloy;

/// <summary>
/// The deferred walk goldens' scenes, built by hand: a camera, planes hit per pixel with the walk's own
/// reconstruction, and seeded light sets. Each pixel's record and each light's word are the formats
/// <c>cx_gbuf</c> and <c>cx_lightfrag</c> document.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The rig's frame size: eight by four 16-pixel groups, two froxel columns.</summary>
    private const int DwW = 128, DwH = 64;

    /// <summary><c>cx_gbuf</c>'s flag bits the scenes set.</summary>
    private const int GbDeferred = 1, GbBaked = 2, GbFilm = 8, GbStockLobe = 32;

    /// <summary>One surface's material, in the G-buffer's own terms.</summary>
    private readonly record struct DwMat(float Rough, float Spec, float Fresnel, float Coat, float CoatRough,
                                         Vector3 SpecC, int Slot, Vector3 Albedo, int Flags);

    private static readonly DwMat DwStone = new(0.7f, 0.25f, 0.5f, 0f, 0f, new Vector3(0.04f), 0, new Vector3(0.55f, 0.5f, 0.45f), 0);
    private static readonly DwMat DwGlossy = new(0.1f, 0.8f, 1f, 0f, 0f, new Vector3(0.05f), 0, new Vector3(0.3f), 0);
    private static readonly DwMat DwCoated = new(0.45f, 0.5f, 1f, 1f, 0.05f, new Vector3(0.04f), 0, new Vector3(0.6f, 0.1f, 0.1f), 0);
    private static readonly DwMat DwSheen = new(0.8f, 0.2f, 0.5f, 0f, 0f, new Vector3(0.03f), 1, new Vector3(0.2f, 0.25f, 0.6f), 0);
    private static readonly DwMat DwSkin = new(0.5f, 0.3f, 0.6f, 0f, 0f, new Vector3(0.028f), 2, new Vector3(0.8f, 0.55f, 0.45f), GbStockLobe);
    private static readonly DwMat DwFilm = new(0.3f, 0.4f, 1f, 0.8f, 0.3f, new Vector3(0.04f), 0, new Vector3(0.35f), GbFilm | GbBaked);
    private static readonly DwMat DwRing = new(0.2f, 0.9f, 1f, 1f, 0.03f, new Vector3(0.9f, 0.8f, 0.6f), 0, new Vector3(0.1f), 0);

    /// <summary>One plane of a scene: a point, its normal, and the material a pixel takes where it is hit;
    /// <see cref="Where"/> limits it to a pixel region (null = everywhere it is the nearest hit), and
    /// <see cref="Skew"/> puts the frame depth that share past the record's -- a coarse fragment's offset.</summary>
    private sealed record DwPlane(Vector3 At, Vector3 N, Func<int, int, DwMat> Mat, Func<int, int, bool>? Where = null,
                                  Func<int, int, float>? Skew = null);

    /// <summary>The scene camera: 60 degrees vertical, reversed depth, looking from <paramref name="eye"/> at
    /// <paramref name="target"/>; the near and far planes the froxel grid spans.</summary>
    private static DeferredRigScene DwCamera(Vector3 eye, Vector3 target)
    {
        const float near = 0.5f, far = 120f;
        float t = MathF.Tan(MathF.PI / 6f), aspect = (float)DwW / DwH;
        var proj = new Matrix4x4(1f / (t * aspect), 0, 0, 0, 0, 1f / t, 0, 0, 0, 0, near / (far - near), -1f,
                                 0, 0, near * far / (far - near), 0);
        return new DeferredRigScene
        {
            W = DwW, H = DwH, Eye = eye, Near = near, Far = far, Proj = proj,
            View = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY),
        };
    }

    /// <summary>Each pixel's nearest plane hit along the ray the walk reconstructs it on, written as the twin
    /// would; the record texel's slots split the frame in two and its visibilities are a stripe pattern.</summary>
    private static void DwSurfaces(DeferredRigScene sc, DwPlane[] planes)
    {
        Matrix4x4.Invert(sc.View * sc.Proj, out Matrix4x4 inv);
        Vector3 fwd = Vector3.Normalize(new Vector3(-sc.View.M13, -sc.View.M23, -sc.View.M33));
        sc.Pixels = new float[DwW * DwH * DeferredRigScene.PixelFloats];
        for (int y = 0; y < DwH; y++)
            for (int x = 0; x < DwW; x++)
            {
                float nx = 2f * (x + 0.5f) / DwW - 1f, ny = 2f * (y + 0.5f) / DwH - 1f;
                Vector4 a = Vector4.Transform(new Vector4(nx, ny, 1f, 1f), inv), b = Vector4.Transform(new Vector4(nx, ny, 0.5f, 1f), inv);
                Vector3 o = new Vector3(a.X, a.Y, a.Z) / a.W, dir = Vector3.Normalize(new Vector3(b.X, b.Y, b.Z) / b.W - o);
                float best = float.MaxValue;
                DwPlane? hit = null;
                foreach (DwPlane p in planes)
                {
                    if (p.Where != null && !p.Where(x, y)) continue;
                    float den = Vector3.Dot(p.N, dir);
                    if (MathF.Abs(den) < 1e-6f) continue;
                    float s = Vector3.Dot(p.At - o, p.N) / den;
                    if (s > 0f && s < best) { best = s; hit = p; }
                }
                if (hit == null) continue;
                Vector3 pos = o + dir * best;
                float depth = Vector3.Dot(pos - sc.Eye, fwd);
                if (depth <= sc.Near || depth >= sc.Far) continue;
                DwMat m = hit.Mat(x, y);
                int at = (y * DwW + x) * DeferredRigScene.PixelFloats;
                float[] px = sc.Pixels;
                px[at] = GbDeferred | m.Flags;
                px[at + 1] = hit.N.X; px[at + 2] = hit.N.Y; px[at + 3] = hit.N.Z;
                px[at + 4] = m.Rough; px[at + 5] = m.Spec; px[at + 6] = m.Fresnel; px[at + 7] = m.Coat; px[at + 8] = m.CoatRough;
                px[at + 9] = m.SpecC.X; px[at + 10] = m.SpecC.Y; px[at + 11] = m.SpecC.Z;
                px[at + 12] = m.Slot;
                px[at + 13] = 1f;
                px[at + 14] = m.Albedo.X; px[at + 15] = m.Albedo.Y; px[at + 16] = m.Albedo.Z;
                px[at + 22] = depth;
                px[at + 38] = hit.Skew?.Invoke(x, y) ?? 0f;
                bool left = x < DwW / 2;
                for (int k = 0; k < 4; k++)
                {
                    px[at + 30 + k] = left ? k : 4 + k;
                    px[at + 34 + k] = ((x / 6 + y / 5 + k) % 3) * 0.5f;
                }
            }
    }

    /// <summary><paramref name="n"/> lights from a fixed seed inside the box <paramref name="lo"/>..<paramref name="hi"/>,
    /// radius <paramref name="r0"/>..<paramref name="r1"/>; the first <paramref name="cubes"/> cast shadows from cube
    /// slots 0 up, and every <paramref name="staticEvery"/>th after them is a static light with no cube.</summary>
    private static void DwLights(DeferredRigScene sc, int n, Vector3 lo, Vector3 hi, float r0, float r1, int cubes,
                                 int staticEvery, uint seed)
    {
        uint s = seed;
        float Next() { s = s * 1664525u + 1013904223u; return (s >> 8) / 16777216f; }
        sc.Lights = new float[n * DeferredRigScene.LightFloats];
        sc.Cubes = new Vector4[cubes];
        for (int i = 0; i < n; i++)
        {
            var p = new Vector3(lo.X + (hi.X - lo.X) * Next(), lo.Y + (hi.Y - lo.Y) * Next(), lo.Z + (hi.Z - lo.Z) * Next());
            float r = r0 + (r1 - r0) * Next();
            var c = new Vector3(0.3f + 0.7f * Next(), 0.3f + 0.7f * Next(), 0.3f + 0.7f * Next());
            float word = i < cubes ? i : staticEvery > 0 && (i - cubes) % staticEvery == staticEvery - 1 ? 255f : -1f;
            if (i < cubes) sc.Cubes[i] = new Vector4(p, r);
            int at = i * DeferredRigScene.LightFloats;
            sc.Lights[at] = p.X; sc.Lights[at + 1] = p.Y; sc.Lights[at + 2] = p.Z; sc.Lights[at + 3] = r;
            sc.Lights[at + 4] = c.X; sc.Lights[at + 5] = c.Y; sc.Lights[at + 6] = c.Z; sc.Lights[at + 7] = word;
        }
    }

    /// <summary>The sheen records the scenes' slots 1 and 2 name: a cloth's blue sheen, a skin's warm one.</summary>
    private static readonly Vector4[] DwSheenRecords = [Vector4.Zero, new(0.6f, 0.5f, 0.4f, 0.5f), new(0.5f, 0.3f, 0.25f, 0.6f)];

    /// <summary>E3: a stone wall and its baked floor, 40 lights of mixed colour and reach, 8 casting shadows. Every
    /// other floor column's record sits 1.5% off the frame depth: past the bare tolerance, inside the floor's slope.</summary>
    private static DeferredRigScene DwWall()
    {
        DeferredRigScene sc = DwCamera(new Vector3(0f, 2f, 10f), new Vector3(0f, 1.5f, 0f));
        DwSurfaces(sc, [new(Vector3.Zero, Vector3.UnitZ, (_, _) => DwStone),
                        new(Vector3.Zero, Vector3.UnitY, (_, _) => DwStone with { Flags = GbBaked },
                            Skew: (x, _) => (x & 1) * 0.015f)]);
        DwLights(sc, 40, new Vector3(-8f, 0.3f, 0.3f), new Vector3(8f, 4f, 3f), 2f, 8f, 8, 5, 3889u);
        sc.Sheen = DwSheenRecords;
        return sc;
    }

    /// <summary>E4: six materials side by side on one wall under the same 16 lights, the film strip baked, with three
    /// static lamps in front of it (the last on cube slot 4) for the film pass to mirror.</summary>
    private static DeferredRigScene DwMaterials()
    {
        DwMat[] strips = [DwStone, DwGlossy, DwCoated, DwSheen, DwSkin, DwFilm];
        DeferredRigScene sc = DwCamera(new Vector3(0f, 2f, 10f), new Vector3(0f, 1.5f, 0f));
        DwSurfaces(sc, [new(Vector3.Zero, Vector3.UnitZ, (x, _) => strips[x * strips.Length / DwW]),
                        new(Vector3.Zero, Vector3.UnitY, (x, _) => strips[x * strips.Length / DwW])]);
        DwLights(sc, 16, new Vector3(-7f, 0.5f, 0.5f), new Vector3(7f, 4f, 3f), 3f, 9f, 4, 3, 1201u);
        float[] film = [5f, 1f, 1.5f, 6f, 1f, 0.9f, 0.7f, 255f,  6f, 0.5f, 2f, 6f, 0.7f, 0.8f, 1f, 255f,
                        4.5f, 2f, 1f, 7f, 1f, 1f, 1f, 260f];
        sc.Lights = [.. sc.Lights, .. film];
        sc.Cubes = [.. sc.Cubes, new Vector4(4.5f, 2f, 1f, 7f)];
        sc.Sheen = DwSheenRecords;
        return sc;
    }

    /// <summary>E5: skin and a coated metal ring alternating every three pixels, under 12 lights.</summary>
    private static DeferredRigScene DwRingScene()
    {
        DeferredRigScene sc = DwCamera(new Vector3(0f, 1.5f, 4f), new Vector3(0f, 1.5f, 0f));
        DwSurfaces(sc, [new(Vector3.Zero, Vector3.UnitZ, (x, _) => (x / 3) % 2 == 0 ? DwSkin : DwRing)]);
        DwLights(sc, 12, new Vector3(-3f, 0.5f, 0.5f), new Vector3(3f, 3f, 2.5f), 2f, 6f, 4, 0, 77u);
        sc.Sheen = DwSheenRecords;
        return sc;
    }

    /// <summary>E6: a door frame 4 m off and the room wall 25 m off seen through it, a lamp in the doorway reaching
    /// both, and six more about the frame.</summary>
    private static DeferredRigScene DwDoorway()
    {
        DeferredRigScene sc = DwCamera(new Vector3(0f, 1.6f, 10f), new Vector3(0f, 1.6f, 0f));
        DwSurfaces(sc, [new(new Vector3(0f, 0f, 6f), Vector3.UnitZ, (_, _) => DwStone,
                            (x, y) => x < DwW * 3 / 10 || x >= DwW * 7 / 10 || y < DwH / 5),
                        new(new Vector3(0f, 0f, -15f), Vector3.UnitZ, (_, _) => DwStone)]);
        DwLights(sc, 7, new Vector3(-3f, 0.5f, 6.2f), new Vector3(3f, 3f, 7.5f), 3f, 6f, 2, 0, 4242u);
        float[] lamp = [0f, 1.8f, 5.5f, 30f, 1f, 0.8f, 0.5f, 2f];
        lamp.CopyTo(sc.Lights, 2 * DeferredRigScene.LightFloats);
        Array.Resize(ref sc.Cubes, 3);
        sc.Cubes[2] = new Vector4(0f, 1.8f, 5.5f, 30f);
        sc.Sheen = DwSheenRecords;
        return sc;
    }

    /// <summary>E7: 130 lamps hung over one patch of stone floor, every one reaching it.</summary>
    private static DeferredRigScene DwCrowd()
    {
        DeferredRigScene sc = DwCamera(new Vector3(0f, 6f, 6f), new Vector3(0f, 0f, 0f));
        DwSurfaces(sc, [new(Vector3.Zero, Vector3.UnitY, (_, _) => DwStone)]);
        DwLights(sc, 130, new Vector3(-2f, 0.5f, -2f), new Vector3(2f, 1.5f, 2f), 6f, 8f, 4, 0, 130u);
        sc.Sheen = DwSheenRecords;
        return sc;
    }
}
