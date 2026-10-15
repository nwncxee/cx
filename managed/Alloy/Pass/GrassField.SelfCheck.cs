using System.Collections.Generic;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

internal static unsafe partial class GrassField
{
    /// <summary>Off-device. Three decisions that each fail silently on a GPU: the scatter must be
    /// DETERMINISTIC (a rebuild that reshuffles the meadow reads as grass twitching at every area reload) and
    /// must scale with the density dial; the frustum test must actually reject; and the box distance must be
    /// the NEAREST point, or a patch the camera stands inside is culled by its own centre.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        GrassAtlas.SelfCheck(log);

        uint a = 12345u, b = 12345u;
        float first = Rnd(ref a);
        check.Expect(first == Rnd(ref b), "the same seed yields the same first blade");
        float thin = Grass.SnowThin.V, fall = Grass.SnowFall.V;
        Grass.SnowThin.V = 0.85f; Grass.SnowFall.V = 0.1f;
        check.Expect(SnowThinShare(0f, 0f) == 0f && SnowThinShare(0f, 1f) == 0.9f && SnowThinShare(1f, 0f) == 0.85f && SnowThinShare(1f, 1f) == 0.9f,
          "falling snow thins to the snowfall share, cover to its own, and the deeper cut wins");
        Grass.SnowThin.V = thin; Grass.SnowFall.V = fall;
        // #478: a blade never reads the screen-space visibility -- the pixel's occlusion is the surface behind it
        check.Expect(!GrassFsHead.Contains("uSsao") && !GrassFsMain.Contains("uSsao"), "the grass stage takes no SSAO tap");
        check.Expect(first != Rnd(ref a), "and the stream advances");
        float lo = 1f, hi = 0f;
        uint st = 99u;
        for (int i = 0; i < 4096; i++) { float r = Rnd(ref st); lo = MathF.Min(lo, r); hi = MathF.Max(hi, r); }
        check.Expect(lo >= 0f && hi < 1f && lo < 0.01f && hi > 0.99f, "the stream covers [0,1) without leaving it");

        // density scales the engine's authored per-face counts rather than replacing them, and a face that
        // asks for a meadow of its own is capped instead of scattering a million quads off one triangle
        check.Expect(BladesForFace(7, 2f) == 14 && BladesForFace(7, 0.5f) == 3,
          "the density dial multiplies the tileset's own count, truncating");
        check.Expect(BladesForFace(1_000_000, 1f) == 4096, "a runaway face count is capped at 4096 blades");

        // #355: the ladder is continuous at its knee, falls as 1/d^2, and never drops below its floor
        check.Expect(Keep(Grass.Lod.V) == 1f && Keep(Grass.Lod.V * 0.5f) == 1f, "inside the full-density radius every blade draws");
        check.Expect(MathF.Abs(Keep(Grass.Lod.V * 2f) - MathF.Max(0.25f, Grass.LodMin.V)) < 1e-6f, "twice the radius keeps a quarter");
        check.Expect(Keep(Grass.Lod.V * 1000f) == Grass.LodMin.V, "and the floor holds far out");
        bool mono = true;
        for (float d = 1f, prev = 1f; d < 400f; d += 1f) { float k = Keep(d); if (k > prev) mono = false; prev = k; }
        check.Expect(mono, "keep never rises with distance");
        // CX-24: the scatter's record and material strides are C# layouts copied into its GLSL; a drift reads
        // every card out of its neighbour's record, silently
        check.Expect(ShaderSource.Get("cx_gbladebuf.glsl").Contains($"const int CX_GRASS_MATV = {MatVec4};") && ScatterCs.Contains($"const uint CX_GRASS_RECV = {InstFloats / 4}u;"),
          "the scatter's material and record strides match GrassField's");
        check.Expect(GrassVs.Contains("layout(location = 4) in vec4 iNrm;") && InstFloats == 12,
          "the card stage reads the scatter's third vec4 as the ground normal");
        check.Expect(ScatterCs.Contains("return max(uLod.y, r * r);"), "the scatter's keep is GrassField.Keep's");
        check.Expect(BoxFar(new Vector3(0f), new Vector3(1f), new Vector3(0f)) == MathF.Sqrt(3f)
                     && BoxFar(new Vector3(0f), new Vector3(1f), new Vector3(2f, 0.5f, 0.5f)) == MathF.Sqrt(4.5f),
          "the far box distance is to the farthest corner -- a patch straddling the blade radius feeds both blade draws");

