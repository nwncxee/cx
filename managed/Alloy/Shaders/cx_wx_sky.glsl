// The sky a drop or a crown droplet sees, for the water bead's reflection and refraction. Reads the pass block and the
// probe's shape, so the stages that include it include both first.
#ifndef CX_WX_SKY
#define CX_WX_SKY
/// The sky's light arriving along `d`: the area ambient shaped by the probe where one is captured.
vec3 CxWxSky(vec3 d)
{
    return uAreaAmbient * (uProbeMips > 0.0 ? CxAmbientShape(d) : vec3(1.0));
}
#endif
