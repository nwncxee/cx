using System;
using System.Numerics;
using CX.Framework;

namespace CX.Alloy;

public static partial class ShadowMath
{
    /// <summary>Headless check of the light-space transform (light along -Y, unit sphere at origin, margin 1):
    /// centre maps to the NDC origin, the sphere rim to |x|==1, and a point deeper along the light stores a
    /// greater depth.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        Matrix4x4 vp = DirectionalLightViewProj(new Vector3(0f, -1f, 0f), Vector3.Zero, 1f, 1f);
        Vector3 c = WorldToLightNdc(vp, Vector3.Zero);
        check.Expect(MathF.Abs(c.X) < 1e-3f && MathF.Abs(c.Y) < 1e-3f, "centre -> NDC x/y origin");
        Vector3 rim = WorldToLightNdc(vp, new Vector3(1f, 0f, 0f));
        check.Expect(MathF.Abs(MathF.Abs(rim.X) - 1f) < 1e-3f, "rim -> NDC |x|==1");
        float dCentre = WorldToLightNdc(vp, Vector3.Zero).Z;
        float dDeep = WorldToLightNdc(vp, new Vector3(0f, -0.5f, 0f)).Z;
        check.Expect(dDeep > dCentre, "deeper-along-light -> greater depth");

        // Texel snap: two centres closer than one texel apart must produce the SAME map (identical
        // alignment) -- that is what stops the shadow edges crawling as the camera slides the centre.
        float radius = 40f; int map = 1024; float texel = 2f * radius / map;
        Vector3 dir = new(0f, 0f, -1f);
        Matrix4x4 a = DirectionalLightViewProj(dir, new Vector3(100f, 200f, 5f), radius, radius, map);
        Matrix4x4 b = DirectionalLightViewProj(dir, new Vector3(100f + texel * 0.3f, 200f - texel * 0.2f, 5f), radius, radius, map);
        check.Expect(a.Equals(b), "texel snap: sub-texel centre shift -> identical map");
        Matrix4x4 far = DirectionalLightViewProj(dir, new Vector3(100f + texel * 3f, 200f, 5f), radius, radius, map);
        check.Expect(!far.Equals(a), "texel snap: multi-texel shift -> different map");
        // #306: a sub-texel shift ALONG the light must reproduce the map too -- the along-light snap is
        // what makes the matrix a usable static-bake key while the focus walks.
        Matrix4x4 al = DirectionalLightViewProj(dir, new Vector3(100f, 200f, 5f - texel * 0.4f), radius, radius, map);
        check.Expect(a.Equals(al), "texel snap: sub-texel along-light shift -> identical map");

        // Sun cascades (#94): radii strictly increase, the outermost IS the max shadow distance, and every
        // cascade is contained in the next -- containment is what lets the receiver pick the FIRST cascade
        // whose NDC box holds the fragment and be sure the rest would too.
        const float maxDist = 120f; const int cascades = 4;
        for (int i = 1; i < cascades; i++)
            check.Expect(CascadeRadius(i, cascades, maxDist) > CascadeRadius(i - 1, cascades, maxDist), $"cascade {i} radius grows");
        check.Expect(CascadeRadius(cascades - 1, cascades, maxDist) == maxDist, "outermost cascade == max distance");
        check.Expect(CascadeRadius(0, 1, maxDist) == maxDist, "single cascade == max distance");

        // CX-49: the sun's penumbra scale, against HAND GEOMETRY -- no call to the code under test on the
        // expected side. A 2048-texel cascade fitted to a 60 m radius spans 120 m of stored depth at
        // 0.0585938 m per texel; the real sun is 0.53 deg wide, so a blocker 20 m in front of the receiver
        // (a sixth of that range) casts a penumbra of radius 20 * tan(0.265 deg) = 0.0925031 m = 1.57872 texels.
        check.Expect(MathF.Abs(SunPenumbraScale(0.53f, 2048) * (20f / 120f) - 1.57872f) < 1e-3f,
              "sun penumbra: a 20 m gap under a 0.53 deg sun on a 120 m cascade == 1.579 texels");
        // ...the same metres on half the map size are half the texels.
        check.Expect(MathF.Abs(SunPenumbraScale(0.53f, 1024) * (20f / 120f) - 0.78936f) < 1e-3f,
              "sun penumbra: half the map size == half the texels for the same gap");
        // A 2 deg artist's sun is 3.77x the real one, not 2x: it is the tangent of the HALF angle that scales.
        check.Expect(MathF.Abs(SunPenumbraScale(2f, 2048) - 35.748f) < 1e-2f,
              "sun penumbra: 2 deg == 35.75 texels per unit of stored depth");
        // The three inputs the receiver must read as "no blocker search, take the fixed cross".
        check.Expect(SunPenumbraScale(0f, 2048) == 0f && SunPenumbraScale(-1f, 2048) == 0f
              && SunPenumbraScale(0.53f, 0) == 0f, "sun penumbra: no angle or no map == 0 (search off)");

