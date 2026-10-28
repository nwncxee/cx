// The vertex main, composed after the light libraries.
void main()
{
    vCellUv = vec2(0.0); vCellBase = vec2(0.0); vTuft = 1.0; vLight = vec3(0.0); vRoot = aPos.z;
    float dist = distance(uEyePos, iPos.xyz);
    // The root blend reads the pixel behind the blade, which is ground only when the eye is above the
    // root: from below (a cliff seen from its foot) that pixel is sky, and the blade would levitate.
    vLookDown = smoothstep(0.02, 0.12, (uEyePos.z - iPos.z) / max(distance(uEyePos.xy, iPos.xy), 0.001));
    float kv = CxGrassKeep(dist);
    // A blade past its own keep edge is gone; one AT the edge thins out over the last 20% of the
    // prefix so the count changing under a moving camera never pops. Culled before rasterisation.
    float lod = clamp((kv * 1.2 - iParam.z) / (kv * 0.2), 0.0, 1.0);
    // snow: the highest-ranked blades go first, the same prefix the distance ladder keeps -- so a snowed
    // field thins the way a far one does, without a rebake
    if (uSnow.x > 0.0 && iParam.z > 1.0 - uSnow.x) lod = 0.0;
    if (lod <= 0.0)
    {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        vWorld = vec3(0.0); vNrm = vec3(0.0, 0.0, 1.0);
        vDepth = 1.0; vFade = 0.0; vDist = 0.0; vSeed = 0u;
        return;
    }
    // The survivors stand in for the blades the prefix dropped: wider and taller, by the ladder's
    // exponents, so a far patch reads as a covered meadow rather than a thinning one.
    float widen = pow(kv, -uLod.z), grow = pow(kv, -uLod.w);
    if (uCast != 0) widen *= uCastWidth;
    float c = cos(iPos.w), s = sin(iPos.w);
    vec3 p = vec3((aPos.x * c - aPos.y * s) * widen, (aPos.x * s + aPos.y * c) * widen, aPos.z * grow) * iParam.x;
    p.z *= 1.0 - 0.4 * uSnow.y;   // beaten down under the weight

    // The engine's areaGlobalWind is a world vector; a still area still wants a breeze, so a floor
    // stands in for it the way the stock wind shaders' 0.356 does.
    vec2 dir = uAreaWind.xy;
    float mag = length(dir);
    if (mag < 1e-4) { dir = vec2(0.7071, 0.7071); mag = 0.35; }
    else dir /= mag;
    float t = CxClock() * uWindSpeed;
    float phase = dot(iPos.xy, vec2(0.21, 0.17));
    // The amplitude is CLAMPED to a fraction of the blade's own height before it scales anything:
    // areaGlobalWind is an unbounded world vector, and an area that sets a big one must bend the
    // grass, not launch it.
    float amp = clamp(iParam.w * mag * uWindGain, 0.0, 1.0) * 0.35 * iParam.x;
    // root-anchored (z^2, so the base never leaves the ground), and the same scale last frame's sway takes
    float bend = aPos.z * aPos.z * amp;
    float sway = CxGrassSway(t, phase) * bend;
    p.xy += dir * sway;
    p.z -= abs(sway) * 0.25;            // a leaning blade is shorter, or it stretches

    // Creature and object displacement, and object culling. One fetch at the blade's ROOT -- the
    // whole blade bends as a unit, which is what a stalk does, and it means the field is sampled once
    // per blade rather than once per vertex position.
    vec2 puv = (iPos.xy - uPushWindow.xy) * uPushWindow.z;
    if (puv.x > 0.0 && puv.y > 0.0 && puv.x < 1.0 && puv.y < 1.0)
    {
        vec4 field = texture(uPushTex, puv);
        // A blade standing inside a solid object is not bent, it is gone. Thresholded against this
        // blade's OWN stable jitter rather than a constant, so the footprint thins out toward its
        // edge instead of ending in a straight line -- and it is stable frame to frame, because the
        // jitter is baked, so nothing flickers as the camera moves.
        if (field.a > mix(0.30, 0.95, fract(iParam.w * 7.31)))
        {
            // Outside clip space on every axis: the whole blade is discarded before rasterisation.
            gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
            vWorld = vec3(0.0); vNrm = vec3(0.0, 0.0, 1.0);
            vDepth = 1.0; vFade = 0.0; vDist = 0.0; vSeed = 0u;
            return;
        }
        if (uPushGain > 0.0)
        {
            vec2 push = (field.rg * 2.0 - 1.0) * uPushGain;
            float bendZ = aPos.z * aPos.z;
            p.xy += push * bendZ * iParam.x;
            // ...and pressed DOWN by its own channel, which peaks directly UNDER the gob where there
            // is no direction to lean in. Read off the map rather than derived from the lateral
            // magnitude, or the one tuft between a creature's feet stands perfectly upright.
            p.z -= min(field.b * uPushGain, 0.9) * bendZ * iParam.x;
        }
    }

    vWorld = iPos.xyz + p;
    gl_Position = uVP * vec4(vWorld, 1.0);
    vDepth = gl_Position.w;
#ifdef CX_MOTION
    // CX-48: last frame's blade -- the same sway at last frame's clock, over this frame's widening and
    // this frame's push field (which is not kept from frame to frame), so a blade bent by a passing
    // creature carries its sway term alone.
    float swayPrev = CxGrassSway(CxPrevClock() * uWindSpeed, phase) * bend;
    vec3 pPrev = p;
    pPrev.xy += dir * (swayPrev - sway);
    pPrev.z -= (abs(swayPrev) - abs(sway)) * 0.25;
    vCur = gl_Position;
    vPrev = uPrevVP * vec4(iPos.xyz + pPrev, 1.0);
#endif
    // Grass shades best off an up-ish normal with a per-blade tilt: a true card normal turns half
    // the field black whenever the sun is not behind the camera, which is not what a lawn does. CX-24: "up"
    // is the ground the tuft stands on, and the normal rounds outward across the card (a lit side and a
    // shaded side to every tuft); far out it is the ground's alone, so a far field shades like its hill.
    vec2 across = vec2(aPos.x * c - aPos.y * s, aPos.x * s + aPos.y * c);
    vNrm = normalize(vec3(c, s, 0.0) * 0.3 + vec3(across, 0.0) * 1.2 + iNrm.xyz);
    vNrm = normalize(mix(vNrm, iNrm.xyz, smoothstep(uLod.x * 0.5, uLod.x, dist)));
    int cell = int(iParam.y + 0.5);
    vCellUv = aUV;
    vCellBase = vec2(float(cell & 1), float(cell >> 1)) * 0.5;
    // A far blade stands in for the ones the ladder dropped and is widened for it; rather than one
    // stretched sprite, its quad carries the cell that many times over -- a tuft, at no extra instance.
    vTuft = clamp(floor(widen + 0.5), 1.0, max(uTuft, 1.0));
    vFade = (1.0 - smoothstep(uFade.x, uFade.y, dist)) * lod;
    vDist = dist;
    vSeed = uint(gl_InstanceID);
    // The cluster loop per vertex: the blade's normal is flat, so the point-light sample point moves
    // from the pixel to the vertex. The SUN's shadow stays per fragment (#196): a neighbouring
    // blade's shadow is thinner than the gap between two vertices and averaged away here.
    // Not gated by uShade: past it only the SHADOW taps stop. A blade is a handful of vertices, and
    // without its point lights a night field past 40u went black under lit lamps.
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
