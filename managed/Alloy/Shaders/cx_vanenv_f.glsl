#include "cx_vanenv_defs.glsl"
// The engine's own projection, off the same matrix stack Alloy reads its own from. Only the
// DIAGONAL is ever read by the files spliced here (m_proj[3][3] separates the orthographic 2D UI
// from the perspective world), and a diagonal element is the same in either transpose convention --
// which matters, because Alloy stages hold the transpose of what the engine's do.
uniform mat4 m_proj;
// The scriptable block is NOT declared here. Every file spliced into this stage includes
// inc_scriptable for it, and the bridge resolves that include through the resource chain, so
// declaring it too is a redefinition and a dead stage. What Alloy owes is the VALUES:
// UploadVanillaEnv and UploadTargetShape write them under those names out of the engine's own
// tables (scriptableInt(n) = i[10+n], scriptableFloat(n) = f[23+n]), and a name the stage does not
// use resolves to -1, which GL ignores.

// inc_common's colour-space pair, and its exact curve. ApplyColorSpace goes display -> WORKING
// space and RevertColorSpace comes back. The Alloy frame is linear, which is what working space is
// on a gamma-correcting client, so content that converts either way gets what it expects -- and
// that is also why a stock stage's FragmentColor is linear where it is handed to a hosted body.
vec3 ApplyColorSpace(vec3 c)  { return sign(c) * pow(abs(c), vec3($SRGB$)); }
vec3 RevertColorSpace(vec3 c) { return sign(c) * pow(abs(c), vec3(1.0 / $SRGB$)); }
// #350: the stock stage's working colour, and the texel its setup call multiplies into it --
// Alloy's linear albedo with the coverage in .a, sampled before the body runs. .rgb is read back
// as the albedo.
vec4 FragmentColor;
vec4 CxHostTexel;
// ...and the material terms as inc_material's generated globals: filled from Alloy's synthesis
// before a hosted body runs, read back after it (see the MATERIAL_*_VALUE macros above).
float fRoughness, fSpecularity, fMetallicness;
vec3 SpecularColor;

// #348: what inc_ripple.shd's TiltNormal writes through, and the two matrices it converts with.
// The helper tilts a VIEW-space normal in the surface's world xy plane; Alloy's normal is already
// world-space, so the matrices are IDENTITY and the conversion collapses to the world-plane tilt the
// helper is written to perform. That is exact, not an approximation -- read the function.
vec3 vFragmentNormal;
const mat4 m_view = mat4(1.0);
const mat4 m_view_inv = mat4(1.0);
// The GEOMETRY normal (before any normal map) and the eye-to-fragment direction, both under the
// same identity view: stock reads them in view space and only ever takes a dot or a Z off them,
// neither of which a rotation changes.
vec3 vSurfaceNormal;
vec3 vViewToSurface_n;
// ...and the fragment's position from the eye, under that same identity view: its length and every dot
// against the world normal are stock's exactly. A stage that reads its .xy gets world axes where stock has
// screen ones -- fs_snowed01 offsets a sparkle lookup by it, the same scatter in another frame.
vec3 vPosView;

// inc_common.shd's brightness, both overloads, which a hosted body calls and the bridge never follows into.
float ColorToBrightness(vec3 Color) { return dot(Color, vec3(0.3333333)); }
float ColorToBrightness(vec4 Color) { return ColorToBrightness(Color.rgb); }

/// #347: how lit this fragment is, for a stock file that would have asked the engine's light arrays.
/// The RAW measurement -- the caller applies its own floor/full curve to it, which is where those
/// constants belong.
/// <para>This is <c>inc_vision.shd</c>'s <c>VisionLightHere</c> term for term: the strongest single
/// light reaching this point, by the engine's own attenuation, with NO N.L and NO shadow. That is not
/// an approximation of the stock loop, it IS the stock loop -- it asks "is this spot inside a light",
/// and max() rather than a sum because two torches do not make that twice as true.</para>
/// <para>It used to answer with the luma of Alloy's clustered DIFFUSE, which is N.L-weighted, cube-
/// shadowed and summed. Every one of those makes the answer smaller than the stock loop's, and the
/// answer holds a distance veil OPEN -- so a lit tavern read as an unlit one and the veil crushed it
/// to black. A floor at grazing incidence to a wall lantern, or anything the lantern's own cube
/// shadows, lost the light entirely.</para>
float CxVisionLight()
{
    uint cl = CxCluster(vDepth);
    // the culled tail cannot win this max: cx_lightbuild culls only under the brightest light's floor here
    uint n = CxClusterWalk(cl);
    uint base = cl * CX_CLUSTER_CAP;
    vec2 at = uAtten.x > 0.0 ? uAtten : vec2(1.0, 1.0);
    float lit = 0.0;
    for (uint k = 0u; k < n; k++)
    {
        uint li = uClIdx[base + k];
        vec4 pr = uLights[li * 2u];
        float rr = pr.w * pr.w;
        vec3 dl = pr.xyz - vWorld;
        float dd = dot(dl, dl);
        if (dd > rr) continue;
        float f2 = dd / rr;
        float att = (1.0 - f2) / (at.x + at.y * f2);
        vec3 c = CxLinear(uLights[li * 2u + 1u].rgb);
        lit = max(lit, att * dot(c, vec3(0.299, 0.587, 0.114)));
    }
    return lit;
}
