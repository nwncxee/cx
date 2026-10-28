// The blade vertex main: a quadratic Bezier (root, bend, tip) swept across a tapering width.
const float CX_KH_PUNCH = 0.3, CX_KH_SIZE = 12.5;   // cx_keyhole.glsl's own

// cx_keyhole.glsl's aperture, asked once per blade at its root: a blade is punched out whole or not at all,
// which keeps the fragment stage free of discard (and so of the early-Z it would cost).
bool CxBladeKeyholed(vec3 root, vec4 clip)
{
    float depth = clip.w;
    float off = depth - uKeyholeCam.x + (90.0 - uKeyholeCam.y) / 45.0;
    if (off >= 0.0 || depth <= 0.0) return false;
    float r = clamp((uKeyholeDia.y - uKeyholeDia.x) * uKeyholeDia.x * CX_KH_SIZE / depth + uKeyholeDia.x,
                    uKeyholeDia.x, uKeyholeDia.y);
    vec2 cv = clip.xy / clip.w;
    if (uPixel.x < uPixel.y) cv.x *= uPixel.z; else cv.y /= uPixel.z;
    float lvl = 1.0 - dot(cv, cv) / (r * r) + 0.5 / off;
    lvl *= clamp(root.z + 0.5 - uKeyholeDia.z - uPlayerPos.z, 0.0, 1.0);
    return lvl >= CX_KH_PUNCH;
}

// The blade's curve: a quadratic Bezier over (root, bend, tip) whose tip comes DOWN as the blade leans
// rather than stretching away from it, plus the tangent along it. One text, so last frame's blade is this
// same shape under last frame's lean.
vec3 CxBladeCurve(vec3 root, vec2 lean, float h, float press, float t, out vec3 T)
{
    float ll = min(length(lean), h * 0.95);
    lean = ll > 0.0 ? normalize(lean) * ll : lean;
    vec3 p0 = root, p2 = root + vec3(lean, sqrt(max(h * h - ll * ll, 0.0)) * (1.0 - press));
    vec3 p1 = root + vec3(lean * 0.3, (p2.z - root.z) * 0.65);
    float u = 1.0 - t;
    T = normalize(2.0 * u * (p1 - p0) + 2.0 * t * (p2 - p1) + vec3(0.0, 0.0, 1e-4));
    return u * u * p0 + 2.0 * t * u * p1 + t * t * p2;
}

