#include "cx_castmaskhead.glsl"
flat in int vDraw;
// CX-223: the record and the palette tint's side table, so a palette caster casts the coverage it draws.
#define CX_CAST_HANDLE 1
#include "cx_drawbuf.glsl"
#include "cx_featdecl.glsl"
#include "cx_tintbuf.glsl"
void main()
{
#ifdef CX_BINDLESS
    // a handle zeroed under a dying texture: opaque until the record is re-resolved, never sampled
    if (uTexH[vDraw << 3] == uvec2(0u)) return;
#endif
#include "cx_castmaskcut.glsl"
}
