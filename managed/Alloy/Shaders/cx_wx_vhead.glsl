#version 460 core
uniform mat4 uView;
uniform mat4 uProj;
uniform vec3 uEye;
uniform vec3 uCentre;    // the field's centre: the camera's follow target
uniform vec3 uFall;      // unit fall direction: gravity leaning into the area wind
uniform float uPhase;    // metres fallen, wrapped at 1024 box lengths
uniform float uTime;     // wobble clock, seconds
uniform vec2 uBox;       // (width across the fall axis, length along it)
uniform vec2 uSize;      // (extent along the fall axis, width) of one quad
uniform int uKind;       // 1 rain, 2 snow, 3 roof drips
uniform float uDrift;    // snow wobble amplitude, metres
uniform sampler2DArrayShadow uCover;   // #617: the sun array; the cover map is layer uCoverLayer
uniform mat4 uCoverVp;   // world -> the cover view's clip space; zero matrix = no map
uniform float uCoverLayer;
uniform float uCoverSoft;    // metres of feather at a cover edge
uniform int uLit;        // 1 = take the light rig; 0 = flat area terms
uniform float uBack;     // snow: how strongly a flake scatters light forward, toward an eye looking at the light
// CX-43: uAreaAmbient / uAreaDiffuse / uAreaDir ride the pass block.
#include "cx_pass.glsl"
out vec2 vUV;
out float vViewZ;
out float vSeed;
out vec3 vLight;         // linear light at this drop or flake
out vec3 vBehind;        // linear sky seen through it
/// The forward lobe of a rain drop's sun scatter (Henyey-Greenstein g).
const float CX_WX_RAIN_G = 0.35;

vec2 cxClusterXY;
#define CX_CLUSTER_XY cxClusterXY
#define CX_LIGHT_LITE 1   // CX-188: cx_lightfrag's slimmed walk -- not a baked receiver, one shadow tap per light
