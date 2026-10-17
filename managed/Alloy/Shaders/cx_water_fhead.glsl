#version 460 core
#define CX_FS 1   // #642: gl_FragCoord is in reach for the shadow lib's dither
#define CX_LIGHT_LITE 1   // CX-188: cx_lightfrag's slimmed walk -- not a baked receiver, one shadow tap per light
in vec2 vUV;
in vec3 vWorld;
in vec3 vNrm;
in float vDepth;
flat in int vDraw;
#include "cx_drawbuf.glsl"
#include "cx_featdecl.glsl"
#include "cx_keyfogdecl.glsl"
uniform sampler2D uTex;        // the water material's diffuse -- its AVERAGE is the liquid's colour
// #347: VanillaEnvFs aliases this as texUnit6. Declared, never bound, never sampled here -- nothing
// spliced into this stage reads it since #350 (water takes the DISPLAY hook alone). Present so a
// content file that names texUnit6 still compiles; unused, it is eliminated and its location
// resolves to -1.
uniform sampler2D uFlowTex;
// #129: the one framebuffer copy, taken at PassId.FbCopy. Since #132 its colour is LINEAR -- the
// copy is of PostChain's own target, every Alloy stage writes linear into it, and the single encode
// happens later at the resolve. Depth is the raw window-space buffer, which CxSceneZ linearises
// with this pass's own near/far rather than with an engine uniform.
uniform sampler2D uFbColor;
uniform sampler2D uFbDepth;
// #339: the engine's own texUnitNoise -- the resman texture `solid_noise`, bound by the pass. The
// wave field IS this texture's red channel at four scales, so a procedural stand-in is a different
// sea rather than a slightly different one.
uniform sampler2D uNoise;
// #339: the SCENE's environment map (Scene+0x1e8), which stock water reflects at FULL strength --
// see the composite for why. uEnvOk is 0 where the scene has none, and then the term is skipped
// rather than multiplied by a black texel.
uniform sampler2D uEnvMap;
uniform int uEnvOk;
uniform vec2 uNearFar;
uniform mat4 uView;            // world -> view, this pass's own (see uVP's row-vector note)
uniform mat4 uProj;
// CX-43: uAreaAmbient / uAreaDiffuse / uAreaDir ride the pass block.
#include "cx_pass.glsl"
// uAreaWind (areaGlobalWind), uTimeOfDay (worldtimerTimeOfDay) and uEyePos come from FeatDecl and
// KeyFogDecl, which every Alloy fragment stage carries (#341 moved the eye into the fog block, since
// the fog target is a direction now). The clock is deliberately the GAME one: vswater.shd derives
// every phase from it, so a module running time at 30x real gets vanilla's own fast sea, not a
// corrected one.
uniform int uViz;
uniform int uCapture;
uniform float uAlphaCut;
out vec4 frag;

// inc_water.shd's config block, verbatim.
const float CX_ANIMATION_SPEED = 1.0;
const int CX_WAVE_ITERATIONS = 3;
const float CX_WATER_TRANSPARENCY_FACTOR = 0.25;
const float CX_WATER_SPECULARITY = 0.02;
const int CX_WATER_REFRACTION_ITERATIONS = 5;
const float CX_WATER_REFRACTION_MAX_RANGE = 4.0;
// fswater.shd's own displacement search, at SHADER_QUALITY_MODE 2.
const float CX_VECTOR_LIMIT = 0.01;
const int CX_DISPLACEMENT_ITERATIONS = 4;
// inc_material.shd's environment LOD ramp, applied as a texture BIAS exactly as stock does
// (texture2D's third argument is a bias, not a level).
const float CX_ENV_LOD_MOD = 30.0, CX_ENV_LOD_OFF = -1.0, CX_ENV_LOD_MAX = 10.0;
// LIGHT_SUBSURFACE_SCATTERING_WRAP at this quality mode, and inc_lighting.shd:537's energy
// preservation for it -- 2*(1+w)/(2+PI*w), which is 0.778 at w=1 and is what stops a wrapped
// diffuse from adding light the surface never received.
const float CX_SSS_WRAP = 1.0;
const float CX_SSS_ENERGY = 2.0 * (1.0 + CX_SSS_WRAP) / (2.0 + 3.14159265 * CX_SSS_WRAP);
