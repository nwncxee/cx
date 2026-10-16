#ifndef CX_INC_STANDARD
#define CX_INC_STANDARD
#include "inc_scriptable"
// CX-303: inc_standard's two entry points for a CONTENT stage (ContentStage substitutes this for its include). The
// setup multiplies the texel in, as cx_vanstd_f's does, and also makes the stock stage's coverage cut, because a
// content program skips Alloy's own: stock cuts the texel alpha unless the STAGE declares its alpha an env level
// (ENVIRONMENT_MAP 1), and the stage's own defines are in force here. Alloy's threshold and mip compensation.
bool CxCoverCut(float coverage, float alpha, vec2 du1, vec2 du2, vec2 cutSize, float cutMip);
void SetupStandardShaderInputs()
{
    FragmentColor *= CxHostTexel;
#if ENVIRONMENT_MAP != 1 && NO_TEXTURE != 1
    if (CxCoverCut(FragmentColor.a, uDraw[vDraw].rot.w, CxHostDu.xy, CxHostDu.zw, CxHostCutAt.xy, CxHostCutAt.z)) discard;
#endif
}
void ApplyStandardShader() { }
#endif
