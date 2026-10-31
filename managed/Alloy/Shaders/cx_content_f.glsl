// CX-303: the environment a CONTENT fragment stage compiles in -- the `customShaderFS` of a .mtr pair. ContentStage
// splices this ahead of the stage's own text, inside its pair's own programs, and #undefs every macro here after
// it, so nothing below reaches Alloy's own code.
//
// The stage runs as a SURFACE MODIFIER: stock setup has already run (FragmentColor is the texel, vFragmentNormal the
// shading normal, the material globals synthesised), the body edits them, and Alloy lights what it leaves. Its
// ApplyStandardShader is the stand-in's no-op; what follows the call runs on the lit colour (CxHostPost).
//
// The engine's vocabulary (inc_common, inc_uniforms, the GLRender prologue) comes in three kinds here: a name
// mapped onto the Alloy value that IS it, a name with no Alloy source held at the value it has on a world part
// the engine draws with nothing set, and a stock helper function. Every helper is guarded, so a stage that
// defines its own copy (the flowmaps inline SetupTSB and friends) keeps it: the translator defines
// CX_CON_HAS_<name> for each function the stage defines.
//
// "View space" is CAMERA-CENTRED WORLD space: world axes, origin at the eye. The vanilla environment already reads
// normals and directions as world-oriented (m_view's rotation is the identity, cx_vanenv_f), and content reads
// positions both ways -- normalize(vPosView) as the view direction (fs_snowed01) and
// m_view_inv * vPosView back to a world position (fslit_hair_nm). This frame keeps both true.

// ---- the prologue GLRender::LoadAndCompileSingleShader (@0x4d34b0) puts ahead of every content stage. The
// build numbers ride the per-program block ContentStage emits; GAMMA_CORRECTION, texture2D and textureCube are
// cx_vanenv_defs'.
#define mediump
#define lowp
#define highp
#define MAX_NUM_LIGHTS 32
#define MAX_NUM_BONES 64
#define FRAGMENT_LIGHTING 1
// The Alloy lobe is GGX + Schlick geometric shadowing unconditionally, which is the stock high setting.
#define SHADER_QUALITY_MODE 2
#define KEYHOLING_ENABLED 1
#define SHADER_DEBUG_MODE 0
// One program per stage, so the engine's per-draw NO_DISCARD permutation is the discarding one.
#define NO_DISCARD 0
#define POSTPROCESSING_TYPES_ENABLED 0
#define varying in
#define attribute in
// inc_common's colour constants beyond the COLOR_WHITE cx_vanenv_defs carries
#define COLOR_BLACK vec4(0.0, 0.0, 0.0, 1.0)
#define COLOR_GREY vec4(0.5, 0.5, 0.5, 1.0)
#define COLOR_RED vec4(1.0, 0.0, 0.0, 1.0)
#define COLOR_GREEN vec4(0.0, 1.0, 0.0, 1.0)
#define COLOR_BLUE vec4(0.0, 0.0, 1.0, 1.0)
#define COLOR_MAGENTA vec4(1.0, 0.0, 1.0, 1.0)
#define COLOR_YELLOW vec4(1.0, 1.0, 0.0, 1.0)
#define COLOR_CYAN vec4(0.0, 1.0, 1.0, 1.0)

// ---- the stock varyings, off the lit stage's own inputs
#undef vTexCoords
#define vTexCoords cxConUV
#define vVertexTexCoords vUV
#define vVertexNormal (normalize(vNrm))
#define vPosView (vWorld - uEyePos)
#define vVertexTangent (vTan.xyz)
#define fTextureHandedness (vTan.w)
#define fFogFragCoord vDepth
#define vVertexTexCoordsEnvironment (CxConSphereUV(vFragmentNormal))
#define vVertexTexCoordsEnvironmentCube (reflect(normalize(vWorld - uEyePos), vFragmentNormal))
// A varying the stage's own vertex half writes reaches it under its own name (the translator drops the macro); these
// are what the two names read under a stock vertex half -- the world height every content stage that writes
// fVertexHeight computes, and the keyhole offset SetupKeyholeFactors would have handed over.
#define fVertexHeight (vWorld.z)
#define fFragCamOffset (vDepth - uKeyholeCam.x + (90.0 - uKeyholeCam.y) / 45.0)

