// CX-303: the environment a CONTENT vertex stage compiles in -- the `customShaderVS` of a .mtr whose pair Alloy runs
// translated. ContentStage splices this ahead of the stage's own text inside the lit vertex stage's host region, and
// #undefs every macro here after it, so nothing below reaches Alloy's own code.
//
// The stage runs after Alloy's own vertex work, with the stock outputs SEEDED from it: a stage that leaves one alone
// (a fragment-only fork's stock vertex half) hands Alloy's own value back. Its outputs are read back into the lit
// stage's varyings -- gl_Position, the camera-centred position, the normal, the tangent frame, the texture coords --
// and a varying of its own reaches the fragment stage by name, as the engine's link does.
//
// "View space" is CAMERA-CENTRED WORLD space, as in cx_content_f: world axes, origin at the eye. So m_mv is the model
// matrix moved by the eye, m_proj the view-projection moved back, and their product the clip position Alloy draws.

// ---- the prologue GLRender::LoadAndCompileSingleShader (@0x4d34b0) puts ahead of every content stage
#define mediump
#define lowp
#define highp
#define MAX_NUM_LIGHTS 32
#define MAX_NUM_BONES 64
#define GAMMA_CORRECTION 1
#define FRAGMENT_LIGHTING 1
#define SHADER_QUALITY_MODE 2
#define KEYHOLING_ENABLED 1
#define SHADER_DEBUG_MODE 0
#define NO_DISCARD 0
#define POSTPROCESSING_TYPES_ENABLED 0
#define varying out
#define attribute in
#define texture2D texture
#define textureCube texture
#define PI 3.14159265359
#define COLOR_WHITE vec4(1.0, 1.0, 1.0, 1.0)
#define COLOR_BLACK vec4(0.0, 0.0, 0.0, 1.0)
#define COLOR_GREY vec4(0.5, 0.5, 0.5, 1.0)
#define COLOR_RED vec4(1.0, 0.0, 0.0, 1.0)
#define COLOR_GREEN vec4(0.0, 1.0, 0.0, 1.0)
#define COLOR_BLUE vec4(0.0, 0.0, 1.0, 1.0)

#include "cx_keyfogdecl.glsl"
#include "cx_pass.glsl"
#ifdef CX_BINDLESS
layout(std430, binding = 3) readonly buffer CxTexH { uvec2 uTexH[]; };
#endif
#include "cx_conbuf.glsl"

// ---- what the host region sets before the stage runs: the draw's record and slot, its bone base (-1 unposed), and
// the derived matrices the macros below name (the row is cx_conbuf's cxConRow)
CxDraw cxConD;
int cxConDraw;
int cxConBone;
mat4 cxConMv, cxConMvp, cxConProj;

// ---- the engine's vertex attributes (GLRender::ConnectShaderData @0x4ce8e0 binds them by name) off the arena's
#define vPos (vec4(aPos, 1.0))
#define vTcIn aUV
#define vNormal aNrm
#define vTangent (aTangent.xyz)
#define fHandedness (aTangent.w)
#define vColor aPaint
#define vIndex aBoneIdx
#define vWeight aBoneWeight
// The arena carries one UV set: the others read it, which is the set every stock model fills.
#define vTcIn1 aUV
#define vTcIn2 aUV
#define vTcIn3 aUV
// No arena stream: the engine's own values for a world part that sets none.
#define vCustomColor COLOR_WHITE
#define vStaticLightDir (vec3(0.0))
#define fProjectionFrontal 0.0

// ---- the stock outputs, as plain globals: seeded from Alloy's own before the stage runs, read back after it
vec3 vPosView;
vec3 vVertexNormal;
vec3 vVertexTangent;
float fTextureHandedness;
vec2 vVertexTexCoords;
vec3 vPosWorld;
float fFogFragCoord;
float fFragCamOffset;
vec4 VertexColor;
vec3 VertexStaticLighting;
vec3 vStaticLightDirOut;
vec3 vVertexTexCoordsEnvironmentCube;
vec2 vVertexTexCoordsEnvironment;
vec3 vViewToSurface_n;