        bool upX = false;
        check.Expect(!LatchUpX(ref upX, new Vector3(0.2f, 0f, -0.98f)) && LatchUpX(ref upX, new Vector3(0.1f, 0f, -0.995f))
                     && LatchUpX(ref upX, new Vector3(0.17f, 0f, -0.985f)) && !LatchUpX(ref upX, new Vector3(0.25f, 0f, -0.97f)),
                     "up-axis latch: enters at 0.99, holds across the band, leaves under 0.98");
        Vector3 sun = Normalize(new Vector3(0.4f, 0.3f, -0.86f));
        Vector3 anchor = new(112f, -34f, 6f);
        for (int i = 0; i + 1 < cascades; i++)
        {
            float ri = CascadeRadius(i, cascades, maxDist);
            Matrix4x4 inner = DirectionalLightViewProj(sun, anchor, ri, ri);
            Matrix4x4 outer = DirectionalLightViewProj(sun, anchor, CascadeRadius(i + 1, cascades, maxDist),
                                                       CascadeRadius(i + 1, cascades, maxDist));
            // a point on cascade i's rim: inside i (|x| ~ 1) and comfortably inside i+1
            Vector3 edge = anchor + Vector3.Cross(PerpendicularUp(sun), sun) * (ri * 0.95f);
            Vector3 inNdc = WorldToLightNdc(inner, edge), outNdc = WorldToLightNdc(outer, edge);
            check.Expect(MathF.Abs(inNdc.X) <= 1f && MathF.Abs(inNdc.Y) <= 1f, $"cascade {i}: rim inside its own box");
            check.Expect(MathF.Abs(outNdc.X) < 1f && MathF.Abs(outNdc.Y) < 1f, $"cascade {i}: rim inside cascade {i + 1}");
        }
        // A caster far UP-SUN of the innermost cascade still shades the fragment at its centre, and
        // innermost-first selection means cascade 0 is the only one that fragment ever asks -- so the
        // up-sun margin has to be the max shadow distance for EVERY cascade, not each one's own radius.
        // Scaled per cascade, this point falls outside the near plane and its shadow silently vanishes.
        float r0 = CascadeRadius(0, cascades, maxDist);
        Matrix4x4 c0 = DirectionalLightViewProj(sun, anchor, r0, maxDist);
        Vector3 upSun = WorldToLightNdc(c0, anchor - sun * (maxDist * 0.9f));
        check.Expect(MathF.Abs(upSun.Z) <= 1f, "cascade 0: a caster 0.9 * maxDist up-sun is still inside the fit");

        // Camera forward convention: identity rotation looks along -Z (straight down, NWN Z-up), so the
        // focus descends below the camera; dist<=0 is a no-op.
        Vector3 focus = CameraGroundFocus(new Vector3(0f, 0f, 100f), Quaternion.Identity, 30f);
        check.Expect(MathF.Abs(focus.Z - 70f) < 1e-3f && focus.X == 0f && focus.Y == 0f, "camera focus: identity -> straight down");
        check.Expect(CameraGroundFocus(new Vector3(1f, 2f, 3f), Quaternion.Identity, 0f) == new Vector3(1f, 2f, 3f), "camera focus: dist<=0 no-op");