        // an identity VP: clip == world, so the frustum is the [-1,1] cube
        Matrix4x4 id = Matrix4x4.Identity;
        Span<Vector4> planes = stackalloc Vector4[6];
        Frustum(in id, planes);
        check.Expect(Visible(new Vector3(-0.5f), new Vector3(0.5f), planes), "a box inside the cube is visible");
        check.Expect(!Visible(new Vector3(2f, 2f, 2f), new Vector3(3f, 3f, 3f), planes), "a box past +x/+y/+z is culled");
        check.Expect(!Visible(new Vector3(-3f), new Vector3(-2f), planes), "and one past -x/-y/-z is too");
        check.Expect(Visible(new Vector3(-2f, -0.1f, -0.1f), new Vector3(0f, 0.1f, 0.1f), planes),
          "a box STRADDLING a plane is kept -- a straddle test that rejects pops geometry at the screen edge");

        check.Expect(BoxDistance(new Vector3(0f), new Vector3(1f), new Vector3(0.5f, 0.5f, 0.5f)) == 0f,
          "a point inside the box is at distance 0");
        check.Expect(MathF.Abs(BoxDistance(new Vector3(0f), new Vector3(1f), new Vector3(4f, 0.5f, 0.5f)) - 3f) < 1e-4f,
          "and outside it, the distance to the NEAREST face");

        // The displacement splat. Its sign is the whole effect: pushing the wrong way pulls grass INTO a
        // creature instead of parting it around one, and nothing about the resulting frame says which.
        byte[] f = new byte[PushRes * PushRes * PushChannels];
        void Clear() { for (int i = 0; i < f.Length; i += PushChannels)
               { f[i] = 128; f[i + 1] = 128; f[i + 2] = 0; f[i + 3] = 0; } }
        Clear();
        const float cx = 64.5f, cy = 64.5f, rad = 6f;
        Splat(f, cx, cy, rad);
        int R(int x, int y) => f[(y * PushRes + x) * PushChannels];
        int G(int x, int y) => f[(y * PushRes + x) * PushChannels + 1];
        int B(int x, int y) => f[(y * PushRes + x) * PushChannels + 2];
        check.Expect(R(67, 64) > 128 && R(61, 64) < 128, "a gob pushes grass OUTWARD along x, not toward itself");
        check.Expect(G(64, 67) > 128 && G(64, 61) < 128, "and outward along y");
        check.Expect(R(67, 64) - 128 == 128 - R(61, 64), "the push is symmetric about the gob");
        check.Expect(R(64, 64) == 128 && G(64, 64) == 128, "the texel under the centre has no direction, so no push");
        check.Expect(R(64 + (int)rad + 1, 64) == 128, "and nothing past the radius is touched at all");
        check.Expect(R(66, 64) > R(68, 64), "the push falls off with distance");
        // The flatten channel is the half the direction cannot carry: it has to PEAK where the direction
        // vanishes, or the tuft between a creature's feet is the one thing left standing.
        check.Expect(B(64, 64) > B(67, 64) && B(67, 64) > B(69, 64), "the press-down peaks under the gob and falls off");
        check.Expect(B(64 + (int)rad + 1, 64) == 0, "and is zero past the radius");
        Clear();
        Splat(f, cx, cy, rad);
        int solo = B(66, 64);
        Splat(f, cx, cy, rad);
        check.Expect(B(66, 64) == solo, "two overlapping gobs do not press the same grass twice -- flatten takes the max");
        check.Expect(R(66, 64) != 128 && R(66, 64) > 128, "...while their lateral pushes do sum");
        Clear();
        Splat(f, cx, cy, 0f);
        check.Expect(R(64, 64) == 128 && B(64, 64) == 0, "a zero radius splats nothing rather than dividing by it");