// ---- the engine's own projection, as the fragment environment reads it (cx_vanenv_f): only its depth terms, the
// clip planes inc_common's clipParams carries, come off it here -- the m_proj a stage names is the frame's below
uniform mat4 m_proj;
vec3 CxConClipV()
{
    float a = m_proj[2][2], b = m_proj[3][2];
    float n = b / (a - 1.0), f = b / (a + 1.0);
    return vec3(n, f, 1.0 - n / f);
}
#define clipParams (CxConClipV())
#define nearClip (clipParams.x)
#define farClip (clipParams.y)
#define fDepthToLinearModifier (clipParams.z)

// ---- matrices, in the camera-centred frame
#undef m_view
#undef m_view_inv
#undef m_proj
#define m_m (cxConD.model)
#define m_view (CxConView(-uEyePos))
#define m_view_inv (CxConView(uEyePos))
#define m_mv cxConMv
#define m_mvp cxConMvp
#define m_proj cxConProj
#define m_proj_inv (inverse(cxConProj))
#define m_vp cxVP
#define m_vp_inv (inverse(cxVP))
#define m_normal (mat3(cxConD.model))
#define m_texture (CxConTexMat(cxConD.rot.xy))
// m_bones[i] is rewritten to CxConBoneAt(i) by the translator: the palette is the draw's slice of the bone buffer
#define skinmesh (cxConBone >= 0 ? 1 : 0)

// ---- the material and draw terms, off the record
#define materialFrontAmbient (vec4(cxConD.amb.rgb, 1.0))
#define materialFrontDiffuse (vec4(cxConD.dif.rgb, cxConD.rot.w))
#define materialFrontSpecular (vec4(cxConD.spc.rgb, 1.0))
#define materialFrontEmissive (vec4(cxConD.emi.rgb, 1.0))
#define materialFrontShininess 0.0
#define frontLightModelProductSceneColor (vec4(0.0, 0.0, 0.0, 1.0))
#define globalColor COLOR_WHITE
#define fAlphaDiscardValue 0.2
#define blendMode 0
#define subvariant 0
#define keyholeCanDissolve (int(cxConD.rot.z))
#define keyholeDiameterMin (uKeyholeDia.x)
#define keyholeDiameterMax (uKeyholeDia.y)
#define keyholeMinZOffset 0.0
#define bboxMin (vec3(0.0))
#define bboxMax (vec3(0.0))
// the material units: GLRender::ConnectShaderData @0x4ce8e0 pins texUnitN to unit N for the PROGRAM, so a vertex
// stage samples them too -- off the record's handles under bindless, else the fragment stage's own sampler uniforms
#ifdef CX_BINDLESS
#define CxConUnit(k) sampler2D(uTexH[(cxConDraw << 3) + (k)])
#define texUnit0 CxConUnit(0)
#define texUnit1 CxConUnit(1)
#define texUnit2 CxConUnit(2)
#define texUnit3 CxConUnit(3)
#define texUnit4 CxConUnit(4)
#define texUnit5 CxConUnit(5)
#define texUnit6 CxConUnit(6)
#else
uniform sampler2D uTex, uNrmTex, uSpecTex, uRoughTex, uHeightTex, uIllumTex, uFlowTex;
#define texUnit0 uTex
#define texUnit1 uNrmTex
#define texUnit2 uSpecTex
#define texUnit3 uRoughTex
#define texUnit4 uHeightTex
#define texUnit5 uIllumTex
#define texUnit6 uFlowTex
#endif
#define texUnit7  CxConPal(0)
#define texUnit8  CxConPal(1)
#define texUnit9  CxConPal(2)
#define texUnit10 CxConPal(3)
// the record's slot mask carries bit slot-1 per bound material slot (AlloyRenderer's 1 << (slot - 1))
#define CxConVBound(slot) ((int(cxConD.pbr.w) & (1 << ((slot) - 1))) != 0 ? 1 : 0)
#define texture0Bound 1
#define texture1Bound CxConVBound(1)
#define texture2Bound CxConVBound(2)
#define texture3Bound CxConVBound(3)
#define texture4Bound CxConVBound(4)
#define texture5Bound CxConVBound(5)
#define texture6Bound CxConVBound(6)
#define texture7Bound  CxConPalBound(cxConDraw, 0)
#define texture8Bound  CxConPalBound(cxConDraw, 1)
#define texture9Bound  CxConPalBound(cxConDraw, 2)
#define texture10Bound CxConPalBound(cxConDraw, 3)
#define texNoiseBound 0
#define envMapCube 1
#define texEnvCubeBound 0
#define texEnvBound 0

