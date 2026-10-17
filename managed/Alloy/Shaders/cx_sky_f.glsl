// CX-88: the SKY, as an Alloy program. What is ahead of the region is the ENVIRONMENT -- the names
// inc_common/inc_uniforms/inc_standard give a sky fragment stage, from Alloy's values, under the ENGINE's
// names -- so the content's own sky stage (e.g. a ~1900-line cloud, star, sun and moon stage)
// drops in unedited. Between the markers is that stage, its main() renamed; the stub is a black dome.
// A content stage's Alloy-native twin (cx_<stage>) runs in the same region, built with CX_SKY_LINEAR: it hands
// back linear HDR and reads the images and frame its #pragma cx_sky lines declare (docs/shader-overrides.md).
#version 460 core
in vec2 vUV;
flat in float vAlpha;
in vec3 vWorld;
layout(location = 0) out vec4 frag;
#include "cx_featdecl.glsl"
#include "cx_fogdecl.glsl"
#include "cx_fogmix.glsl"
#include "cx_oitout.glsl"
uniform sampler2D uTex;          // material texture 0
uniform vec3 uTint;              // #222: white, or the scene's fog colour for the skyfade gob
uniform float uSkyFog;           // the horizon band (see cx_owned_f.glsl); 0 = no band
uniform mat4 uViewInv;           // the camera's world matrix: column 3 is the eye, which is all stock reads
// The dome's own varyings and samplers, under the engine's names; the rest of the environment is the sky's shared one.
#define VertexColor COLOR_WHITE
#define vTexCoords vUV
#define vPosWorld vWorld
#define texUnit0 uTex
#define m_view_inv uViewInv
#include "cx_skyenv.glsl"

// The stock working colour, and the texel inc_standard's setup call multiplies into it -- sampled by the
// wrapper before the stage's body runs, in DISPLAY space, which is where the stage expects to find it.
vec4 FragmentColor;
vec4 CxWholeTexel;

// $VANILLA-MAIN $SKYFS$
void CxVanMain() { FragmentColor = vec4(0.0, 0.0, 0.0, 1.0); }
// $VANILLA-END

void main()
{
    CxWholeTexel = texture(uTex, vUV, uLodBias);
    FragmentColor = vec4(0.0, 0.0, 0.0, 1.0);
    CxVanMain();
    // The stage hands back DISPLAY space (its own RevertColorSpace tail, and every layer after it is
    // display-space arithmetic); the Alloy frame is LINEAR, so the one encode comes off here. That is what
    // the #136 SkyDecodeFs fullscreen pass used to do a pass later over every depth==1.0 pixel.
    // The stage was authored against the engine's UNORM target: [0,1], no NaN. The frame and the probe cube
    // are float, and one NaN texel poisons the probe's whole mip chain and SH (every reflection went white).
#ifdef CX_SKY_LINEAR
    // An Alloy-native twin hands back LINEAR HDR (a sun that blooms), so neither the clamp nor the encode applies;
    // NaN and half-float overflow are still kept out of the frame and the probe cube.
    vec4 c = mix(vec4(clamp(FragmentColor.rgb, 0.0, 60000.0), clamp(FragmentColor.a, 0.0, 1.0)), vec4(0.0),
                 isnan(FragmentColor));
    vec3 rgb = c.rgb;
#else
    vec4 c = mix(clamp(FragmentColor, 0.0, 1.0), vec4(0.0), isnan(FragmentColor));
    vec3 rgb = pow(c.rgb, vec3($SRGB$));
#endif
    if (uSkyFog > 0.0 && uFogParams.x != 0.0)
        rgb = CxFogMix(rgb, vWorld, 1.0 - smoothstep(0.0, uSkyFog, normalize(vWorld - uEyePos).z));
    frag = CxOitOut(vec4(rgb, c.a));
}
