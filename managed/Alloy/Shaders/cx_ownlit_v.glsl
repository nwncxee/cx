#version 460 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aUV;
layout(location = 2) in vec3 aNrm;
// #674: the lightmap chart UV, in the mesh atlas's texel units (MeshChart.LocChartUv). Only a charted
// VAO enables the array; every other reads the constant attribute, and its record says unbaked anyway.
layout(location = 3) in vec2 aChart;
// CX-221: the mesh arena's two packed words. aTangent is INT_2_10_10_10_REV -- xyz the model-space tangent,
// w the handedness -- and a ZERO w is "this mesh carries no frame", which is what sends the fragment stage
// back to its derivative frame. aPaint is the mesh's normalized RGBA8 vertex paint, white where it has none.
layout(location = 4) in vec4 aTangent;
layout(location = 5) in vec4 aPaint;
#include "cx_drawbuf.glsl"
#include "cx_featdecl.glsl"
uniform mat4 uVP;
uniform int uDrawBase;
invariant gl_Position;
out vec2 vUV;
out vec3 vWorld;
out vec3 vNrm;
out vec4 vTan;                 // CX-221: world tangent xyz, handedness w (0 = the mesh supplied no frame)
out float vDepth;
out float vAnchorZ;            // #123: undisplaced world height, for the roof anchor discard
out vec2 vChart;               // #674
flat out int vDraw;
#include "cx_feat_v.glsl"
#include "cx_bone_v.glsl"
void main()
{
    int id = CxSlot(uDrawBase + gl_DrawID);
    CxDraw d = uDraw[id];
    vec4 pos = CxFeatPos(d, aPos, vAnchorZ);
    // #137: a posed skinmesh REPLACES the feature position -- the two never coexist (see
    // ShaderFeature.Skin) -- and the same blended matrix then carries the normal, so a cloak is lit
    // off the surface it actually has rather than the one it had in bind pose.
    int bb = CxBoneBase(d);
    mat4 skin = bb >= 0 ? CxSkinMat(bb) : mat4(1.0);
    if (bb >= 0) pos = skin * vec4(aPos, 1.0);
    vec4 world = d.model * pos;
    // The undisplaced branch is written out in full rather than folded into the line below, because
    // #119's prepass re-tests this exact expression and `invariant` only holds over an identical
    // SEQUENCE of operations -- not merely identical values. So the test is "does this part displace",
    // NOT "does it have a feature": a nokeyhole or flowmap part does not move, therefore stays in the
    // prepass, and therefore must reach the literal expression the prepass compiled. Only a displacing
    // part takes the other branch, and the CPU excludes exactly those from the prepass with the same
    // predicate -- no text check can catch the two disagreeing, so they mirror each other by
    // construction and SelfCheck holds it.
    // The skinned arm takes the DISPLACED expression, not a third one, and takes it first -- see
    // the fullbright stage for why, and ShaderFeatures.OutOfPrepass for the prepass side of it.
    if (bb >= 0) gl_Position = uVP * d.model * pos;
    else if (!CxDisplaces(int(d.feat.x))) gl_Position = uVP * d.model * vec4(aPos, 1.0);
    else gl_Position = uVP * d.model * pos;
    vDepth = gl_Position.w;
    // shaded at the DISPLACED position, which is where the vanilla shaders light a swaying plant too
    vWorld = world.xyz;
    vNrm = mat3(d.model) * (bb >= 0 ? mat3(skin) * aNrm : aNrm);
    // The tangent rides the same matrices as the normal -- a posed cloak's frame has to follow its pose --
    // and the handedness is a scalar no transform touches. d.rot rotates the UVs below, and the supplied
    // frame was derived in the UNROTATED ones, so it turns with them: T' = cos*T - sin*B over the frame's
    // own bitangent. A zero word stays zero through all of it, which is the "no frame" signal.
    vec3 aT = bb >= 0 ? mat3(skin) * aTangent.xyz : aTangent.xyz;
    vec3 aB = cross(bb >= 0 ? mat3(skin) * aNrm : aNrm, aT) * aTangent.w;
    vTan = vec4(mat3(d.model) * (d.rot.x * aT - d.rot.y * aB), aTangent.w);
    vUV = vec2(d.rot.x * aUV.x - d.rot.y * aUV.y, d.rot.y * aUV.x + d.rot.x * aUV.y);
    vChart = aChart;
    vDraw = id;
}
