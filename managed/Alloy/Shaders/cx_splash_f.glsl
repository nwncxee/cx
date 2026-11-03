#version 460 core
// CX-102: one part of a rain crown: the ring on the ground, the crown's sheet of water or a puddle's rebound column,
// or a bead off the lip; all are premultiplied like the drop and the flake, so an unlit impact is nothing rather than a grey
// mark on the stone. No contact fade here: a splash SITS on the surface, and the fade that saves a falling
// drop from a hard intersection line would erase the whole thing. In the scene the hardware depth test puts
// a splash behind a wall in front of it; drawn after the temporal resolve there is no depth target, and this
// stage tests the depth copy itself.
in vec2 vUV;
in float vSeed;
in float vAge;
in float vPart;
in vec3 vLight;
in float vViewZ;
in vec3 vSide;
in vec3 vUp;
in vec3 vSkyUp;
in vec3 vSkyLow;
in vec3 vLampDir;
in float vSunVis;
uniform vec3 uTint;
uniform float uAlpha;
uniform int uLate;       // 1 = drawn after the upscale or the TAA resolve, with no depth target
uniform vec2 uScreen;    // the target being drawn, pixels: the depth copy is sampled across it whatever its own size
// uAreaDiffuse / uAreaDir ride the pass block.
#include "cx_pass.glsl"
#include "cx_scenedepth.glsl"
#include "cx_wx_hit.glsl"
#include "cx_wx_drop.glsl"

/// Rec.709 luminance: how much of its own scatter a splash also occludes.
const vec3 CX_WX_LUMA = vec3(0.2126, 0.7152, 0.0722);
/// Half-width of the ring's crest, as a share of the quad's half-extent. One world width at every radius.
/// The quad's half-extent is 1.15 reaches, so the crest is 2 x 0.13 x 1.15 = 0.3 of a reach -- which is the
/// vertex stage's CX_SPLASH_CREST, the share its footprint fade measures. Move one and move the other.
const float CX_SPLASH_RINGW = 0.13;
/// Where the crest has got to by the end of the life, in the same share. Short of 1 so the ring dies inside
/// its own quad instead of being clipped by its edge.
const float CX_SPLASH_RINGR = 0.87;
/// The ring is the faint half of an impact: the crown is what the eye reads, the ring only says "on water".
const float CX_SPLASH_RINGA = 0.5;
/// Metres, plus a share of the scene's distance, a part may stand behind the depth copy and still be ON the
/// surface there: the ring stands off it and the copy is the render size's, so one of its texels spans more
/// than one target pixel.
const vec2 CX_SPLASH_HIDE = vec2(0.1, 0.01);

/// True when a part `splash` metres away stands behind the scene the depth copy holds `scene` metres away.
bool CxSplashHidden(float scene, float splash)
{
    return splash - scene > CX_SPLASH_HIDE.x + CX_SPLASH_HIDE.y * scene;
}

void main()
{
    if (uLate != 0 && CxSplashHidden(CxSceneViewZ(gl_FragCoord.xy / uScreen), -vViewZ)) discard;
    float a;
    int kind = int(vPart + 0.5);
    if (kind == CX_PART_RING)
    {
        float r = length(vUV);
        if (r > 1.0) discard;
        // sqrt in time, damped: the crest leaves fast, slows as it spreads and is gone by the life's end
        float u = (r - CX_SPLASH_RINGR * sqrt(vAge)) / CX_SPLASH_RINGW;
        a = exp(-u * u) * (1.0 - vAge) * CX_SPLASH_RINGA;
    }
    else if (kind != CX_PART_BEAD)
    {
        // the crown's sheet, or the rebound column out of a puddle, sinking back as it ages
        float solid = kind == CX_PART_COLUMN ? 1.0 : 0.0;
        vec2 shape = solid > 0.5 ? CxSplashColumn(vAge) : CxCrownShape(vAge);
        vec3 V = normalize(cross(vSide, vUp));
        vec4 c = CxCrownSheet(vUV, shape, solid, V, vSide, vUp, vSkyUp, vSkyLow, vLampDir, uTint * vLight);
        if (c.a <= 0.0) discard;
        if (uViz == CX_SPLASH_VIZ) { frag = vec4(vLight, 1.0) * (c.a * vSeed); return; }
        c.rgb += CxCrownSheet(vUV, shape, solid, V, vSide, vUp, vec3(0.0), vec3(0.0), uAreaDir, uTint * uAreaDiffuse * vSunVis).rgb;
        frag = vec4(c.rgb, c.a * uAlpha) * vSeed;
        return;
    }
    else
    {
        // a bead off the lip: the falling drop's own water bead, fading as it spends itself
        if (dot(vUV, vUV) >= 1.0) discard;
        if (uViz == CX_SPLASH_VIZ) { frag = vec4(vLight, 1.0) * vSeed; return; }
        vec3 V = normalize(cross(vSide, vUp));
        vec4 b = CxWxBead(vUV, V, vSide, vUp, vSkyUp, vSkyLow, vLampDir, uTint * vLight, uAreaDir, uTint * uAreaDiffuse * vSunVis);
        frag = vec4(b.rgb, b.a * uAlpha) * ((1.0 - vAge * vAge) * vSeed);
        return;
    }
    if (a <= 0.01) discard;
    // R1: premultiplied scatter, blended ONE / ONE_MINUS_SRC_ALPHA -- the precipitation stages' contract.
    // rgb is the light this part throws at the eye weighted by its coverage; alpha is the extinction behind
    // it, riding the SAME scatter, so a splash with no light on it neither adds nor hides anything.
    float cov = a * vSeed;
    if (uViz == CX_SPLASH_VIZ) { frag = vec4(vLight * cov, cov); return; }
    vec3 scatter = uTint * vLight;
    frag = vec4(scatter * cov, cov * uAlpha * min(dot(scatter, CX_WX_LUMA), 1.0));
}
