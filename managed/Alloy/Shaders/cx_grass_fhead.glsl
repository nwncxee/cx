#version 460 core
#define CX_FS 1   // #642: gl_FragCoord is in reach for the shadow lib's dither
#define CX_LIGHT_LITE 1   // CX-188: cx_lightfrag's slimmed walk -- not a baked receiver, one shadow tap per light
#define CX_SKY_VIEW 1   // ground cover takes the same sky-visibility term the stone under it does
in vec3 vLight;
in vec3 vWorld;
in vec3 vNrm;
in float vDepth;
in float vFade;
in float vDist;
flat in uint vSeed;
#include "cx_featdecl.glsl"
#include "cx_keyfogdecl.glsl"
uniform sampler2D uTex;
uniform int uViz;                       // /alloy viz: 3 = sun visibility, 9 = the light arriving, as the lit stage's own
uniform float uShade;                   // #355: past this, no shadow or per-fragment cluster taps
// #347: what VanillaEnvFs aliases as texUnit6. Declared, never bound and never sampled here --
// nothing spliced into this stage reads it since #350. It is present so a content file that names
// texUnit6 still COMPILES; an unused sampler is dead-code eliminated and its location resolves to
// -1, which the uniform upload ignores. Grass takes the DISPLAY hook alone: it has no material
// features to reshade.
uniform sampler2D uFlowTex;
// CX-43: uAreaAmbient / uAreaDiffuse / uAreaDir ride the pass block.
#include "cx_pass.glsl"
#include "cx_skyview.glsl"
uniform samplerCube uProbe;               // CX-51: the reflection probe, on the lit stage's unit
#include "cx_ssr.glsl"
#include "cx_reflrec.glsl"
uniform vec3 uMatAmbient;               // the grass material's own ambient (CAurGrassInfo +0x34)
uniform vec3 uMatDiffuse;               // ... and diffuse (+0x28), WITHOUT vanilla's fade in its alpha
uniform int uLit;                       // 0 = no cluster build this pass: ambient + sun only
uniform int uVLit;                      // 1 = vLight carries the cluster loop's answer from the vertex stage
uniform int uMsaa;                      // 1 = the target is multisampled, so alpha-to-coverage is live
uniform int uSoftEdge;                  // 1 = blended fringe: the ramp goes out as alpha, not as dither
uniform float uEdge;                    // cutout ramp width, in units of the analytic one-pixel edge
uniform vec4 uLod;                      // x = the full-density radius the hard far edge starts inside
uniform float uRootAo;                  // darkening at the root, 0..1
uniform vec4 uLook;                     // x backlit transmission, y specular sheen (CX-24), z rain wetness where the map has not reached, w the wet-darkening exponent scale
#include "cx_wx_map.glsl"
uniform vec2 uSnow;                     // y = winter look: straw colour, snow on the tips
uniform float uGroundTint;              // fraction of the blade's height that fades into the ground under its root
uniform sampler2D uGround;              // the frame as it stood before the grass: the lit, fogged ground
uniform int uGroundOn;                  // 1 = the copy was taken this pass
uniform int uGroundViz;                 // /grass gviz 1: paint the fetched copy texel over the whole blade
in float vRoot;
in float vLookDown;
layout(location = 0) out vec4 frag;
#include "cx_grassfetch.glsl"
#include "cx_grassdither.glsl"
