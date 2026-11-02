// The precipitation drawn after the TAA resolve, over the resolved frame. The overlay holds premultiplied
// scene-referred light, so its light takes the frame's exposure and tone curve and lands over the resolved
// pixel in display-linear light: the resolve never sees it, and so never holds it in its history.
#include "cx_tonemap.glsl"

/// `lin` the resolved pixel in display-linear light, `wx` the overlay over it (rgb light, a extinction).
vec3 CxWxOver(vec3 lin, vec4 wx, float exposure, int mode, float knee, float acesMix)
{
    return lin * (1.0 - wx.a) + CxTonemap(wx.rgb * exposure, mode, knee, acesMix);
}
