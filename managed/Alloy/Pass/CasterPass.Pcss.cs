using CX.Framework;

namespace CX.Alloy;

public sealed unsafe partial class CasterPass
{
    /// <summary><c>uSunPenum</c> -- penumbra texels per unit of stored cascade depth, from
    /// <c>shadow.sunangle</c> and the live cascade resolution. 0 (no angular size) stands the sun's blocker
    /// search down to <c>CxSunTaps</c>' fixed cross.</summary>
    internal static float SunPenumbraScale => ShadowMath.SunPenumbraScale(Shadows.SunAngle.V, _mapSize);

    /// <summary>The contact-hardening half of the receiver contract, against the embedded GLSL -- an
    /// independent artifact, never a restatement of the C# beside it. Each pin names the silent failure it
    /// stands between: a filter that stops hardening, a search ring that picked up the disc's dither, a disc
    /// that stops matching across the static/dynamic split, a stand-down that stopped landing on the cross.</summary>
    internal static bool PcssCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        string sun = AlloyLights.SunShadowLib, cube = AlloyLights.LocalShadowLib;

        // The sun's estimate: the mean blocker gap scaled by uSunPenum and clamped to the shared texel
        // ceiling, and a search that found nothing returning 0 (which the caller reads as the cross).
        check.Expect(sun.Contains("return clamp((ref - sum / n) * uSunPenum, 0.0, uLocalSoft.x);"),
          "sun penumbra: the estimate is (receiver - mean blocker) * uSunPenum, clamped to shadow.soft");
        check.Expect(sun.Contains("if (n <= 0.0) return 0.0;"), "sun penumbra: no blocker found == 0 texels");
        check.Expect(sun.Contains("full = n >= float(ns);"), "sun penumbra: `full` is every read finding a blocker");
        // shadow.search picks the count, the sun caps it at half the cube's: it is paid by every outdoor fragment.
        check.Expect(sun.Contains("const int CX_SUN_SEARCH = 8;")
          && sun.Contains("int ns = min(CxSearchTaps(), CX_SUN_SEARCH);"),
          "sun penumbra: shadow.search taps under an 8-tap ceiling");
        check.Expect(sun.Contains("const float CX_SUN_DISC_MIN = 0.5;"), "sun penumbra: the disc's half-texel floor");
        // The search ring takes rotation 0.0 -- LITERALLY, on both receivers. A dithered ring makes
        // neighbouring fragments disagree on the penumbra and the shadow edge speckles.
        check.Expect(sun.Contains("mat2 ring = CxSpin(ns, 0.0, sr);"),
          "sun penumbra: the search ring is undithered");
        // ...at the radius the source size and the receiver's depth allow, as the cube's ring is. Worked
        // example: a 2048 cascade under a 0.53 deg sun carries uSunPenum 9.4723, so a receiver at ref 0.3 can
        // be reached by no blocker more than 2.84 texels out and the 4-texel ceiling never binds there.
        check.Expect(sun.Contains("float sr = min(uLocalSoft.x, ref * uSunPenum);"),
          "sun penumbra: the search ring is bounded by the widest penumbra this receiver could take");
        check.Expect(cube.Contains("mat2 ring = CxSpin(ns, 0.0, texel * sr);"),
          "cube penumbra: the search ring is undithered");
        // ...and the disc IS rotated, per fragment, by the noise the cube disc uses plus the taa phase --
        // uLocalPcss.y is 0 with no taa stage live, so the pattern is spatial and converges without one.
        check.Expect(sun.Contains("float rot = fract(CxShadowNoise(worldPos) + uLocalPcss.y);"),
          "sun disc: rotated per fragment by CxShadowNoise, taa phase only added on top");
        // Matching kernels across the split: one radius, one tap count, one rotation, min-composed.
        check.Expect(sun.Contains("float vs = CxSunDisc(uSunShadow, uv, layer, ref, texel, rs, taps, rot);")
          && sun.Contains("float vd = uSunDyn[i] > 0.5 ? CxSunDisc(uSunDynShadow, uv, layer, ref, texel, rs, taps, rot) : 1.0;")
          && sun.Contains("        return min(vs, vd);"),
          "sun disc: the dynamic layer takes the SAME radius, taps and rotation, min-composed");
        // One search for both layers, min-composed, because depth is a min-of-blockers.
        check.Expect(sun.Contains("if (dyn) zk = min(zk, texture(uSunDynShadowRaw, vec3(t, layer)).r);"),
          "sun penumbra: one ring reads both layers and keeps the nearer blocker");
        // Every stand-down lands on the pre-CX-49 cross: no raw view / no angular size (uSunPenum 0),
        // shadow.taps 0, shadow.soft 0, a penumbra under the floor, and a fully-occluded search.
        check.Expect(sun.Contains("if (uSunPenum <= 0.0 || uLocalSoft.x <= 0.0 || int(uLocalPcss.z) <= 0) return -1.0;"),
          "sun filter: uSunPenum 0, shadow.taps 0 and shadow.soft 0 each ask for the cheap cross");
        check.Expect(sun.Contains("if (uSunPenum * ref < CX_SUN_DISC_MIN) return -1.0;"),
          "sun filter: a receiver no blocker can soften skips the search");
        check.Expect(sun.Contains("return full || rs < CX_SUN_DISC_MIN ? -1.0 : rs;"),
          "sun filter: a full or empty search asks for the cheap cross");
        check.Expect(sun.Contains("#else\n    return -1.0;\n#endif"),
          "sun filter: a stage that never asks for the soft filter has no radius, so it compiles to CxSunTaps alone");
        check.Expect(sun.Contains("    return CxSunTaps(i, ndc, slope, texel);\n}"),
          "sun filter: every negative radius lands on CxSunTaps");
        // Both sides of the cascade handoff run the same filter at the same estimate, or the boundary draws as
        // a softness step -- and the handoff CARRIES the radius rather than searching the coarser map again.
        check.Expect(sun.Contains("float rs = CxSunRadius(i, ndc, slope, texel);")
          && sun.Contains("float v = CxSunFilter(i, ndc, worldPos, slope, texel, rs);")
          && sun.Contains("v = mix(v, CxSunFilter(i + 1, n2, worldPos, slope, texel, CxSunCarry(i, rs)), f);"),
          "sun filter: one search per fragment, carried across the cascade cross-fade");
        // The carry is a unit conversion, not a second guess: a penumbra is a world width and uSunTexel is
        // world units per texel, so cascade i+1 (4x coarser) wants rs * texel_i / texel_(i+1) -- a quarter of
        // the texels for the same metres. Worked example: 2 texels on a 7.5 m cascade (texel 0.00732 m) is
        // 0.0146 m, which is 0.5 texels on the 30 m cascade (texel 0.0293 m).
        check.Expect(sun.Contains("float r2 = min(uLocalSoft.x, rs * uSunTexel[i] / uSunTexel[i + 1]);")
          && sun.Contains("return r2 < CX_SUN_DISC_MIN ? -1.0 : r2;"),
          "sun filter: the carried radius is the same world penumbra in the next cascade's texels");

