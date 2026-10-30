// CX-51: the reflection record, beside the colour on draw buffers 2 and 3 while Ssr has them attached (the
// opaque, cutout and grass passes; elsewhere they land on NONE). Ssr's trace reads it back per block and its resolve per pixel; the format
// is cx_ssrrec's.
#ifndef CX_REFLREC
#define CX_REFLREC
#include "cx_ssrrec.glsl"
layout(location = 2) out vec4 oReflN;   // octahedral world normal, roughness, view depth
layout(location = 3) out vec4 oReflW;   // the reflection's weight as it reaches the screen (split-sum, dissolve, fog); no alpha
/// A surface's record. The view depth lets the trace reject a record a later draw covered without writing one.
void CxReflOut(vec3 N, float rough, vec3 w)
{
    oReflN = vec4(CxOctEncode(N), rough, 1.0 / gl_FragCoord.w);
    oReflW = vec4(w, 0.0);
}
/// A surface the trace must leave alone.
void CxReflNone() { oReflN = vec4(0.0); oReflW = vec4(0.0); }
#endif
