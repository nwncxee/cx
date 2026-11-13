#ifndef CX_INC_STANDARD
#define CX_INC_STANDARD
#include "inc_scriptable"
// inc_common's own derivation, after the stage's defines: a stage tests these to pick the path the engine lights by.
#if NORMAL_MAP == 1
#undef FRAGMENT_NORMAL
#define FRAGMENT_NORMAL 1
#endif
#if LIGHTING == 1 && (FRAGMENT_LIGHTING == 1 || FRAGMENT_NORMAL == 1) && POSTPROCESSING != 1
#define SPECULAR_LIGHT 1
#else
#define SPECULAR_LIGHT 0
#endif
// CX-303: inc_standard's two entry points for a CONTENT stage (ContentStage substitutes this for its include). The
// setup multiplies the texel in (unless the stage declares NO_TEXTURE), as cx_vanstd_f's does, and also makes the
// stock stage's coverage cut, because a content program skips Alloy's own: stock cuts the texel alpha unless the
// STAGE declares its alpha an env level (ENVIRONMENT_MAP 1), and the stage's own defines are in force here.
// Alloy's threshold.
bool CxCoverCut(float coverage, float alpha);
void SetupStandardShaderInputs()
{
#if NO_TEXTURE != 1
    FragmentColor *= CxHostTexel;
#endif
#if ENVIRONMENT_MAP != 1 && NO_TEXTURE != 1
    if (CxCoverCut(FragmentColor.a, uDraw[vDraw].rot.w)) discard;
#endif
}
void ApplyStandardShader() { }
#endif
