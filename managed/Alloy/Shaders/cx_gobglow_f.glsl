// The scene's highlight texture (fxpa_dot02) times the quad's colour, added at alpha -- the engine's
// SetBlendFunc(0, 2) over the display-space frame, unlit and unfogged.
#version 460 core
in vec2 vUV;
in vec4 vColor;
in float vViewZ;
uniform sampler2D uTex;
uniform vec4 uViewRect;   // the target's viewport: its origin, then 1 / its size (origin and height flipped on a top-down screen), so the depth fetch works under a render scale
#include "cx_scenedepth.glsl"
void main()
{
    // DoHighlightGob leaves the depth test on: a quad fragment behind the scene surface is hidden.
    if (-vViewZ > CxSceneViewZ((gl_FragCoord.xy - uViewRect.xy) * uViewRect.zw)) discard;
    vec4 t = texture(uTex, vUV);
    frag = vec4(vColor.rgb * t.rgb * (vColor.a * t.a), 0.0);
}