        // The object cull. Its footprint is a RECTANGLE, not a disc, because the things it exists for -- a
        // crate, a wall, a chest -- are not round, and a disc over a long wall either misses its ends or
        // erases the grass beside it.
        int A(int x, int y) => f[(y * PushRes + x) * PushChannels + 3];
        Clear();
        CullRect(f, cx, cy, 10f, 3f);
        check.Expect(A(64, 64) == 255, "the middle of an object's footprint is fully culled");
        check.Expect(A(72, 64) > 0 && A(64, 66) > 0, "...along both axes of the rectangle");
        check.Expect(A(76, 64) == 0 && A(64, 69) == 0, "and nothing outside it is");
        check.Expect(A(73, 64) < A(64, 64), "the footprint softens toward its edge, so grass thins instead of stopping");
        Clear();
        CullRect(f, cx, cy, 0f, 3f);
        check.Expect(A(64, 64) == 0, "a degenerate footprint culls nothing rather than dividing by it");

        // The incremental rebuild: the clear and the upload trust the reported rectangle, so a writer that
        // touches one texel outside it leaves a push behind for good -- a permanent dent in the meadow that
        // nothing else in the pass would ever name.
        byte[] neutral = new byte[f.Length];
        for (int i = 0; i < neutral.Length; i += PushChannels) { neutral[i] = 128; neutral[i + 1] = 128; }
        // ClearPush restores from the pass's own template, which off-device is not built yet.
        byte[]? clear0 = _pushClear;
        _pushClear = neutral;
        bool Covers(int bx0, int by0, int bx1, int by1)
        {
            for (int y = 0; y < PushRes; y++)
            for (int x = 0; x < PushRes; x++)
            {
                int o = (y * PushRes + x) * PushChannels;
                bool changed = false;
                for (int c = 0; c < PushChannels; c++) changed |= f[o + c] != neutral[o + c];
                if (changed && (x < bx0 || x > bx1 || y < by0 || y > by1)) return false;
            }
            return true;
        }
        bool AllNeutral() => Covers(0, 0, -1, -1);
        Array.Copy(neutral, f, f.Length);
        Splat(f, cx, cy, rad);
        check.Expect(SplatBounds(cx, cy, rad, out int sx0, out int sy0, out int sx1, out int sy1)
                     && Covers(sx0, sy0, sx1, sy1), "every texel a splat changes is inside the rect it reports");
        ClearPush(f, sx0, sy0, sx1, sy1);
        check.Expect(AllNeutral(), "...and clearing that rect puts the whole field back to neutral");
        // The same for the cull rectangle, whose extents are the object's, not the push radius'.
        Array.Copy(neutral, f, f.Length);
        CullRect(f, cx, cy, 10f, 3f);
        check.Expect(CullBounds(cx, cy, 10f, 3f, out int kx0, out int ky0, out int kx1, out int ky1)
                     && Covers(kx0, ky0, kx1, ky1), "every texel a cull footprint changes is inside its rect");
        ClearPush(f, kx0, ky0, kx1, ky1);
        check.Expect(AllNeutral(), "...and clearing that one restores the field too");
        // An edge case the rect arithmetic gets wrong the moment it stops clamping: a pusher STRADDLING the
        // field's edge must report the part of its rect that is on the field, and never an index off it.
        check.Expect(SplatBounds(-4f, 20f, 6f, out int ox0, out int oy0, out int ox1, out int oy1)
                     && ox0 == 0 && ox1 == 3 && oy0 >= 0 && oy1 < PushRes,
          "a pusher straddling the edge reports the clamped part of its rect");
        check.Expect(!SplatBounds(-40f, 20f, 6f, out _, out _, out _, out _)
                     && !SplatBounds(PushRes + 40f, 20f, 6f, out _, out _, out _, out _),
          "...and one wholly outside the field reports no rect at all");
        check.Expect(!SplatBounds(cx, cy, 0f, out _, out _, out _, out _), "a zero radius reports no rect at all");