        // Frustum cull (look-at basis; straight-down camera exercises the degenerate-right branch).
        Vector3 dn = new(0f, 0f, -10f);
        check.Expect(SphereInCameraFrustum(new Vector3(0f, 0f, -10f), 1f, Vector3.Zero, dn, 45f, 1f, 0.1f, 45f), "frustum: on-axis kept");
        check.Expect(!SphereInCameraFrustum(new Vector3(0f, 0f, 10f), 1f, Vector3.Zero, dn, 45f, 1f, 0.1f, 45f), "frustum: behind culled");
        check.Expect(!SphereInCameraFrustum(new Vector3(100f, 0f, -10f), 1f, Vector3.Zero, dn, 45f, 1f, 0.1f, 45f), "frustum: far off-side culled");
        check.Expect(SphereInCameraFrustum(new Vector3(12f, 0f, -10f), 10f, Vector3.Zero, dn, 45f, 1f, 0.1f, 45f), "frustum: off-side big sphere kept");
        check.Expect(!SphereInCameraFrustum(new Vector3(0f, 0f, -60f), 1f, Vector3.Zero, dn, 45f, 1f, 0.1f, 45f), "frustum: beyond far culled");
        // Oblique chase-camera angle: light beside the player is in view, light far behind the camera is not.
        Vector3 cp = new(0f, -8f, 6f), tgt = new(0f, 0f, 1f);
        check.Expect(SphereInCameraFrustum(new Vector3(2f, 2f, 1f), 1f, cp, tgt, 45f, 1.8f, 0.1f, 45f), "frustum: near-target kept");
        check.Expect(!SphereInCameraFrustum(new Vector3(0f, -30f, 6f), 2f, cp, tgt, 45f, 1.8f, 0.1f, 45f), "frustum: behind camera culled");

        // Point-light cube face (slice 6): +X face from the origin, near 0.5 far 10. A point straight along
        // +X maps to the NDC centre; nudging it off-axis moves it off-centre; and a farther point stores a
        // greater depth. (Face-selection round-trip with samplerCube is verified live, not here.)
        Matrix4x4 fx = PointLightFaceViewProj(Vector3.Zero, 0, 0.5f, 10f);
        Vector3 onAxis = WorldToLightNdc(fx, new Vector3(5f, 0f, 0f));
        check.Expect(MathF.Abs(onAxis.X) < 1e-3f && MathF.Abs(onAxis.Y) < 1e-3f, "cube +X: on-axis -> NDC centre");
        // The face MUST be exactly 90deg: a cube sampler maps a direction to a texel by that rule, so a
        // point on the face's 45deg diagonal has to land exactly on the NDC edge. A wider fit renders
        // every caster off the texel the receiver will read it back from -- an offset shadow, not a
        // missing one, which is the failure mode nobody spots by looking.
        Vector3 corner = WorldToLightNdc(fx, new Vector3(5f, 5f, 5f));
        check.Expect(MathF.Abs(MathF.Abs(corner.X) - 1f) < 1e-3f && MathF.Abs(MathF.Abs(corner.Y) - 1f) < 1e-3f,
              "cube +X: 45deg diagonal -> exactly the NDC edge (face is 90deg)");
        Vector3 offAxis = WorldToLightNdc(fx, new Vector3(5f, 2f, 0f));
        check.Expect(MathF.Abs(offAxis.Y) > 0.1f, "cube +X: off-axis -> off-centre");
        float dNear = WorldToLightNdc(fx, new Vector3(2f, 0f, 0f)).Z;
        float dFar = WorldToLightNdc(fx, new Vector3(8f, 0f, 0f)).Z;
        check.Expect(dFar > dNear, "cube +X: farther-along-axis -> greater depth");

        // Weld the render matrix to the receiver's linearize formula: project an on-axis point at a known
        // distance, take the stored device depth sd=(ndc.z+1)/2, and assert LinearizeFaceDepth recovers it.
        // A convention slip between render and receiver would fail here headless instead of live.
        Matrix4x4 fx2 = PointLightFaceViewProj(Vector3.Zero, 0, 0.5f, 20f);
        foreach (float dist in new[] { 2f, 7f, 15f })
        {
            float sd = (WorldToLightNdc(fx2, new Vector3(dist, 0f, 0f)).Z + 1f) * 0.5f;
            check.Expect(MathF.Abs(ShadowMath.LinearizeFaceDepth(sd, 0.5f, 20f) - dist) < 0.05f, $"cube linearize d={dist}");
        }

        // Scene-camera latch (#99). The colour pass recovers the eye and look direction by inverting the
        // engine's view matrix; this pins that convention headless. A slip here does not crash, it reports
        // a camera somewhere else entirely -- which is the failure that put #93's cull at (0, 0, 766).
        Vector3 eye = new(10f, -20f, 8f), at = new(10f, 0f, 2f);
        Matrix4x4 camView = Matrix4x4.CreateLookAt(eye, at, new Vector3(0f, 0f, 1f));
        check.Expect(Matrix4x4.Invert(camView, out Matrix4x4 camInv), "scene cam: view matrix inverts");
        check.Expect((camInv.Translation - eye).Length() < 1e-3f, "scene cam: inverse translation IS the eye");
        Vector3 fwd = Vector3.Normalize(Vector3.TransformNormal(ViewForward, camInv));
        check.Expect(Vector3.Dot(fwd, Vector3.Normalize(at - eye)) > 0.999f, "scene cam: view -Z -> the look direction");
        // ...and the depth the period readout prints is the distance down that axis to the focus.
        check.Expect(MathF.Abs(Vector3.Dot(at - eye, fwd) - (at - eye).Length()) < 1e-3f,
              "scene cam: on-axis focus depth == its distance");

