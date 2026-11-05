// Slot mask bits, matching AlloyRenderer's 1 << (slot - 1).
const int CX_SLOT_NORMAL = 1, CX_SLOT_SPEC = 2, CX_SLOT_ROUGH = 4,
          CX_SLOT_HEIGHT = 8, CX_SLOT_ILLUM = 16;
// #146: bit 6, texture slot 7 -- the AO map. It multiplies the SAME occlusion factor the height
// map's local term feeds, so a material carrying both gets one combined ambient scale.
const int CX_SLOT_AO = 128;
// bit 8: no shadow lands on this record (an untextured blended sheet: a door's transition sheen)
const int CX_SLOT_NOSHADOW = 256;
// bits 9..12, .mtr texture7..texture10 -- set only on a content pair's material, the stages that can name them, which
// is the same reading the flow slot's bit takes. CX_SLOT_PAL0 is the first, declared in cx_conbuf.glsl because that
// library is included ahead of this block.
const int CX_SLOT_PAL_LAST = CX_SLOT_PAL0 << 3;
// #121: bit 5 of the same slot mask -- the material declares an env map (Material::HasEnvMap, i.e.
// mat+0x40). NO texture is bound for it: the probe replaces what it held. What survives is the
// engine's own "this is shiny" flag on 2002 content, which drives the synthesis below.
const int CX_SLOT_ENV = 64;
// #123: bit 5, texture slot 6 -- the only slot outside the stock five, which a content pair's stage reads as
// texUnit6. Enrolled only on such a part, so on everything else this bit is clear and uFlowTex is never sampled.
const int CX_SLOT_FLOW = 32;