// ---- matrices, in the camera-centred frame above
#undef m_view
#undef m_view_inv
#define m_view (CxConView(-uEyePos))
#define m_view_inv (CxConView(uEyePos))
#define m_m (uDraw[vDraw].model)
#define m_mv (CxConView(-uEyePos) * uDraw[vDraw].model)
#define m_mvp (uVP * uDraw[vDraw].model)
#define m_normal (mat3(uDraw[vDraw].model))
#define m_vp uVP
#define m_vp_inv (inverse(uVP))
#define m_proj_inv (inverse(m_proj))
#define m_texture (CxConTexMat(uDraw[vDraw].rot.xy))

// ---- the material units (GLRender::ConnectShaderData @0x4ce8e0 pins texUnitN to unit N) and their flags
#define texUnit1 uNrmTex
#define texUnit2 uSpecTex
#define texUnit3 uRoughTex
#define texUnit4 uHeightTex
#define texUnit5 uIllumTex
#define texture0Bound 1
#define texture1Bound CxConBound(CX_SLOT_NORMAL)
#define texture2Bound CxConBound(CX_SLOT_SPEC)
#define texture3Bound CxConBound(CX_SLOT_ROUGH)
#define texture4Bound CxConBound(CX_SLOT_HEIGHT)
#define texture5Bound CxConBound(CX_SLOT_ILLUM)
#define texture6Bound CxConBound(CX_SLOT_FLOW)
// The per-area probe is what Alloy reflects in place of the material's own env map, and it holds linear radiance.
#define texUnitEnvCube uProbe
#define texEnvCubeBound CxConBound(CX_SLOT_ENV)
#define envMapCube 1
#define texUnitEnv uTex
#define texEnvBound 0
// Units 11/12 are bound only by the engine's framebuffer-effect chain, never for a world part.
#define texFBColor uTex
#define texFBDepth uSceneDepth
#define texFBColorBound 0
#define texFBDepthBound 0
// The engine's solid_noise, the texture its own water samples here, as the handle the CxEngine block carries.
#ifdef CX_BINDLESS
// a null handle is a GPU fault on AMD's driver, so a program bound before its handle went up samples the diffuse's
#define texUnitNoise (sampler2D(uConNoise != uvec2(0u) ? uConNoise : uTexH[vDraw << 3]))
#define texNoiseBound (uConNoise != uvec2(0u) ? 1 : 0)
#else
#define texUnitNoise uTex
#define texNoiseBound 0
#endif

// ---- the material and draw terms, off the record
#define materialFrontAmbient (vec4(uDraw[vDraw].amb.rgb, 1.0))
#define materialFrontDiffuse (vec4(uDraw[vDraw].dif.rgb, uDraw[vDraw].rot.w))
#define materialFrontSpecular (vec4(uDraw[vDraw].spc.rgb, 1.0))
#define materialFrontEmissive (vec4(uDraw[vDraw].emi.rgb, 1.0))
#define materialFrontShininess 0.0
#define frontLightModelProductSceneColor (vec4(0.0, 0.0, 0.0, 1.0))
#define globalColor COLOR_WHITE
#define fAlphaDiscardValue uAlphaCut
#define blendMode 0
#define subvariant 0
#define skinmesh (CxSkinned(uDraw[vDraw].feat) ? 1 : 0)
#define keyholeCanDissolve (int(uDraw[vDraw].rot.z))
#define keyholeDiameterMin (uKeyholeDia.x)
#define keyholeDiameterMax (uKeyholeDia.y)
#define keyholeMinZOffset (uKeyholeDia.z)
#define bboxMin (vec3(0.0))
#define bboxMax (vec3(0.0))
#define DisplacementOffset 0.0

