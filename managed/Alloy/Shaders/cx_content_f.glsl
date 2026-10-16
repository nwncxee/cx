// CX-303: the environment a CONTENT fragment stage compiles in -- any `customShaderFS` a `.mtr` names that Alloy
// does not reproduce or host by name. ContentStage splices this ahead of the stage's own text, inside its own
// program (one per stage), and #undefs every macro here after it, so nothing below reaches Alloy's own code.
//
// The stage runs as a SURFACE MODIFIER, the way the four named hosted stages do: stock setup has already run
// (FragmentColor is the texel, vFragmentNormal the shading normal, the material globals synthesised), the body
// edits them, and Alloy lights what it leaves. Its ApplyStandardShader is the stand-in's no-op.
//
// The engine's vocabulary (inc_common, inc_uniforms, the GLRender prologue) comes in three kinds here: a name
// mapped onto the Alloy value that IS it, a name with no Alloy source held at the value it has on a world part
// the engine draws with nothing set, and a stock helper function. Every helper is guarded, so a stage that
// defines its own copy (the flowmaps inline SetupTSB and friends) keeps it: the translator defines
// CX_CON_HAS_<name> for each function the stage defines.
//
// "View space" is CAMERA-CENTRED WORLD space: world axes, origin at the eye. The named hosted stages already
// read normals and directions as world-oriented (m_view's rotation is the identity, cx_vanenv_f), and content
// reads positions both ways -- normalize(vPosView) as the view direction (fs_snowed01) and
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
// The one custom varying content ships: every vertex stage that writes it (shd_roofkn/knm/ky, shd_mixlitekn/ky)
// writes (m_m * vPos).z, the fragment's world height.
#define fVertexHeight (vWorld.z)

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
// The engine's solid_noise, the texture its own water samples here, as a handle the program is handed per bind.
#ifdef CX_BINDLESS
uniform uvec2 uConNoise;
#define texUnitNoise (sampler2D(uConNoise))
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
#define keyholeMinZOffset 0.0
#define bboxMin (vec3(0.0))
#define bboxMax (vec3(0.0))
#define DisplacementOffset 0.0

// ---- lighting: Alloy lights the surface after the body, so the engine's per-light arrays are empty here
#define numLights 0
#define lightAreaAmbient uAreaAmbient
#define lightAreaDiffuse uAreaDiffuse
#define lightAreaDiffuseDirection uAreaDir
#define staticLighting 0
#define FragmentLighting 1
#define GammaCorrection 1
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

// ---- fog, camera, scene. A constant stand-in carries the type the engine declares the uniform with: content
// passes these to built-ins, and in GLSL 4.60 an int where a float was declared is an ambiguous overload
// (the double overloads count) that NVIDIA refuses at link.
#define fogParams uFogParams
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
#define areaWeatherType 0
#define areaWeatherDensity 0.0
#define sceneCurrentFrame 0
#define moduleYear 0
#define moduleMonth 0
#define moduleDay 0
#define moduleHour 0
#define moduleDawnHour 0
#define moduleDuskHour 0
#define moduleMinutesPerHour 0
#define moduleTimeIntoTransition 0.0
#define moduleTransitionTime 0.0
#define userinputMousePosition (ivec2(0))
#define userinputMouseButtons 0
#define windPointSourcesCount 0
#define particleSoftness 0.0
#define projectionSource (vec4(0.0))
#define projectionWorldZClip 0.0

// ---- stock globals a stage may read or write beyond what the lit stage already declares
vec2 cxConUV;
float fEnvMapLevel;
float fOcclusion;

// ---- helpers the macros above use
mat4 CxConView(vec3 t) { return mat4(1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, t.x, t.y, t.z, 1.0); }
// The lit vertex stage turns the UVs by (cos, sin) = d.rot.xy; this is that turn as the engine's texture matrix.
mat4 CxConTexMat(vec2 cs) { return mat4(cs.x, cs.y, 0.0, 0.0, -cs.y, cs.x, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0); }
int CxConBound(int bit) { return (int(uDraw[vDraw].pbr.w) & bit) != 0 ? 1 : 0; }
// (near, far, 1 - near/far) off the projection's own depth terms.
vec3 CxConClip()
{
    float a = m_proj[2][2], b = m_proj[3][2];
    float n = b / (a - 1.0), f = b / (a + 1.0);
    return vec3(n, f, 1.0 - n / f);
}
vec2 CxConSphereUV(vec3 n)
{
    vec3 r = reflect(normalize(vWorld - uEyePos), n);
    float m = 2.0 * sqrt(r.x * r.x + r.y * r.y + (r.z + 1.0) * (r.z + 1.0));
    return r.xy / max(m, 1e-4) + 0.5;
}