        // A list that repeats must compare equal, or nothing is ever skipped; one that differs by a single
        // texel must not, or the field goes stale.
        _pushNow[0] = new Pusher { X = 1f, Y = 2f, R = 3f, Hx = 4f, Hy = 5f };
        _pushWas[0] = _pushNow[0];
        check.Expect(SamePushers(1), "an unchanged pusher list compares equal");
        _pushWas[0].X = 1.01f;
        check.Expect(!SamePushers(1), "...and one texel of movement does not");
        _pushNowN = 0; _gl.PushWasN = -1;
        _pushClear = clear0;

        // A pusher is kept only where grass lies: one marked cell (texels 8..11 square) must be found by a rect
        // touching any of its texels and by no rect stopping a texel short.
        bool[] cells0 = (bool[])_mask.Cell.Clone();
        Array.Clear(_mask.Cell);
        _mask.Cell[2 * MaskRes + 2] = true;
        check.Expect(OnGrass(11, 11, 20, 20) && OnGrass(0, 0, 8, 8), "a rect touching a grass cell is on the grass");
        check.Expect(!OnGrass(0, 0, 7, 30) && !OnGrass(12, 0, 30, 30), "...and one a texel short of it is not");
        Array.Copy(cells0, _mask.Cell, cells0.Length);
        _mask.OriginX = float.NaN;

        // ---- the toggle (#180). _off is the resolution latch and Grass.Enabled is the player's dial; they are
        // two flags on purpose, because /grass enabled 1 on a client whose grass globals never came up would
        // run the pass over null pointers. Direct field writes, not sets: a set arms the cx.cfg writer, and
        // this runs at boot against the real user's file.
        bool on0 = Grass.Enabled.V, off0 = _off;
        Grass.Enabled.V = false;
        check.Expect(Stats().StartsWith("no grass"), "with the toggle off the pass reports no grass");
        _off = true; Grass.Enabled.V = true;
        check.Expect(_off && !CastsInto(0), "arming from chat cannot clear the resolution latch");
        _off = off0; Grass.Enabled.V = on0;

        // ---- CX-48: the sway, and last frame's. The oracle is the embedded GLSL against the hand-written
        // expressions below: one text states the sway (so the two frames cannot evaluate different curves),
        // each vertex path calls it twice -- this clock and last frame's -- and each reconstructs a previous
        // world position from the second call.
        check.Expect(SwayGlsl.Contains("(sin(t * 1.7 + phase) + 0.4 * sin(t * 3.9 + phase * 2.3)) * 0.5")
          && SwayGlsl.Contains("float(uPrevTimeOfDay % 3600000)"),
          "the sway and the previous clock are stated once, in the library both vertex paths include");
        check.Expect(ShaderContract.SwaySafe(GrassVmain) && ShaderContract.SwaySafe(BladeVmain),
          "both grass vertex paths reconstruct last frame's blade from last frame's clock");
        check.Expect(GrassVmain.Contains("float sway = CxGrassSway(t, phase) * bend;")
          && BladeVmain.Contains("float sway = CxGrassSway(tw, phase);"),
          "...and draw THIS frame's blade through the same library call, so the two differ only by the clock");
        check.Expect(GrassVmain.Contains("vPrev = uPrevVP * vec4(iPos.xyz + pPrev, 1.0);")
          && BladeVmain.Contains("CxBladeCurve(root, leanPrev, h, press, t, Tprev) + across * side * width * 0.5"),
          "the previous position rides this frame's widening, push and width: the sway term is what moves");
        check.Expect(GrassDepthFs.Contains("frag = vec4(CxMotionVec(vCur, vPrev), 0.0, 0.0);")
          && BladeDepthFs.Contains("frag = vec4(CxMotionVec(vCur, vPrev), 0.0, 0.0);"),
          "both velocity stages write the shared convention, behind the coverage decision that drew the blade");
        check.Expect(!ShaderContract.SwaySafe("void main() { gl_Position = uVP * vec4(aPos, 1.0); }"),
          "a vertex stage with no previous blade in it is refused, and its class keeps the camera's field");

        log.Info(check.Ok ? "GrassField.SelfCheck: passed" : "GrassField.SelfCheck: FAILED");
    }
}
