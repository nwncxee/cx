// The cutout, then #363's dither: the body every masked depth stage runs.
    float t = texture(uTex, vUV).a;
    float tmip = uAlphaMip;
    vec2 tsize = vec2(textureSize(uTex, 0));
    // CX-191: the colour pass's compensated test, so a distant canopy and its shadow keep the same holes
    if (CxAlphaMipAt(t, dFdx(vUV), dFdy(vUV), tsize, 0.0, tmip) <= uAlphaCut) discard;
    // the colour pass's own blend weight: coverage (1 where the diffuse alpha is reflectivity) x rot.w
    float a = (uEnvAlpha != 0 ? 1.0 : t) * vAlpha;
    if (uDither > 0 && cxHash(uvec2(gl_FragCoord.xy) / uDither) > a) discard;