// ---- the stock helpers. Colour space: cx_vanenv_f has the vec3 pair; these are its other overloads.
#ifndef CX_CON_HAS_ApplyColorSpace
float ApplyColorSpace(float c) { return ApplyColorSpace(vec3(c)).x; }
vec4 ApplyColorSpace(vec4 c) { return vec4(ApplyColorSpace(c.rgb), c.a); }
#endif
#ifndef CX_CON_HAS_RevertColorSpace
float RevertColorSpace(float c) { return RevertColorSpace(vec3(c)).x; }
vec4 RevertColorSpace(vec4 c) { return vec4(RevertColorSpace(c.rgb), c.a); }
#endif
#ifndef CX_CON_HAS_ColorToBrightness
float ColorToBrightness(vec3 c) { return max(c.r, max(c.g, c.b)); }
float ColorToBrightness(vec4 c) { return max(c.r, max(c.g, c.b)); }
#endif
#ifndef CX_CON_HAS_ColorToLuma
float ColorToLuma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }
float ColorToLuma(vec4 c) { return dot(c.rgb, vec3(0.299, 0.587, 0.114)); }
#endif
#ifndef CX_CON_HAS_ColorClamp
vec3 ColorClamp(vec3 c) { return clamp(c, 0.0, 1.0); }
vec4 ColorClamp(vec4 c) { return clamp(c, 0.0, 1.0); }
#endif
#ifndef CX_CON_HAS_sqr
float sqr(float a) { return a * a; }
#endif
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
float GetDepth(vec2 uv) { return nearClip / max(1.0 - texture(uSceneDepth, uv).x * fDepthToLinearModifier, 1e-6); }
#endif
// The fragment's own view z, which the newer generation rebuilds from gl_FragCoord: Alloy carries it exactly.
#ifndef CX_CON_HAS_GetFragmentViewZ
float GetFragmentViewZ() { return -vDepth; }
#endif
#ifndef CX_CON_HAS_GetViewZFromDepth
float GetViewZFromDepth(vec2 uv) { return -GetDepth(uv); }
#endif
// Hash and noise helpers. The hashes are inc_common's own formulas (a sine hash, interleaved gradient noise); the
// noises are a gradient noise of Alloy's own that answers in the same [-1, 1] range under the stock names, so a
// stage calling them compiles and varies smoothly -- the exact pattern is not the engine's.
#ifndef CX_CON_HAS_Random
float Random(vec2 v) { return fract(sin(dot(v, vec2(12.9898, 78.233))) * 43758.5453); }
float Random(vec3 v) { return fract(sin(dot(v, vec3(12.9898, 78.233, 10.1523))) * 43758.5453); }
#endif
#ifndef CX_CON_HAS_InterleavedGradientNoise
float InterleavedGradientNoise(vec2 p) { return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715)))); }
#endif
vec2 CxConGrad2(vec2 c) { float a = 6.2831853 * fract(sin(dot(c, vec2(127.1, 311.7))) * 43758.5453); return vec2(cos(a), sin(a)); }
vec3 CxConGrad3(vec3 c)
{
    vec2 h = fract(sin(vec2(dot(c, vec3(127.1, 311.7, 74.7)), dot(c, vec3(269.5, 183.3, 246.1)))) * 43758.5453);
    float z = h.x * 2.0 - 1.0, a = 6.2831853 * h.y, r = sqrt(max(1.0 - z * z, 0.0));
    return vec3(r * cos(a), r * sin(a), z);
}
#ifndef CX_CON_HAS_NoiseSimplex2D
float NoiseSimplex2D(vec2 p)
{
    vec2 i = floor(p), f = fract(p), u = f * f * (3.0 - 2.0 * f);
    float a = dot(CxConGrad2(i), f), b = dot(CxConGrad2(i + vec2(1.0, 0.0)), f - vec2(1.0, 0.0));
    float c = dot(CxConGrad2(i + vec2(0.0, 1.0)), f - vec2(0.0, 1.0)), d = dot(CxConGrad2(i + 1.0), f - 1.0);
    return 1.4142 * mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}
#endif
#ifndef CX_CON_HAS_NoiseSimplex3D
float NoiseSimplex3D(vec3 p)
{
    vec3 i = floor(p), f = fract(p), u = f * f * (3.0 - 2.0 * f);
    float n000 = dot(CxConGrad3(i), f), n100 = dot(CxConGrad3(i + vec3(1, 0, 0)), f - vec3(1, 0, 0));
    float n010 = dot(CxConGrad3(i + vec3(0, 1, 0)), f - vec3(0, 1, 0)), n110 = dot(CxConGrad3(i + vec3(1, 1, 0)), f - vec3(1, 1, 0));
    float n001 = dot(CxConGrad3(i + vec3(0, 0, 1)), f - vec3(0, 0, 1)), n101 = dot(CxConGrad3(i + vec3(1, 0, 1)), f - vec3(1, 0, 1));
    float n011 = dot(CxConGrad3(i + vec3(0, 1, 1)), f - vec3(0, 1, 1)), n111 = dot(CxConGrad3(i + 1.0), f - 1.0);
    return 1.1547 * mix(mix(mix(n000, n100, u.x), mix(n010, n110, u.x), u.y),
                        mix(mix(n001, n101, u.x), mix(n011, n111, u.x), u.y), u.z);
}
#endif
