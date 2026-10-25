// CX-316: the terms the engine's own SetUniforms hands every program by NAME, as ONE std140 block -- the module
// calendar, the frame counter, the weather, the mouse, the wind's point sources, the engine's projection and the
// solid-noise handle. Every content environment includes this file, so a stage reads the same layout whichever
// environment it compiles in; AlloyRenderer's EngineBlock fills it and UboSlots.Engine pins its point.
//
// The engine's names are MACROS over the members rather than members under those names. Vanilla's rule is that
// content may declare the set itself -- inc_uniforms declares all of it, inc_sunm moduleTimeIntoTransition,
// vs_beamvol and vs_shadowvol an inline m_proj -- and a macro is what lets the translator drop such a line
// (ContentStage's declaration strip) instead of colliding with it. It also makes a stale environment override
// that kept one as a loose uniform expand into a redeclaration of the member, which fails the compile loudly
// instead of linking and reading zero.
#ifndef CX_ENGINE_BLOCK
#define CX_ENGINE_BLOCK
// What inc_uniforms sizes its parallel arrays to, and the engine's own ceiling (SetUniforms clamps the point-source
// list to 0x80). The block spells 128 literally: its member offsets are computed from this text.
const int MAX_WINDS = 128;
layout(std140, binding = 2) uniform CxEngine
{
    mat4 uEngProj;
    // std140 pads an array element out to a whole vec4, so each of the five lanes below costs 2 KB for 128
    // entries. That is the price of the engine's five PARALLEL arrays staying indexable under their own names.
    vec3 uWindPos[128];
    float uWindRadius[128];
    float uWindDuration[128];
    float uWindIntensity[128];
    float uWindRemain[128];
    ivec2 uMouse;
    // The engine's solid_noise, the texture its own water samples, as a bindless handle (0 before it is resident).
    uvec2 uConNoise;
    int uMouseBtn;
    int uWindCount;
    int uModYear;
    int uModMonth;
    int uModDay;
    int uModHour;
    int uModDawn;
    int uModDusk;
    int uModMinutes;
    int uSceneFrame;
    int uWeatherType;
    float uModTransition;
    float uModTransTime;
    float uWeatherDensity;
};
#define m_proj uEngProj
#define windPointSourcesCount uWindCount
#define windPointSourcesPosition uWindPos
#define windPointSourcesRadius uWindRadius
#define windPointSourcesDuration uWindDuration
#define windPointSourcesIntensity uWindIntensity
#define windPointSourcesTimeRemaining uWindRemain
#define moduleYear uModYear
#define moduleMonth uModMonth
#define moduleDay uModDay
#define moduleHour uModHour
#define moduleDawnHour uModDawn
#define moduleDuskHour uModDusk
#define moduleMinutesPerHour uModMinutes
#define moduleTimeIntoTransition uModTransition
#define moduleTransitionTime uModTransTime
#define sceneCurrentFrame uSceneFrame
#define areaWeatherType uWeatherType
#define areaWeatherDensity uWeatherDensity
#define userinputMousePosition uMouse
#define userinputMouseButtons uMouseBtn
#endif
