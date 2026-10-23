    vec3 base = albedo * matDiffuse;
    vec3 specColor = SpecularColor;
    vec3 F0 = specColor * spec;
    cxFresnel = max(cxFresnel, wxWet * (1.0 - wxPoro));   // rain on a bare SEALED surface is authored wet: the grazing sheen; porous ground drinks it
    // ADR 0026: the loop's per-fragment material state. Under light.brdf 0 this is the engine's split
    // exactly -- the view-side normalisation and the specular colour, applied per light instead of once
    // over the sum, which is the same product. Under 1 it adds multiple scattering and the two layers.
    float NoV = max(dot(N, V), 0.0);
    // at 0 the lobe is inf x 0 (Smith k 0, CxSpecBase 0): NaN over every flat triangle, where aaKernel is 0 too
    rough = max(rough, CX_ALPHA_MIN);
    // after the weather splice, so a puddle's 0.03 is widened too
    if (uMaterial.z > 0.5) rough = sqrt(min(rough * rough + aaKernel, 1.0));
    cxCoat = 0.0; cxCoatRough = 0.0;
    CxSheenOf(d.ext1, cxSheen, cxSheenRough);
    vec3 msc = vec3(1.0);
    if (uMaterial.x > 0.5)
    {
        msc = CxMultiScatter(F0, NoV, sqrt(rough));
        cxCoat = clamp(d.ext0.z, 0.0, 1.0);
        if (cxCoat > 0.0)
        {
            float cp = d.ext0.w >= 0.0 ? d.ext0.w : CX_COAT_ROUGH_DEFAULT;
            cxCoatRough = max(cp * cp, CX_ALPHA_MIN);
        }
    }
    // R3: standing water is a clear coat on ANY material, the engine model included -- the one layer the
    // film is, over a damp base it does not rewrite. The thicker layer sets the roughness.
    if (wxFilm > 0.0)
    {
        if (wxFilm >= cxCoat) cxCoatRough = wxFilmRough;
        cxCoat = max(cxCoat, wxFilm);
    }
    // ...and the coat's lobe takes the same specular-AA kernel the base does (Kaplanyan & Hoffman 2016).
    // A film's alpha sits near the CX_ALPHA_MIN floor -- far narrower than a pixel of plaza -- and a lobe
    // that narrow on a normal field with any variance across the pixel does not draw a highlight, it draws
    // a thin bright curve that crawls as the camera moves.
    if (cxCoatRough > 0.0 && uMaterial.z > 0.5) cxCoatRough = sqrt(min(cxCoatRough * cxCoatRough + aaKernel, 1.0));
    // what the coat reflects never reaches the layer under it
    float coatT = CxCoatT(cxCoat, NoV);
    cxF0 = F0; cxSpec = spec;
    cxSpecScale = CxSpecScaleOf(uMaterial.x > 0.5 ? F0 : specColor, NoV, rough, cxCoat);
    // the diffuse share: the engine's 1 - fEnvironmentSpecularity (inc_lighting.shd:549, :603-609), the
    // specularity raised toward 1 by the view Fresnel and pulled back by sqrt(roughness); a physical metal has
    // none at all. The max() keeps a dielectric at 0.96 and still lets the wet splice's specularity take its share.
    float kd = physical ? (1.0 - metal) * (1.0 - max(spec * (1.0 - metal), CX_DIELECTRIC_F0))
                        : 1.0 - mix(mix(spec, 1.0, CxFresnel(NoV) * cxFresnel), spec, sqrt(rough));

    vec3 sun = uAreaDiffuse * (sunNdl * sunVis);
    // #143's wrap is a DIFFUSE fix -- it stands in for light bleeding across a low-poly terminator,
    // which is a subsurface/interreflection effect. A specular highlight has no such excuse: fed the
    // wrapped dot, a glossy face angled away from the sun picks up a lobe it cannot geometrically
    // see. The lobe takes the raw dot, the way the ambient hemisphere already does.
    vec3 sunSpec = uAreaDiffuse * (sunRaw * sunVis);
    vec3 sunSss = uAreaDiffuse * (sunVis * max(dot(N, uAreaDir) + uSss.x, 0.0) * uSss.y);
    vec3 pointSpec;
    // #308 follow-up: 1 on a creature fragment (skinmesh, or the dangly marker in feat.w) -- the
    // local receiver's carrier-band gate, so a creature takes no dyn-pool self-shadow up close.
    float onMover = (CxSkinned(d.feat) || d.feat.w < 0.0) ? 1.0 : 0.0;
    // #674: the baked term. A surface whose bake landed reads its static light from the pages --
    // irradiance baked against the VERTEX normal, rebalanced here for the shading normal (Unity's
    // directional decode) -- and the loop skips every static light for it; a hot light's mover shadow
    // comes back out through lmSub. One specular lobe along the dominant direction with the
    // irradiance as its radiance stands in for the per-light lobes the loop no longer runs.
    vec3 lmIrr, lmDir, lmSub;
    CxChart chart = uChart[vDraw];
    bool baked = CxLightmap(chart.rec, vChart, lmIrr, lmDir);
    // CX-51: the mirrored draw of the planar reflection skips the loop, its cube shadows and the re-binning they need:
    // the lightmap still brings every static light, and a reflection is the last place a torch's own lobe shows
    vec3 point = vec3(0.0);
    pointSpec = vec3(0.0); lmSub = vec3(0.0);
