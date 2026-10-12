// The cutout, then #363's dither: the body every masked depth stage runs.
    // CX-223: a palette-tint caster's coverage is its own map, not the diffuse alpha -- the same source the
    // colour draw reads (CoverageLayer.HasCoverageSource), so the shadow drops the texels the draw drops.
    // Only the HANDLE stage can ask: the plain one carries no record index, and the maps are handles anyway.
    float t = texture(uTex, vUV).a;
    float tmip = uAlphaMip;
    vec2 tsize = vec2(textureSize(uTex, 0));
#ifdef CX_CAST_HANDLE
    cxTintRow = CxTintRow(vDraw);
    vec4 ctint = cxTintRow >= 0 ? CxTintCoverage(vDraw, cxTintRow, vUV) : vec4(-1.0);
    if (ctint.x >= 0.0) { t = ctint.x; tmip = ctint.y; tsize = ctint.zw; }
    // CX-218: and the same for a slot-1 coverage caster, at the threshold the batch was staged with
    // (CasterCut.CoverNrm, the content stages' own 0.2) -- the colour draw's source, so the shadow keeps the
    // fence's gaps. Such a batch is staged ONLY on this stage, for the reason above: slot 1 is a handle, and
    // one still loading reads 0, whose alpha would discard the whole caster.
#ifdef CX_BINDLESS
    if ((int(uDraw[vDraw].pbr.w) & CX_SLOT_COVERNRM) != 0 && uTexH[(vDraw << 3) + 1] != uvec2(0u))
    { t = texture(uNrmTex, vUV).a; tsize = vec2(textureSize(uNrmTex, 0)); }
#endif
#endif
    // CX-191: the colour pass's compensated test, so a distant canopy and its shadow keep the same holes
    if (CxAlphaMipAt(t, dFdx(vUV), dFdy(vUV), tsize, 0.0, tmip) <= uAlphaCut) discard;
    // the colour pass's own blend weight: coverage (1 where the diffuse alpha is reflectivity) x rot.w
    float a = (uEnvAlpha != 0 ? 1.0 : t) * vAlpha;
    if (uDither > 0 && cxHash(uvec2(gl_FragCoord.xy) / uDither) > a) discard;
