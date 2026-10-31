// The depth-copy fade every soft billboard stage runs -- particles, halos, rain, snow: the
// scene's view-space DISTANCE (positive) under a fragment, plus the stage's viz switch and output.
// The two coefficients come out of the PROJECTION MATRIX rather than from near/far uniforms -- it is
// the matrix this draw is actually using, so the linearisation cannot drift from the pass's own
// frustum the way a pair of separately-uploaded planes can.
uniform mat4 uProj;
uniform sampler2D uSceneDepth;
uniform int uViz;
out vec4 frag;

// View distance of world depth `d` under the world projection `proj`. The world's depth is reversed (near 1, far 0)
// and its projection folded to match, so the stored depth IS the NDC z and needs no remap.
float CxDepthViewZ(float d, mat4 proj)
{
    return proj[3].z / (d + proj[2].z);
}

float CxSceneViewZ(vec2 uv)
{
    return CxDepthViewZ(texture(uSceneDepth, uv).r, uProj);
}
