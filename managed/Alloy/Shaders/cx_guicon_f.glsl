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
// The engine's lobe: a GUI program never uploads light.brdf, so it reads the engine's own forms.
const vec4 uMaterial = vec4(0.0);
#include "cx_matsynth.glsl"
#include "cx_lobe.glsl"

#include "cx_vanenv_f.glsl"

#define CX_PLT_HEIGHT 1792.0

// The engine's PLT scheme feed off this record's rows, as cx_plt's CxPltScheme reads a world record's.
float CxPltScheme(int draw, int layer)
{
    return (int(uGui[cxGuiRec + 13].x) & 32) != 0 && layer >= 0 && layer < 10 ? GuiPltRow(cxGuiRec, layer) / CX_PLT_HEIGHT : 0.0;
}

//$GUI-CONTENT-REGION

// A GUI scene never alpha-tests (Scene::RenderOnlyGobs), so the stock setup's cut has nothing to do.
bool CxCoverCut(float coverage, float alpha) { return false; }

// A lit part whose stage gave it a material of its own, as inc_lighting.shd's ComputeLighting shades one under
// SPECULAR_LIGHT: the diffuse share 1 - fEnvironmentSpecularity, the environment as specular light scaled by that
// specularity, and the directional light's lobe, all under the material's specular colour.
vec4 GuiSpecShade(int r, int flags, vec3 albedo, vec3 n)
{
    bool nrm = dot(n, n) > 1e-8;
    vec3 un = nrm ? normalize(n) : vec3(0.0, 0.0, 1.0);
    vec3 L, keyCol;
    vec3 lit = GuiLight(r, flags, un, nrm, L, keyCol);
    vec3 V = -normalize(vPos);
    float NoV = max(dot(un, V), 0.0);
    float rough = max(fRoughness, CX_ALPHA_MIN);
    float envSpec = mix(mix(fSpecularity, 1.0, CxFresnel(NoV) * cxFresnel), fSpecularity, sqrt(rough));
    vec3 specLight = lit * ((flags & 768) == 768 ? GuiEnvTexel(un) : CX_ENV_GREY_LIN) * envSpec;
    if (nrm) specLight += keyCol * (max(dot(un, L), 0.0) * CxSpecLobe(un, V, L, rough, fSpecularity) * CxSpecBase(NoV, rough));
    return vec4(GuiLitColour(r, flags, albedo, lit * (1.0 - envSpec)) + SpecularColor * specLight, uGui[r + 11].a);
}

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
    uDraw[0].amb = uDraw[0].dif = vec4(1.0);
    uDraw[0].spc = vec4(0.0);
    uDraw[0].emi = vec4(0.0);
    uDraw[0].pbr = vec4(0.0, 0.0, 0.0, float(uGuiSlots));
    uDraw[0].feat = vec4(float(CX_FEAT_CONTENT0), -1.0, float(uGuiRow), 0.0);
    CxHostTexel = vec4(ApplyColorSpace(t.rgb), t.a);
    CxHostUV = vUv;
    float specTexel = (uGuiSlots & CX_SLOT_SPEC) != 0 ? texture(uSpecTex, vUv).r : 0.0;
    float roughTexel = (uGuiSlots & CX_SLOT_ROUGH) != 0 ? texture(uRoughTex, vUv).r : 0.0;
    vec3 pbr = CxSynthPbr(uDraw[0].pbr.xyz, uGuiSlots, envLevel, specTexel, roughTexel);
    vec3 specColour = CxSpecColor(uDraw[0].spc, CxHostTexel.rgb, pbr.z, uGuiSlots);
    CxHostMat = vec4(specTexel, roughTexel, envLevel, 0.0);
    FragmentColor = vec4(1.0);
    vFragmentNormal = vNrm;
    vSurfaceNormal = vNrm;
    vViewToSurface_n = normalize(vPos);
    fSpecularity = pbr.x; fRoughness = pbr.y; fMetallicness = pbr.z;
    SpecularColor = specColour;
    cxFresnel = CxBareMaterial(uDraw[0].pbr.xyz, uGuiSlots) ? 0.0 : 1.0;
    CxHostSurface(CX_FEAT_CONTENT0);
    envLevel = fEnvMapLevel;
    // the GUI's own shading stays the part's until its stage rebuilds or edits the material it was handed
    bool own = vec3(fSpecularity, fRoughness, fMetallicness) != pbr || SpecularColor != specColour;
    vec4 c = own && (flags & 4) != 0 ? GuiSpecShade(r, flags, FragmentColor.rgb, vFragmentNormal)
                                     : GuiShade(r, flags, FragmentColor.rgb, 1.0, envLevel, vFragmentNormal);
#ifdef CX_CONTENT_SELFILLUM
    c.rgb += cxSelfIllum;
#endif
    float part = c.a;
    c.a = FragmentColor.a;
#ifdef CX_CONTENT_POST
    c = CxHostPost(c);
#endif
    frag = vec4(RevertColorSpace(c.rgb), c.a * part);
}
