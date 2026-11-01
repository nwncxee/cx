// The one decode from world depth to view distance: every shader that turns the scene's depth into metres calls it,
// so the decode the reversed-depth golden holds is every reader's (tools/gates/world-depth.sh refuses another copy).
#ifndef CX_DEPTH_LIN
#define CX_DEPTH_LIN
// Positive view distance of world depth `d` under the folded world projection `proj`: z_clip = z_v*M22 + M32,
// w_clip = -z_v, so z_v = -M32 / (ndc + M22). The world's depth is reversed (near 1, far 0) and its projection
// folded to match, so the stored depth IS the NDC z and needs no remap.
float CxDepthViewZ(float d, mat4 proj)
{
    return proj[3].z / (d + proj[2].z);
}
#endif