void main()
{
    vLight = vec3(0.0);
    vec3 root = iPos.xyz;
    uint mat = uint(iParam.y + 0.5) >> 2u, cell = uint(iParam.y + 0.5) & 3u;
    vec4 rootRamp = CxGrassMat(mat, 4 + int(cell) * 3);
    vColRoot = rootRamp.rgb;
    vColMid = CxGrassMat(mat, 5 + int(cell) * 3).rgb;
    vColTip = CxGrassMat(mat, 6 + int(cell) * 3).rgb;
    vMatDif = CxGrassMat(mat, 2).rgb;
    vMatAmb = CxGrassMat(mat, 3).rgb;
    vFade = iNrm.w;
    float dist = distance(uEyePos, root);
    vDist = dist;
    vLookDown = smoothstep(0.02, 0.12, (uEyePos.z - root.z) / max(distance(uEyePos.xy, root.xy), 0.001));

    uint pairs = uint(max(uSegments, 1)), vid = uint(gl_VertexID);
    float t = min(float(vid >> 1u) / float(pairs), 1.0);
    float side = vid >= 2u * pairs ? 0.0 : ((vid & 1u) == 0u ? -1.0 : 1.0);
    vRoot = t;

    // the far ladder's survivors stand in for the blades it dropped: wider, taller
    float kv = CxGrassKeep(dist);
    // the art's own blade length, not the card's: a card's tallest blade reaches its top, the tuft's mean does not
    float len = iParam.x * max(rootRamp.w, 0.2);
    float widen = pow(kv, -uLod.z), h = len * pow(kv, -uLod.w) * (1.0 - 0.4 * uSnow.y);
    if (uGroundOn == 0) h *= iNrm.w;
    if (uCast != 0) widen *= uCastWidth;
    vec2 face = vec2(cos(iPos.w), sin(iPos.w));
    // the facing turned toward the eye, so the lit side of a two-sided strip is the side you see
    if (dot(face, uEyePos.xy - root.xy) < 0.0) face = -face;
    vec3 across = vec3(-face.y, face.x, 0.0);

    // rest curvature: a blade leans off its root and droops toward the tip, by its own bend jitter
    vec2 lean = -face * h * (0.1 + 0.35 * (iParam.w - 0.7));
    vec2 dir = uAreaWind.xy;
    float mag = length(dir);
    if (mag < 1e-4) { dir = vec2(0.7071, 0.7071); mag = 0.35; } else dir /= mag;
    float tw = CxClock() * uWindSpeed, phase = dot(root.xy, vec2(0.21, 0.17));
    // the gust this blade takes, and the same scale last frame's sway takes
    float gust = clamp(iParam.w * mag * uWindGain, 0.0, 1.0) * 0.45 * h;
    float sway = CxGrassSway(tw, phase);
    lean += dir * sway * gust;
    vec2 puv = (root.xy - uPushWindow.xy) * uPushWindow.z;
    float press = 0.0;
    if (uPushGain > 0.0 && puv.x > 0.0 && puv.y > 0.0 && puv.x < 1.0 && puv.y < 1.0)
    {
        vec4 field = texture(uPushTex, puv);
        lean += (field.rg * 2.0 - 1.0) * uPushGain * h;
        press = min(field.b * uPushGain, 0.9);
    }
    vec3 T;
    vec3 P = CxBladeCurve(root, lean, h, press, t, T);
    float u = 1.0 - t;

    // wider when seen edge-on, so a blade never thins to a sub-pixel sliver that TAA has to rebuild
    vec3 V = normalize(uEyePos - P);
    float edgeOn = 1.0 - abs(dot(V.xy, face));
    float width = len * uWidth * widen * pow(u, 0.7) * (1.0 + 1.5 * edgeOn * edgeOn);
    P += across * side * width * 0.5;

    // the rounded normal: the strip's own normal tilted across its width (Tsushima's trick), so a flat blade
    // shades as a stalk with a lit and a shaded side; far blades take the ground's normal instead
    vec3 nb = normalize(cross(across, T));
    if (dot(nb.xy, face) < 0.0) nb = -nb;
    vec3 N = normalize(nb + across * side * 0.6);
    N = normalize(mix(N, iNrm.xyz, smoothstep(uLod.x * 0.5, uLod.x, dist)));

    vWorld = P;
    vNrm = N;
    gl_Position = uVP * vec4(P, 1.0);
    vDepth = gl_Position.w;
#ifdef CX_MOTION
    // CX-48: last frame's blade -- the same sway at last frame's clock through the same curve, over this
    // frame's width and this frame's push field (which is not kept from frame to frame).
    vec2 leanPrev = lean + dir * (CxGrassSway(CxPrevClock() * uWindSpeed, phase) - sway) * gust;
    vec3 Tprev;
    vec3 Pprev = CxBladeCurve(root, leanPrev, h, press, t, Tprev) + across * side * width * 0.5;
    vCur = gl_Position;
    vPrev = uPrevVP * vec4(Pprev, 1.0);
#endif
    if (uCast == 0 && CxBladeKeyholed(root, uVP * vec4(root, 1.0)))
    { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }
    // A depth or velocity stage never reads vLight and compiles the rig out: NVIDIA's pipeline compiler
    // crashes eliminating this loop as dead once the fragment half leaves its output unread.
#ifndef CX_GRASS_UNLIT
    if (uVLit != 0)
    {
        cxClusterXY = (gl_Position.xy / max(gl_Position.w, 1e-3) * 0.5 + 0.5) * vec2(uGrid.xy);
        vLight = uLit != 0 ? CxClusteredDiffuse(vWorld, vNrm, vDepth) : vec3(0.0);
    }
#endif
}
