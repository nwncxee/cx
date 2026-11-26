struct CxDraw { mat4 model; vec4 rot; vec4 amb; vec4 dif; vec4 emi; vec4 pbr; vec4 spc; vec4 feat; mat4 prev; vec4 ext0; vec4 ext1; vec4 ext2; };
layout(std430, binding = 0) readonly buffer CxDrawBuf { CxDraw uDraw[]; };
layout(std430, binding = 1) readonly buffer CxDrawIdxBuf { int uDrawIdx[]; };
// #242: the record slot draw n reads. Records are indexed by OWNERSHIP slot and persist across
// frames; draw order is per pass, so the two are different numbers and this is the map between them.
int CxSlot(int draw) { return uDrawIdx[draw]; }
