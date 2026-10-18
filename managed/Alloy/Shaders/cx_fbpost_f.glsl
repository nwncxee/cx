// #352: the engine's post program, hosted: the resource chain's fsfbpostpr run WHOLE, its main() renamed --
// stock's is inc_postpr's FS_PostProcess(), a content one may draw over it (spell telegraphs off the scriptable
// uniforms). What is ahead of the region is the ENVIRONMENT: the names inc_common/inc_uniforms give a fragment
// post stage, from Alloy's values. The stub is a pass-through.
// CX-93: $POSTMASK$ is 0 by construction (FbPostStage.MaskStripped), so every `#if
// POSTPROCESSING_TYPES_ENABLED & ...` include in whatever the chain answers with preprocesses away and
// no stock post shader compiles here. Sharpen and vibrance are Alloy's own stages ahead of this one; what
// this region is for is the CONTENT's own passes, which is the only reason the stage exists.
#define CX_ALLOY 1
#define SHADER_TYPE 2
#define GAMMA_CORRECTION 1
#define SHADER_QUALITY_MODE $SQM$
#define POSTPROCESSING_TYPES_ENABLED $POSTMASK$
#define texture2D texture
#define textureCube texture
#define vVertexTexCoords vUV
// texFBColor / texFBDepth are the chain's OWN declarations (inc_framebuffer); Prepare binds them to
// the stack's units by name, so no macro stands between the two and no line has to be stripped.
#ifndef PI
#define PI 3.14159265359
#endif
uniform highp vec3 pixelParams;
#define vPixelSize (pixelParams.xy)
#define invScreenWidth (pixelParams.x)
#define invScreenHeight (pixelParams.y)
#define fAspectRatio (pixelParams.z)
uniform highp vec3 clipParams;
#define nearClip (clipParams.x)
#define farClip (clipParams.y)
#define fDepthToLinearModifier (clipParams.z)
uniform highp mat4 m_view;
uniform highp mat4 m_view_inv;
// 1 while a sky twin's lens (cx_sky_lens) is in this frame: a content flare of its own can stand aside for it.
uniform int cxSkyLens;
uniform highp mat4 m_proj;
// inc_uniforms, which the bridge never follows, under the engine's names: the feature terms
// (AlloyRenderer.UploadFeatureTerms) and the terms the engine hands every program by name (AlloyRenderer.EngineNamed)
uniform int uTimeOfDay;
#define worldtimerTimeOfDay uTimeOfDay
uniform int sceneCurrentFrame;
uniform int moduleYear, moduleMonth, moduleDay, moduleHour, moduleDawnHour, moduleDuskHour, moduleMinutesPerHour;
uniform highp vec3 uPlayerPos, uCamPos, uCamFocus;
uniform vec2 uKeyholeCam;
#define playerPosition uPlayerPos
#define playerCameraDist (uKeyholeCam.x)
#define playerInCutscene 0
#define cameraPosition uCamPos
#define cameraPitch (uKeyholeCam.y)
#define cameraYaw 0.0
#define cameraFocus uCamFocus
#define cameraViewAngle 0.0
#define cameraDialogState 0
const int MAX_WINDS = 128;
uniform int windPointSourcesCount;
uniform vec3 windPointSourcesPosition[MAX_WINDS];
uniform float windPointSourcesRadius[MAX_WINDS], windPointSourcesDuration[MAX_WINDS];
uniform float windPointSourcesIntensity[MAX_WINDS], windPointSourcesTimeRemaining[MAX_WINDS];
uniform vec3 uAreaWind;
#define areaGlobalWind uAreaWind
uniform float areaWeatherDensity;
#define NWAREA_FLAG_INTERIOR 1
#define NWAREA_FLAG_UNDERGROUND 2
#define NWAREA_FLAG_NATURAL 4
uniform int uAreaFlags;
#define areaFlags uAreaFlags
#define NWAREA_WEATHER_CLEAR 0
#define NWAREA_WEATHER_RAIN 1
#define NWAREA_WEATHER_SNOW 2
uniform int areaWeatherType;
#define bboxMin (vec3(0.0))
#define bboxMax (vec3(0.0))
// inc_common's colour constants and inc_fog's terms, which the bridge never follows either -- the current
// inc_postpr's passes read both. fogColor is display space in stock; Alloy uploads it linear.
#define COLOR_BLACK vec4(0.0, 0.0, 0.0, 1.0)
#define COLOR_WHITE vec4(1.0, 1.0, 1.0, 1.0)
#define COLOR_GREY vec4(0.5, 0.5, 0.5, 1.0)
#define COLOR_RED vec4(1.0, 0.0, 0.0, 1.0)
#define COLOR_GREEN vec4(0.0, 1.0, 0.0, 1.0)
#define COLOR_BLUE vec4(0.0, 0.0, 1.0, 1.0)
#define COLOR_MAGENTA vec4(1.0, 0.0, 1.0, 1.0)
#define COLOR_YELLOW vec4(1.0, 1.0, 0.0, 1.0)
#define COLOR_CYAN vec4(0.0, 1.0, 1.0, 1.0)
uniform vec4 uFogParams, uFogColor;
#define fogParams uFogParams
#define fogEnabled (uFogParams.x)
#define fogStart (uFogParams.y)
#define fogEnd (uFogParams.z)
#define fogInvEndMinusStart (uFogParams.w)
#define fogColor (vec4(pow(max(uFogColor.rgb, vec3(0.0)), vec3(1.0 / $SRGB$)), uFogColor.a))
float sqr(float a) { return a * a; }
float ColorToLuma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }
float ColorToLuma(vec4 c) { return ColorToLuma(c.rgb); }

vec4 FragmentColor;

// $VANILLA-SKIP inc_lighting inc_fog inc_shadow
// $VANILLA-MAIN fsfbpostpr
void CxVanMain() { FragmentColor = texture(uSrc, vUV); }
// $VANILLA-END

vec4 CxStage(vec4 c)
{
    CxVanMain();
    return vec4(FragmentColor.rgb, c.a);
}
