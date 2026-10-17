#include "cx_castmaskhead.glsl"
flat in int vDraw;
// the record, so a caster samples its own diffuse handle
#define CX_CAST_HANDLE 1
#include "cx_drawbuf.glsl"
#include "cx_featdecl.glsl"
void main()
{
#ifdef CX_BINDLESS
    // a handle zeroed under a dying texture: opaque until the record is re-resolved, never sampled
    if (uTexH[vDraw << 3] == uvec2(0u)) return;
#endif
#include "cx_castmaskcut.glsl"
}
