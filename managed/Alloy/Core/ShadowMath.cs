using System;
using System.Numerics;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// Pure light-space matrix math for the shadow-map pipeline. No engine or GL deps -- exercised headless by
/// <see cref="SelfCheck"/>. Built with System.Numerics (row-vector, row-major, RH); NWN's world is Z-up, and
/// the GL-facing layout and handedness reconciliation happens where these feed a shader.
/// </summary>
public static partial class ShadowMath
{
    /// <summary>
    /// Light view-projection for a directional light along unit <paramref name="lightDir"/>, fitted to the
    /// sphere (<paramref name="center"/>, <paramref name="radius"/>). <paramref name="upstreamMargin"/>
    /// extends the frustum TOWARD the light, so a caster between light and sphere clears the near plane.
    /// </summary>
    /// <param name="upX">the caller's <see cref="LatchUpX"/> verdict; null picks by the bare threshold.</param>
    public static Matrix4x4 DirectionalLightViewProj(Vector3 lightDir, Vector3 center, float radius, float upstreamMargin, int mapSize = 0,
                                                     bool? upX = null)
    {
        Vector3 dir = Normalize(lightDir);
        // Orthonormal light basis (right, up, dir). Snap the centre to whole shadow-texel steps in the
        // right/up plane so the map doesn't crawl/shimmer as the (moving) centre slides sub-texel -- the
        // classic CSM texel snap. mapSize<=0 disables it (the pure-math SelfCheck cases).
        Vector3 up = upX is bool x ? (x ? Vector3.UnitX : Vector3.UnitZ) : PerpendicularUp(dir);
        Vector3 right = Normalize(Vector3.Cross(up, dir));
        up = Vector3.Cross(dir, right);
        if (mapSize > 0)
        {
            float texel = 2f * radius / mapSize;
            float cr = MathF.Round(Vector3.Dot(center, right) / texel) * texel;
            float cu = MathF.Round(Vector3.Dot(center, up) / texel) * texel;
            // #306: the along-light component snaps too. It never affected which texel a caster lands on,
            // but leaving it continuous made the matrix a function of the RAW focus -- so no two walking
            // frames ever produced the same map, and the cascade static half could never be reused. Snapped,
            // the whole matrix is quantised: a sub-texel focus move reproduces it bit-identical, which is
            // what lets CasterPass key a cascade's static bake on the matrix itself. Depth shifts by at most
            // one texel step, identically for caster and receiver, so the comparison is unmoved.
            float cd = MathF.Round(Vector3.Dot(center, dir) / texel) * texel;
            center = right * cr + up * cu + dir * cd;
        }
        Vector3 eye = center - dir * (radius + upstreamMargin);
        Matrix4x4 view = Matrix4x4.CreateLookAt(eye, center, up);
        Matrix4x4 proj = Matrix4x4.CreateOrthographic(2f * radius, 2f * radius, 0f, 2f * radius + upstreamMargin);
        return view * proj * D3DToGlClipZ;
    }

    /// <summary>
    /// Half-extent of sun cascade <paramref name="i"/> of <paramref name="count"/>, out to
    /// <paramref name="maxDist"/> world units. The cascades are CONCENTRIC on the shadow focus, not fitted to
    /// camera-frustum corners: a fit that does not depend on camera orientation cannot re-shape as the camera
    /// orbits, which is what keeps the texel snap locked to the world. Radii halve inward.
    /// </summary>
    public static float CascadeRadius(int i, int count, float maxDist)
    {
        if (count < 1) count = 1;
        if (i < 0) i = 0; else if (i >= count) i = count - 1;
        return maxDist / (1 << (count - 1 - i));
    }

    /// <summary>The sun's penumbra scale -- shadow-map TEXELS of penumbra radius per unit of STORED depth
    /// between receiver and blocker, for a sun of angular diameter <paramref name="angleDeg"/> over a
    /// <paramref name="mapSize"/>-texel cascade. One number for every cascade, since a cascade's depth range
    /// over its texel is always the map size. 0 for a non-positive input: the receiver reads that as no search.</summary>
    public static float SunPenumbraScale(float angleDeg, int mapSize)
    {
        if (angleDeg <= 0f || mapSize <= 0) return 0f;
        // the HALF angle: the penumbra's radius subtends the source's radius, as a point light's estimate
        // scales by its source radius and not its diameter
        return MathF.Tan(angleDeg * (MathF.PI / 360f)) * mapSize;
    }