// ---- lighting: Alloy lights the surface after the body. A stage that reads the engine's per-light arrays itself (its
// own lighting) gets them filled from the lights binned at this fragment, in the engine's gamma-correcting layout:
// the position camera-centred, the colour linear with the squared reach in .a, negative for an ambient light.
#define numLights cxConNumLights
#define lightAreaAmbient uAreaAmbient
#define lightAreaDiffuse uAreaDiffuse
#define lightAreaDiffuseDirection uAreaDir
#define staticLighting 0
#define FragmentLighting 1
#define GammaCorrection 1
#define lightCutoffRangeMultiplier 1.0
#define lightFalloffFactor (uAtten.y)
#define lightMaxIntensityInv (uAtten.x)
#define lightPosition cxConLightPos
#define lightColor cxConLightCol
#define lightDiffuse cxConLightCol
#define lightAmbient cxConLightAmb
#define lightQuadraticAtten cxConLightAtt
int cxConNumLights;
vec4 cxConLightPos[MAX_NUM_LIGHTS];
vec4 cxConLightCol[MAX_NUM_LIGHTS];
vec4 cxConLightAmb[MAX_NUM_LIGHTS];
float cxConLightAtt[MAX_NUM_LIGHTS];
void CxConFillLights()
{
    // a depth or caster twin draws outside the lit bracket -- the caster's points 8..10 hold the gather's buffers,
    // not the lights -- so its stage sees none
#ifdef CX_DEPTH_ONLY
    cxConNumLights = 0;
    return;
#endif
    uint cl = CxCluster(vDepth);
    uint n = min(CxClusterWalk(cl), uint(MAX_NUM_LIGHTS));
    cxConNumLights = int(n);
    for (uint k = 0u; k < n; k++)
    {
        uint li = uClIdx[cl * CX_CLUSTER_CAP + k];
        vec4 pr = uLights[li * 2u], cs = uLights[li * 2u + 1u];
        cxConLightPos[k] = vec4(pr.xyz - uEyePos, 1.0);
        cxConLightCol[k] = vec4(CxLinear(cs.rgb), (CxLightSlot(int(cs.w)) == -2 ? -1.0 : 1.0) * pr.w * pr.w);
        cxConLightAmb[k] = vec4(0.0);
        cxConLightAtt[k] = 0.0;
    }
}

// ---- fog, camera, scene. A constant stand-in carries the type the engine declares the uniform with: content
// passes these to built-ins, and in GLSL 4.60 an int where a float was declared is an ambiguous overload
// (the double overloads count) that NVIDIA refuses at link.
#define fogParams uFogParams
// inc_common's shorthands over fogParams
#define fogEnabled (uFogParams.x)
#define fogStart (uFogParams.y)
#define fogEnd (uFogParams.z)
#define fogInvEndMinusStart (uFogParams.w)
#define cameraPosition uCamPos
#define playerCameraDist (uKeyholeCam.x)
#define cameraPitch (uKeyholeCam.y)
#define cameraYaw 0.0
#define cameraViewAngle 0.0
#define cameraDialogState 0
#define playerOrientation (vec3(0.0))
#define playerInCutscene 0
#define pixelParams uPixel
#define screenParams (vec2(1.0) / uPixel.xy)
#define clipParams (CxConClip())
// inc_common's shorthands over those two
#define screenWidth (screenParams.x)
#define screenHeight (screenParams.y)
#define vPixelSize (uPixel.xy)
#define invScreenWidth (uPixel.x)
#define invScreenHeight (uPixel.y)
#define fAspectRatio (uPixel.z)
#define nearClip (clipParams.x)
#define farClip (clipParams.y)
#define fDepthToLinearModifier (clipParams.z)
#define fFOV 0.78539816
#define areaGlobalWind uAreaWind
#define areaFlags uAreaFlags
#define particleSoftness 0.0

// ---- the scene terms the engine hands every program by NAME -- the module calendar, the frame counter, the weather
// kind, the mouse and the wind's point sources (Alloy's own wind), in the one block every environment shares
#include "cx_envblk.glsl"
#define NWAREA_FLAG_INTERIOR 1
#define NWAREA_FLAG_UNDERGROUND 2
#define NWAREA_FLAG_NATURAL 4
#define NWAREA_WEATHER_CLEAR 0
#define NWAREA_WEATHER_RAIN 1
#define NWAREA_WEATHER_SNOW 2
#define projectionSource (vec4(0.0))
#define projectionWorldZClip 0.0

// ---- stock globals a stage may read or write beyond what the lit stage already declares
vec2 cxConUV;
float fEnvMapLevel;
float fOcclusion;

// ---- helpers the macros above use
int CxConBound(int bit) { return (int(uDraw[vDraw].pbr.w) & bit) != 0 ? 1 : 0; }
// (near, far, 1 - near/far) off the projection's own depth terms.
vec3 CxConClip()
{
    float a = m_proj[2][2], b = m_proj[3][2];
    float n = b / (a + 1.0), f = b / a;
    return vec3(n, f, 1.0 - n / f);
}
vec2 CxConSphereUV(vec3 n)
{
    vec3 r = reflect(normalize(vWorld - uEyePos), n);
    float m = 2.0 * sqrt(r.x * r.x + r.y * r.y + (r.z + 1.0) * (r.z + 1.0));
    return r.xy / max(m, 1e-4) + 0.5;
}

