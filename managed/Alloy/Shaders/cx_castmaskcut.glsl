// The cutout, then #363's dither: the body every masked depth stage runs.
    // CX-223: a palette-tint caster's coverage is its own map, not the diffuse alpha -- the same source the
    // colour draw reads (CoverageLayer.FromDiffuse), so the shadow drops the texels the draw drops. Only the
    // HANDLE stage can ask: the plain one carries no record index, and the maps are handles either way.
#ifdef CX_CAST_HANDLE
    bool ctint = int(uDraw[vDraw].feat.x) == CX_FEAT_PALETTETINT;
    float t = ctint ? CxTintCover(vDraw, vUV) : texture(uTex, vUV).a;
    float tmip = ctint ? CxTintMip(vDraw) : uAlphaMip;
    vec2 tsize = ctint ? CxTintCoverSize(vDraw) : vec2(textureSize(uTex, 0));
#else
    float t = texture(uTex, vUV).a;
    float tmip = uAlphaMip;
    vec2 tsize = vec2(textureSize(uTex, 0));
#endif
    // CX-191: the colour pass's compensated test, so a distant canopy and its shadow keep the same holes
    if (CxAlphaMipAt(t, dFdx(vUV), dFdy(vUV), tsize, 0.0, tmip) <= uAlphaCut) discard;
    // the colour pass's own blend weight: coverage (1 where the diffuse alpha is reflectivity) x rot.w
    float a = (uEnvAlpha != 0 ? 1.0 : t) * vAlpha;
    if (uDither > 0 && cxHash(uvec2(gl_FragCoord.xy) / uDither) > a) discard;
