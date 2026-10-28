#version 460 core
// Blade depth (sun cascades). Opaque geometry: nothing to decide per fragment.
// Under CX_MOTION the same stage writes the blade's own velocity instead -- still nothing to decide,
// since a blade has no cutout. The convention library sits outside the conditional: its uniforms are
// the stage's first file-scope lines, which is where the Vulkan translation seats the shared block.
#include "cx_motionout.glsl"
#ifdef CX_MOTION
in vec4 vCur;
in vec4 vPrev;
out vec4 frag;
#endif
void main()
{
#ifdef CX_MOTION
    frag = vec4(CxMotionVec(vCur, vPrev), 0.0, 0.0);
#endif
}