    /// <summary>
    /// One face of a point light's depth cube: a 90&deg; perspective from <paramref name="lightPos"/> along
    /// face <paramref name="face"/> (0..5 = +X,-X,+Y,-Y,+Z,-Z), fitted to [<paramref name="near"/>,
    /// <paramref name="far"/>]. The standard GL cube-map face basis, so the face round-trips with a
    /// <c>samplerCube</c> sampled by <c>fragWorld - lightPos</c>. RH lookAt * D3D [0,1] * GL clip-z remap.
    /// </summary>
    public static Matrix4x4 PointLightFaceViewProj(Vector3 lightPos, int face, float near, float far)
    {
        (Vector3 fwd, Vector3 up) = CubeFace(face);
        Matrix4x4 view = Matrix4x4.CreateLookAt(lightPos, lightPos + fwd, up);
        // EXACTLY 90deg, and that is a correctness constraint, not a default. A cube sampler maps a direction
        // to a face texel by the exact 90deg rule; a face rendered even 1.5% wider puts the same world point
        // on a different texel, so every shadow lands slightly off its caster. The interim system rendered
        // over-scanned faces on purpose (a seam border) and paid for it with a matching uv shrink in the
        // receiver -- which only works because it sampled a 2D array by hand. GL_TEXTURE_CUBE_MAP_SEAMLESS
        // solves the same seam problem in the filter, with no geometry to compensate for.
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 1f, near, far);
        return view * proj * D3DToGlClipZ;
    }

    /// <summary>Widening on the framed half-angle: the PCF disc reads off the silhouette, and a sphere fitted
    /// exactly to the tile edge has nothing there to read.</summary>
    public const float ConeSlack = 1.15f;
    /// <summary>The widest half-angle a pair view takes (sin; ~58 degrees): past it the projection's corners run
    /// away. A mover too close to its lamp to fit is framed at this cone round its centre: the shadow it casts
    /// lies along the same directions, so the clamp trims the shadow's rim, never the whole shadow.</summary>
    public const float ConeMaxSin = 0.85f;

    /// <summary>One (static light, mover) PAIR view -- a perspective from <paramref name="lightPos"/> framed
    /// on the sphere (<paramref name="at"/>, <paramref name="radius"/>), clamped to <see cref="ConeMaxSin"/>,
    /// and fitted out to the light's own reach. A light inside the sphere looks straight down, where a body
    /// standing under it throws its shadow. False only for a degenerate sphere or depth range.</summary>
    public static bool PairViewProj(Vector3 lightPos, Vector3 at, float radius, float near, float far,
                                    out Matrix4x4 vp, out float halfSin)
    {
        vp = Matrix4x4.Identity; halfSin = 0f;
        if (!(far > near) || !(radius > 0f)) return false;
        Vector3 d = at - lightPos;
        float dist = d.Length();
        if (!(dist > radius)) { d = -Vector3.UnitZ; dist = 1f; at = lightPos + d; }
        halfSin = MathF.Min(radius / dist * ConeSlack, ConeMaxSin);
        Vector3 up = PerpendicularUp(d / dist);
        Matrix4x4 view = Matrix4x4.CreateLookAt(lightPos, at, up);
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(2f * MathF.Asin(halfSin), 1f, near, far);
        vp = view * proj * D3DToGlClipZ;
        return true;
    }

    /// <summary>Inverse of the point-light face projection's stored depth: a device depth <paramref name="sd"/>
    /// -&gt; the view-space distance along that face's major axis (<c>sd=0</c> near, <c>sd=1</c> far). The
    /// receiver compares with the SAME formula against <c>max(|dx|,|dy|,|dz|)</c> of
    /// <c>fragWorld-lightPos</c>: raw device depth is nearly all ~1.0 and compares badly.</summary>
    public static float LinearizeFaceDepth(float sd, float near, float far)
        => near * far / (far - sd * (far - near));

    /// <summary>The VIEW half of <see cref="PointLightFaceViewProj"/> alone -- what the probe capture
    /// hands the cluster build, which needs the face's view without its projection. Shares the one face
    /// basis rather than restating it: a duplicated up vector would mis-bin exactly one face's lights.</summary>
    public static Matrix4x4 CubeFaceView(Vector3 eye, int face)
    {
        (Vector3 fwd, Vector3 up) = CubeFace(face);
        return Matrix4x4.CreateLookAt(eye, eye + fwd, up);
    }

    /// <summary>Look direction of cube face <paramref name="face"/> -- the caster cull's "is this in front of
    /// the face" axis. Exposed rather than duplicated at the call site: a face axis that disagreed with the
    /// matrix it culls for would drop exactly the casters that face actually sees.</summary>
    public static Vector3 CubeFaceForward(int face) => CubeFace(face).fwd;

    /// <summary>Standard GL cube-map face basis (look direction, up), faces 0..5 = +X,-X,+Y,-Y,+Z,-Z. These are
    /// the fixed conventions a <c>samplerCube</c> expects, so rendering with them makes depth sampling round-trip.</summary>
    private static (Vector3 fwd, Vector3 up) CubeFace(int face) => face switch
    {
        0 => (new Vector3(1f, 0f, 0f),  new Vector3(0f, -1f, 0f)),
        1 => (new Vector3(-1f, 0f, 0f), new Vector3(0f, -1f, 0f)),
        2 => (new Vector3(0f, 1f, 0f),  new Vector3(0f, 0f, 1f)),
        3 => (new Vector3(0f, -1f, 0f), new Vector3(0f, 0f, -1f)),
        4 => (new Vector3(0f, 0f, 1f),  new Vector3(0f, -1f, 0f)),
        _ => (new Vector3(0f, 0f, -1f), new Vector3(0f, -1f, 0f)),
    };

    /// <summary>Radius slack every caller of <see cref="SphereInCameraFrustum"/> applies: covers the
    /// mirrored reflection-subframe camera and one frame of camera-turn latency without re-introducing pop.
    /// One constant on purpose -- the light cull and the cube-bake cull must give the same answer to "is this
    /// light visible", or a light one keeps is starved by the other.</summary>
    public const float FrustumCullSlack = 1.25f;

    /// <summary>Sphere-vs-camera-frustum visibility (conservative: the per-plane tests never cull a visible
    /// sphere, and may keep a corner-outside one). The camera basis is built from a LOOK-AT point (the chase
    /// camera has no roll; world is Z-up), deliberately NOT from the captured view quaternion.
    /// <paramref name="fovy"/> is degrees as SetPerspective delivers it; a degenerate look-at keeps it.</summary>
    public static bool SphereInCameraFrustum(Vector3 p, float r, Vector3 camPos, Vector3 lookAt,
                                             float fovy, float aspect, float near, float far)
    {
        Vector3 fwd = lookAt - camPos;
        if (fwd.LengthSquared() < 1e-8f) return true;
        CameraBasis(fwd, fovy, aspect, out fwd, out Vector3 right, out Vector3 up, out float tanX, out float tanY);
        Vector3 d = p - camPos;
        Vector3 v = new(Vector3.Dot(d, right), Vector3.Dot(d, up), 0f);
        float depth = Vector3.Dot(d, fwd);
        if (depth + r < near || depth - r > far) return false;
        if ((MathF.Abs(v.X) - depth * tanX) / MathF.Sqrt(1f + tanX * tanX) > r) return false;
        if ((MathF.Abs(v.Y) - depth * tanY) / MathF.Sqrt(1f + tanY * tanY) > r) return false;
        return true;
    }

    /// <summary>The roll-free camera basis (world is Z-up) and the half-fov tangents, shared by every
    /// camera-frustum test here. <paramref name="fovy"/> is degrees as SetPerspective delivers it (values
    /// &lt;= 3.2 = radians, defensively).</summary>
    private static void CameraBasis(Vector3 camFwd, float fovy, float aspect,
                                    out Vector3 fwd, out Vector3 right, out Vector3 up, out float tanX, out float tanY)
    {
        fwd = Vector3.Normalize(camFwd);
        right = Vector3.Cross(fwd, new Vector3(0f, 0f, 1f));
        // straight-down camera: pick any horizontal right axis (roll-free by construction)
        right = right.LengthSquared() < 1e-6f ? new Vector3(1f, 0f, 0f) : Vector3.Normalize(right);
        up = Vector3.Cross(right, fwd);
        float halfY = fovy * (fovy > 3.2f ? MathF.PI / 360f : 0.5f);
        tanY = MathF.Tan(halfY);
        tanX = tanY * aspect;
    }

    /// <summary>The camera frustum as six inward-facing planes (xyz = unit normal, w = offset, so
    /// <c>dot(n, p) + w</c> is the signed distance) from the same roll-free basis
    /// <see cref="SphereInCameraFrustum"/> builds, each pushed OUT by <paramref name="slack"/> world units.
    /// Order: near, far, left, right, bottom, top. A degenerate forward keeps everything (all w = +inf).</summary>
    public static void ReceiverPlanes(Vector3 camPos, Vector3 camFwd, float fovy, float aspect, float near,
                                      float far, float slack, Span<float> planes)
    {
        if (camFwd.LengthSquared() < 1e-8f)
        {
            for (int i = 0; i < 6; i++) { planes[i * 4] = 0f; planes[i * 4 + 1] = 0f; planes[i * 4 + 2] = 1f; planes[i * 4 + 3] = float.PositiveInfinity; }
            return;
        }
        CameraBasis(camFwd, fovy, aspect, out Vector3 fwd, out Vector3 right, out Vector3 up, out float tanX, out float tanY);
        Put(planes, 0, fwd, -Vector3.Dot(fwd, camPos) - near + slack);
        Put(planes, 1, -fwd, Vector3.Dot(fwd, camPos) + far + slack);
        // side planes pass through the eye: n = normalize(fwd * tan -/+ axis), w = -dot(n, eye)
        Vector3 nl = Vector3.Normalize(fwd * tanX + right), nr = Vector3.Normalize(fwd * tanX - right);
        Vector3 nb = Vector3.Normalize(fwd * tanY + up), nt = Vector3.Normalize(fwd * tanY - up);
        Put(planes, 2, nl, slack - Vector3.Dot(nl, camPos)); Put(planes, 3, nr, slack - Vector3.Dot(nr, camPos));
        Put(planes, 4, nb, slack - Vector3.Dot(nb, camPos)); Put(planes, 5, nt, slack - Vector3.Dot(nt, camPos));
    }

    private static void Put(Span<float> planes, int i, Vector3 n, float w)
    { planes[i * 4] = n.X; planes[i * 4 + 1] = n.Y; planes[i * 4 + 2] = n.Z; planes[i * 4 + 3] = w; }

    /// <summary>Can a caster's shadow reach the receiver frustum? The C# twin of the gather dispatch's test,
    /// term for term: the shadow volume is bounded by the convex hull of two spheres -- the caster's own and
    /// the extrusion's end -- and that hull misses the frustum only if BOTH spheres sit wholly outside ONE
    /// plane. Conservative: a caster this rejects casts on no visible pixel; one it keeps may still not.</summary>
    public static bool ShadowReaches(ReadOnlySpan<float> planes, Vector3 p0, float r0, Vector3 p1, float r1)
    {
        for (int i = 0; i < 6; i++)
        {
            Vector3 n = new(planes[i * 4], planes[i * 4 + 1], planes[i * 4 + 2]);
            float w = planes[i * 4 + 3];
            if (Vector3.Dot(n, p0) + w < -r0 && Vector3.Dot(n, p1) + w < -r1) return false;
        }
        return true;
    }

    /// <summary>The sun extrusion -- the caster swept along the light to the fit's far extent
    /// (<paramref name="along"/> from <paramref name="focus"/>), radius unchanged.</summary>
    public static bool SunShadowReaches(ReadOnlySpan<float> planes, Vector3 c, float r, Vector3 dir, Vector3 focus, float along)
    {
        float t = Vector3.Dot(c - focus, dir);
        return ShadowReaches(planes, c, r, c + dir * MathF.Max(0f, along - t), r);
    }

    /// <summary>The local-light extrusion -- the tangent cone from the light through the caster, out
    /// to the light's reach. The cone is wider than the caster at the caster's own depth
    /// (<c>dist * tan(a) &gt; r</c>), so both end radii are inflated by <c>dist / (dist - r)</c>; a light
    /// inside the caster keeps it.</summary>
    public static bool CubeShadowReaches(ReadOnlySpan<float> planes, Vector3 c, float r, Vector3 light, float reach)
    {
        Vector3 d = c - light;
        float dist = d.Length();
        if (dist <= r) return true;
        float k = 1f / (dist - r);
        return ShadowReaches(planes, c, r * dist * k, light + d * (reach / dist), r * reach * k);
    }

    /// <summary>A ground-ish focus point for the shadow map: march from the camera along its forward until it
    /// has descended <paramref name="dist"/> world units, so the map centres on the visible ground rather
    /// than on the elevated camera position. Camera forward is the view-space -Z axis rotated into world by
    /// the camera quaternion (SetViewTransform treats it as camera-&gt;world); <paramref name="dist"/>&lt;=0 = off.</summary>
    public static Vector3 CameraGroundFocus(Vector3 camPos, Quaternion camRot, float dist)
    {
        if (dist <= 0f) return camPos;
        Vector3 fwd = Vector3.Transform(ViewForward, camRot);
        return camPos + fwd * dist;
    }

    /// <summary>View-space forward (into the screen) = -Z (OpenGL). Rotated by the camera quaternion it gives
    /// the world direction the camera looks along.</summary>
    private static readonly Vector3 ViewForward = new(0f, 0f, -1f);

    /// <summary>System.Numerics' CreateOrthographic maps clip-z to the Direct3D range [0,1]; OpenGL wants NDC
    /// z in [-1,1]. This post-multiply (z' = 2z - w) converts it, so the depth buffer uses its full range and
    /// the debug view shows a real gradient. Identity on x/y/w, so it doesn't touch the projection otherwise.</summary>
    private static readonly Matrix4x4 D3DToGlClipZ = new(
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 2f, 0f,
        0f, 0f, -1f, 1f);

    /// <summary>World point -> light NDC (x,y in [-1,1] across the map; z the stored depth). For sampling/tests.</summary>
    public static Vector3 WorldToLightNdc(Matrix4x4 lightViewProj, Vector3 world)
    {
        Vector4 c = Vector4.Transform(new Vector4(world, 1f), lightViewProj);
        return c.W != 0f ? new Vector3(c.X, c.Y, c.Z) / c.W : new Vector3(c.X, c.Y, c.Z);
    }

    private static Vector3 Normalize(Vector3 v)
    {
        float len = v.Length();
        return len > 1e-6f ? v / len : new Vector3(0f, 0f, -1f);   // degenerate dir -> straight down (-Z, NWN is Z-up)
    }

    /// <summary>An up vector not parallel to the light, so LookAt doesn't degenerate. Prefers world up (+Z,
    /// NWN Z-up); falls back to +X when the light is near-vertical.</summary>
    private static Vector3 PerpendicularUp(Vector3 dir) =>
        MathF.Abs(dir.Z) < UpXEnter ? new Vector3(0f, 0f, 1f) : new Vector3(1f, 0f, 0f);

    /// <summary>|dir.Z| at which the light basis switches to the +X up, and the lower one it switches back at.</summary>
    private const float UpXEnter = 0.99f, UpXLeave = 0.98f;

    /// <summary>Hysteresis on the +Z/+X up switch, one latch per light. The switch turns the basis 90 degrees,
    /// which moves the texel snap grid and re-keys every bake, so a light hovering at the threshold (near-vertical
    /// rain under a gusting wind, the sun at zenith) must not flip on every frame it crosses it.</summary>
    public static bool LatchUpX(ref bool upX, Vector3 lightDir)
    {
        float z = MathF.Abs(Normalize(lightDir).Z);
        if (upX ? z < UpXLeave : z >= UpXEnter) upX = !upX;
        return upX;
    }
}
