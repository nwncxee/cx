// #124: the fragment half of the dissolve and the fog, ported from the current inc_keyhole.shd /
// inc_standard.shd (ovr.bif, the set the client runs). Constants, the simplex noise and the
// punch-through/border split are verbatim; what changed is only where the inputs come from.
// The engine computes fFragCamOffset in its VERTEX stage and interpolates it. Recomputing
// it per fragment from vDepth is the same value: both are perspective-correct interpolations of
// a linear function of the view position, and gl_Position.w IS -vPosView.z.
// The engine reconstructs the world position with m_view_inv * vPosView purely because its
// stage does not carry one. Ours does, and it is the same point.
#include "cx_noise.glsl"
// inc_keyhole.shd's own constants (:56-63). The floor offset -- the height above the player below which
// nothing dissolves, what stops the floor under the PC being punched out along with the wall -- is the
// engine's keyholeMinZOffset uniform (:61, :98), uKeyholeDia.z.
const float CX_KH_BLACK = 0.8, CX_KH_NOISE = 0.35, CX_KH_PUNCH = 0.3, CX_KH_SIZE = 12.5;

/// Returns the LINEAR colour multiplier the dissolve border wants, or a negative value meaning
/// "punch through" -- the caller discards. 1.0 = this fragment is not in the aperture at all.
float CxKeyhole(float dissolve, vec3 world, float depth)
{
    // vDepth is -vPosView.z, so every sign below is the engine's with that substitution made.
    float off = depth - uKeyholeCam.x + (90.0 - uKeyholeCam.y) / 45.0;
    if (dissolve <= 0.0 || off >= 0.0) return 1.0;
    float r = clamp((uKeyholeDia.y - uKeyholeDia.x) * uKeyholeDia.x * CX_KH_SIZE / depth
                    + uKeyholeDia.x, uKeyholeDia.x, uKeyholeDia.y);
    float r2 = r * r;
    vec2 cv = vec2(2.0 * gl_FragCoord.x * uPixel.x - 1.0, 2.0 * gl_FragCoord.y * uPixel.y - 1.0);
    if (uPixel.x < uPixel.y) cv.x *= uPixel.z; else cv.y /= uPixel.z;
    float d2 = dot(cv, cv);
    if (d2 >= r2) return 1.0;
    float noise = CxNoise3D(world) * 0.5 + 0.5;
    float lvl = 1.0 - d2 / r2 - noise * CX_KH_NOISE + 0.5 / off;
    lvl *= clamp(world.z - uKeyholeDia.z - uPlayerPos.z, 0.0, 1.0);
    if (lvl >= CX_KH_PUNCH) return -1.0;
    if (lvl > 0.0) return 1.0 - CX_KH_BLACK * (lvl * lvl / (CX_KH_PUNCH * CX_KH_PUNCH));
    return 1.0;
}