// ---- the stock helpers. Colour space: cx_vanenv_f has the vec3 pair; cx_conhelp carries its other overloads.
#include "cx_conhelp.glsl"
// The cut a stock stage makes on the coverage it computed: the batch's own threshold, which is the engine's
// fAlphaDiscardValue (0.2 on the cutout and blended classes, none on the rest).
#ifndef CX_CON_HAS_AlphaDiscard
void AlphaDiscard(float a) { if (uAlphaCut >= 0.0 && a <= uAlphaCut) discard; }
#endif
// What Alloy does itself, after the body: shading, keyholing, fog, the view vector, the specular synthesis.
#ifndef CX_CON_HAS_ComputeLighting
void ComputeLighting(inout vec4 c, vec3 n) { }
#endif
#ifndef CX_CON_HAS_ApplyKeyhole
void ApplyKeyhole(inout vec4 c) { }
#endif
#ifndef CX_CON_HAS_ApplyFog
void ApplyFog(inout vec3 c) { }
void ApplyFog(inout vec4 c) { }
#endif
#ifndef CX_CON_HAS_SetupViewToSurfaceNormal
void SetupViewToSurfaceNormal() { }
#endif
#ifndef CX_CON_HAS_SetupSpecularity
void SetupSpecularity(vec3 c) { }
#endif
#ifndef CX_CON_HAS_SetupOcclusion
void SetupOcclusion() { }
#endif
// ...and the newer generation's split of it, with the two terms it seeds: Alloy's occlusion is its own, so both read
// "unoccluded" to a stage that consults them
float fAmbientOcclusionModifier = 1.0;
float fSurfaceFadeModifier = 1.0;
#ifndef CX_CON_HAS_SetupOcclusionBase
void SetupOcclusionBase() { }
#endif
#ifndef CX_CON_HAS_SetupOcclusionFromTexture
void SetupOcclusionFromTexture(vec2 uv, sampler2D height) { }
#endif
// The tangent frame in the shading frame: the supplied world tangent where the mesh carries one, else one
// built off the normal (the same fallback the lit stage's normal mapping takes without a frame).
#ifndef CX_CON_HAS_SetupTSB
mat3 SetupTSB()
{
    vec3 n = normalize(vNrm);
    vec3 t = vTan.w != 0.0 ? normalize(vTan.xyz - n * dot(n, vTan.xyz))
           : normalize(abs(n.z) < 0.999 ? cross(vec3(0.0, 0.0, 1.0), n) : vec3(1.0, 0.0, 0.0));
    return mat3(t, cross(n, t) * (vTan.w < 0.0 ? -1.0 : 1.0), n);
}
#endif
// Alloy has already offset the UVs for the height map before the body runs. Both generations' signatures.
#ifndef CX_CON_HAS_DisplaceTextureCoords
void DisplaceTextureCoords(inout vec2 uv, mat3 tsb) { }
void DisplaceTextureCoords(inout vec2 uv, sampler2D height, mat3 tsb) { }
#endif
#ifndef CX_CON_HAS_CalculateEnvironmentTexCoordsCube
vec3 CalculateEnvironmentTexCoordsCube(vec3 n) { return reflect(normalize(vWorld - uEyePos), n); }
#endif
#ifndef CX_CON_HAS_SampleEnvironmentMapCube
vec3 SampleEnvironmentMapCube(vec3 dir, float lod) { return textureLod(uProbe, dir, lod).rgb; }
#endif
#ifndef CX_CON_HAS_CalculateEnvironmentTexCoords
vec2 CalculateEnvironmentTexCoords(vec3 n) { return CxConSphereUV(n); }
#endif
#ifndef CX_CON_HAS_SampleEnvironmentMap
vec3 SampleEnvironmentMap(vec2 uv, float lod) { return CX_ENV_GREY_LIN; }
#endif
#ifndef CX_CON_HAS_GetFog
float GetFog(float d) { return 0.0; }
#endif
// Linear view depth off the depth buffer the engine binds as texFBDepth -- only ever bound for its framebuffer
// effects, so on a world part this reads whatever the scene depth holds, as it does there.
#ifndef CX_CON_HAS_GetDepth
float GetDepth(vec2 uv) { return nearClip / max(1.0 - CxGlDepth(uSceneDepth, uv).x * fDepthToLinearModifier, 1e-6); }
#endif
// The fragment's own view z, which the newer generation rebuilds from gl_FragCoord: Alloy carries it exactly.
#ifndef CX_CON_HAS_GetFragmentViewZ
float GetFragmentViewZ() { return -vDepth; }
#endif
#ifndef CX_CON_HAS_GetViewZFromDepth
float GetViewZFromDepth(vec2 uv) { return -GetDepth(uv); }
#endif