// ---- scene, camera and lighting terms (cx_content_f's, minus the per-fragment ones)
#define fogParams uFogParams
// inc_common's shorthands over fogParams
#define fogEnabled (uFogParams.x)
#define fogStart (uFogParams.y)
#define fogEnd (uFogParams.z)
#define fogInvEndMinusStart (uFogParams.w)
#define cameraPosition uCamPos
#define cameraFocus uCamFocus
#define playerPosition uPlayerPos
#define playerCameraDist (uKeyholeCam.x)
#define cameraPitch (uKeyholeCam.y)
#define cameraYaw 0.0
#define cameraViewAngle 0.0
#define cameraDialogState 0
#define playerOrientation (vec3(0.0))
#define playerInCutscene 0
#define pixelParams uPixel
#define screenParams (vec2(1.0) / uPixel.xy)
#define screenWidth (screenParams.x)
#define screenHeight (screenParams.y)
#define vPixelSize (uPixel.xy)
#define invScreenWidth (uPixel.x)
#define invScreenHeight (uPixel.y)
#define fAspectRatio (uPixel.z)
#define fFOV 0.78539816
#define worldtimerTimeOfDay uTimeOfDay
#define areaGlobalWind uAreaWind
#define areaFlags uAreaFlags
#define particleSoftness 0.0

// ---- the scene terms the engine hands every program by NAME (AlloyRenderer.EngineNamed uploads them): the module
// calendar, the frame counter, the weather kind, the mouse, and the wind's point sources (Alloy's own wind)
uniform int moduleYear, moduleMonth, moduleDay, moduleHour, moduleDawnHour, moduleDuskHour, moduleMinutesPerHour;
uniform float moduleTimeIntoTransition, moduleTransitionTime;
uniform int sceneCurrentFrame;
uniform int areaWeatherType;
uniform float areaWeatherDensity;
uniform ivec2 userinputMousePosition;
uniform int userinputMouseButtons;
const int MAX_WINDS = 128;
uniform int windPointSourcesCount;
uniform vec3 windPointSourcesPosition[MAX_WINDS];
uniform float windPointSourcesRadius[MAX_WINDS], windPointSourcesDuration[MAX_WINDS];
uniform float windPointSourcesIntensity[MAX_WINDS], windPointSourcesTimeRemaining[MAX_WINDS];
#define NWAREA_FLAG_INTERIOR 1
#define NWAREA_FLAG_UNDERGROUND 2
#define NWAREA_FLAG_NATURAL 4
#define NWAREA_WEATHER_CLEAR 0
#define NWAREA_WEATHER_RAIN 1
#define NWAREA_WEATHER_SNOW 2
#define projectionSource (vec4(0.0))
#define projectionWorldZClip 0.0
#define numLights 0
#define staticLighting 0
#define FragmentLighting 1
#define GammaCorrection 1
#define lightAreaAmbient uAreaAmbient
#define lightAreaDiffuse uAreaDiffuse
#define lightAreaDiffuseDirection uAreaDir
#define lightCutoffRangeMultiplier 1.0
#define lightFalloffFactor (uAtten.y)
#define lightMaxIntensityInv (uAtten.x)
#define lightAmbient cxConLight4
#define lightDiffuse cxConLight4
#define lightPosition cxConLight4
#define lightColor cxConLight4
#define lightQuadraticAtten cxConLight1
vec4 cxConLight4[MAX_NUM_LIGHTS];
float cxConLight1[MAX_NUM_LIGHTS];

