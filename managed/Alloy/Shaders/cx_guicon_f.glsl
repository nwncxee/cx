// A content pair's fragment half on a GUI part: the part's own texel, the half's body over it, then the GUI's own light.
// GuiContent.TwinFs puts the version line ahead of this text and the translated half in its region.
// ContentStage translates the half against cx_content_f and cx_vanenv_f, the world's environment, so a stage reads the
// same names here. The names that environment expects of the lit stage are this draw's values, or what a world part with
// nothing set reads: a GUI scene has no fog, keyhole, area light or probe. cx_featdecl's own terms stay loose, so they read 0.
#define CX_GUI_TWIN 1
#include "cx_guilib.glsl"
#include "cx_featdecl.glsl"

uniform int uGuiRow;       // the draw's ContentTable row, -1 for none
uniform int uGuiSlots;     // its recipe's slot mask
uniform vec2 uGuiScreen;   // the view's size
int cxGuiRec;

layout(binding = 1) uniform sampler2D uNrmTex;
layout(binding = 2) uniform sampler2D uSpecTex;
layout(binding = 3) uniform sampler2D uRoughTex;
layout(binding = 4) uniform sampler2D uHeightTex;
layout(binding = 5) uniform sampler2D uIllumTex;
layout(binding = 6) uniform sampler2D uFlowTex;
layout(binding = 7) uniform sampler2D uGuiPal0;
layout(binding = 8) uniform sampler2D uGuiPal1;
layout(binding = 9) uniform sampler2D uGuiPal2;
layout(binding = 10) uniform sampler2D uGuiPal3;
layout(binding = 11) uniform sampler2D uSceneDepth;
layout(binding = 13) uniform samplerCube uProbe;

struct CxDraw { mat4 model; vec4 rot; vec4 amb; vec4 dif; vec4 emi; vec4 pbr; vec4 spc; vec4 feat; };
CxDraw uDraw[1];
const int vDraw = 0;
#include "cx_conbuf.glsl"
#include "cx_slotbits.glsl"

#define vUV vUv
#define vWorld vPos
#define vDepth (-vPos.z)
const vec4 vTan = vec4(0.0);
const vec3 uEyePos = vec3(0.0);
const vec2 uKeyholeCam = vec2(0.0);
const vec3 uKeyholeDia = vec3(0.0);
const vec3 uAreaAmbient = vec3(0.0), uAreaDiffuse = vec3(0.0), uAreaDir = vec3(0.0, 0.0, 1.0);
const vec2 uAtten = vec2(0.0);
const vec4 uFogParams = vec4(0.0), uFogColor = vec4(0.0);
const vec3 CX_ENV_GREY_LIN = vec3(0.4098);
mat4 uVP;
vec4 uPixel;

// No light is binned for a GUI scene.
const uint CX_CLUSTER_CAP = 1u;
const uint uClIdx[1] = uint[1](0u);
const vec4 uLights[2] = vec4[2](vec4(0.0), vec4(0.0));
uint CxCluster(float depth) { return 0u; }
uint CxClusterWalk(uint cluster) { return 0u; }
int CxLightSlot(int w) { return -1; }
vec3 CxLinear(vec3 c) { return pow(max(c, vec3(0.0)), vec3($SRGB$)); }

#include "cx_vanenv_f.glsl"

#define CX_PLT_HEIGHT 1792.0

// The engine's PLT scheme feed off this record's rows, as cx_plt's CxPltScheme reads a world record's.
float CxPltScheme(int draw, int layer)
{
    return (int(uGui[cxGuiRec + 13].x) & 32) != 0 && layer >= 0 && layer < 10 ? GuiPltRow(cxGuiRec, layer) / CX_PLT_HEIGHT : 0.0;
}

//$GUI-CONTENT-REGION

// A GUI scene never alpha-tests (Scene::RenderOnlyGobs), so the stock setup's cut has nothing to do.
bool CxCoverCut(float coverage, float alpha, vec2 du1, vec2 du2, vec2 cutSize, float cutMip) { return false; }

void main()
{
    int r = vRec * 16;
    cxGuiRec = r;
    int flags = int(uGui[r + 13].x);
    float envLevel;
    vec4 t = GuiTexel(r, flags, envLevel);
    uVP = mat4(uGui[r], uGui[r + 1], uGui[r + 2], uGui[r + 3]);
    uPixel = vec4(vec2(1.0) / max(uGuiScreen, vec2(1.0)), uGuiScreen.x / max(uGuiScreen.y, 1.0), 0.0);
    uDraw[0].model = mat4(1.0);
    uDraw[0].rot = vec4(1.0, 0.0, 0.0, 1.0);
    uDraw[0].amb = uDraw[0].dif = uDraw[0].spc = vec4(1.0);
    uDraw[0].emi = vec4(0.0);
    uDraw[0].pbr = vec4(0.0, 0.0, 0.0, float(uGuiSlots));
    uDraw[0].feat = vec4(float(CX_FEAT_CONTENT0), -1.0, float(uGuiRow), 0.0);
    CxHostTexel = vec4(ApplyColorSpace(t.rgb), t.a);
    CxHostUV = vUv;
    FragmentColor = vec4(1.0);
    vFragmentNormal = vNrm;
    vSurfaceNormal = vNrm;
    vViewToSurface_n = normalize(vPos);
    fSpecularity = 0.0; fRoughness = 1.0; fMetallicness = 0.0;
    SpecularColor = vec3(0.0);
    CxHostSurface(CX_FEAT_CONTENT0);
    vec4 c = GuiShade(r, flags, FragmentColor.rgb, 1.0, envLevel, vFragmentNormal);
    float part = c.a;
    c.a = FragmentColor.a;
#ifdef CX_CONTENT_POST
    c = CxHostPost(c);
#endif
    frag = vec4(RevertColorSpace(c.rgb), c.a * part);
}