        // The soft path's two borrowed helpers come from the CUBE receiver, which every stage that asks for
        // it composes FIRST. Pinned on the COMPOSED lit stage, where the splice order is visible.
        string lit = ShaderSource.Get("cx_ownlit_fs.glsl");
        int atCube = lit.IndexOf(ShaderSource.LibMark + "cx_localshadow\n", System.StringComparison.Ordinal);
        int atSun = lit.IndexOf(ShaderSource.LibMark + "cx_sunshadow\n", System.StringComparison.Ordinal);
        check.Expect(atCube >= 0 && atSun > atCube,
          "the lit stage composes cx_localshadow before cx_sunshadow (CxSpin/CxVogelTurn/CxShadowNoise)");
        check.Expect(lit.Contains("#define CX_SUN_SOFT 1") && lit.Contains("#define CX_FS 1"),
          "the lit stage asks for the soft sun filter, and for the gl_FragCoord dither it rotates by");
        // ...and the callers the ticket keeps on the cheap path really are on it: a disc in a vertex stage
        // blurs a whole blade or billboard, and the sun is paid by every outdoor fragment.
        foreach (string cheap in new[] { "cx_grass_v.glsl", "cx_grass_fs.glsl", "cx_gblade_vs.glsl", "cx_gblade_fs.glsl",
                                         "cx_part_v.glsl", "cx_part_f.glsl", "cx_wx_cull_cs.glsl", "cx_wx_sv.glsl", "cx_water_fs.glsl" })
            // the DEFINE, not the token: every one of these composes cx_sunshadow, whose own #ifdef names it
            check.Expect(!ShaderSource.Get(cheap).Contains("#define CX_SUN_SOFT"), $"{cheap} stays on the four-tap sun cross");

        // The cube search: 16 taps, and a ring radius off the source size and the two depths rather than
        // pinned at the ceiling. Worked example for the formula below: a 512-face cube, source radius 1 m,
        // receiver 5 m out (texel 0.0195 m), centre blocker at 4.5 m -> 1 * 0.5 / 4.5 = 0.111 m = 5.7 texels,
        // so a 4-texel ceiling still binds; the same receiver with its blocker at 4.95 m asks for 0.52.
        check.Expect(cube.Contains("const int CX_LOCAL_SEARCH = 16;")
          && cube.Contains("int ns = CxSearchTaps();")
          && cube.Contains("int n = int(uSearchTaps); return n > 0 ? min(n, CX_LOCAL_SEARCH) : CX_LOCAL_SEARCH;"),
          "cube penumbra: shadow.search taps under a 16-tap ceiling, a self-filled block reading as the ceiling");
        check.Expect(cube.Contains("float sr = mc < m ? min(uLocalSoft.x, src * (m - mc) / (mc * texel)) : uLocalSoft.x;"),
          "cube penumbra: the ring radius comes off the source size and the receiver/blocker depths");
        check.Expect(cube.Contains("full = n > float(ns);"),
          "cube penumbra: `full` counts the centre read on top of the ring");
        return check.Ok;
    }
}