// ---- inc_common's colour-space pair, the curve cx_vanenv_f carries for a fragment stage
#ifndef CX_CON_HAS_ApplyColorSpace
vec3 ApplyColorSpace(vec3 c) { return sign(c) * pow(abs(c), vec3($SRGB$)); }
#endif
#ifndef CX_CON_HAS_RevertColorSpace
vec3 RevertColorSpace(vec3 c) { return sign(c) * pow(abs(c), vec3(1.0 / $SRGB$)); }
#endif
#include "cx_conhelp.glsl"

// ---- the stock vertex helpers
mat4 CxConBoneAt(int i) { return cxConBone >= 0 ? uBone[cxConBone + i] : mat4(1.0); }
#ifndef CX_CON_HAS_SkinmeshTransformPosition
vec4 SkinmeshTransformPosition(vec4 p, int i) { return CxConBoneAt(i) * p; }
#endif
#ifndef CX_CON_HAS_SkinmeshTransformOrientation
vec3 SkinmeshTransformOrientation(vec3 v, int i) { return mat3(CxConBoneAt(i)) * v; }
#endif
// inc_common's transform: the mesh position posed by up to four bones (when the draw is posed), then into view and
// clip space; the normal and tangent follow the pose and the model rotation; the texture coords take the matrix.
#ifndef CX_CON_HAS_TransformMainCoordinates
void TransformMainCoordinates()
{
    vec4 p = vPos;
    vec3 n = vNormal, t = vTangent;
    if (skinmesh == 1)
    {
        p = vec4(0.0); n = vec3(0.0); t = vec3(0.0);
        for (int k = 0; k < 4; k++)
        {
            float w = vWeight[k];
            if (w <= 0.0) continue;
            int b = int(vIndex[k]);
            p += SkinmeshTransformPosition(vPos, b) * w;
            n += SkinmeshTransformOrientation(vNormal, b) * w;
            t += SkinmeshTransformOrientation(vTangent, b) * w;
        }
    }
    vPosView = (m_mv * p).xyz;
    gl_Position = m_proj * vec4(vPosView, 1.0);
    vVertexNormal = mat3(m_mv) * n;
    vVertexTangent = mat3(m_mv) * t;
    fTextureHandedness = fHandedness;
    vPosWorld = vPosView + uEyePos;
    vVertexTexCoords = mat2(m_texture) * vTcIn;
}
#endif
#ifndef CX_CON_HAS_SetupKeyholeFactors
void SetupKeyholeFactors()
{
    fFragCamOffset = keyholeCanDissolve == 1 ? -vPosView.z - playerCameraDist + (90.0 - cameraPitch) / 45.0 : 0.0;
}
#endif
#ifndef CX_CON_HAS_SetupViewToSurfaceNormal
void SetupViewToSurfaceNormal() { vViewToSurface_n = normalize(vPosView); }
#endif
#ifndef CX_CON_HAS_GetFog
float GetFog(float d) { return uFogParams.x == 0.0 ? 0.0 : (d - uFogParams.y) * uFogParams.w; }
#endif
#ifndef CX_CON_HAS_SetupFog
void SetupFog() { fFogFragCoord = GetFog(length(vPosView)); }
#endif
// What Alloy does itself, in the fragment stage: the lights, the specular synthesis, the environment coordinates.
#ifndef CX_CON_HAS_SetupLights
void SetupLights() { }
#endif
#ifndef CX_CON_HAS_ComputeLighting
void ComputeLighting(inout vec4 c, vec3 n) { }
#endif
#ifndef CX_CON_HAS_SetupSpecularity
void SetupSpecularity(vec3 c) { }
#endif
#ifndef CX_CON_HAS_CalculateEnvironmentTexCoordsCube
vec3 CalculateEnvironmentTexCoordsCube(vec3 n) { return reflect(normalize(vPosView), n); }
#endif
#ifndef CX_CON_HAS_CalculateEnvironmentTexCoords
vec2 CalculateEnvironmentTexCoords(vec3 n)
{
    vec2 r = n.xy / (1.0 + abs(n.z)) + 0.5 * (vPosView.xy / (-vPosView.z + 0.2));
    return 0.5 * r + vec2(0.5, 0.25);
}
#endif