#ifdef CX_DEFERRED
    // CX-188 (ADR 0029): the walk is COMPILED OUT here, not branched over -- its registers are the whole
    // reason the twin exists. `point` and `pointSpec` stay zero and every term downstream keeps the value
    // that gives; what the walk would have added, the deferred compute pass adds back through the transfer
    // weights this stage writes into the G-buffer (cx_ownlit_tail).
    float gLmLum = 0.0;         // luminance of the UNDIMMED baked term: the lmSub.x denominator
    vec3 gLmSpec = vec3(0.0);   // the baked term's own lobe, i.e. the whole of pointSpec in the twin
#else
    // CX-188: a fragment lying on a surface the deferred walk already lit (a floor decal) takes that walk's answer
    bool fromRec = false;
    float owe = 0.0;
    if (uLightRecOn != 0 && uCapture == 0)
    {
        fromRec = CxLightRecRead(texelFetch(uLightRec, ivec2(gl_FragCoord.xy), 0), vDepth, baked, point, pointSpec, owe);
        if (fromRec) lmSub = vec3(0.0, owe, 0.0);
        else { point = vec3(0.0); pointSpec = vec3(0.0); }
    }
    // 52 = where a forward fragment's light came from: green the deferred record (darker by the movers' debt it
    // carries), yellow baked and walked, red unbaked and walked
    vec3 cxLightFrom = fromRec ? vec3(0.0, 1.0 - 0.8 * owe, 0.0) : baked ? vec3(1.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    if (!fromRec && uCapture != 2)
        point = CxClusteredLightLm(vWorld, N, V, vDepth, onMover, rough, spec, baked ? 1.0 : 0.0, CxLmTexelWorld(chart.rec),
                                   baked ? CxLightmapMover(chart.rec, vChart) : vec4(0.0), chart.pick, pointSpec, lmSub);
#ifdef CX_ABLATE
    // /alloy bench's blended ladder: the stopped walk's result kept alive at a strength the target rounds away
    point *= CX_ABL_EPS; pointSpec *= CX_ABL_EPS;
#endif
#endif
    vec3 lm = vec3(0.0);
    if (baked)
    {
        lm = CxLightmapLit(lmIrr, lmDir, N, normalize(vNrm));
#ifdef CX_DEFERRED
        // full strength: the hot-light debt is the compute pass's to take, through lmShare. The denominator
        // is the luminance of the term BEFORE uDayDim -- which is the value the forward line below reads.
        gLmLum = dot(lm, vec3(0.2126, 0.7152, 0.0722));
        lm *= uDayDim;
#else
        lm *= (1.0 - min(lmSub.x / max(dot(lm, vec3(0.2126, 0.7152, 0.0722)), 1e-5) + lmSub.y, 1.0)) * uDayDim;
#endif
        point += lm;
        // a glossy receiver took each static light's own lobe in the loop instead, cross-faded (light.hidden)
        // ...and a film's coat took each one's own image in CxFilmStatics below, so the pages' lobe leaves it out
        float lmLobe = CxSpecLobe(N, V, lmDir, rough, spec);
        vec3 lmSpec = lm * (1.0 - CxGlossW(rough)) * (wxFilm > 0.0 && cxCoat > 0.0 && uCapture != 2
                      ? CxLoopSpecBase(lmLobe, N, V, lmDir) : CxLoopSpec(lmLobe, N, V, lmDir));
        pointSpec += lmSpec;
#ifdef CX_DEFERRED
        gLmSpec = lmSpec;
#endif
    }
    // 43 = the lightmap term alone: the baked light on a baked surface, MAGENTA on an unbaked one.
    // 44 = the pages' texel grid over baked surfaces (chart seams and density), magenta unbaked.
    if (uViz == 51) { frag = vec4(cxHotViz.x * 0.25, cxHotViz.y * 0.25, 0.0, 1.0); return; }
    vec3 cxLm = baked ? lm : vec3(1.0, 0.0, 1.0);
    if (uViz == 43 && uOit == 0) { frag = vec4(cxLm, 1.0); return; }   // a blended part shows it through the tail
    if (uViz == 44)
    {
        vec4 rec = chart.rec;
        vec2 t = floor(rec.yz + vChart * rec.w);
        frag = vec4(baked ? vec3(mod(t.x + t.y, 2.0) < 1.0 ? 0.3 : 0.7) : vec3(1.0, 0.0, 1.0), 1.0);
        return;
    }
    // The engine's split: every light accumulates its own lobe, then ONE view-side normalisation
    // (fSpecularBaseModifier) and the material's specular colour scale the sum
    // (inc_lighting.shd:545 and :653). fSpecularity is already inside the lobe via the Fresnel term.
    // ADR 0021 gives occlusion to the ambient term alone, which is right and is invisible wherever
    // the area ambient is dark -- every interior, where the light is lamps and the corners are
    // exactly what wants occluding. /ao direct is the dial back toward vanilla's post-multiply:
    // 0 is ADR 0021 untouched, 1 occludes the direct terms outright. The probe term below picks it
    // up through `diffuse` at no extra cost.
    float dirOcc = mix(1.0, occl, uAoDirect);
    vec3 specLight = (sunSpec * CxLoopSpec(CxSpecLobe(N, V, uAreaDir, rough, spec), N, V, uAreaDir) + pointSpec)
                     * dirOcc;
    // R4: the film's own highlights from the baked statics the loop skipped -- a walk of two to four
    // lights on the coat's lobe, capped at one cube tap. The base stayed matte, so it never joined the
    // mirror path that walks the whole static list.
    // CX-188: compiled out of the twin with the walk, for the same reason -- the compute pass adds the film's
    // own highlights back through specT, which is the same weight pointSpec takes (both ride dirOcc).
#ifndef CX_DEFERRED
    if (wxFilm > 0.0 && baked && cxCoat > 0.0 && uCapture != 2)
        specLight += CxFilmStatics(vWorld, N, V, vDepth, cxCoat, cxCoatRough, onMover) * dirOcc;
#endif
    vec3 diffuse = (sun + sunSss + point) * dirOcc;
    // the 0.15 ambient floor and the sun-facing wrap are a content lighting fork's term, NOT stock: stock's
    // ambient is flat, AmbientLight *= materialFrontAmbient (inc_lighting.shd:512). The height map's
    // occlusion scales the AMBIENT, which is the term it is actually occluding
    // ADR 0026 (light.ambientdir): the probe gives that ambient a direction -- sky above, ground below --
    // at the authored level; the stock wrap's 0.15 stays the floor. Never during the capture itself.
    float ambDir = mix(0.15, 1.0, sunRaw);
    vec3 ambShape = vec3(ambDir);
    if (uMaterial.y > 0.0 && uCapture != 1 && uProbeMips > 0.0)
        ambShape = mix(ambShape, max(CxAmbientShape(N), vec3(0.15)), uMaterial.y);
    // The GEOMETRIC normal, not the shading one: a normal map must not move where the sky lookup lands.
    vec3 ambient = uAreaAmbient * matAmbient * ambShape * occl * CxSkyView(vWorld, normalize(vNrm));
    // 34 = the ONLY term occlusion can move inside `total` (the (1-spec) share of the ambient).
    //      Black at both /ao on 0 and /ao on 1 means AO has nothing to scale here, however bright
    //      viz 8 reads -- which no other view in the ladder can say.
    if (uViz == 34) { frag = vec4(ambient * (1.0 - spec), 1.0); return; }
    // ...and the clear coat reflects on its own lobe, on every material that declares one
#ifdef CX_DEFERRED
    vec3 gCoatProbe = vec3(0.0);   // d(specLight)/d(diffuse) through the coat's probe term, for Wd
#endif
    if (cxCoat > 0.0 && uCapture != 1 && uProbeMips > 0.0)
    {
        vec2 cab = CxEnvBrdf(NoV, sqrt(cxCoatRough));
        vec3 cenv = CxProbeFetch(uProbe, vWorld, reflect(-V, N), sqrt(cxCoatRough) * (uProbeMips - 1.0));
        // CX-188 split this out of the += so the twin can carry the factor as a transfer weight; it is the
        // same product, parenthesised
        vec3 cw = cenv * (cxCoat * (CX_DIELECTRIC_F0 * cab.x + cab.y)
                  * CxSpecOcclusion(NoV, occl, cxCoatRough)) * uProbeGain;
        specLight += (ambient + diffuse) * cw;
#ifdef CX_DEFERRED
        gCoatProbe = cw;
#endif
    }
