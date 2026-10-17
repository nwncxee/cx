#ifndef CX_INC_STANDARD
#define CX_INC_STANDARD
#include "inc_scriptable"
// CX-303: inc_standard's vertex entry point for a CONTENT stage (ContentStage substitutes this for its include): the
// stock transform and the per-vertex terms the fragment side reads. The lights and the environment coordinates are
// the fragment stage's own under Alloy, so their stock vertex halves have nothing to set up.
void ApplyStandardShader()
{
    TransformMainCoordinates();
#if KEYHOLING == 1
    SetupKeyholeFactors();
#endif
    SetupViewToSurfaceNormal();
#if FOG == 1
    SetupFog();
#endif
}
#endif
