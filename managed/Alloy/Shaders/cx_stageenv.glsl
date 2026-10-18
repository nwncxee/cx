// The environment a WHOLE content stage runs in -- the sky's fragment stage (cx_sky_fs) and a native stage's compute
// passes (cx_pass_cs): the names inc_common/inc_uniforms give a stock stage, from Alloy's values under the ENGINE's
// names, so stock text drops in unedited. The cx frame uniforms are not here: a stage declares the ones it reads
// (docs/shader-overrides.md, "The frame"). Needs cx_featdecl and cx_fogdecl ahead of it.
#ifndef CX_STAGEENV_LIB
#define CX_STAGEENV_LIB
#define CX_ALLOY 1
#define GAMMA_CORRECTION 1
#define texture2D texture
#define textureCube texture
#ifndef PI
#define PI 3.14159265359
#endif
#define COLOR_WHITE vec4(1.0, 1.0, 1.0, 1.0)
#define playerPosition uPlayerPos
#define cameraPosition uCamPos
#define cameraFocus uCamFocus
#define worldtimerTimeOfDay uTimeOfDay
#define areaGlobalWind uAreaWind
#define areaFlags uAreaFlags
// The area's fog colour as the engine's stages see it: DISPLAY space (Alloy linearises at upload, #132).
#define fogColor vec4(pow(max(uFogColor.rgb, vec3(0.0)), vec3(1.0 / $SRGB$)), 1.0)
uniform mat4 m_proj;             // inc_noir's ortho-vs-perspective test reads [3][3]
// The engine's calendar and weather scalars, from m_shaderExposedRuntimeData: the same values
// GLRender::SetUniforms @0x4ccbf0 feeds the engine's own copy of a stage, under the names
// GLRender::ConnectShaderData @0x4ce8e0 binds them to. Macros rather than declarations under those names,
// so a content stage that declares one for itself (inc_sunm does, for moduleTimeIntoTransition) has its
// line dropped instead of colliding.
uniform int uModYear, uModMonth, uModDay, uModHour, uModDawn, uModDusk;
uniform float uModTransition, uWeatherDensity;
#define moduleYear uModYear
#define moduleMonth uModMonth
#define moduleDay uModDay
#define moduleHour uModHour
#define moduleDawnHour uModDawn
#define moduleDuskHour uModDusk
#define moduleTimeIntoTransition uModTransition
#define areaWeatherDensity uWeatherDensity

// inc_common's colour-space pair and its exact curve: ApplyColorSpace goes display -> WORKING space.
vec3 ApplyColorSpace(vec3 c)  { return sign(c) * pow(abs(c), vec3($SRGB$)); }
vec4 ApplyColorSpace(vec4 c)  { return vec4(ApplyColorSpace(c.rgb), c.a); }
vec3 RevertColorSpace(vec3 c) { return sign(c) * pow(abs(c), vec3(1.0 / $SRGB$)); }
vec4 RevertColorSpace(vec4 c) { return vec4(RevertColorSpace(c.rgb), c.a); }

#endif