        // #257: the same forward, from the camera QUATERNION instead of the inverted view matrix. The
        // two derivations have to agree, because the frame constants now take the quaternion route and
        // every shadow fit in this file was validated against the matrix one.
        Quaternion look = Quaternion.CreateFromRotationMatrix(camInv);
        check.Expect(Vector3.Dot(FrameRead.ViewForward(look), fwd) > 0.999f,
              "scene cam: the camera quaternion gives the same forward as the inverted view");

        // #488: the receiver cull. Camera at the origin looking +X, 90deg fov, square, far 100: the
        // frustum is |y|,|z| <= x. Sun straight down (-Z).
        Span<CullPlane> pl = stackalloc CullPlane[6];
        ReceiverPlanes(Vector3.Zero, Vector3.UnitX, 90f, 1f, 0.1f, 100f, 0f, pl);
        Vector3 down = new(0f, 0f, -1f);
        check.Expect(ShadowReaches(pl, new Vector3(50f, 0f, 0f), 1f, new Vector3(50f, 0f, 0f), 1f), "receiver: a caster inside the frustum reaches");
        check.Expect(!SunShadowReaches(pl, new Vector3(-20f, 0f, 0f), 1f, down, Vector3.Zero, 200f), "receiver: a caster behind the camera with a downward sun cannot reach");
        check.Expect(SunShadowReaches(pl, new Vector3(50f, 0f, 80f), 1f, down, Vector3.Zero, 200f), "receiver: a caster above the frustum shades down into it");
        check.Expect(!SunShadowReaches(pl, new Vector3(50f, 0f, 80f), 1f, down, new Vector3(50f, 0f, 85f), 20f), "receiver: ...unless the fit's extent ends before it gets there");
        check.Expect(!SunShadowReaches(pl, new Vector3(50f, 0f, -80f), 1f, down, Vector3.Zero, 200f), "receiver: a caster below the frustum shading further down cannot reach");
        check.Expect(SunShadowReaches(pl, new Vector3(50f, 0f, -80f), 1f, new Vector3(0f, 0f, 1f), Vector3.Zero, 200f), "receiver: ...but shading up it does");
        // cube: light beside the frustum, caster between it and the frustum -> the cone widens in
        check.Expect(CubeShadowReaches(pl, new Vector3(50f, 70f, 0f), 1f, new Vector3(50f, 80f, 0f), 60f), "receiver: a local-light cone widening toward the frustum reaches");
        check.Expect(!CubeShadowReaches(pl, new Vector3(50f, 90f, 0f), 1f, new Vector3(50f, 80f, 0f), 60f), "receiver: ...one pointing away does not");
        check.Expect(!CubeShadowReaches(pl, new Vector3(50f, 70f, 0f), 1f, new Vector3(50f, 80f, 0f), 15f), "receiver: ...nor one whose reach ends short");
        check.Expect(CubeShadowReaches(pl, new Vector3(50f, 70f, 0f), 5f, new Vector3(50f, 72f, 0f), 60f), "receiver: a light inside the caster keeps it");
        ReceiverPlanes(Vector3.Zero, Vector3.UnitX, 90f, 1f, 0.1f, 100f, 30f, pl);
        check.Expect(ShadowReaches(pl, new Vector3(-20f, 0f, 0f), 1f, new Vector3(-20f, 0f, 0f), 1f), "receiver: slack pushes every plane out");
        ReceiverPlanes(Vector3.Zero, Vector3.Zero, 90f, 1f, 0.1f, 100f, 0f, pl);
        check.Expect(ShadowReaches(pl, new Vector3(-500f, 0f, 0f), 1f, new Vector3(-500f, 0f, 0f), 1f), "receiver: a degenerate camera keeps everything");

        log.Info(check.Ok ? "ShadowMath.SelfCheck: passed" : "ShadowMath.SelfCheck: FAILED");
    }
}
