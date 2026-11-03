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
// The block's memory layout. std140 pads every array element out to a whole vec4, which costs the five parallel
// wind lanes below 2 KB each for 512 B of data; scalar block layout packs them at their own size and the block
// drops from about 10.3 KB to 3.7 KB. A device that offers scalarBlockLayout gets the macro defined to `scalar`
// ahead of this file (VkGlsl); on its own -- a standalone compile, a content override read by itself -- the text
// reads std140, which every device lays out alike.
#ifndef CX_ENGBLK
#define CX_ENGBLK std140
#endif
layout(CX_ENGBLK, binding = 2) uniform CxEngine
{
    mat4 uEngProj;
    // The engine's five PARALLEL arrays, each indexable under its own name, as its own shaders read them.
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
// Content reads GL's window depth (far 1); the world's is reversed (far 0), exactly 1 minus GL's. The splice sends a
// content text's reads of texFBDepth through these (VanillaShader.GlDepth).
vec4 CxGlDepth(sampler2D s, vec2 uv) { return vec4(1.0) - texture(s, uv); }
vec4 CxGlDepthLod(sampler2D s, vec2 uv, float lod) { return vec4(1.0) - textureLod(s, uv, lod); }
vec4 CxGlDepthFetch(sampler2D s, ivec2 p, int lod) { return vec4(1.0) - texelFetch(s, p, lod); }
#endif
